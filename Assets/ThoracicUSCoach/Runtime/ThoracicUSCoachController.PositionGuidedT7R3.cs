using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SMMARTS;
using UnityEngine;

namespace CSSALT.ThoracicUSCoach
{
    public sealed partial class ThoracicUSCoachController
    {
        private const string P1R3OutputDirectory =
            "Artifacts/ThoracicUSCoach/position_guided_t7/P1_R3_RECOVERY_COMPLETION";
        private const string P1R3RegistryFile = "development_run_registry.json";
        private const string P1R3FrozenFile = "frozen_configuration.json";
        private const float P1R3CoarseStepMm = 3f;
        private const float P1R3FineStepMm = 0.5f;
        private const float P1R3PoseToleranceMm = 0.25f;
        private const float P1R3OrientationToleranceDeg = 0.5f;
        private const float P1R3MinimumImprovementMm = 0.05f;
        private const int P1R3CoarseBudget = 16;
        private const int P1R3FineBudget = 8;
        private const int P1R3MaximumDevelopmentRuns = 20;
        private const int P1R3MaximumConfirmationRuns = 3;

        [Serializable]
        private sealed class P1R3Registry
        {
            public string contract = "thoracic_p1_r3_registry_v1";
            public string state = "NOT_STARTED";
            public int maximumDevelopmentRuns = P1R3MaximumDevelopmentRuns;
            public int maximumConfirmationRuns = P1R3MaximumConfirmationRuns;
            public List<P1R3RegistryEntry> developmentRuns = new List<P1R3RegistryEntry>();
            public List<P1R3RegistryEntry> confirmationRuns = new List<P1R3RegistryEntry>();
        }

        [Serializable]
        private sealed class P1R3RegistryEntry
        {
            public string runId = string.Empty;
            public string kind = string.Empty;
            public string scenario = string.Empty;
            public string directory = string.Empty;
            public string status = "RUNNING";
            public string startedUtc = string.Empty;
            public string completedUtc = string.Empty;
            public string failure = string.Empty;
            public bool physicalRunConsumed = true;
        }

        [Serializable]
        private sealed class P1R3Calibration
        {
            public string contract = "thoracic_p1_r3_calibration_v1";
            public string runId = string.Empty;
            public string calibrationBoundary =
                "T7 reference is read once for development calibration. Runtime navigation uses only this stored robot pose.";
            public P1RobotPose targetPose = new P1RobotPose();
            public Vector3 corridorAxis;
            public Vector3 lateralAxis;
            public Vector3 targetSurfaceNormal;
            public string referenceImage = string.Empty;
            public string referenceImageSha256 = string.Empty;
            public float originalSsimThreshold;
            public long poseEpoch = -1;
            public int poseAppliedUnityFrame = -1;
            public long producerPoseEpoch = -1;
            public int producerUnityFrame = -1;
            public long producerScanSerial = -1;
            public int captureUnityFrame = -1;
            public bool fresh;
            public bool provenanceMatched;
        }

        [Serializable]
        private sealed class P1R3FrozenConfiguration
        {
            public string contract = "thoracic_p1_r3_frozen_configuration_v1";
            public bool frozen;
            public string sourceRunId = string.Empty;
            public P1RobotPose targetPose = new P1RobotPose();
            public Vector3 corridorAxis;
            public Vector3 lateralAxis;
            public Vector3 targetSurfaceNormal;
            public string referenceImage = string.Empty;
            public string referenceImageSha256 = string.Empty;
            public float originalSsimThreshold;
            public float poseToleranceMm;
            public float orientationToleranceDeg;
            public float coarseStepMm;
            public float fineStepMm;
            public float minimumImprovementMm;
            public int coarseBudget;
            public int fineBudget;
            public float[] confirmationStartOffsetsMm = Array.Empty<float>();
        }

        [Serializable]
        private sealed class P1R3Step
        {
            public int step;
            public string stage = string.Empty;
            public string action = string.Empty;
            public bool accepted;
            public float requestedDisplacementMm;
            public float achievedDisplacementMm;
            public P1RobotPose measuredPose = new P1RobotPose();
            public float longitudinalErrorMm;
            public float lateralErrorMm;
            public float fullContactPointErrorMm;
            public float orientationErrorDeg;
            public bool contactValid;
            public bool workspaceValid;
            public bool rollbackAttempted;
            public bool rollbackContactRestored;
            public bool safetyFailure;
            public string rejectionReason = string.Empty;
            public string image = string.Empty;
            public string imageSha256 = string.Empty;
            public bool frameIntegrityValid;
            public long poseEpoch = -1;
            public int poseAppliedUnityFrame = -1;
            public long producerPoseEpoch = -1;
            public int producerUnityFrame = -1;
            public long producerScanSerial = -1;
            public int captureUnityFrame = -1;
        }

