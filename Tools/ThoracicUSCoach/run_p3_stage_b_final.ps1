param(
    [Parameter(Mandatory=$true)][ValidateSet('EngineeringPreflight','Outcome')][string]$Mode,
    [Parameter(Mandatory=$true)][string]$Manifest,
    [Parameter(Mandatory=$true)][string]$ExpectedManifestSha256,
    [string]$HandoffDestination='D:\CSSALT\Thoracic-US-Coach-P3-Handoff\stage_b_final\p3_stage_b_final_20261026',
    [string]$UnityPath='C:\Program Files\Unity\Hub\Editor\6000.3.9f1\Editor\Unity.exe',
    [string]$PythonPath="$env:USERPROFILE\anaconda3\python.exe"
)
$ErrorActionPreference='Stop'
$projectRoot=(Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
function Raw([string]$Path){
    $stream=[IO.File]::OpenRead($Path)
    $algorithm=[Security.Cryptography.SHA256]::Create()
    try{([BitConverter]::ToString($algorithm.ComputeHash($stream))-replace '-','').ToLowerInvariant()}
    finally{$stream.Dispose();$algorithm.Dispose()}
}
function Canon([string]$Path){
    $text=[IO.File]::ReadAllText($Path).Replace("`r`n","`n").Replace("`r","`n")
    $algorithm=[Security.Cryptography.SHA256]::Create()
    try{([BitConverter]::ToString($algorithm.ComputeHash([Text.UTF8Encoding]::new($false).GetBytes($text)))-replace '-','').ToLowerInvariant()}finally{$algorithm.Dispose()}
}
$manifestPath=if([IO.Path]::IsPathRooted($Manifest)){(Resolve-Path -LiteralPath $Manifest).Path}else{(Resolve-Path -LiteralPath (Join-Path $projectRoot $Manifest)).Path}
if((Raw $manifestPath)-ne $ExpectedManifestSha256.ToLowerInvariant()){throw 'Stage B prefrozen manifest hash mismatch'}
$m=Get-Content -Raw -LiteralPath $manifestPath|ConvertFrom-Json
if($m.protocolId-ne'G2_PHASE2_FINAL_SPRINT_P3_V1'-or$m.mode-ne'P3_STAGE_B_FINAL'){throw 'Stage B protocol/mode mismatch'}
$protocolPath=Join-Path $projectRoot $m.auditProtocolPath
if((Canon $protocolPath)-ne$m.auditProtocolCanonicalSha256-or$m.auditProtocolCanonicalSha256-ne'14967ccaef673152b3784fc33ccf184c527769403418db765c82ee54d6aa6278'){throw 'Stage B audit protocol mismatch'}
$upstreamPath=Join-Path $projectRoot $m.upstreamStageFIndependentAuditPath
if((Raw $upstreamPath)-ne$m.upstreamStageFIndependentAuditSha256){throw 'Stage B upstream audit hash mismatch'}
$upstream=Get-Content -Raw -LiteralPath $upstreamPath|ConvertFrom-Json
if($upstream.verdict-ne'STAGE_F_FINAL_PASS'-or-not$upstream.authorizedNextStage-or$upstream.integrityStatus-ne'PASS'){throw 'Stage B upstream gate is not PASS'}
if($m.mapCandidates.Count-ne212-or$m.refinement.offsetTemplate.Count-ne27-or$m.maximumCandidatePoses-ne320-or$m.outcomeAdaptiveCandidateAddition){throw 'Stage B 212+108 frozen grid contract mismatch'}
$safeRoot=$projectRoot.Replace('\','/')
$head=(&git -c "safe.directory=$safeRoot" -C $projectRoot rev-parse HEAD).Trim()
$branch=(&git -c "safe.directory=$safeRoot" -C $projectRoot branch --show-current).Trim()
&git -c "safe.directory=$safeRoot" -C $projectRoot merge-base --is-ancestor $m.sourceCommit $head
if($LASTEXITCODE-ne0-or$branch-ne'codex/p2-unity-final'){throw 'Stage B source lineage mismatch'}
$scoped=@(
    'Assets/ThoracicUSCoach/Runtime/ThoracicP3StageBFinalTypes.cs',
    'Assets/ThoracicUSCoach/Runtime/ThoracicP3StageBFinalTypes.cs.meta',
    'Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.P3StageBFinal.cs',
    'Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.P3StageBFinal.cs.meta',
    'Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.DirectFreshCapture.cs',
    'Assets/ThoracicUSCoach/Editor/ThoracicP3StageBFinalBatch.cs',
    'Assets/ThoracicUSCoach/Editor/ThoracicP3StageBFinalBatch.cs.meta',
    'Assets/ThoracicUSCoach/Tests/Editor/ThoracicP3StageBFinalTests.cs',
    'Assets/ThoracicUSCoach/Tests/Editor/ThoracicP3StageBFinalTests.cs.meta',
    'Tools/ThoracicUSCoach/materialize_p3_stage_b_final.py',
    'Tools/ThoracicUSCoach/test_materialize_p3_stage_b_final.py',
    'Tools/ThoracicUSCoach/prepare_p3_stage_b_final_manifest.ps1',
    'Tools/ThoracicUSCoach/run_p3_stage_b_final.ps1',
    'Tools/ThoracicUSCoach/g2_p3_stage_b_final_audit.py',
    'Tools/ThoracicUSCoach/g2_p2_basin_audit.py',
    'Tools/ThoracicUSCoach/g2_p2_freshness_audit.py',
    'Tools/ThoracicUSCoach/g2_r6_audit.py',
    'Tools/ThoracicUSCoach/experiments/g2_p3_stage_b_final_protocol_v1.json'
)
foreach($path in $scoped){
    &git -c "safe.directory=$safeRoot" -C $projectRoot cat-file -e "$($m.sourceCommit):$path"
    if($LASTEXITCODE-ne0){throw "Stage B source commit does not contain scoped file: $path"}
}
&git -c "safe.directory=$safeRoot" -C $projectRoot diff --quiet "$($m.sourceCommit)..HEAD" -- $scoped
if($LASTEXITCODE-ne0){throw 'Stage B scoped source differs from manifest sourceCommit'}
$scopedDirt=@(&git -c "safe.directory=$safeRoot" -C $projectRoot status --porcelain=v1 -- $scoped)
if($scopedDirt.Count-ne0){throw 'Stage B scoped source has uncommitted changes'}
if(-not(Test-Path -LiteralPath $PythonPath -PathType Leaf)){throw 'Stage B Python path missing'}
$resolvedUnity=(Resolve-Path -LiteralPath $UnityPath).Path
$active=Get-Process -Name Unity -ErrorAction SilentlyContinue|Where-Object{try{(Resolve-Path -LiteralPath $_.Path).Path-eq$resolvedUnity}catch{$false}}
if($active){throw 'Stage B exclusive Unity check failed'}
$logRoot=Join-Path $projectRoot 'Artifacts\ThoracicUSCoach\active2d\evaluation\p3_stage_b_final'
New-Item -ItemType Directory -Path $logRoot -Force|Out-Null
$env:THORACIC_P3_STAGE_B_EXECUTION_COMMIT=$m.sourceCommit
$env:THORACIC_P3_STAGE_B_MANIFEST_SHA256=$ExpectedManifestSha256.ToLowerInvariant()
$env:THORACIC_P3_STAGE_B_PYTHON=(Resolve-Path -LiteralPath $PythonPath).Path
$xml=Join-Path $logRoot 'p3_stage_b_final_editmode.xml';$log=Join-Path $logRoot 'p3_stage_b_final_editmode.log'
$test=Start-Process -FilePath $resolvedUnity -ArgumentList @('-batchmode','-projectPath',$projectRoot,'-runTests','-testPlatform','EditMode','-testFilter','CSSALT.ThoracicUSCoach.Tests.ThoracicP3StageBFinalTests','-testResults',$xml,'-logFile',$log) -WindowStyle Hidden -PassThru
if(-not$test.WaitForExit(900000)){try{$test.Kill()}catch{};throw 'Stage B EditMode preflight timeout'}
$test.Refresh();if(-not(Test-Path -LiteralPath $xml)){throw "Stage B preflight produced no XML (exit $($test.ExitCode))"}
[xml]$result=Get-Content -Raw -LiteralPath $xml
if([int]$result.'test-run'.failed-ne0){throw 'Stage B engineering preflight failed'}
if($Mode-eq'EngineeringPreflight'){
    [pscustomobject]@{status='ENGINEERING_PREFLIGHT_PASS';sourceCommit=$m.sourceCommit;manifestSha256=$ExpectedManifestSha256.ToLowerInvariant();protocolCanonicalSha256=$m.auditProtocolCanonicalSha256;baseCandidates=212;conditionalRefinementSlots=108;passed=[int]$result.'test-run'.passed;failed=[int]$result.'test-run'.failed}|ConvertTo-Json
    exit 0
}
$outputRoot=[IO.Path]::GetFullPath((Join-Path $projectRoot $m.outputDirectory))
$handoffFinal=[IO.Path]::GetFullPath($HandoffDestination)
if((Test-Path $outputRoot)-or(Test-Path($outputRoot+'.partial'))-or(Test-Path $handoffFinal)-or(Test-Path($handoffFinal+'.partial'))){throw 'Stage B refuses to overwrite output/handoff'}
$rootUri=[Uri]::new($projectRoot.TrimEnd('\')+'\');$relative=[Uri]::UnescapeDataString($rootUri.MakeRelativeUri([Uri]::new($manifestPath)).ToString())
$captureLog=Join-Path $logRoot 'p3_stage_b_final_capture.log'
$process=Start-Process -FilePath $resolvedUnity -ArgumentList @('-batchmode','-projectPath',$projectRoot,'-executeMethod','CSSALT.ThoracicUSCoach.Editor.ThoracicP3StageBFinalBatch.Run','-p3StageBFinalManifest',$relative,'-logFile',$captureLog) -WindowStyle Hidden -PassThru
if(-not$process.WaitForExit(2100000)){try{$process.Kill()}catch{};throw 'Stage B outcome timeout'}
$process.Refresh();if($process.ExitCode-ne0){throw "Stage B Unity failed exit $($process.ExitCode)"}
$required=@('stage_b_final_manifest.json','stage_b_coarse_snapshot.json','refinement_selection_manifest.json','stage_b_final_report.json','evidence_index.json','snapshot_seal.json')
foreach($name in $required){if(-not(Test-Path -LiteralPath (Join-Path $outputRoot $name)-PathType Leaf)){throw "Stage B snapshot missing $name"}}
$seal=Get-Content -Raw (Join-Path $outputRoot 'snapshot_seal.json')|ConvertFrom-Json
$report=Get-Content -Raw (Join-Path $outputRoot 'stage_b_final_report.json')|ConvertFrom-Json
$index=Get-Content -Raw (Join-Path $outputRoot 'evidence_index.json')|ConvertFrom-Json
if(-not$report.complete-or$report.infrastructureFailure-or-not$seal.complete-or-not$seal.immutable-or$seal.failureSnapshot){throw 'Stage B snapshot incomplete'}
foreach($check in @(@('stage_b_final_manifest.json','manifestSha256'),@('stage_b_coarse_snapshot.json','coarseSnapshotSha256'),@('refinement_selection_manifest.json','refinementSelectionManifestSha256'),@('stage_b_final_report.json','reportSha256'),@('evidence_index.json','evidenceIndexSha256'))){if((Raw (Join-Path $outputRoot $check[0]))-ne$seal.($check[1])){throw "Stage B seal hash mismatch: $($check[1])"}}
foreach($entry in $index.files){$path=[IO.Path]::GetFullPath((Join-Path $outputRoot $entry.path));if(-not$path.StartsWith($outputRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)-or-not(Test-Path $path)-or(Raw $path)-ne$entry.sha256-or(Get-Item $path).Length-ne[long]$entry.bytes){throw "Stage B indexed file mismatch: $($entry.path)"}}
New-Item -ItemType Directory -Path (Split-Path -Parent $handoffFinal) -Force|Out-Null
Copy-Item -LiteralPath $outputRoot -Destination ($handoffFinal+'.partial') -Recurse;Move-Item -LiteralPath ($handoffFinal+'.partial') -Destination $handoffFinal
Get-ChildItem -LiteralPath $handoffFinal -Recurse -File|ForEach-Object{$_.IsReadOnly=$true}
[pscustomobject]@{status='STAGE_B_FINAL_SNAPSHOT_SEALED';handoff=$handoffFinal;sourceCommit=$m.sourceCommit;manifestSha256=$ExpectedManifestSha256;recordCount=$seal.recordCount}|ConvertTo-Json
