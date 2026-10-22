using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace DcTech.PosIntegration.Data;

public static partial class SchemaMigrator
{
    /// <summary>Applies the embedded schema.sql. Safe to run on every start: every statement checks before it creates.</summary>
    public static async Task ApplyAsync(string connectionString, CancellationToken ct)
    {
        await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("schema.sql")
            ?? throw new InvalidOperationException("schema.sql is not embedded");
        using var reader = new StreamReader(stream);
        var script = await reader.ReadToEndAsync(ct);

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        foreach (var batch in SplitBatches(script))
        {
            await using var cmd = new SqlCommand(batch, conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>Creates the database named in the connection string if it does not exist (demo and CI convenience; needs CREATE DATABASE rights).</summary>
    public static async Task EnsureDatabaseAsync(string connectionString, CancellationToken ct)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var name = builder.InitialCatalog;
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("The connection string has no Database/Initial Catalog.");
        builder.InitialCatalog = "master";

        await using var conn = new SqlConnection(builder.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            IF DB_ID(@name) IS NULL
            BEGIN
                DECLARE @sql nvarchar(max) = N'CREATE DATABASE ' + QUOTENAME(@name);
                EXEC (@sql);
            END
            """, conn);
        cmd.Parameters.AddWithValue("@name", name);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>GO is a client-tool separator, not T-SQL, so it has to be split here.</summary>
    public static IEnumerable<string> SplitBatches(string script) =>
        GoLine().Split(script).Select(b => b.Trim()).Where(b => b.Length > 0);

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoLine();
}
