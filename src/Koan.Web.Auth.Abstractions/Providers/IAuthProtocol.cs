using System.ComponentModel;
using Koan.Web.Auth.Options;

namespace Koan.Web.Auth.Providers;

/// <summary>
/// Connector-owned configuration validation for an authentication protocol whose named ASP.NET handler is
/// registered by that connector. Web Auth retains provider activation, default election, and reporting.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public interface IAuthProtocol
{
    /// <summary>The protocol identifier used by <see cref="ProviderOptions.Type"/>.</summary>
    string Protocol { get; }

    /// <summary>
    /// Validate the provider's effective, merged configuration once during host composition, without mutation or network I/O.
    /// Return no entries when complete; otherwise return secret-free corrective messages naming the relevant
    /// configuration paths. Unsupported overrides must reject instead of being silently ignored.
    /// </summary>
    IReadOnlyList<string> Validate(string providerId, ProviderOptions options);
}
