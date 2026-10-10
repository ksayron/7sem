using StackApi;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<IStackStore, CookieStackStore>();
var app = builder.Build();
app.MapStackLab("cookie", "WS01-02");
app.Run();
