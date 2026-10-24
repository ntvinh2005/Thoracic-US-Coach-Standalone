param(
    [string]$UnityPath = "C:\Program Files\Unity\Hub\Editor\6000.3.9f1\Editor\Unity.exe",
    [string]$PythonPath = "C:\Users\Vinh\anaconda3\python.exe"
)

$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$manifestRelative = "Tools/ThoracicUSCoach/experiments/u08_p1_paired_v1.json"
$manifestPath = Join-Path $projectRoot $manifestRelative
$outputDirectory = Join-Path $projectRoot "Artifacts\ThoracicUSCoach\active2d\evaluation\u08_p1"
$bridgeDirectory = Join-Path $projectRoot "Artifacts\ThoracicUSCoach\bridge"
$testResults = Join-Path $outputDirectory "u08_p1_preflight_editmode.xml"
$testLog = Join-Path $outputDirectory "u08_p1_preflight_editmode.log"

function Get-Sha256([string]$path) {
    $stream = [System.IO.File]::OpenRead($path)
    try {
        $algorithm = [System.Security.Cryptography.SHA256]::Create()
        try {
            return ([System.BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace("-", "").ToLowerInvariant()
        }
        finally { $algorithm.Dispose() }
    }
    finally { $stream.Dispose() }
}

New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $bridgeDirectory | Out-Null

if (-not (Test-Path -LiteralPath $UnityPath)) { throw "Frozen Unity executable is missing: $UnityPath" }
if (-not (Test-Path -LiteralPath $PythonPath)) { throw "Python executable is missing: $PythonPath" }
$existingEditor = Get-Process -Name Unity -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.Equals($UnityPath, [System.StringComparison]::OrdinalIgnoreCase) }
if ($existingEditor) { throw "Exclusive Unity preflight failed: Unity Editor is already running" }

& git -C $projectRoot diff --quiet --exit-code
if ($LASTEXITCODE -ne 0) { throw "U08-P1 requires a committed clean controlled worktree" }
$executionCommit = (& git -C $projectRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $executionCommit.Length -ne 40) { throw "Execution commit is unavailable" }
$env:THORACIC_U08_P1_EXECUTION_COMMIT = $executionCommit

$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
foreach ($entry in @(
    @($manifest.phaseOneCheckpoint, $manifest.phaseOneCheckpointSha256),
    @($manifest.stopCheckpoint, $manifest.stopCheckpointSha256),
    @($manifest.actionCheckpoint, $manifest.actionCheckpointSha256),
    @($manifest.policyServer, $manifest.policyServerSha256)
)) {
    $actual = Get-Sha256 (Join-Path $projectRoot $entry[0])
    if ($actual -ne $entry[1].ToLowerInvariant()) { throw "Frozen hash mismatch: $($entry[0])" }
}

$testFilter = "CSSALT.ThoracicUSCoach.Tests.ThoracicUSCoachTests;CSSALT.ThoracicUSCoach.Tests.Active2DSafetyTests"
$testProcess = Start-Process -FilePath $UnityPath -ArgumentList @(
    "-batchmode", "-nographics", "-projectPath", $projectRoot,
    "-runTests", "-testPlatform", "EditMode", "-testFilter", $testFilter,
    "-testResults", $testResults, "-logFile", $testLog
) -WindowStyle Hidden -Wait -PassThru
$unityTestExit = $testProcess.ExitCode
if (-not (Test-Path -LiteralPath $testResults)) { throw "Unity preflight produced no test XML (exit $unityTestExit)" }
[xml]$testXml = Get-Content -Raw -LiteralPath $testResults
$testRun = $testXml.'test-run'
$total = [int]$testRun.total
$passed = [int]$testRun.passed
$failed = [int]$testRun.failed
$inconclusive = [int]$testRun.inconclusive
$skipped = [int]$testRun.skipped
$preflight = [ordered]@{
    executionCommit = $executionCommit
    unityVersion = $manifest.unityVersion
    filter = $testFilter
    total = $total
    passed = $passed
    failed = $failed
    inconclusive = $inconclusive
    skipped = $skipped
    unityExitCode = $unityTestExit
    resultSha256 = Get-Sha256 $testResults
}
$preflight | ConvertTo-Json -Depth 4 | Set-Content -Encoding utf8 (Join-Path $outputDirectory "u08_p1_preflight.json")
if ($total -ne 31 -or $passed -ne 31 -or $failed -ne 0 -or $inconclusive -ne 0) {
    throw "PILOT_BLOCKED_PREFLIGHT: expected 31/31, observed $passed/$total with $failed failures"
}

function Invoke-U08PolicySession([string]$policy) {
    Remove-Item -LiteralPath (Join-Path $bridgeDirectory "active2d_request.json") -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path $bridgeDirectory "active2d_response.json") -Force -ErrorAction SilentlyContinue
    $serverLog = Join-Path $outputDirectory "u08_p1_${policy}_server_stdout.log"
    $serverError = Join-Path $outputDirectory "u08_p1_${policy}_server_stderr.log"
    $unityLog = Join-Path $outputDirectory "u08_p1_${policy}_unity.log"
    $serverArguments = @(
        (Join-Path $projectRoot "Tools\ThoracicUSCoach\run_active2d_policy_server.py"),
        "--checkpoint", (Join-Path $projectRoot $manifest.phaseOneCheckpoint),
        "--handoff-threshold", "0.85",
        "--stop-checkpoint", (Join-Path $projectRoot $manifest.stopCheckpoint),
        "--stop-threshold", "0.93",
        "--phase-two-only-pilot",
        "--bridge", $bridgeDirectory
    )
    if ($policy -eq "candidate") {
        $serverArguments += @("--phase2-action-checkpoint", (Join-Path $projectRoot $manifest.actionCheckpoint))
    }
    $server = Start-Process -FilePath $PythonPath -ArgumentList $serverArguments `
        -WorkingDirectory (Join-Path $projectRoot "Tools\ThoracicUSCoach") `
        -RedirectStandardOutput $serverLog -RedirectStandardError $serverError `
        -WindowStyle Hidden -PassThru
    try {
        Start-Sleep -Seconds 1
        if ($server.HasExited) { throw "Policy server exited before Unity ($policy)" }
        $unityProcess = Start-Process -FilePath $UnityPath -ArgumentList @(
            "-batchmode", "-projectPath", $projectRoot,
            "-executeMethod", "CSSALT.ThoracicUSCoach.Editor.ThoracicU08P1Batch.Run",
            "-u08P1Manifest", $manifestRelative, "-u08P1Policy", $policy,
            "-logFile", $unityLog
        ) -WindowStyle Hidden -Wait -PassThru
        if ($unityProcess.ExitCode -ne 0) {
            throw "Unity $policy session failed with exit code $($unityProcess.ExitCode)"
        }
    }
    finally {
        if (-not $server.HasExited) { Stop-Process -Id $server.Id -Force }
        $server.WaitForExit()
    }
}

Invoke-U08PolicySession "raster"
Invoke-U08PolicySession "candidate"

& $PythonPath (Join-Path $projectRoot "Tools\ThoracicUSCoach\evaluate_u08_p1.py") `
    --root $projectRoot --output-directory $outputDirectory
if ($LASTEXITCODE -ne 0) { throw "U08-P1 evaluator failed" }

$jobSummary = [ordered]@{
    protocolId = $manifest.protocolId
    executionCommit = $executionCommit
    totalClosedLoopEpisodes = 4
    rasterEpisodes = 2
    candidateEpisodes = 2
    masterReport = "Artifacts/ThoracicUSCoach/active2d/evaluation/u08_p1/u08_p1_master_report.json"
}
$jobSummary | ConvertTo-Json -Depth 4 | Set-Content -Encoding utf8 (Join-Path $outputDirectory "u08_p1_job_summary.json")
