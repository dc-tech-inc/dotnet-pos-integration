using DcTech.PosIntegration.Data;
using DcTech.PosIntegration.FakePos;
using static DcTech.PosIntegration.Tests.Unit.TestData;

namespace DcTech.PosIntegration.Tests.Unit;

public class RetryPolicyTests
{
    [Fact]
    public async Task Retries_transient_failures_with_doubling_delays_then_succeeds()
    {
        var delays = new List<TimeSpan>();
        var policy = new RetryPolicy(5, TimeSpan.FromMilliseconds(100), _ => true,
            (d, _) => { delays.Add(d); return Task.CompletedTask; }, new Random(1));
        var calls = 0;

        var result = await policy.ExecuteAsync(_ => ++calls < 4 ? throw new TimeoutException() : Task.FromResult(calls), default);

        Assert.Equal(4, result);
        Assert.Equal(3, delays.Count);
        for (var i = 0; i < 3; i++)
        {
            var baseMs = 100 * (1 << i);
            Assert.InRange(delays[i].TotalMilliseconds, baseMs, baseMs * 1.2);
        }
    }

    [Fact]
    public async Task Gives_up_after_max_attempts_and_surfaces_the_last_error()
    {
        var policy = new RetryPolicy(3, TimeSpan.Zero, _ => true, (_, _) => Task.CompletedTask);
        var calls = 0;
        await Assert.ThrowsAsync<TimeoutException>(() => policy.ExecuteAsync<int>(_ => { calls++; throw new TimeoutException(); }, default));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Does_not_retry_a_permanent_error()
    {
        var policy = new RetryPolicy(5, TimeSpan.Zero, ex => ex is TimeoutException, (_, _) => Task.CompletedTask);
        var calls = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => policy.ExecuteAsync<int>(_ => { calls++; throw new InvalidOperationException(); }, default));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(503, true)]
    [InlineData(500, true)]
    [InlineData(429, true)]
    [InlineData(404, false)]
    [InlineData(400, false)]
    public void Classifies_http_status_codes(int status, bool transient) =>
        Assert.Equal(transient, Worker.Transient.IsTransient(new HttpRequestException("x", null, (System.Net.HttpStatusCode)status)));
}

public class SaleValidatorTests
{
    [Fact]
    public void Accepts_a_normal_sale() => Assert.Null(SaleValidator.Validate(Sale(1, ("SKU-0001", 2))));

    [Fact]
    public void Rejects_an_empty_sale() => Assert.NotNull(SaleValidator.Validate(Sale(1)));

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Rejects_non_positive_quantities(int qty) => Assert.NotNull(SaleValidator.Validate(Sale(1, ("SKU-0001", qty))));

    [Fact]
    public void Normalize_merges_repeated_skus_and_sorts_them()
    {
        var lines = SaleValidator.Normalize([new("B", 1), new("A", 2), new("B", 4)]);
        Assert.Equal([new SaleLine("A", 2), new SaleLine("B", 5)], lines);
    }
}

public class ReconcilerTests
{
    [Fact]
    public void Compare_reports_only_differences_and_treats_a_missing_side_as_zero()
    {
        var pos = new Dictionary<string, int> { ["A"] = 5, ["B"] = 3, ["C"] = 1 };
        var local = new Dictionary<string, int> { ["A"] = 5, ["B"] = 2, ["D"] = 4 };

        var diff = Reconciler.Compare(pos, local);

        Assert.Equal([new SkuMismatch("B", 3, 2), new SkuMismatch("C", 1, 0), new SkuMismatch("D", 0, 4)], diff);
    }

    [Fact]
    public async Task A_rejected_sale_shows_up_as_a_mismatch()
    {
        var sales = new[] { Sale(1, ("A", 2)), Sale(2, ("A", 0), ("B", 3)), Sale(3, ("B", 1)) };
        var store = new InMemoryStore(("A", 10), ("B", 10));
        var source = new ScriptedSource(sales);

        var result = await Sync(source, store).DrainAsync(default);
        var report = await new Reconciler(source, store, NoWaitRetry()).RunAsync(default);

        Assert.Equal(1, result.Rejected);
        Assert.False(report.IsClean);
        Assert.Contains(new SkuMismatch("B", 4, 1), report.Mismatches);
    }
}

