using System.Globalization;
using Grpc.Core;
using Grpc.Net.Client;
using LabGrpc;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
if (args.Length > 1 || (args.Length == 1 &&
    (!Uri.TryCreate(args[0], UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))))
{
    Console.Error.WriteLine("Usage: gRPCClient01 [http://127.0.0.1:8302]");
    return 2;
}

var address = args.Length == 0 ? "http://127.0.0.1:8302" : args[0];
using var channel = GrpcChannel.ForAddress(address);
var client = new Calculator.CalculatorClient(channel);
var failures = 0;
var total = 0;
Console.WriteLine($"gRPCClient01 (C#) -> {address}");

await Check("SUM(10.5, 2.5)", () => Binary("SUM", 10.5, 2.5), 13);
await Check("SUB(10, 3)", () => Binary("SUB", 10, 3), 7);
await Check("MUL(2.5, 4)", () => Binary("MUL", 2.5, 4), 10);
await Check("DIV(7, 2)", () => Binary("DIV", 7, 2), 3.5);
await Check("SUM(-5, 2)", () => Binary("SUM", -5, 2), -3);
await Check("SUB(2, 5)", () => Binary("SUB", 2, 5), -3);
await Check("MUL(0, 4)", () => Binary("MUL", 0, 4), 0);
await Check("DIV(0, 2)", () => Binary("DIV", 0, 2), 0);
await Check("DIV(7, -2)", () => Binary("DIV", 7, -2), -3.5);
await Check("FACT(5)", () => Factorial(5), 120);
await Check("FACT(0)", () => Factorial(0), 1);
await Check("FACT(1)", () => Factorial(1), 1);
await Check("FACT(12)", () => Factorial(12), 479001600);
await Check("DIV(1, 0)", () => Binary("DIV", 1, 0), error: StatusCode.InvalidArgument, detail: "Division by zero.");
await Check("DIV(0, -0)", () => Binary("DIV", 0, -0.0), error: StatusCode.InvalidArgument, detail: "Division by zero.");
await Check("FACT(13)", () => Factorial(13), error: StatusCode.OutOfRange, detail: "Factorial exceeds Int32.MaxValue.");
await Check("FACT(Int32.MaxValue)", () => Factorial(int.MaxValue), error: StatusCode.OutOfRange, detail: "Factorial exceeds Int32.MaxValue.");
await Check("FACT(-1)", () => Factorial(-1), error: StatusCode.InvalidArgument, detail: "Factorial requires x >= 0.");
await Check("FACT(Int32.MinValue)", () => Factorial(int.MinValue), error: StatusCode.InvalidArgument, detail: "Factorial requires x >= 0.");
await Check("SUM(NaN, 1)", () => Binary("SUM", double.NaN, 1), error: StatusCode.InvalidArgument, detail: "Operands must be finite doubles.");
await Check("MUL(+Infinity, 2)", () => Binary("MUL", double.PositiveInfinity, 2), error: StatusCode.InvalidArgument, detail: "Operands must be finite doubles.");
await Check("MUL(Double.MaxValue, 2)", () => Binary("MUL", double.MaxValue, 2), error: StatusCode.OutOfRange, detail: "Result exceeds finite double range.");
await Check("SUM(1, 2) after errors", () => Binary("SUM", 1, 2), 3);

Console.WriteLine($"Checks: {total - failures}/{total} passed, {failures} failed.");
return failures == 0 ? 0 : 1;

CallOptions Options() => new CallOptions(deadline: DateTime.UtcNow.AddSeconds(5)).WithWaitForReady();

async Task<double> Binary(string method, double x, double y)
{
    var request = new BinaryRequest { X = x, Y = y };
    using var call = method switch
    {
        "SUM" => client.SUMAsync(request, Options()),
        "SUB" => client.SUBAsync(request, Options()),
        "MUL" => client.MULAsync(request, Options()),
        "DIV" => client.DIVAsync(request, Options()),
        _ => throw new ArgumentException("Unknown method.", nameof(method))
    };
    return (await call.ResponseAsync).Result;
}

async Task<double> Factorial(int x)
{
    using var call = client.FACTAsync(new FactorialRequest { X = x }, Options());
    return (await call.ResponseAsync).Result;
}

async Task Check(string label, Func<Task<double>> invoke, double expected = 0,
    StatusCode? error = null, string? detail = null)
{
    total++;
    try
    {
        var actual = await invoke();
        var passed = error is null && actual == expected;
        if (!passed) failures++;
        Console.WriteLine($"[{(passed ? "PASS" : "FAIL")}] {label} = {actual:G17}");
    }
    catch (RpcException exception)
    {
        var passed = error == exception.StatusCode && detail == exception.Status.Detail;
        if (!passed) failures++;
        Console.WriteLine($"[{(passed ? "PASS" : "FAIL")}] {label}: caught RpcException " +
            $"{exception.StatusCode} ({(int)exception.StatusCode}): {exception.Status.Detail}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.WriteLine($"[FAIL] {label}: {exception.GetType().Name}: {exception.Message}");
    }
}
