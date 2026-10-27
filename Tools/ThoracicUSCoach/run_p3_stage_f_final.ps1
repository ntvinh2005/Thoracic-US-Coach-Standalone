param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('EngineeringPreflight', 'Outcome')]
    [string]$Mode,
    [Parameter(Mandatory = $true)] [string]$Manifest,
    [Parameter(Mandatory = $true)] [string]$ExpectedManifestSha256,
    [string]$HandoffDestination = 'D:\CSSALT\Thoracic-US-Coach-P3-Handoff\stage_f_final\p3_stage_f_final_20261026',
    [string]$UnityPath = 'C:\Program Files\Unity\Hub\Editor\6000.3.9f1\Editor\Unity.exe'
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

function Get-RawSha256([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { $hash = $algorithm.ComputeHash($stream) } finally { $stream.Dispose(); $algorithm.Dispose() }
    ([BitConverter]::ToString($hash) -replace '-', '').ToLowerInvariant()
}

function Get-CanonicalSha256([string]$Path) {
    $text = [IO.File]::ReadAllText($Path).Replace("`r`n", "`n").Replace("`r", "`n")
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($text)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { $hash = $algorithm.ComputeHash($bytes) } finally { $algorithm.Dispose() }
    ([BitConverter]::ToString($hash) -replace '-', '').ToLowerInvariant()
}

$manifestPath = if ([IO.Path]::IsPathRooted($Manifest)) {
    (Resolve-Path -LiteralPath $Manifest).Path
} else {
    (Resolve-Path -LiteralPath (Join-Path $projectRoot $Manifest)).Path
}
if ((Get-RawSha256 $manifestPath) -ne $ExpectedManifestSha256.ToLowerInvariant()) {
    throw 'P3 Stage F-Final prefrozen manifest hash mismatch'
}
$manifestObject = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
if ($manifestObject.protocolId -ne 'G2_PHASE2_FINAL_SPRINT_P3_V1' -or
    $manifestObject.mode -ne 'P3_STAGE_F_FINAL') {
    throw 'P3 Stage F-Final protocol/mode mismatch'
}
$protocolPath = Join-Path $projectRoot $manifestObject.auditProtocolPath
if ((Get-CanonicalSha256 $protocolPath) -ne $manifestObject.auditProtocolCanonicalSha256) {
    throw 'P3 Stage F-Final protocol hash mismatch'
}
$safeRoot = $projectRoot.Replace('\', '/')
$head = (& git -c "safe.directory=$safeRoot" -C $projectRoot rev-parse HEAD).Trim()
$branch = (& git -c "safe.directory=$safeRoot" -C $projectRoot branch --show-current).Trim()
& git -c "safe.directory=$safeRoot" -C $projectRoot merge-base --is-ancestor $manifestObject.sourceCommit $head
if ($LASTEXITCODE -ne 0 -or $branch -ne 'codex/p2-unity-final') {
    throw 'P3 Stage F-Final source commit is not an ancestor of the isolated execution branch'
}
$resolvedUnity = (Resolve-Path -LiteralPath $UnityPath).Path
$activeEditor = Get-Process -Name Unity -ErrorAction SilentlyContinue | Where-Object {
    try { (Resolve-Path -LiteralPath $_.Path).Path -eq $resolvedUnity } catch { $false }
}
if ($activeEditor) { throw 'P3 Stage F-Final exclusive Unity preflight failed' }

$logRoot = Join-Path $projectRoot 'Artifacts\ThoracicUSCoach\active2d\evaluation\p3_stage_f_final'
New-Item -ItemType Directory -Path $logRoot -Force | Out-Null
$testXml = Join-Path $logRoot 'p3_stage_f_final_editmode.xml'
$testLog = Join-Path $logRoot 'p3_stage_f_final_editmode.log'
$artifactPath = Join-Path $projectRoot $manifestObject.syntheticFaultPlan.liveTestArtifactPath
$productionArtifactPath = Join-Path $projectRoot $manifestObject.syntheticFaultPlan.productionReducerArtifactPath
$env:THORACIC_P3_EXECUTION_COMMIT = $manifestObject.sourceCommit
$env:THORACIC_P3_FAULT_TEST_ARTIFACT = $artifactPath
$python = "$env:USERPROFILE\anaconda3\python.exe"
Push-Location (Join-Path $projectRoot 'Tools\ThoracicUSCoach')
try {
    & $python generate_p3_production_fault_artifact.py `
        --source-commit $manifestObject.sourceCommit `
        --output $productionArtifactPath
} finally {
    Pop-Location
}
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $productionArtifactPath)) {
    throw 'P3 production feedback reducer preflight failed'
}
$productionArtifact = Get-Content -Raw -LiteralPath $productionArtifactPath | ConvertFrom-Json
if ($productionArtifact.result -ne 'PASS' -or
    $productionArtifact.sourceCommit -ne $manifestObject.sourceCommit -or
    $productionArtifact.exportedBy -ne 'Python' -or
    $productionArtifact.module -ne 'Tools.ThoracicUSCoach.run_active2d_policy_server') {
    throw 'P3 production feedback reducer artifact invalid'
}
$testProcess = Start-Process -FilePath $resolvedUnity -ArgumentList @(
    '-batchmode','-projectPath',$projectRoot,'-runTests','-testPlatform','EditMode',
    '-testFilter','CSSALT.ThoracicUSCoach.Tests.ThoracicP2FreshnessTests;CSSALT.ThoracicUSCoach.Tests.ThoracicP3FaultInjectionTests',
    '-testResults',$testXml,'-logFile',$testLog
) -WindowStyle Hidden -PassThru
if (-not $testProcess.WaitForExit(900000)) {
    try { $testProcess.Kill() } catch {}
    throw 'P3 Stage F-Final EditMode preflight exceeded 900 seconds'
}
$testProcess.Refresh()
if (-not (Test-Path -LiteralPath $testXml)) {
    throw "P3 Stage F-Final preflight produced no XML (Unity exit $($testProcess.ExitCode))"
}
[xml]$testResult = Get-Content -Raw -LiteralPath $testXml
if ([int]$testResult.'test-run'.failed -ne 0 -or -not (Test-Path -LiteralPath $artifactPath)) {
    throw 'P3 Stage F-Final engineering preflight failed'
}
$artifact = Get-Content -Raw -LiteralPath $artifactPath | ConvertFrom-Json
if ($artifact.result -ne 'PASS' -or $artifact.sourceCommit -ne $manifestObject.sourceCommit -or
    $artifact.exportedBy -ne 'CSharp') {
    throw 'P3 Stage F-Final live C# test artifact invalid'
}
if ($Mode -eq 'EngineeringPreflight') {
    [pscustomobject]@{
        status = 'ENGINEERING_PREFLIGHT_PASS'
        sourceCommit = $manifestObject.sourceCommit
        manifestSha256 = $ExpectedManifestSha256.ToLowerInvariant()
        protocolCanonicalSha256 = $manifestObject.auditProtocolCanonicalSha256
        passed = [int]$testResult.'test-run'.passed
        failed = [int]$testResult.'test-run'.failed
        liveTestArtifact = $manifestObject.syntheticFaultPlan.liveTestArtifactPath
        liveTestArtifactSha256 = Get-RawSha256 $artifactPath
        productionReducerArtifact = $manifestObject.syntheticFaultPlan.productionReducerArtifactPath
        productionReducerArtifactSha256 = Get-RawSha256 $productionArtifactPath
    } | ConvertTo-Json
    exit 0
}

$outputRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot $manifestObject.outputDirectory))
if ((Test-Path -LiteralPath $outputRoot) -or (Test-Path -LiteralPath ($outputRoot + '.partial'))) {
    throw 'P3 Stage F-Final refuses to overwrite output'
}
$handoffFinal = [IO.Path]::GetFullPath($HandoffDestination)
if ((Test-Path -LiteralPath $handoffFinal) -or (Test-Path -LiteralPath ($handoffFinal + '.partial'))) {
    throw 'P3 Stage F-Final refuses to overwrite handoff'
}
$env:THORACIC_P3_MANIFEST_SHA256 = $ExpectedManifestSha256.ToLowerInvariant()
$rootUri = [Uri]::new($projectRoot.TrimEnd('\') + '\')
$relativeManifest = [Uri]::UnescapeDataString(
    $rootUri.MakeRelativeUri([Uri]::new($manifestPath)).ToString())
$captureLog = Join-Path $logRoot 'p3_stage_f_final_capture.log'
$process = Start-Process -FilePath $resolvedUnity -ArgumentList @(
    '-batchmode','-projectPath',$projectRoot,'-executeMethod',
    'CSSALT.ThoracicUSCoach.Editor.ThoracicP3StageFFinalBatch.Run',
    '-p3StageFFinalManifest',$relativeManifest,'-logFile',$captureLog
) -WindowStyle Hidden -PassThru
if (-not $process.WaitForExit(2100000)) {
    try { $process.Kill() } catch {}
    throw 'P3 Stage F-Final outcome exceeded 2100 seconds'
}
$process.Refresh()
if ($process.ExitCode -ne 0) {
    throw "P3 Stage F-Final Unity failed exit $($process.ExitCode); preserve $captureLog"
}
foreach ($name in @('stage_f_final_manifest.json','stage_f_final_report.json','evidence_index.json','snapshot_seal.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $outputRoot $name) -PathType Leaf)) {
        throw "P3 Stage F-Final snapshot missing $name"
    }
}
$seal = Get-Content -Raw (Join-Path $outputRoot 'snapshot_seal.json') | ConvertFrom-Json
$report = Get-Content -Raw (Join-Path $outputRoot 'stage_f_final_report.json') | ConvertFrom-Json
$index = Get-Content -Raw (Join-Path $outputRoot 'evidence_index.json') | ConvertFrom-Json
if (-not $report.complete -or $report.infrastructureFailure -or -not $seal.complete -or
    -not $seal.immutable -or $seal.failureSnapshot) {
    throw 'P3 Stage F-Final snapshot incomplete or failed'
}
if ((Get-RawSha256 (Join-Path $outputRoot 'stage_f_final_manifest.json')) -ne $seal.manifestSha256 -or
    (Get-RawSha256 (Join-Path $outputRoot 'stage_f_final_report.json')) -ne $seal.reportSha256 -or
    (Get-RawSha256 (Join-Path $outputRoot 'evidence_index.json')) -ne $seal.evidenceIndexSha256) {
    throw 'P3 Stage F-Final seal hash mismatch'
}
foreach ($entry in $index.files) {
    $path = [IO.Path]::GetFullPath((Join-Path $outputRoot $entry.path))
    if (-not $path.StartsWith($outputRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $path) -or (Get-RawSha256 $path) -ne $entry.sha256 -or
        (Get-Item -LiteralPath $path).Length -ne [long]$entry.bytes) {
        throw "P3 Stage F-Final indexed file mismatch: $($entry.path)"
    }
}
New-Item -ItemType Directory -Path (Split-Path -Parent $handoffFinal) -Force | Out-Null
Copy-Item -LiteralPath $outputRoot -Destination ($handoffFinal + '.partial') -Recurse
Move-Item -LiteralPath ($handoffFinal + '.partial') -Destination $handoffFinal
Get-ChildItem -LiteralPath $handoffFinal -Recurse -File | ForEach-Object { $_.IsReadOnly = $true }
[pscustomobject]@{
    status = 'STAGE_F_FINAL_SNAPSHOT_SEALED'
    handoff = $handoffFinal
    sourceCommit = $manifestObject.sourceCommit
    manifestSha256 = $ExpectedManifestSha256.ToLowerInvariant()
    physicalRecordCount = $seal.physicalRecordCount
    syntheticTrialCount = $seal.syntheticTrialCount
} | ConvertTo-Json
