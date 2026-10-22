using Microsoft.Extensions.Logging.Abstractions;

namespace DcTech.PosIntegration.Tests.Unit;

/// <summary>In-memory stock store with the same contract as the SQL one: idempotent per sale id, stock never negative.</summary>
public sealed class InMemoryStore : IStockStore
{
    private readonly Dictionary<string, int> _onHand = [];
    private readonly Dictionary<string, Sale> _sales = [];
    private readonly Dictionary<string, int> _opening = [];
    private long _cursor;

    public int ApplyCalls { get; private set; }

    public InMemoryStore(params (string Sku, int Qty)[] stock)
    {
        foreach (var (sku, qty) in stock) { _onHand[sku] = qty; _opening[sku] = qty; }
    }

    public int OnHand(string sku) => _onHand.GetValueOrDefault(sku);

    public Task<ApplyResult> ApplySaleAsync(Sale sale, CancellationToken ct)
    {
        ApplyCalls++;
        if (!_sales.TryAdd(sale.SaleId, sale)) return Task.FromResult(new ApplyResult(ApplyOutcome.Duplicate, 0));
        var shortfall = 0;
        foreach (var line in SaleValidator.Normalize(sale.Lines))
        {
            var applied = Math.Min(_onHand.GetValueOrDefault(line.Sku), line.Quantity);
            if (applied > 0) _onHand[line.Sku] -= applied;
            shortfall += line.Quantity - applied;
        }
        return Task.FromResult(new ApplyResult(ApplyOutcome.Applied, shortfall));
    }

    public Task<long> GetCursorAsync(CancellationToken ct) => Task.FromResult(_cursor);
    public Task SetCursorAsync(long seq, CancellationToken ct) { _cursor = Math.Max(_cursor, seq); return Task.CompletedTask; }
    public Task<long> GetMaxProcessedSeqAsync(CancellationToken ct) =>
        Task.FromResult(_sales.Count == 0 ? 0 : _sales.Values.Max(s => s.Seq));

    public Task<IReadOnlyDictionary<string, int>> GetSoldTotalsAsync(long upToSeq, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, int>>(_sales.Values.Where(s => s.Seq <= upToSeq)
            .SelectMany(s => s.Lines).GroupBy(l => l.Sku).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity)));

    public Task<IReadOnlyList<StockDrift>> FindStockDriftAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<StockDrift>>([]);

    /// <summary>Test hook: change on-hand behind the ledger's back.</summary>
    public void Tamper(string sku, int onHand) => _onHand[sku] = onHand;

    public int OpeningOf(string sku) => _opening.GetValueOrDefault(sku);
}

/// <summary>A source that serves scripted sales and optionally fails the first N calls.</summary>
public sealed class ScriptedSource(IReadOnlyList<Sale> sales, int failFirstCalls = 0, int redeliver = 0) : ISaleSource
{
    private int _calls;

    public int Calls => _calls;

    public Task<SalesPage> GetSalesAsync(long afterSeq, int limit, CancellationToken ct)
    {
        if (++_calls <= failFirstCalls) throw new HttpRequestException("pos unavailable", null, System.Net.HttpStatusCode.ServiceUnavailable);
        var start = Math.Max(0, afterSeq - redeliver);
        return Task.FromResult(new SalesPage(sales.Where(s => s.Seq > start).Take(limit + redeliver).ToList()));
    }

    public Task<IReadOnlyDictionary<string, int>> GetSummaryAsync(long upToSeq, CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, int>>(sales.Where(s => s.Seq <= upToSeq)
            .SelectMany(s => s.Lines).GroupBy(l => l.Sku).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity)));
}

public static class TestData
{
    public static Sale Sale(long seq, params (string Sku, int Qty)[] lines) =>
        new($"S-{seq:D6}", seq, "STORE-01", DateTimeOffset.Parse("2026-01-05T08:00:00Z"),
            lines.Select(l => new SaleLine(l.Sku, l.Qty)).ToList());

    public static RetryPolicy NoWaitRetry(int attempts = 4) =>
        new(attempts, TimeSpan.FromMilliseconds(1), Worker.Transient.IsTransient, (_, _) => Task.CompletedTask);

    public static SyncService Sync(ISaleSource source, IStockStore store, int pageSize = 50) =>
        new(source, store, NoWaitRetry(), NullLogger<SyncService>.Instance, pageSize);
}
