using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Koan.Core;
using Koan.Web.Extensions;
using Xunit;

namespace Koan.Web.Auth.Integration.Tests;

/// <summary>
/// Boots a REAL Kestrel host on a loopback port (Development) with the full Koan auth fabric + the dev Test
/// provider (oauth2 <c>test</c> + oidc <c>test-oidc</c>). Real Kestrel (not TestServer) so the maintained
/// OAuth/OIDC handler's server-side Backchannel — token/userinfo/discovery/JWKS — works over real HTTP, which
/// is exactly what the engine swap (WEB-0071) delegates to the handler. <c>ASPNETCORE_URLS</c> is deliberately
/// NOT set (reproducing a container/proxy deployment), so the Test provider's relative endpoints must resolve from
/// the live request host at challenge time — the Bug-2 regression guard.
/// </summary>
public sealed class AuthSwapFixture : IAsyncLifetime
{
    private IHost? _host;
    private string? _priorUrls;
    private readonly ConcurrentQueue<string> _errors = new();

    public int Port { get; private set; }
    public string BaseUrl => $"http://127.0.0.1:{Port}";
    public string PublicBaseUrl => $"http://koan-browser.test:{Port}";
    public IServiceProvider Services => _host?.Services ?? throw new InvalidOperationException("Host not started");
    public string Diagnostics => string.Join(Environment.NewLine, _errors);

    public async ValueTask InitializeAsync()
    {
        Port = GrabFreePort();
        _priorUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        // Reproduce the real deployment: the server binds (via UseUrls below) but ASPNETCORE_URLS is NOT readable by
        // the process (a container / Kestrel-config / chiseled image). The self-hosted Test provider endpoints must
        // therefore resolve from the LIVE request host, not the env var. This is the Bug-2 regression guard.
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", null);

        _host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(new ErrorCaptureLoggerProvider(_errors));
            })
            .ConfigureAppConfiguration(b => b.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // offline-only, mirrors the bootstrap specs
                ["ConnectionStrings:Redis"] = "localhost:0",
                // Deliberately NO TestProvider:Enabled opt-in — this fixture runs in plain Development, exactly like
                // the real deployment that filed the regression. Automatic definitions and attribute-routed
                // endpoints share IsActive; if they drift, every round-trip below 404s.
            }))
            .ConfigureWebHostDefaults(web =>
            {
                web.UseUrls(BaseUrl);
                web.UseEnvironment("Development");
                web.ConfigureServices(s =>
                {
                    s.AddDataProtection().UseEphemeralDataProtectionProvider();
                    s.AddKoan();
                    s.AddKoanWeb();
                    s.AddKoanControllersFrom<WhoAmIController>();
                });
                // KoanWebStartupFilter builds the real routing → authn → authz → endpoints pipeline.
                web.Configure(_ => { });
            })
            .Build();

        await _host.StartAsync();
    }

    /// <summary>A fresh cookie-aware, non-auto-redirecting client pre-seeded with the dev persona cookie
    /// (so the Test authorize endpoint issues a code without the interactive login page).</summary>
    public HttpClient NewClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = new CookieContainer()
        };
        handler.CookieContainer.Add(new Uri(BaseUrl),
            new Cookie("_tp_user", Uri.EscapeDataString("alice|alice@example.com")) { Path = "/" });
        return new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
    }

    /// <summary>
    /// Browser-shaped client whose public hostname deliberately does not identify the server's bound address.
    /// The client maps that hostname to loopback, as a browser/host port mapping would; the application's own
    /// back-channel receives no such mapping. A successful OIDC flow therefore proves that Koan uses the internal
    /// Kestrel address for discovery/token/userinfo/JWKS while preserving this public issuer.
    /// </summary>
    public HttpClient NewSplitHostClient(string? publicBaseUrl = null)
    {
        publicBaseUrl ??= PublicBaseUrl;
        var cookies = new CookieContainer();
        cookies.Add(new Uri(publicBaseUrl),
            new Cookie("_tp_user", Uri.EscapeDataString("alice|alice@example.com")) { Path = "/" });
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = true,
            CookieContainer = cookies,
            UseProxy = false,
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(IPAddress.Loopback, Port, ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };
        return new HttpClient(handler) { BaseAddress = new Uri(publicBaseUrl) };
    }

    public async ValueTask DisposeAsync()
    {
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", _priorUrls);
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    private static int GrabFreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }

    private sealed class ErrorCaptureLoggerProvider(ConcurrentQueue<string> errors) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ErrorCaptureLogger(categoryName, errors);
        public void Dispose() { }
    }

    private sealed class ErrorCaptureLogger(string category, ConcurrentQueue<string> errors) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            errors.Enqueue($"{category} [{eventId.Id}]: {formatter(state, exception)}{Environment.NewLine}{exception}");
        }
    }
}
