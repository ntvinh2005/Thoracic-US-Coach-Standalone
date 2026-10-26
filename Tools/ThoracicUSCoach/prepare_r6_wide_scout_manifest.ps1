param(
    [string]$CoordinatorProtocolPath = 'Tools/ThoracicUSCoach/experiments/g2_r6_final_sprint_protocol_v1.json',
    [string]$CoordinatorProtocolSha256 = 'd201227ab95e5a858003fa2985d5c118ae724de0b1740972a82c68d1f4eaf7c9',
    [string]$AuditProtocolPath = 'Tools/ThoracicUSCoach/experiments/g2_r6_audit_protocol_v1.json',
    [string]$AuditProtocolSha256 = 'a01d30e294939eda46f6f2090a11761bd5b37f26d6b516d05b3d3c00c19e07a5',
    [Parameter(Mandatory = $true)] [string]$PrerequisiteEvidencePath,
    [Parameter(Mandatory = $true)] [string]$PrerequisiteEvidenceSha256,
    [string]$PrerequisiteRequiredVerdict = 'GO_R6_WIDE_SCOUT',
    [string]$Output = 'Artifacts/ThoracicUSCoach/active2d/r6_wide_scout_manifest_20261026.json',
    [string]$CaptureOutput = 'Artifacts/ThoracicUSCoach/active2d/r6_wide_scout_raw_20261026'
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if ($CoordinatorProtocolPath -ne 'Tools/ThoracicUSCoach/experiments/g2_r6_final_sprint_protocol_v1.json' -or
    $AuditProtocolPath -ne 'Tools/ThoracicUSCoach/experiments/g2_r6_audit_protocol_v1.json') {
    throw 'R6 frozen protocol paths changed'
}
function Get-RawSha256([string]$RelativePath) {
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { $hash = $algorithm.ComputeHash([IO.File]::ReadAllBytes((Join-Path $projectRoot $RelativePath))) }
    finally { $algorithm.Dispose() }
    ([BitConverter]::ToString($hash) -replace '-', '').ToLowerInvariant()
}
function Get-CanonicalSha256([string]$RelativePath) {
    $text = [IO.File]::ReadAllText((Join-Path $projectRoot $RelativePath)).Replace("`r`n", "`n").Replace("`r", "`n")
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($text)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { $hash = $algorithm.ComputeHash($bytes) } finally { $algorithm.Dispose() }
    ([BitConverter]::ToString($hash) -replace '-', '').ToLowerInvariant()
}
foreach ($contract in @(
    @($CoordinatorProtocolPath, $CoordinatorProtocolSha256), @($AuditProtocolPath, $AuditProtocolSha256)
)) {
    if ((Get-CanonicalSha256 $contract[0]) -ne $contract[1].ToLowerInvariant()) {
        throw "Frozen canonical hash mismatch for $($contract[0])"
    }
}
if ((Get-RawSha256 $PrerequisiteEvidencePath) -ne $PrerequisiteEvidenceSha256.ToLowerInvariant()) {
    throw 'R6 basin prerequisite evidence hash mismatch'
}
$decision = Get-Content -LiteralPath (Join-Path $projectRoot $PrerequisiteEvidencePath) -Raw | ConvertFrom-Json
if ($decision.verdict -ne $PrerequisiteRequiredVerdict) {
    throw "R6 wide scout remains blocked by prerequisite verdict: $($decision.verdict)"
}

$safeRoot = $projectRoot.Replace('\','/')
$commit = (& git -c "safe.directory=$safeRoot" -C $projectRoot rev-parse HEAD).Trim()
$branch = (& git -c "safe.directory=$safeRoot" -C $projectRoot branch --show-current).Trim()
if ($LASTEXITCODE -ne 0 -or $branch -ne 'codex/r6-unity-final') { throw "Unexpected R6 capture branch: $branch" }

$longitudinalStarts = @(-18.0,-12.0,-6.0,0.0,0.0,6.0,12.0,18.0)
$yawStarts = @(-6.0,0.0,6.0,-3.0,3.0,-6.0,0.0,6.0)
$routes = for ($routeIndex = 0; $routeIndex -lt 8; $routeIndex++) {
    $actions = [Collections.Generic.List[string]]::new()
    1..12 | ForEach-Object { $actions.Add('Lateral') }
    1..24 | ForEach-Object { $actions.Add('Medial') }
    1..12 | ForEach-Object { $actions.Add('Lateral') }
    $forward = if ($routeIndex % 2 -eq 0) { 'Caudal' } else { 'Cranial' }
    $reverse = if ($forward -eq 'Caudal') { 'Cranial' } else { 'Caudal' }
    1..3 | ForEach-Object { $actions.Add($forward) }
    1..3 | ForEach-Object { $actions.Add($reverse) }
    $actions.Add('RotateClockwise'); $actions.Add('RotateCounterClockwise')
    $actions.Add('RotateCounterClockwise'); $actions.Add('RotateClockwise')
    $actions.Add($forward); $actions.Add($reverse)
    if ($actions.Count -ne 60) { throw 'R6 route action budget construction failed' }
    [ordered]@{
        routeId="r6_wide_scout_$($routeIndex.ToString('D2'))"
        familyId="r6_wide_scout_family_$($routeIndex.ToString('D2'))"
        arm='wide_scout'; proposalSeed=20261026 + (7919 * $routeIndex)
        startLongitudinalOffsetMm=$longitudinalStarts[$routeIndex]
        startLateralOffsetMm=0.0; startYawDeg=$yawStarts[$routeIndex]
        actions=$actions; repeatRenderAtObservationIndices=@(0,30,60)
    }
}

$manifest = [ordered]@{
    schemaVersion=1; protocolId='G2_R6_FINAL_SPRINT_V1'; mode='R6_TARGET_INDEPENDENT_WIDE_SCOUT'
    scene='Assets/ThoracicUSCoach/Scenes/ThoracicUSCoach_POC.unity'; unityVersion='6000.3.9f1'
    sourceCommit=$commit; baseCommit='8a61f33947a29296658cf6eff44742fac4811301'
    captureBranch=$branch; datasetRunId='r6_wide_scout_20261026'; outputDirectory=$CaptureOutput
    acceptableScoreFloor=0.5912307500839233
    coordinatorProtocolPath='Tools/ThoracicUSCoach/experiments/g2_r6_final_sprint_protocol_v1.json'; coordinatorProtocolSha256=$CoordinatorProtocolSha256.ToLowerInvariant()
    auditProtocolPath='Tools/ThoracicUSCoach/experiments/g2_r6_audit_protocol_v1.json'; auditProtocolSha256=$AuditProtocolSha256.ToLowerInvariant()
    prerequisiteEvidencePath=$PrerequisiteEvidencePath; prerequisiteEvidenceSha256=$PrerequisiteEvidenceSha256.ToLowerInvariant()
    prerequisiteRequiredVerdict=$PrerequisiteRequiredVerdict
    maxCandidatePoses=320; maxScoutRoutes=8; maxMovementsPerScoutRoute=60; maxScoutObservations=512
    maxDevelopmentTrajectories=16; maxObservationsPerDevelopmentTrajectory=121
    positiveControls=@(); mapCandidates=@(); routes=$routes
}
$outputPath = [IO.Path]::GetFullPath((Join-Path $projectRoot $Output))
if (-not $outputPath.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Manifest output escapes worktree'
}
if (Test-Path -LiteralPath $outputPath) { throw "Refusing to overwrite manifest: $outputPath" }
New-Item -ItemType Directory -Path (Split-Path -Parent $outputPath) -Force | Out-Null
[IO.File]::WriteAllText($outputPath, ($manifest | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
[pscustomobject]@{ path=$outputPath; sourceCommit=$commit; routes=$routes.Count; movements=480; observations=512 } | ConvertTo-Json
