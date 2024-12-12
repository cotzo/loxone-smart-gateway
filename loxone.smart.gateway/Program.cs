using loxone.smart.gateway.Api.PhilipsHue;
using NReco.Logging.File;

var builder = WebApplication.CreateBuilder(args);
builder.Host.ConfigureHostOptions((_, options) =>
{
    options.ShutdownTimeout = TimeSpan.FromMinutes(1);
});

// Add services to the container.

builder.Services.AddLogging(loggingBuilder => {
    var loggingSection = builder.Configuration.GetSection("Logging");
    loggingBuilder.AddFile(loggingSection);
});

builder.Services.AddControllers();
builder.Services.AddSingleton<PhilipsHueMessageSender>();

builder.Services.AddHostedService<PhilipsHueMessageSender>(provider => provider.GetRequiredService<PhilipsHueMessageSender>());

// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks( "/health" );

app.Run();