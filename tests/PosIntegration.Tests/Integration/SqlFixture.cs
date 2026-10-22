using DcTech.PosIntegration.Data;
using Microsoft.Data.SqlClient;

namespace DcTech.PosIntegration.Tests.Integration;

/// <summary>
/// Integration tests need a real SQL Server: set POS_TEST_SQL to a connection string that may create databases
/// (CI uses the mcr.microsoft.com/mssql/server service container). Without it they are reported as skipped, not passed.
/// </summary>
public sealed class SqlFactAttribute : FactAttribute
{
    public SqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(SqlFixture.AdminConnection))
            Skip = "POS_TEST_SQL is not set (no SQL Server available)";
    }
}

/// <summary>One throwaway database per test class, created from the real schema.sql and dropped afterwards.</summary>
public sealed class SqlFixture : IAsyncLifetime
{
    public static string? AdminConnection { get; } = Environment.GetEnvironmentVariable("POS_TEST_SQL");

    private readonly string _name = "pos_test_" + Guid.NewGuid().ToString("N")[..12];

    public string ConnectionString { get; private set; } = "";

    public SqlStockStore Store => new(ConnectionString);

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(AdminConnection)) return;
        await ExecuteAdminAsync($"CREATE DATABASE [{_name}]");
        ConnectionString = new SqlConnectionStringBuilder(AdminConnection) { InitialCatalog = _name }.ConnectionString;
        await SchemaMigrator.ApplyAsync(ConnectionString, default);
    }

    public async Task DisposeAsync()
    {
        if (string.IsNullOrWhiteSpace(AdminConnection)) return;
        SqlConnection.ClearAllPools();
        await ExecuteAdminAsync($"ALTER DATABASE [{_name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_name}]");
    }

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        return (T)Convert.ChangeType((await cmd.ExecuteScalarAsync())!, typeof(T));
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task ExecuteAdminAsync(string sql)
    {
        await using var conn = new SqlConnection(AdminConnection);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
