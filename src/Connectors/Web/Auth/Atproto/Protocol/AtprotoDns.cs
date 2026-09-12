using CarpaNet.Identity;
using DnsClient;

namespace Koan.Web.Auth.Connector.Atproto.Protocol;

// The maintained resolver owns DNS wire parsing, transaction matching, retries and TCP fallback.
internal sealed class AtprotoDns : IDnsResolver
{
    private readonly LookupClient client = new(new LookupClientOptions
    { UseCache = true, Timeout = TimeSpan.FromSeconds(5), Retries = 1, UseTcpOnly = true });

    public async Task<IReadOnlyList<string>> GetTxtRecordsAsync(string name, CancellationToken cancellationToken = default)
    {
        var response = await client.QueryAsync(name, QueryType.TXT, QueryClass.IN, cancellationToken);
        return response.Answers.TxtRecords()
            .Where(x => x.DomainName.Value.TrimEnd('.').Equals(name.TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
            .Select(x => string.Concat(x.Text)).ToArray();
    }
}
