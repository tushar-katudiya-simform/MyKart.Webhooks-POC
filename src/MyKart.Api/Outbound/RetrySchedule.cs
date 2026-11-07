using System.Net;

namespace MyKart.Api.Outbound;

public static class RetrySchedule
{
    // Follows the Standard Webhooks example: 10 attempts spread over roughly three days.
    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(2), TimeSpan.FromHours(5), TimeSpan.FromHours(10),
        TimeSpan.FromHours(14), TimeSpan.FromHours(20), TimeSpan.FromHours(24)
    ];

    /// <returns>The delay before the next attempt, or null when it is time to dead-letter.</returns>
    public static TimeSpan? NextDelay(int attemptsMade, TimeSpan? retryAfter = null)
    {
        if (attemptsMade < 1 || attemptsMade > Delays.Length)
            return null;

        var delay = Delays[attemptsMade - 1];
        delay += delay * (Random.Shared.NextDouble() * 0.2);        // up to +20% jitter

        return retryAfter > delay ? retryAfter : delay;               // the receiver asked us to back off
    }
}

public enum AttemptOutcome { Delivered, Failed, EndpointGone, CircuitOpen }

public readonly record struct AttemptResult(
    AttemptOutcome Outcome, int? StatusCode = null, string? Error = null, TimeSpan? RetryAfter = null)
{
    public static AttemptResult CircuitOpen => new(AttemptOutcome.CircuitOpen, Error: "Circuit open");

    public static AttemptResult Failure(string error) => new(AttemptOutcome.Failed, Error: error);

    public static AttemptResult FromResponse(HttpResponseMessage response, DateTimeOffset now)
    {
        var code = (int)response.StatusCode;

        if (response.IsSuccessStatusCode)
            return new(AttemptOutcome.Delivered, code);

        if (response.StatusCode == HttpStatusCode.Gone)               // "stop sending me this"
            return new(AttemptOutcome.EndpointGone, code, "410 Gone");

        var retryAfter = response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - now,
            _ => (TimeSpan?)null
        };

        return new(AttemptOutcome.Failed, code, $"HTTP {code}", retryAfter);
    }
}
