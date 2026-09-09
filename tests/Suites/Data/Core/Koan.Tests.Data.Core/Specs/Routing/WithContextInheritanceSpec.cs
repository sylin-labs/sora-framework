using System;
using AwesomeAssertions;
using Koan.Data.Core;
using Xunit;

namespace Koan.Tests.Data.Core.Specs.Routing;

/// <summary>
/// Pins the documented inherit-unless-overridden semantics of <see cref="EntityContext.With"/> (E9): a
/// nested scope adopts the ambient context's value for every dimension whose argument is null, overrides
/// the ones it supplies, and restores the previous context on disposal — it does NOT wholesale-replace.
/// Also pins the mutual-exclusion of source/adapter on the EFFECTIVE (post-inheritance) values.
/// </summary>
public class WithContextInheritanceSpec
{
    [Fact]
    public void Empty_route_overrides_clear_axes_to_the_same_default_as_an_absent_context()
    {
        using var outer = EntityContext.With(source: "analytics", partition: "fr");
        using (EntityContext.With(source: "", adapter: "json", partition: ""))
        {
            EntityContext.Current!.Source.Should().BeNull();
            EntityContext.Current.Adapter.Should().Be("json");
            EntityContext.Current.Partition.Should().BeNull();
        }
        EntityContext.Current!.Source.Should().Be("analytics");
        EntityContext.Current.Partition.Should().Be("fr");
    }

    [Fact]
    public void Omitted_dimensions_inherit_the_ambient_context()
    {
        using var outer = EntityContext.With(source: "analytics", partition: "archive");

        // Inner sets nothing → inherits both source and partition unchanged (not cleared to null).
        using var inner = EntityContext.With();

        EntityContext.Current!.Source.Should().Be("analytics");
        EntityContext.Current!.Partition.Should().Be("archive");
        EntityContext.Current!.Adapter.Should().BeNull();
    }

    [Fact]
    public void Supplied_dimension_overrides_while_others_carry_over()
    {
        using var outer = EntityContext.With(source: "analytics", partition: "archive");

        // Add a partition override; source carries over from the ambient context.
        using var inner = EntityContext.Partition("cold");

        EntityContext.Current!.Source.Should().Be("analytics");
        EntityContext.Current!.Partition.Should().Be("cold");
    }

    [Fact]
    public void Overriding_source_replaces_only_that_dimension()
    {
        using var outer = EntityContext.With(source: "analytics", partition: "archive");

        using var inner = EntityContext.With(source: "backup");

        EntityContext.Current!.Source.Should().Be("backup");
        EntityContext.Current!.Partition.Should().Be("archive"); // inherited
    }

    [Fact]
    public void Adapter_while_a_source_is_inherited_throws_mutual_exclusion()
    {
        using var outer = EntityContext.Source("analytics");

        // The effective context would carry the inherited source AND the new adapter → forbidden.
        var act = () => EntityContext.Adapter("sqlite");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Source_while_an_adapter_is_inherited_throws_mutual_exclusion()
    {
        using var outer = EntityContext.Adapter("sqlite");

        var act = () => EntityContext.Source("analytics");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Disposing_the_inner_scope_restores_the_outer_context()
    {
        using var outer = EntityContext.With(source: "analytics", partition: "archive");

        using (EntityContext.With(source: "backup", partition: "cold"))
        {
            EntityContext.Current!.Source.Should().Be("backup");
        }

        // Inner disposed → outer context is back, intact.
        EntityContext.Current!.Source.Should().Be("analytics");
        EntityContext.Current!.Partition.Should().Be("archive");
    }
}
