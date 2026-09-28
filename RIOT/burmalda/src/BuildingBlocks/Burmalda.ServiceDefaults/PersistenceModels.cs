using Microsoft.EntityFrameworkCore;

namespace Burmalda.ServiceDefaults;

public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public required string Exchange { get; set; }
    public required string RoutingKey { get; set; }
    public required string Type { get; set; }
    public required string Payload { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public int RetryCount { get; set; }
    public string? LastError { get; set; }
}

public sealed class InboxMessage
{
    public Guid MessageId { get; set; }
    public required string Consumer { get; set; }
    public DateTimeOffset ProcessedAt { get; set; }
}

public static class MessagingModelBuilderExtensions
{
    public static void AddMessagingModels(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.ToTable("outbox_messages");
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.PublishedAt, x.OccurredAt });
            entity.Property(x => x.Exchange).HasMaxLength(100);
            entity.Property(x => x.RoutingKey).HasMaxLength(200);
            entity.Property(x => x.Type).HasMaxLength(200);
            entity.Property(x => x.Payload).HasColumnType("jsonb");
        });
        modelBuilder.Entity<InboxMessage>(entity =>
        {
            entity.ToTable("inbox_messages");
            entity.HasKey(x => new { x.MessageId, x.Consumer });
            entity.Property(x => x.Consumer).HasMaxLength(200);
        });
    }
}