        [Serializable]
        private sealed class P1R3Trajectory
        {
            public string contract = "thoracic_p1_r3_actor_trajectory_v1";
            public string runId = string.Empty;
            public string kind = string.Empty;
            public string scenario = string.Empty;
            public string mode = "CALIBRATED_POSITION_GUIDED_BASELINE_P1_R3";
            public string allowedInputs =
                "Stored calibrated target, measured robot pose, executed action outcome, contact/workspace feedback, and fresh ultrasound confirmation.";
            public string restrictedInputs =
                "No runtime T7 marker, evaluator distance, target score, anatomy label, mesh identity, or renderer provenance enters navigation decisions.";
            public float startOffsetMm;
            public P1RobotPose targetPose = new P1RobotPose();
            public P1RobotPose startPose = new P1RobotPose();
            public Vector3 corridorAxis;
            public Vector3 lateralAxis;
            public float poseToleranceMm = P1R3PoseToleranceMm;
            public float orientationToleranceDeg = P1R3OrientationToleranceDeg;
            public float coarseStepMm = P1R3CoarseStepMm;
            public float fineStepMm = P1R3FineStepMm;
            public int acceptedCoarseMovements;
            public int acceptedFineMovements;
            public int rejectedMovements;
            public float cumulativeMovementMm;
            public string status = "RUNNING";
            public string stopReason = string.Empty;
            public List<P1R3Step> steps = new List<P1R3Step>();
        }

        [Serializable]
        private sealed class P1R3PrivilegedStep
        {
            public int step;
            public float distanceToT7ReferenceMm;
            public float achievedSurfaceYawDeg;
            public float surfaceYawErrorDeg;
            public float normalAlignmentErrorDeg;
            public float signedContactDepthMm;
            public float contactFraction;
            public string provenance = string.Empty;
            public bool provenanceMatched;
        }

        [Serializable]
        private sealed class P1R3PrivilegedTrajectory
        {
            public string contract = "thoracic_p1_r3_evaluator_trajectory_v1";
            public string warning = "EVALUATOR ONLY. Never supplied to navigation decisions.";
            public string runId = string.Empty;
            public Vector3 t7ReferenceContactPoint;
            public List<P1R3PrivilegedStep> steps = new List<P1R3PrivilegedStep>();
        }

        private Coroutine p1R3Routine;
        private bool p1R3Complete;
        private string p1R3Failure = string.Empty;
        private string p1R3Status = "idle";
        private string p1R3CurrentRunId = string.Empty;
        private bool p1R3CurrentConfirmation;

        public bool P1R3Complete => p1R3Complete;
        public string P1R3Failure => p1R3Failure;
        public string P1R3Status => p1R3Status;
        public string P1R3OutputRoot => P1R3OutputDirectory;

        public bool StartP1R3DevelopmentRun()
        {
            return StartP1R3Run(false);
        }

        public bool StartP1R3ConfirmationRun()
        {
            return StartP1R3Run(true);
        }

        private bool StartP1R3Run(bool confirmation)
        {
            if (!Application.isPlaying || p1R3Routine != null) return false;
            p1R3Complete = false;
            p1R3Failure = string.Empty;
            p1R3Status = "starting";
            p1R3Routine = StartCoroutine(RunP1R3Envelope(confirmation));
            return true;
        }

        private IEnumerator RunP1R3Envelope(bool confirmation)
        {
            Stack<IEnumerator> stack = new Stack<IEnumerator>();
            stack.Push(RunP1R3Core(confirmation));
            Exception failure = null;
            while (stack.Count > 0)
            {
                IEnumerator current = stack.Peek();
                bool advanced;
                object yielded = null;
                try
                {
                    advanced = current.MoveNext();
                    if (advanced) yielded = current.Current;
                }
                catch (Exception exception)
                {
                    failure = exception;
                    break;
                }
                if (!advanced)
                {
                    stack.Pop();
                    (current as IDisposable)?.Dispose();
                }
                else if (yielded is IEnumerator nested) stack.Push(nested);
                else yield return yielded;
            }
            if (failure != null)
            {
                while (stack.Count > 0)
                {
                    try { (stack.Pop() as IDisposable)?.Dispose(); }
                    catch (Exception) { }
                }
                p1R3Failure = failure.ToString();
                p1R3Status = "BLOCKED";
                MarkP1R3RegistryFailure(p1R3CurrentRunId, p1R3CurrentConfirmation,
                    failure.Message);
                Debug.LogError("[ThoracicUSCoach][P1R3] " + failure);
            }
            p1R3Routine = null;
            p1R3Complete = true;
        }

