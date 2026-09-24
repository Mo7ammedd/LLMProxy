using LLMProxy.Api;
using LLMProxy.Application;
using LLMProxy.Infrastructure;
using LLMProxy.Providers;
using LLMProxy.Server;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

if (args is ["healthcheck"]) return await ServerCommands.HealthCheckAsync();
var commandMode = args.FirstOrDefault() is "keys" or "migrate" or "reservations";
var builder = WebApplication.CreateBuilder(commandMode ? [] : args);
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(options => options.IncludeScopes = true);
if (commandMode) builder.Logging.SetMinimumLevel(LogLevel.Critical);

var gatewayOptions = builder.Configuration.GetSection("LLMProxy").Get<GatewayOptions>() ?? new();
var apiOptions = builder.Configuration.GetSection("LLMProxy:Api").Get<ApiOptions>() ?? new();
apiOptions.AdminKey = builder.Configuration["LLMPROXY_ADMIN_KEY"] ?? apiOptions.AdminKey;
builder.WebHost.ConfigureKestrel(server =>
{
    server.AddServerHeader = false;
    server.Limits.MaxRequestBodySize = gatewayOptions.Requests.MaxBodyBytes;
    server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(30);
});
if (string.IsNullOrEmpty(builder.Configuration["urls"]) && string.IsNullOrEmpty(builder.Configuration["http_ports"])
    && string.IsNullOrEmpty(builder.Configuration["https_ports"])) builder.WebHost.UseUrls("http://0.0.0.0:4000");
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(45));
builder.Services.AddLlmProxyApplication(gatewayOptions);
builder.Services.AddLlmProxyInfrastructure(builder.Configuration);
builder.Services.AddLlmProxyProviders(builder.Configuration);
builder.Services.AddLlmProxyApi(apiOptions);
builder.Services.AddSingleton<IRuntimeConfiguration, RuntimeConfigurationService>();
builder.Services.AddHostedService<BatchWorker>();

var exportOtlp = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
builder.Services.AddOpenTelemetry().ConfigureResource(resource => resource.AddService("LLMProxy"))
    .WithTracing(tracing =>
    {
        tracing.AddSource(GatewayTelemetry.SourceName)
            .AddAspNetCoreInstrumentation(options =>
            {
                options.RecordException = false;
                options.Filter = context => !context.Request.Path.StartsWithSegments("/health");
            })
            .AddHttpClientInstrumentation(options => options.RecordException = false);
        if (exportOtlp) tracing.AddOtlpExporter();
    })
    .WithMetrics(metrics =>
    {
        metrics.AddMeter(GatewayTelemetry.SourceName).AddAspNetCoreInstrumentation().AddHttpClientInstrumentation();
        if (exportOtlp) metrics.AddOtlpExporter();
    });

await using var app = builder.Build();
try
{
    // Validate provider/strategy registrations at startup without making external API calls.
    app.Services.GetRequiredService<ModelRouter>();
    await app.Services.GetRequiredService<StorageInitializer>().InitializeAsync(args is ["migrate"], CancellationToken.None);
    if (commandMode) return await ServerCommands.RunAsync(args, app.Services);
    app.UseLlmProxyApi();
    await app.RunAsync();
    return 0;
}
catch (Exception exception)
{
    // Configuration or database errors can contain credentials; never dump exception objects to startup logs.
    app.Logger.LogCritical("Server startup or command failed: {FailureType}. Check configuration, database reachability and migrations.", exception.GetType().Name);
    return 1;
}

public partial class Program;
