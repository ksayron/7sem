using Burmalda.LibraryGame;
using Burmalda.LibraryGame.Server;
using Microsoft.EntityFrameworkCore;

namespace Burmalda.LibraryGame.Api;

public sealed class SaveRow
{
    public Guid Id { get; set; } public Guid UserId { get; set; } public bool Demo { get; set; }
    public long Version { get; set; } public string Json { get; set; } = "";
}
public sealed class CommandRow
{
    public Guid Id { get; set; } public Guid UserId { get; set; } public Guid SessionId { get; set; }
    public string Request { get; set; } = ""; public string Result { get; set; } = "";
}
public sealed class AppliedEffect { public Guid Id { get; set; } public Guid SessionId { get; set; } }
public sealed class GameDb(DbContextOptions<GameDb> options) : DbContext(options), IOutboxDatabase
{
    public DbSet<SaveRow> Saves => Set<SaveRow>();
    public DbSet<CommandRow> Commands => Set<CommandRow>();
    public DbSet<AppliedEffect> Effects => Set<AppliedEffect>();
    public DbSet<OutgoingEvent> Outgoing => Set<OutgoingEvent>();
    protected override void OnModelCreating(ModelBuilder b) {
        b.Entity<SaveRow>().HasIndex(x => new { x.UserId, x.Demo }).IsUnique();
        b.Entity<SaveRow>().Property(x => x.Version).IsConcurrencyToken();
        b.Entity<OutgoingEvent>().HasIndex(x => x.EventId).IsUnique();
    }
}
public sealed class GameStore(IDbContextFactory<GameDb> factory, ILogger<GameStore> logger)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<GameState> Current(Guid user, bool demo, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try {
            await using var db = await factory.CreateDbContextAsync(ct);
            var row = await db.Saves.SingleOrDefaultAsync(x => x.UserId == user && x.Demo == demo, ct);
            if (row is not null) return GameJson.Read<GameState>(row.Json);
            var state = new GameState { UserId = user, Demo = demo };
            db.Saves.Add(new() { Id = state.SessionId, UserId = user, Demo = demo, Json = GameJson.Write(state) });
            await db.SaveChangesAsync(ct); return state;
        } finally { gate.Release(); }
    }
    public async Task<GameState> Execute(Guid user, bool demo, GameCommand command, CancellationToken ct)
    {
        await Current(user, demo, ct);
        await gate.WaitAsync(ct);
        try {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var row = await db.Saves.SingleAsync(x => x.UserId == user && x.Demo == demo, ct);
            var previous = await db.Commands.FindAsync([command.CommandId], ct);
            var request = GameJson.Write(command);
            if (previous is not null) {
                if (previous.UserId != user || previous.SessionId != row.Id || previous.Request != request) throw new GameRuleException("CommandId уже использован другой командой.", 409);
                return GameJson.Read<GameState>(previous.Result);
            }
            var change = GameRules.Execute(GameJson.Read<GameState>(row.Json), command, DateTimeOffset.UtcNow);
            row.Json = GameJson.Write(change.State); row.Version = change.State.Version;
            db.Commands.Add(new() { Id = command.CommandId, UserId = user, SessionId = row.Id, Request = request, Result = row.Json });
            foreach (var e in change.Events) db.Outgoing.Add(new() { EventId = e.EventId, Json = GameJson.Write(e) });
            await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct);
            logger.LogInformation("Client {Client} → {Command}; Publisher queued {EventType} session {Session}", command.ClientId, command.Type, change.Events[0].EventType, row.Id);
            return change.State;
        } finally { gate.Release(); }
    }
    public async Task<GameState> Reset(Guid user, bool demo, Guid expectedSession, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var row = await db.Saves.SingleOrDefaultAsync(x => x.UserId == user && x.Demo == demo, ct);
            if (row is null || row.Id != expectedSession) throw new GameRuleException("Сохранение уже изменилось.", 409);
            db.Saves.Remove(row); await db.SaveChangesAsync(ct);
            var state = new GameState { UserId = user, Demo = demo };
            db.Saves.Add(new() { Id = state.SessionId, UserId = user, Demo = demo, Json = GameJson.Write(state) });
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return state;
        } finally { gate.Release(); }
    }
    public async Task<object> Apply(GameEvent e, CancellationToken ct)
    {
        if (e.EventType != "AbilityUsed" || e.Data.OperationId is null) throw new GameRuleException("Некорректный эффект.");
        await gate.WaitAsync(ct);
        try {
            await using var db = await factory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (await db.Effects.AnyAsync(x => x.Id == e.EventId, ct)) return new { status = "Duplicate" };
            var row = await db.Saves.SingleOrDefaultAsync(x => x.Id == e.SessionId && x.UserId == e.UserId, ct);
            if (row is null) return new { status = "Superseded" }; // Old save after an explicit reset.
            var change = GameRules.ApplyAttraction(GameJson.Read<GameState>(row.Json), e.Data.OperationId.Value);
            row.Json = GameJson.Write(change); row.Version = change.Version;
            db.Effects.Add(new() { Id = e.EventId, SessionId = row.Id });
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            return new { status = change.Operations.Single(x => x.Id == e.Data.OperationId).Status };
        } finally { gate.Release(); }
    }
}
public sealed record ResetRequest(Guid SessionId);
public sealed record LabControlRequest(string Subscriber, SubscriberControl Control);
