using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DcTech.PosIntegration.FakePos;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DcTech.PosIntegration.Tests.Unit;

/// <summary>The fake POS's simulated terminal over HTTP, with the guard settings from appsettings.json (T-02 also requires scanning).</summary>
public class TerminalApiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static WebApplicationFactory<Program> Pos(int total = 5) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseSetting("FakePos:Total", total.ToString()));

    private static async Task<(HttpStatusCode Status, JournalEntry Entry)> LaunchAsync(HttpClient http, string terminal, SaleAttempt attempt)
    {
        var res = await http.PostAsJsonAsync($"terminals/{terminal}/sales", attempt, Json);
        return (res.StatusCode, (await res.Content.ReadFromJsonAsync<JournalEntry>(Json))!);
    }

    private static SaleAttempt Basket(string sku = "SKU-0001", int qty = 1, EntryMethod entry = EntryMethod.Scanned, string? supervisor = null) =>
        new("OP-01", [new AttemptLine(sku, qty, entry)], supervisor);

    [Fact]
    public async Task A_posted_sale_opens_the_drawer_and_the_next_one_waits_for_it_to_close()
    {
        await using var pos = Pos();
        var http = pos.CreateClient();

        Assert.Equal("no-session", (await LaunchAsync(http, "T-01", Basket())).Entry.Blocks.Single().Code);
        await http.PostAsJsonAsync("terminals/T-01/session", new SessionRequest("OP-01"));

        var first = await LaunchAsync(http, "T-01", Basket());
        Assert.Equal(HttpStatusCode.Created, first.Status);
        Assert.Equal("S-000006", first.Entry.SaleId); // after the 5 generated sales

        var second = await LaunchAsync(http, "T-01", Basket("SKU-0002"));
        Assert.Equal(HttpStatusCode.Conflict, second.Status);
        Assert.Equal("drawer-open", second.Entry.Blocks.Single().Code);

        await http.PutAsJsonAsync("terminals/T-01/drawer", new DrawerRequest(false));
        Assert.Equal(HttpStatusCode.Created, (await LaunchAsync(http, "T-01", Basket("SKU-0002"))).Status);

        var journal = await http.GetFromJsonAsync<List<JournalEntry>>("terminals/T-01/journal", Json);
        Assert.Equal(["blocked", "posted", "blocked", "posted"], journal!.Select(e => e.Outcome));
    }

    [Fact]
    public async Task Posted_sales_reach_the_feed_and_the_summary_with_their_overrides()
    {
        await using var pos = Pos(total: 0);
        var http = pos.CreateClient();
        await http.PostAsJsonAsync("terminals/T-02/session", new SessionRequest("OP-01"));

        var keyed = Basket(entry: EntryMethod.Keyed);
        Assert.Equal("item-keyed", (await LaunchAsync(http, "T-02", keyed)).Entry.Blocks.Single().Code);

        await http.PutAsJsonAsync("terminals/T-02/keylock", new KeylockRequest(KeylockPosition.Supervisor), Json);
        var released = await LaunchAsync(http, "T-02", keyed with { SupervisorId = "SUP-01" });
        Assert.Equal("overridden", released.Entry.Outcome);

        var page = await http.GetFromJsonAsync<SalesPage>("sales?after=0", Json);
        var sale = Assert.Single(page!.Sales);
        Assert.Equal([new SaleOverride("item", "item-keyed", "SUP-01")], sale.Overrides);
        Assert.Equal(1, (await http.GetFromJsonAsync<Dictionary<string, int>>("sales/summary", Json))!["SKU-0001"]);
    }

    [Fact]
    public async Task Terminal_settings_layer_on_the_defaults()
    {
        await using var pos = Pos();
        var http = pos.CreateClient();
        foreach (var t in new[] { "T-01", "T-02" })
            await http.PostAsJsonAsync($"terminals/{t}/session", new SessionRequest("OP-01"));

        // T-01 takes keyed entry; T-02 requires the scanner but keeps the default quantity confirmation.
        Assert.Equal(HttpStatusCode.Created, (await LaunchAsync(http, "T-01", Basket(entry: EntryMethod.Keyed))).Status);
        var t2 = await LaunchAsync(http, "T-02", Basket(qty: 12, entry: EntryMethod.Keyed));
        Assert.Equal(["item-keyed", "quantity-unconfirmed"], t2.Entry.Blocks.Select(b => b.Code));
    }

    [Fact]
    public async Task A_malformed_attempt_is_rejected_before_the_guards()
    {
        await using var pos = Pos();
        var res = await pos.CreateClient().PostAsJsonAsync("terminals/T-01/sales", new SaleAttempt("OP-01", [new AttemptLine("SKU-0001", 0)]), Json);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }
}
