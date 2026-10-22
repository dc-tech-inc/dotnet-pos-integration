using DcTech.PosIntegration.Worker;
using Microsoft.AspNetCore.Mvc.Testing;
using static DcTech.PosIntegration.Tests.Unit.TestData;

namespace DcTech.PosIntegration.Tests.Integration;

/// <summary>The real fake POS (in-process HTTP, with injected 503s and redelivery) feeding the real SQL store.</summary>
public class EndToEndTests(SqlFixture db) : IClassFixture<SqlFixture>
{
    [SqlFact]
    public async Task A_flaky_at_least_once_pos_ends_in_a_clean_reconciliation()
    {
        await using var pos = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b
            .UseSetting("FakePos:Total", "150")
            .UseSetting("FakePos:FailureEvery", "4")
            .UseSetting("FakePos:Redeliver", "3"));
        var store = db.Store;
        await store.UpsertOpeningStockAsync(Enumerable.Range(1, 10).Select(i => ($"SKU-{i:D4}", 60)), default);
        var source = new HttpSaleSource(pos.CreateClient());

        var sync = Sync(source, store, pageSize: 20);
        var result = await sync.DrainAsync(default);
        var report = await new Reconciler(source, store, NoWaitRetry(6)).RunAsync(default);

        Assert.Equal(150, result.Applied);
        Assert.True(result.Duplicates > 0, "redelivered sales should have been seen and ignored");
        Assert.True(result.ShortfallUnits > 0, "60 units per SKU is not enough for 150 sales");
        Assert.True(report.IsClean, string.Join("; ", report.Mismatches));

        // A second run must find nothing new.
        Assert.Equal(0, (await sync.DrainAsync(default)).Applied);
    }
}
