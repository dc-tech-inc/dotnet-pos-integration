using DcTech.PosIntegration.Data;
using Microsoft.Extensions.Options;

namespace DcTech.PosIntegration.Worker;

public sealed class WorkerOptions
{
    public string SqlConnection { get; set; } = "";
    public string PosApiUrl { get; set; } = "http://localhost:5080";
    public int PollIntervalSeconds { get; set; } = 5;

    /// <summary>Drain once, reconcile, then exit: 0 when clean, 2 when reconciliation found differences.</summary>
    public bool RunOnce { get; set; }

    /// <summary>Load opening stock for the fake catalogue when the Stock table is empty.</summary>
    public bool SeedDemo { get; set; }

    /// <summary>Create the database when it is missing. Meant for the compose demo; production databases are provisioned separately.</summary>
    public bool EnsureDatabase { get; set; }

    public int SeedQuantity { get; set; } = 100;
}

public sealed class SyncWorker(SyncService sync, Reconciler reconciler, SqlStockStore store, SqlConnectionInfo db,
    IOptions<WorkerOptions> options, IHostApplicationLifetime lifetime, ILogger<SyncWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var o = options.Value;
        try
        {
            if (o.EnsureDatabase) await SchemaMigrator.EnsureDatabaseAsync(db.ConnectionString, ct);
            await SchemaMigrator.ApplyAsync(db.ConnectionString, ct);
            if (o.SeedDemo && await store.CountStockAsync(ct) == 0)
            {
                await store.UpsertOpeningStockAsync(DemoSkus().Select(s => (s, o.SeedQuantity)), ct);
                log.LogInformation("Seeded {Count} demo SKUs with {Qty} units each", DemoSkus().Count(), o.SeedQuantity);
            }

            do
            {
                var result = await sync.DrainAsync(ct);
                var report = await reconciler.RunAsync(ct);
                log.LogInformation("Sync: {Applied} applied, {Duplicates} duplicate, {Rejected} rejected, {Shortfall} unit(s) short",
                    result.Applied, result.Duplicates, result.Rejected, result.ShortfallUnits);

                foreach (var m in report.Mismatches)
                    log.LogError("Mismatch {Sku}: POS sold {Pos}, we booked {Local}", m.Sku, m.PosUnits, m.LocalUnits);
                foreach (var d in report.Drift)
                    log.LogError("Drift {Sku}: ledger says {Expected} taken, stock says {Actual}", d.Sku, d.ExpectedReduction, d.ActualReduction);
                log.LogInformation("Reconciliation {State}", report.IsClean ? "clean" : "FAILED");

                if (o.RunOnce)
                {
                    Environment.ExitCode = report.IsClean ? 0 : 2;
                    break;
                }
                await Task.Delay(TimeSpan.FromSeconds(o.PollIntervalSeconds), ct);
            } while (!ct.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            log.LogCritical(ex, "Worker stopped");
            Environment.ExitCode = 1;
        }
        finally
        {
            lifetime.StopApplication();
        }
    }

    // Same ten SKUs the fake POS sells. Hard-coded here on purpose: the worker must not reference the fake.
    private static IEnumerable<string> DemoSkus() => Enumerable.Range(1, 10).Select(i => $"SKU-{i:D4}");
}

public sealed record SqlConnectionInfo(string ConnectionString);
