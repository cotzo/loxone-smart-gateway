using loxone.smart.gateway.Apis;

var builder = WebApplication.CreateSlimBuilder(args);

// builder.Services.ConfigureHttpJsonOptions(_ =>
// {
//     //options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
// });

var app = builder.Build();

var philipsApi = app.MapGroup("/philips");
philipsApi.MapPut("setLight/{id}", PhilipsHueApi.SetLight);

app.Run();

// public record Todo(int Id, string? Title, DateOnly? DueBy = null, bool IsComplete = false);
//
// [JsonSerializable(typeof(Todo[]))]
// internal partial class AppJsonSerializerContext : JsonSerializerContext
// {
// }