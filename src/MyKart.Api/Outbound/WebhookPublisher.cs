using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MyKart.Api.Data;
using MyKart.Api.Webhooks;

namespace MyKart.Api.Outbound;

/// <summary>
/// Stages one delivery row per interested subscriber inside the CURRENT unit of work.
/// Nothing goes over the network here – the dispatcher only sees rows after the transaction commits.
/// </summary>
public sealed class WebhookPublisher(MyKartDb db, TimeProvider clock)
{
    public async Task StageAsync<T>(string eventType, T data, CancellationToken ct)
    {
        var subscriberIds = await db.Subscriptions
            .Where(s => s.IsActive && s.EventTypes.Contains(eventType))
            .Select(s => s.Id)
            .ToListAsync(ct);

        if (subscriberIds.Count == 0)
            return;

        var now = clock.GetUtcNow();
        var payload = JsonSerializer.Serialize(new EventEnvelope<T>(eventType, now, data), JsonSerializerOptions.Web);

        foreach (var subscriptionId in subscriberIds)
        {
            db.Deliveries.Add(new WebhookDelivery
            {
                SubscriptionId = subscriptionId,
                MessageId = $"msg_{Guid.CreateVersion7():N}",
                EventType = eventType,
                Payload = payload,
                NextAttemptAt = now,
                CreatedAt = now
            });
        }
    }
}
