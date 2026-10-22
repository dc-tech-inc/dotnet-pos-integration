using System.Net;
using Microsoft.Data.SqlClient;

namespace DcTech.PosIntegration.Worker;

public static class Transient
{
    /// <summary>Deadlock victim, timeouts, connection-level SQL errors, and HTTP 5xx / 408 / 429 or no response at all.</summary>
    public static bool IsTransient(Exception ex) => ex switch
    {
        SqlException s => s.IsTransient || s.Number is 1205 or -2,
        HttpRequestException h => h.StatusCode is null or >= HttpStatusCode.InternalServerError
            or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests,
        TimeoutException or TaskCanceledException => true,
        _ => false,
    };
}
