using System;
using System.Collections.Generic;
using Koan.Data.Abstractions.Filtering;
using System.Security.Claims;
using AwesomeAssertions;
using Koan.Web.Authorization;
using Xunit;

namespace Koan.Web.Extensions.Tests;

/// <summary>
/// SEC-0004 (§C) — unit coverage for the pure <see cref="RowProjection{TEntity}"/>. A verb is advertised only when
/// the coarse seam allows it AND the gate re-evaluated with the row bound to <c>owner</c> allows it AND the verb's
/// Constrain predicate passes on the row. Custom verbs participate via the gate alone. No host, no discovery —
/// the projection logic is proven deterministically here; the e2e specs prove the wiring.
/// </summary>
public sealed class AccessProjectionTests
{
    private sealed class Doc
    {
        public string? OwnerId { get; set; }
        public string Title { get; set; } = "";
    }

    private static readonly Doc AlicesDoc = new() { OwnerId = "alice", Title = "a" };
    private static readonly Doc BobsDoc = new() { OwnerId = "bob", Title = "b" };

    private static ClaimsPrincipal User(params Claim[] claims) => new(new ClaimsIdentity(claims, "Test"));
    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    private static readonly Filter? NoFilter = null;

    private static AccessGate Gate(
        IDictionary<string, ActionGate>? byAction = null,
        IDictionary<string, ActionGate>? custom = null)
        => new(
            new Dictionary<string, ActionGate>(byAction ?? new Dictionary<string, ActionGate>(), StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, ActionGate>(custom ?? new Dictionary<string, ActionGate>(), StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void Open_gate_with_no_constrain_advertises_every_verb()
    {
        var proj = new RowProjection<Doc>(AccessGate.Open, User(), true, true, true,
            owner: null, authenticatedFallback: true, NoFilter, NoFilter, NoFilter);

        proj.Can(AlicesDoc).Should().Equal("read", "write", "remove");
    }

    [Fact]
    public void A_coarse_denied_verb_is_never_advertised_even_if_the_row_would_pass()
    {
        // The seam denied write coarsely (e.g. an external provider / a closed gate) — the row cannot resurrect it.
        var proj = new RowProjection<Doc>(AccessGate.Open, User(), coarseRead: true, coarseWrite: false, coarseRemove: true,
            owner: null, authenticatedFallback: true, NoFilter, NoFilter, NoFilter);

        proj.Can(AlicesDoc).Should().Equal("read", "remove");
    }

    [Fact]
    public void A_constrain_predicate_narrows_a_verb_to_the_rows_that_satisfy_it()
    {
        // write is constrained to alice's rows (the Update Where(Owner)); read/remove stay open.
        var ownAlice = LinqFilterCompiler.Compile<Doc>(d => d.OwnerId == "alice");
        var proj = new RowProjection<Doc>(AccessGate.Open, User(), true, true, true,
            owner: null, authenticatedFallback: true, NoFilter, ownAlice, NoFilter);

        proj.Can(AlicesDoc).Should().Contain("write");
        proj.Can(BobsDoc).Should().NotContain("write", "bob's row fails the write Constrain");
        proj.Can(BobsDoc).Should().Equal("read", "remove");
    }

    [Fact]
    public void An_owner_gate_term_resolves_against_the_row_when_an_owner_predicate_is_present()
    {
        // WriteGate = owner; the realization's Owner predicate matches alice's rows. The principal is authenticated
        // (so the gate is not Challenged) but only OWNS alice's row.
        var gate = Gate(byAction: new Dictionary<string, ActionGate> { ["write"] = Koan.Web.Authorization.Gate.Owner });
        Func<Doc, bool> ownerIsAlice = d => d.OwnerId == "alice";
        var proj = new RowProjection<Doc>(gate, User(new Claim(ClaimTypes.NameIdentifier, "alice")), true, true, true,
            owner: ownerIsAlice, authenticatedFallback: true, NoFilter, NoFilter, NoFilter);

        proj.Can(AlicesDoc).Should().Contain("write", "the row is owned → the owner gate term is satisfied");
        proj.Can(BobsDoc).Should().NotContain("write", "the row is not owned → the owner gate term fails");
    }

    [Fact]
    public void An_owner_gate_term_degrades_to_authenticated_when_no_owner_predicate_exists()
    {
        // [Access(write:"owner")] but NO realization → there is no Owner predicate to bind. The verb degrades to
        // "authenticated" (consistent with the coarse gate's owner→authenticated), advertised for any signed-in
        // principal and withheld from an anonymous one.
        var gate = Gate(byAction: new Dictionary<string, ActionGate> { ["write"] = Koan.Web.Authorization.Gate.Owner });

        var authed = new RowProjection<Doc>(gate, User(), true, true, true,
            owner: null, authenticatedFallback: true, NoFilter, NoFilter, NoFilter);
        authed.Can(AlicesDoc).Should().Contain("write");

        var anon = new RowProjection<Doc>(gate, Anonymous(), true, true, true,
            owner: null, authenticatedFallback: false, NoFilter, NoFilter, NoFilter);
        anon.Can(AlicesDoc).Should().NotContain("write", "no owner predicate + anonymous → the owner term cannot pass");
    }

    [Fact]
    public void A_custom_verb_is_advertised_exactly_when_its_gate_permits_the_principal()
    {
        // Standard verbs open; a custom "fulfill" verb gated on is:admin. Only an admin sees it.
        var gate = Gate(custom: new Dictionary<string, ActionGate> { ["fulfill"] = Koan.Web.Authorization.Gate.Is("admin") });

        var admin = new RowProjection<Doc>(gate, User(new Claim(ClaimTypes.Role, "admin")), true, true, true,
            owner: null, authenticatedFallback: true, NoFilter, NoFilter, NoFilter);
        admin.Can(AlicesDoc).Should().Equal("read", "write", "remove", "fulfill");

        var nonAdmin = new RowProjection<Doc>(gate, User(new Claim(ClaimTypes.Role, "clerk")), true, true, true,
            owner: null, authenticatedFallback: true, NoFilter, NoFilter, NoFilter);
        nonAdmin.Can(AlicesDoc).Should().Equal("read", "write", "remove");
    }

    [Fact]
    public void A_custom_verb_can_be_owner_gated_and_resolves_against_the_row()
    {
        var gate = Gate(custom: new Dictionary<string, ActionGate> { ["sign"] = Koan.Web.Authorization.Gate.Owner });
        Func<Doc, bool> ownerIsAlice = d => d.OwnerId == "alice";
        var proj = new RowProjection<Doc>(gate, User(new Claim(ClaimTypes.NameIdentifier, "alice")), true, true, true,
            owner: ownerIsAlice, authenticatedFallback: true, NoFilter, NoFilter, NoFilter);

        proj.Can(AlicesDoc).Should().Contain("sign");
        proj.Can(BobsDoc).Should().NotContain("sign");
    }
}
