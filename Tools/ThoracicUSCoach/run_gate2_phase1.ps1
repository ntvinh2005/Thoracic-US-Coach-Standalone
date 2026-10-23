param(
    [string]$ProjectPath = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [string]$UnityPath = 'C:\Program Files\Unity\Hub\Editor\6000.3.9f1\Editor\Unity.exe',
    [string]$Manifest = 'Tools/ThoracicUSCoach/experiments/gate2_phase1_fresh_40_v1.json'
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath $ProjectPath).Path
$manifestPath = (Resolve-Path -LiteralPath (Join-Path $projectRoot $Manifest)).Path
$manifestData = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
$git = (Get-Command git -ErrorAction Stop).Source
$executionCommit = (& $git -c "safe.directory=$projectRoot" -C $projectRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $executionCommit.Length -ne 40) {
    throw 'Could not resolve the Gate2 execution commit.'
}
$controlledChanges = @(& $git -c "safe.directory=$projectRoot" -C $projectRoot status `
    --porcelain --untracked-files=all -- Assets Packages ProjectSettings Tools)
if ($LASTEXITCODE -ne 0 -or $controlledChanges.Count -gt 0) {
    throw 'Gate2 requires a clean committed Assets/Packages/ProjectSettings/Tools execution tree.'
}
& $git -c "safe.directory=$projectRoot" -C $projectRoot cat-file -e "$($manifestData.sourceCommit)^{commit}"
if ($LASTEXITCODE -ne 0) { throw 'Gate2 manifest sourceCommit does not exist.' }
& $git -c "safe.directory=$projectRoot" -C $projectRoot merge-base --is-ancestor $manifestData.sourceCommit $executionCommit
if ($LASTEXITCODE -ne 0) { throw 'Gate2 manifest sourceCommit is not an ancestor of the execution commit.' }
$runNonce = $executionCommit.Substring(0, 12)

if ($manifestData.requiredCaseCount -ne 40 -or $manifestData.requiredPerStratum -ne 10) {
    throw 'Gate2 manifest must contain 40 cases with 10 cases per stratum.'
}
if (-not $manifestData.phaseOneOnly -or $manifestData.stepBudget -ne 300 -or
    $manifestData.hybridEnabled -or $manifestData.landmarkCheckpoint -or $manifestData.stopCheckpoint) {
    throw 'Gate2 wrapper only permits the frozen direct-image, Phase1-only, 300-step protocol.'
}
if ([Math]::Abs([double]$manifestData.handoffThreshold - 0.85) -gt 0.0000001) {
    throw 'Gate2 handoff threshold must be exactly 0.85.'
}

$checkpointPath = (Resolve-Path -LiteralPath (Join-Path $projectRoot $manifestData.checkpoint)).Path
$serverPath = (Resolve-Path -LiteralPath (Join-Path $projectRoot $manifestData.policyServer)).Path
$checkpointHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $checkpointPath).Hash.ToLowerInvariant()
$serverHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $serverPath).Hash.ToLowerInvariant()
if ($checkpointHash -ne $manifestData.checkpointSha256.ToLowerInvariant()) {
    throw 'Frozen Gate2 checkpoint hash mismatch.'
}
if ($serverHash -ne $manifestData.policyServerSha256.ToLowerInvariant()) {
    throw 'Frozen Gate2 policy-server hash mismatch.'
}
if (-not (Test-Path -LiteralPath $UnityPath -PathType Leaf)) {
    throw "Unity executable not found: $UnityPath"
}

$artifactRoot = Join-Path $projectRoot 'Artifacts\ThoracicUSCoach\active2d'
$bridgeRoot = Join-Path $projectRoot 'Artifacts\ThoracicUSCoach\bridge'
$logRoot = Join-Path $artifactRoot 'gate2_logs'
New-Item -ItemType Directory -Force -Path $bridgeRoot, $logRoot | Out-Null
$requestPath = Join-Path $bridgeRoot 'active2d_request.json'
$responsePath = Join-Path $bridgeRoot 'active2d_response.json'
Remove-Item -LiteralPath $requestPath, $responsePath -Force -ErrorAction SilentlyContinue

$resultPath = Join-Path $projectRoot $manifestData.output
$recomputedPath = [IO.Path]::Combine(
    [IO.Path]::GetDirectoryName($resultPath),
    [IO.Path]::GetFileNameWithoutExtension($resultPath) + '_recomputed.json')
Remove-Item -LiteralPath $resultPath, $recomputedPath -Force -ErrorAction SilentlyContinue
$episodeOrders = @(0..39) + @(9999)
foreach ($order in $episodeOrders) {
    $episodeId = 'G2E_{0}_{1:D4}' -f $runNonce, $order
    $rawPath = Join-Path $artifactRoot "runs\$episodeId.json"
    $privilegedPath = Join-Path $artifactRoot "runs_privileged\$episodeId.json"
    Remove-Item -LiteralPath $rawPath, $privilegedPath -Force -ErrorAction SilentlyContinue
    $observationRoot = Join-Path $bridgeRoot 'active2d'
    if (Test-Path -LiteralPath $observationRoot -PathType Container) {
        Get-ChildItem -LiteralPath $observationRoot -File -Filter "observation_${episodeId}_*.png" |
            Remove-Item -Force
    }
}

$policyOut = Join-Path $logRoot 'gate2_policy_stdout.log'
$policyErr = Join-Path $logRoot 'gate2_policy_stderr.log'
$unityLog = Join-Path $logRoot 'gate2_unity.log'
$evaluationLog = Join-Path $logRoot 'gate2_evaluator.log'
$preflightLog = Join-Path $logRoot 'gate2_static_preflight.log'
$uv = (Get-Command uv -ErrorAction Stop).Source
Remove-Item -LiteralPath $preflightLog -Force -ErrorAction SilentlyContinue
foreach ($testFile in @('test_active2d_no_cheating.py', 'test_gate2_phase1.py')) {
    & $uv run --offline --frozen --project (Join-Path $projectRoot 'Tools\ThoracicUSCoach') `
        python (Join-Path $projectRoot "Tools\ThoracicUSCoach\$testFile") *>&1 |
        Tee-Object -FilePath $preflightLog -Append
    if ($LASTEXITCODE -ne 0) {
        throw "Gate2 static/no-cheating preflight failed in $testFile. See $preflightLog"
    }
}
$policyArguments = @(
    'run', '--offline', '--frozen', '--project', (Join-Path $projectRoot 'Tools\ThoracicUSCoach'),
    'python', $serverPath,
    '--checkpoint', $checkpointPath,
    '--bridge', $bridgeRoot,
    '--handoff-threshold', '0.85'
)

