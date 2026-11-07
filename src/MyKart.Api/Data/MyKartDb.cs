using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace MyKart.Api.Data;

public sealed class MyKartDb(DbContextOptions<MyKartDb> options) : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<InboxMessage> Inbox => Set<InboxMessage>();
    public DbSet<WebhookSubscription> Subscriptions => Set<WebhookSubscription>();
    public DbSet<WebhookDelivery> Deliveries => Set<WebhookDelivery>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<Order>(e =>
        {
            e.Property(o => o.Id).HasMaxLength(32);
            e.Property(o => o.Total).HasPrecision(18, 2);
            e.Property(o => o.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(o => o.Version).IsRowVersion();
        });

        model.Entity<InboxMessage>(e =>
        {
            e.ToTable("InboxMessages");
            e.Property(m => m.Source).HasMaxLength(32);
            e.Property(m => m.WebhookId).HasMaxLength(128);
            e.Property(m => m.EventType).HasMaxLength(100);
            e.HasIndex(m => new { m.Source, m.WebhookId }).IsUnique();          // the idempotency guard
            e.HasIndex(m => m.NextAttemptAt).HasFilter("[ProcessedAt] IS NULL");
        });

        model.Entity<WebhookSubscription>(e =>
        {
            e.ToTable("WebhookSubscriptions");
            e.Property(s => s.Url).HasMaxLength(2048);
            e.Property(s => s.Secret).HasMaxLength(100);
            e.Property(s => s.PreviousSecret).HasMaxLength(100);
        });

        model.Entity<WebhookDelivery>(e =>
        {
            e.ToTable("WebhookDeliveries");
            e.Property(d => d.MessageId).HasMaxLength(64);
            e.Property(d => d.EventType).HasMaxLength(100);
            e.Property(d => d.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(d => new { d.Status, d.NextAttemptAt });
            e.HasIndex(d => d.LeaseId);
        });
    }
}

public static class DbUpdateExceptionExtensions
{
    // C# 14 extension members: an extension *property* instead of a helper method.
    extension(DbUpdateException exception)
    {
        public bool IsUniqueViolation =>
            exception.InnerException is SqlException { Number: 2601 or 2627 };
    }
}
