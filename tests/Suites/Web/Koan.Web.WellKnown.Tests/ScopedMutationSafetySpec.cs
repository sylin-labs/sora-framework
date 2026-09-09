using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using AwesomeAssertions;
using AwesomeAssertions.Execution;
using Koan.Core;
using Koan.Data.Abstractions;
using Koan.Data.Core;
using Koan.Data.Core.Model;
using Koan.Web.Authorization;
using Koan.Web.Context;
using Koan.Web.Controllers;
using Koan.Web.Extensions;
using Koan.Web.Endpoints;
using Koan.Web.Hooks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Koan.Web.WellKnown.Tests;

public sealed class ScopedMutationSafetySpec
{
    [Fact]
    public async Task Stamp_only_create_policy_cannot_replace_a_read_hidden_identity()
    {
        using var host = await StartHost();
        var original = await new StampOnlyMemo { OwnerId = "alice", Text = "Private original" }.Save();
        var response = await Bob(host).PostAsJsonAsync("/scoped-mutation/stamp-only",
            new { id = original.Id, ownerId = "bob", text = "Replacement" });
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var preserved = await StampOnlyMemo.Get(original.Id);
        preserved!.OwnerId.Should().Be("alice");
        preserved.Text.Should().Be("Private original");
    }

    [Fact]
    public async Task Read_only_realization_preserves_unrestricted_admin_single_and_bulk_writes()
    {
        using var host = await StartHost();
        var original = await new ReadPolicyMemo { OwnerId = "alice", Text = "Before" }.Save();
        var client = Bob(host);
        client.DefaultRequestHeaders.Add("X-Test-Role", "admin");
        var single = await client.PostAsJsonAsync("/scoped-mutation/read-policy",
            new { id = original.Id, ownerId = "alice", text = "Admin edit" });
        single.IsSuccessStatusCode.Should().BeTrue();
        (await ReadPolicyMemo.Get(original.Id))!.Text.Should().Be("Admin edit");
        var newId = Guid.NewGuid().ToString("N");
        var bulk = await client.PostAsJsonAsync("/scoped-mutation/read-policy/bulk", new[]
        {
            new { id = original.Id, ownerId = "alice", text = "Bulk edit" },
            new { id = newId, ownerId = "alice", text = "Bulk create" }
        });
        bulk.IsSuccessStatusCode.Should().BeTrue();
        (await ReadPolicyMemo.Get(original.Id))!.Text.Should().Be("Bulk edit");
        (await ReadPolicyMemo.Get(newId))!.Text.Should().Be("Bulk create");
    }

    [Fact]
    public async Task Server_granted_admin_preserves_read_only_realization_single_and_bulk_writes()
    {
        using var host = await StartHost();
        var original = await new ReadPolicyMemo { OwnerId = "alice", Text = "Before" }.Save();
        await new AgentGrant { Subject = "bob", Capability = "is:admin", Resource = nameof(ReadPolicyMemo) }.Save();
        var client = Bob(host);
        var single = await client.PostAsJsonAsync("/scoped-mutation/read-policy",
            new { id = original.Id, ownerId = "alice", text = "Granted edit" });
        single.IsSuccessStatusCode.Should().BeTrue();
        (await ReadPolicyMemo.Get(original.Id))!.Text.Should().Be("Granted edit");
        var newId = Guid.NewGuid().ToString("N");
        var bulk = await client.PostAsJsonAsync("/scoped-mutation/read-policy/bulk", new[]
        {
            new { id = original.Id, ownerId = "alice", text = "Granted bulk edit" },
            new { id = newId, ownerId = "alice", text = "Granted bulk create" }
        });
        bulk.IsSuccessStatusCode.Should().BeTrue();
        (await ReadPolicyMemo.Get(original.Id))!.Text.Should().Be("Granted bulk edit");
        (await ReadPolicyMemo.Get(newId))!.Text.Should().Be("Granted bulk create");
    }

