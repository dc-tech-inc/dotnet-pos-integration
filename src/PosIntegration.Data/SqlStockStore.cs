using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace DcTech.PosIntegration.Data;

public sealed class SqlStockStore(string connectionString) : IStockStore
{
    // Primary key / unique constraint violations.
    private const int DuplicateKey = 2627;
    private const int DuplicateIndex = 2601;

    public async Task<ApplyResult> ApplySaleAsync(Sale sale, CancellationToken ct)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);

        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT dbo.ProcessedSale (SaleId, Seq, StoreId, OccurredAt) VALUES (@SaleId, @Seq, @StoreId, @OccurredAt)",
                new { sale.SaleId, sale.Seq, sale.StoreId, sale.OccurredAt }, tx, cancellationToken: ct));
        }
        catch (SqlException ex) when (ex.Number is DuplicateKey or DuplicateIndex)
        {
            await tx.RollbackAsync(ct);
            return new ApplyResult(ApplyOutcome.Duplicate, 0);
        }

        var shortfall = 0;
        // Sorted by SKU so two concurrent sales always take row locks in the same order.
        foreach (var line in SaleValidator.Normalize(sale.Lines))
        {
            // UPDLOCK holds the stock row until commit, so the read-then-update below cannot interleave with another sale.
            var onHand = await conn.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
                "SELECT OnHand FROM dbo.Stock WITH (UPDLOCK, ROWLOCK) WHERE Sku = @Sku",
                new { line.Sku }, tx, cancellationToken: ct));

            var applied = Math.Min(onHand ?? 0, line.Quantity);
            if (applied > 0)
                await conn.ExecuteAsync(new CommandDefinition(
                    "UPDATE dbo.Stock SET OnHand = OnHand - @Applied WHERE Sku = @Sku",
                    new { line.Sku, Applied = applied }, tx, cancellationToken: ct));

            await conn.ExecuteAsync(new CommandDefinition(
                "INSERT dbo.StockMovement (SaleId, Sku, Quantity, Applied) VALUES (@SaleId, @Sku, @Quantity, @Applied)",
                new { sale.SaleId, line.Sku, line.Quantity, Applied = applied }, tx, cancellationToken: ct));
            shortfall += line.Quantity - applied;
        }

        await tx.CommitAsync(ct);
        return new ApplyResult(ApplyOutcome.Applied, shortfall);
    }

    public Task<long> GetCursorAsync(CancellationToken ct) =>
        ScalarAsync<long>("SELECT LastSeq FROM dbo.SyncCursor WHERE Id = 1", ct);

    public async Task SetCursorAsync(long seq, CancellationToken ct)
    {
        await using var conn = new SqlConnection(connectionString);
        // Never moves backwards, even if two workers race.
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE dbo.SyncCursor SET LastSeq = @seq WHERE Id = 1 AND LastSeq < @seq", new { seq }, cancellationToken: ct));
    }

    public Task<long> GetMaxProcessedSeqAsync(CancellationToken ct) =>
        ScalarAsync<long>("SELECT COALESCE(MAX(Seq), 0) FROM dbo.ProcessedSale", ct);

    public async Task<IReadOnlyDictionary<string, int>> GetSoldTotalsAsync(long upToSeq, CancellationToken ct)
    {
        await using var conn = new SqlConnection(connectionString);
        var rows = await conn.QueryAsync<(string Sku, int Units)>(new CommandDefinition(
            """
            SELECT m.Sku, CAST(SUM(m.Quantity) AS int) AS Units
            FROM dbo.StockMovement m JOIN dbo.ProcessedSale s ON s.SaleId = m.SaleId
            WHERE s.Seq <= @upToSeq GROUP BY m.Sku
            """, new { upToSeq }, cancellationToken: ct));
        return rows.ToDictionary(r => r.Sku, r => r.Units, StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<StockDrift>> FindStockDriftAsync(CancellationToken ct)
    {
        await using var conn = new SqlConnection(connectionString);
        var rows = await conn.QueryAsync<StockDrift>(new CommandDefinition(
            """
            SELECT s.Sku, CAST(COALESCE(SUM(m.Applied), 0) AS int) AS ExpectedReduction, s.Opening - s.OnHand AS ActualReduction
            FROM dbo.Stock s LEFT JOIN dbo.StockMovement m ON m.Sku = s.Sku
            GROUP BY s.Sku, s.Opening, s.OnHand
            HAVING s.Opening - s.OnHand <> COALESCE(SUM(m.Applied), 0)
            ORDER BY s.Sku
            """, cancellationToken: ct));
        return rows.ToList();
    }

    /// <summary>Loads opening stock; an existing SKU keeps its movements, so OnHand is moved by the same delta as Opening.</summary>
    public async Task UpsertOpeningStockAsync(IEnumerable<(string Sku, int Quantity)> items, CancellationToken ct)
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        foreach (var (sku, quantity) in items)
            await conn.ExecuteAsync(new CommandDefinition(
                """
                MERGE dbo.Stock WITH (HOLDLOCK) AS t USING (VALUES (@Sku, @Quantity)) AS s (Sku, Quantity) ON t.Sku = s.Sku
                WHEN MATCHED THEN UPDATE SET t.OnHand = t.OnHand + (s.Quantity - t.Opening), t.Opening = s.Quantity
                WHEN NOT MATCHED THEN INSERT (Sku, Opening, OnHand) VALUES (s.Sku, s.Quantity, s.Quantity);
                """, new { Sku = sku, Quantity = quantity }, (SqlTransaction)tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public async Task<int> CountStockAsync(CancellationToken ct) =>
        await ScalarAsync<int>("SELECT COUNT(*) FROM dbo.Stock", ct);

    private async Task<T> ScalarAsync<T>(string sql, CancellationToken ct)
    {
        await using var conn = new SqlConnection(connectionString);
        return await conn.ExecuteScalarAsync<T>(new CommandDefinition(sql, cancellationToken: ct)) ?? throw new InvalidOperationException("no value");
    }
}
