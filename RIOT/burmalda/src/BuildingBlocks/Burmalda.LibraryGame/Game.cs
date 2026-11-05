using System.Numerics;
using System.Text.Json;

namespace Burmalda.LibraryGame;

public static class GameJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    public static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)!;
}

public sealed record BookSeries(int Id, string Name, string Symbol, uint Color);
public static class LibraryWorld
{
    public static readonly BookSeries[] Series = [
        new(0, "Лунная ботаника", "Лист", 0x4D866C), new(1, "Атлас созвездий", "Звезда", 0x596DAD),
        new(2, "Рецепты алхимика", "Капля", 0xC47D38), new(3, "Письма драконам", "Крыло", 0xB7554A),
        new(4, "Хроники времени", "Часы", 0x8C65A0), new(5, "Тихие заклинания", "Круг", 0x468E96)];
    public static string BookId(int series, int volume) => $"{series}:{volume}";
    public static Vector3 Shelf(int series) => new(-6 + series / 2 * 6, 0, series % 2 == 0 ? -7.4f : 7.4f);
    public static Vector3 BookPosition(int series, int volume)
    {
        var shelf = Shelf(series);
        return new(shelf.X + ((volume - 1) % 4 - 1.5f) * .85f, .14f, (series % 2 == 0 ? -1 : 1) * (3.4f + (volume - 1) / 4 * .75f));
    }
    public static Vector3 Move(Vector3 position, Vector3 delta)
    {
        var next = position;
        bool Free(Vector3 p) => MathF.Abs(p.X) < 9.3f && MathF.Abs(p.Z) < 8.5f &&
            !Enumerable.Range(0, 6).Any(i => MathF.Abs(p.X - Shelf(i).X) < 2.15f && MathF.Abs(p.Z - Shelf(i).Z) < .8f);
        if (Free(position + new Vector3(delta.X, 0, 0))) 
            next.X += delta.X;
        if (Free(next + new Vector3(0, 0, delta.Z))) 
            next.Z += delta.Z;
        return next;
    }
    public static float? RayBox(Vector3 origin, Vector3 direction, Vector3 center, Vector3 half)
    {
        float near = 0, far = 3.2f;
        for (var axis = 0; axis < 3; axis++)
        {
            var o = axis == 0 ? origin.X : axis == 1 ? origin.Y : origin.Z;
            var d = axis == 0 ? direction.X : axis == 1 ? direction.Y : direction.Z;
            var c = axis == 0 ? center.X : axis == 1 ? center.Y : center.Z;
            var h = axis == 0 ? half.X : axis == 1 ? half.Y : half.Z;
            if (MathF.Abs(d) < .0001f) { if (o < c - h || o > c + h) return null; continue; }
            var a = (c - h - o) / d; 
            var b = (c + h - o) / d;
            near = MathF.Max(near, MathF.Min(a, b));
            far = MathF.Min(far, MathF.Max(a, b));
            if (near > far) 
                return null;
        }
        return near;
    }
}

