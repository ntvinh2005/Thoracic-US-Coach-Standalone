using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CSSALT.ThoracicUSCoach
{
    public sealed partial class ThoracicUSCoachController
    {
        private bool g2R4P1Complete;
        private G2R4P1Manifest g2R4P1Manifest;
        private G2R4P1UnityReport g2R4P1Report;
        private string g2R4P1OutputRoot;

        public bool G2R4P1Complete => g2R4P1Complete;
        public string G2R4P1Failure => g2R4P1Report != null ? g2R4P1Report.infrastructureFailure : string.Empty;

        public bool StartG2R4P1(string manifestProjectRelativePath)
        {
            if (!Application.isPlaying || active2DRoutine != null || active2DCaptureRoutine != null) return false;
            try
            {
                AutoWire();
                string path = ResolveProjectPath(manifestProjectRelativePath);
                g2R4P1Manifest = JsonUtility.FromJson<G2R4P1Manifest>(File.ReadAllText(path));
                ValidateG2R4P1Manifest(g2R4P1Manifest);
                g2R4P1OutputRoot = ResolveProjectPath(g2R4P1Manifest.outputDirectory);
                if (Directory.Exists(g2R4P1OutputRoot) && Directory.EnumerateFileSystemEntries(g2R4P1OutputRoot).Any())
                    throw new InvalidOperationException("G2-R4 P1 refuses to overwrite an existing capture directory");
                Directory.CreateDirectory(g2R4P1OutputRoot);
                g2R4P1Report = new G2R4P1UnityReport
                {
                    protocolId = g2R4P1Manifest.protocolId,
                    generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    executionCommit = Environment.GetEnvironmentVariable("THORACIC_G2_R4_P1_EXECUTION_COMMIT") ?? string.Empty,
                    unityVersion = Application.unityVersion,
                    scene = SceneManager.GetActiveScene().path,
                    sceneSha256 = HashG2R4P1ProjectFile(g2R4P1Manifest.scene),
                    evaluatorSha256 = HashG2R4P1ProjectFile("Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.cs"),
                    active2DControllerSha256 = HashG2R4P1ProjectFile("Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.Active2D.cs"),
                    captureControllerSha256 = HashG2R4P1ProjectFile("Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.G2R4P1.cs"),
                    manifest = ProjectRelativePath(path),
                    manifestSha256 = Sha256Bytes(File.ReadAllBytes(path)),
                    acceptableScoreFloor = g2R4P1Manifest.acceptableScoreFloor,
                    requestedCases = g2R4P1Manifest.cases.Count
                };
                g2R4P1Complete = false;
                active2DRoutine = StartCoroutine(RunG2R4P1());
                return true;
            }
            catch (Exception exception)
            {
                g2R4P1Report = g2R4P1Report ?? new G2R4P1UnityReport();
                g2R4P1Report.infrastructureFailure = exception.ToString();
                g2R4P1Complete = true;
                Debug.LogError("[ThoracicUSCoach][G2-R4-P1] " + exception);
                return false;
            }
        }

        private IEnumerator RunG2R4P1()
        {
            IEnumerator cases = RunG2R4P1Cases();
            try
            {
                while (true)
                {
                    object current;
                    try
                    {
                        if (!cases.MoveNext()) break;
                        current = cases.Current;
                    }
                    catch (Exception exception)
                    {
                        g2R4P1Report.infrastructureFailure = exception.ToString();
                        break;
                    }
                    yield return current;
                }
                g2R4P1Report.complete = string.IsNullOrEmpty(g2R4P1Report.infrastructureFailure);
            }
            finally
            {
                if (!g2R4P1Report.complete && string.IsNullOrEmpty(g2R4P1Report.infrastructureFailure))
                    g2R4P1Report.infrastructureFailure = "G2-R4 P1 coroutine ended before completion";
                SaveG2R4P1Report();
                g2R4P1Complete = true;
                active2DRoutine = null;
            }
        }

        private IEnumerator RunG2R4P1Cases()
        {
            foreach (G2R4P1Case selected in g2R4P1Manifest.cases)
            {
                G2R4P1CaseResult result = new G2R4P1CaseResult
                {
                    caseId = selected.caseId,
                    arm = selected.arm,
                    familyId = selected.familyId,
                    captureSeed = selected.captureSeed,
                    countsAsNewIndependentFamily = selected.countsAsNewIndependentFamily,
                    planningReference = selected.planningReference,
                    captureStatus = "started"
                };
                g2R4P1Report.cases.Add(result);
                yield return CaptureG2R4P1Case(selected, result);
                if (result.captureStatus == "complete") g2R4P1Report.completedCases++;
                else g2R4P1Report.failedCases++;
                if (selected.countsAsNewIndependentFamily && result.captureStatus == "complete")
                {
                    g2R4P1Report.newIndependentFamilies++;
                    if (result.positiveObservations > 0) g2R4P1Report.newFamiliesWithPositive++;
                }
                SaveG2R4P1Report();
                if (result.safetyFailures > 0)
                {
                    g2R4P1Report.infrastructureFailure = "Safety failure in " + selected.caseId;
                    yield break;
                }
            }
        }

        private IEnumerator CaptureG2R4P1Case(G2R4P1Case selected, G2R4P1CaseResult result)
        {
            ResetActive2DRunCounters();
            if (!PlaceG2R4P1Base(selected.baseStart, result))
            {
                result.captureStatus = "rejected_start";
                yield break;
            }
            result.basePlacementValid = true;
            int actionIndex = 0;
            foreach (string actionName in selected.prepositionActions)
            {
                yield return ExecuteG2R4P1Action(actionName, "preposition", actionIndex++, result);
                if (result.safetyFailures > 0 || result.rejectedActions > 0 || result.incompleteActions > 0)
                {
                    result.failureReason = "preposition action did not complete safely";
                    result.captureStatus = "rejected_start";
                    yield break;
                }
            }

            Active2DPrivilegedFrame startAudit = BuildActive2DPrivilegedFrame(-1, null);
            result.randomizedStartValid = startAudit.contactValid && startAudit.workspaceValid && startAudit.t7LevelAcceptable;
            result.targetAcceptableAtStart = false;
            if (!result.randomizedStartValid)
            {
                result.failureReason = "predeclared start failed contact/workspace/T7 validation";
                result.captureStatus = "rejected_start";
                yield break;
            }

            string finalDirectory = Path.Combine(g2R4P1OutputRoot, selected.arm, selected.caseId);
            string partialDirectory = finalDirectory + ".partial";
            if (Directory.Exists(partialDirectory) || Directory.Exists(finalDirectory))
                throw new InvalidOperationException("Refusing to overwrite case " + selected.caseId);
            Directory.CreateDirectory(partialDirectory);
            Active2DStreamManifest stream = new Active2DStreamManifest
            {
                datasetRunId = g2R4P1Manifest.datasetRunId,
                episodeId = selected.caseId,
                split = "exploratory",
                family = selected.familyId,
                seed = selected.captureSeed,
                captureStatus = "partial",
                imageSource = "primary-ultrasound-camera-1024",
                policyInputs = "clean ultrasound history plus actually executed actions"
            };
            Active2DPrivilegedAnnotations privileged = new Active2DPrivilegedAnnotations
            {
                episodeId = selected.caseId,
                workspaceOrigin = active2DWorkspaceOrigin,
                actuatorCranialAxis = -active2DCaudalAxis,
                actuatorLateralAxis = active2DLateralAxis,
                t7TargetPosition = t7RightSeed != null ? t7RightSeed.position : Vector3.zero,
                t7PreciseRadiusMm = Active2DT7PreciseRadiusMm(),
                stepMm = active2DStepMm,
                longitudinalHalfExtentMm = active2DLongitudinalHalfExtentMm,
                lateralHalfExtentMm = active2DLateralHalfExtentMm
            };

            int observationIndex = 0;
            yield return StoreG2R4P1Observation(
                partialDirectory, stream, privileged, result, observationIndex++, "RESET", true, true, true);
            result.targetAcceptableAtStart = result.observations[0].finalTargetAcceptable;
            if (selected.arm == "randomized_start" && result.targetAcceptableAtStart)
            {
                result.failureReason = "randomized-start family began on an acceptable target";
                result.captureStatus = "rejected_start";
                WriteG2R4P1CaseFiles(partialDirectory, stream, privileged, result);
                Directory.Move(partialDirectory, finalDirectory);
                yield break;
            }

            foreach (string actionName in selected.trajectoryActions)
            {
                int before = result.actions.Count;
                yield return ExecuteG2R4P1Action(actionName, "trajectory", actionIndex++, result);
                G2R4P1ActionAudit execution = result.actions[before];
                yield return StoreG2R4P1Observation(
                    partialDirectory, stream, privileged, result, observationIndex++, execution.action,
                    execution.accepted, execution.completed, false);
                if (execution.safetyFailure)
                {
                    result.failureReason = "trajectory safety failure: " + execution.rejectionReason;
                    break;
                }
            }

            stream.captureStatus = result.safetyFailures == 0 ? "complete" : "failed";
            result.captureStatus = stream.captureStatus;
            WriteG2R4P1CaseFiles(partialDirectory, stream, privileged, result);
            Directory.Move(partialDirectory, finalDirectory);
        }

        private bool PlaceG2R4P1Base(U08P1StartConfig start, G2R4P1CaseResult result)
        {
            if (start == null || probeRoot == null || faceCenter == null)
            {
                result.failureReason = "base start or probe references missing";
                return false;
            }
            probeRoot.SetPositionAndRotation(start.probePosition, start.probeRotation);
            seedProbeRotation = start.probeRotation;
            active2DCaudalAxis = -start.actuatorCranialAxis.normalized;
            active2DLateralAxis = start.actuatorLateralAxis.normalized;
            caudalTangent = active2DCaudalAxis;
            active2DLongitudinalHalfExtentMm = start.longitudinalHalfExtentMm;
            active2DLateralHalfExtentMm = start.lateralHalfExtentMm;
            active2DWorkspaceOrigin = start.workspaceOrigin;
            Physics.SyncTransforms();
            if (!EnforceRigidContact())
            {
                result.failureReason = "rigid contact could not be established";
                return false;
            }
            Physics.SyncTransforms();
            Active2DPrivilegedFrame audit = BuildActive2DPrivilegedFrame(-1, null);
            if (!audit.contactValid || !audit.workspaceValid || !audit.t7LevelAcceptable)
            {
                result.failureReason = "base start invalid: contact=" + audit.contactValid
                    + " workspace=" + audit.workspaceValid + " t7=" + audit.t7LevelAcceptable;
                return false;
            }
            return true;
        }

        private IEnumerator ExecuteG2R4P1Action(
            string actionName, string stage, int sequenceIndex, G2R4P1CaseResult result)
        {
            if (!Enum.TryParse(actionName, true, out Active2DAction action))
                throw new InvalidOperationException("Unsupported G2-R4 action " + actionName);
            bool accepted = TryActive2DAction(action);
            yield return null;
            Active2DActionExecution execution = active2DLastActionExecution;
            bool completed = accepted && IsActive2DActionExecutionComplete(execution);
            G2R4P1ActionAudit audit = new G2R4P1ActionAudit
            {
                sequenceIndex = sequenceIndex,
                stage = stage,
                action = action.ToString().ToUpperInvariant(),
                accepted = accepted,
                completed = completed,
                requestedDisplacementMm = execution != null ? execution.requestedDisplacementMm : 0f,
                attemptedDisplacementMm = execution != null ? execution.attemptedDisplacementMm : 0f,
                achievedDisplacementMm = execution != null ? execution.achievedDisplacementMm : 0f,
                requestedRotationDeg = execution != null ? execution.requestedRotationDeg : 0f,
                achievedRotationDeg = execution != null ? execution.achievedRotationDeg : 0f,
                contactValidAfterExecution = execution != null && execution.contactValidAfterExecution,
                workspaceValidAfterExecution = execution != null && execution.workspaceValidAfterExecution,
                rollbackAttempted = execution != null && execution.rollbackAttempted,
                rollbackContactRestored = execution != null && execution.rollbackContactRestored,
                safetyFailure = execution != null && execution.safetyFailure,
                rejectionReason = execution != null ? execution.rejectionReason : "missing execution record"
            };
            result.actions.Add(audit);
            if (accepted) result.acceptedActions++; else result.rejectedActions++;
            if (accepted && !completed) result.incompleteActions++;
            if (audit.safetyFailure) result.safetyFailures++;
            g2R4P1Report.acceptedActions += accepted ? 1 : 0;
            g2R4P1Report.rejectedActions += accepted ? 0 : 1;
            g2R4P1Report.incompleteActions += accepted && !completed ? 1 : 0;
            g2R4P1Report.safetyFailures += audit.safetyFailure ? 1 : 0;
        }

        private IEnumerator StoreG2R4P1Observation(
            string directory,
            Active2DStreamManifest stream,
            Active2DPrivilegedAnnotations privileged,
            G2R4P1CaseResult result,
            int index,
            string action,
            bool accepted,
            bool completed,
            bool reset)
        {
            Texture2D image = CaptureCleanImage();
            if (image == null) throw new InvalidOperationException("Clean renderer returned null for " + result.caseId);
            byte[] bytes = image.EncodeToPNG();
            string filename = "frame_" + index.ToString("D4", CultureInfo.InvariantCulture) + "_us.png";
            string absolute = Path.Combine(directory, filename);
            File.WriteAllBytes(absolute, bytes);
            string relative = ProjectRelativePath(absolute).Replace(".partial/", "/").Replace(".partial\\", "\\");
            ScoreBreakdown score = ScoreCandidate(image, SampleAnatomyLabels(labelGridSize));
            Active2DPrivilegedFrame label = BuildActive2DPrivilegedFrame(index, null);
            label.finalTargetScore = score.total;
            label.finalTargetAcceptable = score.total >= g2R4P1Manifest.acceptableScoreFloor;
            Destroy(image);
            stream.frames.Add(new Active2DStreamFrame
            {
                index = index,
                image = relative,
                imageSha256 = Sha256Bytes(bytes),
                issuedAction = action,
                actionAccepted = accepted,
                actionCompleted = completed,
                episodeReset = reset
            });
            privileged.frames.Add(label);
            float margin = score.total - g2R4P1Manifest.acceptableScoreFloor;
            G2R4P1ObservationAudit observation = new G2R4P1ObservationAudit
            {
                index = index,
                image = relative,
                imageSha256 = Sha256Bytes(bytes),
                incomingAction = action,
                actionAccepted = accepted,
                actionCompleted = completed,
                episodeReset = reset,
                probePosition = label.probePosition,
                probeRotation = label.probeRotation,
                contactPosition = label.contactPosition,
                signedDepthMm = label.signedDepthMm,
                contactValid = label.contactValid,
                workspaceValid = label.workspaceValid,
                nearestLevel = label.nearestLevel,
                t7DistanceMm = label.t7DistanceMm,
                t7LevelAcceptable = label.t7LevelAcceptable,
                score = score,
                marginToAcceptableFloor = margin,
                finalTargetAcceptable = label.finalTargetAcceptable,
                evaluatorFloorAgreement = label.finalTargetAcceptable == (score.total >= g2R4P1Manifest.acceptableScoreFloor)
            };
            result.observations.Add(observation);
            result.minimumScore = Mathf.Min(result.minimumScore, score.total);
            result.maximumScore = Mathf.Max(result.maximumScore, score.total);
            if (observation.finalTargetAcceptable) result.positiveObservations++;
            if (margin >= 0f && margin <= 0.015f) result.nearBoundaryPositiveObservations++;
            if (margin < 0f && margin >= -0.015f) result.nearBoundaryNegativeObservations++;
            if (margin < 0f && margin >= -0.05f) result.hardNegativeObservations++;
            g2R4P1Report.totalObservations++;
            if (result.arm == "hard_causal") g2R4P1Report.hardCausalObservations++;
            if (!observation.contactValid) g2R4P1Report.contactViolations++;
            if (!observation.workspaceValid) g2R4P1Report.workspaceViolations++;
            yield return null;
        }

        private static void WriteG2R4P1CaseFiles(
            string directory, Active2DStreamManifest stream,
            Active2DPrivilegedAnnotations privileged, G2R4P1CaseResult result)
        {
            File.WriteAllText(Path.Combine(directory, "stream_manifest.json"), JsonUtility.ToJson(stream, true));
            File.WriteAllText(Path.Combine(directory, "privileged_annotations.json"), JsonUtility.ToJson(privileged, true));
            File.WriteAllText(Path.Combine(directory, "case_audit.json"), JsonUtility.ToJson(result, true));
        }

        private void ValidateG2R4P1Manifest(G2R4P1Manifest manifest)
        {
            if (manifest == null || manifest.cases == null)
                throw new InvalidOperationException("G2-R4 P1 manifest missing");
            if (manifest.scene != SceneManager.GetActiveScene().path || manifest.unityVersion != Application.unityVersion)
                throw new InvalidOperationException("G2-R4 P1 scene or Unity version mismatch");
            if (Mathf.Abs(manifest.acceptableScoreFloor - 0.5912307500839233f) > 1e-6f)
                throw new InvalidOperationException("Frozen acceptable score floor changed");
            if (manifest.cases.Select(item => item.caseId).Distinct().Count() != manifest.cases.Count
                || manifest.cases.Select(item => item.familyId).Distinct().Count() != manifest.cases.Count)
                throw new InvalidOperationException("G2-R4 P1 case/family IDs must be unique");
            G2R4P1Case[] independent = manifest.cases.Where(item => item.countsAsNewIndependentFamily).ToArray();
            bool hardSupplement = manifest.protocolId.EndsWith("HARD_SUPPLEMENT_V1", StringComparison.Ordinal);
            if ((!hardSupplement && independent.Length < 1) || independent.Length > manifest.maxIndependentFamilies)
                throw new InvalidOperationException("Independent family budget exceeded");
            if (independent.Count(item => item.arm == "randomized_start") > 8
                || independent.Count(item => item.arm == "target_enriched") > 8
                || independent.Any(item => item.arm != "randomized_start" && item.arm != "target_enriched"))
                throw new InvalidOperationException("Capture arm budget or identity invalid");
            if (independent.Any(item => item.trajectoryActions.Count + 1 > manifest.maxObservationsPerIndependentFamily))
                throw new InvalidOperationException("Per-family image budget exceeded");
            int hard = manifest.cases.Where(item => item.arm == "hard_causal")
                .Sum(item => item.trajectoryActions.Count + 1);
            if (hard < manifest.minHardCausalObservations || hard > manifest.maxHardCausalObservations)
                throw new InvalidOperationException("Hard causal observation budget invalid: " + hard);
            if (!hardSupplement
                && (!manifest.cases.Any(item => item.arm == "hard_causal" && item.planningReference.Contains("0090"))
                || !manifest.cases.Any(item => item.arm == "hard_causal" && item.planningReference.Contains("0066"))))
                throw new InvalidOperationException("Mandatory hard families 0090/0066 missing");
            if (independent.Any(item => manifest.historicallyLockedFamilies.Contains(item.familyId)))
                throw new InvalidOperationException("Historically locked family reused");
            foreach (G2R4P1Case item in manifest.cases)
            {
                if (item.baseStart == null || string.IsNullOrEmpty(item.familyId))
                    throw new InvalidOperationException("Malformed capture case " + item.caseId);
                foreach (string action in item.prepositionActions.Concat(item.trajectoryActions))
                {
                    if (!Enum.TryParse(action, true, out Active2DAction parsed)
                        || parsed == Active2DAction.Stop || parsed == Active2DAction.PhaseHandoff)
                        throw new InvalidOperationException("Forbidden capture action " + action);
                }
            }
            ResolveProjectPath(manifest.outputDirectory);
        }

        private string HashG2R4P1ProjectFile(string relativePath)
        {
            return Sha256Bytes(File.ReadAllBytes(ResolveProjectPath(relativePath)));
        }

        private void SaveG2R4P1Report()
        {
            if (g2R4P1Report == null || string.IsNullOrEmpty(g2R4P1OutputRoot)) return;
            g2R4P1Report.generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            string path = Path.Combine(g2R4P1OutputRoot, "g2_r4_p1_unity_report.json");
            File.WriteAllText(path, JsonUtility.ToJson(g2R4P1Report, true));
        }
    }
}
