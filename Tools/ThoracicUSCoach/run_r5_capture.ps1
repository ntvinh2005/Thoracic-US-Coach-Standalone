param(
    [Parameter(Mandatory = $true)]
    [string]$Manifest,
    [Parameter(Mandatory = $true)]
    [string]$HandoffDestination,
    [string]$UnityPath = "C:\Program Files\Unity\Hub\Editor\6000.3.9f1\Editor\Unity.exe",
    [switch]$SkipPreflight,
    [switch]$FinalizeExisting
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
function Get-R5RawSha256([string]$Path) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { $hash = $algorithm.ComputeHash([IO.File]::ReadAllBytes($Path)) } finally { $algorithm.Dispose() }
    ([BitConverter]::ToString($hash) -replace '-', '').ToLowerInvariant()
}
$manifestPath = if ([IO.Path]::IsPathRooted($Manifest)) {
    (Resolve-Path -LiteralPath $Manifest).Path
} else {
    (Resolve-Path -LiteralPath (Join-Path $projectRoot $Manifest)).Path
}
if (-not $manifestPath.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'R5 manifest must be inside this isolated Unity worktree'
}
if (-not $FinalizeExisting) {
    if (-not (Test-Path -LiteralPath $UnityPath)) { throw "Unity executable missing: $UnityPath" }
    $resolvedUnity = (Resolve-Path -LiteralPath $UnityPath).Path
    $activeEditor = Get-Process -Name Unity -ErrorAction SilentlyContinue | Where-Object {
        try { (Resolve-Path -LiteralPath $_.Path).Path -eq $resolvedUnity } catch { $false }
    }
    if ($activeEditor) { throw 'Exclusive Unity preflight failed: the requested Unity Editor is already running' }
}

$manifestObject = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifestObject.mode -notin @('R5_A_SCOUT', 'R5_B_DEVELOPMENT')) { throw 'Invalid R5 capture mode' }
$commit = (& git -c "safe.directory=$($projectRoot.Replace('\','/'))" -C $projectRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or (-not $FinalizeExisting -and $commit -ne $manifestObject.sourceCommit)) {
    throw "Execution commit does not match frozen manifest: HEAD=$commit manifest=$($manifestObject.sourceCommit)"
}
$branch = (& git -c "safe.directory=$($projectRoot.Replace('\','/'))" -C $projectRoot branch --show-current).Trim()
if ($LASTEXITCODE -ne 0 -or $branch -ne $manifestObject.captureBranch) {
    throw "Execution branch does not match frozen manifest: branch=$branch manifest=$($manifestObject.captureBranch)"
}
$outputRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot $manifestObject.outputDirectory))
if (-not $outputRoot.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'R5 output path escapes this isolated worktree'
}
if ($FinalizeExisting) {
    if (-not (Test-Path -LiteralPath $outputRoot -PathType Container)) { throw "Final R5 output missing: $outputRoot" }
    if (Test-Path -LiteralPath ($outputRoot + '.partial')) { throw "Mutable partial R5 output exists: $outputRoot.partial" }
} else {
    if (Test-Path -LiteralPath $outputRoot) { throw "Refusing to overwrite R5 output: $outputRoot" }
    if (Test-Path -LiteralPath ($outputRoot + '.partial')) { throw "Stale partial R5 output exists: $outputRoot.partial" }
}
if (Test-Path -LiteralPath $HandoffDestination) { throw "Refusing to overwrite handoff snapshot: $HandoffDestination" }
if (Test-Path -LiteralPath ($HandoffDestination + '.partial')) { throw "Stale partial handoff exists: $HandoffDestination.partial" }

$logRoot = Join-Path $projectRoot 'Artifacts\ThoracicUSCoach\active2d\evaluation\g2_r5'
New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
if (-not $FinalizeExisting -and -not $SkipPreflight) {
    $testXml = Join-Path $logRoot 'r5_preflight_editmode.xml'
    $testLog = Join-Path $logRoot 'r5_preflight_editmode.log'
    $testFilter = 'CSSALT.ThoracicUSCoach.Tests.ThoracicR5CaptureTests;CSSALT.ThoracicUSCoach.Tests.Active2DSafetyTests;CSSALT.ThoracicUSCoach.Tests.ThoracicUSCoachTests'
    $testProcess = Start-Process -FilePath $UnityPath -ArgumentList @(
        '-batchmode', '-projectPath', $projectRoot,
        '-runTests', '-testPlatform', 'EditMode', '-testFilter', $testFilter,
        '-testResults', $testXml, '-logFile', $testLog
    ) -WindowStyle Hidden -Wait -PassThru
    if (-not (Test-Path -LiteralPath $testXml)) {
        throw "R5 preflight produced no XML (Unity exit $($testProcess.ExitCode)); preserve $testLog"
    }
    [xml]$testResult = Get-Content -LiteralPath $testXml -Raw
    $run = $testResult.'test-run'
    if ([int]$run.failed -ne 0 -or [int]$run.passed -lt 20) {
        throw "R5 safety preflight failed: passed=$($run.passed), failed=$($run.failed); preserve $testLog"
    }
}