        private IEnumerator RunP1R3Core(bool confirmation)
        {
            AutoWire();
            Vector3 originalPosition = probeRoot != null ? probeRoot.position : Vector3.zero;
            Quaternion originalRotation = probeRoot != null ? probeRoot.rotation : Quaternion.identity;
            float originalStep = active2DStepMm;
            float originalLongitudinalExtent = active2DLongitudinalHalfExtentMm;
            float originalLateralExtent = active2DLateralHalfExtentMm;
            float originalSweepYaw = sweepCountYawDeg;
            Vector3 originalWorkspaceOrigin = active2DWorkspaceOrigin;
            Vector3 originalCaudalAxis = active2DCaudalAxis;
            Vector3 originalLateralAxis = active2DLateralAxis;
            try
            {
                if (!ValidateReferences(out string referenceError))
                    throw new InvalidOperationException(referenceError);
                if (probeRoot == null || faceCenter == null || rearCenter == null
                    || skinCollider == null || skinIntersection == null)
                    throw new InvalidOperationException("P1-R3 probe/contact references are incomplete");
                Directory.CreateDirectory(P1R3OutputDirectory);
                P1R3Registry registry = LoadP1R3Registry();
                P1R3RegistryEntry entry = BeginP1R3RegistryEntry(registry, confirmation);
                p1R3CurrentRunId = entry.runId;
                p1R3CurrentConfirmation = confirmation;
                string runRoot = Path.Combine(P1R3OutputDirectory, entry.directory);
                string frameRoot = Path.Combine(runRoot, "frames");
                string evaluatorRoot = Path.Combine(runRoot, "evaluator_only");
                string provenanceRoot = Path.Combine(evaluatorRoot, "provenance");
                Directory.CreateDirectory(frameRoot);
                Directory.CreateDirectory(provenanceRoot);

                SweepCountReferenceSet references = BuildSweepCountReferences();
                SweepCountReference t7Reference = references.references.Single(item => item.level == "T7");
                if (!UltrasoundManager.TryGet(UltrasoundManager.Plane.Primary,
                        out UltrasoundManager manager) || manager == null)
                    throw new InvalidOperationException("P1-R3 primary ultrasound manager is unavailable");
                P1R3Calibration calibration;
                if (confirmation)
                {
                    calibration = LoadP1R3FrozenCalibration();
                }
                else
                {
                    calibration = new P1R3Calibration
                    {
                        runId = entry.runId,
                        originalSsimThreshold = LoadP1R3OriginalSsimThreshold(),
                        targetSurfaceNormal = t7Reference.surfaceNormal
                    };
                    Vector3 corridorAxis = (references.references[6].contactPoint
                        - references.references[0].contactPoint).normalized;
                    if (!PlaceSweepCountProbe(t7Reference.contactPoint,
                            t7Reference.surfaceNormal, corridorAxis, out string placementFailure))
                        throw new InvalidOperationException(
                            "P1-R3 development calibration placement failed: " + placementFailure);
                    active2DCaudalAxis = Vector3.ProjectOnPlane(
                        corridorAxis, CurrentSurfaceNormal()).normalized;
                    active2DLateralAxis = Vector3.Cross(
                        CurrentSurfaceNormal(), active2DCaudalAxis).normalized;
                    caudalTangent = active2DCaudalAxis;
                    calibration.corridorAxis = active2DCaudalAxis;
                    calibration.lateralAxis = active2DLateralAxis;
                    calibration.targetPose = MeasureP1RobotPose();
                    DirectFreshCaptureResult calibrationCapture = new DirectFreshCaptureResult();
                    yield return CaptureP1R3Frame("calibration_" + entry.runId,
                        calibrationCapture);
                    SaveP1R3Capture(frameRoot, "calibration.png", calibrationCapture);
                    calibration.referenceImage = entry.directory + "/frames/calibration.png";
                    calibration.referenceImageSha256 = Sha256Bytes(calibrationCapture.pngBytes);
                    calibration.poseEpoch = calibrationCapture.poseEpoch;
                    calibration.poseAppliedUnityFrame = calibrationCapture.poseAppliedUnityFrame;
                    calibration.producerPoseEpoch = calibrationCapture.producerPoseEpoch;
                    calibration.producerUnityFrame = calibrationCapture.producerUnityFrame;
                    calibration.producerScanSerial = calibrationCapture.producerScanSerial;
                    calibration.captureUnityFrame = calibrationCapture.captureUnityFrame;
                    calibration.fresh = P1CaptureFresh(calibrationCapture);
                    calibration.provenanceMatched = WriteP1R3Provenance(manager,
                        calibrationCapture, provenanceRoot, "calibration.json", out _);
                    if (!calibration.fresh || !calibration.provenanceMatched)
                        throw new InvalidOperationException("P1-R3 calibration frame is not fresh/provenance-matched");
                    WriteSweepCountJson(Path.Combine(runRoot, "calibration_manifest.json"), calibration);
                }

                active2DCaudalAxis = calibration.corridorAxis.normalized;
                active2DLateralAxis = calibration.lateralAxis.normalized;
                caudalTangent = active2DCaudalAxis;
                active2DWorkspaceOrigin = calibration.targetPose.contactPointMm;
                active2DLongitudinalHalfExtentMm = originalLongitudinalExtent;
                active2DLateralHalfExtentMm = originalLateralExtent;
                sweepCountYawDeg = 0f;
                active2DRejectedMoves = 0;
                active2DContactViolations = 0;
                active2DWorkspaceViolations = 0;

                P1R3Trajectory actor = new P1R3Trajectory
                {
                    runId = entry.runId,
                    kind = entry.kind,
                    scenario = entry.scenario,
                    targetPose = calibration.targetPose,
                    corridorAxis = calibration.corridorAxis,
                    lateralAxis = calibration.lateralAxis
                };
                P1R3PrivilegedTrajectory evaluator = new P1R3PrivilegedTrajectory
                {
                    runId = entry.runId,
                    t7ReferenceContactPoint = t7Reference.contactPoint
                };

                if (!confirmation && entry.scenario == "FINE_MOTION_SENSITIVITY")
                {
                    actor.startPose = MeasureP1RobotPose();
                    yield return RunP1R3Sensitivity(actor, evaluator, calibration,
                        t7Reference, frameRoot, provenanceRoot, manager);
                }
                else
                {
                    float startOffset = confirmation
                        ? P1R3ConfirmationOffset(registry.confirmationRuns.Count - 1)
                        : P1R3DevelopmentOffset(registry.developmentRuns.Count - 1);
                    actor.startOffsetMm = startOffset;
                    Vector3 proposedStart = calibration.targetPose.contactPointMm
                        + calibration.corridorAxis.normalized * startOffset;
                    if (!FindSurfaceAt(proposedStart, calibration.targetSurfaceNormal,
                            out RaycastHit startHit))
                        throw new InvalidOperationException("P1-R3 start surface projection failed");
                    if (!PlaceSweepCountProbe(startHit.point, startHit.normal,
                            calibration.corridorAxis, out string startFailure))
                        throw new InvalidOperationException("P1-R3 start placement failed: " + startFailure);
                    active2DCaudalAxis = Vector3.ProjectOnPlane(
                        calibration.corridorAxis, CurrentSurfaceNormal()).normalized;
                    active2DLateralAxis = Vector3.Cross(
                        CurrentSurfaceNormal(), active2DCaudalAxis).normalized;
                    caudalTangent = active2DCaudalAxis;
                    actor.startPose = MeasureP1RobotPose();
                    DirectFreshCaptureResult startCapture = new DirectFreshCaptureResult();
                    yield return CaptureP1R3Frame("start_" + entry.runId, startCapture);
                    SaveP1R3Capture(frameRoot, "start.png", startCapture);
                    RecordP1R3Step(actor, evaluator, calibration, t7Reference, 0,
                        "START", Active2DAction.Observe, true, startCapture,
                        entry.directory + "/frames/start.png", provenanceRoot, manager);
                    yield return RunP1R3Navigation(actor, evaluator, calibration,
                        t7Reference, provenanceRoot, manager);
                    if (actor.status != "POSITION_PRECISION_PASS")
                        throw new InvalidOperationException(actor.stopReason);
                    DirectFreshCaptureResult returnCapture = new DirectFreshCaptureResult();
                    yield return CaptureP1R3Frame("return_" + entry.runId, returnCapture);
                    SaveP1R3Capture(frameRoot, "return.png", returnCapture);
                    RecordP1R3Step(actor, evaluator, calibration, t7Reference,
                        actor.steps.Count, "CONFIRMATION", Active2DAction.Stop, true,
                        returnCapture, entry.directory + "/frames/return.png",
                        provenanceRoot, manager);
                    actor.status = "EXECUTED_PENDING_OFFLINE_EVALUATION";
                    actor.stopReason = "pose precision and fresh return capture completed";
                }

                WriteSweepCountJson(Path.Combine(runRoot, "trajectory_actor.json"), actor);
                WriteSweepCountJson(Path.Combine(evaluatorRoot, "trajectory_privileged.json"), evaluator);
                CompleteP1R3RegistryEntry(entry.runId, confirmation);
                p1R3Status = "COMPLETE_PENDING_OFFLINE_ANALYSIS";
            }
            finally
            {
                active2DStepMm = originalStep;
                active2DLongitudinalHalfExtentMm = originalLongitudinalExtent;
                active2DLateralHalfExtentMm = originalLateralExtent;
                sweepCountYawDeg = originalSweepYaw;
                active2DWorkspaceOrigin = originalWorkspaceOrigin;
                active2DCaudalAxis = originalCaudalAxis;
                active2DLateralAxis = originalLateralAxis;
                if (probeRoot != null && skinCollider != null)
                {
                    probeRoot.SetPositionAndRotation(originalPosition, originalRotation);
                    EnforceRigidContact();
                }
            }
        }

