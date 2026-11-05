using System.Text.Json;
using System.Text.RegularExpressions;

namespace Burmalda.PostgresControl;

public interface IMaintenanceCleanup
{
    Task<int> CleanupCompletedMaintenance(IReadOnlySet<Guid> completedRecoveries, CancellationToken ct);
}

// A completed job is disposable only after both Raft and the local execution journal
// confirm recovery. Never use prune, --force or --volumes here.
public static partial class MaintenanceCleanup
{
    [GeneratedRegex("^[a-f0-9]{12,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex ContainerId();

    public static async Task<int> Run(Func<CancellationToken, string[], Task<string>> docker,
        IReadOnlySet<Guid> completedRecoveries, CancellationToken ct)
    {
        if (completedRecoveries.Count == 0) return 0;
        var listed = await docker(ct, ["ps", "-a", "--filter", "label=com.docker.compose.project=burmalda",
            "--filter", "label=burmalda.raft.operation", "--format", "{{.ID}}"]);
        var ids = listed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(id => ContainerId().IsMatch(id)).Distinct().ToArray();
        if (ids.Length == 0) return 0;
        using var inspected = JsonDocument.Parse(await docker(ct, ["inspect", ..ids]));
        var removed = 0;
        foreach (var container in inspected.RootElement.EnumerateArray())
        {
            if (!container.TryGetProperty("Id", out var idValue) || idValue.GetString() is not { } id || !ContainerId().IsMatch(id) ||
                !ids.Any(candidate => id.StartsWith(candidate, StringComparison.Ordinal)) ||
                !container.TryGetProperty("Config", out var config) || !config.TryGetProperty("Labels", out var labels) || labels.ValueKind != JsonValueKind.Object ||
                !labels.TryGetProperty("com.docker.compose.project", out var project) || project.GetString() != "burmalda" ||
                !labels.TryGetProperty("com.docker.compose.oneoff", out var oneoff) || !string.Equals(oneoff.GetString(), "true", StringComparison.OrdinalIgnoreCase) ||
                !labels.TryGetProperty("burmalda.raft.operation", out var operation) || !Guid.TryParse(operation.GetString(), out var operationId) ||
                !completedRecoveries.Contains(operationId) ||
                !container.TryGetProperty("Name", out var name) || name.GetString() != $"/burmalda-raft-maintenance-{operationId:N}" ||
                !container.TryGetProperty("State", out var state) || !state.TryGetProperty("Status", out var status) || status.GetString() != "exited" ||
                !state.TryGetProperty("Running", out var running) || running.ValueKind != JsonValueKind.False ||
                !state.TryGetProperty("ExitCode", out var exitCode) || !exitCode.TryGetInt32(out var code) || code != 0)
                continue;
            // Removing by inspected ID without force also closes the inspect/start race safely.
            await docker(ct, ["rm", id]);
            removed++;
        }
        return removed;
    }
}
