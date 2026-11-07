using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using MyKart.Api.Data;
using MyKart.Api.Inbound;
using MyKart.Api.Outbound;
using MyKart.Api.Webhooks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<StripeOptions>()
    .Bind(builder.Configuration.GetSection("Stripe"))
    .Validate(o => o.WebhookSecret.StartsWith("whsec_"), "Set Stripe:WebhookSecret (from 'stripe listen' or the Dashboard).")
    .ValidateOnStart();

builder.Services.AddOptions<WebhookOptions>()
    .Bind(builder.Configuration.GetSection("Webhooks"));

builder.Services.AddDbContext<MyKartDb>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("MyKart")));

builder.Services.AddValidation();                         // .NET 10 built-in minimal API validation
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(typeof(WorkSignal<>));
builder.Services.AddSingleton<SsrfGuard>();
builder.Services.AddScoped<WebhookPublisher>();
builder.Services.AddScoped<StripeEventHandler>();

builder.Services.AddHttpClient("webhooks", client =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MyKart-Webhooks/1.0");
        client.Timeout = TimeSpan.FromSeconds(30);        // hard ceiling; Polly's 15s attempt timeout fires first
    })
    .ConfigurePrimaryHttpMessageHandler(sp => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,                        // a 3xx is a failure, never something to follow
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = sp.GetRequiredService<SsrfGuard>().ConnectAsync
    })
    .AddResilienceHandler("webhook-delivery", pipeline =>
    {
        // Long-term retries live in the database. Polly only protects a single attempt.
        pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
        {
            FailureRatio = 0.5,
            MinimumThroughput = 5,
            SamplingDuration = TimeSpan.FromMinutes(1),
            BreakDuration = TimeSpan.FromSeconds(30)
        });
        pipeline.AddTimeout(TimeSpan.FromSeconds(15));
    })
    .SelectPipelineByAuthority();                         // one circuit breaker per receiver host

builder.Services.AddHostedService<InboxProcessor>();
builder.Services.AddHostedService<WebhookDispatcher>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<MyKartDb>();
    await db.Database.EnsureCreatedAsync();               // use migrations outside a demo

    if (!await db.Orders.AnyAsync())
    {
        db.Orders.Add(new Order { Id = "ORD-1001", CustomerEmail = "maya@example.com", Total = 49.99m });
        await db.SaveChangesAsync();
    }
}

app.MapStripeWebhooks();
app.MapSubscriptionEndpoints();

app.Run();