        private IEnumerator RunP1R3Navigation(
            P1R3Trajectory actor,
            P1R3PrivilegedTrajectory evaluator,
            P1R3Calibration calibration,
            SweepCountReference evaluatorReference,
            string provenanceRoot,
            UltrasoundManager manager)
        {
            int step = 1;
            int coarse = 0;
            int fine = 0;
            bool fineStage = false;
            while (true)
            {
                P1RobotPose measured = MeasureP1RobotPose();
                CalculateP1PoseErrors(measured, calibration.targetPose,
                    calibration.corridorAxis, calibration.lateralAxis,
                    out float longitudinal, out float lateral, out float full);
                float orientation = Quaternion.Angle(measured.rotation,
                    calibration.targetPose.rotation);
                if (P1R3PoseAccepted(longitudinal, lateral, full, orientation))
                {
                    actor.status = "POSITION_PRECISION_PASS";
                    actor.stopReason = "locked 0.25 mm full-pose tolerance reached";
                    yield break;
                }

                float requestedStep;
                string stage;
                Active2DAction action;
                if (!fineStage)
                {
                    action = ChooseP1ImprovingAction(longitudinal, lateral,
                        P1R3CoarseStepMm, P1R3MinimumImprovementMm);
                    if (action == Active2DAction.Stop || coarse >= P1R3CoarseBudget)
                    {
                        fineStage = true;
                        continue;
                    }
                    requestedStep = P1R3CoarseStepMm;
                    stage = "COARSE";
                }
                else
                {
                    if (fine >= P1R3FineBudget)
                    {
                        actor.status = "POSITION_PRECISION_FAIL";
                        actor.stopReason = "fine movement budget exhausted";
                        yield break;
                    }
                    action = ChooseP1ImprovingAction(longitudinal, lateral,
                        P1R3FineStepMm, P1R3MinimumImprovementMm);
                    if (action == Active2DAction.Stop)
                    {
                        actor.status = "POSITION_PRECISION_FAIL";
                        actor.stopReason = "no safe 0.5 mm action predicts meaningful improvement";
                        yield break;
                    }
                    requestedStep = P1R3FineStepMm;
                    stage = "FINE";
                }

                float before = P1SelectedAxisAbsoluteError(action, longitudinal, lateral);
                active2DStepMm = requestedStep;
                bool accepted = TryP1PhysicalAction(action, 0f);
                if (accepted)
                {
                    actor.cumulativeMovementMm += active2DLastActionExecution.achievedDisplacementMm;
                    if (fineStage) { fine++; actor.acceptedFineMovements++; }
                    else { coarse++; actor.acceptedCoarseMovements++; }
                }
                else actor.rejectedMovements++;
                RecordP1R3Step(actor, evaluator, calibration, evaluatorReference,
                    step, stage, action, accepted, null, string.Empty,
                    provenanceRoot, manager);
                if (!accepted || active2DLastActionExecution.safetyFailure)
                    throw new InvalidOperationException(
                        "P1-R3 rejected/unsafe movement: "
                        + active2DLastActionExecution.rejectionReason);
                P1R3Step completed = actor.steps.Last();
                float after = P1SelectedAxisAbsoluteError(action,
                    completed.longitudinalErrorMm, completed.lateralErrorMm);
                if (before - after < P1R3MinimumImprovementMm)
                {
                    if (!fineStage) fineStage = true;
                    else
                    {
                        actor.status = "POSITION_PRECISION_FAIL";
                        actor.stopReason = "fine movement failed minimum progress";
                        yield break;
                    }
                }
                step++;
                yield return null;
            }
        }

