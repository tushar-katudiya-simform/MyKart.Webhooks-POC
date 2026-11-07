using System.Security.Cryptography;
using System.Text;

namespace MyKart.Api.Webhooks;

/// <summary>
/// The Standard Webhooks signing scheme (https://www.standardwebhooks.com):
/// signature = base64( HMAC-SHA256( secret, "{webhook-id}.{webhook-timestamp}.{raw body}" ) )
/// </summary>
public static class StandardWebhook
{
    private const string SecretPrefix = "whsec_";

    public static string NewSecret() =>
        SecretPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static string Sign(string secret, string messageId, long timestamp, ReadOnlySpan<byte> body) =>
        "v1," + Convert.ToBase64String(ComputeHmac(DecodeSecret(secret), messageId, timestamp, body));

    public static VerificationResult Verify(
        IReadOnlyList<string> secrets,
        string? messageId,
        string? timestampHeader,
        string? signatureHeader,
        ReadOnlySpan<byte> body,
        DateTimeOffset now,
        TimeSpan tolerance)
    {
        if (string.IsNullOrEmpty(messageId) || string.IsNullOrEmpty(signatureHeader) ||
            !long.TryParse(timestampHeader, out var timestamp))
            return VerificationResult.Fail("Missing or malformed webhook headers");

        // A valid signature on an old message is still a replay attack.
        if ((now - DateTimeOffset.FromUnixTimeSeconds(timestamp)).Duration() > tolerance)
            return VerificationResult.Fail("Timestamp outside the allowed tolerance");

        Span<byte> candidate = stackalloc byte[32];

        foreach (var secret in secrets)                       // current secret first, then the previous one
        {
            var expected = ComputeHmac(DecodeSecret(secret), messageId, timestamp, body);

            // The header can carry several signatures: "v1,abc= v1,def="
            foreach (var signature in signatureHeader.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (!signature.StartsWith("v1,", StringComparison.Ordinal))
                    continue;                                 // unknown scheme – skip, don't fail

                if (Convert.TryFromBase64String(signature[3..], candidate, out var written) &&
                    written == expected.Length &&
                    CryptographicOperations.FixedTimeEquals(candidate[..written], expected))
                    return VerificationResult.Ok;
            }
        }

        return VerificationResult.Fail("No matching signature");
    }

    private static byte[] ComputeHmac(byte[] key, string messageId, long timestamp, ReadOnlySpan<byte> body)
    {
        // IncrementalHash lets us sign "id.timestamp." + body without copying the body into a new array.
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        hmac.AppendData(Encoding.UTF8.GetBytes($"{messageId}.{timestamp}."));
        hmac.AppendData(body);
        return hmac.GetHashAndReset();
    }

    private static byte[] DecodeSecret(string secret) =>
        Convert.FromBase64String(secret.StartsWith(SecretPrefix, StringComparison.Ordinal)
            ? secret[SecretPrefix.Length..]
            : secret);
}

public readonly record struct VerificationResult(bool IsValid, string? Error)
{
    public static VerificationResult Ok => new(true, null);
    public static VerificationResult Fail(string error) => new(false, error);
}
