using Koan.Web.Auth.Options;
using Koan.Web.Auth.Providers;

namespace Koan.Web.Auth.Integration.Tests.CustomProtocol;

public sealed class ProbeAuthProtocol : IAuthProtocol
{
    public string Protocol => "synthetic";
    public IReadOnlyList<string> Validate(string providerId, ProviderOptions options) => [];
}