$policy = $null
$previousExecutionCommit = $env:THORACIC_GATE2_EXECUTION_COMMIT
$previousRunNonce = $env:THORACIC_GATE2_RUN_NONCE
try {
    $env:THORACIC_GATE2_EXECUTION_COMMIT = $executionCommit
    $env:THORACIC_GATE2_RUN_NONCE = $runNonce
    $policy = Start-Process -FilePath $uv -ArgumentList $policyArguments -WorkingDirectory $projectRoot `
        -RedirectStandardOutput $policyOut -RedirectStandardError $policyErr -WindowStyle Hidden -PassThru
    Start-Sleep -Milliseconds 750
    if ($policy.HasExited) {
        throw "Image-only policy server exited during startup. See $policyErr"
    }

    $unityArguments = @(
        '-batchmode',
        '-projectPath', $projectRoot,
        '-executeMethod', 'CSSALT.ThoracicUSCoach.Editor.ThoracicGate2Batch.Run',
        '-gate2Manifest', $Manifest,
        '-logFile', $unityLog
    )
    $unity = Start-Process -FilePath $UnityPath -ArgumentList $unityArguments -WorkingDirectory $projectRoot `
        -WindowStyle Hidden -Wait -PassThru
    if ($unity.ExitCode -ne 0) {
        throw "Unity Gate2 evaluator failed with exit code $($unity.ExitCode). See $unityLog"
    }

    $evaluator = Join-Path $projectRoot 'Tools\ThoracicUSCoach\evaluate_gate2_phase1.py'
    & $uv run --offline --frozen --project (Join-Path $projectRoot 'Tools\ThoracicUSCoach') `
        python $evaluator --manifest $manifestPath --result $resultPath *>&1 | Tee-Object -FilePath $evaluationLog
    if ($LASTEXITCODE -ne 0) {
        throw "Gate2 result evaluation failed. See $evaluationLog"
    }
}
finally {
    if ($null -ne $policy -and -not $policy.HasExited) {
        Stop-Process -Id $policy.Id -Force
        $policy.WaitForExit()
    }
    $env:THORACIC_GATE2_EXECUTION_COMMIT = $previousExecutionCommit
    $env:THORACIC_GATE2_RUN_NONCE = $previousRunNonce
}
