using Koan.Core.Context;
using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Capabilities;
using Koan.Data.Core;
using Koan.Data.AI.Telemetry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace Koan.Data.AI.Workers;

/// <summary>
/// Background service that processes async embedding jobs from the queue.
/// Part of ARCH-0070: Attribute-Driven AI Embeddings (Phase 3).
/// </summary>
public class EmbeddingWorker(
    ILogger<EmbeddingWorker> logger,
    IOptions<EmbeddingWorkerOptions> options,
    EmbeddingTelemetry? telemetry,
    KoanContextCarrierRegistry contextCarriers) : BackgroundService
{
    /// <summary>Compatibility constructor for the public 0.17.0 infrastructure shape.</summary>
    [Obsolete("Direct EmbeddingWorker construction is compatibility-only; let AddKoan compose Core context.")]
    public EmbeddingWorker(
        ILogger<EmbeddingWorker> logger,
        IOptions<EmbeddingWorkerOptions> options,
        EmbeddingTelemetry? telemetry = null)
        : this(logger, options, telemetry, new KoanContextCarrierRegistry([]))
    {
    }

    // Rate limiting: track embeddings generated per minute
    private readonly ConcurrentQueue<DateTimeOffset> _recentEmbeddings = new();
    private readonly SemaphoreSlim _rateLimitSemaphore = new(1, 1);
    private readonly string _owner = Guid.CreateVersion7().ToString("N");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("EmbeddingWorker disabled via configuration");
            return;
        }
        if (options.Value.ProcessingLeaseDuration <= TimeSpan.Zero)
            throw new InvalidOperationException("EmbeddingWorker ProcessingLeaseDuration must be positive.");

        logger.LogInformation("EmbeddingWorker started (BatchSize={BatchSize}, RateLimit={RateLimit}/min)",
            options.Value.BatchSize, options.Value.GlobalRateLimitPerMinute);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var processedCount = await ProcessBatch(stoppingToken);

                if (processedCount > 0)
                {
                    // Jobs available - poll frequently
                    await Task.Delay(options.Value.PollInterval, stoppingToken);
                }
                else
                {
                    // No jobs - poll less frequently
                    await Task.Delay(options.Value.IdlePollInterval, stoppingToken);
                }

                // Periodic cleanup of completed jobs
                if (options.Value.AutoCleanupCompleted)
                {
                    await CleanupCompletedJobs(stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "EmbeddingWorker encountered error in main loop");
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
        }

        logger.LogInformation("EmbeddingWorker stopped");
    }

    /// <summary>
    /// Processes a batch of pending jobs across all entity types.
    /// </summary>
    private async Task<int> ProcessBatch(CancellationToken ct)
    {
        var processedCount = 0;

        // Process jobs for each registered entity type
        foreach (var entityType in EmbeddingRegistry.GetRegisteredTypes())
        {
            // Only process entity types with Async=true
            if (!EmbeddingRegistry.AsyncEntityTypes.Contains(entityType))
                continue;

            try
            {
                var count = await ProcessEntityTypeJobs(entityType, ct);
                processedCount += count;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process jobs for entity type {EntityType}", entityType.Name);
            }
        }

        return processedCount;
    }

    /// <summary>
    /// Processes pending jobs for a specific entity type using reflection.
    /// </summary>
    private async Task<int> ProcessEntityTypeJobs(Type entityType, CancellationToken ct)
    {
        // Use reflection to call ProcessJobsAsync<TEntity>
        var method = typeof(EmbeddingWorker)
            .GetMethod(nameof(ProcessJobsAsync), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.MakeGenericMethod(entityType);

        if (method == null)
        {
            logger.LogError("Failed to find ProcessJobsAsync method for {EntityType}", entityType.Name);
            return 0;
        }

        var task = method.Invoke(this, new object[] { ct }) as Task<int>;
        return task != null ? await task : 0;
    }

    /// <summary>
    /// Processes pending jobs for a specific entity type.
    /// </summary>
    private async Task<int> ProcessJobsAsync<TEntity>(CancellationToken ct)
        where TEntity : class, IEntity<string>
    {
        var batchStopwatch = Stopwatch.StartNew();

        // Query pending work plus abandoned claims in durable FIFO order. A legacy Processing row without lease
        // metadata becomes recoverable only after the configured grace window, so a mixed-version deployment does
        // not immediately steal work from an older worker.
        var now = DateTimeOffset.UtcNow;
        var legacyCutoff = now - options.Value.ProcessingLeaseDuration;
        var candidates = (await EmbedJob<TEntity>.Query(
            j => j.Status == EmbedJobStatus.Pending ||
                 (j.Status == EmbedJobStatus.Processing &&
                  ((j.LeaseUntil != null && j.LeaseUntil <= now) ||
                   (j.LeaseUntil == null && j.StartedAt != null && j.StartedAt <= legacyCutoff))),
            ct))
            .OrderBy(j => j.CreatedAt)
            .Take(options.Value.BatchSize)
            .ToList();

        if (candidates.Count == 0)
            return 0;

        logger.LogDebug("Processing {Count} claimable embedding jobs for {EntityType}",
            candidates.Count, typeof(TEntity).Name);

        var processedCount = 0;
        foreach (var candidate in candidates)
        {
            // Do not consume a durable claim while waiting for local rate capacity.
            await WaitForRateLimit(ct);
            var job = await TryClaim<TEntity>(candidate.Id!, ct);
            if (job is null) continue;

            using var execution = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var heartbeat = MaintainLease<TEntity>(job.Id!, execution);
            try
            {
                await ProcessJobAsync(job, execution.Token);
                if (await CompleteClaim(job, execution.Token))
                {
                    processedCount++;
                    logger.LogDebug("Completed embedding job {JobId} for entity {EntityId}",
                        job.Id, job.EntityId);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning(
                    "Embedding job {JobId} stopped because its processing lease could not be maintained",
                    job.Id);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to process embedding job {JobId}: {Error}",
                    job.Id, ex.Message);

                await HandleJobFailureAsync(job, ex.Message, execution.Token);
            }
            finally
            {
                execution.Cancel();
                try { await heartbeat; }
                catch (OperationCanceledException) { }
            }
        }

        batchStopwatch.Stop();

        // Record batch processing metrics
        telemetry?.RecordBatchProcessing(
            entityType: typeof(TEntity).Name,
            batchSize: processedCount,
            durationSeconds: batchStopwatch.Elapsed.TotalSeconds);

        // Record queue processing metrics
        telemetry?.RecordQueueProcessing(
            count: processedCount,
            success: true,
            entityType: typeof(TEntity).Name);

        // Update queue state (get latest counts for telemetry)
        var allPending = await EmbedJob<TEntity>.Query(j => j.Status == EmbedJobStatus.Pending, ct);
        var allFailed = await EmbedJob<TEntity>.Query(j => j.Status == EmbedJobStatus.FailedPermanent, ct);
        var oldestPending = allPending.OrderBy(j => j.CreatedAt).FirstOrDefault();
        var oldestAge = oldestPending != null ? (DateTimeOffset.UtcNow - oldestPending.CreatedAt).TotalSeconds : 0.0;

        telemetry?.UpdateQueueState(
            pending: allPending.Count(),
            failed: allFailed.Count(),
            oldestAgeSeconds: oldestAge);

        return processedCount;
    }

    private async Task<EmbedJob<TEntity>?> TryClaim<TEntity>(string jobId, CancellationToken ct)
        where TEntity : class, IEntity<string>
    {
        var job = await EmbedJob<TEntity>.Get(jobId, ct);
        var now = DateTimeOffset.UtcNow;
        if (job is null || !IsClaimable(job, now)) return null;

        var priorStatus = job.Status;
        var priorOwner = job.Owner;
        var priorLeaseUntil = job.LeaseUntil;
        var priorStartedAt = job.StartedAt;
        job.Status = EmbedJobStatus.Processing;
        job.Owner = _owner;
        job.StartedAt = now;
        job.LeaseUntil = now + options.Value.ProcessingLeaseDuration;
        job.CompletedAt = null;

        var cas = ConditionalRepository<TEntity>();
        if (cas is not null)
        {
            var claimed = priorStatus == EmbedJobStatus.Pending
                ? await cas.ConditionalReplaceAsync(job, Koan.Data.Abstractions.Filtering.LinqFilterCompiler.Compile<EmbedJob<TEntity>>(value => value.Status == EmbedJobStatus.Pending), ct)
                : await cas.ConditionalReplaceAsync(
                    job,
                    Koan.Data.Abstractions.Filtering.LinqFilterCompiler.Compile<EmbedJob<TEntity>>(value => value.Status == EmbedJobStatus.Processing &&
                             value.Owner == priorOwner &&
                             value.LeaseUntil == priorLeaseUntil &&
                             value.StartedAt == priorStartedAt),
                    ct);
            return claimed ? job : null;
        }

        // Providers without conditional replacement retain an honest at-least-once claim: write the unique owner,
        // then reconcile before doing external work. A racing writer may cause duplicate work, never silent loss.
        await job.Save(ct);
        var verified = await EmbedJob<TEntity>.Get(job.Id!, ct);
        return verified is { Status: EmbedJobStatus.Processing } && verified.Owner == _owner
            ? verified
            : null;
    }

    private bool IsClaimable<TEntity>(EmbedJob<TEntity> job, DateTimeOffset now)
        where TEntity : class, IEntity<string>
    {
        if (job.Status == EmbedJobStatus.Pending) return true;
        if (job.Status != EmbedJobStatus.Processing) return false;
        if (job.LeaseUntil is { } leaseUntil) return leaseUntil <= now;
        return job.StartedAt is { } startedAt &&
               startedAt <= now - options.Value.ProcessingLeaseDuration;
    }

    private async Task MaintainLease<TEntity>(
        string jobId,
        CancellationTokenSource execution)
        where TEntity : class, IEntity<string>
    {
        var duration = options.Value.ProcessingLeaseDuration;
        var interval = TimeSpan.FromTicks(Math.Max(TimeSpan.FromMilliseconds(100).Ticks, duration.Ticks / 3));
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(execution.Token))
            {
                if (await RenewLease<TEntity>(jobId, execution.Token)) continue;
                logger.LogWarning("Embedding worker lost the processing lease for job {JobId}", jobId);
                execution.Cancel();
                return;
            }
        }
        catch (OperationCanceledException) when (execution.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            logger.LogError(error, "Embedding worker could not renew the processing lease for job {JobId}", jobId);
            execution.Cancel();
        }
    }

    private async Task<bool> RenewLease<TEntity>(string jobId, CancellationToken ct)
        where TEntity : class, IEntity<string>
    {
        var job = await EmbedJob<TEntity>.Get(jobId, ct);
        if (job is not { Status: EmbedJobStatus.Processing } || job.Owner != _owner) return false;
        var leaseUntil = DateTimeOffset.UtcNow + options.Value.ProcessingLeaseDuration;
        job.LeaseUntil = leaseUntil;

        var cas = ConditionalRepository<TEntity>();
        if (cas is not null)
            return await cas.ConditionalReplaceAsync(
                job,
                Koan.Data.Abstractions.Filtering.LinqFilterCompiler.Compile<EmbedJob<TEntity>>(value => value.Status == EmbedJobStatus.Processing && value.Owner == _owner),
                ct);

        await job.Save(ct);
        var verified = await EmbedJob<TEntity>.Get(jobId, ct);
        return verified is { Status: EmbedJobStatus.Processing } &&
               verified.Owner == _owner &&
               verified.LeaseUntil == leaseUntil;
    }

    private async Task<bool> CompleteClaim<TEntity>(EmbedJob<TEntity> job, CancellationToken ct)
        where TEntity : class, IEntity<string>
    {
        job.Status = EmbedJobStatus.Completed;
        job.Error = null;
        job.CompletedAt = DateTimeOffset.UtcNow;
        job.Owner = null;
        job.LeaseUntil = null;
        return await ReplaceOwned(job, ct);
    }

    private async Task<bool> ReplaceOwned<TEntity>(EmbedJob<TEntity> replacement, CancellationToken ct)
        where TEntity : class, IEntity<string>
    {
        var cas = ConditionalRepository<TEntity>();
        if (cas is not null)
            return await cas.ConditionalReplaceAsync(
                replacement,
                Koan.Data.Abstractions.Filtering.LinqFilterCompiler.Compile<EmbedJob<TEntity>>(value => value.Status == EmbedJobStatus.Processing && value.Owner == _owner),
                ct);

        var current = await EmbedJob<TEntity>.Get(replacement.Id!, ct);
        if (current is not { Status: EmbedJobStatus.Processing } || current.Owner != _owner) return false;
        await replacement.Save(ct);
        return true;
    }

    private static IConditionalWriteRepository<EmbedJob<TEntity>, string>? ConditionalRepository<TEntity>()
        where TEntity : class, IEntity<string>
        => Data<EmbedJob<TEntity>, string>.Capabilities.Has(DataCaps.Write.ConditionalReplace)
            ? Data<EmbedJob<TEntity>, string>.As<IConditionalWriteRepository<EmbedJob<TEntity>, string>>()
            : null;

    /// <summary>
    /// Processes a single embedding job.
    /// </summary>
    private async Task ProcessJobAsync<TEntity>(EmbedJob<TEntity> job, CancellationToken ct)
        where TEntity : class, IEntity<string>
    {
        var stopwatch = Stopwatch.StartNew();
        EmbeddingContent? content = null;

        try
        {
            // Restore durable service context (tenant + registered carrier axes) captured at enqueue so this global
            // worker reads/writes the entity, vector, and state in the scope they belong to. Request-only Web filters
            // are intentionally not carried into jobs. An unrestorable carrier throws here and the job is
            // retried/dead-lettered, never silently mis-scoped.
            using var _ambient = contextCarriers.Restore(job.AmbientCarrier, ContextIngressTrust.HostTrusted);

            // Load the entity to get fresh data
            var entity = await Data<TEntity, string>.Get(job.EntityId, ct);
            if (entity == null)
            {
                throw new InvalidOperationException($"Entity {job.EntityId} not found");
            }

            // Verify content signature hasn't changed
            var metadata = EmbeddingMetadata.Resolve<TEntity>();
            content = EmbeddingWriter.Describe(entity, metadata);

            if (content.Value.Signature != job.ContentSignature)
            {
                logger.LogWarning(
                    "Content signature changed for entity {EntityId}; indexing the current Entity state",
                    job.EntityId);

                job.ContentSignature = content.Value.Signature;

                // Record cache invalidation
                telemetry?.RecordCacheInvalidation(typeof(TEntity).Name, "content_changed");
            }

            // Estimate tokens for cost tracking
            var estimatedTokens = EmbeddingMetadata.EstimateTokens(content.Value.Text);

            var write = await EmbeddingWriter.Write(
                entity,
                metadata,
                content.Value,
                ct: ct).ConfigureAwait(false);

            // Estimate cost
            var estimatedCost = EmbeddingCostEstimator.EstimateCost(
                write.Model,
                write.Source?.Split('-').FirstOrDefault(), // Extract provider from source like "openai-prod"
                estimatedTokens);

            stopwatch.Stop();

            // Record telemetry
            telemetry?.RecordEmbeddingGeneration(
                entityType: typeof(TEntity).Name,
                model: write.Model,
                provider: write.Source?.Split('-').FirstOrDefault(),
                source: write.Source,
                latencyMs: write.ProviderLatency.TotalMilliseconds,
                tokens: estimatedTokens,
                estimatedCost: estimatedCost,
                success: true);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            // Record failure telemetry
            var metadata = EmbeddingMetadata.Resolve<TEntity>();
            var estimatedTokens = content is { } prepared
                ? EmbeddingMetadata.EstimateTokens(prepared.Text)
                : 0;

            telemetry?.RecordEmbeddingGeneration(
                entityType: typeof(TEntity).Name,
                model: metadata.Model,
                provider: metadata.Source?.Split('-').FirstOrDefault(),
                source: metadata.Source,
                latencyMs: stopwatch.Elapsed.TotalMilliseconds,
                tokens: estimatedTokens,
                estimatedCost: 0.0,
                success: false,
                errorMessage: ex.Message);

            throw; // Re-throw for caller's error handling
        }
    }

    /// <summary>
    /// Handles job failure with retry logic.
    /// </summary>
    private async Task HandleJobFailureAsync<TEntity>(
        EmbedJob<TEntity> job,
        string error,
        CancellationToken ct)
        where TEntity : class, IEntity<string>
    {
        job.Error = error;
        job.RetryCount++;

        if (job.RetryCount >= options.Value.MaxRetries)
        {
            // Permanently failed
            job.Status = EmbedJobStatus.FailedPermanent;
            job.CompletedAt = DateTimeOffset.UtcNow;

            logger.LogError(
                "Embedding job {JobId} permanently failed after {Retries} retries: {Error}",
                job.Id, job.RetryCount, error);
        }
        else
        {
            // Schedule retry with exponential backoff
            job.Status = EmbedJobStatus.Failed;

            var delay = CalculateRetryDelay(job.RetryCount);
            logger.LogWarning(
                "Embedding job {JobId} failed (retry {Retry}/{MaxRetries}), will retry in {Delay}: {Error}",
                job.Id, job.RetryCount, options.Value.MaxRetries, delay, error);

            // Reset to pending after delay (simplified - in production might use scheduled jobs)
            await Task.Delay(delay, ct);
            job.Status = EmbedJobStatus.Pending;
            job.StartedAt = null;
            job.CompletedAt = null;
        }

        job.Owner = null;
        job.LeaseUntil = null;
        if (!await ReplaceOwned(job, ct))
            logger.LogWarning("Embedding failure outcome for job {JobId} was not written because its lease was lost", job.Id);
    }

    /// <summary>
    /// Calculates retry delay with exponential backoff.
    /// </summary>
    private TimeSpan CalculateRetryDelay(int retryCount)
    {
        var delay = options.Value.InitialRetryDelay.TotalSeconds *
                    Math.Pow(options.Value.RetryBackoffMultiplier, retryCount - 1);

        var clampedDelay = Math.Min(delay, options.Value.MaxRetryDelay.TotalSeconds);
        return TimeSpan.FromSeconds(clampedDelay);
    }

    /// <summary>
    /// Waits if rate limit is exceeded.
    /// </summary>
    private async Task WaitForRateLimit(CancellationToken ct)
    {
        if (options.Value.GlobalRateLimitPerMinute <= 0)
            return; // Rate limiting disabled

        await _rateLimitSemaphore.WaitAsync(ct);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var oneMinuteAgo = now.AddMinutes(-1);

            // Remove embeddings older than 1 minute
            while (_recentEmbeddings.TryPeek(out var timestamp) && timestamp < oneMinuteAgo)
            {
                _recentEmbeddings.TryDequeue(out _);
            }

            // Check if we're at the rate limit
            while (_recentEmbeddings.Count >= options.Value.GlobalRateLimitPerMinute)
            {
                logger.LogDebug("Rate limit reached, waiting...");
                await Task.Delay(TimeSpan.FromSeconds(1), ct);

                // Clean up old timestamps
                while (_recentEmbeddings.TryPeek(out var timestamp) && timestamp < now.AddMinutes(-1))
                {
                    _recentEmbeddings.TryDequeue(out _);
                }

                now = DateTimeOffset.UtcNow;
            }

            // Record this embedding
            _recentEmbeddings.Enqueue(now);
        }
        finally
        {
            _rateLimitSemaphore.Release();
        }
    }

    /// <summary>
    /// Cleans up old completed jobs.
    /// </summary>
    private async Task CleanupCompletedJobs(CancellationToken ct)
    {
        try
        {
            var cutoff = DateTimeOffset.UtcNow - options.Value.CompletedJobRetention;

            foreach (var entityType in EmbeddingRegistry.GetRegisteredTypes())
            {
                if (!EmbeddingRegistry.AsyncEntityTypes.Contains(entityType))
                    continue;

                await CleanupEntityTypeJobs(entityType, cutoff, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to cleanup completed jobs");
        }
    }

    /// <summary>
    /// Cleans up completed jobs for a specific entity type.
    /// </summary>
    private async Task CleanupEntityTypeJobs(Type entityType, DateTimeOffset cutoff, CancellationToken ct)
    {
        var method = typeof(EmbeddingWorker)
            .GetMethod(nameof(CleanupJobsAsync), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.MakeGenericMethod(entityType);

        if (method == null)
            return;

        var task = method.Invoke(this, new object[] { cutoff, ct }) as Task;
        if (task != null)
            await task;
    }

    /// <summary>
    /// Cleans up completed jobs for a specific entity type.
    /// </summary>
    private async Task CleanupJobsAsync<TEntity>(DateTimeOffset cutoff, CancellationToken ct)
        where TEntity : class, IEntity<string>
    {
        var oldJobs = (await EmbedJob<TEntity>.Query(
            j => j.Status == EmbedJobStatus.Completed && j.CompletedAt < cutoff,
            ct)).ToList();

        if (oldJobs.Any())
        {
            foreach (var job in oldJobs)
            {
                await EmbedJob<TEntity>.Remove(job.Id!, ct);
            }

            logger.LogInformation(
                "Cleaned up {Count} completed jobs for {EntityType}",
                oldJobs.Count, typeof(TEntity).Name);
        }
    }
}
