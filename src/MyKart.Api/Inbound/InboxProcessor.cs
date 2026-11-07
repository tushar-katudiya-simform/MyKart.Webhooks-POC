using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MyKart.Api.Data;
using MyKart.Api.Outbound;
using MyKart.Api.Webhooks;

namespace MyKart.Api.Inbound;

/// <summary>Turns stored inbound webhooks into business changes, one message per transaction.</summary>
public sealed class InboxProcessor(
    IServiceScopeFactory scopes,
    TimeProvider clock,
    WorkSignal<InboxProcessor> inboxSignal,
    WorkSignal<WebhookDispatcher> dispatchSignal,
    IOptions<WebhookOptions> options,
    ILogger<InboxProcessor> log) : BackgroundService
{
    private const int BatchSize = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var found = 0;
            try
            {
                List<long> due;
                await using (var scope = scopes.CreateAsyncScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<MyKartDb>();
                    var now = clock.GetUtcNow();
                    due = await db.Inbox
                        .Where(m => m.ProcessedAt == null && m.Attempts < options.Value.MaxInboxAttempts && m.NextAttemptAt <= now)
                        .OrderBy(m => m.Id)
                        .Select(m => m.Id)
                        .Take(BatchSize)
                        .ToListAsync(stoppingToken);
                }

                found = due.Count;
                foreach (var id in due)
                    await ProcessOneAsync(id, stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                log.LogError(ex, "Inbox sweep failed");
            }

            if (found < BatchSize)
                await inboxSignal.WaitAsync(TimeSpan.FromSeconds(30), stoppingToken);   // doorbell or 30s poll
        }
    }

    private async Task ProcessOneAsync(long id, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MyKartDb>();
        var handler = scope.ServiceProvider.GetRequiredService<StripeEventHandler>();

        var message = await db.Inbox.SingleAsync(m => m.Id == id, ct);
        try
        {
            await handler.HandleAsync(message, ct);     // changes the order + stages outbound webhooks
            message.ProcessedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);              // ONE transaction: order + inbox + outbox
            dispatchSignal.Ring();
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            var attempt = message.Attempts + 1;
            var retryAt = clock.GetUtcNow().AddSeconds(5 * Math.Pow(2, attempt));
            log.LogWarning(ex, "Inbox message {Id} failed on attempt {Attempt}", id, attempt);

            db.ChangeTracker.Clear();                   // throw away the half-applied business changes
            await db.Inbox.Where(m => m.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(m => m.Attempts, attempt)
                .SetProperty(m => m.LastError, ex.Message)
                .SetProperty(m => m.NextAttemptAt, retryAt), ct);
        }
    }
}
