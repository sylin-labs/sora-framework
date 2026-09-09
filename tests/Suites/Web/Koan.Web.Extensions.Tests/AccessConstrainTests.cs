using System;
using System.Linq;
using System.Security.Claims;
using AwesomeAssertions;
using Koan.Web.Authorization;
using Koan.Web.Hooks;
using Koan.Data.Abstractions.Filtering;
using Xunit;

namespace Koan.Web.Extensions.Tests;

/// <summary>
/// SEC-0004 Slice B (Constrain) — unit coverage for the accumulator, the row-bound evaluator stub, the cache
/// gate-merge precedence, and the discovery mapping. The end-to-end ownership behavior lives in
/// <see cref="EntityConstrainE2ESpec"/>. Public test realizations so the singleton cache can Activator-construct
/// them cross-assembly.
/// </summary>
public sealed class AccessConstrainTests
{
    [Access(read: "anyone")]
    public sealed class Widget2 { }

    public sealed class Widget2Access : EntityAccess<Widget2>
    {
        protected override ActionGate ReadGate => Gate.Is("admin");
    }

    private static ClaimsPrincipal Authed(params string[] roles)
        => new(new ClaimsIdentity(roles.Select(r => new Claim(ClaimTypes.Role, r)), "test"));

    private static AccessBag Bag(string[]? roles = null, bool owner = false)
        => new(roles ?? Array.Empty<string>(), Array.Empty<Grant>(), owner, Anyone: false, Authenticated: true);

    // ── AccessFilter accumulator ─────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Where_accumulates_predicates()
    {
        var f = new AccessFilter<Memo>();
        f.Where(m => m.Text == "a").Where(m => m.Text == "b");
        f.Filter.Should().BeOfType<AllOf>().Which.Operands.Should().HaveCount(2);
    }

    [Fact]
    public void Access_and_options_capture_supported_values_when_contributed()
    {
        var text = "original";
        var allowed = new System.Collections.Generic.List<string> { "allowed" };
        var access = new AccessFilter<Memo>();
        var options = new QueryOptions();
        access.Where(memo => memo.Text == text || allowed.Contains(memo.Text));
        options.AddPredicate<Memo>(memo => memo.Text == text || allowed.Contains(memo.Text));
        text = "changed";
        allowed.Clear();
        allowed.Add("later");

        foreach (var filter in new[] { access.Filter, options.Filter })
        {
            var matches = InMemoryFilterEvaluator.Compile<Memo>(filter!);
            matches(new Memo { Text = "original" }).Should().BeTrue();
            matches(new Memo { Text = "allowed" }).Should().BeTrue();
            matches(new Memo { Text = "changed" }).Should().BeFalse();
            matches(new Memo { Text = "later" }).Should().BeFalse();
        }
    }

    [Fact]
    public void Stamp_rejects_a_computed_selector()
    {
        var f = new AccessFilter<Memo>();
        var act = () => f.Stamp(m => m.Text + "!", "x");
        act.Should().Throw<InvalidOperationException>().WithMessage("*writable property*");
    }

    // ── evaluator row overload (lazy owner probe; Slice C seam) ──────────────────────────────────────────────────
    [Fact]
    public void Row_owner_probe_runs_only_when_a_bag_requires_owner()
    {
        var rolesOnly = new ActionGate(new[] { Bag(roles: new[] { "admin" }) });
        var probed = false;
        AccessGateEvaluator.Evaluate(rolesOnly, Authed("admin"), () => { probed = true; return true; });
        probed.Should().BeFalse("no bag requires owner → the row predicate is never compiled/run");

        var ownerGate = new ActionGate(new[] { Bag(owner: true) });
        probed = false;
        AccessGateEvaluator.Evaluate(ownerGate, Authed(), () => { probed = true; return true; });
        probed.Should().BeTrue("an owner bag triggers the probe");
    }

    // ── cache gate-merge: realization gate (highest precedence) overrides [Access] ───────────────────────────────
    [Fact]
    public void Realization_gate_overrides_the_access_attribute()
    {
        var cache = new AccessGateCache(realizationFor: t => t == typeof(Widget2) ? typeof(Widget2Access) : null);
        var read = cache.GetOrCompile(typeof(Widget2)).For(EntityAuthorizeActions.Read);
        read.IsOpen.Should().BeFalse("[Access(read: anyone)] is overridden by the realization's is:admin ReadGate");
        read.AnyOf.Should().ContainSingle().Which.IsRolesAnyOf.Should().Contain("admin");
    }

    // ── discovery mapping ────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Registry_maps_a_realization_to_its_entity()
    {
        var reg = EntityAccessRegistry.FromImplementors(new[] { typeof(MemoAccess) });
        reg.For(typeof(Memo)).Should().Be(typeof(MemoAccess));
        reg.For(typeof(Trinket)).Should().BeNull();
    }
}
