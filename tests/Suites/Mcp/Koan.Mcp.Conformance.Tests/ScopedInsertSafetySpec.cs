using System.Security.Claims;
using Koan.Core.Capabilities;
using Koan.Data.Abstractions.Filtering;
using Koan.Data.Abstractions.Pipeline;
using Koan.Data.Core.Model;
using Koan.Data.Core;
using Koan.Data.Core.Pipeline;
using Koan.Mcp.TestKit;
using Koan.Web.Authorization;
using Koan.Web.Controllers;
using Koan.Web.Endpoints;
using Koan.Web.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json.Linq;

namespace Koan.Mcp.Conformance.Tests;

public sealed class ScopedInsertSafetySpec : IClassFixture<ScopedInsertFixture>
{
    private readonly ScopedInsertFixture _fixture;
    public ScopedInsertSafetySpec(ScopedInsertFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Hidden_insert_collision_has_no_committed_delta_or_private_prior()
    {
        var original = await new ScopedInsertNote { OwnerId = "alice", Text = "Private original" }.Save();
        _fixture.Completed = 0;
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "bob")], "test"));
        var tool = _fixture.ResolveToolName("scoped-insert-note", EntityEndpointOperationKind.Upsert);
        var request = new JObject
        {
            ["model"] = new JObject { ["id"] = original.Id, ["text"] = "Caller replacement" },
            ["dry_run"] = true
        };
        var preview = await _fixture.CallToolAsAsync(tool, request, principal);
        preview.ToString().Should().NotContain("Private original").And.NotContain("alice");
        preview["meta"]?["diagnostics"]?["dryRun"]?.Value<bool>().Should().BeTrue();
        _fixture.Completed.Should().Be(0);
        request.Remove("dry_run");
        var rejected = await _fixture.CallToolAsAsync(tool, request, principal);
        McpHarnessFixtureBase.IsShortCircuited(rejected).Should().BeTrue();
        rejected["meta"]?["diagnostics"]?["delta"].Should().BeNull();
        rejected.ToString().Should().NotContain("Private original").And.NotContain("alice");
        _fixture.Completed.Should().Be(0);
        _fixture.FilterEnabled = false;
        try
        {
            var persisted = await ScopedInsertNote.Get(original.Id);
            persisted!.Text.Should().Be("Private original");
            persisted.OwnerId.Should().Be("alice");
        }
        finally { _fixture.FilterEnabled = true; }
    }
}

public sealed class ScopedInsertFixture : McpHarnessFixtureBase
{
    public bool FilterEnabled { get; set; } = true;
    public int Completed { get; set; }
    protected override void ConfigureKoan() =>
        ScopedInsertNote.Lifecycle.AfterUpsert(_ => Completed++);
    protected override void ConfigureServices(IServiceCollection services)
    {
        services.AddKoanControllersFrom<ScopedInsertNotesController>();
        services.AddSingleton<IReadFilterContributor>(new Visibility(this));
    }
    private sealed class Visibility(ScopedInsertFixture fixture) : IReadFilterContributor
    {
        public Capability? RequiredCapability => null;
        public Filter? ReadFilter(Type entityType) => entityType == typeof(ScopedInsertNote) && fixture.FilterEnabled
            ? Filter.Eq(nameof(ScopedInsertNote.OwnerId), "bob") : null;
    }
}

[McpEntity(Name = "scoped-insert-note", Exposure = McpExposureMode.Full)]
public sealed class ScopedInsertNote : Entity<ScopedInsertNote>
{
    public string OwnerId { get; set; } = "";
    public string Text { get; set; } = "";
}

public sealed class ScopedInsertNoteAccess : EntityAccess<ScopedInsertNote>
{
    public override IAccessFilter<ScopedInsertNote> Constrain(IAccessFilter<ScopedInsertNote> filter, AccessAction action)
        => action == AccessAction.Create
            ? filter.Stamp(note => note.OwnerId, CurrentUserId!)
            : filter.Where(note => note.OwnerId == CurrentUserId).Stamp(note => note.OwnerId, CurrentUserId!);
}

[Route("api/scoped-insert-notes")]
public sealed class ScopedInsertNotesController : EntityController<ScopedInsertNote>;