        private IEnumerator RunP1R3Sensitivity(
            P1R3Trajectory actor,
            P1R3PrivilegedTrajectory evaluator,
            P1R3Calibration calibration,
            SweepCountReference evaluatorReference,
            string frameRoot,
            string provenanceRoot,
            UltrasoundManager manager)
        {
            Active2DAction[] route =
            {
                Active2DAction.Caudal, Active2DAction.Caudal,
                Active2DAction.Cranial, Active2DAction.Cranial,
                Active2DAction.Cranial, Active2DAction.Cranial,
                Active2DAction.Caudal, Active2DAction.Caudal
            };
            active2DStepMm = P1R3FineStepMm;
            for (int index = 0; index < route.Length; index++)
            {
                Active2DAction action = route[index];
                bool accepted = TryP1PhysicalAction(action, 0f);
                if (!accepted || active2DLastActionExecution.safetyFailure)
                    throw new InvalidOperationException(
                        "P1-R3 sensitivity movement rejected: "
                        + active2DLastActionExecution.rejectionReason);
                actor.acceptedFineMovements++;
                actor.cumulativeMovementMm += active2DLastActionExecution.achievedDisplacementMm;
                DirectFreshCaptureResult capture = new DirectFreshCaptureResult();
                yield return CaptureP1R3Frame("sensitivity_" + (index + 1), capture);
                string fileName = "sensitivity_" + (index + 1).ToString("D2",
                    CultureInfo.InvariantCulture) + ".png";
                SaveP1R3Capture(frameRoot, fileName, capture);
                RecordP1R3Step(actor, evaluator, calibration, evaluatorReference,
                    index + 1, "SENSITIVITY", action, true, capture,
                    actor.scenario == "FINE_MOTION_SENSITIVITY"
                        ? "development/D03_FINE_MOTION_SENSITIVITY/frames/" + fileName
                        : fileName,
                    provenanceRoot, manager);
                yield return null;
            }
            P1R3Step final = actor.steps.Last();
            actor.status = final.fullContactPointErrorMm <= P1R3PoseToleranceMm
                && final.orientationErrorDeg <= P1R3OrientationToleranceDeg
                ? "SENSITIVITY_ROUTE_PASS" : "SENSITIVITY_ROUTE_FAIL";
            actor.stopReason = "bounded physical +/-1 mm fine-motion route completed";
            if (actor.status != "SENSITIVITY_ROUTE_PASS")
                throw new InvalidOperationException("P1-R3 sensitivity route failed center return tolerance");
        }

