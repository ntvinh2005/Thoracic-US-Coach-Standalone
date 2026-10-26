param(
    [Parameter(Mandatory = $true)] [string]$Manifest,
    [string]$HandoffDestination = 'D:\CSSALT\Thoracic-US-Coach-R6-Handoff\basin\g2_r6_basin_20261026',
    [string]$UnityPath = 'C:\Program Files\Unity\Hub\Editor\6000.3.9f1\Editor\Unity.exe',
    [switch]$SkipPreflight
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
function Get-R6RawSha256([string]$Path) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { $hash = $algorithm.ComputeHash([IO.File]::ReadAllBytes($Path)) } finally { $algorithm.Dispose() }
    ([BitConverter]::ToString($hash) -replace '-', '').ToLowerInvariant()
}
function Get-R6CanonicalSha256([string]$Path) {
    $text = [IO.File]::ReadAllText($Path).Replace("`r`n", "`n").Replace("`r", "`n")
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($text)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { $hash = $algorithm.ComputeHash($bytes) } finally { $algorithm.Dispose() }
    ([BitConverter]::ToString($hash) -replace '-', '').ToLowerInvariant()
}
$manifestPath = if ([IO.Path]::IsPathRooted($Manifest)) {
    (Resolve-Path -LiteralPath $Manifest).Path
} else { (Resolve-Path -LiteralPath (Join-Path $projectRoot $Manifest)).Path }
if (-not $manifestPath.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'R6 manifest must be inside this isolated worktree'
}
if (-not (Test-Path -LiteralPath $UnityPath)) { throw "Unity executable missing: $UnityPath" }
$resolvedUnity = (Resolve-Path -LiteralPath $UnityPath).Path
$activeEditor = Get-Process -Name Unity -ErrorAction SilentlyContinue | Where-Object {
    try { (Resolve-Path -LiteralPath $_.Path).Path -eq $resolvedUnity } catch { $false }
}
if ($activeEditor) { throw 'Exclusive Unity preflight failed: the requested Unity Editor is already running' }

$manifestObject = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifestObject.mode -notin @(
    'R6_POSITIVE_CONTROL_BASIN','R6_TARGET_INDEPENDENT_WIDE_SCOUT','R6_BOUNDED_DEVELOPMENT')) {
    throw 'Invalid R6 capture mode'
}
if ($manifestObject.mode -eq 'R6_POSITIVE_CONTROL_BASIN') {
    $requiredBasinHandoff = [IO.Path]::GetFullPath('D:\CSSALT\Thoracic-US-Coach-R6-Handoff\basin\g2_r6_basin_20261026')
    if ([IO.Path]::GetFullPath($HandoffDestination) -ne $requiredBasinHandoff) {
        throw "Stage B immutable handoff destination changed: $HandoffDestination"
    }
}
$safeRoot = $projectRoot.Replace('\','/')
$commit = (& git -c "safe.directory=$safeRoot" -C $projectRoot rev-parse HEAD).Trim()
$branch = (& git -c "safe.directory=$safeRoot" -C $projectRoot branch --show-current).Trim()
if ($LASTEXITCODE -ne 0 -or $commit -ne $manifestObject.sourceCommit) {
    throw "Execution commit does not match frozen manifest: HEAD=$commit manifest=$($manifestObject.sourceCommit)"
}
if ($branch -ne 'codex/r6-unity-final' -or $branch -ne $manifestObject.captureBranch) {
    throw "Execution branch does not match frozen R6 branch: $branch"
}
$outputRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot $manifestObject.outputDirectory))
if (-not $outputRoot.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'R6 output escapes isolated worktree'
}
if ((Test-Path -LiteralPath $outputRoot) -or (Test-Path -LiteralPath ($outputRoot + '.partial'))) {
    throw "R6 refuses to overwrite final or partial output: $outputRoot"
}

