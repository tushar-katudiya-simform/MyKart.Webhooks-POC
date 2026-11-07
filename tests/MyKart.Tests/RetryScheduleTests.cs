using MyKart.Api.Outbound;
using Xunit;

namespace MyKart.Tests;

public class RetryScheduleTests
{
    [Fact]
    public void Attempt_0_returns_null_dead_letters_immediately()
    {
        Assert.Null(RetrySchedule.NextDelay(0));
    }

    [Fact]
    public void Attempt_1_returns_delay_near_5_seconds()
    {
        var delay = RetrySchedule.NextDelay(1);

        Assert.NotNull(delay);
        // 5s base + up to 20% jitter = between 5s and 6s
        Assert.InRange(delay!.Value.TotalSeconds, 5.0, 6.1);
    }

    [Fact]
    public void Attempt_9_is_the_last_valid_attempt()
    {
        var delay = RetrySchedule.NextDelay(9);

        Assert.NotNull(delay);
        // 24h base + up to 20% jitter
        Assert.InRange(delay!.Value.TotalHours, 24.0, 28.9);
    }

    [Fact]
    public void Attempt_10_returns_null_dead_letters()
    {
        Assert.Null(RetrySchedule.NextDelay(10));
    }

    [Fact]
    public void RetryAfter_header_overrides_schedule_when_longer()
    {
        var longBackoff = TimeSpan.FromHours(2);

        // Attempt 1 normally delays ~5s — a 2h Retry-After should win
        var delay = RetrySchedule.NextDelay(1, longBackoff);

        Assert.Equal(longBackoff, delay);
    }

    [Fact]
    public void RetryAfter_header_ignored_when_shorter_than_schedule()
    {
        var shortBackoff = TimeSpan.FromSeconds(1);

        var delay = RetrySchedule.NextDelay(1, shortBackoff);

        // Schedule delay (~5s) is longer, so Retry-After is ignored
        Assert.NotNull(delay);
        Assert.True(delay!.Value > shortBackoff);
    }
}
