using System.Text.Json.Serialization;
using DcTech.PosIntegration;
using DcTech.PosIntegration.FakePos;

var builder = WebApplication.CreateBuilder(args);
var options = builder.Configuration.GetSection("FakePos").Get<FakePosOptions>() ?? new FakePosOptions();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<PosGenerator>();
builder.Services.AddSingleton(builder.Configuration.GetSection("LaunchGuards").Get<LaunchGuardSettings>() ?? new LaunchGuardSettings());
builder.Services.AddSingleton<TerminalRegistry>();
builder.Services.AddSingleton(TimeProvider.System);
// Enums travel as names ("Keyed", "Supervisor"), which reads better in a request body and in the journal.
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

var app = builder.Build();

var requests = 0;
app.Use(async (ctx, next) =>
{
    // Deterministic fault injection: every Nth request to the sales feed fails with 503. /health and the terminals are never failed.
    if (options.FailureEvery > 0 && ctx.Request.Path.StartsWithSegments("/sales") && Interlocked.Increment(ref requests) % options.FailureEvery == 0)
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

// Simulated terminals: the device state that a real POS reads from its drawer sensor, keylock and cash session.
var terminals = app.MapGroup("/terminals/{id}");
terminals.MapGet("/", (TerminalRegistry r, string id) => r.Get(id).State);
terminals.MapPost("/session", (TerminalRegistry r, string id, SessionRequest body) => { r.Get(id).OpenSession(body.OperatorId); return Results.NoContent(); });
terminals.MapDelete("/session", (TerminalRegistry r, string id) => { r.Get(id).CloseSession(); return Results.NoContent(); });
terminals.MapPut("/drawer", (TerminalRegistry r, string id, DrawerRequest body) => { r.Get(id).SetDrawer(body.Open); return Results.NoContent(); });
terminals.MapPut("/keylock", (TerminalRegistry r, string id, KeylockRequest body) => { r.Get(id).SetKeylock(body.Position); return Results.NoContent(); });
terminals.MapGet("/journal", (TerminalRegistry r, string id) => r.Journal(id));
terminals.MapPost("/sales", (TerminalRegistry r, PosGenerator pos, TimeProvider clock, string id, SaleAttempt attempt) =>
{
    if (string.IsNullOrWhiteSpace(attempt.OperatorId) || attempt.Lines is not { Count: > 0 } || attempt.Lines.Any(l => l.Quantity <= 0))
        return Results.BadRequest(new { error = "operatorId and at least one line with a positive quantity are required" });
    var entry = r.Get(id).Launch(attempt, pos, clock.GetUtcNow());
    r.Record(entry);
    return entry.Outcome == "blocked" ? Results.Conflict(entry) : Results.Json(entry, statusCode: StatusCodes.Status201Created);
});

app.Run();

public partial class Program;

public sealed record SessionRequest(string OperatorId);
public sealed record DrawerRequest(bool Open);
public sealed record KeylockRequest(KeylockPosition Position);
