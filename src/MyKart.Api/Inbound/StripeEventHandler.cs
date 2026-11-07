using MyKart.Api.Data;
using MyKart.Api.Outbound;
using Stripe;

namespace MyKart.Api.Inbound;

public sealed record OrderPaid(string OrderId, decimal Total, string Currency, DateTimeOffset PaidAt);

public sealed record OrderPaymentFailed(string OrderId, string? Reason);

/// <summary>Applies a stored Stripe event to the order it belongs to.</summary>
public sealed class StripeEventHandler(MyKartDb db, WebhookPublisher publisher, ILogger<StripeEventHandler> log)
{
    public async Task HandleAsync(InboxMessage message, CancellationToken ct)
    {
        // The signature was checked when the event arrived; here we only parse the stored JSON.
        var stripeEvent = EventUtility.ParseEvent(message.Payload, throwOnApiVersionMismatch: false);

        if (stripeEvent.Data.Object is not PaymentIntent intent ||
            intent.Metadata is null ||
            !intent.Metadata.TryGetValue("order_id", out var orderId))
        {
            log.LogInformation("Ignoring {Type} {EventId}: not a MyKart payment", stripeEvent.Type, stripeEvent.Id);
            return;
        }

        var order = await db.Orders.FindAsync([orderId], ct)
                    ?? throw new InvalidOperationException($"Unknown order {orderId}");

        // The order's state machine decides what an event means. Stripe doesn't guarantee ordering
        // and may deliver an event twice, so duplicates and late events must be harmless no-ops.
        switch (stripeEvent.Type)
        {
            case "payment_intent.succeeded" when order.Status is OrderStatus.AwaitingPayment or OrderStatus.PaymentFailed:
                if (intent.Amount != order.TotalInMinorUnits ||
                    !string.Equals(intent.Currency, order.Currency, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Amount mismatch on {order.Id}: needs manual review");

                order.Status = OrderStatus.Paid;
                order.PaidAt = DateTime.SpecifyKind(stripeEvent.Created, DateTimeKind.Utc);
                await publisher.StageAsync("order.paid",
                    new OrderPaid(order.Id, order.Total, order.Currency, order.PaidAt.Value), ct);
                break;

            case "payment_intent.payment_failed" when order.Status is OrderStatus.AwaitingPayment:
                order.Status = OrderStatus.PaymentFailed;
                await publisher.StageAsync("order.payment_failed",
                    new OrderPaymentFailed(order.Id, intent.LastPaymentError?.Message), ct);
                break;

            default:
                log.LogInformation("Ignoring {Type} for order {OrderId} in state {Status}",
                    stripeEvent.Type, order.Id, order.Status);
                break;
        }
    }
}
