param(
    [string]$UnityPath = "C:\Program Files\Unity\Hub\Editor\6000.3.9f1\Editor\Unity.exe",
    [string]$PythonPath = "C:\Users\Vinh\anaconda3\python.exe",
    [string]$JobId = ""
)

$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$manifestRelative = "Tools/ThoracicUSCoach/experiments/phase2_fixed_anatomy_engineering_poc_v1.json"
$manifestPath = Join-Path $projectRoot $manifestRelative
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
$outputDirectory = Join-Path $projectRoot $manifest.outputDirectory
$bridgeDirectory = Join-Path $projectRoot "Artifacts\ThoracicUSCoach\bridge"

function Get-Sha256([string]$path) {
    $stream = [System.IO.File]::OpenRead($path)
    try {
        $algorithm = [System.Security.Cryptography.SHA256]::Create()
        try { return ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace("-", "").ToLowerInvariant() }
        finally { $algorithm.Dispose() }
    }
    finally { $stream.Dispose() }
}

if (-not (Test-Path -LiteralPath $UnityPath)) { throw "Unity executable missing: $UnityPath" }
if (-not (Test-Path -LiteralPath $PythonPath)) { throw "Python executable missing: $PythonPath" }
if (Test-Path -LiteralPath (Join-Path $outputDirectory "phase2_engineering_poc_unity.json")) {
    throw "Engineering POC outcome already exists; refusing an unapproved repeat"
}
$existingEditor = Get-Process -Name Unity -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.Equals($UnityPath, [StringComparison]::OrdinalIgnoreCase) }
if ($existingEditor) { throw "Exclusive Unity preflight failed: Unity Editor is already running" }

Push-Location $projectRoot
try {
    & git diff --quiet --exit-code
    if ($LASTEXITCODE -ne 0) { throw "Engineering POC requires a committed tracked worktree" }
    & git diff --cached --quiet --exit-code
    if ($LASTEXITCODE -ne 0) { throw "Engineering POC requires an empty index" }
    $executionCommit = (& git rev-parse HEAD).Trim()
}
finally {
    Pop-Location
}
$env:THORACIC_U08_P1_EXECUTION_COMMIT = $executionCommit

foreach ($entry in @(
    @($manifest.phaseOneCheckpoint, $manifest.phaseOneCheckpointSha256),
    @($manifest.stopCheckpoint, $manifest.stopCheckpointSha256),
    @($manifest.diagnosticStopCheckpoint, $manifest.diagnosticStopCheckpointSha256),
    @($manifest.actionCheckpoint, $manifest.actionCheckpointSha256),
    @($manifest.policyServer, $manifest.policyServerSha256)
)) {
    $actual = Get-Sha256 (Join-Path $projectRoot $entry[0])
    if ($actual -ne $entry[1].ToLowerInvariant()) { throw "Frozen hash mismatch: $($entry[0])" }
}

New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $bridgeDirectory | Out-Null
$testResults = Join-Path $outputDirectory "phase2_poc_preflight.xml"
$testLog = Join-Path $outputDirectory "phase2_poc_preflight.log"
$testFilter = "CSSALT.ThoracicUSCoach.Tests.Active2DSafetyTests;CSSALT.ThoracicUSCoach.Tests.U08LiveIntegrationTests"
$testProcess = Start-Process -FilePath $UnityPath -ArgumentList @(
    "-batchmode", "-nographics", "-projectPath", $projectRoot,
    "-runTests", "-testPlatform", "EditMode", "-testFilter", $testFilter,
    "-testResults", $testResults, "-logFile", $testLog
) -WindowStyle Hidden -PassThru
if (-not $testProcess.WaitForExit(600000)) {
    Stop-Process -Id $testProcess.Id -Force
    throw "Unity preflight exceeded the 10-minute hard timeout"
}
if (-not (Test-Path -LiteralPath $testResults)) { throw "Unity preflight produced no XML" }
[xml]$testXml = Get-Content -Raw -LiteralPath $testResults
$testRun = $testXml.'test-run'
if ([int]$testRun.failed -ne 0 -or [int]$testRun.inconclusive -ne 0 -or [int]$testRun.passed -lt 1) {
    throw "Unity preflight failed: $($testRun.passed)/$($testRun.total) passed"
}

Remove-Item -LiteralPath (Join-Path $bridgeDirectory "active2d_request.json") -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $bridgeDirectory "active2d_response.json") -Force -ErrorAction SilentlyContinue
$serverOut = Join-Path $outputDirectory "policy_server_stdout.log"
$serverErr = Join-Path $outputDirectory "policy_server_stderr.log"
$unityLog = Join-Path $outputDirectory "unity_poc.log"
$serverArguments = @(
    (Join-Path $projectRoot "Tools\ThoracicUSCoach\run_active2d_policy_server.py"),
    "--checkpoint", (Join-Path $projectRoot $manifest.phaseOneCheckpoint),
    "--handoff-threshold", "0.85",
    "--diagnostic-template-checkpoint", (Join-Path $projectRoot $manifest.diagnosticStopCheckpoint),
    "--phase2-action-checkpoint", (Join-Path $projectRoot $manifest.actionCheckpoint),
    "--phase-two-only-pilot",
    "--phase2-exploration-limit", ([string]$manifest.diagnosticExplorationLimit),
    "--bridge", $bridgeDirectory
)
$server = Start-Process -FilePath $PythonPath -ArgumentList $serverArguments `
    -WorkingDirectory (Join-Path $projectRoot "Tools\ThoracicUSCoach") `
    -RedirectStandardOutput $serverOut -RedirectStandardError $serverErr `
    -WindowStyle Hidden -PassThru
try {
    Start-Sleep -Seconds 1
    if ($server.HasExited) { throw "Policy server exited before Unity" }
    $unity = Start-Process -FilePath $UnityPath -ArgumentList @(
        "-batchmode", "-projectPath", $projectRoot,
        "-executeMethod", "CSSALT.ThoracicUSCoach.Editor.ThoracicU08P1Batch.Run",
        "-u08P1Manifest", $manifestRelative, "-u08P1Policy", "candidate",
        "-logFile", $unityLog
    ) -WindowStyle Hidden -PassThru
    if (-not $unity.WaitForExit(1200000)) {
        Stop-Process -Id $unity.Id -Force
        throw "Unity engineering POC exceeded the 20-minute hard timeout"
    }
    if ($unity.ExitCode -ne 0) { throw "Unity engineering POC failed with exit code $($unity.ExitCode)" }
}
finally {
    if (-not $server.HasExited) { Stop-Process -Id $server.Id -Force }
    $server.WaitForExit()
}

& $PythonPath (Join-Path $projectRoot "Tools\ThoracicUSCoach\summarize_phase2_engineering_poc.py") `
    --root $projectRoot --output-directory $outputDirectory --job-id $JobId
if ($LASTEXITCODE -ne 0) { throw "Engineering POC summarizer failed" }

[ordered]@{
    protocolId = $manifest.protocolId
    executionCommit = $executionCommit
    unityExitCode = 0
    preflightPassed = [int]$testRun.passed
    preflightTotal = [int]$testRun.total
    episodeCount = $manifest.requiredStarts
    stepBudget = $manifest.stepBudget
    explorationLimit = $manifest.diagnosticExplorationLimit
    result = "phase2_engineering_poc_summary.json"
} | ConvertTo-Json -Depth 4 | Set-Content -Encoding utf8 (Join-Path $outputDirectory "job_summary.json")
