namespace Burmalda.DemoControl;

public sealed record RunOptions(string Scenario = "wave", int Peak = 300, int RampSeconds = 90, int HoldSeconds = 120)
{
    public int DurationSeconds => 30 + RampSeconds + HoldSeconds + 30 + 150;
    public void Validate()
    {
        if (!new[] { "wave", "spike", "mixed", "recovery" }.Contains(Scenario) || Peak is < 1 or > 500 ||
            RampSeconds is < 5 or > 300 || HoldSeconds is < 30 or > 600)
            throw new DemoConflict("Проверьте сценарий, пик 1–500, рост 5–300 с и удержание 30–600 с.");
    }
}

public static class ScenarioSchedule
{
    public static int Target(RunOptions options, double elapsed)
    {
        var baseline = Math.Min(20, options.Peak);
        if (elapsed < 30) return baseline;
        if (elapsed < 30 + options.RampSeconds)
            return (int)Math.Round(baseline + (options.Peak - baseline) * (elapsed - 30) / options.RampSeconds);
        var drop = 30 + options.RampSeconds + options.HoldSeconds;
        if (elapsed < drop) return options.Peak;
        if (elapsed < drop + 30) return (int)Math.Round(options.Peak * (1 - (elapsed - drop) / 30));
        return 0;
    }
    public static string Phase(RunOptions options, double elapsed) => elapsed < 30 ? "Исходная нагрузка" :
        elapsed < 30 + options.RampSeconds ? "Приход клиентов" :
        elapsed < 30 + options.RampSeconds + options.HoldSeconds ? "Пиковая нагрузка" :
        elapsed < options.DurationSeconds - 150 ? "Клиенты уходят" : "Наблюдение снижения реплик";
}

public sealed class DemoConflict(string message) : Exception(message);
public sealed record FaultRequest(string Service, int ErrorPercent = 25, int DelayMs = 0, int DurationSeconds = 30)
{
    public void Validate()
    {
        if (Service is not ("catalog" or "gateway") || ErrorPercent is < 0 or > 100 || DelayMs is < 0 or > 5000 || DurationSeconds is < 1 or > 120)
            throw new DemoConflict("Разрешены Catalog/Gateway, 0–100% ошибок, задержка до 5000 мс, длительность 1–120 с.");
    }
}
public sealed record DemoRun(Guid Id, RunOptions Options, DateTimeOffset StartedAt, string State = "running", DateTimeOffset? FinishedAt = null);
public sealed record Fixture(int Index, string Email, string DisplayName, string ProtectedTokens, Guid BookId, Guid CopyId);
public sealed record SessionTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);
public sealed record DemoEvent(long Id, DateTimeOffset At, string Kind, string Message);
public sealed record PodView(string Name, string Uid, string App, string Phase, bool Ready, int Restarts, DateTimeOffset? DeletingAt);
public sealed record HpaView(string Name, int Current, int Desired, int Min, int Max, double? CpuPercent, string Conditions);
public sealed record ChartPoint(double Time, double Value);
public sealed record ClusterSnapshot(DateTimeOffset? CheckedAt, string? Error, PodView[] Pods, HpaView[] Hpas,
    Dictionary<string, ChartPoint[]> Charts, DateTimeOffset? MetricsAt);
public sealed record Preparation(bool Running, int Ready, int Requested, string? Error);
public sealed record EngineSnapshot(DemoRun? Run, int Target, int Active, string Phase, Preparation Preparation,
    double SentRps, double SuccessRps, double P95Ms, double ErrorPercent, long CompletedOperations,
    long FailedOperations, int PendingOperations, string[] Unresolved, Dictionary<string, long> Outcomes,
    Burmalda.ServiceDefaults.DemoFault[] Faults);
