using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DcTech.PosIntegration.FakePos;
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

/// <summary>A sale posted through a simulated terminal with a supervisor override, booked by the real store. Own database, own fixture.</summary>
public class OverrideEndToEndTests(SqlFixture db) : IClassFixture<SqlFixture>
{
    [SqlFact]
    public async Task A_sale_released_by_a_supervisor_is_booked_with_its_audit_trail()
    {
        await using var pos = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("FakePos:Total", "0"));
        var http = pos.CreateClient();
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
        var store = db.Store;
        await store.UpsertOpeningStockAsync([("SKU-0001", 100)], default);

        await http.PostAsJsonAsync("terminals/T-02/session", new SessionRequest("OP-01"), json);
        await http.PutAsJsonAsync("terminals/T-02/keylock", new KeylockRequest(KeylockPosition.Supervisor), json);
        var attempt = new SaleAttempt("OP-01", [new AttemptLine("SKU-0001", 3, EntryMethod.Keyed)], "SUP-01");
        Assert.Equal(System.Net.HttpStatusCode.Created, (await http.PostAsJsonAsync("terminals/T-02/sales", attempt, json)).StatusCode);

        var source = new HttpSaleSource(http);
        var result = await Sync(source, store).DrainAsync(default);

        Assert.Equal(1, result.Applied);
        Assert.Equal(97, await db.ScalarAsync<int>("SELECT OnHand FROM dbo.Stock WHERE Sku = 'SKU-0001'"));
        Assert.Equal("SUP-01", await db.ScalarAsync<string>("SELECT SupervisorId FROM dbo.SaleOverride WHERE Code = 'item-keyed'"));
        Assert.True((await new Reconciler(source, store, NoWaitRetry()).RunAsync(default)).IsClean);
    }
}
