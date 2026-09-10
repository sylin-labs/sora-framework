[CmdletBinding()]
param([string]$ArtifactRoot = (Join-Path $PSScriptRoot '../../../artifacts/application-evolution'))

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$runRoot = Join-Path ([IO.Path]::GetFullPath($ArtifactRoot)) ([guid]::NewGuid().ToString('N'))
$fixture = Join-Path $runRoot 'fixture'
$feed = Join-Path $runRoot 'feed'
$logs = Join-Path $runRoot 'logs'
$checks = [Collections.Generic.List[object]]::new()
$packages = [Collections.Generic.List[object]]::new()
$graphs = [Collections.Generic.List[object]]::new()
$approvedIds = @{}
$unfinishedApprovedIds = @{}
$commandNumber = 0
$failure = $null
$frameworkReceipt = $null
$sourceFiles = [Collections.Generic.List[object]]::new()
foreach ($directory in @($fixture, $feed, $logs)) { [IO.Directory]::CreateDirectory($directory) | Out-Null }
Write-Output "AE|ARTIFACTS|$runRoot"

function Check([bool]$Condition, [string]$Name) {
    $checks.Add([ordered]@{ name = $Name; passed = $Condition })
    if (-not $Condition) { throw "Check failed: $Name" }
    Write-Output "AE|PASS|$Name"
}

function Run-Tool([string]$File, [string[]]$CommandArguments, [string]$Label) {
    $script:commandNumber++
    $info = [Diagnostics.ProcessStartInfo]::new($File)
    $info.WorkingDirectory = $fixture
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.Environment['NUGET_PACKAGES'] = Join-Path $runRoot 'packages'
    $info.Environment['DOTNET_NOLOGO'] = '1'
    foreach ($argument in $CommandArguments) { $info.ArgumentList.Add($argument) }
    Write-Output "AE|RUN|$Label"
    $process = [Diagnostics.Process]::Start($info)
    try {
        $output = $process.StandardOutput.ReadToEndAsync()
        $errors = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(240000)) { $process.Kill($true); throw "$Label timed out." }
        $transcript = $output.GetAwaiter().GetResult() + $errors.GetAwaiter().GetResult()
        $path = Join-Path $logs ('{0:00}-{1}.log' -f $script:commandNumber, $Label)
        [IO.File]::WriteAllText($path, $transcript)
        if ($process.ExitCode -ne 0) { throw "$Label failed ($($process.ExitCode)). See $path`n$transcript" }
    }
    finally { $process.Dispose() }
}

function Pack-Foundation([string]$Phase) {
    Run-Tool 'dotnet' @('pack', 'Foundation/Example.Approvals.Foundation.csproj', '-c', 'Release',
        '-p:IsPackable=true', '-p:PublicRelease=true', '-o', $feed) "$Phase-pack"
    $file = Get-ChildItem -LiteralPath $feed -Filter '*.nupkg' | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    $archive = [IO.Compression.ZipFile]::OpenRead($file.FullName)
    try {
        $entry = $archive.Entries | Where-Object FullName -Like '*.nuspec' | Select-Object -First 1
        $reader = [IO.StreamReader]::new($entry.Open())
        try { $metadata = [xml]$reader.ReadToEnd() } finally { $reader.Dispose() }
        $version = [string]$metadata.package.metadata.version
        Check ($metadata.package.metadata.id -eq 'Example.Approvals.Foundation') "$Phase-package-identity"
        $guidanceEntry = $archive.Entries | Where-Object FullName -EQ 'README.md' | Select-Object -First 1
        Check ($null -ne $guidanceEntry) "$Phase-package-guidance"
        $guidanceReader = [IO.StreamReader]::new($guidanceEntry.Open())
        try { $guidance = $guidanceReader.ReadToEnd() } finally { $guidanceReader.Dispose() }
        $documentedLimit = if ($Phase -eq 'baseline') { 'USD 1,000' } else { 'USD 500' }
        Check ($guidance.Contains("permits approvals through $documentedLimit.")) "$Phase-package-guidance-matches-policy"
    }
    finally { $archive.Dispose() }
    $package = [ordered]@{ phase = $Phase; version = $version; sha256 = (Get-FileHash -LiteralPath $file.FullName).Hash }
    $packages.Add($package)
    Write-Output "AE|PACKAGE|$Phase|$version"
    return $version
}

