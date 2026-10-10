using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace StackApi;

public static class LabEndpoints
{
    public const int MaxStackSize = 64;
    private static readonly JsonSerializerOptions ResponseJson = new() { PropertyNamingPolicy = null };

    public static void MapDemo(this WebApplication app, string mode, string name)
    {
        app.MapGet("/", () =>
        {
            using var stream = typeof(LabEndpoints).Assembly.GetManifestResourceStream("StackApi.demo.html")!;
            using var reader = new StreamReader(stream);
            return Results.Content(reader.ReadToEnd().Replace("__MODE__", mode).Replace("__NAME__", name), "text/html; charset=utf-8");
        });
        app.MapGet("/health", () => Results.Ok(new { status = "ok", service = name }));
    }

    public static void MapStackLab(this WebApplication app, string mode, string service)
    {
        app.MapDemo(mode, service);
        var instance = app.Configuration["INSTANCE_NAME"] ?? service;
        app.MapMethods("/stack", ["GET", "POST", "PUT", "DELETE"], async (HttpContext context, IStackStore store) =>
        {
            context.Response.Headers["X-Instance"] = instance;
            context.Response.Headers["X-State-Storage"] = mode;
            context.Response.Headers.CacheControl = "no-store";
            try
            {
                var value = await ReadValue(context.Request);
                var state = store.Apply(context, current => ApplyOperation(context.Request.Method, value, current));
                int? top = state.Stack.Count == 0 ? null : state.Stack[^1];
                // A GET computes the sum without feeding the returned value back into the base.
                // Int64 safely holds the sum of two Int32 values.
                var result = (long)state.BaseValue + (top ?? 0);
                app.Logger.LogInformation("{Method} /stack => RESULT={Result}, depth={Depth}, instance={Instance}",
                    context.Request.Method, result, state.Stack.Count, instance);
                return Results.Json(new
                {
                    RESULT = result,
                    baseValue = state.BaseValue,
                    topStackValue = top,
                    stackSize = state.Stack.Count,
                    stack = state.Stack,
                    instance,
                    storage = mode
                }, ResponseJson);
            }
            catch (LabException error)
            {
                return Results.Json(new { error = error.Message }, statusCode: error.StatusCode);
            }
        });
    }

    private static async Task<int?> ReadValue(HttpRequest request)
    {
        if (request.Method == "PUT")
        {
            if (request.Query["ADD"].Count != 1 || !int.TryParse(request.Query["ADD"],
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var add))
                throw new LabException(400, "ADD must be one Int32 query parameter.");
            return add;
        }
        if (request.Method != "POST") return null;
        if (!request.HasJsonContentType()) throw new LabException(415, "POST requires Content-Type: application/json.");
        // The endpoint accepts a tiny object; cap the body even for chunked requests.
        using var buffer = new MemoryStream();
        var chunk = new byte[1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk.AsMemory(), request.HttpContext.RequestAborted)) > 0)
        {
            if (buffer.Length + read > 4096) throw new LabException(413, "JSON body is too large.");
            buffer.Write(chunk, 0, read);
        }
        try
        {
            using var body = JsonDocument.Parse(buffer.ToArray());
            if (body.RootElement.ValueKind != JsonValueKind.Object ||
                body.RootElement.EnumerateObject().Count(property => property.Name == "RESULT") != 1 ||
                !body.RootElement.TryGetProperty("RESULT", out var result) ||
                result.ValueKind != JsonValueKind.Number || !result.TryGetInt32(out var value))
                throw new LabException(400, "Body must contain exactly one integer RESULT, for example {\"RESULT\":10}.");
            return value;
        }
        catch (JsonException)
        {
            throw new LabException(400, "Malformed JSON body.");
        }
    }

    private static StackState ApplyOperation(string method, int? value, StackState state)
    {
        switch (method)
        {
            case "POST": return state with { BaseValue = value!.Value };
            case "PUT":
                if (state.Stack.Count >= MaxStackSize) throw new LabException(409, "Stack limit is 64 elements.");
                state.Stack.Add(value!.Value);
                break;
            case "DELETE":
                if (state.Stack.Count == 0) throw new LabException(409, "Stack is empty.");
                state.Stack.RemoveAt(state.Stack.Count - 1);
                break;
        }
        return state;
    }
}
