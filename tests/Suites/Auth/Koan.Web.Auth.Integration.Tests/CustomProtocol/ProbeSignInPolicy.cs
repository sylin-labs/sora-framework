using Koan.Web.Auth.Contributors;
using Koan.Web.Auth.Flow;

namespace Koan.Web.Auth.Integration.Tests.CustomProtocol;

public sealed class ProbeSignInPolicy : IKoanAuthFlowHandler
{
    public Task OnSignIn(AuthSignInContext context, CancellationToken cancellationToken)
    {
        if (context.Provider == "custom" && context.Identity.HasClaim("fixture-identifier", "reject.example.test"))
            context.Reject("Fixture policy rejected this sign-in.");
        return Task.CompletedTask;
    }
}
