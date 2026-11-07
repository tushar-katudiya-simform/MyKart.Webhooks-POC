#:sdk Microsoft.NET.Sdk.Web
#:property PublishAot=false

// Warehouse service: a pretend 3PL warehouse that receives MyKart webhooks.
// Run:  dotnet run warehouse.cs -- <whsec_secret> [failFirst]
// failFirst = how many requests to answer with 503 before "recovering".

using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

var key = Convert.FromBase64String(args[0].Replace("whsec_", ""));
var failFirst = args.Length > 1 ? int.Parse(args[1]) : 0;
var processed = new ConcurrentDictionary<string, DateTimeOffset>();
var calls = 0;

var builder = WebApplication.CreateBuilder();
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
var app = builder.Build();

app.MapPost("/hooks/mykart", async (HttpRequest request) =>
{
    using var buffer = new MemoryStream();
    await request.Body.CopyToAsync(buffer);
    var body = buffer.ToArray();

    string id = request.Headers["webhook-id"].ToString();
    string timestamp = request.Headers["webhook-timestamp"].ToString();
    string signatures = request.Headers["webhook-signature"].ToString();

    // 1. Reject stale messages (replay protection).
    if (!long.TryParse(timestamp, out var ts) ||
        Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts) > 300)
        return Results.Unauthorized();

    // 2. Verify the HMAC over "id.timestamp.body" – any one valid signature is enough.
    byte[] signedContent = [.. Encoding.UTF8.GetBytes($"{id}.{timestamp}."), .. body];
    var expected = HMACSHA256.HashData(key, signedContent);
    var valid = signatures.Split(' ').Any(s =>
        s.StartsWith("v1,") &&
        CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(s[3..]), expected));
    if (!valid)
        return Results.Unauthorized();

    // 3. Simulate an outage so we can watch MyKart retry.
    if (Interlocked.Increment(ref calls) <= failFirst)
    {
        Console.WriteLine($"💥 {id}: pretending to be down (503)");
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }

    // 4. Idempotency: the same webhook-id is processed once, no matter how often it arrives.
    if (!processed.TryAdd(id, DateTimeOffset.UtcNow))
    {
        Console.WriteLine($"↩️  {id}: duplicate, already handled");
        return Results.Ok();
    }

    Console.WriteLine($"📦 {id}: {Encoding.UTF8.GetString(body)}");
    return Results.Ok();
});

app.Run("http://localhost:5080");
