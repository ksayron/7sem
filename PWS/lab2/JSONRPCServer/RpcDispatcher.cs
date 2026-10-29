using System.Text.Json;

namespace JSONRPCServer;

public sealed class RpcDispatcher(ILogger<RpcDispatcher> logger)
{
    public object? Dispatch(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array)
            return DispatchOne(root);
        if (root.GetArrayLength() == 0)
            return Error(null, -32600, "Invalid Request");

        var responses = new List<object>();
        foreach (var item in root.EnumerateArray())
        {
            var response = DispatchOne(item);
            if (response is not null)
                responses.Add(response);
        }
        return responses.Count == 0 ? null : responses;
    }

    private object? DispatchOne(JsonElement request)
    {
        if (request.ValueKind != JsonValueKind.Object ||
            request.EnumerateObject().Select(p => p.Name).Distinct().Count() !=
            request.EnumerateObject().Count() ||
            !request.TryGetProperty("jsonrpc", out var version) ||
            version.ValueKind != JsonValueKind.String || version.GetString() != "2.0" ||
            !request.TryGetProperty("method", out var method) ||
            method.ValueKind != JsonValueKind.String)
            return Error(null, -32600, "Invalid Request");

        var hasId = request.TryGetProperty("id", out var id);
        if (hasId && id.ValueKind is not (JsonValueKind.String or JsonValueKind.Number or JsonValueKind.Null))
            return Error(null, -32600, "Invalid Request");

        object? responseId = hasId ? id.Clone() : null;
        try
        {
            request.TryGetProperty("params", out var parameters);
            var result = Invoke(method.GetString()!, parameters);
            return hasId ? new { jsonrpc = "2.0", result, id = responseId } : null;
        }
        catch (RpcException exception)
        {
            return hasId ? Error(responseId, exception.Code, exception.Message, exception.Details) : null;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Unexpected JSON-RPC failure in {Method}", method.GetString());
            return hasId ? Error(responseId, -32603, "Internal error") : null;
        }
    }

    private static object Invoke(string method, JsonElement parameters)
    {
        if (method == "FACT")
        {
            var values = Bind(parameters, "x");
            if (!values[0].TryGetInt32(out var x))
                throw InvalidParams("x must be an Int32 JSON integer.");
            return Arithmetic.FACT(x);
        }

        if (method is not ("SUM" or "SUB" or "MUL" or "DIV"))
            throw new RpcException(-32601, "Method not found");

        var arguments = Bind(parameters, "x", "y");
        var left = ReadDouble(arguments[0], "x");
        var right = ReadDouble(arguments[1], "y");
        var result = method switch
        {
            "SUM" => Arithmetic.SUM(left, right),
            "SUB" => Arithmetic.SUB(left, right),
            "MUL" => Arithmetic.MUL(left, right),
            "DIV" => Arithmetic.DIV(left, right),
            _ => throw new InvalidOperationException()
        };
        if (!double.IsFinite(result))
            throw new RpcException(-32003, "Result exceeds finite Double range");
        return result;
    }

    private static JsonElement[] Bind(JsonElement parameters, params string[] names)
    {
        if (parameters.ValueKind == JsonValueKind.Array && parameters.GetArrayLength() == names.Length)
        {
            var values = parameters.EnumerateArray().ToArray();
            EnsureNumbers(values);
            return values;
        }
        if (parameters.ValueKind == JsonValueKind.Object)
        {
            var properties = parameters.EnumerateObject().ToArray();
            if (properties.Length == names.Length &&
                properties.Select(p => p.Name).Distinct().Count() == names.Length &&
                names.All(name => parameters.TryGetProperty(name, out _)))
            {
                var values = names.Select(name => parameters.GetProperty(name)).ToArray();
                EnsureNumbers(values);
                return values;
            }
        }
        throw InvalidParams($"Expected [{string.Join(", ", names)}] or an object with exactly these names.");
    }

    private static void EnsureNumbers(JsonElement[] values)
    {
        if (values.Any(value => value.ValueKind != JsonValueKind.Number))
            throw InvalidParams("Parameters must be JSON numbers.");
    }

    private static double ReadDouble(JsonElement value, string name)
    {
        if (!value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw InvalidParams($"{name} must be a finite Double.");
        return number;
    }

    private static RpcException InvalidParams(string details) => new(-32602, "Invalid params", details);

    public static object Error(object? id, int code, string message, string? details = null)
    {
        var error = new Dictionary<string, object> { ["code"] = code, ["message"] = message };
        if (details is not null)
            error["data"] = details;
        return new { jsonrpc = "2.0", error, id };
    }
}
