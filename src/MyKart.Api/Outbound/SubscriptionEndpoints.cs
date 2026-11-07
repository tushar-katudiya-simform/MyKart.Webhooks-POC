using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MyKart.Api.Data;
using MyKart.Api.Webhooks;

namespace MyKart.Api.Outbound;

public sealed record CreateSubscriptionRequest(
    [Required, Url, MaxLength(2048)] string Url,
    [Required, MinLength(1)] string[] EventTypes) : IValidatableObject
{
    public static readonly string[] KnownEvents = ["order.paid", "order.payment_failed"];

    public IEnumerable<ValidationResult> Validate(ValidationContext context)
    {
        foreach (var type in EventTypes.Except(KnownEvents))
            yield return new ValidationResult($"Unknown event type '{type}'.", [nameof(EventTypes)]);
    }
}

public sealed record SubscriptionCreated(Guid Id, string Url, string[] EventTypes, string Secret);

public static class SubscriptionEndpoints
{
    public static IEndpointRouteBuilder MapSubscriptionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/webhook-subscriptions");     // add .RequireAuthorization() in real life

        group.MapPost("/", CreateAsync);
        group.MapPost("/{id:guid}/rotate-secret", RotateSecretAsync);
        group.MapGet("/{id:guid}/deliveries", GetDeliveriesAsync);
        group.MapPost("/deliveries/{deliveryId:guid}/replay", ReplayAsync);

        return app;
    }

    // .NET 10 validates CreateSubscriptionRequest (attributes + IValidatableObject) before this runs.
    private static async Task<Results<Created<SubscriptionCreated>, ValidationProblem>> CreateAsync(
        CreateSubscriptionRequest request, MyKartDb db, IOptions<WebhookOptions> options,
        TimeProvider clock, CancellationToken ct)
    {
        if (!request.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && !options.Value.AllowPrivateNetworks)
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["url"] = ["Webhook URLs must use HTTPS."]
            });

        var subscription = new WebhookSubscription
        {
            Url = request.Url,
            EventTypes = request.EventTypes,
            Secret = StandardWebhook.NewSecret(),
            CreatedAt = clock.GetUtcNow()
        };

        db.Subscriptions.Add(subscription);
        await db.SaveChangesAsync(ct);

        // Like an API key, the secret is shown exactly once.
        return TypedResults.Created($"/api/webhook-subscriptions/{subscription.Id}",
            new SubscriptionCreated(subscription.Id, subscription.Url, subscription.EventTypes, subscription.Secret));
    }

    // Zero-downtime rotation: for 24h every delivery carries two signatures (new + old).
    private static async Task<IResult> RotateSecretAsync(Guid id, MyKartDb db, TimeProvider clock, CancellationToken ct)
    {
        var subscription = await db.Subscriptions.FindAsync([id], ct);
        if (subscription is null)
            return TypedResults.NotFound();

        subscription.PreviousSecret = subscription.Secret;
        subscription.PreviousSecretExpiresAt = clock.GetUtcNow().AddHours(24);
        subscription.Secret = StandardWebhook.NewSecret();
        await db.SaveChangesAsync(ct);

        return TypedResults.Ok(new { subscription.Id, subscription.Secret, subscription.PreviousSecretExpiresAt });
    }

    private static async Task<IResult> GetDeliveriesAsync(Guid id, MyKartDb db, CancellationToken ct) =>
        TypedResults.Ok(await db.Deliveries
            .Where(d => d.SubscriptionId == id)
            .OrderByDescending(d => d.CreatedAt)
            .Take(50)
            .Select(d => new { d.Id, d.MessageId, d.EventType, d.Status, d.AttemptCount, d.LastStatusCode, d.LastError, d.NextAttemptAt, d.DeliveredAt })
            .ToListAsync(ct));

    // Dead letters are not the end of the road: once the receiver is fixed, replay them.
    private static async Task<IResult> ReplayAsync(
        Guid deliveryId, MyKartDb db, TimeProvider clock, WorkSignal<WebhookDispatcher> signal, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var updated = await db.Deliveries
            .Where(d => d.Id == deliveryId && d.Status == DeliveryStatus.DeadLettered)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, DeliveryStatus.Pending)
                .SetProperty(d => d.AttemptCount, 0)
                .SetProperty(d => d.NextAttemptAt, now), ct);

        if (updated == 0)
            return TypedResults.NotFound();

        signal.Ring();
        return TypedResults.Accepted($"/api/webhook-subscriptions/deliveries/{deliveryId}");
    }
}
