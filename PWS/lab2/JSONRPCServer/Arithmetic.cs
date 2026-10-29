namespace JSONRPCServer;

public static class Arithmetic
{
    public static double SUM(double x, double y) => x + y;
    public static double SUB(double x, double y) => x - y;
    public static double MUL(double x, double y) => x * y;

    public static double DIV(double x, double y)
    {
        if (y == 0)
            throw new RpcException(-32001, "Division by zero");
        return x / y;
    }

    public static int FACT(int x)
    {
        if (x < 0)
            throw new RpcException(-32602, "Invalid params", "x must be non-negative.");
        if (x > 12)
            throw new RpcException(-32002, "Factorial exceeds Int32 range");

        var result = 1;
        for (var i = 2; i <= x; i++)
            result = checked(result * i);
        return result;
    }
}

public sealed class RpcException(int code, string message, string? details = null)
    : Exception(message)
{
    public int Code { get; } = code;
    public string? Details { get; } = details;
}
