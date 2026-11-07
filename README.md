# MyKart.Webhooks: .NET 10 webhook POC

Companion code for the Medium article **"Webhooks in .NET 10: Building a Sender and a Receiver That Survive Real Traffic"**.

MyKart (a fictional online store) **receives** Stripe webhooks (`payment_intent.succeeded`, `payment_intent.payment_failed`), verified with Stripe.net, and **sends** its own signed `order.paid` webhooks to subscribed services such as a warehouse service. Outbound webhooks follow the [Standard Webhooks](https://www.standardwebhooks.com) spec.

## What's inside

| Path | Purpose |
|---|---|
| `src/MyKart.Api/Inbound/StripeWebhookEndpoint.cs` | Raw body → `Stripe-Signature` check (Stripe.net `EventUtility`) → inbox (unique Stripe event ID) → `202 Accepted` |
| `src/MyKart.Api/Inbound/InboxProcessor.cs` | `BackgroundService` that applies inbox messages, one transaction each, with backoff |
| `src/MyKart.Api/Inbound/StripeEventHandler.cs` | Finds the order via PaymentIntent `metadata.order_id`, checks amount/currency, runs the order state machine, stages outbound events |
| `src/MyKart.Api/Webhooks/StandardWebhook.cs` | HMAC-SHA256 signing/verification for MyKart's own webhooks (`webhook-id`, `webhook-timestamp`, `webhook-signature`) |
| `src/MyKart.Api/Outbound/WebhookPublisher.cs` | Transactional outbox: one `WebhookDeliveries` row per subscriber |
| `src/MyKart.Api/Outbound/WebhookDispatcher.cs` | Claims due rows (UPDLOCK + READPAST + lease), signs, sends, records outcome |
| `src/MyKart.Api/Outbound/RetrySchedule.cs` | 10 attempts over ~3 days, jitter, `Retry-After`, 410 handling |
| `src/MyKart.Api/Outbound/SsrfGuard.cs` | Blocks private/loopback/metadata IPs in `SocketsHttpHandler.ConnectCallback` |
| `src/MyKart.Api/Outbound/SubscriptionEndpoints.cs` | Subscribe (.NET 10 validation), rotate secret, delivery log, replay dead letters |
| `tools/warehouse.cs` | Single-file .NET 10 receiver that verifies, dedupes and can simulate outages |

## Requirements

- .NET 10 SDK
- SQL Server (LocalDB, Developer edition or a container). Update `ConnectionStrings:MyKart` if you are not on LocalDB.
- A Stripe account (a free sandbox is enough) and the [Stripe CLI](https://docs.stripe.com/stripe-cli)
- NuGet: `Microsoft.EntityFrameworkCore.SqlServer` 10.0.14, `Microsoft.Extensions.Http.Resilience` 10.11.0, `Stripe.net` 53.1.0

## Run it

```bash
# 1. Forward Stripe sandbox events to MyKart; copy the whsec_ secret it prints
stripe login
stripe listen --events payment_intent.succeeded,payment_intent.payment_failed \
  --forward-to localhost:5000/webhooks/stripe

# 2. Give MyKart that secret and start it (Development: creates the DB, seeds ORD-1001)
cd src/MyKart.Api
dotnet user-secrets set "Stripe:WebhookSecret" "whsec_..."
dotnet run

# 3. Subscribe the warehouse service and copy the returned "secret"
curl -X POST http://localhost:5000/api/webhook-subscriptions \
  -H "Content-Type: application/json" \
  -d '{"url":"http://localhost:5080/hooks/mykart","eventTypes":["order.paid"]}'

# 4. Start the warehouse service, failing its first 2 requests to show retries
dotnet run tools/warehouse.cs -- <secret-from-step-3> 2

# 5. Make a sandbox payment for ORD-1001 (49.99 USD = 4999 cents)
stripe trigger payment_intent.succeeded \
  --override payment_intent:amount=4999 \
  --add payment_intent:metadata.order_id=ORD-1001
```

## Conclusion

Working through this project makes a few things clear about webhooks that documentation alone rarely spells out:

- **Webhooks are push, not pull.** Unlike a REST API where your code asks for data, webhooks flip the model — the sender calls *you* when something happens. Your endpoint must be reachable 24/7 and respond fast (the sender expects a `2xx` within seconds, not minutes).

- **Never trust a webhook without verifying its signature.** Anyone on the internet can POST to your endpoint. The HMAC-SHA256 signature (the `Stripe-Signature` header inbound, the `webhook-signature` header outbound) is what proves the payload genuinely came from the expected sender and was not tampered with in transit.

- **Responding and processing are two separate steps.** The right answer is always: write the raw payload to an inbox table, return `202 Accepted`, then process it in the background. This decouples your availability from however long your business logic takes, and prevents the sender from timing out and retrying while you are still working.

- **Idempotency is non-negotiable.** Senders retry on network errors, timeouts, and anything that isn't a `2xx`. Your handler will receive the same event more than once. The inbox deduplication (unique `StripeEventId`) and the warehouse's deduplication (unique `webhook-id`) are what make repeated deliveries harmless.

- **Retries belong on the sender, not the receiver.** When your outbound delivery fails (the warehouse returns `503`), the right move is exponential backoff and a retry schedule — not an immediate second attempt. The receiver signals transient failure with a `5xx`; the sender owns the retry clock.

- **A dead-letter queue is the safety net.** After all retry attempts are exhausted, the delivery row is dead-lettered rather than deleted. This gives an operator the chance to inspect what failed and replay it once the downstream system is healthy again, without any data loss.

- **Secret rotation must be zero-downtime.** When a webhook secret changes, there is a window where in-flight events were signed with the old key. The overlap period (here 24 hours) lets both keys be valid simultaneously so the receiver keeps working through a rotation without dropping a single delivery.

## Troubleshooting

**LocalDB not found / Cannot open database**
Switch to a Docker SQL Server connection string in `appsettings.Development.json`:
```json
"ConnectionStrings": {
  "MyKart": "Server=localhost,1433;Database=MyKart;User Id=sa;Password=YourStrong!Passw0rd;TrustServerCertificate=True"
}
```
Then start SQL Server: `docker run -e ACCEPT_EULA=Y -e SA_PASSWORD=YourStrong!Passw0rd -p 1433:1433 -d mcr.microsoft.com/mssql/server:2022-latest`

**`stripe listen` shows events but MyKart returns 400**
The `whsec_` secret from `stripe listen` must match what you stored with `dotnet user-secrets set "Stripe:WebhookSecret" "whsec_..."`. Restart the API after changing user-secrets.

**Warehouse service exits immediately (single-file `dotnet run`)**
Ensure you pass the subscription secret as the first argument: `dotnet run tools/warehouse.cs -- <secret> <fail-count>`. The `--` separator is required so the .NET tooling does not interpret `<secret>` as a dotnet flag.

## Not production-ready on purpose

Add authentication/tenancy to the subscription API, encrypt stored secrets (Data Protection / Key Vault), use EF migrations instead of `EnsureCreated`, add retention cleanup and OpenTelemetry metrics, and consider per-endpoint queues to avoid head-of-line blocking.
