using loxone.smart.gateway.Api.PhilipsHue;
using loxone.smart.gateway.Api.Tuya;
using OpenTelemetry.Metrics;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((hostingContext, loggerConfiguration) =>
    loggerConfiguration.ReadFrom.Configuration(hostingContext.Configuration));

var enablePrometheus = bool.Parse(builder.Configuration["Configuration:EnablePrometheus"] ?? "false");
if (enablePrometheus)
{
    builder.Services.AddOpenTelemetry()
        .WithMetrics(prometheus =>
        {
            prometheus.AddPrometheusExporter();

            prometheus.AddMeter("Microsoft.AspNetCore.Hosting",
                "Microsoft.AspNetCore.Server.Kestrel");
            prometheus.AddMeter("PhilipsHue");
            prometheus.AddMeter("Tuya");
            prometheus.AddView("http.server.request.duration",
                new ExplicitBucketHistogramConfiguration
                {
                    Boundaries =
                    [
                        0, 0.005, 0.01, 0.025, 0.05,
                        0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10
                    ]
                });
        });
}

builder.Host.ConfigureHostOptions((_, options) =>
{
    options.ShutdownTimeout = TimeSpan.FromMinutes(1);
});

builder.Services.AddHttpContextAccessor();

// Add services to the container.
builder.Services.AddHealthChecks();
builder.Services.AddControllers();

// Philips Hue
builder.Services.AddHttpClient("PhilipsHue")
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        // The Hue Bridge lives at a fixed LAN address, so keep the TLS connection open and
        // reuse it across commands instead of paying a handshake on every (usually sparse) request.
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(10),
        PooledConnectionLifetime = Timeout.InfiniteTimeSpan,
        SslOptions =
        {
            RemoteCertificateValidationCallback = (_, _, _, _) => true
        }
    })
    // Never recycle the handler: there is no DNS to refresh for a static IP, and recycling
    // would throw away the warm connection.
    .SetHandlerLifetime(Timeout.InfiniteTimeSpan);
builder.Services.AddSingleton<PhilipsHueMessageSender>();
builder.Services.AddHostedService<PhilipsHueMessageSender>(provider => provider.GetRequiredService<PhilipsHueMessageSender>());

// Tuya
builder.Services.AddSingleton<TuyaMessageSender>();
builder.Services.AddHostedService<TuyaMessageSender>(provider => provider.GetRequiredService<TuyaMessageSender>());

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

builder.Services.AddSingleton<PhilipsHueMetrics>();
builder.Services.AddSingleton<TuyaMetrics>();

var app = builder.Build();

if (enablePrometheus)
{
    app.MapPrometheusScrapingEndpoint();
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapControllers();
app.MapHealthChecks( "/health" );

app.Run();