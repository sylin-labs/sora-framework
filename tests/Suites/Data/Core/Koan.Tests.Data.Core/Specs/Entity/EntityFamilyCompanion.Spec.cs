using System.Reflection;
using Koan.Core.Hosting.Registry;
using Koan.Data.Abstractions;
using Koan.Data.Core.Model;
using Koan.Data.Core.Polymorphism;
using Koan.Tests.Data.Core.EntityFamilyFixtures;
using Koan.Core;
using Koan.Data.Core;
using Koan.Testing.Integration;
using Microsoft.Extensions.DependencyInjection;

namespace Koan.Tests.Data.Core.Specs.Entity;

public sealed class EntityFamilyCompanionSpec
{
    [Fact]
    public async Task Insert_through_a_variant_preserves_root_identity_collisions()
    {
        await using var host = await KoanIntegrationHost.Configure()
            .ConfigureServices(services => services.AddKoan()).StartAsync(TestContext.Current.CancellationToken);
        using var route = EntityContext.With(adapter: "inmemory", partition: Guid.NewGuid().ToString("N"));
        var root = await new GeneratedFamilyMedia { Kind = "root" }.Save();
        var repository = host.Services.GetRequiredService<IDataService>().GetRepository<GeneratedFamilyAnime, string>();
        var inserts = (IInsertOnlyRepository<GeneratedFamilyAnime, string>)repository;
        var conflict = await inserts.Insert(new GeneratedFamilyAnime { Id = root.Id, Kind = "anime", Episodes = 12 });
        conflict.Outcome.Should().Be(MutationOutcome.Conflict);
        conflict.Entity.Should().BeNull();
        (await GeneratedFamilyMedia.Get(root.Id))!.Kind.Should().Be("root");
        var inserted = await inserts.Insert(new GeneratedFamilyAnime { Kind = "new", Episodes = 24 });
        inserted.Outcome.Should().Be(MutationOutcome.Inserted);
        (await GeneratedFamilyAnime.Get(inserted.Key))!.Episodes.Should().Be(24);
    }

    [Fact]
    public void Generated_companion_preserves_the_root_and_carries_the_family_contract()
    {
        var companion = typeof(GeneratedFamilyMedia<GeneratedFamilyAnime>);

        companion.BaseType.Should().Be(typeof(GeneratedFamilyMedia));
        typeof(IEntityFamilyVariant<GeneratedFamilyMedia, GeneratedFamilyAnime, string>)
            .IsAssignableFrom(companion)
            .Should().BeTrue();
    }

    [Fact]
    public void Generated_companion_declares_the_complete_exact_typed_get_family()
    {
        var gets = typeof(GeneratedFamilyMedia<GeneratedFamilyAnime>)
            .GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(method => method.Name == nameof(GeneratedFamilyAnime.Get))
            .ToArray();

        gets.Should().HaveCount(4);
        gets.Count(method =>
                method.ReturnType == typeof(Task<GeneratedFamilyAnime?>))
            .Should().Be(2);
        gets.Count(method =>
                method.ReturnType == typeof(Task<IReadOnlyList<GeneratedFamilyAnime?>>))
            .Should().Be(2);
    }

    [Fact]
    public void Self_closed_variant_is_registered_for_discovery()
    {
        KoanRegistry.GetDiscoveredImplementors(typeof(IEntity))
            .Should().Contain(typeof(GeneratedFamilyAnime));
    }

    [Fact]
    public void Variant_of_an_imported_companion_is_registered_in_the_consumer_assembly()
    {
        (typeof(ImportedFamilyMedia<CrossAssemblyFamilyAnime>).Assembly
            == typeof(CrossAssemblyFamilyAnime).Assembly)
            .Should().BeFalse();
        typeof(IEntityFamilyVariant<ImportedFamilyMedia, CrossAssemblyFamilyAnime, string>)
            .IsAssignableFrom(typeof(CrossAssemblyFamilyAnime))
            .Should().BeTrue();
        KoanRegistry.GetDiscoveredImplementors(typeof(IEntity))
            .Should().Contain(typeof(CrossAssemblyFamilyAnime));
        EntityTypeCatalog.HasVariants(typeof(ImportedFamilyMedia))
            .Should().BeTrue();
    }

    // These assignments are compile-time evidence that ordinary point syntax binds every generated overload to
    // GeneratedFamilyAnime rather than to the GeneratedFamilyMedia root.
    private static Task<GeneratedFamilyAnime?> BindSingle(string id, CancellationToken ct)
        => GeneratedFamilyAnime.Get(id, ct);

    private static Task<IReadOnlyList<GeneratedFamilyAnime?>> BindMany(
        IEnumerable<string> ids,
        CancellationToken ct)
        => GeneratedFamilyAnime.Get(ids, ct);

    private static Task<GeneratedFamilyAnime?> BindPartitionedSingle(
        string id,
        string partition,
        CancellationToken ct)
        => GeneratedFamilyAnime.Get(id, partition, ct);

    private static Task<IReadOnlyList<GeneratedFamilyAnime?>> BindPartitionedMany(
        IEnumerable<string> ids,
        string partition,
        CancellationToken ct)
        => GeneratedFamilyAnime.Get(ids, partition, ct);

    private static Task<CrossAssemblyFamilyAnime?> BindImportedSingle(string id, CancellationToken ct)
        => CrossAssemblyFamilyAnime.Get(id, ct);
}

public class GeneratedFamilyMedia : Entity<GeneratedFamilyMedia>
{
    public string Kind { get; set; } = "";
}

public sealed class GeneratedFamilyAnime : GeneratedFamilyMedia<GeneratedFamilyAnime>
{
    public int? Episodes { get; set; }
}

public sealed class CrossAssemblyFamilyAnime : ImportedFamilyMedia<CrossAssemblyFamilyAnime>
{
    public int? Episodes { get; set; }
}