    [Fact]
    public async Task Default_numeric_identity_is_rejected_before_native_inmemory_insertion()
    {
        using var host = await StartHost();
        var repository = host.Services.GetRequiredService<IDataService>().GetRepository<NumericInsertMemo, long>();
        var insert = (IInsertOnlyRepository<NumericInsertMemo, long>)repository;
        await ((Func<Task>)(() => insert.Insert(new NumericInsertMemo())))
            .Should().ThrowAsync<NotSupportedException>();
        (await NumericInsertMemo.Get(0)).Should().BeNull();
    }

    [Fact]
    public async Task Requested_set_insertion_does_not_touch_the_default_partition()
    {
        using var host = await StartHost();
        var original = await new ScopedMutationMemo { OwnerId = "alice", Text = "Default original" }.Save();
        var partition = "insert-" + Guid.NewGuid().ToString("N");
        var response = await Bob(host).PostAsJsonAsync($"/scoped-mutation?set={partition}",
            new { id = original.Id, text = "Partition record" }, TestContext.Current.CancellationToken);
        response.IsSuccessStatusCode.Should().BeTrue();
        (await ScopedMutationMemo.Get(original.Id))!.Text.Should().Be("Default original");
        using (EntityContext.Partition(partition))
        {
            var inserted = await ScopedMutationMemo.Get(original.Id);
            inserted!.OwnerId.Should().Be("bob");
            inserted.Text.Should().Be("Partition record");
        }
    }

    [Fact]
    public async Task Hidden_collision_dry_run_exposes_only_the_tentative_caller_model()
    {
        using var host = await StartHost();
        var original = await new ScopedMutationMemo { OwnerId = "alice", Text = "Secret original" }.Save();
        var response = await Bob(host).PostAsJsonAsync("/scoped-mutation/rehearse",
            new { id = original.Id, text = "Preview" }, TestContext.Current.CancellationToken);
        response.IsSuccessStatusCode.Should().BeTrue();
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("Secret original").And.NotContain("alice");
        body.Should().Contain("Preview");
        (await ScopedMutationMemo.Get(original.Id))!.Text.Should().Be("Secret original");
    }

