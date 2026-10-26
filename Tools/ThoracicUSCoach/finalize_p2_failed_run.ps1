param(
    [string]$SourcePartial = 'Artifacts/ThoracicUSCoach/active2d/p2_freshness_direct_v2_20261026.partial',
    [string]$Manifest = 'Artifacts/ThoracicUSCoach/active2d/g2_p2_freshness_transition_manifest_v2.json',
    [string]$CaptureLog = 'Artifacts/ThoracicUSCoach/active2d/evaluation/p2_final_sprint/p2_freshness_direct_v2_capture.log',
    [string]$JobHistory = 'Artifacts/ThoracicUSCoach/active2d/evaluation/p2_final_sprint/p2_preflight_history_v1.json',
    [string]$Destination = 'D:\CSSALT\Thoracic-US-Coach-P2-Handoff\freshness\p2_freshness_direct_v2_20261026_failed_run',
    [string]$ExpectedManifestSha256 = 'de9c833e2f660da21b5e89e9e818239dcef0f82a843783eec9ae7566845307b7',
    [string]$ExpectedCaptureSourceCommit = 'c93c18c6af9e9140ed9722cdd4a85bb8470be094'
)

$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path

function Get-RawSha256([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { $hash = $algorithm.ComputeHash($stream) } finally { $stream.Dispose(); $algorithm.Dispose() }
    ([BitConverter]::ToString($hash) -replace '-', '').ToLowerInvariant()
}

function Get-CanonicalSha256([string]$Path) {
    $text = [IO.File]::ReadAllText($Path).Replace("`r`n", "`n").Replace("`r", "`n")
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($text)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { $hash = $algorithm.ComputeHash($bytes) } finally { $algorithm.Dispose() }
    ([BitConverter]::ToString($hash) -replace '-', '').ToLowerInvariant()
}

function Resolve-ProjectFile([string]$Path) {
    $resolved = if ([IO.Path]::IsPathRooted($Path)) {
        (Resolve-Path -LiteralPath $Path).Path
    } else {
        (Resolve-Path -LiteralPath (Join-Path $projectRoot $Path)).Path
    }
    return $resolved
}

$sourceRoot = Resolve-ProjectFile $SourcePartial
$manifestPath = Resolve-ProjectFile $Manifest
$captureLogPath = Resolve-ProjectFile $CaptureLog
$jobHistoryPath = Resolve-ProjectFile $JobHistory
$destinationFinal = [IO.Path]::GetFullPath($Destination)
$destinationPartial = $destinationFinal + '.partial'
if ((Test-Path -LiteralPath $destinationFinal) -or (Test-Path -LiteralPath $destinationPartial)) {
    throw 'P2 failed-run finalizer refuses to overwrite an existing handoff'
}

$manifestSha = Get-RawSha256 $manifestPath
if ($manifestSha -ne $ExpectedManifestSha256.ToLowerInvariant()) {
    throw "P2 failed-run manifest hash mismatch: $manifestSha"
}
$manifestObject = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
if ($manifestObject.sourceCommit -ne $ExpectedCaptureSourceCommit) {
    throw 'P2 failed-run capture source commit mismatch'
}
$captureProtocolPath = Resolve-ProjectFile $manifestObject.captureProtocolPath
$auditProtocolPath = Resolve-ProjectFile $manifestObject.auditProtocolPath
$captureProtocolSha = Get-CanonicalSha256 $captureProtocolPath
$auditProtocolSha = Get-CanonicalSha256 $auditProtocolPath
if ($captureProtocolSha -ne $manifestObject.captureProtocolSha256 -or
    $auditProtocolSha -ne $manifestObject.auditProtocolSha256) {
    throw 'P2 failed-run protocol hash mismatch'
}

$capturedManifest = Join-Path $sourceRoot 'capture_input_manifest.json'
if (-not (Test-Path -LiteralPath $capturedManifest -PathType Leaf) -or
    (Get-RawSha256 $capturedManifest) -ne $manifestSha) {
    throw 'P2 failed-run partial does not contain the exact prefrozen capture manifest'
}

$safeRoot = $projectRoot.Replace('\', '/')
$finalizerCommit = (& git -c "safe.directory=$safeRoot" -C $projectRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Unable to resolve failed-run finalizer commit' }

New-Item -ItemType Directory -Path $destinationPartial -Force | Out-Null
$logRoot = Join-Path $destinationPartial 'logs'
$provenanceRoot = Join-Path $destinationPartial 'provenance'
New-Item -ItemType Directory -Path $logRoot, $provenanceRoot -Force | Out-Null
Get-ChildItem -LiteralPath $sourceRoot -Force | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $destinationPartial -Recurse
}
Copy-Item -LiteralPath $captureLogPath -Destination (Join-Path $logRoot 'p2_freshness_direct_v2_capture.log')
Copy-Item -LiteralPath $jobHistoryPath -Destination (Join-Path $provenanceRoot 'p2_preflight_history_v1.json')

$rawFiles = New-Object System.Collections.Generic.List[object]
$sourceFiles = @(Get-ChildItem -LiteralPath $sourceRoot -Recurse -File | Sort-Object FullName)
$sourceUri = [Uri]::new($sourceRoot.TrimEnd('\') + '\')
foreach ($sourceFile in $sourceFiles) {
    $relative = [Uri]::UnescapeDataString($sourceUri.MakeRelativeUri([Uri]::new($sourceFile.FullName)).ToString())
    $copied = Join-Path $destinationPartial $relative.Replace('/', [IO.Path]::DirectorySeparatorChar)
    $sourceSha = Get-RawSha256 $sourceFile.FullName
    if ((Get-RawSha256 $copied) -ne $sourceSha -or (Get-Item -LiteralPath $copied).Length -ne $sourceFile.Length) {
        throw "P2 failed-run raw copy mismatch: $relative"
    }
    $rawFiles.Add([ordered]@{role='raw_partial';path=$relative;sha256=$sourceSha;bytes=$sourceFile.Length})
}
foreach ($entry in @(
    [ordered]@{role='capture_log';path='logs/p2_freshness_direct_v2_capture.log';source=$captureLogPath},
    [ordered]@{role='job_history';path='provenance/p2_preflight_history_v1.json';source=$jobHistoryPath}
)) {
    $copied = Join-Path $destinationPartial $entry.path.Replace('/', [IO.Path]::DirectorySeparatorChar)
    $rawFiles.Add([ordered]@{role=$entry.role;path=$entry.path;sha256=(Get-RawSha256 $copied);bytes=(Get-Item -LiteralPath $copied).Length})
}

$reportPath = Join-Path $destinationPartial 'p2_freshness_report.json'
$reportObject = Get-Content -Raw -LiteralPath $reportPath | ConvertFrom-Json
if ($reportObject.complete -or -not $reportObject.infrastructureFailure) {
    throw 'P2 failed-run report is not an explicit incomplete failure'
}
$metadata = [ordered]@{
    schemaVersion = 1
    artifactType = 'P2_FAILED_RUN_METADATA'
    generatedUtc = [DateTime]::UtcNow.ToString('o')
    complete = $false
    scientificVerdictPending = $true
    failureReason = 'accepted-but-incomplete not observed within frozen budget'
    infrastructureFailure = [string]$reportObject.infrastructureFailure
    captureJobId = '580adbf0b2e6'
    priorJobIds = @((Get-Content -Raw -LiteralPath $jobHistoryPath | ConvertFrom-Json).jobs | ForEach-Object { $_.jobId })
    captureSourceCommit = $ExpectedCaptureSourceCommit
    finalizerCommit = $finalizerCommit
    manifestSha256 = $manifestSha
    captureProtocolCanonicalSha256 = $captureProtocolSha
    auditProtocolCanonicalSha256 = $auditProtocolSha
    sourcePartialPreserved = $true
    captureLogPreserved = $true
    rawPayloadFileCount = $sourceFiles.Count
    rawPayloadBytes = [long](($sourceFiles | Measure-Object Length -Sum).Sum)
    imageOrResultBytesModified = $false
    scientificUse = 'AUDIT_PENDING_FAILED_RUN_ONLY'
}
$metadataPath = Join-Path $destinationPartial 'failed_run_metadata.json'
[IO.File]::WriteAllText($metadataPath, ($metadata | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
$rawFiles.Add([ordered]@{role='failed_run_metadata';path='failed_run_metadata.json';sha256=(Get-RawSha256 $metadataPath);bytes=(Get-Item -LiteralPath $metadataPath).Length})

$index = [ordered]@{
    schemaVersion = 1
    artifactType = 'P2_EVIDENCE_INDEX'
    generatedUtc = [DateTime]::UtcNow.ToString('o')
    captureSourceCommit = $ExpectedCaptureSourceCommit
    manifestSha256 = $manifestSha
    files = @($rawFiles)
}
$indexPath = Join-Path $destinationPartial 'evidence_index.json'
[IO.File]::WriteAllText($indexPath, ($index | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))

$seal = [ordered]@{
    schemaVersion = 1
    artifactType = 'P2_SNAPSHOT_SEAL'
    generatedUtc = [DateTime]::UtcNow.ToString('o')
    complete = $true
    failureSnapshot = $true
    scientificVerdictPending = $true
    immutable = $true
    failureReason = $metadata.failureReason
    captureSourceCommit = $ExpectedCaptureSourceCommit
    sourceCommit = $ExpectedCaptureSourceCommit
    finalizerCommit = $finalizerCommit
    manifestSha256 = $manifestSha
    captureProtocolCanonicalSha256 = $captureProtocolSha
    auditProtocolCanonicalSha256 = $auditProtocolSha
    sceneCanonicalSha256 = [string]$reportObject.sceneCanonicalSha256
    evaluatorCanonicalSha256 = [string]$reportObject.evaluatorCanonicalSha256
    evidenceIndexSha256 = Get-RawSha256 $indexPath
    reportSha256 = Get-RawSha256 $reportPath
    indexedFiles = $rawFiles.Count
    recordCount = @($reportObject.records).Count
}
$sealPath = Join-Path $destinationPartial 'snapshot_seal.json'
[IO.File]::WriteAllText($sealPath, ($seal | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
$sealSha = Get-RawSha256 $sealPath

Move-Item -LiteralPath $destinationPartial -Destination $destinationFinal
Get-ChildItem -LiteralPath $destinationFinal -Recurse -File | ForEach-Object { $_.IsReadOnly = $true }

[pscustomobject]@{
    status = 'FAILED_RUN_HANDOFF_SEALED'
    complete = $false
    scientificVerdictPending = $true
    handoff = $destinationFinal
    sealSha256 = $sealSha
    evidenceIndexSha256 = $seal.evidenceIndexSha256
    reportSha256 = $seal.reportSha256
    indexedFiles = $seal.indexedFiles
} | ConvertTo-Json
