using System.Text;
using MyKart.Api.Webhooks;
using Xunit;

namespace MyKart.Tests;

public class StandardWebhookTests
{
    private static readonly string Secret = StandardWebhook.NewSecret();
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"type\":\"order.paid\"}");
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    [Fact]
    public void Sign_then_Verify_roundtrip_succeeds()
    {
        var messageId = "msg_01";
        var timestamp = Now.ToUnixTimeSeconds();

        var signature = StandardWebhook.Sign(Secret, messageId, timestamp, Body);

        var result = StandardWebhook.Verify(
            [Secret], messageId, timestamp.ToString(), signature, Body, Now, Tolerance);

        Assert.True(result.IsValid);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Verify_fails_when_body_tampered()
    {
        var messageId = "msg_02";
        var timestamp = Now.ToUnixTimeSeconds();
        var signature = StandardWebhook.Sign(Secret, messageId, timestamp, Body);

        var tamperedBody = Encoding.UTF8.GetBytes("{\"type\":\"order.refunded\"}");

        var result = StandardWebhook.Verify(
            [Secret], messageId, timestamp.ToString(), signature, tamperedBody, Now, Tolerance);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Verify_fails_when_outside_tolerance()
    {
        var messageId = "msg_03";
        var staleTimestamp = Now.AddMinutes(-6).ToUnixTimeSeconds();
        var signature = StandardWebhook.Sign(Secret, messageId, staleTimestamp, Body);

        var result = StandardWebhook.Verify(
            [Secret], messageId, staleTimestamp.ToString(), signature, Body, Now, Tolerance);

        Assert.False(result.IsValid);
        Assert.Contains("tolerance", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Verify_fails_when_signature_header_missing()
    {
        var result = StandardWebhook.Verify(
            [Secret], "msg_04", Now.ToUnixTimeSeconds().ToString(), null, Body, Now, Tolerance);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void Verify_accepts_previous_secret_during_rotation()
    {
        var oldSecret = StandardWebhook.NewSecret();
        var newSecret = StandardWebhook.NewSecret();

        var messageId = "msg_05";
        var timestamp = Now.ToUnixTimeSeconds();
        var signature = StandardWebhook.Sign(oldSecret, messageId, timestamp, Body);

        // Both secrets accepted — simulates overlap window after rotation
        var result = StandardWebhook.Verify(
            [newSecret, oldSecret], messageId, timestamp.ToString(), signature, Body, Now, Tolerance);

        Assert.True(result.IsValid);
    }
}
