using DcTech.PosIntegration;
using DcTech.PosIntegration.Data;
using DcTech.PosIntegration.Worker;

var builder = Host.CreateApplicationBuilder(args);
// Environment variables such as Worker__SqlConnection or Worker__RunOnce map onto WorkerOptions.
builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection("Worker"));
var opts = builder.Configuration.GetSection("Worker").Get<WorkerOptions>() ?? new WorkerOptions();
if (string.IsNullOrWhiteSpace(opts.SqlConnection))
    throw new InvalidOperationException("Worker:SqlConnection is not set (env Worker__SqlConnection).");

builder.Services.AddSingleton(new SqlConnectionInfo(opts.SqlConnection));
builder.Services.AddSingleton(new SqlStockStore(opts.SqlConnection));
builder.Services.AddSingleton<IStockStore>(sp => sp.GetRequiredService<SqlStockStore>());
builder.Services.AddHttpClient<ISaleSource, HttpSaleSource>(c =>
{
    c.BaseAddress = new Uri(opts.PosApiUrl.TrimEnd('/') + "/");
    c.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddSingleton(new RetryPolicy(maxAttempts: 5, baseDelay: TimeSpan.FromMilliseconds(200), Transient.IsTransient));
builder.Services.AddSingleton<SyncService>(sp => new SyncService(
    sp.GetRequiredService<ISaleSource>(), sp.GetRequiredService<IStockStore>(), sp.GetRequiredService<RetryPolicy>(),
    sp.GetRequiredService<ILogger<SyncService>>()));
builder.Services.AddSingleton<Reconciler>();
builder.Services.AddHostedService<SyncWorker>();

await builder.Build().RunAsync();
