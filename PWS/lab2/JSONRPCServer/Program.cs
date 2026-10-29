using System.Text.Json;
using JSONRPCServer;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<RpcDispatcher>();
var app = builder.Build();

app.MapGet("/", () => Results.Ok(new
{
    service = "JSONRPCServer",
    protocol = "JSON-RPC 2.0",
    endpoint = "/rpc",
    methods = new[] { "SUM", "SUB", "MUL", "DIV", "FACT" }
}));

app.MapPost("/rpc", async (HttpRequest request, RpcDispatcher dispatcher,
    CancellationToken cancellationToken) =>
{
    try
    {
        using var document = await JsonDocument.ParseAsync(request.Body,
            cancellationToken: cancellationToken);
        var response = dispatcher.Dispatch(document.RootElement);
        return response is null ? Results.NoContent() : Results.Json(response);
    }
    catch (JsonException)
    {
        return Results.Json(RpcDispatcher.Error(null, -32700, "Parse error"));
    }
});

app.Run();