        private IEnumerator CaptureP1R3Frame(string label, DirectFreshCaptureResult result)
        {
            DirectFreshPoseToken token = BeginDirectFreshCapturePose(label, true);
            yield return CaptureDirectProducerFreshFrame(token, label, result, true);
            if (!M0BFrameIntegrityValid(result.image, result.pngBytes) || !P1CaptureFresh(result))
                throw new InvalidOperationException("P1-R3 frame failed integrity/freshness: " + label);
        }

        private static void SaveP1R3Capture(
            string frameRoot, string fileName, DirectFreshCaptureResult capture)
        {
            File.WriteAllBytes(Path.Combine(frameRoot, fileName), capture.pngBytes);
        }

        private void RecordP1R3Step(
            P1R3Trajectory actor,
            P1R3PrivilegedTrajectory evaluator,
            P1R3Calibration calibration,
            SweepCountReference evaluatorReference,
            int step,
            string stage,
            Active2DAction action,
            bool accepted,
            DirectFreshCaptureResult capture,
            string image,
            string provenanceRoot,
            UltrasoundManager manager)
        {
            P1RobotPose measured = MeasureP1RobotPose();
            CalculateP1PoseErrors(measured, calibration.targetPose,
                calibration.corridorAxis, calibration.lateralAxis,
                out float longitudinal, out float lateral, out float full);
            bool noMovement = action == Active2DAction.Observe || action == Active2DAction.Stop;
            Active2DActionExecution execution = active2DLastActionExecution;
            P1R3Step actorStep = new P1R3Step
            {
                step = step,
                stage = stage,
                action = action.ToString().ToUpperInvariant(),
                accepted = accepted,
                requestedDisplacementMm = noMovement ? 0f : execution.requestedDisplacementMm,
                achievedDisplacementMm = noMovement ? 0f : execution.achievedDisplacementMm,
                measuredPose = measured,
                longitudinalErrorMm = longitudinal,
                lateralErrorMm = lateral,
                fullContactPointErrorMm = full,
                orientationErrorDeg = Quaternion.Angle(measured.rotation,
                    calibration.targetPose.rotation),
                contactValid = noMovement ? MeasureSweepCountContact()
                    : execution.contactValidAfterExecution,
                workspaceValid = noMovement ? IsActive2DWorkspaceValid(faceCenter.position)
                    : execution.workspaceValidAfterExecution,
                rollbackAttempted = !noMovement && execution.rollbackAttempted,
                rollbackContactRestored = !noMovement && execution.rollbackContactRestored,
                safetyFailure = !noMovement && execution.safetyFailure,
                rejectionReason = noMovement ? string.Empty : execution.rejectionReason,
                image = image,
                imageSha256 = capture != null ? Sha256Bytes(capture.pngBytes) : string.Empty,
                frameIntegrityValid = capture == null
                    || M0BFrameIntegrityValid(capture.image, capture.pngBytes),
                poseEpoch = capture != null ? capture.poseEpoch : -1,
                poseAppliedUnityFrame = capture != null ? capture.poseAppliedUnityFrame : -1,
                producerPoseEpoch = capture != null ? capture.producerPoseEpoch : -1,
                producerUnityFrame = capture != null ? capture.producerUnityFrame : -1,
                producerScanSerial = capture != null ? capture.producerScanSerial : -1,
                captureUnityFrame = capture != null ? capture.captureUnityFrame : -1
            };
            actor.steps.Add(actorStep);

            Vector3 normal = CurrentSurfaceNormal();
            Vector3 outward = (rearCenter.position - faceCenter.position).normalized;
            P1R3PrivilegedStep privileged = new P1R3PrivilegedStep
            {
                step = step,
                distanceToT7ReferenceMm = Vector3.Distance(
                    faceCenter.position, evaluatorReference.contactPoint),
                achievedSurfaceYawDeg = MeasureSweepCountSurfaceYawDeg(normal),
                normalAlignmentErrorDeg = Vector3.Angle(outward, normal),
                signedContactDepthMm = skinIntersection.SignedDepth,
                contactFraction = skinIntersection.ContactPercent
            };
            privileged.surfaceYawErrorDeg = Mathf.DeltaAngle(0f,
                privileged.achievedSurfaceYawDeg);
            if (capture != null)
            {
                string fileName = "step_" + step.ToString("D3",
                    CultureInfo.InvariantCulture) + ".json";
                privileged.provenanceMatched = WriteP1R3Provenance(manager,
                    capture, provenanceRoot, fileName, out string relative);
                privileged.provenance = relative;
            }
            evaluator.steps.Add(privileged);
        }

