param(
    [string]$ProjectPath = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path,
    [Parameter(Mandatory = $true)]
    [string]$DataRoot,
    [string]$Manifest = 'Tools/ThoracicUSCoach/experiments/u08_phase2_action_response_v1.json'
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath $ProjectPath).Path
$dataRootPath = (Resolve-Path -LiteralPath $DataRoot).Path
$manifestPath = (Resolve-Path -LiteralPath (Join-Path $projectRoot $Manifest)).Path
$manifestData = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
$git = (Get-Command git -ErrorAction Stop).Source
$executionCommit = (& $git -c "safe.directory=$projectRoot" -C $projectRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $executionCommit.Length -ne 40) {
    throw 'Could not resolve the U08 execution commit.'
}
$controlledChanges = @(& $git -c "safe.directory=$projectRoot" -C $projectRoot status `
    --porcelain --untracked-files=all -- Assets Packages ProjectSettings Tools)
if ($LASTEXITCODE -ne 0 -or $controlledChanges.Count -gt 0) {
    throw 'U08 requires a clean committed Assets/Packages/ProjectSettings/Tools execution tree.'
}
& $git -c "safe.directory=$projectRoot" -C $projectRoot cat-file -e "$($manifestData.sourceCommit)^{commit}"
if ($LASTEXITCODE -ne 0) { throw 'U08 manifest sourceCommit does not exist.' }
& $git -c "safe.directory=$projectRoot" -C $projectRoot merge-base --is-ancestor $manifestData.sourceCommit $executionCommit
if ($LASTEXITCODE -ne 0) { throw 'U08 manifest sourceCommit is not an ancestor of execution HEAD.' }

if ($manifestData.taskId -ne 'U08' -or $manifestData.livePolicyIntegration -or $manifestData.unityRequired) {
    throw 'U08 wrapper only permits the frozen offline experiment.'
}
if (($manifestData.allowedActions -join ',') -ne 'MEDIAL,LATERAL') {
    throw 'U08 wrapper only permits the bounded MEDIAL/LATERAL action set.'
}
if ($manifestData.configurations.Count -ne 2) {
    throw 'U08 wrapper only permits two configurations.'
}
foreach ($configuration in $manifestData.configurations) {
    if (($configuration.seeds -join ',') -ne '13,47') {
        throw 'U08 wrapper only permits seeds 13 and 47.'
    }
}

$datasetPath = Join-Path $dataRootPath 'Artifacts\ThoracicUSCoach\active2d_dataset'
if (-not (Test-Path -LiteralPath $datasetPath -PathType Container)) {
    throw "U08 Active2D dataset not found under explicit data root: $datasetPath"
}
$stopPath = Join-Path $dataRootPath $manifestData.frozenStopBaseline.path
if (-not (Test-Path -LiteralPath $stopPath -PathType Leaf)) {
    throw "Frozen U08 STOP baseline not found: $stopPath"
}
$stopHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $stopPath).Hash.ToLowerInvariant()
if ($stopHash -ne $manifestData.frozenStopBaseline.sha256.ToLowerInvariant()) {
    throw 'Frozen U08 STOP baseline hash mismatch.'
}

$outputRoot = Join-Path $projectRoot $manifestData.outputDirectory
$logRoot = Join-Path $outputRoot 'logs'
New-Item -ItemType Directory -Force -Path $outputRoot, $logRoot | Out-Null
$knownOutputs = @(
    'spatial_v2_random_relu32_ridge_seed13.json',
    'spatial_v2_random_relu32_ridge_seed47.json',
    'history_v1_random_relu32_ridge_seed13.json',
    'history_v1_random_relu32_ridge_seed47.json',
    'u08_selected_action_response.json',
    'u08_development_report.json',
    'u08_locked_test_report.json'
)
foreach ($name in $knownOutputs) {
    Remove-Item -LiteralPath (Join-Path $outputRoot $name) -Force -ErrorAction SilentlyContinue
}
$testLog = Join-Path $logRoot 'u08_tests.log'
$trainLog = Join-Path $logRoot 'u08_train.log'
$evaluationLog = Join-Path $logRoot 'u08_evaluate.log'
Remove-Item -LiteralPath $testLog, $trainLog, $evaluationLog -Force -ErrorAction SilentlyContinue

$uv = (Get-Command uv -ErrorAction Stop).Source
$toolRoot = Join-Path $projectRoot 'Tools\ThoracicUSCoach'
& $uv run --offline --frozen --project $toolRoot `
    python (Join-Path $toolRoot 'test_phase2_action_selector.py') *>&1 |
    Tee-Object -FilePath $testLog
if ($LASTEXITCODE -ne 0) {
    throw "U08 tests failed. See $testLog"
}

& $uv run --offline --frozen --project $toolRoot `
    python (Join-Path $toolRoot 'train_phase2_action_selector.py') `
    --manifest $manifestPath --data-root $dataRootPath --output-dir $outputRoot *>&1 |
    Tee-Object -FilePath $trainLog
if ($LASTEXITCODE -ne 0) {
    throw "U08 development training failed. See $trainLog"
}

$selectedPath = Join-Path $outputRoot 'u08_selected_action_response.json'
$developmentPath = Join-Path $outputRoot 'u08_development_report.json'
$lockedPath = Join-Path $outputRoot 'u08_locked_test_report.json'
& $uv run --offline --frozen --project $toolRoot `
    python (Join-Path $toolRoot 'evaluate_phase2_action_selector.py') `
    --manifest $manifestPath --data-root $dataRootPath --checkpoint $selectedPath `
    --development-report $developmentPath --output $lockedPath *>&1 |
    Tee-Object -FilePath $evaluationLog
if ($LASTEXITCODE -ne 0) {
    throw "U08 locked evaluation failed. See $evaluationLog"
}

$final = Get-Content -Raw -LiteralPath $lockedPath | ConvertFrom-Json
if ($final.status -notin @('TRAINED_DEV_PASS', 'NO_IMPROVEMENT', 'BLOCKED', 'FAILED')) {
    throw "U08 produced an unsupported status: $($final.status)"
}
[pscustomobject]@{
    taskId = 'U08'
    status = $final.status
    executionCommit = $executionCommit
    manifest = $manifestPath
    dataRoot = $dataRootPath
    frozenStopSha256 = $stopHash
    developmentReport = $developmentPath
    lockedTestReport = $lockedPath
    closedLoopAcquisitionEvaluated = $false
} | ConvertTo-Json -Depth 4
