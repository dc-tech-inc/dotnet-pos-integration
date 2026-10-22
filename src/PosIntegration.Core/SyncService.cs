using Microsoft.Extensions.Logging;

namespace DcTech.PosIntegration;

/// <summary>
/// Pulls pages of sales after the stored cursor and books them. Redelivered sales are harmless (the store is idempotent),
/// so the cursor only has to be good enough to avoid re-reading everything: it moves after each page.
/// </summary>
public sealed class SyncService(ISaleSource source, IStockStore store, RetryPolicy retry, ILogger<SyncService> log, int pageSize = 50)
{
    public async Task<SyncResult> DrainAsync(CancellationToken ct)
    {
        var total = new SyncResult(0, 0, 0, 0);
        while (true)
        {
            var cursor = await retry.ExecuteAsync(store.GetCursorAsync, ct);
            var page = await retry.ExecuteAsync(t => source.GetSalesAsync(cursor, pageSize, t), ct);
            if (page.Sales.Count == 0) return total;

            var result = new SyncResult(0, 0, 0, 0);
            foreach (var sale in page.Sales.OrderBy(s => s.Seq))
                result += await BookAsync(sale, ct);

            // A redelivered page can sit behind the cursor; never move it backwards.
            var next = Math.Max(cursor, page.Sales.Max(s => s.Seq));
            if (next == cursor) return total + result;
            await retry.ExecuteAsync(t => store.SetCursorAsync(next, t), ct);
            total += result;
        }
    }

    private async Task<SyncResult> BookAsync(Sale sale, CancellationToken ct)
    {
        if (SaleValidator.Validate(sale) is { } reason)
        {
            // Not booked, so reconciliation will show the gap against the POS totals.
            log.LogWarning("Rejected sale {SaleId}: {Reason}", sale.SaleId, reason);
            return new SyncResult(0, 0, 1, 0);
        }

        var applied = await retry.ExecuteAsync(t => store.ApplySaleAsync(sale, t), ct);
        if (applied.ShortfallUnits > 0)
            log.LogWarning("Sale {SaleId}: {Units} unit(s) not covered by stock", sale.SaleId, applied.ShortfallUnits);
        return applied.Outcome == ApplyOutcome.Duplicate
            ? new SyncResult(0, 1, 0, 0)
            : new SyncResult(1, 0, 0, applied.ShortfallUnits);
    }
}
