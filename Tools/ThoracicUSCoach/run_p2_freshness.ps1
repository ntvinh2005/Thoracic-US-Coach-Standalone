param(
    [Parameter(Mandatory = $true)] [string]$Manifest,
    [Parameter(Mandatory = $true)] [string]$ExpectedManifestSha256,
    [string]$HandoffDestination = 'D:\CSSALT\Thoracic-US-Coach-P2-Handoff\freshness\p2_freshness_20261026',
    [string]$UnityPath = 'C:\Program Files\Unity\Hub\Editor\6000.3.9f1\Editor\Unity.exe'
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
function Get-RawSha256([string]$Path) {
    $stream=[IO.File]::OpenRead($Path);$algorithm=[Security.Cryptography.SHA256]::Create()
    try{$hash=$algorithm.ComputeHash($stream)}finally{$stream.Dispose();$algorithm.Dispose()}
    ([BitConverter]::ToString($hash)-replace'-','').ToLowerInvariant()
}
function Get-CanonicalSha256([string]$Path) {
    $text=[IO.File]::ReadAllText($Path).Replace("`r`n","`n").Replace("`r","`n");$bytes=[Text.UTF8Encoding]::new($false).GetBytes($text)
    $algorithm=[Security.Cryptography.SHA256]::Create();try{$hash=$algorithm.ComputeHash($bytes)}finally{$algorithm.Dispose()}
    ([BitConverter]::ToString($hash)-replace'-','').ToLowerInvariant()
}
$manifestPath = if ([IO.Path]::IsPathRooted($Manifest)) {(Resolve-Path -LiteralPath $Manifest).Path} else {(Resolve-Path -LiteralPath (Join-Path $projectRoot $Manifest)).Path}
if ((Get-RawSha256 $manifestPath) -ne $ExpectedManifestSha256.ToLowerInvariant()) { throw 'P2 pre-frozen manifest hash mismatch before launch' }
$manifestObject=Get-Content -Raw -LiteralPath $manifestPath|ConvertFrom-Json
if ($manifestObject.protocolId -ne 'G2_PHASE2_FINAL_SPRINT_P2_V1' -or $manifestObject.mode -ne 'P2_IMAGE_POSE_FRESHNESS') { throw 'P2 manifest protocol/mode mismatch' }
$safeRoot=$projectRoot.Replace('\','/');$commit=(& git -c "safe.directory=$safeRoot" -C $projectRoot rev-parse HEAD).Trim();$branch=(& git -c "safe.directory=$safeRoot" -C $projectRoot branch --show-current).Trim()
if ($commit -ne $manifestObject.sourceCommit -or $branch -ne 'codex/p2-unity-final') { throw 'P2 source commit/branch mismatch' }
if ((Get-CanonicalSha256 (Join-Path $projectRoot $manifestObject.captureProtocolPath)) -ne $manifestObject.captureProtocolSha256 -or (Get-CanonicalSha256 (Join-Path $projectRoot $manifestObject.auditProtocolPath)) -ne $manifestObject.auditProtocolSha256) { throw 'P2 protocol hash mismatch' }
$resolvedUnity=(Resolve-Path -LiteralPath $UnityPath).Path
$activeEditor=Get-Process -Name Unity -ErrorAction SilentlyContinue|Where-Object{try{(Resolve-Path -LiteralPath $_.Path).Path -eq $resolvedUnity}catch{$false}}
if($activeEditor){throw 'Exclusive Unity preflight failed'}
$outputRoot=[IO.Path]::GetFullPath((Join-Path $projectRoot $manifestObject.outputDirectory))
if((Test-Path -LiteralPath $outputRoot)-or(Test-Path -LiteralPath ($outputRoot+'.partial'))){throw 'P2 refuses to overwrite output'}
$handoffFinal=[IO.Path]::GetFullPath($HandoffDestination);$handoffPartial=$handoffFinal+'.partial'
if((Test-Path -LiteralPath $handoffFinal)-or(Test-Path -LiteralPath $handoffPartial)){throw 'P2 refuses to overwrite handoff'}
$env:THORACIC_P2_EXECUTION_COMMIT=$commit;$env:THORACIC_P2_MANIFEST_SHA256=$ExpectedManifestSha256.ToLowerInvariant()
$rootUri=[Uri]::new($projectRoot.TrimEnd('\')+'\');$relativeManifest=[Uri]::UnescapeDataString($rootUri.MakeRelativeUri([Uri]::new($manifestPath)).ToString())
$logRoot=Join-Path $projectRoot 'Artifacts\ThoracicUSCoach\active2d\evaluation\p2_final_sprint';New-Item -ItemType Directory -Path $logRoot -Force|Out-Null
$captureLog=Join-Path $logRoot 'p2_freshness_capture.log'
$process=Start-Process -FilePath $UnityPath -ArgumentList @('-batchmode','-projectPath',$projectRoot,'-executeMethod','CSSALT.ThoracicUSCoach.Editor.ThoracicP2FreshnessBatch.Run','-p2FreshnessManifest',$relativeManifest,'-logFile',$captureLog) -WindowStyle Hidden -Wait -PassThru
if($process.ExitCode -ne 0){throw "P2 freshness Unity failed exit $($process.ExitCode); preserve $captureLog"}
$reportPath=Join-Path $outputRoot 'p2_freshness_report.json';$indexPath=Join-Path $outputRoot 'evidence_index.json';$sealPath=Join-Path $outputRoot 'snapshot_seal.json';$captureManifest=Join-Path $outputRoot 'capture_input_manifest.json'
foreach($required in @($reportPath,$indexPath,$sealPath,$captureManifest)){if(-not(Test-Path -LiteralPath $required -PathType Leaf)){throw "P2 snapshot missing $required"}}
$report=Get-Content -Raw -LiteralPath $reportPath|ConvertFrom-Json;$index=Get-Content -Raw -LiteralPath $indexPath|ConvertFrom-Json;$seal=Get-Content -Raw -LiteralPath $sealPath|ConvertFrom-Json
if(-not $report.complete -or $report.infrastructureFailure -or -not $seal.complete -or -not $seal.immutable){throw "P2 snapshot incomplete: $($report.infrastructureFailure)"}
if((Get-RawSha256 $captureManifest)-ne $ExpectedManifestSha256.ToLowerInvariant() -or (Get-RawSha256 $indexPath)-ne $seal.evidenceIndexSha256 -or (Get-RawSha256 $reportPath)-ne $seal.reportSha256){throw 'P2 snapshot seal mismatch'}
foreach($entry in $index.files){$path=[IO.Path]::GetFullPath((Join-Path $outputRoot $entry.path));if(-not $path.StartsWith($outputRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe indexed path'};if(-not(Test-Path -LiteralPath $path)-or(Get-RawSha256 $path)-ne $entry.sha256 -or(Get-Item -LiteralPath $path).Length-ne[long]$entry.bytes){throw "P2 indexed file mismatch: $($entry.path)"}}
if(@($report.records).Count-ne[int]$seal.recordCount){throw 'P2 report/seal record count mismatch'}
New-Item -ItemType Directory -Path (Split-Path -Parent $handoffFinal) -Force|Out-Null;Copy-Item -LiteralPath $outputRoot -Destination $handoffPartial -Recurse
foreach($entry in $index.files){$path=Join-Path $handoffPartial $entry.path;if((Get-RawSha256 $path)-ne $entry.sha256){throw "P2 copied file mismatch: $($entry.path)"}}
Move-Item -LiteralPath $handoffPartial -Destination $handoffFinal;Get-ChildItem -LiteralPath $handoffFinal -Recurse -File|ForEach-Object{$_.IsReadOnly=$true}
[pscustomobject]@{mode=$report.mode;sourceCommit=$commit;manifestSha256=$ExpectedManifestSha256;records=@($report.records).Count;accepted=@($report.records|Where-Object captureDisposition -eq 'accepted').Count;discarded=@($report.records|Where-Object captureDisposition -eq 'discarded').Count;handoff=$handoffFinal}|ConvertTo-Json
