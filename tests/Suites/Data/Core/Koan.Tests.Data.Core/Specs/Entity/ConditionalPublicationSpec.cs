using Koan.Core;
using Koan.Data.Abstractions;
using Koan.Data.Abstractions.Capabilities;
using Koan.Data.Core;
using Koan.Testing.Integration;
using Koan.Tests.Data.Core.EntityFamilyFixtures;
using Microsoft.Extensions.DependencyInjection;

namespace Koan.Tests.Data.Core.Specs.Entity;

public sealed class ConditionalPublicationSpec
{
    [Fact]
    public async Task Root_conditional_replace_compares_stored_state_and_never_inserts_a_missing_identity()
    {
        await using var host = await KoanIntegrationHost.Configure()
            .ConfigureServices(services => services.AddKoan()).StartAsync(TestContext.Current.CancellationToken);
        using var route = EntityContext.With(adapter: "inmemory", partition: Guid.NewGuid().ToString("N"));
        var saved = await new GeneratedFamilyMedia { Kind = "expected" }.Save();
        var conditional = Data<GeneratedFamilyMedia, string>.As<IConditionalWriteRepository<GeneratedFamilyMedia, string>>();
        conditional.Should().NotBeNull();

        (await conditional!.ConditionalReplaceAsync(
            new GeneratedFamilyMedia { Id = saved.Id, Kind = "published" }, Koan.Data.Abstractions.Filtering.LinqFilterCompiler.Compile<GeneratedFamilyMedia>(row => row.Kind == "expected")))
            .Should().BeTrue();
        (await conditional.ConditionalReplaceAsync(
            new GeneratedFamilyMedia { Id = saved.Id, Kind = "delayed" }, Koan.Data.Abstractions.Filtering.LinqFilterCompiler.Compile<GeneratedFamilyMedia>(row => row.Kind == "expected")))
            .Should().BeFalse();
        var missingId = Guid.NewGuid().ToString("N");
        (await conditional.ConditionalReplaceAsync(
            new GeneratedFamilyMedia { Id = missingId, Kind = "new" }, Koan.Data.Abstractions.Filtering.LinqFilterCompiler.Compile<GeneratedFamilyMedia>(row => true))).Should().BeFalse();

        (await GeneratedFamilyMedia.Get(saved.Id))!.Kind.Should().Be("published");
        (await GeneratedFamilyMedia.Get(missingId)).Should().BeNull();
    }

    [Fact]
    public async Task Variant_without_exact_membership_conditional_seam_does_not_advertise_root_capability()
    {
        await using var host = await KoanIntegrationHost.Configure()
            .ConfigureServices(services => services.AddKoan()).StartAsync(TestContext.Current.CancellationToken);
        using var route = EntityContext.With(adapter: "inmemory", partition: Guid.NewGuid().ToString("N"));
        var root = await new GeneratedFamilyMedia { Kind = "root" }.Save();
        var variant = host.Services.GetRequiredService<IDataService>().GetRepository<GeneratedFamilyAnime, string>();

        Data<GeneratedFamilyMedia, string>.Capabilities.Has(DataCaps.Write.ConditionalReplace).Should().BeTrue();
        Data<GeneratedFamilyAnime, string>.As<IConditionalWriteRepository<GeneratedFamilyAnime, string>>()
            .Should().BeNull("an exact-variant native conditional seam is not implemented");
        (await GeneratedFamilyMedia.Get(root.Id))!.Kind.Should().Be("root");
        DataCaps.Describe(variant, variant.GetType().Name).Has(DataCaps.Write.ConditionalReplace)
            .Should().BeFalse("a variant cannot advertise an unusable root capability");
    }
}
