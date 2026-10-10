using StackApi;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<IStackStore, ServerStackStore>();
var app = builder.Build();
app.MapStackLab("server", "WS01-01");
app.Run();
