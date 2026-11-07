using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using MyKart.Api.Data;
using MyKart.Api.Webhooks;

namespace MyKart.Api.Outbound;

/// <summary>Works through the WebhookDeliveries outbox: claim → send → record outcome.</summary>
public sealed class WebhookDispatcher(
    IServiceScopeFactory scopes,
    IHttpClientFactory httpClients,
    TimeProvider clock,
    IOptions<WebhookOptions> options,
    WorkSignal<WebhookDispatcher> signal,
    ILogger<WebhookDispatcher> log) : BackgroundService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(2);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var claimed = 0;
            try
            {
                claimed = await DispatchBatchAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogError(ex, "Webhook dispatch loop failed");
            }

            if (claimed < options.Value.DispatchBatchSize)                    // outbox drained
                await signal.WaitAsync(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    private async Task<int> DispatchBatchAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MyKartDb>();
        var now = clock.GetUtcNow();
        var leaseId = Guid.NewGuid();
        var batchSize = options.Value.DispatchBatchSize;

        // 1. Claim due rows in ONE statement. READPAST makes other instances skip rows we hold,
        //    and the lease keeps them away while we are busy on the network.
        var claimedIds = await db.Database.SqlQuery<Guid>($"""
            WITH due AS (
                SELECT TOP ({batchSize}) d.Id, d.LeaseId, d.LeaseExpiresAt
                FROM WebhookDeliveries d WITH (ROWLOCK, UPDLOCK, READPAST)
                JOIN WebhookSubscriptions s ON s.Id = d.SubscriptionId
                WHERE d.Status = 'Pending' AND s.IsActive = 1
                  AND d.NextAttemptAt <= {now}
                  AND (d.LeaseExpiresAt IS NULL OR d.LeaseExpiresAt < {now})
                ORDER BY d.NextAttemptAt)
            UPDATE due SET LeaseId = {leaseId}, LeaseExpiresAt = {now + LeaseDuration}
            OUTPUT inserted.Id AS Value;
            """).ToListAsync(ct);

        if (claimedIds.Count == 0)
            return 0;

        var batch = await db.Deliveries
            .Include(d => d.Subscription)
            .Where(d => d.LeaseId == leaseId)
            .ToListAsync(ct);

        // 2. Send in parallel. No DbContext calls in here – DbContext is not thread-safe.
        var results = await Task.WhenAll(batch.Select(d => SendAsync(d, ct)));

        // 3. Record every outcome, then persist the whole batch in one SaveChanges.
        for (var i = 0; i < batch.Count; i++)
            Apply(batch[i], results[i], clock.GetUtcNow());

        await db.SaveChangesAsync(ct);
        return claimedIds.Count;
    }

    private async Task<AttemptResult> SendAsync(WebhookDelivery delivery, CancellationToken ct)
    {
        var subscription = delivery.Subscription;
        var body = Encoding.UTF8.GetBytes(delivery.Payload);
        var now = clock.GetUtcNow();
        var timestamp = now.ToUnixTimeSeconds();

        // Fresh timestamp + signature on every attempt; the webhook-id never changes.
        var signature = StandardWebhook.Sign(subscription.Secret, delivery.MessageId, timestamp, body);
        if (subscription.PreviousSecret is { } previous && subscription.PreviousSecretExpiresAt > now)
            signature += " " + StandardWebhook.Sign(previous, delivery.MessageId, timestamp, body);

        using var request = new HttpRequestMessage(HttpMethod.Post, subscription.Url)
        {
            Content = new ByteArrayContent(body)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Add("webhook-id", delivery.MessageId);
        request.Headers.Add("webhook-timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("webhook-signature", signature);

        try
        {
            // ResponseHeadersRead: we only need the status code, never the receiver's body.
            using var response = await httpClients.CreateClient("webhooks")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            return AttemptResult.FromResponse(response, now);
        }
        catch (BrokenCircuitException)
        {
            return AttemptResult.CircuitOpen;            // receiver is known-bad right now – don't burn an attempt
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            return AttemptResult.Failure($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void Apply(WebhookDelivery delivery, AttemptResult result, DateTimeOffset now)
    {
        var subscription = delivery.Subscription;
        delivery.LeaseId = null;
        delivery.LeaseExpiresAt = null;

        if (result.Outcome == AttemptOutcome.CircuitOpen)
        {
            delivery.NextAttemptAt = now.AddSeconds(30);
            return;
        }

        delivery.AttemptCount++;
        delivery.LastStatusCode = result.StatusCode;
        delivery.LastError = result.Error;

        switch (result.Outcome)
        {
            case AttemptOutcome.Delivered:
                delivery.Status = DeliveryStatus.Succeeded;
                delivery.DeliveredAt = now;
                subscription.FailingSince = null;
                break;

            case AttemptOutcome.EndpointGone:
                delivery.Status = DeliveryStatus.DeadLettered;
                subscription.Disable("Receiver answered 410 Gone");
                break;

            case AttemptOutcome.Failed:
                subscription.FailingSince ??= now;
                if (now - subscription.FailingSince > options.Value.DisableEndpointAfter)
                    subscription.Disable($"Failing continuously since {subscription.FailingSince:u}");

                if (RetrySchedule.NextDelay(delivery.AttemptCount, result.RetryAfter) is { } delay)
                    delivery.NextAttemptAt = now + delay;
                else
                    delivery.Status = DeliveryStatus.DeadLettered;

                log.LogWarning("Delivery {MessageId} to {Url} failed ({Error}); attempt {Attempt}, status {Status}",
                    delivery.MessageId, subscription.Url, result.Error, delivery.AttemptCount, delivery.Status);
                break;
        }
    }
}