function Json-Request([string]$Base, [string]$Path, [string]$Method = 'GET', $Body = $null, [int[]]$Expected = @(200)) {
    $parameters = @{ Uri = $Base + $Path; Method = $Method; SkipHttpErrorCheck = $true; TimeoutSec = 20 }
    if ($null -ne $Body) { $parameters.ContentType = 'application/json'; $parameters.Body = $Body | ConvertTo-Json -Depth 20 -Compress }
    $response = Invoke-WebRequest @parameters
    $content = if ($response.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($response.Content) } else { $response.Content }
    if ([int]$response.StatusCode -notin $Expected) { throw "$Method $Path returned $($response.StatusCode), expected $Expected`: $content" }
    if ($response.Content) {
        return $content | ConvertFrom-Json -AsHashtable
    }
}

function Mcp-Request([string]$Base, [hashtable]$Headers, [string]$Method, $Parameters, $Id) {
    $body = @{ jsonrpc = '2.0'; method = $Method; params = $Parameters }
    if ($null -ne $Id) { $body.id = $Id }
    $response = Invoke-WebRequest -Uri "$Base/mcp" -Method POST -ContentType 'application/json' -Headers $Headers -Body ($body | ConvertTo-Json -Depth 20 -Compress) -TimeoutSec 20
    if ($Method -eq 'initialize') { $Headers['Mcp-Session-Id'] = [string]$response.Headers['Mcp-Session-Id'][0] }
    if ($null -eq $Id) { return }
    $content = if ($response.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($response.Content) } else { $response.Content }
    $messages = @($content -split "`n" | Where-Object { $_.StartsWith('data: ') } | ForEach-Object { $_.Substring(6) | ConvertFrom-Json -AsHashtable })
    $message = $messages | Where-Object { $_.id -eq $Id } | Select-Object -Last 1
    if ($null -eq $message -or $message.ContainsKey('error')) { throw "MCP $Method failed: $content" }
    return $message.result
}