public sealed class GameState
{
    public Guid SessionId { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public bool Demo { get; set; }
    public long Version { get; set; }
    public List<string> Held { get; set; } = [];
    public HashSet<string> Shelved { get; set; } = [];
    public int Sparks { get; set; }
    public int Capacity { get; set; } = 1;
    public int AttractionLimit { get; set; } = 2;
    public DateTimeOffset? LastAbilityAt { get; set; }
    public List<AbilityOperation> Operations { get; set; } = [];
    public int OpenSeries => Shelved.Count >= 48 ? 6 : Shelved.Count >= 24 ? 4 : 2;
    public bool Complete => Shelved.Count == 72;
    public bool IsWorldBook(string id) => !Held.Contains(id) && !Shelved.Contains(id);
    public GameState Copy() => GameJson.Read<GameState>(GameJson.Write(this));
}

public sealed record GameCommand(Guid CommandId, long ExpectedVersion, string Type, string? BookId = null,
    int? SeriesId = null, string? Upgrade = null, string ClientId = "browser");
public sealed record AbilityOperation(Guid Id, int SeriesId, string Status, int Moved = 0);
public sealed record EventData(string? BookId = null, int? SeriesId = null, int TotalShelved = 0,
    int SeriesShelved = 0, string? Upgrade = null, Guid? OperationId = null, string? Achievement = null, int Moved = 0);
public sealed record GameEvent(Guid EventId, string EventType, string EntityId, DateTimeOffset Timestamp,
    Guid UserId, Guid SessionId, bool Demo, string ClientId, Guid CorrelationId, long StateVersion, EventData Data);
public sealed record GameChange(GameState State, List<GameEvent> Events);
public sealed class GameRuleException(string message, int status = 400) : Exception(message)
{
    public int Status { get; } = status;
}

public static class GameRules
{
    public static GameChange Execute(GameState previous, GameCommand cmd, DateTimeOffset now)
    {
        if (cmd.CommandId == Guid.Empty || string.IsNullOrWhiteSpace(cmd.ClientId) || cmd.ClientId.Length > 80)
            throw new GameRuleException("Команде необходимы CommandId и ClientId (до 80 символов).");
        if (cmd.ExpectedVersion != previous.Version) 
            throw new GameRuleException("Сохранение изменилось в другой вкладке. Состояние обновлено, повторите действие.", 409);
        var state = previous.Copy();
        var events = new List<GameEvent>();
        void Emit(string type, string entity, EventData data) => events.Add(new(Guid.NewGuid(), type, entity, now,
            state.UserId, state.SessionId, state.Demo, cmd.ClientId, cmd.CommandId, state.Version + 1, data));
        switch (cmd.Type)
        {
            case "PickBook":
            {
                var (series, _) = ParseBook(cmd.BookId);
                if (series >= state.OpenSeries) 
                        throw new GameRuleException("Эта секция ещё закрыта.");
                if (!state.IsWorldBook(cmd.BookId!)) 
                        throw new GameRuleException("Эту книгу уже забрали.", 409);
                if (state.Held.Count >= state.Capacity) 
                        throw new GameRuleException("Руки заняты. Отнесите книги на полку.");
                state.Held.Add(cmd.BookId!);
                Emit("BookPickedUp", cmd.BookId!, new(cmd.BookId, series));
                break;
            }
            case "PlaceBook":
            {
                var id = cmd.BookId ?? state.Held.LastOrDefault();
                var (series, _) = ParseBook(id);
                if (!state.Held.Contains(id!)) 
                        throw new GameRuleException("Сначала возьмите книгу.");
                if (cmd.SeriesId != series) 
                        throw new GameRuleException($"Нужна полка «{LibraryWorld.Series[series].Name}».");
                state.Held.Remove(id!); 
                    state.Shelved.Add(id!); 
                    state.Sparks++;
                Emit("BookShelved", id!, new(id, series, state.Shelved.Count, state.Shelved.Count(x => x.StartsWith($"{series}:"))));
                break;
            }
            case "UseAbility":
            {
                if (state.Shelved.Count < 6) 
                        throw new GameRuleException("Притяжение откроется после шестой книги.");
                if (state.Held.Count == 0) 
                        throw new GameRuleException("Возьмите книгу: её серия определяет притяжение.");
                if (state.Held.Count >= state.Capacity) 
                        throw new GameRuleException("Для притяжения нужно свободное место в руках.");
                if (state.Operations.Any(x => x.Status == "Pending")) 
                        throw new GameRuleException("Предыдущее притяжение ещё обрабатывается.", 409);
                if (state.LastAbilityAt is { } last && now - last < TimeSpan.FromSeconds(10)) 
                        throw new GameRuleException("Заклинание восстанавливается 10 секунд.");
                var (series, _) = ParseBook(state.Held.Last());
                state.LastAbilityAt = now;
                state.Operations.Add(new(cmd.CommandId, series, "Pending"));
                Emit("AbilityUsed", cmd.CommandId.ToString(), new(SeriesId: series, TotalShelved: state.Shelved.Count, OperationId: cmd.CommandId));
                break;
            }
            case "BuyUpgrade":
            {
                var cost = cmd.Upgrade switch { "hands" when state.Capacity == 1 => 4, "hands" when state.Capacity == 3 => 12, "attraction" when state.AttractionLimit == 2 => 10, _ => throw new GameRuleException("Это улучшение недоступно.") };
                if (state.Sparks < cost) 
                        throw new GameRuleException($"Нужно {cost} искр.");
                state.Sparks -= cost;
                if (cmd.Upgrade == "hands") 
                        state.Capacity = state.Capacity == 1 ? 3 : 5; 
                else 
                    state.AttractionLimit = 4;
                Emit("UpgradePurchased", cmd.Upgrade!, new(Upgrade: cmd.Upgrade));
                break;
            }
            default: throw new GameRuleException("Неизвестная команда.");
        }
        state.Version++;
        return new(state, events);
    }

