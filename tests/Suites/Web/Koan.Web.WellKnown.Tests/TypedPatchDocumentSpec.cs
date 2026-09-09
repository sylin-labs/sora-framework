using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Threading;
using AwesomeAssertions;
using Koan.Core;
using Koan.Data.Abstractions.Instructions;
using Koan.Data.Core;
using Koan.Data.Core.Model;
using Koan.Data.Core.Patch;
using Koan.Web.Authorization;
using Koan.Web.Controllers;
using Koan.Web.Endpoints;
using Koan.Web.Extensions;
using Koan.Web.Hooks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Koan.Web.WellKnown.Tests;

/// <summary>Typed merge/partial restoration, independent of the provider's own patch instruction path.</summary>
public sealed class TypedPatchDocumentSpec
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Name_only_patch_preserves_additive_collections_and_private_state(bool merge)
    {
        var original = New(withTags: true);
        var target = Apply(original, new JObject { ["name"] = "after" }, merge);
        target.Name.Should().Be("after");
        target.Tags.Should().Equal("public", "private");
        target.Retained.Should().Be("domain-state");
        target.State.Should().Be(PatchDocumentState.Ready);
        original.Name.Should().Be("before");
        ReferenceEquals(original, target).Should().BeFalse();
    }

    [Theory]
    [InlineData(false, "detail")]
    [InlineData(true, "detail")]
    [InlineData(false, "Detail")]
    [InlineData(true, "Detail")]
    public void Nested_alias_patch_preserves_omitted_siblings(bool merge, string detail)
    {
        var target = Apply(New(), new JObject { [detail] = new JObject { ["display_name"] = "after" } }, merge);
        target.Detail!.Name.Should().Be("after");
        target.Detail.Sibling.Should().Be("persisted-sibling");
        target.Detail.Stored.Should().Be("stored-note");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Existing_private_setter_input_is_ignored_without_changing_stored_state(bool merge)
    {
        var target = Apply(New(), new JObject { ["retained"] = "forged" }, merge);
        target.Retained.Should().Be("domain-state");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void New_subtree_admission_blocks_private_state_forgery(bool merge)
    {
        var target = New();
        target.Detail = null;
        var patched = Apply(target, new JObject
        {
            ["detail"] = new JObject { ["display_name"] = "allowed", ["stored"] = "forged" },
        }, merge);
        patched.Detail!.Name.Should().Be("allowed");
        patched.Detail.Stored.Should().Be("stored-note");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void New_dictionary_entry_and_array_elements_are_admitted(bool merge)
    {
        var target = New();
        target.Registry = new Dictionary<string, PatchDocumentBadge>();
        var patched = Apply(target, new JObject
        {
            ["registry"] = new JObject
            {
                ["first"] = new JObject { ["label"] = "L", ["guarded"] = "forged" },
            },
            ["badges"] = new JArray(new JObject { ["label"] = "B", ["guarded"] = "forged" }),
        }, merge);
        patched.Registry!["first"].Label.Should().Be("L");
        patched.Registry["first"].Guarded.Should().Be("stored-guard");
        patched.Badges.Should().ContainSingle().Which.Label.Should().Be("B");
        patched.Badges[0].Guarded.Should().Be("stored-guard");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Stored_family_child_admits_through_its_own_variant_shape(bool merge)
    {
        var target = New();
        target.Node = new PatchFamilyAlpha { Trunk = "trunk", AlphaOnly = "alpha" };
        var patched = Apply(target, new JObject
        {
            ["node"] = new JObject { ["trunk"] = "patched", ["alphaOnly"] = "patched-alpha" },
        }, merge);
        patched.Node.Should().BeOfType<PatchFamilyAlpha>();
        ((PatchFamilyAlpha)patched.Node!).Trunk.Should().Be("patched");
        ((PatchFamilyAlpha)patched.Node).AlphaOnly.Should().Be("patched-alpha");
    }

    [Fact]
    public void Rejected_null_inside_new_subtree_refuses_without_touching_the_original()
    {
        var target = New();
        target.Detail = null;
        var apply = () => new PartialJsonApplicator<PatchDocumentEntity>(
            new JObject { ["detail"] = new JObject { ["display_name"] = null } },
            PartialJsonNullPolicy.Reject).ApplyToCopy(target);
        apply.Should().Throw<InvalidOperationException>();
        target.Detail.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Merge_null_defaults_beat_constructor_seeds(bool merge)
    {
        var target = New();
        var patched = merge
            ? Apply(target, new JObject { ["note"] = null, ["seeded"] = null, ["detail"] = new JObject { ["sibling"] = null } }, true)
            : Apply(target, new JObject { ["note"] = null, ["detail"] = new JObject { ["sibling"] = null } }, false);
        patched.Note.Should().BeNull();
        patched.Detail!.Sibling.Should().BeNull();
        // Merge SetDefault yields the CLR default; partial SetNull on a non-nullable member is a
        // separate conversion refusal, so partial leaves the seed untouched here.
        patched.Seeded.Should().Be(merge ? 0 : 41);
    }

    [Fact]
    public void Merge_null_dictionary_entry_is_removed_not_defaulted()
    {
        var target = New();
        target.Registry = new Dictionary<string, PatchDocumentBadge>
        {
            ["first"] = new() { Label = "L" },
        };
        var patched = Apply(target, new JObject
        {
            ["registry"] = new JObject { ["first"] = null },
        }, true);
        patched.Registry.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Duplicate_cased_patch_members_refuse_ambiguity(bool merge)
    {
        var target = New();
        var apply = () => Apply(target, JObject.Parse("""{ "name": "first", "NAME": "second" }"""), merge);
        apply.Should().Throw<InvalidOperationException>();
        target.Name.Should().Be("before");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Mixed_case_dictionary_keys_survive_repeated_patches(bool merge)
    {
        var target = New();
        target.Registry = new Dictionary<string, PatchDocumentBadge>
        {
            ["first"] = new() { Label = "lower" },
            ["FIRST"] = new() { Label = "upper" },
        };

        var patched = Apply(target, new JObject { ["name"] = "after" }, merge);
        patched.Registry!.Keys.Should().BeEquivalentTo(new[] { "first", "FIRST" }, "name-only patch must not recase stored keys");
        patched.Registry["first"].Label.Should().Be("lower");
        patched.Registry["FIRST"].Label.Should().Be("upper");

        patched = Apply(patched, new JObject
        {
            ["registry"] = new JObject { ["FIRST"] = new JObject { ["label"] = "changed" } },
        }, merge);
        patched.Registry!.Keys.Should().BeEquivalentTo("first", "FIRST");
        patched.Registry["FIRST"].Label.Should().Be("changed", "a targeted key touches only that exact key");
        patched.Registry["first"].Label.Should().Be("lower");

        patched = Apply(patched, new JObject { ["quantity"] = 5 }, merge);
        patched.Registry!.Keys.Should().BeEquivalentTo(new[] { "first", "FIRST" }, "a later unrelated patch retains both keys");
        patched.Registry["FIRST"].Label.Should().Be("changed");
        patched.Registry["first"].Label.Should().Be("lower");
    }

    [Theory]
    [InlineData(PartialJsonNullPolicy.SetNull)]
    [InlineData(PartialJsonNullPolicy.Ignore)]
    [InlineData(PartialJsonNullPolicy.Reject)]
    public void Partial_nullable_null_policy_is_explicit(PartialJsonNullPolicy policy)
    {
        var target = New();
        var apply = () => new PartialJsonApplicator<PatchDocumentEntity>(
            new JObject { ["optional"] = JValue.CreateNull() }, policy).ApplyToCopy(target);
        if (policy == PartialJsonNullPolicy.Reject) apply.Should().Throw<InvalidOperationException>();
        else target = apply();
        target.Optional.Should().Be(policy == PartialJsonNullPolicy.SetNull ? null : 7);
    }

    [Theory]
    [InlineData(MergePatchNullPolicy.SetDefault)]
    [InlineData(MergePatchNullPolicy.Reject)]
    public void Merge_nonnullable_null_policy_is_explicit(MergePatchNullPolicy policy)
    {
        var target = New();
        var apply = () => new MergePatchApplicator<PatchDocumentEntity>(
            new JObject { ["quantity"] = JValue.CreateNull() }, policy).ApplyToCopy(target);
        if (policy == MergePatchNullPolicy.Reject) apply.Should().Throw<InvalidOperationException>();
        else target = apply();
        target.Quantity.Should().Be(policy == MergePatchNullPolicy.SetDefault ? 0 : 9);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_conversion_leaves_the_original_target_unchanged(bool merge)
    {
        var target = New();
        var apply = () => Apply(target, new JObject { ["name"] = "after", ["quantity"] = "not-an-integer" }, merge);
        apply.Should().Throw<Exception>();
        target.Name.Should().Be("before");
        target.Quantity.Should().Be(9);
    }

    [Theory]
    [InlineData(false, "id")]
    [InlineData(true, "id")]
    [InlineData(false, "Id")]
    [InlineData(true, "__koan_type")]
    public void Identity_and_family_discriminator_refuse_before_save(bool merge, string member)
    {
        var target = New();
        var apply = () => Apply(target, new JObject { [member] = "forged" }, merge);
        apply.Should().Throw<InvalidOperationException>();
        target.Name.Should().Be("before");
    }

    [Fact]
    public void Identity_refuses_through_a_mapped_wire_alias()
    {
        var target = new PatchAliasKeyEntity { Name = "before" };
        var apply = () => new MergePatchApplicator<PatchAliasKeyEntity>(
            new JObject { ["key"] = "forged" }, MergePatchNullPolicy.SetDefault).ApplyToCopy(target);
        apply.Should().Throw<InvalidOperationException>();
        target.Name.Should().Be("before");
    }

    [Fact]
    public async Task Dry_run_rehearses_the_copy_and_persists_nothing()
    {
        using var host = await Start();
        try
        {
            var original = await New(withTags: true).Save();
            using var scope = host.Services.CreateScope();
            var endpoint = scope.ServiceProvider.GetRequiredService<IEntityEndpointService<PatchDocumentEntity, string>>();
            var result = await endpoint.Patch(new EntityPatchRequest<PatchDocumentEntity, string>
            {
                Context = new EntityRequestContext(scope.ServiceProvider, new QueryOptions(), default, user: new ClaimsPrincipal()),
                Id = original.Id,
                Patch = new JObject { ["name"] = "rehearsed" },
                Kind = EntityPatchKind.PartialJson,
                DryRun = true,
            });
            result.ShortCircuitResult.Should().BeNull();
            result.Model!.Name.Should().Be("rehearsed");
            var stored = (await PatchDocumentEntity.Get(original.Id))!;
            stored.Name.Should().Be("before");
            stored.Tags.Should().Equal("public", "private");
            stored.Retained.Should().Be("domain-state");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/merge-patch+json")]
    public async Task HTTP_name_patch_preserves_typed_collection_state(string contentType)
    {
        using var host = await Start();
        try
        {
            var original = await New(withTags: true).Save();
            using var client = host.GetTestClient();
            using var request = new HttpRequestMessage(HttpMethod.Patch, "/typed-patch/" + original.Id)
            { Content = new StringContent("{\"name\":\"after\"}", Encoding.UTF8, contentType) };
            var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.OK, "{0}", await response.Content.ReadAsStringAsync());
            var stored = (await PatchDocumentEntity.Get(original.Id))!;
            stored.Name.Should().Be("after");
            stored.Tags.Should().Equal("public", "private");
            stored.Retained.Should().Be("domain-state");
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData("default", HttpStatusCode.OK)]
    [InlineData("reject", HttpStatusCode.UnprocessableEntity)]
    public async Task HTTP_merge_null_policy_has_a_corrective_result(string policy, HttpStatusCode expected)
    {
        using var host = await Start();
        try
        {
            var original = await New().Save();
            using var client = host.GetTestClient();
            using var request = new HttpRequestMessage(HttpMethod.Patch, "/typed-patch/" + original.Id + "?nulls=" + policy)
            { Content = new StringContent("{\"quantity\":null}", Encoding.UTF8, "application/merge-patch+json") };
            var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(expected);
            (await PatchDocumentEntity.Get(original.Id))!.Quantity.Should().Be(policy == "default" ? 0 : 9);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task HTTP_identity_patch_refuses_correctively_without_saving()
    {
        using var host = await Start();
        try
        {
            var original = await New().Save();
            using var client = host.GetTestClient();
            using var request = new HttpRequestMessage(HttpMethod.Patch, "/typed-patch/" + original.Id)
            { Content = new StringContent("{\"id\":\"" + original.Id + "\"}", Encoding.UTF8, "application/merge-patch+json") };
            var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
            var stored = (await PatchDocumentEntity.Get(original.Id))!;
            stored.Name.Should().Be("before");
            stored.Id.Should().Be(original.Id);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None);
        }
    }

    private static PatchDocumentEntity Apply(PatchDocumentEntity target, JObject patch, bool merge)
        => merge
            ? new MergePatchApplicator<PatchDocumentEntity>(patch, MergePatchNullPolicy.SetDefault).ApplyToCopy(target)
            : new PartialJsonApplicator<PatchDocumentEntity>(patch, PartialJsonNullPolicy.SetNull).ApplyToCopy(target);

    private static PatchDocumentEntity New(bool withTags = false)
    {
        var value = new PatchDocumentEntity { Id = Guid.NewGuid().ToString("N"), Name = "before", Quantity = 9, Optional = 7,
            Detail = new() { Name = "prior", Sibling = "persisted-sibling" }, State = PatchDocumentState.Ready };
        value.SetRetained("domain-state");
        if (withTags) { value.Tags = new(); value.Tags.Add("public"); value.Tags.Add("private"); }
        return value;
    }

    private static async Task<IHost> Start()
    {
        var host = Host.CreateDefaultBuilder().ConfigureWebHost(web =>
        {
            web.UseEnvironment("Test"); web.UseTestServer();
            web.ConfigureServices(services => { services.AddKoan(); services.AddKoanControllersFrom<PatchDocumentController>(); });
            web.Configure(_ => { });
        }).Build();
        try
        {
            await host.StartAsync();
            return host;
        }
        catch
        {
            try
            {
                // A partially started host may already hold the AppHost lease; release it before disposing.
                await host.StopAsync(CancellationToken.None);
            }
            catch
            {
                // Best effort only: the original start failure is the reported failure.
            }

            host.Dispose();
            throw;
        }
    }
}

[Access(read: Access.Anyone, write: Access.Anyone, remove: Access.Anyone)]
public sealed class PatchDocumentEntity : Entity<PatchDocumentEntity>
{
    public string Name { get; set; } = "";
    public string Retained { get; private set; } = "constructor-state";
    public void SetRetained(string value) => Retained = value;
    public int Quantity { get; set; }
    public int? Optional { get; set; }
    public PatchDocumentDetail? Detail { get; set; }
    public PatchDocumentState State { get; set; }
    public PatchDocumentTags? Tags { get; set; }
    public string Note { get; set; } = "seed";
    public int Seeded { get; set; } = 41;
    public List<PatchDocumentBadge>? Badges { get; set; }
    public Dictionary<string, PatchDocumentBadge>? Registry { get; set; }
    public PatchFamilyRoot? Node { get; set; }
}
public enum PatchDocumentState { Unknown, Ready }
public sealed class PatchDocumentDetail
{
    [JsonProperty("display_name")]
    public string Name { get; set; } = "";
    public string Sibling { get; set; } = "default-sibling";
    public string Stored { get; private set; } = "stored-note";
}
public sealed class PatchDocumentBadge
{
    public string Label { get; set; } = "";
    public string Guarded { get; private set; } = "stored-guard";
}
public sealed class PatchDocumentTags : IEnumerable<string>
{
    private readonly List<string> _values = [];
    public void Add(string value) => _values.Add(value);
    public void Clear() => _values.Clear();
    public IEnumerator<string> GetEnumerator() => _values.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
[Access(read: Access.Anyone, write: Access.Anyone, remove: Access.Anyone)]
public class PatchFamilyRoot : Entity<PatchFamilyRoot>
{
    public string Trunk { get; set; } = "";
}
[Access(read: Access.Anyone, write: Access.Anyone, remove: Access.Anyone)]
public sealed class PatchFamilyAlpha : PatchFamilyRoot<PatchFamilyAlpha>
{
    public string AlphaOnly { get; set; } = "";
}
[Access(read: Access.Anyone, write: Access.Anyone, remove: Access.Anyone)]
public sealed class PatchAliasKeyEntity : Entity<PatchAliasKeyEntity>
{
    [JsonProperty("key")]
    public new string Id { get => base.Id; set => base.Id = value; }
    public string Name { get; set; } = "";
}
[Route("typed-patch")]
public sealed class PatchDocumentController : EntityController<PatchDocumentEntity> { }
