using Grpc.Core;
using LabGrpc;

namespace gRPCServer01;

public sealed class CalculatorService : Calculator.CalculatorBase
{
    public override Task<DoubleReply> SUM(BinaryRequest request, ServerCallContext context) =>
        Calculate(request, (x, y) => x + y);

    public override Task<DoubleReply> SUB(BinaryRequest request, ServerCallContext context) =>
        Calculate(request, (x, y) => x - y);

    public override Task<DoubleReply> MUL(BinaryRequest request, ServerCallContext context) =>
        Calculate(request, (x, y) => x * y);

    public override Task<DoubleReply> DIV(BinaryRequest request, ServerCallContext context) =>
        Calculate(request, (x, y) => y == 0
            ? throw new RpcException(new Status(StatusCode.InvalidArgument, "Division by zero."))
            : x / y);

    public override Task<IntReply> FACT(FactorialRequest request, ServerCallContext context)
    {
        if (request.X < 0)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Factorial requires x >= 0."));
        if (request.X > 12)
            throw new RpcException(new Status(StatusCode.OutOfRange, "Factorial exceeds Int32.MaxValue."));

        var result = 1;
        for (var i = 2; i <= request.X; i++)
            result = checked(result * i);

        return Task.FromResult(new IntReply { Result = result });
    }

    private static Task<DoubleReply> Calculate(BinaryRequest request, Func<double, double, double> operation)
    {
        if (!double.IsFinite(request.X) || !double.IsFinite(request.Y))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Operands must be finite doubles."));

        var result = operation(request.X, request.Y);
        if (!double.IsFinite(result))
            throw new RpcException(new Status(StatusCode.OutOfRange, "Result exceeds finite double range."));

        return Task.FromResult(new DoubleReply { Result = result });
    }
}
