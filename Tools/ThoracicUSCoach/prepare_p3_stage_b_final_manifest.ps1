param(
    [string]$Output = 'Artifacts/ThoracicUSCoach/active2d/g2_p3_stage_b_final_manifest_v1.json',
    [string]$CaptureOutput = 'Artifacts/ThoracicUSCoach/active2d/p3_stage_b_final_20261026'
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$protocolPath = 'Tools/ThoracicUSCoach/experiments/g2_p3_stage_b_final_protocol_v1.json'
$protocolSha = '14967ccaef673152b3784fc33ccf184c527769403418db765c82ee54d6aa6278'
$upstreamAuditPath = 'Artifacts/ThoracicUSCoach/active2d/evaluation/p3_final_sprint/p3_stage_f_final_v2_independent_audit.json'
$upstreamAuditSha = 'af574b9b515362e09f6044c4a21e01df216f8fe0cb7e8133514cd61212414d65'
$upstreamSealSha = '93a15b1abb6b29ff4e4e3ceccc6b492ca0079b33a9bc8362a10e24a57c708f0c'
$selectionRule = 'For each anchor, select the passing coarse/wider candidate with greatest minimum repeat score; ties resolve by candidateId ascending.'

function Get-CanonicalSha256([string]$RelativePath) {
    $text = [IO.File]::ReadAllText((Join-Path $projectRoot $RelativePath)).Replace("`r`n", "`n").Replace("`r", "`n")
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { ([BitConverter]::ToString($algorithm.ComputeHash([Text.UTF8Encoding]::new($false).GetBytes($text))) -replace '-', '').ToLowerInvariant() }
    finally { $algorithm.Dispose() }
}
function Get-RawSha256([string]$RelativePath) {
    (Get-FileHash -LiteralPath (Join-Path $projectRoot $RelativePath) -Algorithm SHA256).Hash.ToLowerInvariant()
}
if ((Get-CanonicalSha256 $protocolPath) -ne $protocolSha) { throw 'Stage B frozen protocol hash mismatch' }
if ((Get-RawSha256 $upstreamAuditPath) -ne $upstreamAuditSha) { throw 'Stage B upstream audit hash mismatch' }
$upstream = Get-Content -Raw -LiteralPath (Join-Path $projectRoot $upstreamAuditPath) | ConvertFrom-Json
if ($upstream.verdict -ne 'STAGE_F_FINAL_PASS' -or -not $upstream.authorizedNextStage -or $upstream.integrityStatus -ne 'PASS') {
    throw 'Stage B upstream independent audit is not PASS'
}

$safeRoot = $projectRoot.Replace('\','/')
$sourceCommit = (& git -c "safe.directory=$safeRoot" -C $projectRoot rev-parse HEAD).Trim()
$branch = (& git -c "safe.directory=$safeRoot" -C $projectRoot branch --show-current).Trim()
if ($LASTEXITCODE -ne 0 -or $branch -ne 'codex/p2-unity-final') { throw "Unexpected Stage B branch: $branch" }

$controls = @(
    [ordered]@{controlId='r6_control_hard_00_f0008';sourceCaseId='r4p1_hard_00';sourceFrameIndex=8;sourceArm='hard_causal';sourceImageSha256='46fe40c4c9af5848f6cad450840f7bae96d13c4d259307847a2cdcfc922a9c90';sourcePrivilegedAnnotationsSha256='80cdfaced49decd1c534dd022e318ac52f34397672b526ab1cd6a956aca13229';sourceScore=0.6059278249740601;sourceMargin=0.014697074890136719;probePosition=[ordered]@{x=33.18253707885742;y=180.3268585205078;z=-40.62839126586914};probeRotation=[ordered]@{x=-0.7594529390335083;y=0.04515675827860832;z=0.045794013887643814;w=0.6473755240440369};repeatRenders=2},
    [ordered]@{controlId='r6_control_random_01_f0010';sourceCaseId='r4p1_random_01';sourceFrameIndex=10;sourceArm='randomized_start';sourceImageSha256='eeb9360661573aa64d1f3cbb000ff8ecc3dd9ccb4d3ba05277b58aa26db688a8';sourcePrivilegedAnnotationsSha256='86d5d6fba38ff69a24e365a0cb9bd7a0d4c13bfd15ce3b174bbde9b1f4c409d5';sourceScore=0.6012852191925049;sourceMargin=0.010054469108581543;probePosition=[ordered]@{x=34.15890121459961;y=180.93338012695312;z=-36.30380630493164};probeRotation=[ordered]@{x=-0.7387209534645081;y=0.06366487592458725;z=0.06432347744703293;w=0.66790771484375};repeatRenders=2},
    [ordered]@{controlId='r6_control_random_05_f0016';sourceCaseId='r4p1_random_05';sourceFrameIndex=16;sourceArm='randomized_start';sourceImageSha256='ddad5db3ec4ee58a758179eb8044ba80852871f9ba095e668b1abaea802bc6b7';sourcePrivilegedAnnotationsSha256='8bf3ecf93000e35136f4e9b423725b1a7dc954f6de224950817f186b73b0d7b5';sourceScore=0.6012580990791321;sourceMargin=0.01002734899520874;probePosition=[ordered]@{x=34.59640121459961;y=180.3469696044922;z=-40.60182189941406};probeRotation=[ordered]@{x=-0.7594582438468933;y=0.045066915452480316;z=0.04571745917201042;w=0.6473809480667114};repeatRenders=2},
    [ordered]@{controlId='r6_control_hard_01_f0009';sourceCaseId='r4p1_hard_01';sourceFrameIndex=9;sourceArm='hard_causal';sourceImageSha256='126b14863d3b78a6a829e64b5185d081c09facec0824547ae9fbff1d26a7df0a';sourcePrivilegedAnnotationsSha256='57e57181aee4110805a53531ea0da76a14e98e78182a6c377043c6eaa9cc8ffb';sourceScore=0.5981886982917786;sourceMargin=0.006957948207855225;probePosition=[ordered]@{x=32.958805084228516;y=180.3448486328125;z=-40.49995040893555};probeRotation=[ordered]@{x=-0.7594529390335083;y=0.04515675827860832;z=0.045794013887643814;w=0.6473755240440369};repeatRenders=2}
)

function Token([double]$v) { if ($v -lt 0) { 'n' } elseif ($v -gt 0) { 'p' } else { 'z' } }
function CandidateId([string]$control,[string]$stage,[double]$lg,[double]$lt,[double]$yaw) {
    "$control`_$stage`_lg$(Token $lg)_lt$(Token $lt)_y$(Token $yaw)"
}
function Key([double]$lg,[double]$lt,[double]$yaw) { "$lg|$lt|$yaw" }

$mapCandidates = [Collections.Generic.List[object]]::new()
$neighborPairs = [Collections.Generic.List[object]]::new()
function Add-BaseGrid([object]$control,[string]$stage,[double[]]$lgValues,[double[]]$ltValues,[double[]]$yawValues) {
    $anchorId = CandidateId $control.controlId 'anchor' 0 0 0
    $map = @{ (Key 0 0 0) = $anchorId }
    foreach ($lg in $lgValues) { foreach ($lt in $ltValues) { foreach ($yaw in $yawValues) {
        if ($lg -eq 0 -and $lt -eq 0 -and $yaw -eq 0) { continue }
        $id = CandidateId $control.controlId $stage $lg $lt $yaw
        $map[(Key $lg $lt $yaw)] = $id
        $mapCandidates.Add([ordered]@{candidateId=$id;anchorControlId=$control.controlId;stage=$stage;centerCandidateId=$anchorId;longitudinalOffsetMm=$lg;lateralOffsetMm=$lt;yawDeg=$yaw;role='base_cell';countsAsDistinctCell=$true;consumedHistoricalControl=$false})
    } } }
    for ($i=0;$i -lt 3;$i++) { for ($j=0;$j -lt 3;$j++) { for ($k=0;$k -lt 3;$k++) {
        $here = $map[(Key $lgValues[$i] $ltValues[$j] $yawValues[$k])]
        foreach ($next in @(@(($i+1),$j,$k),@($i,($j+1),$k),@($i,$j,($k+1)))) {
            if ($next[0] -lt 3 -and $next[1] -lt 3 -and $next[2] -lt 3) {
                $there = $map[(Key $lgValues[$next[0]] $ltValues[$next[1]] $yawValues[$next[2]])]
                $neighborPairs.Add([object[]]@($here,$there))
            }
        }
    } } }
}

foreach ($control in $controls) {
    $anchorId = CandidateId $control.controlId 'anchor' 0 0 0
    $mapCandidates.Add([ordered]@{candidateId=$anchorId;anchorControlId=$control.controlId;stage='anchor';centerCandidateId='ANCHOR';longitudinalOffsetMm=0.0;lateralOffsetMm=0.0;yawDeg=0.0;role='consumed_control';countsAsDistinctCell=$true;consumedHistoricalControl=$true})
    Add-BaseGrid $control 'coarse' @(-2.0,0.0,2.0) @(-4.0,0.0,4.0) @(-3.0,0.0,3.0)
    Add-BaseGrid $control 'wider' @(-6.0,0.0,6.0) @(-8.0,0.0,8.0) @(-6.0,0.0,6.0)
}
$template = [Collections.Generic.List[object]]::new()
foreach ($lg in @(-1.0,0.0,1.0)) { foreach ($lt in @(-2.0,0.0,2.0)) { foreach ($yaw in @(-2.0,0.0,2.0)) {
    $template.Add([ordered]@{longitudinalOffsetMm=$lg;lateralOffsetMm=$lt;yawDeg=$yaw})
} } }
if ($mapCandidates.Count -ne 212 -or $neighborPairs.Count -ne 432 -or $template.Count -ne 27) { throw 'Stage B frozen grid count changed' }

$manifest = [ordered]@{
    schemaVersion=1;protocolId='G2_PHASE2_FINAL_SPRINT_P3_V1';mode='P3_STAGE_B_FINAL'
    scene='Assets/ThoracicUSCoach/Scenes/ThoracicUSCoach_POC.unity';unityVersion='6000.3.9f1'
    sourceCommit=$sourceCommit;reviewedDevBaseCommit='d362c50769068f2c57014aa26a0c9ace8abd2921';captureBranch='codex/p2-unity-final'
    datasetRunId='p3_stage_b_final_20261026';outputDirectory=$CaptureOutput;acceptableScoreFloor=0.5912307500839233
    auditProtocolPath=$protocolPath;auditProtocolCanonicalSha256=$protocolSha
    upstreamStageFIndependentAuditPath=$upstreamAuditPath;upstreamStageFIndependentAuditSha256=$upstreamAuditSha
    upstreamRequiredVerdict='STAGE_F_FINAL_PASS';upstreamSnapshotSealSha256=$upstreamSealSha
    maximumCandidatePoses=320;outcomeAdaptiveCandidateAddition=$false;historicalControls=$controls
    mapCandidates=$mapCandidates;neighborPairs=$neighborPairs
    refinement=[ordered]@{declaredBeforeCoarseCapture=$true;selectionRule=$selectionRule;offsetTemplate=$template;coarseSnapshotFile='stage_b_coarse_snapshot.json';selectionManifestFile='refinement_selection_manifest.json';maximumSlotsPerAnchor=27;postOutcomeExtensionAllowed=$false}
}
$outputPath = [IO.Path]::GetFullPath((Join-Path $projectRoot $Output))
if (-not $outputPath.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'manifest output escapes worktree' }
if (Test-Path -LiteralPath $outputPath) { throw "Refusing to overwrite Stage B manifest: $outputPath" }
New-Item -ItemType Directory -Path (Split-Path -Parent $outputPath) -Force | Out-Null
[IO.File]::WriteAllText($outputPath,($manifest | ConvertTo-Json -Depth 20),[Text.UTF8Encoding]::new($false))
[pscustomobject]@{path=$outputPath;sourceCommit=$sourceCommit;baseCandidates=$mapCandidates.Count;conditionalRefinementSlots=108;neighborPairs=$neighborPairs.Count} | ConvertTo-Json
