namespace DcTech.PosIntegration;

/// <summary>The POS side. Sales are read in sequence order, after an exclusive cursor.</summary>
public interface ISaleSource
{
    Task<SalesPage> GetSalesAsync(long afterSeq, int limit, CancellationToken ct);

    /// <summary>Units sold per SKU for every sale with Seq &lt;= upToSeq, according to the POS.</summary>
    Task<IReadOnlyDictionary<string, int>> GetSummaryAsync(long upToSeq, CancellationToken ct);
}

/// <summary>The inventory side (SQL Server in production, a fake in unit tests).</summary>
public interface IStockStore
{
    /// <summary>Books one sale atomically and idempotently: a sale id that was already booked returns <see cref="ApplyOutcome.Duplicate"/> and changes nothing.</summary>
    Task<ApplyResult> ApplySaleAsync(Sale sale, CancellationToken ct);

    Task<long> GetCursorAsync(CancellationToken ct);

    Task SetCursorAsync(long seq, CancellationToken ct);

    Task<long> GetMaxProcessedSeqAsync(CancellationToken ct);

    /// <summary>Units sold per SKU over booked sales with Seq &lt;= upToSeq (what we recorded, including shortfall).</summary>
    Task<IReadOnlyDictionary<string, int>> GetSoldTotalsAsync(long upToSeq, CancellationToken ct);

    Task<IReadOnlyList<StockDrift>> FindStockDriftAsync(CancellationToken ct);
}
