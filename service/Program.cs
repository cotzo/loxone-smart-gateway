using System.Security.Cryptography;
using System.Text;
using loxone.smart.gateway.Api.Gateway;
using loxone.smart.gateway.Api.Irrigation;
using Microsoft.AspNetCore.DataProtection;
using System.Text.Json.Serialization;
using OpenTelemetry.Metrics;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((hostingContext, loggerConfiguration) =>
    loggerConfiguration.ReadFrom.Configuration(hostingContext.Configuration));

var adminPassword = builder.Configuration["Gateway:AdminPassword"];
if (string.IsNullOrWhiteSpace(adminPassword) || adminPassword.Length < 16)
    throw new InvalidOperationException("Set Gateway__AdminPassword to a secret at least 16 characters long.");

var dataDirectory = builder.Configuration["Gateway:DataDirectory"] ?? "/data";
var keyDirectory = Path.Combine(dataDirectory, "keys");
Directory.CreateDirectory(keyDirectory);
if (!OperatingSystem.IsWindows())
{
    try { File.SetUnixFileMode(keyDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    catch (IOException) { }
    catch (UnauthorizedAccessException) { }
}
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(keyDirectory))
    .SetApplicationName("LoxoneSmartGateway");

var enablePrometheus = bool.Parse(builder.Configuration["Configuration:EnablePrometheus"] ?? "false");
if (enablePrometheus)
{
    builder.Services.AddOpenTelemetry().WithMetrics(metrics =>
    {
        metrics.AddPrometheusExporter();
        metrics.AddMeter("Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel", "PhilipsHue", "Tuya",
            GatewayMetrics.MeterName, IrrigationMetrics.MeterName);
        metrics.AddView("http.server.request.duration", new ExplicitBucketHistogramConfiguration
        {
            Boundaries = [0, 0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10]
        });
        metrics.AddView("gateway.provider.queue.wait", new ExplicitBucketHistogramConfiguration
        {
            Boundaries = [1, 5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000, 30000, 60000]
        });
        metrics.AddView("gateway.provider.request.duration", new ExplicitBucketHistogramConfiguration
        {
            Boundaries = [1, 5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000, 30000]
        });
        metrics.AddView("gateway.provider.event.to.dispatch", new ExplicitBucketHistogramConfiguration
        {
            Boundaries = [1, 5, 10, 25, 50, 100, 250, 500, 1000, 2500, 5000, 10000, 30000, 60000]
        });
        metrics.AddView("irrigation.weather.observation.interval", new ExplicitBucketHistogramConfiguration
        {
            Boundaries = [60, 120, 300, 600, 900, 1800, 3600, 10800, 21600, 43200, 86400]
        });
        metrics.AddView("irrigation.run.duration", new ExplicitBucketHistogramConfiguration
        {
            Boundaries = [1, 10, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200]
        });
    });
}

builder.Host.ConfigureHostOptions((_, options) => options.ShutdownTimeout = TimeSpan.FromMinutes(1));
builder.Services.AddHttpContextAccessor();
builder.Services.AddHealthChecks();
builder.Services.AddControllers().AddJsonOptions(options =>
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddOpenApi();
builder.Services.AddSingleton<GatewayStateStore>();
builder.Services.AddSingleton<GatewayMetrics>();
builder.Services.AddSingleton<IrrigationMetrics>();
builder.Services.AddSingleton<LoxoneIntegration>();
builder.Services.AddSingleton<DeviceIntegrations>();
builder.Services.AddHostedService<GatewayRuntimeService>();
builder.Services.AddHostedService<HueGroupMembershipSyncService>();
builder.Services.AddHostedService<IrrigationDurationSyncService>();
builder.Services.AddHostedService<IrrigationSignalSyncService>();
builder.Services.AddHostedService<IrrigationWeatherStateService>(provider => provider.GetRequiredService<IrrigationWeatherStateService>());

builder.Services.AddHttpClient("HueDynamic")
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true }
    });
builder.Services.AddHttpClient("LoxoneDiscovery");
builder.Services.AddHttpClient<OpenMeteoForecastClient>(client =>
{
    client.BaseAddress = new Uri("https://api.open-meteo.com/");
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddSingleton<IrrigationService>();
builder.Services.AddSingleton<WeatherIngestionService>();
builder.Services.AddSingleton<IrrigationWeatherStateService>();
builder.Services.AddSingleton<IrrigationDisableStateService>();
builder.Services.AddSingleton<IrrigationRunTracker>();
builder.Services.AddSingleton<IrrigationLoxoneService>();
builder.Services.AddSingleton<LawnDryService>();

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") &&
        !context.Request.Path.Equals("/api/login", StringComparison.OrdinalIgnoreCase))
    {
        var authorization = context.Request.Headers["Authorization"].ToString();
        var supplied = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authorization[7..]
            : string.Empty;
        if (!CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(adminPassword)),
                SHA256.HashData(Encoding.UTF8.GetBytes(supplied))))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
    }
    await next();
});
if (enablePrometheus) app.MapPrometheusScrapingEndpoint();
if (app.Environment.IsDevelopment()) app.MapOpenApi();
app.MapControllers();
app.MapHealthChecks("/health");
app.Run();
