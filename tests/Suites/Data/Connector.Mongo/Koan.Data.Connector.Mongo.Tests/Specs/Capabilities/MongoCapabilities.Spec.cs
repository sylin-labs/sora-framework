using Koan.Data.Abstractions.Capabilities;
using Koan.Data.Abstractions.Filtering;
using Microsoft.Extensions.DependencyInjection;

namespace Koan.Data.Connector.Mongo.Tests.Specs.Capabilities;

public sealed class MongoCapabilitiesSpec(MongoFixture fixture, ITestOutputHelper output)
    : KoanDataSpec<MongoFixture>(fixture, output)
{
    [Fact]
    public async Task Repository_reports_expected_capabilities()
    {
        RequireBackingStore();
        await using var host = await BootAsync();
        var data = host.Services.GetRequiredService<IDataService>();

        var repo = data.GetRepository<CapabilityProbe, string>();
        repo.Should().BeAssignableTo<IQueryRepository<CapabilityProbe, string>>();

        // ARCH-0084: negotiate via the unified CapabilitySet.
        var caps = DataCaps.Describe(repo, repo.GetType().Name);
        caps.Has(DataCaps.Query.Linq).Should().BeTrue();
        caps.Has(DataCaps.Query.String).Should().BeFalse();
        caps.Has(DataCaps.Write.BulkUpsert).Should().BeTrue();
        caps.Has(DataCaps.Write.BulkDelete).Should().BeTrue();
        caps.Has(DataCaps.Write.AtomicBatch).Should().BeFalse();
        caps.Has(DataCaps.Write.FastRemove).Should().BeFalse();

        // Mongo lowers collection-element substring natively ($elemMatch + $regex) and says so.
        var filterSupport = caps.Detail<FilterSupport>(DataCaps.Query.Filter) ?? FilterSupport.None;
        filterSupport.CollectionOperators.Should().Contain(FilterOperator.HasContains);
        filterSupport.SupportsSameIdIn.Should().BeTrue();

        var partition = NewPartition();
        using var lease = Lease(partition);

        await CapabilityProbe.Upsert(new CapabilityProbe { Name = "cap" });
        var count = await CapabilityProbe.Count.Exact();
        count.Should().Be(1);
    }

    private sealed class CapabilityProbe : Entity<CapabilityProbe>
    {
        public string Name { get; set; } = "";
    }
}