        private bool WriteP1R3Provenance(
            UltrasoundManager manager,
            DirectFreshCaptureResult capture,
            string provenanceRoot,
            string fileName,
            out string relative)
        {
            if (!manager.TryGetPublishedScanProvenance(
                    capture.producerPoseEpoch, capture.producerScanSerial,
                    out UltrasoundManager.PublishedScanProvenanceSnapshot provenance))
                throw new InvalidOperationException("P1-R3 published provenance is missing");
            WriteSweepCountJson(Path.Combine(provenanceRoot, fileName), provenance);
            relative = "evaluator_only/provenance/" + fileName;
            return provenance.poseEpoch == capture.producerPoseEpoch
                && provenance.producerUnityFrame == capture.producerUnityFrame
                && provenance.producerScanSerial == capture.producerScanSerial;
        }

        private static bool P1R3PoseAccepted(
            float longitudinal, float lateral, float full, float orientation)
        {
            return Mathf.Abs(longitudinal) <= P1R3PoseToleranceMm
                && Mathf.Abs(lateral) <= P1R3PoseToleranceMm
                && full <= P1R3PoseToleranceMm
                && orientation <= P1R3OrientationToleranceDeg;
        }

        private P1R3Registry LoadP1R3Registry()
        {
            string path = Path.Combine(P1R3OutputDirectory, P1R3RegistryFile);
            if (!File.Exists(path)) return new P1R3Registry();
            P1R3Registry registry = JsonUtility.FromJson<P1R3Registry>(File.ReadAllText(path));
            return registry ?? new P1R3Registry();
        }

        private void SaveP1R3Registry(P1R3Registry registry)
        {
            Directory.CreateDirectory(P1R3OutputDirectory);
            WriteSweepCountJson(Path.Combine(P1R3OutputDirectory, P1R3RegistryFile), registry);
        }

