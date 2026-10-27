using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CSSALT.ThoracicUSCoach
{
    public sealed partial class ThoracicUSCoachController
    {
        private P3StageBFinalManifest p3StageBManifest;
        private P3StageBRuntimeReport p3StageBRuntime;
        private string p3StageBFinalRoot;
        private string p3StageBPartialRoot;
        private bool p3StageBComplete;
        private int p3StageBRecordIndex;
        private readonly HashSet<string> p3StageBAcceptedHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public bool P3StageBComplete => p3StageBComplete;
        public string P3StageBFailure => p3StageBRuntime != null ? p3StageBRuntime.infrastructureFailure : string.Empty;

        public bool StartP3StageBFinal(string manifestProjectRelativePath)
        {
            if (!Application.isPlaying || active2DRoutine != null || active2DCaptureRoutine != null) return false;
            try
            {
                AutoWire();
                string manifestPath = ResolveProjectPath(manifestProjectRelativePath);
                byte[] manifestBytes = File.ReadAllBytes(manifestPath);
                p3StageBManifest = JsonUtility.FromJson<P3StageBFinalManifest>(Encoding.UTF8.GetString(manifestBytes));
                ValidateP3StageBManifest(manifestPath, manifestBytes);
                RequireP3StageBFrozenEvaluatorFloor();
                p3StageBFinalRoot = ResolveProjectPath(p3StageBManifest.outputDirectory);
                p3StageBPartialRoot = p3StageBFinalRoot + ".partial";
                if (Directory.Exists(p3StageBFinalRoot) || Directory.Exists(p3StageBPartialRoot))
                    throw new InvalidOperationException("P3 Stage B refuses to overwrite final or partial output");
                Directory.CreateDirectory(Path.Combine(p3StageBPartialRoot, "images"));
                Directory.CreateDirectory(Path.Combine(p3StageBPartialRoot, "discarded"));
                File.WriteAllBytes(Path.Combine(p3StageBPartialRoot, "stage_b_final_manifest.json"), manifestBytes);
                p3StageBRuntime = new P3StageBRuntimeReport
                {
                    protocolId = p3StageBManifest.protocolId,
                    mode = p3StageBManifest.mode,
                    sourceCommit = p3StageBManifest.sourceCommit,
                    manifestSha256 = Sha256Bytes(manifestBytes),
                    auditProtocolCanonicalSha256 = p3StageBManifest.auditProtocolCanonicalSha256,
                    upstreamStageFIndependentAuditSha256 = p3StageBManifest.upstreamStageFIndependentAuditSha256,
                    upstreamSnapshotSealSha256 = p3StageBManifest.upstreamSnapshotSealSha256,
                    sceneCanonicalSha256 = HashR6CanonicalText(p3StageBManifest.scene),
                    sceneMaterializedSha256 = HashR6Raw(ResolveProjectPath(p3StageBManifest.scene)),
                    evaluatorCanonicalSha256 = HashR6CanonicalText("Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.cs"),
                    evaluatorMaterializedSha256 = HashR6Raw(ResolveProjectPath("Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.cs"))
                };
                p3StageBRecordIndex = 0;
                p3StageBAcceptedHashes.Clear();
                p3StageBComplete = false;
                active2DRoutine = StartCoroutine(RunP3StageBFinal());
                return true;
            }
            catch (Exception exception)
            {
                p3StageBRuntime = p3StageBRuntime ?? new P3StageBRuntimeReport();
                p3StageBRuntime.infrastructureFailure = exception.ToString();
                p3StageBComplete = true;
                UnityEngine.Debug.LogError("[ThoracicUSCoach][P3StageB] " + exception);
                return false;
            }
        }

        private IEnumerator RunP3StageBFinal()
        {
            Stack<IEnumerator> work = new Stack<IEnumerator>();
            work.Push(RunP3StageBFinalCore());
            try
            {
                while (work.Count > 0)
                {
                    object current;
                    try
                    {
                        IEnumerator active = work.Peek();
                        if (!active.MoveNext()) { work.Pop(); continue; }
                        current = active.Current;
                    }
                    catch (Exception exception)
                    {
                        p3StageBRuntime.infrastructureFailure = exception.ToString();
                        break;
                    }
                    if (current is IEnumerator nested) work.Push(nested); else yield return current;
                }
                p3StageBRuntime.complete = string.IsNullOrEmpty(p3StageBRuntime.infrastructureFailure);
            }
            finally
            {
                try
                {
                    SaveP3StageBRuntime();
                    if (p3StageBRuntime.complete) SealP3StageBSnapshot();
                }
                catch (Exception exception)
                {
                    p3StageBRuntime.complete = false;
                    p3StageBRuntime.infrastructureFailure = exception.ToString();
                    SaveP3StageBRuntime();
                }
                p3StageBComplete = true;
                active2DRoutine = null;
            }
        }

        private IEnumerator RunP3StageBFinalCore()
        {
            Dictionary<string, R6PositiveControl> controls = p3StageBManifest.historicalControls.ToDictionary(item => item.controlId, StringComparer.Ordinal);
            Dictionary<string, P3StageBCandidateResult> results = new Dictionary<string, P3StageBCandidateResult>(StringComparer.Ordinal);
            foreach (P3StageBCandidate candidate in p3StageBManifest.mapCandidates)
                yield return RunP3StageBCandidate(candidate, controls[candidate.anchorControlId], null, results);
            SaveP3StageBRuntime();
            RunP3StageBMaterializer("select-refinement");

            string selectionPath = Path.Combine(p3StageBPartialRoot, p3StageBManifest.refinement.selectionManifestFile);
            P3StageBRefinementSelectionManifest selection = JsonUtility.FromJson<P3StageBRefinementSelectionManifest>(File.ReadAllText(selectionPath));
            ValidateP3StageBMaterializedCandidates(selection);
            foreach (P3StageBCandidate candidate in selection.mapCandidates)
                yield return RunP3StageBCandidate(candidate, controls[candidate.anchorControlId], results[candidate.centerCandidateId], results);
            if (p3StageBRuntime.attemptedCandidateIds.Count != 320 || p3StageBRuntime.attemptedCandidateIds.Distinct().Count() != 320)
                throw new InvalidOperationException("P3 Stage B did not attempt exactly 320 frozen/materialized slots");

            SaveP3StageBRuntime();
            RunP3StageBMaterializer("select-representatives");
            P3StageBRepresentativePlan plan = JsonUtility.FromJson<P3StageBRepresentativePlan>(File.ReadAllText(Path.Combine(p3StageBPartialRoot, "stage_b_representative_plan.json")));
            foreach (P3StageBRepresentativePlanItem item in plan.representatives)
            {
                P3StageBCandidateResult source = results[item.sourceCandidateId];
                P3StageBRepresentativeRuntime representative = new P3StageBRepresentativeRuntime
                {
                    representativeId = item.representativeId,
                    sourceCandidateId = item.sourceCandidateId,
                    component = item.component
                };
                p3StageBRuntime.representativeRuntime.Add(representative);
                R6AttemptRecord restore = RestoreR6Control(new R6PositiveControl
                {
                    controlId = item.sourceCandidateId,
                    probePosition = source.actualPosition,
                    probeRotation = source.actualRotation
                }, "stage_b_representative:" + item.representativeId);
                if (!restore.accepted)
                {
                    representative.rejectionReason = restore.rejectionReason;
                    continue;
                }
                yield return CaptureP3StageBVisit("representative", source.candidateId, source.anchorControlId,
                    source.stage, item.representativeId, restore.requestedPosition, restore.requestedRotation, 3,
                    representative.recordIds);
            }
            SaveP3StageBRuntime();
            RunP3StageBMaterializer("finalize-report");
        }

        private IEnumerator RunP3StageBCandidate(P3StageBCandidate candidate, R6PositiveControl historicalAnchor,
            P3StageBCandidateResult selectedCenter, Dictionary<string, P3StageBCandidateResult> results)
        {
            P3StageBCandidateResult result = new P3StageBCandidateResult
            {
                candidateId = candidate.candidateId,
                anchorControlId = candidate.anchorControlId,
                stage = candidate.stage
            };
            results.Add(candidate.candidateId, result);
            p3StageBRuntime.candidateResults.Add(result);
            p3StageBRuntime.attemptedCandidateIds.Add(candidate.candidateId);
            R6AttemptRecord attempt;
            if (candidate.stage == "anchor")
                attempt = RestoreR6Control(historicalAnchor, "stage_b:" + candidate.candidateId);
            else
            {
                R6PositiveControl placement = historicalAnchor;
                if (candidate.stage == "refinement")
                    placement = new R6PositiveControl { controlId = selectedCenter.candidateId, probePosition = selectedCenter.actualPosition, probeRotation = selectedCenter.actualRotation };
                attempt = PlaceR6MapCandidate(placement, new R6MapCandidate
                {
                    candidateId = candidate.candidateId,
                    anchorControlId = candidate.anchorControlId,
                    stage = candidate.stage,
                    parentCandidateId = candidate.centerCandidateId,
                    longitudinalOffsetMm = candidate.longitudinalOffsetMm,
                    lateralOffsetMm = candidate.lateralOffsetMm,
                    yawDeg = candidate.yawDeg,
                    repeatRenders = 2
                });
            }
            PopulateP3StageBCandidateResult(result, attempt);
            if (!attempt.accepted) yield break;
            yield return CaptureP3StageBVisit("candidate", candidate.candidateId, candidate.anchorControlId,
                candidate.stage, string.Empty, attempt.requestedPosition, attempt.requestedRotation, 2, result.recordIds);
            P3StageBRecord last = p3StageBRuntime.records.Last(item => item.candidateId == candidate.candidateId && item.captureDisposition == "accepted");
            result.actualPosition = last.probePosition;
            result.actualRotation = last.probeRotation;
            result.contactPosition = last.contactPosition;
        }

        private IEnumerator CaptureP3StageBVisit(string sourceKind, string candidateId, string anchorControlId,
            string stage, string componentId, Vector3 requestedPosition, Quaternion requestedRotation,
            int acceptedSlots, List<string> recordIds)
        {
            DirectFreshPoseToken pose = BeginDirectFreshCapturePose("stage_b_" + sourceKind + ":" + candidateId, true);
            DirectFreshCaptureResult immediate = CaptureDirectProducerImmediate(pose, "immediate_post_pose");
            SaveP3StageBRecord(sourceKind, candidateId, anchorControlId, stage, componentId, "discarded",
                "immediate_post_pose", -1, requestedPosition, requestedRotation, immediate, recordIds);
            DirectFreshCaptureResult published = new DirectFreshCaptureResult();
            yield return CaptureDirectProducerFreshFrame(pose, "published_warmup", published);
            SaveP3StageBRecord(sourceKind, candidateId, anchorControlId, stage, componentId, "discarded",
                "published_warmup", -1, requestedPosition, requestedRotation, published, recordIds);
            for (int slot = 0; slot < acceptedSlots; slot++)
            {
                DirectFreshCaptureResult accepted = new DirectFreshCaptureResult();
                yield return CaptureDirectProducerFreshFrame(pose, slot == 0 ? "candidate" : "confirmation", accepted);
                SaveP3StageBRecord(sourceKind, candidateId, anchorControlId, stage, componentId, "accepted",
                    string.Empty, slot, requestedPosition, requestedRotation, accepted, recordIds);
            }
        }

        private void SaveP3StageBRecord(string sourceKind, string candidateId, string anchorControlId,
            string stage, string componentId, string disposition, string discardReason, int acceptedSlot,
            Vector3 requestedPosition, Quaternion requestedRotation, DirectFreshCaptureResult capture,
            List<string> recordIds)
        {
            bool accepted = disposition == "accepted";
            string folder = accepted ? "images" : "discarded";
            string relative = folder + "/stage_b_" + p3StageBRecordIndex.ToString("D5", CultureInfo.InvariantCulture) + ".png";
            File.WriteAllBytes(Path.Combine(p3StageBPartialRoot, relative.Replace('/', Path.DirectorySeparatorChar)), capture.pngBytes);
            string imageSha = Sha256Bytes(capture.pngBytes);
            if (accepted && !p3StageBAcceptedHashes.Add(imageSha))
                throw new InvalidOperationException("P3 Stage B accepted image hash is not unique: " + candidateId);
            Active2DPrivilegedFrame label = BuildActive2DPrivilegedFrame(p3StageBRecordIndex, accepted ? capture.image : null);
            ScoreBreakdown score = accepted ? ScoreCandidate(capture.image, SampleAnatomyLabels(labelGridSize)) : new ScoreBreakdown();
            P3StageBRecord record = new P3StageBRecord
            {
                recordId = "p3_stage_b_record_" + p3StageBRecordIndex.ToString("D5", CultureInfo.InvariantCulture),
                sourceKind = sourceKind, candidateId = candidateId, anchorControlId = anchorControlId, stage = stage,
                componentId = componentId, captureDisposition = disposition, discardReason = discardReason,
                acceptedSlot = acceptedSlot, acquisitionOrder = p3StageBRecordIndex, unityFrameCount = capture.captureUnityFrame,
                evidenceDomain = "REAL_UNITY_PHYSICAL", synthetic = false, faultInjected = false,
                image = relative, imageSha256 = imageSha, poseEpoch = capture.poseEpoch,
                poseAppliedUnityFrame = capture.poseAppliedUnityFrame, poseAppliedRealtime = capture.poseAppliedRealtime,
                producerPoseEpoch = capture.producerPoseEpoch, producerUnityFrame = capture.producerUnityFrame,
                producerScanSerial = capture.producerScanSerial, producerSamplesPublished = capture.producerSamplesPublished,
                captureUnityFrame = capture.captureUnityFrame, captureRealtime = capture.captureRealtime,
                capturePath = capture.capturePath, capturePathMode = capture.capturePathMode,
                activeAggregatorCount = capture.activeAggregatorCount,
                requestedProbePosition = requestedPosition, requestedProbeRotation = requestedRotation,
                probePosition = label.probePosition, probeRotation = label.probeRotation,
                contactPosition = label.contactPosition, contactValid = label.contactValid,
                workspaceValid = label.workspaceValid, t7Valid = label.t7LevelAcceptable,
                nearestLevel = label.nearestLevel, t7DistanceMm = label.t7DistanceMm,
                score = score, acceptable = accepted && score.total >= p3StageBManifest.acceptableScoreFloor,
                finalTargetAcceptable = accepted && label.finalTargetAcceptable,
                evaluatorFloorAgreement = !accepted || label.finalTargetAcceptable == (score.total >= p3StageBManifest.acceptableScoreFloor)
            };
            if (accepted && (record.capturePath != "direct_producer_texture"
                || record.capturePathMode != "DIRECT_PRODUCER_CAMERA" || record.activeAggregatorCount != 0
                || record.producerPoseEpoch != record.poseEpoch
                || record.producerUnityFrame <= record.poseAppliedUnityFrame
                || record.producerSamplesPublished < 1 || record.unityFrameCount < record.producerUnityFrame))
                throw new InvalidOperationException("P3 Stage B accepted image failed direct-producer pose/frame/serial handshake");
            p3StageBRuntime.records.Add(record);
            recordIds.Add(record.recordId);
            p3StageBRecordIndex++;
            Destroy(capture.image);
        }

        private static void PopulateP3StageBCandidateResult(P3StageBCandidateResult result, R6AttemptRecord attempt)
        {
            result.attempted = true;
            result.accepted = attempt.accepted;
            result.rejectionReason = attempt.accepted ? string.Empty : (string.IsNullOrWhiteSpace(attempt.rejectionReason) ? "physical placement rejected" : attempt.rejectionReason);
            result.actualResultingPoseRecorded = true;
            result.requestedPosition = attempt.requestedPosition;
            result.requestedRotation = attempt.requestedRotation;
            result.actualPosition = attempt.resultingPosition;
            result.actualRotation = attempt.resultingRotation;
            result.contactPosition = attempt.surfacePoint;
            result.surfaceNormal = attempt.surfaceNormal;
        }

        private void ValidateP3StageBMaterializedCandidates(P3StageBRefinementSelectionManifest selection)
        {
            if (selection == null || selection.mapCandidates == null || selection.mapCandidates.Count != 108)
                throw new InvalidOperationException("P3 Stage B refinement did not materialize exactly 108 attempts");
            foreach (IGrouping<string, P3StageBCandidate> group in selection.mapCandidates.GroupBy(item => item.anchorControlId))
            {
                if (group.Count() != 27 || group.Count(item => item.role == "selected_center_repeat"
                    && !item.countsAsDistinctCell && Mathf.Abs(item.longitudinalOffsetMm) <= 1e-6f
                    && Mathf.Abs(item.lateralOffsetMm) <= 1e-6f && Mathf.Abs(item.yawDeg) <= 1e-6f) != 1)
                    throw new InvalidOperationException("P3 Stage B refinement zero-offset role/count mismatch");
            }
        }

        private void RunP3StageBMaterializer(string mode)
        {
            SaveP3StageBRuntime();
            string python = Environment.GetEnvironmentVariable("THORACIC_P3_STAGE_B_PYTHON") ?? string.Empty;
            if (!File.Exists(python)) throw new InvalidOperationException("THORACIC_P3_STAGE_B_PYTHON is missing");
            string root = Directory.GetParent(Application.dataPath).FullName;
            string script = Path.Combine(root, "Tools", "ThoracicUSCoach", "materialize_p3_stage_b_final.py");
            string runtime = Path.Combine(p3StageBPartialRoot, "stage_b_runtime_report.json");
            string manifest = Path.Combine(p3StageBPartialRoot, "stage_b_final_manifest.json");
            ProcessStartInfo start = new ProcessStartInfo
            {
                FileName = python,
                Arguments = Quote(script) + " " + mode + " --repo " + Quote(root) + " --root "
                    + Quote(p3StageBPartialRoot) + " --manifest " + Quote(manifest) + " --runtime-report " + Quote(runtime),
                WorkingDirectory = Path.Combine(root, "Tools", "ThoracicUSCoach"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (Process process = Process.Start(start))
            {
                if (process == null) throw new InvalidOperationException("failed to start Stage B materializer");
                if (!process.WaitForExit(180000)) { try { process.Kill(); } catch { } throw new TimeoutException("Stage B materializer timed out"); }
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                File.WriteAllText(Path.Combine(p3StageBPartialRoot, "materializer_" + mode + ".log"), output + Environment.NewLine + error);
                if (process.ExitCode != 0) throw new InvalidOperationException("Stage B materializer failed: " + error);
            }
        }

        private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"") + "\"";

        private void ValidateP3StageBManifest(string manifestPath, byte[] manifestBytes)
        {
            ThoracicP3StageBFinalProtocol.Validate(p3StageBManifest);
            if (p3StageBManifest.scene != SceneManager.GetActiveScene().path || p3StageBManifest.unityVersion != Application.unityVersion)
                throw new InvalidOperationException("P3 Stage B scene/Unity mismatch");
            if (HashR6CanonicalText(p3StageBManifest.scene) != ThoracicP3StageBFinalProtocol.FrozenSceneSha256
                || HashR6CanonicalText("Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.cs") != ThoracicP3StageBFinalProtocol.FrozenEvaluatorSha256)
                throw new InvalidOperationException("P3 Stage B frozen scene/evaluator mismatch");
            if ((Environment.GetEnvironmentVariable("THORACIC_P3_STAGE_B_MANIFEST_SHA256") ?? string.Empty) != Sha256Bytes(manifestBytes)
                || (Environment.GetEnvironmentVariable("THORACIC_P3_STAGE_B_EXECUTION_COMMIT") ?? string.Empty) != p3StageBManifest.sourceCommit)
                throw new InvalidOperationException("P3 Stage B execution identity mismatch");
            if (HashR6CanonicalText(p3StageBManifest.auditProtocolPath) != p3StageBManifest.auditProtocolCanonicalSha256)
                throw new InvalidOperationException("P3 Stage B frozen audit protocol hash mismatch");
            string upstreamPath = ResolveProjectPath(p3StageBManifest.upstreamStageFIndependentAuditPath);
            if (!HashR6Raw(upstreamPath).Equals(p3StageBManifest.upstreamStageFIndependentAuditSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("P3 Stage B upstream audit hash mismatch");
            P3StageBUpstreamAudit upstream = JsonUtility.FromJson<P3StageBUpstreamAudit>(File.ReadAllText(upstreamPath));
            if (upstream == null || upstream.verdict != p3StageBManifest.upstreamRequiredVerdict || !upstream.authorizedNextStage || upstream.integrityStatus != "PASS")
                throw new InvalidOperationException("P3 Stage B upstream gate is not PASS");
            if (!Path.GetFullPath(manifestPath).StartsWith(Directory.GetParent(Application.dataPath).FullName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("P3 Stage B manifest escapes worktree");
        }

        private void RequireP3StageBFrozenEvaluatorFloor()
        {
            float liveFloor = discoveryAcceptableScoreFloor;
            if (liveFloor <= 0f && TryLoadGuided2DCandidate(out CandidateRecord _, out float loadedFloor))
                liveFloor = loadedFloor;
            if (Mathf.Abs(liveFloor - ThoracicP3StageBFinalProtocol.FrozenAcceptableFloor) > 1e-6f)
                throw new InvalidOperationException(
                    "P3 Stage B live evaluator floor differs from the frozen acceptable score floor");
        }

        private void SaveP3StageBRuntime()
        {
            if (string.IsNullOrEmpty(p3StageBPartialRoot) || p3StageBRuntime == null) return;
            p3StageBRuntime.generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            File.WriteAllText(Path.Combine(p3StageBPartialRoot, "stage_b_runtime_report.json"), JsonUtility.ToJson(p3StageBRuntime, true));
        }

        private void SealP3StageBSnapshot()
        {
            string reportPath = Path.Combine(p3StageBPartialRoot, "stage_b_final_report.json");
            string coarsePath = Path.Combine(p3StageBPartialRoot, p3StageBManifest.refinement.coarseSnapshotFile);
            string selectionPath = Path.Combine(p3StageBPartialRoot, p3StageBManifest.refinement.selectionManifestFile);
            if (!File.Exists(reportPath) || !File.Exists(coarsePath) || !File.Exists(selectionPath))
                throw new InvalidOperationException("P3 Stage B required staged artifacts missing");
            string contracts = Path.Combine(p3StageBPartialRoot, "frozen_contracts");
            string sources = Path.Combine(p3StageBPartialRoot, "source");
            Directory.CreateDirectory(contracts); Directory.CreateDirectory(sources);
            File.Copy(ResolveProjectPath(p3StageBManifest.auditProtocolPath), Path.Combine(contracts, Path.GetFileName(p3StageBManifest.auditProtocolPath)), false);
            File.Copy(ResolveProjectPath(p3StageBManifest.upstreamStageFIndependentAuditPath), Path.Combine(contracts, Path.GetFileName(p3StageBManifest.upstreamStageFIndependentAuditPath)), false);
            foreach (string source in new[]
            {
                "Assets/ThoracicUSCoach/Runtime/ThoracicP3StageBFinalTypes.cs",
                "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.P3StageBFinal.cs",
                "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.DirectFreshCapture.cs",
                "Assets/ThoracicUSCoach/Editor/ThoracicP3StageBFinalBatch.cs",
                "Tools/ThoracicUSCoach/materialize_p3_stage_b_final.py",
                "Tools/ThoracicUSCoach/prepare_p3_stage_b_final_manifest.ps1",
                "Tools/ThoracicUSCoach/run_p3_stage_b_final.ps1"
            }) File.Copy(ResolveProjectPath(source), Path.Combine(sources, Path.GetFileName(source)), false);
            P3StageBEvidenceIndex index = new P3StageBEvidenceIndex { protocolId = p3StageBManifest.protocolId, sourceCommit = p3StageBManifest.sourceCommit };
            foreach (string path in Directory.EnumerateFiles(p3StageBPartialRoot, "*", SearchOption.AllDirectories)
                .Where(path => Path.GetFileName(path) != "evidence_index.json" && Path.GetFileName(path) != "snapshot_seal.json")
                .OrderBy(path => path, StringComparer.Ordinal))
            {
                FileInfo info = new FileInfo(path);
                index.files.Add(new P3StageBFileIntegrityRecord
                {
                    path = path.Substring(p3StageBPartialRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/'),
                    sha256 = HashR6Raw(path), bytes = info.Length
                });
            }
            string indexPath = Path.Combine(p3StageBPartialRoot, "evidence_index.json");
            File.WriteAllText(indexPath, JsonUtility.ToJson(index, true));
            P3StageBSnapshotSeal seal = new P3StageBSnapshotSeal
            {
                protocolId = p3StageBManifest.protocolId, sourceCommit = p3StageBManifest.sourceCommit,
                evidenceIndexSha256 = HashR6Raw(indexPath), reportSha256 = HashR6Raw(reportPath),
                manifestSha256 = HashR6Raw(Path.Combine(p3StageBPartialRoot, "stage_b_final_manifest.json")),
                coarseSnapshotSha256 = HashR6Raw(coarsePath), refinementSelectionManifestSha256 = HashR6Raw(selectionPath),
                auditProtocolCanonicalSha256 = p3StageBManifest.auditProtocolCanonicalSha256,
                auditProtocolMaterializedSha256 = HashR6Raw(ResolveProjectPath(p3StageBManifest.auditProtocolPath)),
                upstreamStageFIndependentAuditSha256 = p3StageBManifest.upstreamStageFIndependentAuditSha256,
                upstreamSnapshotSealSha256 = p3StageBManifest.upstreamSnapshotSealSha256,
                sceneCanonicalSha256 = p3StageBRuntime.sceneCanonicalSha256, sceneMaterializedSha256 = p3StageBRuntime.sceneMaterializedSha256,
                evaluatorCanonicalSha256 = p3StageBRuntime.evaluatorCanonicalSha256, evaluatorMaterializedSha256 = p3StageBRuntime.evaluatorMaterializedSha256,
                indexedFiles = index.files.Count, recordCount = p3StageBRuntime.records.Count,
                candidateResultCount = p3StageBRuntime.candidateResults.Count,
                representativeCount = p3StageBRuntime.representativeRuntime.Count,
                acceptedImageCount = p3StageBRuntime.records.Count(item => item.captureDisposition == "accepted"),
                discardedImageCount = p3StageBRuntime.records.Count(item => item.captureDisposition == "discarded"),
                complete = true, immutable = true, failureSnapshot = false
            };
            File.WriteAllText(Path.Combine(p3StageBPartialRoot, "snapshot_seal.json"), JsonUtility.ToJson(seal, true));
            Directory.Move(p3StageBPartialRoot, p3StageBFinalRoot);
        }
    }
}