$logRoot = Join-Path $projectRoot 'Artifacts\ThoracicUSCoach\active2d\evaluation\r6_final_sprint'
New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
if (-not $SkipPreflight) {
    $testXml = Join-Path $logRoot 'r6_preflight_editmode.xml'
    $testLog = Join-Path $logRoot 'r6_preflight_editmode.log'
    $testProcess = Start-Process -FilePath $UnityPath -ArgumentList @(
        '-batchmode','-projectPath',$projectRoot,'-runTests','-testPlatform','EditMode',
        '-testFilter','CSSALT.ThoracicUSCoach.Tests.ThoracicR6FinalSprintTests;CSSALT.ThoracicUSCoach.Tests.ThoracicR5CaptureTests;CSSALT.ThoracicUSCoach.Tests.Active2DSafetyTests',
        '-testResults',$testXml,'-logFile',$testLog
    ) -WindowStyle Hidden -Wait -PassThru
    if (-not (Test-Path -LiteralPath $testXml)) {
        throw "R6 preflight produced no XML (Unity exit $($testProcess.ExitCode)); preserve $testLog"
    }
    [xml]$testResult = Get-Content -LiteralPath $testXml -Raw
    if ([int]$testResult.'test-run'.failed -ne 0) {
        throw "R6 safety preflight failed; preserve $testLog"
    }
}

