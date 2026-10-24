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
        private bool u08P1EvaluationRunning;
        private bool u08P1EvaluationComplete;
        private U08P1Manifest u08P1Manifest;
        private U08P1UnityReport u08P1Report;
        private U08P1StartAudit u08P1CurrentStartAudit;
        private string u08P1Policy;
        private string u08P1ManifestRelativePath;
        private string u08P1OutputAbsolutePath;

        public bool U08P1EvaluationComplete => u08P1EvaluationComplete;
        public string U08P1InfrastructureFailure => u08P1Report != null
            ? u08P1Report.infrastructureFailure : string.Empty;

        public bool StartU08P1Evaluation(string manifestProjectRelativePath, string policy)
        {
            if (!Application.isPlaying || active2DRoutine != null || active2DCaptureRoutine != null)
                return false;
            try
            {
                AutoWire();
                string absoluteManifest = ResolveProjectPath(manifestProjectRelativePath);
                U08P1Manifest parsed = JsonUtility.FromJson<U08P1Manifest>(File.ReadAllText(absoluteManifest));
                ValidateU08P1Manifest(parsed, policy);
                u08P1Manifest = parsed;
                u08P1Policy = policy;
                u08P1ManifestRelativePath = ProjectRelativePath(absoluteManifest);
                u08P1OutputAbsolutePath = ResolveProjectPath(parsed.outputDirectory);
                Directory.CreateDirectory(u08P1OutputAbsolutePath);
                u08P1Report = new U08P1UnityReport
                {
                    protocolId = parsed.protocolId,
                    generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    unityVersion = Application.unityVersion,
                    scene = SceneManager.GetActiveScene().path,
                    executionCommit = Environment.GetEnvironmentVariable("THORACIC_U08_P1_EXECUTION_COMMIT") ?? string.Empty,
                    policy = policy,
                    manifest = u08P1ManifestRelativePath,
                    manifestSha256 = Sha256Bytes(File.ReadAllBytes(absoluteManifest)),
                    phaseOneCheckpointSha256 = parsed.phaseOneCheckpointSha256,
                    stopCheckpointSha256 = parsed.stopCheckpointSha256,
                    actionCheckpointSha256 = parsed.actionCheckpointSha256,
                    policyServerSha256 = parsed.policyServerSha256,
                    stopThreshold = parsed.stopThreshold,
                    stepBudget = parsed.stepBudget
                };
                u08P1EvaluationComplete = false;
                u08P1EvaluationRunning = true;
                active2DRoutine = StartCoroutine(RunU08P1Evaluation());
                return true;
            }
            catch (Exception exception) when (exception is IOException || exception is ArgumentException || exception is InvalidOperationException)
            {
                u08P1Report = u08P1Report ?? new U08P1UnityReport();
                u08P1Report.infrastructureFailure = exception.Message;
                u08P1EvaluationRunning = false;
                u08P1EvaluationComplete = true;
                Debug.LogError("[ThoracicUSCoach][U08-P1] " + exception);
                return false;
            }
        }

        private IEnumerator RunU08P1Evaluation()
        {
            bool previousPhaseOneOnly = active2DPhaseOneOnly;
            int previousBudget = active2DLiveStepBudget;
            active2DPhaseOneOnly = false;
            active2DLiveStepBudget = u08P1Manifest.stepBudget;
            try
            {
                foreach (U08P1StartConfig start in u08P1Manifest.starts.OrderBy(item => item.order))
                {
                    u08P1CurrentStartAudit = null;
                    yield return RunActive2DImageOnlyCore(null, start, u08P1Policy);
                    U08P1EpisodeOutcome outcome = BuildU08P1Outcome(start);
                    u08P1Report.episodes.Add(outcome);
                    SaveU08P1Report(false);
                    if (!outcome.start.valid)
                    {
                        u08P1Report.infrastructureFailure = "Frozen start " + start.startId
                            + " failed equality/safety validation: " + outcome.start.failureReason;
                        yield break;
                    }
                    yield return null;
                }
                if (u08P1Report.episodes.Count != 2)
                {
                    u08P1Report.infrastructureFailure = "U08-P1 policy session did not execute exactly two episodes";
                    yield break;
                }
                u08P1Report.complete = true;
                SaveU08P1Report(true);
            }
            finally
            {
                if (!u08P1Report.complete && string.IsNullOrEmpty(u08P1Report.infrastructureFailure))
                    u08P1Report.infrastructureFailure = "U08-P1 coroutine ended before two frozen starts completed";
                if (!u08P1Report.complete) SaveU08P1Report(false);
                active2DPhaseOneOnly = previousPhaseOneOnly;
                active2DLiveStepBudget = previousBudget;
                u08P1EvaluationRunning = false;
                u08P1EvaluationComplete = true;
                active2DRoutine = null;
            }
        }

        private bool TryPlaceU08P1Start(U08P1StartConfig start, out string description)
        {
            description = string.Empty;
            if (probeRoot == null || faceCenter == null) return false;
            probeRoot.SetPositionAndRotation(start.probePosition, start.probeRotation);
            seedProbeRotation = start.probeRotation;
            active2DCaudalAxis = -start.actuatorCranialAxis.normalized;
            active2DLateralAxis = start.actuatorLateralAxis.normalized;
            caudalTangent = active2DCaudalAxis;
            active2DLongitudinalHalfExtentMm = start.longitudinalHalfExtentMm;
            active2DLateralHalfExtentMm = start.lateralHalfExtentMm;
            if (!EnforceRigidContact()) return false;
            active2DWorkspaceOrigin = start.workspaceOrigin;
            description = "U08-P1 frozen evaluator-owned Phase-1 handoff snapshot " + start.startId
                + "; actor receives image and execution feedback only";
            return true;
        }

        private bool AuditU08P1Start(U08P1StartConfig start)
        {
            Active2DPrivilegedFrame frame = BuildActive2DPrivilegedFrame(-1, null);
            float positionError = Vector3.Distance(start.probePosition, probeRoot.position);
            float rotationError = Quaternion.Angle(start.probeRotation, probeRoot.rotation);
            bool equality = positionError <= 0.05f && rotationError <= 0.05f;
            bool valid = equality && frame.contactValid && frame.workspaceValid
                && frame.t7LevelAcceptable && !frame.finalTargetAcceptable;
            string failure = string.Empty;
            if (!equality) failure += "pose mismatch; ";
            if (!frame.contactValid) failure += "contact invalid; ";
            if (!frame.workspaceValid) failure += "workspace invalid; ";
            if (!frame.t7LevelAcceptable) failure += "not a valid T7 handoff; ";
            if (frame.finalTargetAcceptable) failure += "target already acceptable at start; ";
            u08P1CurrentStartAudit = new U08P1StartAudit
            {
                startId = start.startId,
                expectedConfigSha256 = start.configSha256,
                requestedProbePosition = start.probePosition,
                requestedProbeRotation = start.probeRotation,
                actualProbePosition = probeRoot.position,
                actualProbeRotation = probeRoot.rotation,
                positionErrorMm = positionError,
                rotationErrorDeg = rotationError,
                contactValid = frame.contactValid,
                workspaceValid = frame.workspaceValid,
                t7LevelAcceptable = frame.t7LevelAcceptable,
                targetInitiallyAcceptable = frame.finalTargetAcceptable,
                t7DistanceMm = frame.t7DistanceMm,
                initialTargetScore = frame.finalTargetScore,
                valid = valid,
                failureReason = failure.Trim()
            };
            return valid;
        }

        private U08P1EpisodeOutcome BuildU08P1Outcome(U08P1StartConfig start)
        {
            U08P1EpisodeOutcome outcome = new U08P1EpisodeOutcome
            {
                startId = start.startId,
                policy = u08P1Policy,
                seed = start.seed,
                episodeId = active2DEpisodeId,
                start = u08P1CurrentStartAudit ?? new U08P1StartAudit
                {
                    startId = start.startId,
                    failureReason = "start audit missing"
                },
                rawResult = ProjectRelativePath(Path.Combine(ArtifactRoot(), "active2d", "runs", active2DEpisodeId + ".json")),
                privilegedTrace = ProjectRelativePath(Path.Combine(ArtifactRoot(), "active2d", "runs_privileged", active2DEpisodeId + ".json"))
            };
            if (active2DResult == null)
            {
                outcome.failureReason = "active2D result missing";
                return outcome;
            }
            outcome.completed = active2DResult.finalPhase == Active2DPhase.FinalSuccess.ToString()
                || active2DResult.finalPhase == Active2DPhase.FinalFailure.ToString();
            outcome.explicitStop = active2DResult.explicitStop;
            outcome.acquisitionSuccess = active2DResult.overallPass;
            outcome.wrongStop = active2DResult.explicitStop && !active2DResult.finalTargetPass;
            outcome.phaseTwoSteps = active2DResult.trace.Count;
            outcome.acceptedMovements = active2DResult.trace.Count(item => item.accepted
                && item.action != "STOP" && item.action != "PHASEHANDOFF");
            outcome.rejectedMovements = active2DResult.rejectedMovements;
            outcome.candidateChoices = active2DResult.trace.Count(item => item.candidateSelected);
            outcome.fallbacks = active2DResult.trace.Count(item => item.candidateFallback);
            outcome.returnToBestActions = active2DResult.trace.Count(item =>
                item.candidateFallbackReason == "frozen_return_to_best");
            outcome.safetyFailures = active2DResult.trace.Count(item => item.safetyFailure);
            outcome.contactViolations = active2DResult.contactViolations;
            outcome.workspaceViolations = active2DResult.workspaceViolations;
            outcome.finalContactValid = active2DResult.finalContactValid;
            outcome.finalWorkspaceValid = active2DResult.finalWorkspaceValid;
            outcome.failureReason = active2DResult.failureReason;
            if (active2DLivePrivilegedAudit != null)
            {
                Active2DPrivilegedFrame first = active2DLivePrivilegedAudit.frames
                    .Where(item => item.index >= 0 && item.finalTargetAcceptable)
                    .OrderBy(item => item.index).FirstOrDefault();
                outcome.targetObserved = first != null;
                outcome.firstTargetObservationStep = first != null ? first.index : -1;
            }
            return outcome;
        }

        private void ValidateU08P1Manifest(U08P1Manifest manifest, string policy)
        {
            if (manifest == null) throw new InvalidOperationException("U08-P1 manifest is empty");
            if (policy != "raster" && policy != "candidate")
                throw new InvalidOperationException("U08-P1 policy must be raster or candidate");
            if (manifest.requiredStarts != 2 || manifest.starts == null || manifest.starts.Count != 2
                || manifest.starts.Select(item => item.startId).Distinct().Count() != 2
                || manifest.starts.Select(item => item.seed).Distinct().Count() != 2)
                throw new InvalidOperationException("U08-P1 requires exactly two distinct frozen starts");
            if (manifest.stepBudget != 300 || Mathf.Abs(manifest.stopThreshold - 0.93f) > 0.000001f)
                throw new InvalidOperationException("U08-P1 requires budget 300 and frozen STOP threshold 0.93");
            if (manifest.unityVersion != Application.unityVersion
                || manifest.scene != SceneManager.GetActiveScene().path)
                throw new InvalidOperationException("U08-P1 Unity version or scene mismatch");
            VerifyU08P1Hash(manifest.phaseOneCheckpoint, manifest.phaseOneCheckpointSha256, "Phase 1 checkpoint");
            VerifyU08P1Hash(manifest.stopCheckpoint, manifest.stopCheckpointSha256, "STOP checkpoint");
            VerifyU08P1Hash(manifest.actionCheckpoint, manifest.actionCheckpointSha256, "U08 action checkpoint");
            VerifyU08P1Hash(manifest.policyServer, manifest.policyServerSha256, "policy server");
            ResolveProjectPath(manifest.outputDirectory);
            for (int i = 0; i < manifest.starts.Count; i++)
            {
                U08P1StartConfig start = manifest.starts[i];
                if (start.order != i || start.configSha256.Length != 64
                    || start.streamManifestSha256.Length != 64
                    || start.privilegedAnnotationsSha256.Length != 64
                    || start.startAuditSha256.Length != 64)
                    throw new InvalidOperationException("U08-P1 frozen start provenance is invalid at index " + i);
            }
        }

        private void VerifyU08P1Hash(string path, string expected, string label)
        {
            string actual = Sha256Bytes(File.ReadAllBytes(ResolveProjectPath(path)));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(label + " hash mismatch");
        }

        private string U08P1EpisodeId(U08P1StartConfig start, string policy)
        {
            return "U08P1_" + start.startId + "_" + policy.ToUpperInvariant();
        }

        private void SaveU08P1Report(bool final)
        {
            if (u08P1Report == null || string.IsNullOrEmpty(u08P1OutputAbsolutePath)) return;
            u08P1Report.generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            string path = Path.Combine(u08P1OutputAbsolutePath, "u08_p1_" + u08P1Policy + "_unity.json");
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonUtility.ToJson(u08P1Report, true));
            if (File.Exists(path)) File.Replace(temporary, path, null, true);
            else File.Move(temporary, path);
            if (final) Debug.Log("[ThoracicUSCoach][U08-P1] report=" + ProjectRelativePath(path));
        }
    }
}