    [Fact]
    public async Task Route_pinned_hidden_identity_is_not_replaced()
    {
        using var host = await StartHost();
        var original = await new ScopedMutationMemo { OwnerId = "alice", Text = "Private text" }.Save();
        var client = Bob(host);
        var response = await client.PutAsJsonAsync($"/scoped-mutation/{original.Id}",
            new { ownerId = "bob", text = "Replacement" }, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("Private text");
        (await ScopedMutationMemo.Get(original.Id))!.Text.Should().Be("Private text");
    }

    [Fact]
    public async Task Bulk_rejects_before_writing_an_earlier_authorized_update()
    {
        using var host = await StartHost();
        var own = await new ScopedMutationMemo { OwnerId = "bob", Text = "Own original" }.Save();
        var hidden = await new ScopedMutationMemo { OwnerId = "alice", Text = "Private original" }.Save();
        var client = Bob(host);
        var response = await client.PostAsJsonAsync("/scoped-mutation/bulk", new[]
        {
            new { id = own.Id, ownerId = "bob", text = "Own changed" },
            new { id = hidden.Id, ownerId = "bob", text = "Private changed" }
        }, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
        (await response.Content.ReadAsStringAsync()).Should().Contain("web.mutation.bulkCreateUnsupported");
        (await ScopedMutationMemo.Get(own.Id))!.Text.Should().Be("Own original");
        (await ScopedMutationMemo.Get(hidden.Id))!.Text.Should().Be("Private original");
    }

    [Fact]
    public async Task Row_created_after_visible_null_read_wins_without_replacement()
    {
        using var host = await StartHost(() =>
            ScopedMutationMemo.Lifecycle.BeforeUpsert(async context =>
            {
                if (context.Current.Text == "Contender")
                    await new ScopedMutationMemo
                    {
                        Id = context.Current.Id, OwnerId = "alice", Text = "Concurrent winner"
                    }.Save(context.CancellationToken);
                return context.Proceed();
            }));
        var id = Guid.NewGuid().ToString("N");
        var response = await Bob(host).PostAsJsonAsync("/scoped-mutation",
            new { id, text = "Contender" }, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var winner = await ScopedMutationMemo.Get(id);
        winner!.OwnerId.Should().Be("alice");
        winner.Text.Should().Be("Concurrent winner");
    }

    [Fact]
    public async Task Concurrent_insert_only_calls_have_one_winner_and_save_remains_an_upsert()
    {
        using var host = await StartHost();
        var repository = host.Services.GetRequiredService<IDataService>().GetRepository<ScopedMutationMemo, string>();
        var inserts = (IInsertOnlyRepository<ScopedMutationMemo, string>)repository;
        var id = Guid.NewGuid().ToString("N");
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(index => Task.Run(() =>
            inserts.Insert(new ScopedMutationMemo { Id = id, Text = index.ToString() }))));
        var winner = results.Should().ContainSingle(result => result.Outcome == MutationOutcome.Inserted).Subject;
        results.Count(result => result.Outcome == MutationOutcome.Conflict).Should().Be(11);
        results.Where(result => result.Outcome == MutationOutcome.Conflict).Should().OnlyContain(result => result.Entity == null);
        (await ScopedMutationMemo.Get(id))!.Text.Should().Be(winner.Entity!.Text);
        await new ScopedMutationMemo { Id = id, Text = "Ordinary save" }.Save();
        (await ScopedMutationMemo.Get(id))!.Text.Should().Be("Ordinary save");
    }

    private static HttpClient Bob(IHost host)
    {
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Subject", "bob");
        return client;
    }

    private static async Task<IHost> StartHost(Action? configure = null)
    {
        var host = Host.CreateDefaultBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.UseEnvironment("Test");
            web.ConfigureServices(services =>
            {
                services.AddKoan(configure ?? (() => { }));
                services.AddKoanControllersFrom<ScopedMutationController>();
                services.AddScoped<IWebContextContributor, ScopedMutationContext>();
            });
            web.Configure(_ => { });
        }).Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    [Fact]
    public async Task Request_read_visibility_must_not_turn_an_existing_update_into_a_create()
    {
        using var host = Host.CreateDefaultBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.UseEnvironment("Test");
            web.ConfigureServices(services =>
            {
                services.AddKoan();
                services.AddKoanControllersFrom<ScopedMutationController>();
                services.AddScoped<IWebContextContributor, ScopedMutationContext>();
            });
            web.Configure(_ => { });
        }).Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var original = await new ScopedMutationMemo { OwnerId = "alice", Text = "Original" }.Save();
            var client = host.GetTestClient();
            client.DefaultRequestHeaders.Add("X-Test-Subject", "bob");

            var hidden = await client.GetAsync($"/scoped-mutation/{original.Id}", TestContext.Current.CancellationToken);
            hidden.StatusCode.Should().Be(HttpStatusCode.NotFound);

            // An ordinary new row is still a supported operation under the same read scope.
            var created = await client.PostAsJsonAsync("/scoped-mutation", new { text = "New" }, TestContext.Current.CancellationToken);
            created.IsSuccessStatusCode.Should().BeTrue();
            var own = await created.Content.ReadFromJsonAsync<ScopedMutationMemo>(TestContext.Current.CancellationToken);
            own.Should().NotBeNull();
            own!.OwnerId.Should().Be("bob");
            var updated = await client.PostAsJsonAsync("/scoped-mutation",
                new { id = own.Id, ownerId = "alice", text = "Updated" }, TestContext.Current.CancellationToken);
            updated.IsSuccessStatusCode.Should().BeTrue();
            (await ScopedMutationMemo.Get(own.Id))!.OwnerId.Should().Be("bob");

            var response = await client.PostAsJsonAsync("/scoped-mutation",
                new { id = original.Id, ownerId = "bob", text = "Replacement" }, TestContext.Current.CancellationToken);
            var persisted = await ScopedMutationMemo.Get(original.Id);
            using var assertions = new AssertionScope();
            response.StatusCode.Should().Be(HttpStatusCode.NotFound,
                "read-hidden does not mean absent and cannot bypass the Update constraint");
            persisted.Should().NotBeNull();
            persisted!.OwnerId.Should().Be("alice");
            persisted.Text.Should().Be("Original", "a denied update must preserve the existing row");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }
}

