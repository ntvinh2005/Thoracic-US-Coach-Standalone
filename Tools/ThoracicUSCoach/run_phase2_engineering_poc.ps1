param(
    [string]$UnityPath = "C:\Program Files\Unity\Hub\Editor\6000.3.9f1\Editor\Unity.exe",
    [string]$PythonPath = "C:\Users\Vinh\anaconda3\python.exe",
    [string]$JobId = "",
    [string]$ProjectRoot = "",
    [switch]$PreflightOnly
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
}
else {
    $projectRoot = (Resolve-Path -LiteralPath $ProjectRoot).Path
}
$gitMetadata = Join-Path $projectRoot ".git"
$gitMetadataItem = Get-Item -Force -LiteralPath $gitMetadata -ErrorAction SilentlyContinue
$gitEnvironment = [ordered]@{}
foreach ($name in @("GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR")) {
    $gitEnvironment[$name] = [Environment]::GetEnvironmentVariable($name)
}
[ordered]@{
    pwd = $PWD.Path
    psScriptRoot = $PSScriptRoot
    projectRoot = $projectRoot
    gitMetadataExists = $null -ne $gitMetadataItem
    gitMetadataType = if ($null -eq $gitMetadataItem) { "missing" } elseif ($gitMetadataItem.PSIsContainer) { "directory" } else { "file" }
    gitMetadataContent = if ($null -ne $gitMetadataItem -and -not $gitMetadataItem.PSIsContainer) { (Get-Content -Raw -LiteralPath $gitMetadata).Trim() } else { "" }
    gitEnvironment = $gitEnvironment
} | ConvertTo-Json -Depth 4 | Write-Output

$gitSafeRoot = $projectRoot.Replace('\', '/')
$script:scopedGitExitCode = -1
function Invoke-ScopedGit([string[]]$Arguments) {
    $saved = [ordered]@{}
    foreach ($name in @("GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR")) {
        $saved[$name] = [Environment]::GetEnvironmentVariable($name)
        Remove-Item -Path ("Env:" + $name) -ErrorAction SilentlyContinue
    }
    try {
        & git -c ("safe.directory=" + $gitSafeRoot) -C $projectRoot @Arguments
        $script:scopedGitExitCode = $LASTEXITCODE
    }
    finally {
        foreach ($name in $saved.Keys) {
            if ($null -eq $saved[$name]) {
                Remove-Item -Path ("Env:" + $name) -ErrorAction SilentlyContinue
            }
            else {
                Set-Item -Path ("Env:" + $name) -Value $saved[$name]
            }
        }
    }
}

& git --version
Write-Output ("gitVersionExit=" + $LASTEXITCODE)
$gitTopLevel = (Invoke-ScopedGit @("rev-parse", "--show-toplevel") | Select-Object -Last 1)
Write-Output ("gitTopLevel=" + $gitTopLevel)
Write-Output ("gitTopLevelExit=" + $script:scopedGitExitCode)
$gitHead = (Invoke-ScopedGit @("rev-parse", "HEAD") | Select-Object -Last 1)
Write-Output ("gitHead=" + $gitHead)
Write-Output ("gitHeadExit=" + $script:scopedGitExitCode)
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
foreach ($required in @("Assets", "Packages", "ProjectSettings", $manifestRelative)) {
    if (-not (Test-Path -LiteralPath (Join-Path $projectRoot $required))) {
        throw "Project root is missing required Unity content: $required"
    }
}
if ($null -eq $gitMetadataItem) { throw "Project root has no Git metadata" }
if (-not $gitMetadataItem.PSIsContainer) {
    $gitPointer = (Get-Content -Raw -LiteralPath $gitMetadata).Trim()
    if (-not $gitPointer.StartsWith("gitdir: ")) { throw "Linked-worktree .git pointer is invalid" }
    $gitDirectory = $gitPointer.Substring(8).Trim()
    if (-not [System.IO.Path]::IsPathRooted($gitDirectory)) {
        $gitDirectory = [System.IO.Path]::GetFullPath((Join-Path $projectRoot $gitDirectory))
    }
    if (-not (Test-Path -LiteralPath $gitDirectory -PathType Container)) {
        throw "Linked-worktree Git directory does not exist: $gitDirectory"
    }
}
if (Test-Path -LiteralPath (Join-Path $outputDirectory "phase2_engineering_poc_unity.json")) {
    throw "Engineering POC outcome already exists; refusing an unapproved repeat"
}
$existingEditor = Get-Process -Name Unity -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.Equals($UnityPath, [StringComparison]::OrdinalIgnoreCase) }
if ($existingEditor) { throw "Exclusive Unity preflight failed: Unity Editor is already running" }

Invoke-ScopedGit @("diff", "--quiet", "--exit-code") | Out-Null
if ($script:scopedGitExitCode -ne 0) { throw "Engineering POC requires a committed tracked worktree" }
Invoke-ScopedGit @("diff", "--cached", "--quiet", "--exit-code") | Out-Null
if ($script:scopedGitExitCode -ne 0) { throw "Engineering POC requires an empty index" }
$executionCommit = (Invoke-ScopedGit @("rev-parse", "HEAD") | Select-Object -Last 1).Trim()
if ($script:scopedGitExitCode -ne 0) { throw "Could not resolve execution commit" }
$executionBranch = (Invoke-ScopedGit @("rev-parse", "--abbrev-ref", "HEAD") | Select-Object -Last 1).Trim()
if ($script:scopedGitExitCode -ne 0) { throw "Could not resolve execution branch" }
$originUrl = (Invoke-ScopedGit @("remote", "get-url", "origin") | Select-Object -Last 1).Trim()
if ($script:scopedGitExitCode -ne 0 -or $originUrl -notmatch "ntvinh2005/Thoracic-US-Coach-Standalone") {
    throw "Unexpected Git origin: $originUrl"
}
$developmentBaseCommit = "1a67ea91a80cf2f5aa98fbaa97c0ce2ccd35a693"
Invoke-ScopedGit @("merge-base", "--is-ancestor", $developmentBaseCommit, "HEAD") | Out-Null
if ($script:scopedGitExitCode -ne 0) {
    throw "Execution HEAD is not based on codex/u08-phase2 $developmentBaseCommit"
}
Write-Output ("gitBranch=" + $executionBranch)
Write-Output ("gitOrigin=" + $originUrl)
Write-Output ("developmentBase=" + $developmentBaseCommit)
$env:THORACIC_U08_P1_EXECUTION_COMMIT = $executionCommit

if ($PreflightOnly) {
    Write-Output ("PHASE2_POC_PREFLIGHT_PASS head=" + $executionCommit)
    exit 0
}

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

$summaryArguments = @(
    (Join-Path $projectRoot "Tools\ThoracicUSCoach\summarize_phase2_engineering_poc.py"),
    "--root", $projectRoot,
    "--output-directory", $outputDirectory
)
if (-not [string]::IsNullOrWhiteSpace($JobId)) {
    $summaryArguments += @("--job-id", $JobId)
}
& $PythonPath $summaryArguments
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