function Test-Application([string]$App, [string]$Phase, [string]$Version, [decimal]$Maximum) {
    $isPurchase = $App -eq 'ApprovalDesk'
    $route = if ($isPurchase) { '/api/purchases' } else { '/api/expenses' }
    $operation = if ($isPurchase) { 'order' } else { 'reimburse' }
    $resultField = if ($isPurchase) { 'orderNumber' } else { 'reimbursedAt' }
    $output = Join-Path $runRoot "build/$Phase/$App"
    Run-Tool 'dotnet' @('build', "$App/$App.csproj", '-c', 'Release', '-o', $output,
        '-p:UsePackagedFoundation=true', "-p:ApprovalFoundationVersion=[$Version]") "$Phase-$App-build"
    $assets = Get-Content -LiteralPath (Join-Path $fixture "$App/obj/project.assets.json") -Raw | ConvertFrom-Json -AsHashtable
    Check ($assets.libraries["Example.Approvals.Foundation/$Version"].type -eq 'package') "$Phase-$App-consumes-package"
    $graphs.Add([ordered]@{ phase = $Phase; app = $App; libraries = @($assets.libraries.Keys | Sort-Object) })
    $koanLibraries = @($assets.libraries.Keys | Where-Object { $_ -match '^Sylin\.Koan[./]' })
    $unpublished = @()
    foreach ($library in $koanLibraries) {
        $cachePath = Join-Path (Join-Path $runRoot 'packages') $library.ToLowerInvariant()
        $metadata = Get-Content -LiteralPath (Join-Path $cachePath '.nupkg.metadata') -Raw | ConvertFrom-Json -AsHashtable
        if ($metadata.source -ne 'https://api.nuget.org/v3/index.json') { $unpublished += $library }
        if ($library.StartsWith('Sylin.Koan.Core/')) {
            $coreVersion = $library.Split('/')[1]
            $corePackage = Join-Path $cachePath "sylin.koan.core.$coreVersion.nupkg"
            $script:frameworkReceipt = [ordered]@{
                packageId = 'Sylin.Koan.Core'; version = $coreVersion; source = $metadata.source
                sha256 = (Get-FileHash -LiteralPath $corePackage).Hash
                published = $metadata.source -eq 'https://api.nuget.org/v3/index.json'
            }
        }
    }
    Check ($koanLibraries.Count -gt 0 -and $unpublished.Count -eq 0 -and $null -ne $script:frameworkReceipt) "$Phase-$App-all-koan-packages-from-nuget-org"

    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start()
    $port = $listener.LocalEndpoint.Port
    $listener.Stop()
    $base = "http://127.0.0.1:$port"
    $dataPhase = if ($Phase -eq 'rollback') { 'rollback' } else { 'persistent' }
    $dataPath = Join-Path $runRoot "data/$dataPhase"
    [IO.Directory]::CreateDirectory($dataPath) | Out-Null
    $database = Join-Path $dataPath "$App.db"
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.WorkingDirectory = $output
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @((Join-Path $output "$App.dll"), '--urls', $base)) { $start.ArgumentList.Add($argument) }
    $start.Environment['ASPNETCORE_ENVIRONMENT'] = 'Development'
    $start.Environment['Koan__Data__Sources__Default__ConnectionString'] = "Data Source=$database"
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    try {
        $ready = $false
        for ($attempt = 0; $attempt -lt 100; $attempt++) {
            if ($process.HasExited) { throw "$App exited during startup." }
            try {
                $response = Invoke-WebRequest -Uri "$base/health/ready" -TimeoutSec 2 -SkipHttpErrorCheck
                if ($response.StatusCode -eq 200) { $ready = $true; break }
            } catch { }
            Start-Sleep -Milliseconds 200
        }
        Check $ready "$Phase-$App-ready"
        $policy = Json-Request $base '/api/approval-policy'
        Check ($policy.maximumApprovalAmount -eq $Maximum) "$Phase-$App-policy-limit"
        foreach ($asset in @('/', '/app.js', '/site.css')) {
            $response = Invoke-WebRequest -Uri ($base + $asset) -TimeoutSec 10 -SkipHttpErrorCheck
            Check ($response.StatusCode -eq 200) "$Phase-$App-ui-$asset"
        }
        $facts = Invoke-WebRequest -Uri "$base/.well-known/Koan/facts" -TimeoutSec 10
        [IO.File]::WriteAllText((Join-Path $logs "$Phase-$App-facts.json"), $facts.Content)
        $factEnvelope = $facts.Content | ConvertFrom-Json -AsHashtable
        $sqlite = @($factEnvelope.facts | Where-Object { $_.code -eq 'koan.semantic.component.active' -and $_.subject -eq 'Sylin.Koan.Data.Connector.Sqlite' -and $_.state -eq 'Selected' -and $_.source -eq 'Example.Approvals.Foundation' })
        Check ($factEnvelope.complete -and $sqlite.Count -eq 1) "$Phase-$App-sqlite-facts"
        $lifecycle = @($factEnvelope.facts | Where-Object { $_.code -eq 'koan.data.lifecycle.selected' -and $_.state -eq 'Observed' -and $_.reasonCode -eq 'host-composition' })
        Check ($lifecycle.Count -eq 1) "$Phase-$App-lifecycle-facts"

        if ($Phase -eq 'upgraded') {
            $old = Json-Request $base "$route/$($approvedIds[$App])"
            Check ($old.state -eq 'Approved' -or $old.state -eq 1) "$Phase-$App-old-approved-record-readable"
            Check ([bool]$old[$resultField]) "$Phase-$App-old-business-outcome-preserved"
            $unfinished = Json-Request $base "$route/$($unfinishedApprovedIds[$App])"
            Check ($unfinished.amount -eq 750 -and -not $unfinished[$resultField]) "$Phase-$App-old-approval-awaits-business-action"
            $completedAfterUpgrade = Json-Request $base "$route/$($unfinished.id)/$operation" 'POST'
            Check ([bool]$completedAfterUpgrade[$resultField]) "$Phase-$App-old-over-limit-approval-can-finish"
        }

        $body = if ($isPurchase) {
            @{ subject = "$Phase studio equipment"; amount = 250; supplier = 'Example supplier'; costCenter = 'Design' }
        } else {
            @{ subject = "$Phase client travel"; amount = 250; employee = 'Example colleague'; receiptNumber = 'R-1042' }
        }
        $low = Json-Request $base $route 'POST' $body @(200, 201)
        $null = Json-Request $base "$route/$($low.id)/$operation" 'POST' $null @(409)
        Check $true "$Phase-$App-requires-prior-approval"
        $null = Json-Request $base "$route/$($low.id)/approve" 'POST'
        $finished = Json-Request $base "$route/$($low.id)/$operation" 'POST'
        Check ([bool]$finished[$resultField]) "$Phase-$App-business-outcome"
        $again = Json-Request $base "$route/$($low.id)/$operation" 'POST'
        Check ($again[$resultField] -eq $finished[$resultField]) "$Phase-$App-repeat-action-preserves-receipt"
        $changed = $finished.Clone()
        $changed.amount = 10
        $denied = Json-Request $base "$route/$($low.id)" 'PUT' $changed @(409)
        Check ($denied.code -eq 'approval.already-approved') "$Phase-$App-approved-fields-final"
        $retained = Json-Request $base "$route/$($low.id)"
        Check ($retained.amount -eq 250) "$Phase-$App-denied-write-did-not-persist"
        $null = Json-Request $base "$route/$($low.id)" 'DELETE' $null @(401)
        Check $true "$Phase-$App-anonymous-http-removal-unavailable"

        $insertApproved = $body.Clone(); $insertApproved.state = 'Approved'
        $denied = Json-Request $base $route 'POST' $insertApproved @(409)
        Check ($denied.code -eq 'approval.submit-first') "$Phase-$App-cannot-insert-approved"
        $invalid = $body.Clone(); $invalid.amount = -1
        $denied = Json-Request $base $route 'POST' $invalid @(409)
        Check ($denied.code -eq 'approval.invalid-request') "$Phase-$App-positive-amount-required"

        if (-not $isPurchase) {
            $patchExpense = Json-Request $base $route 'POST' $body @(200, 201)
            $patchBody = @{ subject = "$Phase corrected travel"; amount = 300 }
            $null = Json-Request $base "$route/$($patchExpense.id)" 'PATCH' $patchBody @(200)
            $patched = Json-Request $base "$route/$($patchExpense.id)"
            Check ($patched.id -eq $patchExpense.id) "$Phase-$App-pending-patch-keeps-identity"
            Check ($patched.subject -eq "$Phase corrected travel" -and $patched.amount -eq 300) "$Phase-$App-pending-patch-applies-subject-and-amount"
            Check ($patched.employee -eq 'Example colleague' -and $patched.receiptNumber -eq 'R-1042') "$Phase-$App-pending-patch-preserves-omitted-fields"
            Check (-not $patched['reimbursedAt']) "$Phase-$App-pending-patch-leaves-reimbursement-null"
            Check ($patched.state -eq 'Pending') "$Phase-$App-pending-patch-stays-pending"
            $null = Json-Request $base "$route/$($patchExpense.id)/approve" 'POST'
            $deniedPatch = @{ subject = "$Phase late correction"; amount = 320 }
            $denied = Json-Request $base "$route/$($patchExpense.id)" 'PATCH' $deniedPatch @(409)
            Check ($denied.code -eq 'approval.already-approved') "$Phase-$App-approved-patch-rejected"
            Check (-not [string]::IsNullOrWhiteSpace([string]$denied.detail)) "$Phase-$App-approved-patch-explains-correction"
            $retained = Json-Request $base "$route/$($patchExpense.id)"
            Check ($retained.id -eq $patchExpense.id -and $retained.subject -eq "$Phase corrected travel" -and $retained.amount -eq 300 -and $retained.employee -eq 'Example colleague' -and $retained.receiptNumber -eq 'R-1042' -and -not $retained['reimbursedAt'] -and $retained.state -eq 'Approved') "$Phase-$App-approved-patch-did-not-persist"
        }

        $highBody = $body.Clone(); $highBody.amount = 750
        $high = Json-Request $base $route 'POST' $highBody @(200, 201)
        if ($Maximum -lt 750) {
            $denied = Json-Request $base "$route/$($high.id)/approve" 'POST' $null @(409)
            Check ($denied.code -eq 'approval.over-limit') "$Phase-$App-tightened-rule-enforced"
            $highBody.id = $high.id; $highBody.state = 'Approved'
            $denied = Json-Request $base $route 'POST' $highBody @(409)
            Check ($denied.code -eq 'approval.over-limit') "$Phase-$App-direct-upsert-cannot-bypass-rule"
            $pending = Json-Request $base "$route/$($high.id)"
            Check ($pending.state -eq 'Pending' -or $pending.state -eq 0) "$Phase-$App-denied-approval-stays-pending"
        } else {
            $null = Json-Request $base "$route/$($high.id)/approve" 'POST'
            $accepted = Json-Request $base "$route/$($high.id)/$operation" 'POST'
            Check ([bool]$accepted[$resultField]) "$Phase-$App-earlier-limit-allows-750"
            if ($Phase -eq 'baseline') { $approvedIds[$App] = $high.id }
        }
        if ($Phase -eq 'baseline') {
            $unfinished = Json-Request $base $route 'POST' $highBody @(200, 201)
            $approvedUnfinished = Json-Request $base "$route/$($unfinished.id)/approve" 'POST'
            Check (-not $approvedUnfinished[$resultField]) "$Phase-$App-approved-record-left-for-upgrade"
            $unfinishedApprovedIds[$App] = $unfinished.id
        }

        $headers = @{ Accept = 'application/json, text/event-stream'; 'MCP-Protocol-Version' = '2025-06-18' }
        $null = Mcp-Request $base $headers 'initialize' @{ protocolVersion = '2025-06-18'; capabilities = @{}; clientInfo = @{ name = 'shared-approval-proof'; version = '1.0' } } 1
        $null = Mcp-Request $base $headers 'notifications/initialized' @{} $null
        $tools = Mcp-Request $base $headers 'tools/list' @{} 2
        $upsert = @($tools.tools | Where-Object { $_.metadata.operation -eq 'Upsert' })
        Check ($upsert.Count -eq 1) "$Phase-$App-mcp-entity-discovered"
        $boundaryBody = $body.Clone(); $boundaryBody.amount = $Maximum
        $boundary = Json-Request $base $route 'POST' $boundaryBody @(200, 201)
        $boundaryBody.id = $boundary.id; $boundaryBody.state = 'Approved'
        $allowed = Mcp-Request $base $headers 'tools/call' @{ name = $upsert[0].name; arguments = @{ model = $boundaryBody } } 3
        Check (-not ($allowed.ContainsKey('isError') -and $allowed.isError)) "$Phase-$App-mcp-approval-at-limit-allowed"
        $boundarySaved = Json-Request $base "$route/$($boundary.id)"
        Check ($boundarySaved.state -eq 'Approved' -or $boundarySaved.state -eq 1) "$Phase-$App-mcp-approval-persisted"
        $overBody = $body.Clone(); $overBody.amount = $Maximum + 1
        $over = Json-Request $base $route 'POST' $overBody @(200, 201)
        $overBody.id = $over.id; $overBody.state = 'Approved'
        $rejected = Mcp-Request $base $headers 'tools/call' @{ name = $upsert[0].name; arguments = @{ model = $overBody } } 4
        Check ($rejected.isError -and ($rejected.content.text -join ' ') -match 'approval limit') "$Phase-$App-mcp-over-limit-rejected-correctively"
        $stillPending = Json-Request $base "$route/$($over.id)"
        Check ($stillPending.state -eq 'Pending' -or $stillPending.state -eq 0) "$Phase-$App-mcp-rejection-did-not-persist"
        [IO.File]::WriteAllText((Join-Path $logs "$Phase-$App-mcp.json"), (@{ allowed = $allowed; rejected = $rejected } | ConvertTo-Json -Depth 20))
        Check (Test-Path -LiteralPath $database) "$Phase-$App-real-database"
    }
    finally {
        if (-not $process.HasExited) { $process.Kill($true) }
        $process.WaitForExit()
        [IO.File]::WriteAllText((Join-Path $logs "$Phase-$App-host.log"), $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult())
        $process.Dispose()
    }
}

