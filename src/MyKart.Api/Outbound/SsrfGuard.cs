using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using MyKart.Api.Webhooks;

namespace MyKart.Api.Outbound;

/// <summary>
/// Webhook URLs are typed in by strangers. Without this, "http://169.254.169.254/" turns your
/// dispatcher into a tool for reading your own cloud metadata service.
/// </summary>
public sealed class SsrfGuard(IOptions<WebhookOptions> options)
{
    private static readonly IPNetwork[] BlockedRanges =
    [
        IPNetwork.Parse("0.0.0.0/8"),      IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("100.64.0.0/10"),  IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("169.254.0.0/16"), IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.168.0.0/16"), IPNetwork.Parse("::1/128"),
        IPNetwork.Parse("fc00::/7"),       IPNetwork.Parse("fe80::/10")
    ];

    public bool IsAllowed(IPAddress address)
    {
        if (options.Value.AllowPrivateNetworks)
            return true;

        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        return !BlockedRanges.Any(range => range.Contains(address));
    }

    /// <summary>
    /// Plugged into SocketsHttpHandler.ConnectCallback, so the check runs against the IP we are
    /// actually about to connect to. Validating the URL at registration time alone is defeated by
    /// DNS rebinding (public IP when you check, 10.0.0.5 when you deliver).
    /// </summary>
    public async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = await Dns.GetHostAddressesAsync(host, ct);

        if (addresses.Length == 0 || !addresses.All(IsAllowed))
            throw new HttpRequestException($"Blocked webhook destination '{host}': not a public address.");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