        private P1R3RegistryEntry BeginP1R3RegistryEntry(
            P1R3Registry registry, bool confirmation)
        {
            List<P1R3RegistryEntry> runs = confirmation
                ? registry.confirmationRuns : registry.developmentRuns;
            int maximum = confirmation
                ? P1R3MaximumConfirmationRuns : P1R3MaximumDevelopmentRuns;
            if (runs.Count >= maximum)
                throw new InvalidOperationException("P1-R3 cumulative run budget is exhausted");
            if (runs.Any(item => item.status == "RUNNING"))
                throw new InvalidOperationException("P1-R3 registry contains an unfinished consumed run");
            if (confirmation && !File.Exists(Path.Combine(
                    P1R3OutputDirectory, P1R3FrozenFile)))
                throw new InvalidOperationException("P1-R3 configuration is not frozen");
            int ordinal = runs.Count + 1;
            string scenario = confirmation ? "FROZEN_CONFIRMATION"
                : ordinal == 1 ? "POSITION_NAV_MINUS30"
                : ordinal == 2 ? "POSITION_NAV_MINUS27_4"
                : ordinal == 3 ? "FINE_MOTION_SENSITIVITY"
                : throw new InvalidOperationException(
                    "P1-R3 planned development is complete; freeze before more runs");
            string runId = (confirmation ? "C" : "D")
                + ordinal.ToString("D2", CultureInfo.InvariantCulture);
            string directory = (confirmation ? "confirmation/" : "development/")
                + runId + "_" + scenario;
            P1R3RegistryEntry entry = new P1R3RegistryEntry
            {
                runId = runId,
                kind = confirmation ? "CONFIRMATION" : "DEVELOPMENT",
                scenario = scenario,
                directory = directory,
                startedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            };
            runs.Add(entry);
            registry.state = confirmation ? "CONFIRMATION_EXECUTED" : "DEVELOPMENT_IN_PROGRESS";
            SaveP1R3Registry(registry);
            return entry;
        }

        private void CompleteP1R3RegistryEntry(string runId, bool confirmation)
        {
            P1R3Registry registry = LoadP1R3Registry();
            P1R3RegistryEntry entry = (confirmation
                ? registry.confirmationRuns : registry.developmentRuns)
                .Single(item => item.runId == runId);
            entry.status = "EXECUTED";
            entry.completedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            registry.state = confirmation ? "CONFIRMATION_EXECUTED"
                : registry.developmentRuns.Count >= 3
                    ? "DEVELOPMENT_COMPLETE" : "DEVELOPMENT_IN_PROGRESS";
            SaveP1R3Registry(registry);
        }

        private void MarkP1R3RegistryFailure(
            string runId, bool confirmation, string failure)
        {
            if (string.IsNullOrEmpty(runId)) return;
            P1R3Registry registry = LoadP1R3Registry();
            P1R3RegistryEntry entry = (confirmation
                ? registry.confirmationRuns : registry.developmentRuns)
                .FirstOrDefault(item => item.runId == runId);
            if (entry == null) return;
            entry.status = "FAILED";
            entry.completedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            entry.failure = failure;
            registry.state = "BLOCKED";
            SaveP1R3Registry(registry);
        }

        private P1R3Calibration LoadP1R3FrozenCalibration()
        {
            P1R3FrozenConfiguration frozen = JsonUtility.FromJson<P1R3FrozenConfiguration>(
                File.ReadAllText(Path.Combine(P1R3OutputDirectory, P1R3FrozenFile)));
            if (frozen == null || !frozen.frozen
                || frozen.confirmationStartOffsetsMm == null
                || frozen.confirmationStartOffsetsMm.Length != P1R3MaximumConfirmationRuns)
                throw new InvalidOperationException("P1-R3 frozen configuration is invalid");
            return new P1R3Calibration
            {
                runId = "FROZEN_" + frozen.sourceRunId,
                calibrationBoundary =
                    "Loaded frozen development calibration; no runtime T7 target query for actor control.",
                targetPose = frozen.targetPose,
                corridorAxis = frozen.corridorAxis,
                lateralAxis = frozen.lateralAxis,
                targetSurfaceNormal = frozen.targetSurfaceNormal,
                referenceImage = frozen.referenceImage,
                referenceImageSha256 = frozen.referenceImageSha256,
                originalSsimThreshold = frozen.originalSsimThreshold,
                fresh = true,
                provenanceMatched = true
            };
        }

        private float P1R3ConfirmationOffset(int index)
        {
            P1R3FrozenConfiguration frozen = JsonUtility.FromJson<P1R3FrozenConfiguration>(
                File.ReadAllText(Path.Combine(P1R3OutputDirectory, P1R3FrozenFile)));
            if (index < 0 || index >= frozen.confirmationStartOffsetsMm.Length)
                throw new InvalidOperationException("P1-R3 confirmation start index is invalid");
            return frozen.confirmationStartOffsetsMm[index];
        }

        private static float P1R3DevelopmentOffset(int index)
        {
            if (index == 0) return -30f;
            if (index == 1) return -27.4f;
            throw new InvalidOperationException("P1-R3 development offset is undefined");
        }

        private static float LoadP1R3OriginalSsimThreshold()
        {
            string path = Path.Combine(P1BaselineDirectory, "confirmation_metric.json");
            P1ConfirmationMetric metric = JsonUtility.FromJson<P1ConfirmationMetric>(
                File.ReadAllText(path));
            if (metric == null || metric.lockedThreshold <= 0f)
                throw new InvalidOperationException("P1 original SSIM threshold is unavailable");
            return metric.lockedThreshold;
        }
    }
}
