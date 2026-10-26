using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

internal static class ReplicationCommands
{
    private const string Base = "/api/concurrency-lab/replication/runs";
    public static async Task Run(HttpClient http, string[] args)
    {
        var command = args[0].ToLowerInvariant();
        if (command == "rep-cycle")
        {
            if (!Guid.TryParse(args.ElementAtOrDefault(1), out var copy)) throw new ArgumentException("rep-cycle <actual-copy-guid> [repeats:1..10]");
            var repeats = 1;
            if (args.Length > 2 && (!int.TryParse(args[2], out repeats) || repeats is < 1 or > 10))
                throw new ArgumentException("rep-cycle repeats: 1..10.");
            var folder = Path.Combine(Directory.GetCurrentDirectory(), "lab-results", $"streaming-replication-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}"); Directory.CreateDirectory(folder);
            for (var i = 1; i <= repeats; i++)
            {
                var created = await Post(http, Base, new { }); var id = Text(created, "id");
                await Post(http, $"{Base}/{id}/cycle", new { copyId = copy });
                var final = await Wait(http, id); Print(final);
                await File.WriteAllTextAsync(Path.Combine(folder, $"cycle-{i}.json"), JsonSerializer.Serialize(final, new JsonSerializerOptions { WriteIndented = true }));
                await File.WriteAllTextAsync(Path.Combine(folder, $"cycle-{i}.md"), Markdown(final));
            }
            Console.WriteLine($"[info] Reports: {folder}"); return;
        }
        JsonElement result;
        if (command == "rep-create") result = await Post(http, Base, new { });
        else
        {
            if (!Guid.TryParse(args.ElementAtOrDefault(1), out var id)) throw new ArgumentException("Укажите ID опыта.");
            var url = $"{Base}/{id}";
            switch (command)
            {
                case "rep-report": result = await Get(http, url); break;
                case "rep-read": result = await Post(http, url + "/read", new { clientId = args.ElementAtOrDefault(2) ?? "Client 1" }); break;
                case "rep-update":
                    if (!Guid.TryParse(args.ElementAtOrDefault(2), out var copyId) || !bool.TryParse(args.ElementAtOrDefault(3), out var available) || !long.TryParse(args.ElementAtOrDefault(4), out var version))
                        throw new ArgumentException("rep-update <run-id> <copy-id> <true|false> <expected-version> [client]");
                    result = await Post(http, url + "/update", new { clientId = args.ElementAtOrDefault(5) ?? "Client 2", copyId, available, expectedVersion = version }); break;
                case "rep-instance":
                    if (args.ElementAtOrDefault(2) is not ("postgres" or "postgres-replica") || args.ElementAtOrDefault(3) is not ("on" or "off"))
                        throw new ArgumentException("rep-instance <id> <postgres|postgres-replica> <on|off>");
                    await Post(http, url + "/instances/" + args[2], new { available = args[3] == "on" }); result = await Wait(http, id.ToString()); break;
                case "rep-sync": await Post(http, url + "/synchronize", new { }); result = await Wait(http, id.ToString()); break;
                case "rep-pause": case "rep-resume": result = await Post(http, url + "/replication", new { enabled = command == "rep-resume" }); break;
                default: throw new ArgumentException("Неизвестная команда ЛР №5.");
            }
        }
        Print(result);
    }
    private static async Task<JsonElement> Wait(HttpClient http, string id)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(6);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = await Get(http, $"{Base}/{id}"); var status = Text(state, "actionStatus");
            if (status is "Failed" or "Interrupted") throw new InvalidOperationException(Text(state, "actionError"));
            if (status != "Running") return state;
            Console.WriteLine($"[info] Running; writer={Text(state, "activeInstance")}; milestones={state.GetProperty("milestones").GetArrayLength()}");
            await Task.Delay(1000);
        }
        throw new TimeoutException("Ожидание истекло. Проверьте rep-report; операция не повторяется.");
    }
    private static async Task<JsonElement> Post(HttpClient http, string url, object body)
    { using var response = await http.PostAsJsonAsync(url, body); return await Read(response); }
    private static async Task<JsonElement> Get(HttpClient http, string url)
    { using var response = await http.GetAsync(url); return await Read(response); }
    private static async Task<JsonElement> Read(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {Text(document.RootElement, "message")}");
        return document.RootElement.Clone();
    }
    private static string Text(JsonElement value, string key) => value.GetProperty(key).ToString();
    private static void Print(JsonElement value)
    {
        Console.WriteLine($"[info] ID={Text(value, "id")} writer={Text(value, "activeInstance")} consistent={Text(value, "consistent")} action={Text(value, "actionStatus")}");
        foreach (var node in value.GetProperty("nodes").EnumerateArray())
        {
            Console.WriteLine($"[info] {Text(node, "id")} {Text(node, "role")} {Text(node, "state")} WAL={Text(node, "walLsn")} replay={Text(node, "replayLsn")}");
            foreach (var copy in node.GetProperty("copies").EnumerateArray()) Console.WriteLine($"  {Text(copy, "id")} {Text(copy, "title")} {Text(copy, "status")} v{Text(copy, "version")}");
        }
        if (value.GetProperty("operations").EnumerateArray().LastOrDefault() is var row && row.ValueKind == JsonValueKind.Object) Console.WriteLine($"[info] {Text(row, "message")}");
    }
    private static string Markdown(JsonElement final)
    {
        var b = new StringBuilder($"# LR5: PostgreSQL streaming replication\n\nSession: {Text(final, "id")}\n\n| Stage | Writer | Nodes |\n|---|---|---|\n");
        foreach (var stage in final.GetProperty("milestones").EnumerateArray())
            b.AppendLine($"| {Text(stage, "label").Replace("|", "\\|")} | {Text(stage, "writer")} | {string.Join("; ", stage.GetProperty("nodes").EnumerateArray().Select(n => $"{Text(n, "id")}: {Text(n, "role")} {Text(n, "state")}, WAL {Text(n, "walLsn")}, replay {Text(n, "replayLsn")}"))} |");
        b.AppendLine("\n| Client | Operation | Copy | Node | Committed | Replay | Result |\n|---|---|---|---|---|---|---|");
        foreach (var row in final.GetProperty("operations").EnumerateArray()) b.AppendLine($"| {Text(row, "clientId")} | {Text(row, "operation")} | {Text(row, "copyId")} | {Text(row, "usedInstance")} | {Text(row, "committed")} | {Text(row, "replication")} | {Text(row, "result")} |");
        b.AppendLine("\nMeasured values and full database observations are in the adjacent JSON. Promotion changes roles; recovery rejoins the former primary as a standby.");
        return b.ToString();
    }
}