if (-not $FinalizeExisting) {
    $env:THORACIC_R5_EXECUTION_COMMIT = $commit
    $captureLog = Join-Path $logRoot ("r5_capture_" + $manifestObject.mode.ToLowerInvariant() + '.log')
    $rootUri = [Uri]::new($projectRoot.TrimEnd('\') + '\')
    $manifestUri = [Uri]::new($manifestPath)
    $relativeManifest = [Uri]::UnescapeDataString($rootUri.MakeRelativeUri($manifestUri).ToString())
    $captureProcess = Start-Process -FilePath $UnityPath -ArgumentList @(
        '-batchmode', '-projectPath', $projectRoot,
        '-executeMethod', 'CSSALT.ThoracicUSCoach.Editor.ThoracicR5CaptureBatch.Run',
        '-r5Manifest', $relativeManifest, '-logFile', $captureLog
    ) -WindowStyle Hidden -Wait -PassThru
    if ($captureProcess.ExitCode -ne 0) {
        throw "R5 Unity capture failed with exit $($captureProcess.ExitCode); preserve $captureLog"
    }
}

foreach ($required in 'r5_unity_report.json','r5_evidence.json','evidence_index.json','snapshot_seal.json','capture_input_manifest.json') {
    if (-not (Test-Path -LiteralPath (Join-Path $outputRoot $required))) {
        throw "Completed R5 output is missing $required"
    }
}
$report = Get-Content -LiteralPath (Join-Path $outputRoot 'r5_unity_report.json') -Raw | ConvertFrom-Json
if (-not $report.complete -or -not $report.immutableSnapshotSealed -or $report.infrastructureFailure) {
    throw "R5 snapshot is not complete and sealed: $($report.infrastructureFailure)"
}
$evidencePath = Join-Path $outputRoot 'r5_evidence.json'
$indexPath = Join-Path $outputRoot 'evidence_index.json'
$sealPath = Join-Path $outputRoot 'snapshot_seal.json'
$captureManifestPath = Join-Path $outputRoot 'capture_input_manifest.json'
$evidence = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json
$index = Get-Content -LiteralPath $indexPath -Raw | ConvertFrom-Json
$seal = Get-Content -LiteralPath $sealPath -Raw | ConvertFrom-Json
foreach ($entry in $index.files) {
    $indexedPath = [IO.Path]::GetFullPath((Join-Path $outputRoot $entry.path))
    if (-not $indexedPath.StartsWith($outputRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe indexed path: $($entry.path)"
    }
    if (-not (Test-Path -LiteralPath $indexedPath -PathType Leaf)) { throw "Indexed file missing: $($entry.path)" }
    $actualHash = Get-R5RawSha256 $indexedPath
    $actualBytes = (Get-Item -LiteralPath $indexedPath).Length
    if ($actualHash -ne $entry.sha256 -or $actualBytes -ne [long]$entry.bytes) {
        throw "Indexed file integrity mismatch: $($entry.path)"
    }
}
$evidenceHash = Get-R5RawSha256 $evidencePath
$indexHash = Get-R5RawSha256 $indexPath
$reportHash = Get-R5RawSha256 (Join-Path $outputRoot 'r5_unity_report.json')
$captureManifestHash = Get-R5RawSha256 $captureManifestPath
if ($seal.evidenceManifestSha256 -ne $evidenceHash -or $seal.evidenceIndexSha256 -ne $indexHash -or $seal.reportSha256 -ne $reportHash) {
    throw 'R5 seal does not match evidence/index/report bytes'
}
if ($evidence.integrity.filesSha256 -ne $indexHash -or $evidence.integrity.manifestSha256 -ne $captureManifestHash) {
    throw 'R5 evidence integrity hashes do not match index/capture manifest'
}
if (([int]$evidence.integrity.recordCount -ne @($evidence.records).Count) -or
    ([int]$evidence.integrity.imageCount -ne @($evidence.records).Count) -or
    ([int]$evidence.proposal.attemptedStarts -ne @($evidence.startAttempts).Count)) {
    throw 'R5 evidence count reconciliation failed'
}

$handoffParent = Split-Path -Parent $HandoffDestination
New-Item -ItemType Directory -Path $handoffParent -Force | Out-Null
$handoffPartial = $HandoffDestination + '.partial'
Copy-Item -LiteralPath $outputRoot -Destination $handoffPartial -Recurse
Move-Item -LiteralPath $handoffPartial -Destination $HandoffDestination
Get-ChildItem -LiteralPath $HandoffDestination -Recurse -File | ForEach-Object { $_.IsReadOnly = $true }
[pscustomobject]@{
    mode = $manifestObject.mode
    executionCommit = $commit
    localSnapshot = $outputRoot
    immutableHandoff = $HandoffDestination
    routes = $report.completedRoutes
    observations = $report.totalObservations
    attemptedStarts = $report.attemptedStartProposals
    acceptedStarts = $report.acceptedStarts
    safetyFailures = $report.safetyFailures
} | ConvertTo-Json
