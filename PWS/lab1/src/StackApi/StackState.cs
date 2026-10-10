namespace StackApi;

// Elements are stored bottom-to-top; the last element is the top of the stack.
public sealed record StackState(int BaseValue, List<int> Stack)
{
    public static StackState Empty() => new(0, []);
    public StackState Copy() => new(BaseValue, [.. Stack]);
}

public sealed class LabException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
