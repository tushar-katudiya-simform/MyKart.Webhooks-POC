namespace MyKart.Api.Data;

public enum OrderStatus { AwaitingPayment, Paid, PaymentFailed }

public enum DeliveryStatus { Pending, Succeeded, DeadLettered }

public sealed class Order
{
    public required string Id { get; init; }
    public required string CustomerEmail { get; init; }
    public decimal Total { get; init; }
    public string Currency { get; init; } = "USD";
    public OrderStatus Status { get; set; } = OrderStatus.AwaitingPayment;
    public DateTimeOffset? PaidAt { get; set; }
    public byte[] Version { get; set; } = [];          // rowversion: stops two workers paying the same order twice

    public long TotalInMinorUnits => (long)(Total * 100);
}

/// <summary>Every inbound webhook lands here first: verified, raw and de-duplicated.</summary>
public sealed class InboxMessage
{
    public long Id { get; init; }
    public required string Source { get; init; }            // "stripe"
    public required string WebhookId { get; init; }         // Stripe event ID (evt_...)
    public required string EventType { get; init; }
    public required string Payload { get; init; }           // raw JSON, exactly as received
    public DateTimeOffset ReceivedAt { get; init; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public int Attempts { get; set; }
    public string? LastError { get; set; }
}

/// <summary>A partner system that asked to be told when something happens in MyKart.</summary>
public sealed class WebhookSubscription
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public required string Url { get; init; }
    public required string[] EventTypes { get; init; }
    public required string Secret { get; set; }
    public string? PreviousSecret { get; set; }               // still signed with during rotation
    public DateTimeOffset? PreviousSecretExpiresAt { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? FailingSince { get; set; }
    public string? DisabledReason { get; set; }
    public DateTimeOffset CreatedAt { get; init; }

    public void Disable(string reason)
    {
        IsActive = false;
        DisabledReason = reason;
    }
}

/// <summary>The outbox: one row per (event, subscriber). The dispatcher works through this table.</summary>
public sealed class WebhookDelivery
{
    public Guid Id { get; init; } = Guid.CreateVersion7();
    public Guid SubscriptionId { get; init; }
    public WebhookSubscription Subscription { get; init; } = null!;
    public required string MessageId { get; init; }          // webhook-id: identical on every retry
    public required string EventType { get; init; }
    public required string Payload { get; init; }
    public DeliveryStatus Status { get; set; } = DeliveryStatus.Pending;
    public int AttemptCount { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public int? LastStatusCode { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? DeliveredAt { get; set; }
}
