param(
    [string]$Output = 'Artifacts/ThoracicUSCoach/active2d/r5_a_scout_manifest_20261026.json',
    [string]$CaptureOutput = 'Artifacts/ThoracicUSCoach/active2d/r5_a_scout_raw_20261026'
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$protocolPath = 'Tools/ThoracicUSCoach/experiments/g2_r5_protocol_v1.json'
$schemaPath = 'Tools/ThoracicUSCoach/experiments/g2_r5_evidence_schema_v1.json'
$auditPath = 'Tools/ThoracicUSCoach/experiments/g2_r5_audit_protocol_v1.json'
$expectedProtocol = '45ff77373418ec565faa813f75dd1e9402a0a46ebf1698d3fab6fff154e5cf6d'
$expectedSchema = 'eb66798b4348c7d335090f6d8c210aa1b4cd9409c9f8aa64e800bdd05643e0b0'
$expectedAudit = '98b4b5f0d8ebf77520d4d0483e7858c17bd6f6b3f724f10c03f39dc6534eefe8'

function Get-RawSha256([string]$RelativePath) {
    (Get-FileHash -LiteralPath (Join-Path $projectRoot $RelativePath) -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-CanonicalSha256([string]$RelativePath) {
    $text = [IO.File]::ReadAllText((Join-Path $projectRoot $RelativePath)).Replace("`r`n", "`n").Replace("`r", "`n")
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($text)
    $hash = [Security.Cryptography.SHA256]::HashData($bytes)
    [Convert]::ToHexString($hash).ToLowerInvariant()
}

foreach ($item in @(
    @($protocolPath, $expectedProtocol),
    @($schemaPath, $expectedSchema),
    @($auditPath, $expectedAudit)
)) {
    $actual = Get-CanonicalSha256 $item[0]
    if ($actual -ne $item[1]) { throw "Frozen canonical hash mismatch for $($item[0]): $actual" }
}

$commit = (& git -C $projectRoot rev-parse HEAD).Trim()
$branch = (& git -C $projectRoot branch --show-current).Trim()
if ($LASTEXITCODE -ne 0 -or $branch -ne 'codex/r5-unity') { throw "Unexpected R5 capture branch: $branch" }

$routeActions = @(
    @('Lateral','Lateral','Lateral','Lateral','Medial','Medial','Medial','Medial','Caudal','RotateClockwise','RotateCounterClockwise'),
    @('Medial','Medial','Medial','Medial','Lateral','Lateral','Lateral','Lateral','Cranial','RotateCounterClockwise','RotateClockwise'),
    @('Caudal','Caudal','Caudal','Cranial','Cranial','Cranial','RotateClockwise','Lateral','Medial','RotateCounterClockwise','Lateral'),
    @('Cranial','Cranial','Cranial','Caudal','Caudal','Caudal','RotateCounterClockwise','Medial','Lateral','RotateClockwise','Medial'),
    @('Lateral','Lateral','Caudal','Caudal','Medial','Medial','Cranial','Cranial','RotateClockwise','RotateClockwise','RotateCounterClockwise'),
    @('Medial','Medial','Cranial','Cranial','Lateral','Lateral','Caudal','Caudal','RotateCounterClockwise','RotateCounterClockwise','RotateClockwise'),
    @('Caudal','Lateral','Lateral','Cranial','Medial','Medial','RotateClockwise','Caudal','Lateral','Cranial','RotateCounterClockwise'),
    @('Cranial','Medial','Medial','Caudal','Lateral','Lateral','RotateCounterClockwise','Cranial','Medial','Caudal','RotateClockwise')
)
$strata = @('far_cranial','near_cranial','center','near_caudal','far_caudal','near_cranial','center','near_caudal')
$routes = for ($index = 0; $index -lt 8; $index++) {
    [ordered]@{
        routeId = "r5a_scout_$($index.ToString('D2'))"
        familyId = "r5a_marker_skin_family_$($index.ToString('D2'))"
        arm = 'scout'
        coverageIntent = 'target-independent marker/skin T7 stratum scout'
        startStratum = $strata[$index]
        proposalSeed = 20261026 + (7919 * $index)
        maxStartAttempts = 6
        actions = $routeActions[$index]
        repeatRenderAtObservationIndices = @()
    }
}

$manifest = [ordered]@{
    schemaVersion = 1
    protocolId = 'G2_R5_FIXED_ANATOMY_DIVERSITY_V1'
    mode = 'R5_A_SCOUT'
    scene = 'Assets/ThoracicUSCoach/Scenes/ThoracicUSCoach_POC.unity'
    unityVersion = '6000.3.9f1'
    sourceCommit = $commit
    baseCommit = 'bdbb6e90ebe7e057b704391ede9961815a8bdbb5'
    captureBranch = $branch
    datasetRunId = 'g2_r5_a_scout_20261026'
    proposalSeed = 20261026
    outputDirectory = $CaptureOutput
    sharedContractPath = $schemaPath
    sharedContractSha256 = $expectedSchema
    sharedContractMaterializedSha256 = Get-RawSha256 $schemaPath
    scientificCriteriaPath = $protocolPath
    scientificCriteriaSha256 = $expectedProtocol
    scientificCriteriaMaterializedSha256 = Get-RawSha256 $protocolPath
    independentAuditPath = $auditPath
    independentAuditSha256 = $expectedAudit
    independentAuditMaterializedSha256 = Get-RawSha256 $auditPath
    acceptableScoreFloor = 0.5912307500839233
    maxStartProposals = 48
    maxRoutes = 8
    maxMovementsPerRoute = 12
    maxImages = 96
    maxObservationsPerRoute = 12
    routes = $routes
}
$outputPath = [IO.Path]::GetFullPath((Join-Path $projectRoot $Output))
if (-not $outputPath.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Manifest output escapes worktree'
}
if (Test-Path -LiteralPath $outputPath) { throw "Refusing to overwrite manifest: $outputPath" }
New-Item -ItemType Directory -Path (Split-Path -Parent $outputPath) -Force | Out-Null
[IO.File]::WriteAllText($outputPath, ($manifest | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
[pscustomobject]@{
    path = $outputPath
    sha256 = (Get-FileHash -LiteralPath $outputPath -Algorithm SHA256).Hash.ToLowerInvariant()
    sourceCommit = $commit
    routeCount = $routes.Count
    movementCount = ($routes | ForEach-Object { $_.actions.Count } | Measure-Object -Sum).Sum
    imageBudget = ($routes | ForEach-Object { 1 + $_.actions.Count } | Measure-Object -Sum).Sum
} | ConvertTo-Json