$env:THORACIC_R6_EXECUTION_COMMIT = $commit
$rootUri = [Uri]::new($projectRoot.TrimEnd('\') + '\')
$manifestUri = [Uri]::new($manifestPath)
$relativeManifest = [Uri]::UnescapeDataString($rootUri.MakeRelativeUri($manifestUri).ToString())
$captureLog = Join-Path $logRoot ("r6_capture_" + $manifestObject.mode.ToLowerInvariant() + '.log')
$captureProcess = Start-Process -FilePath $UnityPath -ArgumentList @(
    '-batchmode','-projectPath',$projectRoot,
    '-executeMethod','CSSALT.ThoracicUSCoach.Editor.ThoracicR6FinalSprintBatch.Run',
    '-r6Manifest',$relativeManifest,'-logFile',$captureLog
) -WindowStyle Hidden -Wait -PassThru
if ($captureProcess.ExitCode -ne 0) {
    throw "R6 Unity capture failed with exit $($captureProcess.ExitCode); preserve $captureLog and partial evidence"
}
$reportPath = Join-Path $outputRoot 'r6_unity_report.json'
$indexPath = Join-Path $outputRoot 'evidence_index.json'
$sealPath = Join-Path $outputRoot 'snapshot_seal.json'
$captureManifestPath = Join-Path $outputRoot 'capture_input_manifest.json'
foreach ($required in @($reportPath,$indexPath,$sealPath,$captureManifestPath)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "R6 snapshot missing $required" }
}
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
if (-not $report.complete -or -not $report.immutableSnapshotSealed -or $report.infrastructureFailure) {
    throw "R6 report incomplete: $($report.infrastructureFailure)"
}
$index = Get-Content -LiteralPath $indexPath -Raw | ConvertFrom-Json
$seal = Get-Content -LiteralPath $sealPath -Raw | ConvertFrom-Json
if (-not $seal.complete -or -not $seal.immutable -or @($index.files).Count -ne [int]$seal.indexedFiles) {
    throw 'R6 seal/index completion or count mismatch'
}
foreach ($entry in $index.files) {
    $indexedPath = [IO.Path]::GetFullPath((Join-Path $outputRoot $entry.path))
    if (-not $indexedPath.StartsWith($outputRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe indexed path: $($entry.path)"
    }
    if (-not (Test-Path -LiteralPath $indexedPath -PathType Leaf)) { throw "Indexed file missing: $($entry.path)" }
    if ((Get-R6RawSha256 $indexedPath) -ne $entry.sha256 -or (Get-Item -LiteralPath $indexedPath).Length -ne [long]$entry.bytes) {
        throw "Indexed file integrity mismatch: $($entry.path)"
    }
}
if ((Get-R6RawSha256 $indexPath) -ne $seal.evidenceIndexSha256 -or
    (Get-R6RawSha256 $reportPath) -ne $seal.reportSha256 -or
    (Get-R6RawSha256 $captureManifestPath) -ne $seal.manifestSha256) {
    throw 'R6 seal hash mismatch'
}
$identityChecks = @(
    @('coordinatorProtocol','Tools/ThoracicUSCoach/experiments/g2_r6_final_sprint_protocol_v1.json','d201227ab95e5a858003fa2985d5c118ae724de0b1740972a82c68d1f4eaf7c9'),
    @('auditProtocol','Tools/ThoracicUSCoach/experiments/g2_r6_audit_protocol_v1.json','a01d30e294939eda46f6f2090a11761bd5b37f26d6b516d05b3d3c00c19e07a5'),
    @('scene','Assets/ThoracicUSCoach/Scenes/ThoracicUSCoach_POC.unity','6c86ff30a42e9719aff712a9e48b2ca6b5cce6004bace055abbc82b64db9a02d'),
    @('evaluator','Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.cs','7480ab4ece46fbab6b70344b25212eb0c90621b2d458ed5c343e299a59d597fc')
)
foreach ($identity in $identityChecks) {
    $absoluteIdentity = Join-Path $projectRoot $identity[1]
    $canonicalField = "$($identity[0])CanonicalSha256"
    $materializedField = "$($identity[0])MaterializedSha256"
    if ((Get-R6CanonicalSha256 $absoluteIdentity) -ne $identity[2] -or
        $seal.$canonicalField -ne $identity[2] -or
        (Get-R6RawSha256 $absoluteIdentity) -ne $seal.$materializedField) {
        throw "R6 frozen identity mismatch: $($identity[0])"
    }
}
$imageCount = @($report.records | Where-Object { $_.image }).Count
if (@($report.records).Count -ne [int]$seal.recordCount -or
    $imageCount -ne [int]$seal.imageCount -or
    @($report.actions).Count -ne [int]$seal.actionCount -or
    @($report.attempts).Count -ne [int]$seal.attemptCount -or
    [int]$report.observations -ne @($report.records).Count) {
    throw 'R6 report/manifest/seal count reconciliation failed'
}
foreach ($requiredCopy in @(
    'generator_sources/ThoracicR6FinalSprintTypes.cs',
    'generator_sources/ThoracicUSCoachController.R6FinalSprint.cs',
    'generator_sources/ThoracicR6FinalSprintBatch.cs',
    'generator_sources/prepare_r6_basin_manifest.ps1',
    'generator_sources/prepare_r6_wide_scout_manifest.ps1',
    'generator_sources/run_r6_final_sprint.ps1',
    'frozen_contracts/g2_r6_final_sprint_protocol_v1.json',
    'frozen_contracts/g2_r6_audit_protocol_v1.json',
    'frozen_contracts/g2_r6_contract_identity_v1.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $outputRoot $requiredCopy) -PathType Leaf)) {
        throw "R6 snapshot omitted frozen source/contract: $requiredCopy"
    }
}

$handoffFinal = [IO.Path]::GetFullPath($HandoffDestination)
$handoffPartial = $handoffFinal + '.partial'
if ((Test-Path -LiteralPath $handoffFinal) -or (Test-Path -LiteralPath $handoffPartial)) {
    throw "Refusing to overwrite immutable R6 handoff or mutable partial: $handoffFinal"
}
New-Item -ItemType Directory -Path (Split-Path -Parent $handoffFinal) -Force | Out-Null
Copy-Item -LiteralPath $outputRoot -Destination $handoffPartial -Recurse
foreach ($entry in $index.files) {
    $copied = Join-Path $handoffPartial $entry.path
    if (-not (Test-Path -LiteralPath $copied -PathType Leaf) -or
        (Get-R6RawSha256 $copied) -ne $entry.sha256 -or
        (Get-Item -LiteralPath $copied).Length -ne [long]$entry.bytes) {
        throw "Copied R6 handoff integrity mismatch: $($entry.path)"
    }
}
foreach ($special in @('evidence_index.json','snapshot_seal.json')) {
    $source = Join-Path $outputRoot $special
    $copied = Join-Path $handoffPartial $special
    if ((Get-R6RawSha256 $source) -ne (Get-R6RawSha256 $copied)) {
        throw "Copied R6 handoff integrity mismatch: $special"
    }
}
Move-Item -LiteralPath $handoffPartial -Destination $handoffFinal
Get-ChildItem -LiteralPath $handoffFinal -Recurse -File | ForEach-Object { $_.IsReadOnly = $true }
[pscustomobject]@{
    mode=$report.mode; executionCommit=$commit; output=$outputRoot; immutableHandoff=$handoffFinal
    attemptedCandidatePoses=$report.attemptedCandidatePoses; routes=$report.routesCompleted
    observations=$report.observations; acceptable=$report.acceptableObservations
    safetyFailures=$report.safetyFailures; contactViolations=$report.contactViolations
    workspaceViolations=$report.workspaceViolations
} | ConvertTo-Json
