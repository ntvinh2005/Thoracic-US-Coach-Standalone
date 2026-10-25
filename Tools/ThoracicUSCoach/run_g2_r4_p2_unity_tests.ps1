param(
    [string]$UnityPath = "C:\Program Files\Unity\Hub\Editor\6000.3.9f1\Editor\Unity.exe"
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$output = Join-Path $projectRoot 'Artifacts\ThoracicUSCoach\active2d\evaluation\g2_r4_p2_20261025'
$xml = Join-Path $output 'p2_editmode.xml'
$log = Join-Path $output 'p2_editmode.log'
$summary = Join-Path $output 'p2_unity_test_summary.json'

if (-not (Test-Path -LiteralPath $UnityPath)) { throw "Unity executable missing: $UnityPath" }
$resolvedUnity = (Resolve-Path -LiteralPath $UnityPath).Path
$activeEditor = Get-Process -Name Unity -ErrorAction SilentlyContinue | Where-Object {
    try { (Resolve-Path -LiteralPath $_.Path).Path -eq $resolvedUnity } catch { $false }
}
if ($activeEditor) { throw 'Exclusive Unity preflight failed: requested Unity Editor is already running' }

$filter = 'CSSALT.ThoracicUSCoach.Tests.ThoracicUSCoachTests;CSSALT.ThoracicUSCoach.Tests.Active2DSafetyTests;CSSALT.ThoracicUSCoach.Tests.U08LiveIntegrationTests'
$process = Start-Process -FilePath $resolvedUnity -ArgumentList @(
    '-batchmode', '-projectPath', $projectRoot,
    '-runTests', '-testPlatform', 'EditMode', '-testFilter', $filter,
    '-testResults', $xml, '-logFile', $log
) -WindowStyle Hidden -Wait -PassThru

if (-not (Test-Path -LiteralPath $xml)) {
    throw "Unity produced no test XML (exit $($process.ExitCode)); see $log"
}
[xml]$document = Get-Content -LiteralPath $xml -Raw
$run = $document.'test-run'
$payload = [ordered]@{
    schemaVersion = 1
    taskId = 'G2-R4-P2'
    command = "Unity EditMode: $filter"
    unityExitCode = $process.ExitCode
    total = [int]$run.total
    passed = [int]$run.passed
    failed = [int]$run.failed
    skipped = [int]$run.skipped
    inconclusive = [int]$run.inconclusive
    result = if ([int]$run.failed -eq 0 -and [int]$run.passed -ge 34) { 'PASS' } else { 'FAIL' }
    xml = 'Artifacts/ThoracicUSCoach/active2d/evaluation/g2_r4_p2_20261025/p2_editmode.xml'
    log = 'Artifacts/ThoracicUSCoach/active2d/evaluation/g2_r4_p2_20261025/p2_editmode.log'
}
$payload | ConvertTo-Json | Set-Content -LiteralPath $summary -Encoding utf8
if ($payload.result -ne 'PASS') {
    throw "Unity EditMode tests failed: passed=$($payload.passed) failed=$($payload.failed)"
}
$payload | ConvertTo-Json
