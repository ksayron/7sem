namespace Burmalda.ConcurrencyLab.Api;

public sealed class ReplicationLabException(int status, string message) : Exception(message)
{ public int Status { get; } = status; }