try {
    foreach ($file in Get-ChildItem -LiteralPath $PSScriptRoot -Recurse -File -Force) {
        $relative = [IO.Path]::GetRelativePath($PSScriptRoot, $file.FullName).Replace('\', '/')
        if ($relative -match '(^|/)(bin|obj|artifacts|\.git|\.koan|\.local|mcp-sdk)(/|$)' -or $relative -match '\.db(-wal|-shm)?$' -or $relative -match '/wwwroot/(app\.js|site\.css)$') { continue }
        $destination = Join-Path $fixture $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $destination
        $sourceFiles.Add(@{ path = $relative; sha256 = (Get-FileHash -LiteralPath $file.FullName).Hash })
    }
    $feedXml = [Security.SecurityElement]::Escape($feed)
    [IO.File]::WriteAllText((Join-Path $fixture 'NuGet.Config'), "<configuration><packageSources><clear/><add key=`"fixture`" value=`"$feedXml`"/><add key=`"nuget.org`" value=`"https://api.nuget.org/v3/index.json`"/></packageSources></configuration>")
    $policyPath = Join-Path $fixture 'Foundation/Policy/ApprovalPolicyOptions.cs'
    $currentPolicy = [IO.File]::ReadAllText($policyPath)
    $guidancePath = Join-Path $fixture 'Foundation/README.md'
    $currentGuidance = [IO.File]::ReadAllText($guidancePath)
    Check ($currentPolicy.Contains('= 500m;')) 'current-policy-fixture-starts-at-500'
    [IO.File]::WriteAllText($policyPath, $currentPolicy.Replace('= 500m;', '= 1000m;'))
    [IO.File]::WriteAllText($guidancePath, $currentGuidance.Replace('permits approvals through USD 500.', 'permits approvals through USD 1,000.'))
    Run-Tool 'git' @('init', '-b', 'proof') 'fixture-git-init'
    Run-Tool 'git' @('config', 'user.name', 'Koan verification fixture') 'fixture-git-name'
    Run-Tool 'git' @('config', 'user.email', 'verification@example.invalid') 'fixture-git-email'
    Run-Tool 'git' @('add', '.') 'fixture-baseline-stage'
    Run-Tool 'git' @('commit', '-s', '-m', 'fixture: establish the original approval policy') 'fixture-baseline-commit'
    $baselineOutput = @(Pack-Foundation 'baseline')
    $baselineVersion = [string]$baselineOutput[-1]
    $baselineOutput | Select-Object -SkipLast 1 | Write-Output
    foreach ($app in @('ApprovalDesk', 'ExpenseDesk')) { Test-Application $app 'baseline' $baselineVersion 1000 }

    [IO.File]::WriteAllText($policyPath, $currentPolicy)
    [IO.File]::WriteAllText($guidancePath, $currentGuidance)
    Run-Tool 'git' @('add', 'Foundation/Policy/ApprovalPolicyOptions.cs', 'Foundation/README.md') 'fixture-update-stage'
    Run-Tool 'git' @('commit', '-s', '-m', 'fixture: tighten the shared approval limit') 'fixture-update-commit'
    $updatedOutput = @(Pack-Foundation 'upgraded')
    $updatedVersion = [string]$updatedOutput[-1]
    $updatedOutput | Select-Object -SkipLast 1 | Write-Output
    Check ($updatedVersion -ne $baselineVersion) 'policy-change-advances-computed-package-version'
    foreach ($app in @('ApprovalDesk', 'ExpenseDesk')) { Test-Application $app 'upgraded' $updatedVersion 500 }
    foreach ($app in @('ApprovalDesk', 'ExpenseDesk')) { Test-Application $app 'rollback' $baselineVersion 1000 }
    $baselineFile = Join-Path $feed "Example.Approvals.Foundation.$baselineVersion.nupkg"
    Check ((Get-FileHash -LiteralPath $baselineFile).Hash -eq $packages[0].sha256) 'earlier-package-bytes-unchanged'
    $diffInfo = [Diagnostics.ProcessStartInfo]::new('git')
    $diffInfo.WorkingDirectory = $fixture; $diffInfo.UseShellExecute = $false; $diffInfo.CreateNoWindow = $true
    foreach ($argument in @('diff', '--exit-code', 'HEAD~1', 'HEAD', '--', 'ApprovalDesk', 'ExpenseDesk')) { $diffInfo.ArgumentList.Add($argument) }
    $diff = [Diagnostics.Process]::Start($diffInfo)
    $diff.WaitForExit()
    Check ($diff.ExitCode -eq 0) 'consumer-source-unchanged-between-packages'
    $diff.Dispose()
}
catch { $failure = $_.Exception.Message }
finally {
    $receipt = [ordered]@{
        generatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        passed = $null -eq $failure
        failure = $failure
        scope = 'Two package consumers; shared policy tightening; isolated rollback; HTTP business and negative paths; MCP approval boundary; persisted SQLite state'
        independentParticipants = 0
        framework = $frameworkReceipt
        sourceFiles = @($sourceFiles.ToArray() | Sort-Object path)
        packages = @($packages.ToArray())
        consumerGraphs = @($graphs.ToArray())
        checks = @($checks.ToArray())
    }
    [IO.File]::WriteAllText((Join-Path $runRoot 'receipt.json'), ($receipt | ConvertTo-Json -Depth 20))
}
if ($failure) { throw "$failure`nFull evidence: $runRoot" }
Write-Output "AE|COMPLETE|$($checks.Count) checks|$runRoot"
