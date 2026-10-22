using System.Net.Http.Json;

namespace DcTech.PosIntegration.Worker;

/// <summary>Reads sales from the POS over HTTP. Non-2xx answers throw <see cref="HttpRequestException"/> with the status code, which the retry policy classifies.</summary>
public sealed class HttpSaleSource(HttpClient http) : ISaleSource
{
    public async Task<SalesPage> GetSalesAsync(long afterSeq, int limit, CancellationToken ct) =>
        await http.GetFromJsonAsync<SalesPage>($"sales?after={afterSeq}&limit={limit}", ct)
            ?? throw new InvalidOperationException("empty response from POS");

    public async Task<IReadOnlyDictionary<string, int>> GetSummaryAsync(long upToSeq, CancellationToken ct) =>
        await http.GetFromJsonAsync<Dictionary<string, int>>($"sales/summary?upTo={upToSeq}", ct)
            ?? throw new InvalidOperationException("empty response from POS");
}
