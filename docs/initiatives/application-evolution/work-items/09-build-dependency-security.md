# AE-09: Patched source metadata build dependency

Engineering receipt, 2026-09-09. Baseline: `f238da380`.

**Task:** Remove the observed NU1902 warning by updating the existing SourceLink package pin.

**Application intent:** Build and publish Koan with supported source metadata tooling.

**Public expression:** No application change. The repository already declares SourceLink as a
private build dependency in Directory.Build.props; Directory.Packages.props owns its version.

**Guarantee/correction:** Replace Microsoft.SourceLink.GitHub 10.0.301 with 10.0.303, whose NuGet
dependency metadata selects Microsoft.Build.Tasks.Git 10.0.303. Microsoft lists that version as fixed
for [CVE-2026-62900](https://github.com/dotnet/sourcelink/security/advisories/GHSA-23fw-v26w-5fgq).
This addresses the identified dependency advisory, not a claim that every build dependency is risk-free.

**Complete intent surface:** No new package reference, configuration, runtime requirement or user action.

**Public concepts:** None. Existing source metadata and private build-dependency semantics remain.

**Docs read:** CLAUDE and the release playbook prohibit manual Koan package version edits and require
normal main-boundary publication. The Microsoft advisory identifies affected and patched Git build
task versions. NuGet's 10.0.303 SourceLink.GitHub nuspec confirms the patched transitive dependency.

**Code read:** Directory.Build.props applies SourceLink with PrivateAssets=All. Directory.Packages.props
pins 10.0.301. The actual Cache test restore reported NU1902 against transitive Git tasks 10.0.301.
The current installed SDK is 10.0.401; the explicit package pin is the selected correction owner.

**Reusing:** Central package management, existing SourceLink reference, normal restore/build/audit,
and NBGV version computation. No options, constants, new runtime contracts or parallel package source.

**Creating new:** No production type or file. Change one existing central PackageVersion value;
this receipt records the observed issue, decision and proof.

**Coalescence:** Keep the existing build dependency and update its central pin. Do not suppress the
warning, add a second transitive override, change the SDK installation, or hand-edit Koan versions.

**Ergonomics:** The ordinary build remains the complete action. Applications inherit no new setup.

**Constraints satisfied:** No HTTP/data/provider behavior changes, no new resources, no production
access, no credentials in logs. The maintainer's standing quality-pass authorization covers this fix.

**Risks:** Shared build inputs can advance multiple package versions automatically. Normal dependency
stamping and release certification remain required before publication. A focused restore/build proves
this selected build graph; it does not certify the whole train or replace final package acceptance.

## Verification

The original Cache receipt records NU1902. After the pin change, a forced restore of Koan.Cache
with NuGetAuditMode=all and NU1902 promoted to an error passes without warnings. Its resolved assets
contain SourceLink.GitHub, SourceLink.Common and Microsoft.Build.Tasks.Git at 10.0.303. The following
no-restore build passes with zero warnings and zero errors. Private receipts are
`sourcelink-restore.log` and `sourcelink-build.log` in the task's temporary directory.

Commands:

```powershell
dotnet restore src/Koan.Cache/Koan.Cache.csproj --force -p:NuGetAuditMode=all -p:WarningsAsErrors=NU1902
dotnet build src/Koan.Cache/Koan.Cache.csproj --no-restore -m:1 -v:minimal
```

No runtime test was added for this build-only pin. Full train certification and publication remain
open; earlier test receipts truthfully retain the warning from their original dependency graph.