public class SyncServiceTests
{
    [Fact]
    public async Task Books_every_sale_once_and_leaves_a_clean_reconciliation()
    {
        var sales = Enumerable.Range(1, 120).Select(i => Sale(i, ("A", 1), ("B", 2))).ToList();
        var store = new InMemoryStore(("A", 1000), ("B", 1000));
        var source = new ScriptedSource(sales);

        var result = await Sync(source, store, pageSize: 25).DrainAsync(default);

        Assert.Equal(120, result.Applied);
        Assert.Equal(880, store.OnHand("A"));
        Assert.Equal(760, store.OnHand("B"));
        Assert.True((await new Reconciler(source, store, NoWaitRetry()).RunAsync(default)).IsClean);
    }

    [Fact]
    public async Task Redelivered_sales_are_ignored()
    {
        var sales = Enumerable.Range(1, 30).Select(i => Sale(i, ("A", 1))).ToList();
        var store = new InMemoryStore(("A", 100));

        var result = await Sync(new ScriptedSource(sales, redeliver: 3), store, pageSize: 10).DrainAsync(default);

        Assert.Equal(30, result.Applied);
        Assert.True(result.Duplicates > 0);
        Assert.Equal(70, store.OnHand("A"));
    }

    [Fact]
    public async Task Survives_a_pos_that_fails_the_first_calls()
    {
        var sales = new[] { Sale(1, ("A", 1)), Sale(2, ("A", 1)) };
        var store = new InMemoryStore(("A", 5));

        var result = await Sync(new ScriptedSource(sales, failFirstCalls: 2), store).DrainAsync(default);

        Assert.Equal(2, result.Applied);
        Assert.Equal(3, store.OnHand("A"));
    }

    [Fact]
    public async Task Oversold_and_unknown_skus_become_shortfall_and_stock_stays_non_negative()
    {
        var sales = new[] { Sale(1, ("A", 3)), Sale(2, ("A", 4), ("ZZZ", 2)) };
        var store = new InMemoryStore(("A", 5));

        var result = await Sync(new ScriptedSource(sales), store).DrainAsync(default);

        Assert.Equal(0, store.OnHand("A"));
        Assert.Equal(2 + 2, result.ShortfallUnits); // 2 units of A short on sale 2, plus 2 unknown ZZZ
    }

    [Fact]
    public async Task Draining_twice_changes_nothing_the_second_time()
    {
        var sales = Enumerable.Range(1, 10).Select(i => Sale(i, ("A", 1))).ToList();
        var store = new InMemoryStore(("A", 100));
        var service = Sync(new ScriptedSource(sales), store);

        await service.DrainAsync(default);
        var calls = store.ApplyCalls;
        var again = await service.DrainAsync(default);

        Assert.Equal(new SyncResult(0, 0, 0, 0), again);
        Assert.Equal(calls, store.ApplyCalls);
    }
}

public class PosGeneratorTests
{
    [Fact]
    public void Same_seed_and_sequence_give_the_same_sale()
    {
        var a = new PosGenerator(new FakePosOptions { Seed = 7 }).Create(12);
        var b = new PosGenerator(new FakePosOptions { Seed = 7 }).Create(12);
        Assert.Equal(a.Lines, b.Lines);
        Assert.Equal("S-000012", a.SaleId);
    }

    [Fact]
    public void Pages_respect_the_total_and_can_repeat_earlier_sales()
    {
        var pos = new PosGenerator(new FakePosOptions { Total = 10, Redeliver = 2 });
        Assert.Equal([4L, 5, 6, 7], pos.Page(5, 2).Select(s => s.Seq));
        Assert.Equal([9L, 10], pos.Page(10, 5).Select(s => s.Seq)); // nothing new, only the redelivered tail
        Assert.Empty(new PosGenerator(new FakePosOptions { Total = 10 }).Page(10, 5));
    }

    [Fact]
    public void Summary_matches_the_sum_of_the_sales()
    {
        var pos = new PosGenerator(new FakePosOptions { Total = 60 });
        var expected = Enumerable.Range(1, 60).SelectMany(i => pos.Create(i).Lines).Sum(l => l.Quantity);
        Assert.Equal(expected, pos.Summary(60).Values.Sum());
        Assert.True(pos.Summary(60).ContainsKey("SKU-9999"));
    }
}

public class SchemaMigratorTests
{
    [Fact]
    public void Splits_on_GO_lines_only()
    {
        var batches = SchemaMigrator.SplitBatches("SELECT 1\nGO\nSELECT 'GO' AS x\n  go  \nSELECT 3");
        Assert.Equal(["SELECT 1", "SELECT 'GO' AS x", "SELECT 3"], batches);
    }
}
