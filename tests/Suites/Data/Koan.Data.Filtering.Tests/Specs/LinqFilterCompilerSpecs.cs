using System.Linq.Expressions;

namespace Koan.Data.Filtering.Tests.Specs;

/// <summary>
/// Verifies the LINQ front-end lowers expressions into the same AST the DSL produces (so both
/// entry points converge), and that un-liftable expressions degrade to a ClrFilter that the
/// evaluator still runs correctly in memory.
/// </summary>
public sealed class LinqFilterCompilerSpecs
{
    private static readonly Gamer[] Seed =
    {
        new() { Id = "g1", Name = "Leo",  Level = 10, Score = 100,  Region = Region.EU, Games = new() { "ffxiv", "wow" } },
        new() { Id = "g2", Name = "Lena", Level = 20, Score = null, Region = Region.NA, Games = new() { "wow" } },
        new() { Id = "g3", Name = "Max",  Level = 30, Score = 300,  Region = Region.JP, Games = new() },
        new() { Id = "g4", Name = "Mia",  Level = 5,  Score = 50,   Region = Region.EU, Games = new() { "ffxiv" } },
    };

    private static List<string> ByLinq(Expression<Func<Gamer, bool>> predicate)
        => Run(LinqFilterCompiler.Compile(predicate));

    private static List<string> ByDsl(string json)
        => Run(JsonFilterParser.Parse<Gamer>(json));

    private static List<string> Run(Filter filter)
    {
        var pred = InMemoryFilterEvaluator.Compile<Gamer>(filter);
        return Seed.Where(pred).Select(g => g.Id).OrderBy(x => x).ToList();
    }

    [Fact]
    public void Collection_contains_converges_with_dsl()
    {
        var linq = ByLinq(g => g.Games.Contains("ffxiv"));
        linq.Should().Equal("g1", "g4");
        linq.Should().Equal(ByDsl("{ \"Games\": \"ffxiv\" }"));
    }

    [Fact]
    public void Scalar_in_converges_with_dsl()
    {
        var levels = new[] { 10, 30 };
        Expression<Func<Gamer, bool>> predicate = g => levels.Contains(g.Level);
        var call = predicate.Body.Should().BeAssignableTo<MethodCallExpression>().Subject;
        call.Method.DeclaringType.Should().Be(typeof(MemoryExtensions),
            "C# 14 prefers the span overload for an array receiver");

        var field = LinqFilterCompiler.Compile(predicate).Should().BeOfType<FieldFilter>().Subject;
        field.Field.ToString().Should().Be(nameof(Gamer.Level));
        field.Operator.Should().Be(FilterOperator.In);
        field.Value.Should().BeOfType<FilterValue.Set>().Which.Values.Should().Equal(10, 30);

        var linq = Run(field);
        linq.Should().Equal("g1", "g3");
        linq.Should().Equal(ByDsl("{ \"Level\": { \"$in\": [10, 30] } }"));
    }

    [Fact]
    public void Scalar_in_from_inline_array_lowers_to_structured_filter()
    {
        Expression<Func<Gamer, bool>> predicate = g => new[] { 10, 30 }.Contains(g.Level);
        predicate.Body.Should().BeAssignableTo<MethodCallExpression>()
            .Which.Method.DeclaringType.Should().Be(typeof(MemoryExtensions));

        var field = LinqFilterCompiler.Compile(predicate).Should().BeOfType<FieldFilter>().Subject;
        field.Field.ToString().Should().Be(nameof(Gamer.Level));
        field.Operator.Should().Be(FilterOperator.In);
        field.Value.Should().BeOfType<FilterValue.Set>().Which.Values.Should().Equal(10, 30);
        Run(field).Should().Equal("g1", "g3");
    }

    [Fact]
    public void Explicit_enumerable_contains_remains_structured()
    {
        var levels = new[] { 10, 30 };
        Expression<Func<Gamer, bool>> predicate = g => Enumerable.Contains(levels, g.Level);
        predicate.Body.Should().BeAssignableTo<MethodCallExpression>()
            .Which.Method.DeclaringType.Should().Be(typeof(Enumerable));

        LinqFilterCompiler.Compile(predicate).Should().BeOfType<FieldFilter>()
            .Which.Operator.Should().Be(FilterOperator.In);
    }

