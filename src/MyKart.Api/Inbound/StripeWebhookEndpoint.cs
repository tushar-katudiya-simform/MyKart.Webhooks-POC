using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MyKart.Api.Data;
using MyKart.Api.Webhooks;
using Stripe;

namespace MyKart.Api.Inbound;

public static class StripeWebhookEndpoint
{
    private const int MaxPayloadBytes = 512 * 1024;

    public static IEndpointRouteBuilder MapStripeWebhooks(this IEndpointRouteBuilder app)
    {
        app.MapPost("/webhooks/stripe", HandleAsync).ExcludeFromDescription();
        return app;
    }

    private static async Task<IResult> HandleAsync(
        HttpContext http,
        MyKartDb db,
        IOptions<StripeOptions> stripe,
        TimeProvider clock,
        WorkSignal<InboxProcessor> inboxSignal,
        ILogger<InboxProcessor> log,
        CancellationToken ct)
    {
        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = MaxPayloadBytes;

        // 1. Read the RAW body. Stripe signs the exact bytes it sent; any re-serialisation breaks the check.
        using var reader = new StreamReader(http.Request.Body, Encoding.UTF8);
        var json = await reader.ReadToEndAsync(ct);

        // 2. Verify the Stripe-Signature header: HMAC-SHA256 over "{t}.{body}", 5-minute tolerance.
        //    throwOnApiVersionMismatch: false – the event's API version follows your Stripe account /
        //    endpoint settings, not the version Stripe.net was built for. Don't reject real events for that.
        Event stripeEvent;
        try
        {
            stripeEvent = EventUtility.ConstructEvent(
                json, http.Request.Headers["Stripe-Signature"].ToString(), stripe.Value.WebhookSecret,
                throwOnApiVersionMismatch: false);
        }
        catch (StripeException ex)
        {
            log.LogWarning("Rejected Stripe webhook: {Reason}", ex.Message);
            return Results.BadRequest();
        }

        // 3. Store first. The unique index on (Source, WebhookId) makes Stripe's evt_ ID our idempotency key.
        var now = clock.GetUtcNow();
        db.Inbox.Add(new InboxMessage
        {
            Source = "stripe",
            WebhookId = stripeEvent.Id,
            EventType = stripeEvent.Type,
            Payload = json,
            ReceivedAt = now,
            NextAttemptAt = now
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation)
        {
            log.LogInformation("Duplicate Stripe event {EventId} ignored", stripeEvent.Id);
            return Results.Ok();           // already have it – a 2xx tells Stripe to stop retrying
        }

        // 4. Ring the doorbell and answer immediately. Business logic runs in the background.
        inboxSignal.Ring();
        return Results.Accepted();
    }
}
