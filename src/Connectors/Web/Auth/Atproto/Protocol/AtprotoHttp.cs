using System.Net;
using System.Net.Sockets;
using Koan.Core;
using Koan.Web.Auth.Connector.Atproto.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Koan.Web.Auth.Connector.Atproto.Protocol;

/// <summary>Connection-time address validation: DNS results are vetted and that exact address is connected.</summary>
public sealed class AtprotoHttp : IDisposable
{
    private readonly AtprotoOptions options;
    private readonly bool development;
    internal HttpClient Client { get; }
    private static readonly IPNetwork[] NonPublicV4 = [
        IPNetwork.Parse("0.0.0.0/8"), IPNetwork.Parse("10.0.0.0/8"), IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("127.0.0.0/8"), IPNetwork.Parse("169.254.0.0/16"), IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.0.0.0/24"), IPNetwork.Parse("192.0.2.0/24"), IPNetwork.Parse("192.88.99.0/24"),
        IPNetwork.Parse("192.168.0.0/16"), IPNetwork.Parse("198.18.0.0/15"), IPNetwork.Parse("198.51.100.0/24"),
        IPNetwork.Parse("203.0.113.0/24"), IPNetwork.Parse("224.0.0.0/3")];
    private static readonly IPNetwork[] NonPublicV6 = [IPNetwork.Parse("2001::/23"), IPNetwork.Parse("2001:db8::/32"), IPNetwork.Parse("2002::/16"), IPNetwork.Parse("3fff::/20")];

    public AtprotoHttp(IOptions<AtprotoOptions> configured, IHostEnvironment environment)
    {
        options = configured.Value;
        development = KoanEnv.Gate.DevelopmentOnly(environment);
        var sockets = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, ConnectCallback = Connect };
        Client = new HttpClient(new BoundedHandler(this, sockets)) { Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds) };
    }

    internal bool DevelopmentOrigin(Uri uri) => development && options.DevelopmentAllowedOrigins.Contains(uri.GetLeftPart(UriPartial.Authority), StringComparer.Ordinal);
    internal string ConnectionHost(Uri uri) => DevelopmentOrigin(uri) && options.DevelopmentConnectHost is { } host ? host : uri.Host;

    internal void Validate(Uri uri)
    {
        if (!uri.IsAbsoluteUri || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 ||
            (uri.Scheme != "https" && !DevelopmentOrigin(uri))) throw new InvalidOperationException("Unsafe AT Protocol destination.");
        if (!DevelopmentOrigin(uri) && (uri.Scheme != "https" || !uri.IsDefaultPort))
            throw new InvalidOperationException("Public AT Protocol discovery requires HTTPS on the default port.");
    }

    internal static bool PublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork) return !NonPublicV4.Any(x => x.Contains(address));
        return address.AddressFamily == AddressFamily.InterNetworkV6 && IPNetwork.Parse("2000::/3").Contains(address) && !NonPublicV6.Any(x => x.Contains(address));
    }

    private async ValueTask<Stream> Connect(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var uri = context.InitialRequestMessage.RequestUri!;
        Validate(uri);
        var addresses = await Dns.GetHostAddressesAsync(ConnectionHost(uri), ct);
        if (addresses.Length == 0 || (!DevelopmentOrigin(uri) && addresses.Any(x => !PublicAddress(x))))
            throw new InvalidOperationException("AT Protocol destination resolves to a non-public or unavailable address.");
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try { await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct); return new NetworkStream(socket, ownsSocket: true); }
            catch { socket.Dispose(); if (ct.IsCancellationRequested) throw; }
        }
        throw new HttpRequestException("Unable to connect to the verified AT Protocol destination.");
    }

    public void Dispose() => Client.Dispose();

    /// <summary>Send a caller-owned protocol request through the same address/redirect/size guards. The caller owns authentication context and response disposal.</summary>
    public Task<HttpResponseMessage> Send(HttpRequestMessage request, CancellationToken ct = default) => Client.SendAsync(request, ct);

    private sealed class BoundedHandler(AtprotoHttp owner, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            owner.Validate(request.RequestUri!);
            var response = await base.SendAsync(request, ct);
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var output = new MemoryStream();
                var buffer = new byte[8192];
                int read;
                while ((read = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    if (output.Length + read > owner.options.MaximumResponseBytes) throw new HttpRequestException("AT Protocol response exceeded the configured byte bound.");
                    output.Write(buffer, 0, read);
                }
                var replacement = new ByteArrayContent(output.ToArray());
                foreach (var header in response.Content.Headers) replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
                response.Content.Dispose();
                response.Content = replacement;
                return response;
            }
            catch { response.Dispose(); throw; }
        }
    }
}
