using System.Collections.Generic;
using Koan.Core.Naming;

namespace Koan.Jobs;

/// <summary>
/// ARCH-0100: the coalesce / idempotency identity must include the captured ambient. The "same work" for two
/// different tenants is <i>different</i> work — a tenant-blind coalesce key would let one tenant's idempotent submit
/// collapse onto another tenant's queued job (which would then run once, in the wrong tenant's ambient, against the
/// wrong tenant's data — bypassing the carrier guarantee at the submit gate, before any capture/restore).
///
/// <para>The coalesce key is an <b>identifier</b> (a base key + the ambient axes), so it folds the bag through the
/// ONE ARCH-0096 <see cref="AmbientAxisComposer"/> — the same engine storage blob keys use — rather than a
/// per-pillar hand-rolled fold. The dedup lookup computes the same fold, so it is structurally ambient-scoped: no
/// ledger query can forget the filter because the key itself encodes the axis. An unscoped/system submit (null bag)
/// keeps its global coalesce identity.</para>
/// </summary>
internal static class JobCoalesce
{
    public static string? FoldAmbient(string? baseKey, IReadOnlyDictionary<string, string>? ambientCarrier,
        JobDataRoute route = default)
        => baseKey is null ? null : route.Fold(AmbientAxisComposer.Append(baseKey, ambientCarrier));
}
