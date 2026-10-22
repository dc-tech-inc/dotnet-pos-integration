using DcTech.PosIntegration.Data;
using static DcTech.PosIntegration.Tests.Unit.TestData;

namespace DcTech.PosIntegration.Tests.Integration;

public class SqlStockStoreTests(SqlFixture db) : IClassFixture<SqlFixture>
{
    private async Task<SqlStockStore> FreshStockAsync(params (string Sku, int Qty)[] stock)
    {
        await db.ExecuteAsync("DELETE dbo.StockMovement; DELETE dbo.ProcessedSale; DELETE dbo.Stock; UPDATE dbo.SyncCursor SET LastSeq = 0");
        var store = db.Store;
        await store.UpsertOpeningStockAsync(stock, default);
        return store;
    }

    [SqlFact]
    public async Task Schema_can_be_applied_twice()
    {
        await SchemaMigrator.ApplyAsync(db.ConnectionString, default);
        Assert.Equal(1, await db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.SyncCursor"));
    }

    [SqlFact]
    public async Task A_sale_reduces_stock_and_records_its_movements()
    {
        var store = await FreshStockAsync(("A", 10), ("B", 10));

        var result = await store.ApplySaleAsync(Sale(1, ("B", 2), ("A", 3), ("A", 1)), default);

        Assert.Equal(new ApplyResult(ApplyOutcome.Applied, 0), result);
        Assert.Equal(6, await db.ScalarAsync<int>("SELECT OnHand FROM dbo.Stock WHERE Sku = 'A'"));
        Assert.Equal(8, await db.ScalarAsync<int>("SELECT OnHand FROM dbo.Stock WHERE Sku = 'B'"));
        Assert.Equal(2, await db.ScalarAsync<int>("SELECT COUNT(*) FROM dbo.StockMovement"));
    }

    [SqlFact]
    public async Task The_same_sale_twice_is_booked_once()
    {
        var store = await FreshStockAsync(("A", 10));
        var sale = Sale(1, ("A", 4));

        var first = await store.ApplySaleAsync(sale, default);
        var second = await store.ApplySaleAsync(sale, default);

        Assert.Equal(ApplyOutcome.Applied, first.Outcome);
        Assert.Equal(ApplyOutcome.Duplicate, second.Outcome);
        Assert.Equal(6, await db.ScalarAsync<int>("SELECT OnHand FROM dbo.Stock WHERE Sku = 'A'"));
    }

    [SqlFact]
    public async Task Ten_concurrent_deliveries_of_one_sale_book_it_exactly_once()
    {
        var store = await FreshStockAsync(("A", 10));
        var sale = Sale(1, ("A", 3));

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => store.ApplySaleAsync(sale, default))));

        Assert.Equal(1, results.Count(r => r.Outcome == ApplyOutcome.Applied));
        Assert.Equal(9, results.Count(r => r.Outcome == ApplyOutcome.Duplicate));
        Assert.Equal(7, await db.ScalarAsync<int>("SELECT OnHand FROM dbo.Stock WHERE Sku = 'A'"));
    }

    [SqlFact]
    public async Task Concurrent_sales_never_oversell_and_the_ledger_balances()
    {
        var store = await FreshStockAsync(("A", 5), ("B", 5));

        // 20 sales of 1 A and 1 B each, in both line orders: only 5 of each can be covered.
        var results = await Task.WhenAll(Enumerable.Range(1, 20).Select(i => Task.Run(() =>
            store.ApplySaleAsync(i % 2 == 0 ? Sale(i, ("A", 1), ("B", 1)) : Sale(i, ("B", 1), ("A", 1)), default))));

        Assert.Equal(20, results.Count(r => r.Outcome == ApplyOutcome.Applied));
        Assert.Equal(30, results.Sum(r => r.ShortfallUnits)); // 40 units asked, 10 covered
        Assert.Equal(0, await db.ScalarAsync<int>("SELECT OnHand FROM dbo.Stock WHERE Sku = 'A'"));
        Assert.Empty(await store.FindStockDriftAsync(default));
    }

    [SqlFact]
    public async Task Unknown_sku_is_recorded_as_pure_shortfall()
    {
        var store = await FreshStockAsync(("A", 5));

        var result = await store.ApplySaleAsync(Sale(1, ("A", 1), ("NOPE", 2)), default);

        Assert.Equal(2, result.ShortfallUnits);
        Assert.Equal(2, await db.ScalarAsync<int>("SELECT Shortfall FROM dbo.StockMovement WHERE Sku = 'NOPE'"));
    }

    [SqlFact]
    public async Task Stock_cannot_be_driven_negative_even_by_hand()
    {
        await FreshStockAsync(("A", 1));
        await Assert.ThrowsAnyAsync<Exception>(() => db.ExecuteAsync("UPDATE dbo.Stock SET OnHand = -1 WHERE Sku = 'A'"));
    }

    [SqlFact]
    public async Task Drift_is_detected_when_stock_is_changed_behind_the_ledger()
    {
        var store = await FreshStockAsync(("A", 10));
        await store.ApplySaleAsync(Sale(1, ("A", 2)), default);
        Assert.Empty(await store.FindStockDriftAsync(default));

        await db.ExecuteAsync("UPDATE dbo.Stock SET OnHand = OnHand - 1 WHERE Sku = 'A'");

        Assert.Equal([new StockDrift("A", 2, 3)], await store.FindStockDriftAsync(default));
    }

    [SqlFact]
    public async Task Sold_totals_and_cursor_follow_the_sequence()
    {
        var store = await FreshStockAsync(("A", 10));
        await store.ApplySaleAsync(Sale(1, ("A", 1)), default);
        await store.ApplySaleAsync(Sale(2, ("A", 2)), default);

        Assert.Equal(2, await store.GetMaxProcessedSeqAsync(default));
        Assert.Equal(1, (await store.GetSoldTotalsAsync(1, default))["A"]);
        Assert.Equal(3, (await store.GetSoldTotalsAsync(2, default))["A"]);

        await store.SetCursorAsync(5, default);
        await store.SetCursorAsync(3, default); // must not move backwards
        Assert.Equal(5, await store.GetCursorAsync(default));
    }

    [SqlFact]
    public async Task Re_importing_opening_stock_keeps_sales_already_booked()
    {
        var store = await FreshStockAsync(("A", 10));
        await store.ApplySaleAsync(Sale(1, ("A", 4)), default);

        await store.UpsertOpeningStockAsync([("A", 20)], default);

        Assert.Equal(16, await db.ScalarAsync<int>("SELECT OnHand FROM dbo.Stock WHERE Sku = 'A'"));
        Assert.Empty(await store.FindStockDriftAsync(default));
    }
}
