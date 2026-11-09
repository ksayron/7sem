using Microsoft.Extensions.Configuration;

namespace Burmalda.PostgresControl;

public static class RaftConfiguration
{
    public static bool InDocker(IConfiguration config) => config["ReplicationLab:RuntimeMode"] == "Docker";

    public static Dictionary<int, string> Members(IConfiguration config)
    {
        var configured = config.GetSection("Raft:Members").Get<Dictionary<int, string>>();
        var members = configured ?? Enumerable.Range(1, 5).ToDictionary(id => id,
            id => $"http://127.0.0.1:{config.GetValue("Raft:BasePort", 5121) + id - 1}");
        if (!members.Keys.Order().SequenceEqual(Enumerable.Range(1, 5)) || members.Values.Any(address =>
            !Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.Port < 1))
            throw new InvalidOperationException("Raft:Members must contain exactly five HTTP addresses, IDs 1..5.");
        return members;
    }
}