public sealed class ScopedMutationMemo : Entity<ScopedMutationMemo>
{
    public string OwnerId { get; set; } = "";
    public string Text { get; set; } = "";
}

public sealed class ScopedMutationAccess : EntityAccess<ScopedMutationMemo>
{
    public override IAccessFilter<ScopedMutationMemo> Constrain(IAccessFilter<ScopedMutationMemo> filter, AccessAction action)
        => action switch
        {
            AccessAction.Create => filter.Stamp(memo => memo.OwnerId, CurrentUserId!),
            AccessAction.Update => filter.Where(memo => memo.OwnerId == CurrentUserId)
                .Stamp(memo => memo.OwnerId, CurrentUserId!),
            _ => filter.Where(memo => memo.OwnerId == CurrentUserId)
        };
}

public sealed class ScopedMutationContext : IWebContextContributor
{
    public ValueTask ContributeAsync(WebContext context)
    {
        if (!context.HttpContext.Request.Path.StartsWithSegments("/scoped-mutation")) return ValueTask.CompletedTask;
        var subject = context.HttpContext.Request.Headers["X-Test-Subject"].ToString();
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, subject)], "test");
        if (context.HttpContext.Request.Headers["X-Test-Role"] == "admin")
            identity.AddClaim(new Claim(ClaimTypes.Role, "admin"));
        context.UsePrincipal(new ClaimsPrincipal(identity));
        context.Where<ScopedMutationMemo>(memo => memo.OwnerId == subject);
        context.Where<ReadPolicyMemo>(memo => memo.OwnerId == subject);
        context.Where<StampOnlyMemo>(memo => memo.OwnerId == subject);
        return ValueTask.CompletedTask;
    }
}

[Route("scoped-mutation")]
public sealed class ScopedMutationController : EntityController<ScopedMutationMemo>
{
    [HttpPost("rehearse")]
    public async Task<IActionResult> Rehearse([FromBody] ScopedMutationMemo model, CancellationToken ct)
    {
        var context = new EntityRequestContext(HttpContext.RequestServices, new QueryOptions(), ct, HttpContext);
        context.Items[EntityMutationProbe.WantsDeltaKey] = true;
        var endpoint = HttpContext.RequestServices.GetRequiredService<IEntityEndpointService<ScopedMutationMemo, string>>();
        var result = await endpoint.Upsert(new() { Context = context, Model = model, DryRun = true });
        return result.ShortCircuitResult ?? Ok(new
        {
            before = context.Items[EntityMutationProbe.BeforeKey],
            after = result.Model,
            dryRun = context.Items[EntityMutationProbe.DryRunKey]
        });
    }
}

public sealed class NumericInsertMemo : Entity<NumericInsertMemo, long>;

public sealed class ReadPolicyMemo : Entity<ReadPolicyMemo>
{
    public string OwnerId { get; set; } = "";
    public string Text { get; set; } = "";
}

public sealed class ReadPolicyAccess : EntityAccess<ReadPolicyMemo>
{
    protected override System.Linq.Expressions.Expression<Func<ReadPolicyMemo, bool>> Owner => memo => memo.OwnerId == CurrentUserId;
    protected override ActionGate WriteGate => Gate.Is("admin");
    public override IAccessFilter<ReadPolicyMemo> Constrain(IAccessFilter<ReadPolicyMemo> filter, AccessAction action)
        => action == AccessAction.Read ? filter.Where(Owner) : filter;
}

[Route("scoped-mutation/read-policy")]
public sealed class ReadPolicyController : EntityController<ReadPolicyMemo>;

public sealed class StampOnlyMemo : Entity<StampOnlyMemo>
{
    public string OwnerId { get; set; } = "";
    public string Text { get; set; } = "";
}

public sealed class StampOnlyAccess : EntityAccess<StampOnlyMemo>
{
    public override IAccessFilter<StampOnlyMemo> Constrain(IAccessFilter<StampOnlyMemo> filter, AccessAction action)
        => action == AccessAction.Create ? filter.Stamp(memo => memo.OwnerId, CurrentUserId!) : filter;
}

[Route("scoped-mutation/stamp-only")]
public sealed class StampOnlyController : EntityController<StampOnlyMemo>;
