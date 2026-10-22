namespace DcTech.PosIntegration;

public sealed class Reconciler(ISaleSource source, IStockStore store, RetryPolicy retry)
{
    /// <summary>Pure comparison: every SKU where the POS total and our total differ (a missing side counts as zero).</summary>
    public static IReadOnlyList<SkuMismatch> Compare(IReadOnlyDictionary<string, int> pos, IReadOnlyDictionary<string, int> local) =>
        pos.Keys.Union(local.Keys, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(sku => new SkuMismatch(sku, pos.GetValueOrDefault(sku), local.GetValueOrDefault(sku)))
            .Where(m => m.PosUnits != m.LocalUnits)
            .ToList();

    /// <summary>Compares POS totals with booked totals up to the highest booked sequence, and checks that on-hand still matches the movement ledger.</summary>
    public async Task<ReconciliationReport> RunAsync(CancellationToken ct)
    {
        var upTo = await retry.ExecuteAsync(store.GetMaxProcessedSeqAsync, ct);
        var pos = await retry.ExecuteAsync(t => source.GetSummaryAsync(upTo, t), ct);
        var local = await retry.ExecuteAsync(t => store.GetSoldTotalsAsync(upTo, t), ct);
        var drift = await retry.ExecuteAsync(store.FindStockDriftAsync, ct);
        return new ReconciliationReport(Compare(pos, local), drift);
    }
}
