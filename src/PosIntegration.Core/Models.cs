namespace DcTech.PosIntegration;

/// <summary>One line of a sale: a SKU and how many units left the shelf.</summary>
public sealed record SaleLine(string Sku, int Quantity);

/// <summary>A sale reported by the (fictional) point of sale. <see cref="Seq"/> is the POS's own monotonic sequence.</summary>
public sealed record Sale(string SaleId, long Seq, string StoreId, DateTimeOffset OccurredAt, IReadOnlyList<SaleLine> Lines);

public sealed record SalesPage(IReadOnlyList<Sale> Sales);

public enum ApplyOutcome { Applied, Duplicate }

/// <param name="Outcome">Whether the sale was booked now or had already been booked.</param>
/// <param name="ShortfallUnits">Units sold that the stock could not cover (oversold or unknown SKU). Stock never goes negative.</param>
public sealed record ApplyResult(ApplyOutcome Outcome, int ShortfallUnits);

public sealed record SkuMismatch(string Sku, int PosUnits, int LocalUnits);

/// <summary>A SKU whose on-hand does not equal opening stock minus applied movements.</summary>
public sealed record StockDrift(string Sku, int ExpectedReduction, int ActualReduction);

public sealed record SyncResult(int Applied, int Duplicates, int Rejected, int ShortfallUnits)
{
    public static SyncResult operator +(SyncResult a, SyncResult b) =>
        new(a.Applied + b.Applied, a.Duplicates + b.Duplicates, a.Rejected + b.Rejected, a.ShortfallUnits + b.ShortfallUnits);
}

public sealed record ReconciliationReport(IReadOnlyList<SkuMismatch> Mismatches, IReadOnlyList<StockDrift> Drift)
{
    public bool IsClean => Mismatches.Count == 0 && Drift.Count == 0;
}
