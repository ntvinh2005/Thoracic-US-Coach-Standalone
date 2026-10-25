param(
    [string]$UnityPath = "C:\Program Files\Unity\Hub\Editor\6000.3.9f1\Editor\Unity.exe",
    [string]$Manifest = "Tools/ThoracicUSCoach/experiments/g2_r4_p1_capture_v1.json"
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$resultRoot = Join-Path $projectRoot 'Artifacts\ThoracicUSCoach\active2d\evaluation\g2_r4_revised_20261025'
$captureRoot = Join-Path $projectRoot 'Artifacts\ThoracicUSCoach\active2d\g2_r4_p1_raw_20261025'
New-Item -ItemType Directory -Path $resultRoot -Force | Out-Null
if (-not (Test-Path -LiteralPath $UnityPath)) { throw "Unity executable missing: $UnityPath" }
$resolvedUnity = (Resolve-Path -LiteralPath $UnityPath).Path
$activeEditor = Get-Process -Name Unity -ErrorAction SilentlyContinue | Where-Object {
    try { (Resolve-Path -LiteralPath $_.Path).Path -eq $resolvedUnity } catch { $false }
}
if ($activeEditor) { throw 'Exclusive Unity preflight failed: the requested Unity Editor is already running' }
if (Test-Path -LiteralPath $captureRoot) {
    throw "Refusing to overwrite existing P1 raw evidence: $captureRoot"
}
$commit = (& git -C $projectRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Could not resolve execution commit' }
$env:THORACIC_G2_R4_P1_EXECUTION_COMMIT = $commit

$testXml = Join-Path $resultRoot 'p1_preflight_editmode.xml'
$testLog = Join-Path $resultRoot 'p1_preflight_editmode.log'
$testFilter = 'CSSALT.ThoracicUSCoach.Tests.ThoracicUSCoachTests;CSSALT.ThoracicUSCoach.Tests.Active2DSafetyTests;CSSALT.ThoracicUSCoach.Tests.U08LiveIntegrationTests'
$testProcess = Start-Process -FilePath $UnityPath -ArgumentList @(
    '-batchmode', '-projectPath', $projectRoot,
    '-runTests', '-testPlatform', 'EditMode', '-testFilter', $testFilter,
    '-testResults', $testXml, '-logFile', $testLog
) -WindowStyle Hidden -Wait -PassThru
if (-not (Test-Path -LiteralPath $testXml)) {
    throw "P1 preflight produced no XML (Unity exit $($testProcess.ExitCode)); preserve $testLog"
}
[xml]$testResult = Get-Content -LiteralPath $testXml -Raw
$run = $testResult.'test-run'
if ([int]$run.failed -ne 0 -or [int]$run.passed -lt 34) {
    throw "P1 safety preflight failed: passed=$($run.passed), failed=$($run.failed); preserve $testLog"
}

$captureLog = Join-Path $resultRoot 'p1_capture_unity.log'
$captureProcess = Start-Process -FilePath $UnityPath -ArgumentList @(
    '-batchmode', '-projectPath', $projectRoot,
    '-executeMethod', 'CSSALT.ThoracicUSCoach.Editor.ThoracicG2R4P1Batch.Run',
    '-g2R4P1Manifest', $Manifest, '-logFile', $captureLog
) -WindowStyle Hidden -Wait -PassThru
if ($captureProcess.ExitCode -ne 0) {
    throw "P1 capture failed with Unity exit $($captureProcess.ExitCode); preserve $captureLog"
}
$reportPath = Join-Path $captureRoot 'g2_r4_p1_unity_report.json'
if (-not (Test-Path -LiteralPath $reportPath)) { throw 'P1 Unity report missing' }
$report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
if (-not $report.complete -or $report.infrastructureFailure) {
    throw "P1 capture incomplete: $($report.infrastructureFailure)"
}
$report | Select-Object complete,requestedCases,completedCases,failedCases,newIndependentFamilies,newFamiliesWithPositive,totalObservations,hardCausalObservations,acceptedActions,rejectedActions,incompleteActions,safetyFailures,contactViolations,workspaceViolations | ConvertTo-Json