    [Fact]
    public void Memory_contains_with_comparer_remains_clrfilter()
    {
        var names = new[] { "leo", "max" };
        Expression<Func<Gamer, bool>> predicate =
            g => names.Contains(g.Name, StringComparer.OrdinalIgnoreCase);
        var call = predicate.Body.Should().BeAssignableTo<MethodCallExpression>().Subject;
        call.Method.DeclaringType.Should().Be(typeof(MemoryExtensions));
        call.Arguments.Should().HaveCount(3);

        LinqFilterCompiler.Compile(predicate).Should().BeOfType<ClrFilter>(
            "a comparer changes equality semantics and cannot be represented by a default In node");
    }

    [Fact]
    public void Entity_dependent_memory_contains_source_remains_clrfilter()
    {
        Expression<Func<Gamer, bool>> predicate = g => g.Games.ToArray().Contains(g.Name);
        predicate.Body.Should().BeAssignableTo<MethodCallExpression>()
            .Which.Method.DeclaringType.Should().Be(typeof(MemoryExtensions));

        LinqFilterCompiler.Compile(predicate).Should().BeOfType<ClrFilter>();
    }

    [Fact]
    public void Unrelated_method_named_contains_remains_clrfilter()
    {
        var levels = new[] { 10, 30 };
        Expression<Func<Gamer, bool>> predicate = g => Contains(levels, g.Level);

        LinqFilterCompiler.Compile(predicate).Should().BeOfType<ClrFilter>();
    }

    [Fact]
    public void Range_and_converges_with_dsl()
    {
        var linq = ByLinq(g => g.Level >= 10 && g.Level <= 25);
        linq.Should().Equal("g1", "g2");
        linq.Should().Equal(ByDsl("{ \"Level\": { \"$between\": [10, 25] } }"));
    }

    [Fact]
    public void String_startswith_converges_with_dsl()
    {
        var linq = ByLinq(g => g.Name.StartsWith("Le"));
        linq.Should().Equal("g1", "g2");
        linq.Should().Equal(ByDsl("{ \"Name\": \"Le*\" }"));
    }

    [Fact]
    public void Enum_equality()
        => ByLinq(g => g.Region == Region.EU).Should().Equal("g1", "g4");

    [Fact]
    public void Or_of_comparisons()
        => ByLinq(g => g.Level < 10 || g.Level > 25).Should().Equal("g3", "g4");

    [Fact]
    public void Unliftable_expression_falls_to_clrfilter_and_still_evaluates()
    {
        // g.Games.Count is not an entity field path -> ClrFilter -> evaluated in memory.
        var compiled = LinqFilterCompiler.Compile<Gamer>(g => g.Games.Count > 1);
        compiled.Should().BeOfType<ClrFilter>();
        Run(compiled).Should().Equal("g1");
    }

    [Fact]
    public void Optional_filter_with_null_param_short_circuits_to_match_all()
    {
        int? minScore = null;
        // `minScore == null` is true -> the || folds to match-all WITHOUT evaluating minScore.Value
        // (which would throw "Nullable object must have a value"). The regression this guards against.
        var linq = ByLinq(g => minScore == null || g.Score >= minScore.Value);
        linq.Should().Equal("g1", "g2", "g3", "g4");
    }

    [Fact]
    public void Optional_filter_with_set_param_applies_the_clause()
    {
        int? minScore = 100;
        var linq = ByLinq(g => minScore == null || g.Score >= minScore.Value);
        linq.Should().Equal("g1", "g3");
    }

    [Fact]
    public void Field_to_field_comparison_degrades_to_clrfilter()
    {
        // The value side references the entity -> not a field/value filter; must NOT be Eval()'d
        // (that would throw), so it degrades to an in-memory ClrFilter and still evaluates.
        var compiled = LinqFilterCompiler.Compile<Gamer>(g => g.Score > g.Level);
        compiled.Should().BeOfType<ClrFilter>();
        Run(compiled).Should().Equal("g1", "g3", "g4");
    }

    [Fact]
    public void Entity_argument_method_degrades_to_clrfilter()
    {
        // An entity-derived method argument must not be Eval()'d either; degrade, don't throw.
        var compiled = LinqFilterCompiler.Compile<Gamer>(g => g.Name.StartsWith(g.Id));
        compiled.Should().BeOfType<ClrFilter>();
        Run(compiled).Should().BeEmpty();
    }

    private static bool Contains(IEnumerable<int> values, int value) => values.Contains(value);
}
