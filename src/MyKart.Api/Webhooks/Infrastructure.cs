using System.Threading.Channels;

namespace MyKart.Api.Webhooks;

/// <summary>Settings for webhooks MyKart RECEIVES from Stripe.</summary>
public sealed class StripeOptions
{
    /// <summary>The endpoint signing secret (whsec_...) from the Stripe Dashboard or <c>stripe listen</c>.</summary>
    public string WebhookSecret { get; set; } = "";
}

/// <summary>Settings for webhooks MyKart SENDS to its subscribers.</summary>
public sealed class WebhookOptions
{
    public TimeSpan DisableEndpointAfter { get; set; } = TimeSpan.FromDays(3);
    public int DispatchBatchSize { get; set; } = 20;
    public bool AllowPrivateNetworks { get; set; }
    /// <summary>Max inbox processing attempts before an inbound message is abandoned.</summary>
    public int MaxInboxAttempts { get; set; } = 10;
}

/// <summary>Standard envelope for MyKart's own events: { "type": "...", "timestamp": "...", "data": { ... } }</summary>
public sealed record EventEnvelope<T>(string Type, DateTimeOffset Timestamp, T Data);

/// <summary>
/// A "doorbell" for background workers. Producers ring it; the worker wakes up and reads the database.
/// The database stays the source of truth, so a missed ring only delays work until the next poll.
/// </summary>
public sealed class WorkSignal<TWorker>
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Ring() => _channel.Writer.TryWrite(true);

    public async Task WaitAsync(TimeSpan maxWait, CancellationToken stoppingToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeout.CancelAfter(maxWait);
        try
        {
            await _channel.Reader.ReadAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            // Nobody rang within maxWait – time for a regular poll.
        }
    }
}
