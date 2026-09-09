using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading.Tasks;
using AwesomeAssertions;
using Koan.Core.Capabilities;
using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Capabilities;
using Koan.Data.Conformance;
using Koan.Data.Core;
using Koan.Data.Core.Model;
using Koan.Data.Core.Querying;
using Koan.Testing.Containers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Koan.Data.AdapterSurface.TestKit;

/// <summary>
/// The reusable AODB conformance suite (ARCH-0103 §6 / P5). Each adapter subclasses this with the adapter's
/// container fixture and the per-source placement settings; the base proves, through a real <c>AddKoan()</c> boot, that
/// the adapter realizes ALL THREE AODB isolation modes — and that it <b>declares</b> the matching capability tokens.
/// <para>
/// This binds each token to its proof (ARCH-0094, co-defined): an adapter that declares
/// <see cref="DataCaps.Isolation.ContainerScoped"/>/<see cref="DataCaps.Isolation.DatabaseScoped"/> but does not
/// realize the mode fails the matching behavioral cell; one that realizes it but does not declare fails
/// <see cref="Declares_all_three_isolation_modes"/>. Over-claim and under-claim both go red.
/// </para>
/// <para>
/// The Shared cell delegates to the shared <see cref="ManagedFieldNoLeak"/> oracle; Container proves a distinct
/// physical container per ambient partition (isolation + concurrent no-leak); Database proves per-source physical
/// routing via the shared <see cref="ConformanceShardAxis"/> (and fail-closed on an unconfigured source). Subclasses
/// supply only <see cref="RoutedSourceSettings"/> — the adapter-specific config that places the two conformance
/// sources on distinct physical stores of the same backend.
/// </para>
/// </summary>
public abstract class AodbConformanceSpecsBase<TFixture> : KoanDataSpec<TFixture>
    where TFixture : KoanContainerFixture
{
    protected AodbConformanceSpecsBase(TFixture fixture, ITestOutputHelper output) : base(fixture, output) { }

    /// <summary>The two routed sources the Database cell drives — must match <see cref="RoutedSourceSettings"/>.</summary>
    protected const string SourceA = "conformance_a";
    protected const string SourceB = "conformance_b";

    /// <summary>A source the fixture never configures — the Database fail-closed proof routes to it.</summary>
    protected const string SourceUnconfigured = "conformance_unconfigured";

    /// <summary>
    /// Adapter-specific configuration mapping <see cref="SourceA"/> and <see cref="SourceB"/> to DISTINCT physical
    /// stores on the fixture's backend (same server/engine, different database/file/keyspace/index). Keys are under
    /// <c>Koan:Data:Sources:{conformance_a|conformance_b}:*</c>. E.g. Mongo: per-source <c>Database</c> names;
    /// SQLite: per-source files; Redis: per-source logical-database index. Invoked ONLY by the Database cell, so an
    /// adapter that must provision the routed stores (e.g. relational CREATE DATABASE) does so once, not per fact.
    /// </summary>
    protected abstract IEnumerable<KeyValuePair<string, string?>> RoutedSourceSettings();

    // ==================== The capability-driven gate (ARCH-0094 Phase 1) ====================

    /// <summary>The AODB isolation modules this ledger gates, each with its under-claim disposition. Record adapters
    /// realize all three modes (the ARCH-0103 fleet mandate), so all three are <see cref="UnclaimedDisposition.Required"/>:
    /// under-claim fails <see cref="Declares_all_three_isolation_modes"/>, over-claim fails the matching realization cell.</summary>
    private static readonly (Capability Token, UnclaimedDisposition Disposition)[] Modules =
    {
        (DataCaps.Isolation.RowScoped, UnclaimedDisposition.Required),
        (DataCaps.Isolation.ContainerScoped, UnclaimedDisposition.Required),
        (DataCaps.Isolation.DatabaseScoped, UnclaimedDisposition.Required),
    };

    /// <summary>Streaming is an earned adapter capability, not a fleet mandate: announcing adapters must realize
    /// provider-bounded paging, while unannounced adapters must reject before yielding instead of materializing.</summary>
    private static readonly (Capability Token, UnclaimedDisposition Disposition)[] StreamModules =
    {
        (DataCaps.Query.ProviderBoundedPaging, UnclaimedDisposition.FailClosed),
    };

    /// <summary>The adapter's announced data capabilities, read off a resolved repository (the repository facade
    /// forwards the inner adapter's set; capabilities are adapter-level, so any conformance entity's repository
    /// reports the same tokens).</summary>
    private static CapabilitySet ResolveCaps(IServiceProvider sp)
    {
        var repo = sp.GetRequiredService<IDataService>().GetRepository<ConformancePartitionDoc, string>();
        return DataCaps.Describe(repo, repo.GetType().Name);
    }

    // ==================== The co-definition: declare the required tokens ====================

    [Fact(DisplayName = "G-09/declarations/Adapter: announced isolation modes are mandatory and executable")]
    public async Task Declares_all_three_isolation_modes()
    {
        RequireBackingStore();
        await using var host = await BootAsync();
        // All three modes are Required (the fleet mandate) — the gate fails any under-claim.
        CapabilityConformanceGate.AssertRequiredDeclared(ResolveCaps(host.Services), Modules);
    }

    // ==================== Provider-bounded Entity streams ====================

    [Fact(DisplayName = "B-09/provider-bounded-page/Adapter: bounded streaming realizes or fails closed")]
    public async Task Provider_bounded_streaming_realizes_or_fails_closed()
    {
        RequireBackingStore();
        await using var host = await BootAsync();
        using var lease = Lease(NewPartition("provider-bounded-stream"));

        const int batchSize = 2;
        const string readyTitle = "stream-ready";
        const string heldTitle = "stream-held";

        await CapabilityConformanceGate.RunCell(
            ResolveCaps(host.Services),
            StreamModules,
            DataCaps.Query.ProviderBoundedPaging,
            realize: async () =>
            {
                var idPrefix = Guid.NewGuid().ToString("N");
                var corpus = new[]
                {
                    (Title: readyTitle, Flag: false, Byte: (byte)2, SignedByte: (sbyte)2, Short: (short)2, UnsignedShort: (ushort)2, Sequence: 2, Key: "05"),
                    (Title: heldTitle, Flag: true, Byte: (byte)10, SignedByte: (sbyte)10, Short: (short)10, UnsignedShort: (ushort)10, Sequence: 10, Key: "04"),
                    (Title: readyTitle, Flag: false, Byte: (byte)1, SignedByte: (sbyte)1, Short: (short)1, UnsignedShort: (ushort)1, Sequence: 1, Key: "03"),
                    (Title: heldTitle, Flag: true, Byte: byte.MinValue, SignedByte: sbyte.MinValue, Short: short.MinValue, UnsignedShort: ushort.MinValue, Sequence: -3, Key: "02"),
                    (Title: readyTitle, Flag: false, Byte: byte.MaxValue, SignedByte: sbyte.MaxValue, Short: short.MaxValue, UnsignedShort: ushort.MaxValue, Sequence: 3, Key: "01")
                };
                var saved = new List<ConformancePartitionDoc>();
                foreach (var item in corpus)
                {
                    saved.Add(await new ConformancePartitionDoc
                    {
                        Id = $"{idPrefix}-{item.Key}",
                        Title = item.Title,
                        Flag = item.Flag,
                        ByteValue = item.Byte,
                        SignedByteValue = item.SignedByte,
                        ShortValue = item.Short,
                        UnsignedShortValue = item.UnsignedShort,
                        Sequence = item.Sequence
                    }.Save());
                }

                var expected = saved.OrderBy(static doc => doc.Id, StringComparer.Ordinal).ToList();
                var streamed = await CollectAsync(ConformancePartitionDoc.AllStream(batchSize: batchSize));

                streamed.Should().HaveCount(expected.Count);
                streamed.Select(static doc => doc.Id).Should().Equal(expected.Select(static doc => doc.Id));
                streamed.Select(static doc => doc.Id).Should().OnlyHaveUniqueItems();

                await AssertPortableStreamOrder(saved, static doc => doc.Flag, static doc => doc.Flag, batchSize);
                await AssertPortableStreamOrder(saved, static doc => doc.ByteValue, static doc => doc.ByteValue, batchSize);
                await AssertPortableStreamOrder(saved, static doc => doc.SignedByteValue, static doc => doc.SignedByteValue, batchSize);
                await AssertPortableStreamOrder(saved, static doc => doc.ShortValue, static doc => doc.ShortValue, batchSize);
                await AssertPortableStreamOrder(saved, static doc => doc.UnsignedShortValue, static doc => doc.UnsignedShortValue, batchSize);
                await AssertPortableStreamOrder(saved, static doc => doc.Sequence, static doc => doc.Sequence, batchSize);

                var expectedReady = expected.Where(static doc => doc.Title == readyTitle).ToList();
                var filtered = await CollectAsync(ConformancePartitionDoc.QueryStream(
                    static doc => doc.Title == readyTitle,
                    batchSize: batchSize,
                    ct: TestContext.Current.CancellationToken));
                filtered.Select(static doc => doc.Id).Should().Equal(expectedReady.Select(static doc => doc.Id));

                // Compile and execute the natural (predicate, CancellationToken) overload independently of the
                // explicit batch-size overload above; both must preserve the same provider-handled order and filter.
                var naturalCancellation = await CollectAsync(ConformancePartitionDoc.QueryStream(
                    static doc => doc.Title == readyTitle,
                    TestContext.Current.CancellationToken));
                naturalCancellation.Select(static doc => doc.Id).Should().Equal(expectedReady.Select(static doc => doc.Id));
            },
            failClosed: async () =>
            {
                var returnedItem = false;
                Func<Task> enumerate = async () =>
                {
                    await foreach (var _ in ConformancePartitionDoc.AllStream(batchSize: batchSize))
                    {
                        returnedItem = true;
                        break;
                    }
                };

                var rejection = (await enumerate.Should().ThrowAsync<QueryStreamRejectedException>()).Which;
                returnedItem.Should().BeFalse("an unannounced adapter must reject before returning its first item");
                rejection.EntityType.Should().Contain(nameof(ConformancePartitionDoc));
                rejection.BatchSize.Should().Be(batchSize);
            });
    }

    // ==================== Polymorphic entity roots (DATA-0109) ====================

    [Fact(DisplayName = "B-01/polymorphic-root/Adapter: variants share one root set and preserve leaf payloads")]
    public async Task Polymorphic_variants_share_the_root_set_and_round_trip_leaf_payloads()
    {
        RequireBackingStore();
        var ct = TestContext.Current.CancellationToken;
        var partition = NewPartition("polymorphic-root");
        var familyTag = Guid.NewGuid().ToString("N");
        string animeId;

        await using (var host = await BootAsync())
        using (Lease(partition))
        {
            var root = await new PolyMedia
            {
                Id = $"{familyTag}-01",
                FamilyTag = familyTag,
                Kind = "Media",
                Title = "Root title",
                SortOrder = 1
            }.Save(ct);
            PolyAnime anime = await new PolyAnime
            {
                Id = $"{familyTag}-02",
                FamilyTag = familyTag,
                Kind = "Anime",
                Title = "Anime title",
                SortOrder = 2,
                Episodes = 24
            }.Save(ct);
            PolyManga manga = await new PolyManga
            {
                Id = $"{familyTag}-03",
                FamilyTag = familyTag,
                Kind = "Manga",
                Title = "Manga title",
                SortOrder = 3,
                Volumes = 12,
                Chapters = 108
            }.Save(ct);
            animeId = anime.Id;

            var animeFromRoot = await PolyMedia.Get(anime.Id, ct);
            var hydratedAnime = animeFromRoot.Should().BeOfType<PolyAnime>().Which;
            hydratedAnime.Episodes.Should().Be(24);

            PolyAnime? animeFromLeaf = await PolyAnime.Get(anime.Id, ct);
            animeFromLeaf.Should().NotBeNull();
            animeFromLeaf!.Episodes.Should().Be(24);
            animeFromLeaf.Title.Should().Be("Anime title");

            var family = await PolyMedia.Query(media => media.FamilyTag == familyTag, ct);
            family.Should().HaveCount(3);
        family.Select(static media => media.GetType()).Should().BeEquivalentTo(
            new[] { typeof(PolyMedia), typeof(PolyAnime), typeof(PolyManga) });
            var queriedManga = family.Single(media => media.Id == manga.Id)
                .Should().BeOfType<PolyManga>().Which;
            queriedManga.Volumes.Should().Be(12);
            queriedManga.Chapters.Should().Be(108);
            (await PolyMedia.Count.Where(
                media => media.FamilyTag == familyTag,
                CountStrategy.Exact,
                ct)).Should().Be(3);
            (await PolyMedia.Count.Exact(ct)).Should().Be(
                3,
                "the unique partition must contain only this family before unfiltered paging");

            var firstPage = await PolyMedia.Page(
                1,
                2,
                sort => sort.OrderBy(media => media.SortOrder),
                ct);
            var secondPage = await PolyMedia.Page(
                2,
                2,
                sort => sort.OrderBy(media => media.SortOrder),
                ct);
            firstPage.Select(static media => media.Id).Should().Equal(root.Id, anime.Id);
            secondPage.Select(static media => media.Id).Should().Equal(manga.Id);
            firstPage[1].Should().BeOfType<PolyAnime>();
            secondPage[0].Should().BeOfType<PolyManga>();

            PolyMedia? rootTypedAnime = await PolyMedia.Get(anime.Id, ct);
            rootTypedAnime.Should().NotBeNull();
            rootTypedAnime!.Title = "Anime title after root save";
            PolyMedia resaved = await rootTypedAnime.Save(ct);
            resaved.Should().BeOfType<PolyAnime>();

            PolyAnime? animeAfterRootSave = await PolyAnime.Get(anime.Id, ct);
            animeAfterRootSave.Should().NotBeNull();
            animeAfterRootSave!.Episodes.Should().Be(24);
            animeAfterRootSave.Title.Should().Be("Anime title after root save");
        }

        // InMemory is intentionally host-owned, while JsonFixture intentionally provisions a fresh directory per
        // boot. Shared-store fixtures can additionally prove hydration after DataService and its repository cache die.
        if (Fixture is InMemoryFixture || Fixture is JsonFixture)
        {
            return;
        }

        await using var coldHost = await BootAsync();
        using var coldLease = Lease(partition);
        var coldRootRead = await PolyMedia.Get(animeId, ct);
        var coldAnime = coldRootRead.Should().BeOfType<PolyAnime>().Which;
        coldAnime.Episodes.Should().Be(24);
        coldAnime.Title.Should().Be("Anime title after root save");

        PolyAnime? coldTypedRead = await PolyAnime.Get(animeId, ct);
        coldTypedRead.Should().NotBeNull();
        coldTypedRead!.Episodes.Should().Be(24);
        coldTypedRead.Title.Should().Be("Anime title after root save");
    }

    // ==================== Shared (FieldFilter → managed-record persistence) ====================

    [Fact(DisplayName = "G-09/row/Adapter: the managed discriminator isolates reads writes and deletes")]
    public async Task Shared_isolation_holds()
    {
        RequireBackingStore();
        await using var host = await BootAsync(ManagedFieldNoLeak.Declare);
        await CapabilityConformanceGate.RunCell(ResolveCaps(host.Services), Modules,
            DataCaps.Isolation.RowScoped,
            realize: () => ManagedFieldNoLeak.AssertNoLeakAsync());
    }

    // ==================== Container (Particle → distinct physical container per partition) ====================

    [Fact(DisplayName = "G-09/container/Adapter: each ambient partition resolves to an isolated container")]
    public async Task Container_isolation_holds()
    {
        RequireBackingStore();
        await using var host = await BootAsync();
        await CapabilityConformanceGate.RunCell(ResolveCaps(host.Services), Modules,
            DataCaps.Isolation.ContainerScoped,
            realize: async () =>
        {
            var pA = NewPartition("ca");
            var pB = NewPartition("cb");

            ConformancePartitionDoc a, b;
            using (Lease(pA)) a = await new ConformancePartitionDoc { Title = "a" }.Save();
            using (Lease(pB)) b = await new ConformancePartitionDoc { Title = "b" }.Save();

            // Read isolation across partitions.
            using (Lease(pA))
            {
                (await ConformancePartitionDoc.All()).Select(d => d.Id).Should().Equal(a.Id);
                (await ConformancePartitionDoc.Get(b.Id)).Should().BeNull();   // pB's row is unreachable from pA
            }
            using (Lease(pB))
            {
                (await ConformancePartitionDoc.All()).Select(d => d.Id).Should().Equal(b.Id);
                (await ConformancePartitionDoc.Get(a.Id)).Should().BeNull();
            }

            // Concurrent interleaved writes across two FRESH partitions must not cross-contaminate (no shared mutable
            // per-partition state on the adapter): each partition ends with EXACTLY its own rows.
            const int perPartition = 15;
            var cA = NewPartition("cc");
            var cB = NewPartition("cd");
            await Task.WhenAll(
                Task.Run(async () =>
                {
                    for (var i = 0; i < perPartition; i++)
                        using (Lease(cA)) await new ConformancePartitionDoc { Title = $"a{i}" }.Save();
                }),
                Task.Run(async () =>
                {
                    for (var i = 0; i < perPartition; i++)
                        using (Lease(cB)) await new ConformancePartitionDoc { Title = $"b{i}" }.Save();
                }));

            using (Lease(cA)) (await ConformancePartitionDoc.All()).Should().HaveCount(perPartition);
            using (Lease(cB)) (await ConformancePartitionDoc.All()).Should().HaveCount(perPartition);
        });
    }

    // ==================== Database (Moniker → per-source physical routing) ====================

    [Fact(DisplayName = "G-09/database/Adapter: each ambient shard resolves to an isolated source")]
    public async Task Database_isolation_holds()
    {
        RequireBackingStore();
        await using var host = await BootAsync(RoutedSourceSettings());
        using var partition = Lease(NewPartition("database"));
        await CapabilityConformanceGate.RunCell(ResolveCaps(host.Services), Modules,
            DataCaps.Isolation.DatabaseScoped,
            realize: async () =>
        {
            // No explicit EntityContext.Source — only the ambient shard. The Database-mode axis derives the source.
            ConformanceShardedDoc a, b;
            using (ConformanceShardAmbient.Use(SourceA)) a = await new ConformanceShardedDoc { Title = "from-a" }.Save();
            using (ConformanceShardAmbient.Use(SourceB)) b = await new ConformanceShardedDoc { Title = "from-b" }.Save();

            using (ConformanceShardAmbient.Use(SourceA))
            {
                (await ConformanceShardedDoc.All()).Select(d => d.Title).Should().Equal("from-a");
                (await ConformanceShardedDoc.Get(b.Id)).Should().BeNull();   // b lives in the other physical source
            }
            using (ConformanceShardAmbient.Use(SourceB))
            {
                (await ConformanceShardedDoc.All()).Select(d => d.Title).Should().Equal("from-b");
                (await ConformanceShardedDoc.Get(a.Id)).Should().BeNull();
            }

            // Fail-closed (external-only posture): routing to an unconfigured source throws a self-explaining error.
            Func<Task> act = async () =>
            {
                using (ConformanceShardAmbient.Use(SourceUnconfigured))
                    await new ConformanceShardedDoc { Title = "nope" }.Save();
            };
            (await act.Should().ThrowAsync<InvalidOperationException>())
                .WithMessage($"*{SourceUnconfigured}*not configured*");
        });
    }

    private static async Task<List<TEntity>> CollectAsync<TEntity>(IAsyncEnumerable<TEntity> stream)
    {
        var items = new List<TEntity>();
        await foreach (var item in stream)
            items.Add(item);
        return items;
    }

    private static async Task AssertPortableStreamOrder<TValue>(
        IReadOnlyList<ConformancePartitionDoc> saved,
        Func<ConformancePartitionDoc, TValue> valueSelector,
        Expression<Func<ConformancePartitionDoc, TValue>> sortSelector,
        int batchSize)
    {
        var expected = saved
            .OrderBy(valueSelector, Comparer<TValue>.Default)
            .ThenBy(static doc => doc.Id, StringComparer.Ordinal)
            .Select(static doc => doc.Id)
            .ToList();
        var actual = await CollectAsync(ConformancePartitionDoc.AllStream(
            sort => sort.OrderBy(sortSelector),
            batchSize: batchSize,
            ct: TestContext.Current.CancellationToken));

        actual.Select(static doc => doc.Id).Should().Equal(expected);
    }
}
