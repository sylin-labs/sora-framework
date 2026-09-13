# Sign in as an AT identity

This generic sample references Web, Identity, SQLite, and the native AT
authentication connector. `Program.cs` contains only `AddKoan()` and normal host
startup. The form asks for a handle or DID; `/sample/identity` returns the verified
protocol claims after sign-in. No Bluesky profile, email, or application-specific
domain model is involved.

For the local protocol test network on PLC2582/PDS2583/PDS2584:

```powershell
$env:DOTNET_ENVIRONMENT = 'Development'
dotnet run --project samples/AtprotoIdentity --no-launch-profile
```

Open `http://127.0.0.1:5211`. The development settings use the AT profile's
loopback-client exception, an identity-only scope, and explicit local origins.
Supply a disposable test DID. To exercise a local handle without public DNS,
configure `Koan:Web:Auth:Atproto:DevelopmentHandles:<handle>` with its actual test
DID; the real DID document must reverse-bind that handle. Do not copy a test map
or local allowed-origin list into a public deployment.

For a public host, set the provider ClientId to its canonical HTTPS metadata URL,
remove the development exceptions, use the public PLC directory, and persist and
protect both the session directory and ASP.NET Data Protection key ring. Reuse
the module README's complete configuration/custody contract.

Inspect `/health/ready`, the startup provider election and
`/.well-known/Koan/facts`. Cookie logout and upstream disconnection are separate
actions; this sample's read-only identity page does not add a disconnect API.

## Explicit disposable-provider lifecycle proof

After completing ordinary browser sign-in with the disposable fixture's `owner`
account, stop this sample so its process-exclusive protected store can be opened
by the test. Run from the repository root, under the same OS account and with the
same ASP.NET Data Protection key ring used by the sample:

```powershell
$env:KOAN_ATPROTO_LIFECYCLE = 'disconnect'
$env:KOAN_ATPROTO_SAMPLE = (Resolve-Path samples/AtprotoIdentity).Path
$env:KOAN_ATPROTO_FIXTURES = 'C:/absolute/path/to/disposable-fixtures.json'
$env:KOAN_ATPROTO_RECEIPT = 'C:/absolute/path/to/disconnect-receipt.json'
dotnet test tests/Koan.Web.Auth.Atproto.Tests --filter FullyQualifiedName~LocalProviderLifecycle
```

The fixture shape is `{ "accounts": [{ "role": "owner", "did": "did:plc:...",
"handle": "local.test", "pds": "http://localhost:2583" }] }`. Include the other
local accounts when their handle mappings are needed. This test never reads
passwords, accepts only a loopback provider, and intentionally revokes that
account's grant for this sample client. It expires only local access-token
metadata, proves a real provider refresh rotates both tokens, performs an
authenticated `getSession`, calls checked upstream revocation, and verifies the
retained refresh credential now fails with `invalid_grant`. Credentials remain
in memory or the existing protected store; the receipt contains only booleans,
status codes and the disposable DID.

Start the sample again and explicitly complete browser authentication and
consent. Stop it, change `KOAN_ATPROTO_LIFECYCLE` to `reauthorized`, choose a new
receipt path, and run the same filtered test. It proves the new persisted grant
restores, refreshes, and authenticates as the same DID. Clear the lifecycle
environment variable afterward. The normal test suite skips this destructive
local-provider proof unless the mode is explicitly set.

**Working with a coding agent?** [AGENTS.md](../../AGENTS.md) at the repository root orients any
agent on the Koan conventions this sample follows.
