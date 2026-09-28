using Burmalda.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

namespace Burmalda.Notifications.Api;

public sealed class Notification
{
    public Guid Id { get; set; } public Guid UserId { get; set; } public required string Type { get; set; } public required string Title { get; set; }
    public required string Body { get; set; } public string? DataJson { get; set; } public DateTimeOffset CreatedAt { get; set; } public DateTimeOffset? ReadAt { get; set; }
}
public sealed class NotificationDbContext(DbContextOptions<NotificationDbContext> options) : DbContext(options)
{
    public DbSet<Notification> Notifications => Set<Notification>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Notification>(e => { e.ToTable("notifications"); e.HasKey(x => x.Id); e.HasIndex(x => new { x.UserId, x.ReadAt, x.CreatedAt }); e.Property(x => x.DataJson).HasColumnType("jsonb"); });
        modelBuilder.AddMessagingModels();
    }
}
