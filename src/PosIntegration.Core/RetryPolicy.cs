namespace DcTech.PosIntegration;

/// <summary>Exponential backoff with jitter for transient failures. The delay function is injectable so tests do not sleep.</summary>
public sealed class RetryPolicy(int maxAttempts, TimeSpan baseDelay, Func<Exception, bool> isTransient,
    Func<TimeSpan, CancellationToken, Task>? delay = null, Random? random = null)
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly Random _random = random ?? Random.Shared;

    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < maxAttempts && isTransient(ex) && !ct.IsCancellationRequested)
            {
                // base * 2^(attempt-1), plus up to 20% jitter so several workers do not retry in lockstep.
                var backoff = TimeSpan.FromTicks(baseDelay.Ticks * (1L << (attempt - 1)));
                var jitter = TimeSpan.FromTicks((long)(backoff.Ticks * 0.2 * _random.NextDouble()));
                await _delay(backoff + jitter, ct).ConfigureAwait(false);
            }
        }
    }

    public Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken ct) =>
        ExecuteAsync<bool>(async t => { await action(t).ConfigureAwait(false); return true; }, ct);
}
