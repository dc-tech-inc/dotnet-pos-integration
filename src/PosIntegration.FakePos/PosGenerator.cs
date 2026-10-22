namespace DcTech.PosIntegration.FakePos;

public sealed class FakePosOptions
{
    /// <summary>How many sales the fictional store ever produces.</summary>
    public int Total { get; set; } = 200;

    /// <summary>Every Nth request answers 503 (0 = never). Exercises the worker's retry.</summary>
    public int FailureEvery { get; set; }

    /// <summary>Each page also repeats this many already-delivered sales (at-least-once delivery). Exercises idempotency.</summary>
    public int Redeliver { get; set; }

    public int Seed { get; set; } = 42;
}

/// <summary>
/// Deterministic sales for a fictional store: the same seed and sequence always give the same sale.
/// Sales posted through a simulated terminal are appended after the generated ones (Seq = Total + 1, + 2, ...).
/// </summary>
public sealed class PosGenerator(FakePosOptions options)
{
    private readonly List<Sale> _launched = [];

    public const int CatalogSize = 10;

    /// <summary>SKU-0001 .. SKU-0010. Opening stock for these is what the demo seed loads.</summary>
    public static IReadOnlyList<string> Catalog { get; } =
        Enumerable.Range(1, CatalogSize).Select(i => $"SKU-{i:D4}").ToList();

    private static readonly DateTimeOffset Epoch = new(2026, 1, 5, 8, 0, 0, TimeSpan.Zero);

    public Sale Create(long seq)
    {
        var rng = new Random(unchecked(options.Seed * 1_000_003 + (int)seq));
        var lines = new List<SaleLine>();
        for (var i = rng.Next(1, 4); i > 0; i--)
            lines.Add(new SaleLine(Catalog[rng.Next(CatalogSize)], rng.Next(1, 5)));
        // Every 50th sale carries an item that is not in the stock catalogue (a price-lookup item, say).
        if (seq % 50 == 0) lines.Add(new SaleLine("SKU-9999", 1));
        return new Sale($"S-{seq:D6}", seq, "STORE-01", Epoch.AddMinutes(seq), lines);
    }

    /// <summary>Adds a sale that a terminal let through and gives it the next sequence.</summary>
    public Sale Append(IReadOnlyList<SaleLine> lines, DateTimeOffset at, IReadOnlyList<SaleOverride> overrides)
    {
        lock (_launched)
        {
            var seq = options.Total + _launched.Count + 1L;
            var sale = new Sale($"S-{seq:D6}", seq, "STORE-01", at, lines, overrides.Count == 0 ? null : overrides);
            _launched.Add(sale);
            return sale;
        }
    }

    private long LastSeq
    {
        get { lock (_launched) return options.Total + _launched.Count; }
    }

    private Sale Get(long seq)
    {
        if (seq <= options.Total) return Create(seq);
        lock (_launched) return _launched[(int)(seq - options.Total - 1)];
    }

    public IReadOnlyList<Sale> Page(long afterSeq, int limit)
    {
        var first = Math.Max(1, afterSeq + 1 - options.Redeliver);
        var last = Math.Min(LastSeq, afterSeq + limit);
        return first > last ? [] : Enumerable.Range((int)first, (int)(last - first + 1)).Select(s => Get(s)).ToList();
    }

    public IReadOnlyDictionary<string, int> Summary(long upToSeq) =>
        Enumerable.Range(1, (int)Math.Min(upToSeq, LastSeq))
            .SelectMany(s => Get(s).Lines)
            .GroupBy(l => l.Sku)
            .ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
}
