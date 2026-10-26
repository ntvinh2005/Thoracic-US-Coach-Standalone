param(
    [string]$CoordinatorProtocolPath = 'Tools/ThoracicUSCoach/experiments/g2_r6_final_sprint_protocol_v1.json',
    [string]$CoordinatorProtocolSha256 = 'd201227ab95e5a858003fa2985d5c118ae724de0b1740972a82c68d1f4eaf7c9',
    [string]$AuditProtocolPath = 'Tools/ThoracicUSCoach/experiments/g2_r6_audit_protocol_v1.json',
    [string]$AuditProtocolSha256 = 'a01d30e294939eda46f6f2090a11761bd5b37f26d6b516d05b3d3c00c19e07a5',
    [string]$Output = 'Artifacts/ThoracicUSCoach/active2d/r6_basin_manifest_20261026.json',
    [string]$CaptureOutput = 'Artifacts/ThoracicUSCoach/active2d/r6_positive_basin_raw_20261026'
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if ($CoordinatorProtocolPath -ne 'Tools/ThoracicUSCoach/experiments/g2_r6_final_sprint_protocol_v1.json' -or
    $AuditProtocolPath -ne 'Tools/ThoracicUSCoach/experiments/g2_r6_audit_protocol_v1.json') {
    throw 'R6 frozen protocol paths changed'
}

function Get-CanonicalSha256([string]$RelativePath) {
    $absolute = Join-Path $projectRoot $RelativePath
    $text = [IO.File]::ReadAllText($absolute).Replace("`r`n", "`n").Replace("`r", "`n")
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($text)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { $hash = $algorithm.ComputeHash($bytes) } finally { $algorithm.Dispose() }
    ([BitConverter]::ToString($hash) -replace '-', '').ToLowerInvariant()
}

foreach ($contract in @(
    @($CoordinatorProtocolPath, $CoordinatorProtocolSha256),
    @($AuditProtocolPath, $AuditProtocolSha256)
)) {
    $actual = Get-CanonicalSha256 $contract[0]
    if ($actual -ne $contract[1].ToLowerInvariant()) {
        throw "Frozen canonical hash mismatch for $($contract[0]): $actual"
    }
}

$safeRoot = $projectRoot.Replace('\','/')
$commit = (& git -c "safe.directory=$safeRoot" -C $projectRoot rev-parse HEAD).Trim()
$branch = (& git -c "safe.directory=$safeRoot" -C $projectRoot branch --show-current).Trim()
if ($LASTEXITCODE -ne 0 -or $branch -ne 'codex/r6-unity-final') {
    throw "Unexpected R6 capture branch: $branch"
}

# All four controls are consumed R4 frames with evaluator-positive AND strict T7-positive labels.
$controls = @(
    [ordered]@{
        controlId='r6_control_hard_00_f0008'; sourceCaseId='r4p1_hard_00'; sourceFrameIndex=8; sourceArm='hard_causal'
        sourceImageSha256='46fe40c4c9af5848f6cad450840f7bae96d13c4d259307847a2cdcfc922a9c90'
        sourcePrivilegedAnnotationsSha256='80cdfaced49decd1c534dd022e318ac52f34397672b526ab1cd6a956aca13229'
        sourceScore=0.6059278249740601; sourceMargin=0.014697074890136719
        probePosition=[ordered]@{x=33.18253707885742;y=180.3268585205078;z=-40.62839126586914}
        probeRotation=[ordered]@{x=-0.7594529390335083;y=0.04515675827860832;z=0.045794013887643814;w=0.6473755240440369}
        repeatRenders=3
    },
    [ordered]@{
        controlId='r6_control_random_01_f0010'; sourceCaseId='r4p1_random_01'; sourceFrameIndex=10; sourceArm='randomized_start'
        sourceImageSha256='eeb9360661573aa64d1f3cbb000ff8ecc3dd9ccb4d3ba05277b58aa26db688a8'
        sourcePrivilegedAnnotationsSha256='86d5d6fba38ff69a24e365a0cb9bd7a0d4c13bfd15ce3b174bbde9b1f4c409d5'
        sourceScore=0.6012852191925049; sourceMargin=0.010054469108581543
        probePosition=[ordered]@{x=34.15890121459961;y=180.93338012695312;z=-36.30380630493164}
        probeRotation=[ordered]@{x=-0.7387209534645081;y=0.06366487592458725;z=0.06432347744703293;w=0.66790771484375}
        repeatRenders=3
    },
    [ordered]@{
        controlId='r6_control_random_05_f0016'; sourceCaseId='r4p1_random_05'; sourceFrameIndex=16; sourceArm='randomized_start'
        sourceImageSha256='ddad5db3ec4ee58a758179eb8044ba80852871f9ba095e668b1abaea802bc6b7'
        sourcePrivilegedAnnotationsSha256='8bf3ecf93000e35136f4e9b423725b1a7dc954f6de224950817f186b73b0d7b5'
        sourceScore=0.6012580990791321; sourceMargin=0.01002734899520874
        probePosition=[ordered]@{x=34.59640121459961;y=180.3469696044922;z=-40.60182189941406}
        probeRotation=[ordered]@{x=-0.7594582438468933;y=0.045066915452480316;z=0.04571745917201042;w=0.6473809480667114}
        repeatRenders=3
    },
    [ordered]@{
        controlId='r6_control_hard_01_f0009'; sourceCaseId='r4p1_hard_01'; sourceFrameIndex=9; sourceArm='hard_causal'
        sourceImageSha256='126b14863d3b78a6a829e64b5185d081c09facec0824547ae9fbff1d26a7df0a'
        sourcePrivilegedAnnotationsSha256='57e57181aee4110805a53531ea0da76a14e98e78182a6c377043c6eaa9cc8ffb'
        sourceScore=0.5981886982917786; sourceMargin=0.006957948207855225
        probePosition=[ordered]@{x=32.958805084228516;y=180.3448486328125;z=-40.49995040893555}
        probeRotation=[ordered]@{x=-0.7594529390335083;y=0.04515675827860832;z=0.045794013887643814;w=0.6473755240440369}
        repeatRenders=3
    }
)

$candidates = [Collections.Generic.List[object]]::new()
foreach ($control in $controls) {
    foreach ($longitudinal in @(-6.0,-2.0,2.0,6.0)) {
        foreach ($lateral in @(-8.0,-4.0,0.0,4.0,8.0)) {
            foreach ($yaw in @(-6.0,0.0,6.0)) {
                $candidates.Add([ordered]@{
                    candidateId = "$($control.controlId)_coarse_lg$longitudinal`_lt$lateral`_y$yaw"
                    anchorControlId = $control.controlId; stage='coarse'; parentCandidateId=''
                    longitudinalOffsetMm=$longitudinal; lateralOffsetMm=$lateral; yawDeg=$yaw; repeatRenders=1
                })
            }
        }
    }
    foreach ($longitudinal in @(-1.0,1.0)) {
        foreach ($lateral in @(-2.0,0.0,2.0)) {
            foreach ($yaw in @(-2.0,0.0,2.0)) {
                $candidates.Add([ordered]@{
                    candidateId = "$($control.controlId)_fine_lg$longitudinal`_lt$lateral`_y$yaw"
                    anchorControlId = $control.controlId; stage='fine'; parentCandidateId=$control.controlId
                    longitudinalOffsetMm=$longitudinal; lateralOffsetMm=$lateral; yawDeg=$yaw; repeatRenders=1
                })
            }
        }
    }
}
if ($candidates.Count -ne 312) { throw "R6 basin grid changed unexpectedly: $($candidates.Count)" }

$manifest = [ordered]@{
    schemaVersion=1; protocolId='G2_R6_FINAL_SPRINT_V1'; mode='R6_POSITIVE_CONTROL_BASIN'
    scene='Assets/ThoracicUSCoach/Scenes/ThoracicUSCoach_POC.unity'; unityVersion='6000.3.9f1'
    sourceCommit=$commit; baseCommit='8a61f33947a29296658cf6eff44742fac4811301'
    captureBranch=$branch; datasetRunId='r6_positive_basin_20261026'; outputDirectory=$CaptureOutput
    acceptableScoreFloor=0.5912307500839233
    coordinatorProtocolPath='Tools/ThoracicUSCoach/experiments/g2_r6_final_sprint_protocol_v1.json'; coordinatorProtocolSha256=$CoordinatorProtocolSha256.ToLowerInvariant()
    auditProtocolPath='Tools/ThoracicUSCoach/experiments/g2_r6_audit_protocol_v1.json'; auditProtocolSha256=$AuditProtocolSha256.ToLowerInvariant()
    prerequisiteEvidencePath=''; prerequisiteEvidenceSha256=''; prerequisiteRequiredVerdict=''
    maxCandidatePoses=320; maxScoutRoutes=8; maxMovementsPerScoutRoute=60; maxScoutObservations=512
    maxDevelopmentTrajectories=16; maxObservationsPerDevelopmentTrajectory=121
    positiveControls=$controls; mapCandidates=$candidates; routes=@()
}
$outputPath = [IO.Path]::GetFullPath((Join-Path $projectRoot $Output))
if (-not $outputPath.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Manifest output escapes worktree'
}
if (Test-Path -LiteralPath $outputPath) { throw "Refusing to overwrite manifest: $outputPath" }
New-Item -ItemType Directory -Path (Split-Path -Parent $outputPath) -Force | Out-Null
[IO.File]::WriteAllText($outputPath, ($manifest | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
[pscustomobject]@{ path=$outputPath; sourceCommit=$commit; controls=$controls.Count; candidates=$candidates.Count } | ConvertTo-Json