    public static GameState ApplyAttraction(GameState previous, Guid operationId)
    {
        var state = previous.Copy();
        var index = state.Operations.FindIndex(x => x.Id == operationId);
        if (index < 0 || state.Operations[index].Status != "Pending") return state;
        var operation = state.Operations[index];
        var count = Math.Min(state.AttractionLimit, state.Capacity - state.Held.Count);
        var candidates = Enumerable.Range(1, 12).Select(v => LibraryWorld.BookId(operation.SeriesId, v))
            .Where(state.IsWorldBook).Take(count).ToArray();
        state.Held.AddRange(candidates);
        state.Operations[index] = operation with { Status = candidates.Length > 0 ? "Completed" : "NoEffect", Moved = candidates.Length };
        state.Version++;
        return state;
    }

    public static (int Series, int Volume) ParseBook(string? id)
    {
        var parts = id?.Split(':');
        if (parts is not { Length: 2 } || !int.TryParse(parts[0], out var s) || !int.TryParse(parts[1], out var v) || s is < 0 or > 5 || v is < 1 or > 12 || id != LibraryWorld.BookId(s, v))
            throw new GameRuleException("Неизвестная книга.");
        return (s, v);
    }
}

public sealed record SubscriberRegistration(string Id, string[] EventTypes, string Endpoint);
public sealed record BrokerReceipt(Guid EventId, int Recipients, bool Duplicate = false);
public sealed record DeliveryView(long Id, Guid EventId, string EventType, Guid UserId, Guid SessionId, string Subscriber,
    string Status, int Attempts, DateTimeOffset NextAttemptAt, string? Error);
public sealed record JournalEntry(long Id, DateTimeOffset Time, Guid UserId, Guid SessionId, string ClientId, string EventType,
    Guid EventId, string Publisher, string Broker, string Subscriber, string Result, string? Error);
public sealed record BrokerSnapshot(string BrokerId, List<SubscriberRegistration> Subscriptions, List<DeliveryView> Deliveries, List<JournalEntry> Journal);
public sealed record SubscriberControl(bool Available = true, bool Throw = false, int DelayMs = 0);
public sealed record ControlRequest(Guid SessionId, string Subscriber, SubscriberControl Control);
public sealed record AchievementView(string Key, DateTimeOffset UnlockedAt);
public sealed record NoticeView(string Text, DateTimeOffset CreatedAt);
public sealed record ProjectionView(List<AchievementView> Achievements, List<NoticeView> Notifications, Dictionary<string, int> Analytics,
    Dictionary<string, SubscriberControl> Controls, bool Available = true);
public sealed record GameSnapshot(GameState State, ProjectionView Projections);
public sealed record EffectRequest(GameEvent Event);
