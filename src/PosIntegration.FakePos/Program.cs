using DcTech.PosIntegration;
using DcTech.PosIntegration.FakePos;

var builder = WebApplication.CreateBuilder(args);
var options = builder.Configuration.GetSection("FakePos").Get<FakePosOptions>() ?? new FakePosOptions();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<PosGenerator>();

var app = builder.Build();

var requests = 0;
app.Use(async (ctx, next) =>
{
    // Deterministic fault injection: every Nth data request fails with 503. /health is never failed.
    if (options.FailureEvery > 0 && ctx.Request.Path != "/health" && Interlocked.Increment(ref requests) % options.FailureEvery == 0)
    {
        ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        return;
    }
    await next();
});

app.MapGet("/health", () => Results.Ok(new { status = "ok", total = options.Total }));
app.MapGet("/sales", (PosGenerator pos, long? after, int? limit) =>
    new SalesPage(pos.Page(after ?? 0, Math.Clamp(limit ?? 50, 1, 500))));
app.MapGet("/sales/summary", (PosGenerator pos, long? upTo) => pos.Summary(upTo ?? long.MaxValue));

app.Run();

public partial class Program;
