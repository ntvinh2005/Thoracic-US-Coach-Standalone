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
        private const string P1OutputDirectory =
            "Artifacts/ThoracicUSCoach/position_guided_t7/P1_POSITION_AWARE_PILOT";
        private const string P1Mode = "CALIBRATED_POSITION_GUIDED_BASELINE";
        private const float P1YawDeg = 0f;
        private const float P1StartOffsetMm = -30f;
        private const float P1LongitudinalToleranceMm = 3f;
        private const float P1LateralToleranceMm = 3f;
        private const float P1QuaternionToleranceDeg = 0.5f;
        private const int P1MovementBudget = 24;

        [Serializable]
        private sealed class P1RobotPose
        {
            public Vector3 positionMm;
            public Quaternion rotation;
            public Vector3 contactPointMm;
        }

        [Serializable]
        private sealed class P1ConfirmationMetric
        {
            public string metric = string.Empty;
            public float lockedThreshold;
            public int controlPairCount;
            public string source = string.Empty;
        }

        [Serializable]
        private sealed class P1CalibrationState
        {
            public string contract = "thoracic_p1_calibration_v1";
            public string generatedUtc = string.Empty;
            public string mode = P1Mode;
            public string calibrationBoundary =
                "Unity T7 reference is read once during calibration. Navigation receives only this frozen robot pose.";
            public string robotFrame = "UNITY_WORLD_IDENTITY";
            public P1RobotPose storedTargetPose = new P1RobotPose();
            public Vector3 corridorAxis;
            public Vector3 lateralAxis;
            public string referenceImage = string.Empty;
            public string referenceImageSha256 = string.Empty;
            public float lockedImageSimilarityThreshold;
            public bool fresh;
            public bool frozen;
        }

        [Serializable]
        private sealed class P1ActorStep
        {
            public int step;
            public string action = string.Empty;
            public bool accepted;
            public float requestedDisplacementMm;
            public float achievedDisplacementMm;
            public P1RobotPose measuredPose = new P1RobotPose();
            public float longitudinalErrorMm;
            public float lateralErrorMm;
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
        private sealed class P1ActorTrajectory
        {
            public string contract = "thoracic_p1_calibrated_position_actor_v1";
            public string generatedUtc = string.Empty;
            public string mode = P1Mode;
            public string robotFrame = "UNITY_WORLD_IDENTITY";
            public string allowedInputs =
                "Measured robot pose/orientation, frozen calibrated target pose, executed actions, movement outcomes, and fresh ultrasound.";
            public string restrictedRuntimeInputs =
                "No marker transform query, T-level label, evaluator distance, target score, mesh identity, or renderer provenance during navigation.";
            public P1RobotPose frozenCalibratedTargetPose = new P1RobotPose();
            public P1RobotPose navigationStartPose = new P1RobotPose();
            public float configuredStepMm;
            public float longitudinalToleranceMm;
            public float lateralToleranceMm;
            public int movementBudget;
            public int acceptedMovements;
            public int rejectedMovements;
            public float cumulativeMovementMm;
            public string status = "running";
            public string stopReason = string.Empty;
            public List<P1ActorStep> steps = new List<P1ActorStep>();
        }

        [Serializable]
        private sealed class P1PrivilegedStep
        {
            public int step;
            public string evaluatorReferenceLevel = "T7";
            public float distanceToReferenceContactMm;
            public float evaluatorLongitudinalErrorMm;
            public float evaluatorLateralErrorMm;
            public float achievedSurfaceYawDeg;
            public float surfaceYawErrorDeg;
            public float normalAlignmentErrorDeg;
            public float signedContactDepthMm;
            public float contactFraction;
            public string provenance = string.Empty;
            public bool provenanceMatchedPublishedFrame;
        }

        [Serializable]
        private sealed class P1PrivilegedTrajectory
        {
            public string contract = "thoracic_p1_calibrated_position_evaluator_v1";
            public string generatedUtc = string.Empty;
            public string warning = "EVALUATOR ONLY. Never supplied to navigation or image relocalization.";
            public Vector3 evaluatorReferenceContactPoint;
            public Vector3 evaluatorReferenceSurfaceNormal;
            public List<P1PrivilegedStep> steps = new List<P1PrivilegedStep>();
        }

        [Serializable]
        private sealed class P1PilotEvaluation
        {
            public string contract = "thoracic_p1_calibrated_position_evaluation_v1";
            public string generatedUtc = string.Empty;
            public string mode = P1Mode;
            public string positionNavigation = "NOT_RUN";
            public string freshUltrasoundConfirmation = "NOT_RUN";
            public string spatialRelocalization = "OFFLINE_RESULT_SEPARATE";
            public string autonomousT7Identification = "NOT_TESTED";
            public float initialLongitudinalSeparationMm;
            public float finalLongitudinalErrorMm;
            public float finalLateralErrorMm;
            public float finalQuaternionErrorDeg;
            public float finalSurfaceYawErrorDeg;
            public float finalNormalAlignmentErrorDeg;
            public bool finalContactValid;
            public bool finalWorkspaceValid;
            public bool allFresh;
            public bool noUnhandledSafetyFailure;
            public bool noTeleportDuringNavigation;
            public int acceptedMovements;
            public int rejectedMovements;
            public float cumulativeMovementMm;
            public string calibrationImage = string.Empty;
            public string returnImage = string.Empty;
            public float lockedImageSimilarityThreshold;
            public string imageSimilarityStatus = "PENDING_OFFLINE_ANALYSIS";
            public string note = string.Empty;
            public string g2Status = "FAIL";
            public string g3Status = "NOT_RUN";
            public string g4Status = "NOT_RUN";
        }

        private Coroutine p1PositionGuidedRoutine;
        private bool p1PositionGuidedComplete;
        private string p1PositionGuidedFailure = string.Empty;
        private string p1PositionGuidedStatus = "idle";

        public bool P1PositionGuidedComplete => p1PositionGuidedComplete;
        public string P1PositionGuidedFailure => p1PositionGuidedFailure;
        public string P1PositionGuidedStatus => p1PositionGuidedStatus;
        public string P1PositionGuidedOutputRoot => P1OutputDirectory;

        public bool StartP1PositionGuidedPilot()
        {
            if (!Application.isPlaying || p1PositionGuidedRoutine != null) return false;
            if (File.Exists(Path.Combine(P1OutputDirectory, "pilot_evaluation.json"))) return false;
            p1PositionGuidedComplete = false;
            p1PositionGuidedFailure = string.Empty;
            p1PositionGuidedStatus = "starting";
            p1PositionGuidedRoutine = StartCoroutine(RunP1PositionGuidedPilot());
            return true;
        }

        private IEnumerator RunP1PositionGuidedPilot()
        {
            Stack<IEnumerator> stack = new Stack<IEnumerator>();
            stack.Push(RunP1PositionGuidedPilotCore());
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
            if (failure == null) yield break;
            while (stack.Count > 0)
            {
                try { (stack.Pop() as IDisposable)?.Dispose(); }
                catch (Exception) { }
            }
            p1PositionGuidedFailure = failure.ToString();
            p1PositionGuidedStatus = "INFRASTRUCTURE_OR_SAFETY_FAILURE";
            p1PositionGuidedRoutine = null;
            p1PositionGuidedComplete = true;
            Debug.LogError("[ThoracicUSCoach][P1PositionGuided] " + failure);
        }

        private IEnumerator RunP1PositionGuidedPilotCore()
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
            P1ActorTrajectory actor = null;
            P1PrivilegedTrajectory evaluator = null;
            P1PilotEvaluation evaluation = null;
            string frameRoot = Path.Combine(P1OutputDirectory, "frames");
            string evaluatorRoot = Path.Combine(P1OutputDirectory, "evaluator_only");
            string provenanceRoot = Path.Combine(evaluatorRoot, "provenance");
            try
            {
                if (!ValidateReferences(out string referenceError))
                    throw new InvalidOperationException(referenceError);
                if (probeRoot == null || faceCenter == null || rearCenter == null
                    || skinCollider == null || skinIntersection == null)
                    throw new InvalidOperationException(
                        "P1 requires probe, face/rear centers, exact skin collider, and contact measurement");
                if (!File.Exists(Path.Combine(P1OutputDirectory, "offline_results.sha256")))
                    throw new InvalidOperationException(
                        "P1 offline relocalization must be frozen before the physical pilot");
                P1ConfirmationMetric metric = JsonUtility.FromJson<P1ConfirmationMetric>(
                    File.ReadAllText(Path.Combine(P1OutputDirectory, "confirmation_metric.json")));
                if (metric == null || metric.lockedThreshold <= 0f || metric.lockedThreshold > 1f)
                    throw new InvalidOperationException("P1 locked confirmation metric is invalid");
                Directory.CreateDirectory(frameRoot);
                Directory.CreateDirectory(provenanceRoot);

                active2DStepMm = 3f;
                sweepCountYawDeg = P1YawDeg;
                SweepCountReferenceSet references = BuildSweepCountReferences();
                SweepCountReference targetReference = references.references.Single(item => item.level == "T7");
                Vector3 corridorAxis = (references.references[6].contactPoint
                    - references.references[0].contactPoint).normalized;
                P1CalibrationState calibration = new P1CalibrationState
                {
                    generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    corridorAxis = corridorAxis,
                    lockedImageSimilarityThreshold = metric.lockedThreshold
                };
                if (!PlaceSweepCountProbe(targetReference.contactPoint,
                        targetReference.surfaceNormal, corridorAxis, out string calibrationFailure))
                    throw new InvalidOperationException("P1 calibration placement failed: " + calibrationFailure);
                active2DCaudalAxis = Vector3.ProjectOnPlane(
                    corridorAxis, CurrentSurfaceNormal()).normalized;
                active2DLateralAxis = Vector3.Cross(
                    CurrentSurfaceNormal(), active2DCaudalAxis).normalized;
                caudalTangent = active2DCaudalAxis;
                active2DWorkspaceOrigin = faceCenter.position;
                active2DLongitudinalHalfExtentMm = originalLongitudinalExtent;
                active2DLateralHalfExtentMm = originalLateralExtent;
                calibration.lateralAxis = active2DLateralAxis;

                if (!UltrasoundManager.TryGet(UltrasoundManager.Plane.Primary,
                        out UltrasoundManager manager) || manager == null)
                    throw new InvalidOperationException("P1 primary ultrasound manager is unavailable");
                MiniZigzagInstrumentationCheck instrumentation =
                    RunMiniZigzagInstrumentationCheck(manager, evaluatorRoot);
                WriteSweepCountJson(Path.Combine(evaluatorRoot, "instrumentation_check.json"),
                    instrumentation);
                if (!instrumentation.exactPixelParity || !instrumentation.metadataMatched)
                    throw new InvalidOperationException(
                        "P1 provenance instrumentation parity or metadata check failed");

                DirectFreshCaptureResult calibrationCapture = new DirectFreshCaptureResult();
                DirectFreshPoseToken calibrationToken = BeginDirectFreshCapturePose(
                    "p1_calibration", true);
                yield return CaptureDirectProducerFreshFrame(
                    calibrationToken, "p1_calibration", calibrationCapture, true);
                if (!M0BFrameIntegrityValid(calibrationCapture.image, calibrationCapture.pngBytes))
                    throw new InvalidOperationException("P1 calibration ultrasound frame failed integrity");
                string calibrationFile = "calibration.png";
                File.WriteAllBytes(Path.Combine(frameRoot, calibrationFile), calibrationCapture.pngBytes);
                calibration.storedTargetPose = MeasureP1RobotPose();
                calibration.referenceImage = "frames/" + calibrationFile;
                calibration.referenceImageSha256 = Sha256Bytes(calibrationCapture.pngBytes);
                calibration.fresh = P1CaptureFresh(calibrationCapture);
                calibration.frozen = calibration.fresh;
                if (!calibration.frozen)
                    throw new InvalidOperationException("P1 calibration capture was not fresh");
                WriteSweepCountJson(Path.Combine(P1OutputDirectory, "calibration_manifest.json"),
                    calibration);

                Vector3 proposedStart = calibration.storedTargetPose.contactPointMm
                    + corridorAxis * P1StartOffsetMm;
                if (!FindSurfaceAt(proposedStart, targetReference.surfaceNormal, out RaycastHit startHit))
                    throw new InvalidOperationException("P1 deterministic start surface projection failed");
                if (!PlaceSweepCountProbe(startHit.point, startHit.normal, corridorAxis,
                        out string startFailure))
                    throw new InvalidOperationException("P1 start placement failed: " + startFailure);
                active2DCaudalAxis = Vector3.ProjectOnPlane(
                    corridorAxis, CurrentSurfaceNormal()).normalized;
                active2DLateralAxis = Vector3.Cross(
                    CurrentSurfaceNormal(), active2DCaudalAxis).normalized;
                caudalTangent = active2DCaudalAxis;
                active2DWorkspaceOrigin = calibration.storedTargetPose.contactPointMm;
                active2DRejectedMoves = 0;
                active2DContactViolations = 0;
                active2DWorkspaceViolations = 0;

                actor = new P1ActorTrajectory
                {
                    generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    frozenCalibratedTargetPose = calibration.storedTargetPose,
                    navigationStartPose = MeasureP1RobotPose(),
                    configuredStepMm = active2DStepMm,
                    longitudinalToleranceMm = P1LongitudinalToleranceMm,
                    lateralToleranceMm = P1LateralToleranceMm,
                    movementBudget = P1MovementBudget
                };
                evaluator = new P1PrivilegedTrajectory
                {
                    generatedUtc = actor.generatedUtc,
                    evaluatorReferenceContactPoint = targetReference.contactPoint,
                    evaluatorReferenceSurfaceNormal = targetReference.surfaceNormal
                };
                evaluation = new P1PilotEvaluation
                {
                    generatedUtc = actor.generatedUtc,
                    initialLongitudinalSeparationMm = Mathf.Abs(Vector3.Dot(
                        calibration.storedTargetPose.contactPointMm - actor.navigationStartPose.contactPointMm,
                        calibration.corridorAxis)),
                    calibrationImage = calibration.referenceImage,
                    lockedImageSimilarityThreshold = metric.lockedThreshold
                };
                if (evaluation.initialLongitudinalSeparationMm < 18f)
                    throw new InvalidOperationException("P1 start is too close to the calibrated target");

                DirectFreshCaptureResult startCapture = new DirectFreshCaptureResult();
                DirectFreshPoseToken startToken = BeginDirectFreshCapturePose("p1_navigation_start", true);
                yield return CaptureDirectProducerFreshFrame(
                    startToken, "p1_navigation_start", startCapture, true);
                File.WriteAllBytes(Path.Combine(frameRoot, "start.png"), startCapture.pngBytes);
                RecordP1Step(actor, evaluator, calibration, targetReference, 0,
                    Active2DAction.Observe, true, startCapture, "frames/start.png", provenanceRoot, manager);

                yield return RunP1StoredPoseNavigation(
                    actor, evaluator, calibration, targetReference, provenanceRoot, manager);

                DirectFreshCaptureResult returnCapture = new DirectFreshCaptureResult();
                DirectFreshPoseToken returnToken = BeginDirectFreshCapturePose(
                    "p1_fresh_return_confirmation", true);
                yield return CaptureDirectProducerFreshFrame(
                    returnToken, "p1_fresh_return_confirmation", returnCapture, true);
                if (!M0BFrameIntegrityValid(returnCapture.image, returnCapture.pngBytes))
                    throw new InvalidOperationException("P1 return ultrasound frame failed integrity");
                File.WriteAllBytes(Path.Combine(frameRoot, "return.png"), returnCapture.pngBytes);
                RecordP1Step(actor, evaluator, calibration, targetReference, actor.steps.Count,
                    Active2DAction.Stop, true, returnCapture, "frames/return.png", provenanceRoot, manager);

                P1ActorStep final = actor.steps.Last();
                P1PrivilegedStep finalPrivileged = evaluator.steps.Last();
                bool positionPass = Mathf.Abs(final.longitudinalErrorMm) <= P1LongitudinalToleranceMm
                    && Mathf.Abs(final.lateralErrorMm) <= P1LateralToleranceMm
                    && final.orientationErrorDeg <= P1QuaternionToleranceDeg
                    && Mathf.Abs(finalPrivileged.surfaceYawErrorDeg) <= SweepCountYawToleranceDeg
                    && finalPrivileged.normalAlignmentErrorDeg <= MiniZigzagNormalToleranceDeg
                    && final.contactValid && final.workspaceValid
                    && actor.rejectedMovements == 0
                    && actor.steps.All(item => !item.safetyFailure);
                actor.status = positionPass
                    ? "complete_position_navigation_pass"
                    : "complete_position_navigation_fail";
                actor.stopReason = positionPass
                    ? "stored calibrated target pose reached within locked tolerances"
                    : "final pose, orientation, contact, workspace, or safety gate failed";
                evaluation.positionNavigation = positionPass ? "PASS" : "FAIL";
                evaluation.freshUltrasoundConfirmation = P1CaptureFresh(returnCapture)
                    ? "PENDING_OFFLINE_SIMILARITY_AND_EVALUATOR_CHECK" : "FAIL";
                evaluation.finalLongitudinalErrorMm = Mathf.Abs(final.longitudinalErrorMm);
                evaluation.finalLateralErrorMm = Mathf.Abs(final.lateralErrorMm);
                evaluation.finalQuaternionErrorDeg = final.orientationErrorDeg;
                evaluation.finalSurfaceYawErrorDeg = Mathf.Abs(finalPrivileged.surfaceYawErrorDeg);
                evaluation.finalNormalAlignmentErrorDeg = finalPrivileged.normalAlignmentErrorDeg;
                evaluation.finalContactValid = final.contactValid;
                evaluation.finalWorkspaceValid = final.workspaceValid;
                evaluation.allFresh = actor.steps.Where(item => !string.IsNullOrEmpty(item.image))
                    .All(P1StepFresh);
                evaluation.noUnhandledSafetyFailure = actor.steps.All(item => !item.safetyFailure);
                evaluation.noTeleportDuringNavigation = actor.steps
                    .Where(item => item.action != Active2DAction.Observe.ToString().ToUpperInvariant()
                        && item.action != Active2DAction.Stop.ToString().ToUpperInvariant())
                    .All(item => item.achievedDisplacementMm <= active2DStepMm + 2.5f);
                evaluation.acceptedMovements = actor.acceptedMovements;
                evaluation.rejectedMovements = actor.rejectedMovements;
                evaluation.cumulativeMovementMm = actor.cumulativeMovementMm;
                evaluation.returnImage = "frames/return.png";
                evaluation.note =
                    "Position navigation is calibrated engineering mode. Ultrasound similarity is scored offline with the pre-frozen metric; autonomous T7 identification is not tested.";
                WriteP1Artifacts(actor, evaluator, evaluation);
                p1PositionGuidedStatus = positionPass
                    ? "POSITION_PASS_PENDING_OFFLINE_CONFIRMATION"
                    : "POSITION_NAVIGATION_FAIL";
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
                p1PositionGuidedRoutine = null;
                p1PositionGuidedComplete = true;
            }
        }

        private IEnumerator RunP1StoredPoseNavigation(
            P1ActorTrajectory actor,
            P1PrivilegedTrajectory evaluator,
            P1CalibrationState calibration,
            SweepCountReference evaluatorReference,
            string provenanceRoot,
            UltrasoundManager manager)
        {
            for (int step = 1; step <= P1MovementBudget; step++)
            {
                P1RobotPose measured = MeasureP1RobotPose();
                CalculateP1PoseErrors(measured, calibration.storedTargetPose,
                    calibration.corridorAxis, calibration.lateralAxis,
                    out float longitudinalError, out float lateralError);
                Active2DAction action = ChooseP1PositionAction(
                    longitudinalError, lateralError,
                    P1LongitudinalToleranceMm, P1LateralToleranceMm);
                if (action == Active2DAction.Stop) yield break;
                bool accepted = TryP1PhysicalAction(action, P1YawDeg);
                if (accepted)
                {
                    actor.acceptedMovements++;
                    actor.cumulativeMovementMm += active2DLastActionExecution.achievedDisplacementMm;
                }
                else actor.rejectedMovements++;
                RecordP1Step(actor, evaluator, calibration, evaluatorReference, step,
                    action, accepted, null, string.Empty, provenanceRoot, manager);
                if (!accepted || active2DLastActionExecution.safetyFailure)
                    throw new InvalidOperationException(
                        "P1 stopped on rejected or unsafe physical action: "
                        + active2DLastActionExecution.rejectionReason);
                yield return null;
            }
            throw new InvalidOperationException("P1 stored-pose navigation exhausted its bounded movement budget");
        }

        private bool TryP1PhysicalAction(Active2DAction action, float surfaceYawDeg)
        {
            BeginActive2DActionExecution(action);
            switch (action)
            {
                case Active2DAction.Caudal:
                    return TryActive2DMove(active2DCaudalAxis, surfaceYawDeg);
                case Active2DAction.Cranial:
                    return TryActive2DMove(-active2DCaudalAxis, surfaceYawDeg);
                case Active2DAction.Lateral:
                    return TryActive2DMove(active2DLateralAxis, surfaceYawDeg);
                case Active2DAction.Medial:
                    return TryActive2DMove(-active2DLateralAxis, surfaceYawDeg);
                default:
                    return RejectActive2DActionWithoutPoseChange(
                        "P1 navigation accepts only physical translation actions");
            }
        }

        private static Active2DAction ChooseP1PositionAction(
            float longitudinalErrorMm,
            float lateralErrorMm,
            float longitudinalToleranceMm,
            float lateralToleranceMm)
        {
            if (Mathf.Abs(longitudinalErrorMm) > longitudinalToleranceMm)
                return longitudinalErrorMm > 0f ? Active2DAction.Caudal : Active2DAction.Cranial;
            if (Mathf.Abs(lateralErrorMm) > lateralToleranceMm)
                return lateralErrorMm > 0f ? Active2DAction.Lateral : Active2DAction.Medial;
            return Active2DAction.Stop;
        }

        private static void CalculateP1PoseErrors(
            P1RobotPose measured,
            P1RobotPose target,
            Vector3 corridorAxis,
            Vector3 lateralAxis,
            out float longitudinalErrorMm,
            out float lateralErrorMm)
        {
            Vector3 difference = target.contactPointMm - measured.contactPointMm;
            longitudinalErrorMm = Vector3.Dot(difference, corridorAxis.normalized);
            lateralErrorMm = Vector3.Dot(difference, lateralAxis.normalized);
        }

        private P1RobotPose MeasureP1RobotPose()
        {
            return new P1RobotPose
            {
                positionMm = probeRoot.position,
                rotation = probeRoot.rotation,
                contactPointMm = faceCenter.position
            };
        }

        private void RecordP1Step(
            P1ActorTrajectory actor,
            P1PrivilegedTrajectory evaluator,
            P1CalibrationState calibration,
            SweepCountReference evaluatorReference,
            int step,
            Active2DAction action,
            bool accepted,
            DirectFreshCaptureResult capture,
            string image,
            string provenanceRoot,
            UltrasoundManager manager)
        {
            P1RobotPose measured = MeasureP1RobotPose();
            CalculateP1PoseErrors(measured, calibration.storedTargetPose,
                calibration.corridorAxis, calibration.lateralAxis,
                out float longitudinalError, out float lateralError);
            bool observeOrStop = action == Active2DAction.Observe || action == Active2DAction.Stop;
            Active2DActionExecution execution = active2DLastActionExecution;
            P1ActorStep actorStep = new P1ActorStep
            {
                step = step,
                action = action.ToString().ToUpperInvariant(),
                accepted = accepted,
                requestedDisplacementMm = observeOrStop ? 0f : execution.requestedDisplacementMm,
                achievedDisplacementMm = observeOrStop ? 0f : execution.achievedDisplacementMm,
                measuredPose = measured,
                longitudinalErrorMm = longitudinalError,
                lateralErrorMm = lateralError,
                orientationErrorDeg = Quaternion.Angle(measured.rotation,
                    calibration.storedTargetPose.rotation),
                contactValid = observeOrStop ? MeasureSweepCountContact()
                    : execution.contactValidAfterExecution,
                workspaceValid = observeOrStop ? IsActive2DWorkspaceValid(faceCenter.position)
                    : execution.workspaceValidAfterExecution,
                rollbackAttempted = !observeOrStop && execution.rollbackAttempted,
                rollbackContactRestored = !observeOrStop && execution.rollbackContactRestored,
                safetyFailure = !observeOrStop && execution.safetyFailure,
                rejectionReason = observeOrStop ? string.Empty : execution.rejectionReason,
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
            P1PrivilegedStep privilegedStep = new P1PrivilegedStep
            {
                step = step,
                distanceToReferenceContactMm = Vector3.Distance(
                    faceCenter.position, evaluatorReference.contactPoint),
                evaluatorLongitudinalErrorMm = Mathf.Abs(Vector3.Dot(
                    evaluatorReference.contactPoint - faceCenter.position,
                    calibration.corridorAxis)),
                evaluatorLateralErrorMm = Mathf.Abs(Vector3.Dot(
                    evaluatorReference.contactPoint - faceCenter.position,
                    calibration.lateralAxis)),
                achievedSurfaceYawDeg = MeasureSweepCountSurfaceYawDeg(normal),
                normalAlignmentErrorDeg = Vector3.Angle(outward, normal),
                signedContactDepthMm = skinIntersection.SignedDepth,
                contactFraction = skinIntersection.ContactPercent
            };
            privilegedStep.surfaceYawErrorDeg = Mathf.DeltaAngle(
                P1YawDeg, privilegedStep.achievedSurfaceYawDeg);
            if (capture != null)
            {
                if (!manager.TryGetPublishedScanProvenance(
                        capture.producerPoseEpoch, capture.producerScanSerial,
                        out UltrasoundManager.PublishedScanProvenanceSnapshot provenance))
                    throw new InvalidOperationException("P1 published provenance snapshot is missing");
                string provenanceFile = "p1_step_"
                    + step.ToString("D3", CultureInfo.InvariantCulture) + ".json";
                WriteSweepCountJson(Path.Combine(provenanceRoot, provenanceFile), provenance);
                privilegedStep.provenance = "evaluator_only/provenance/" + provenanceFile;
                privilegedStep.provenanceMatchedPublishedFrame =
                    provenance.poseEpoch == capture.producerPoseEpoch
                    && provenance.producerUnityFrame == capture.producerUnityFrame
                    && provenance.producerScanSerial == capture.producerScanSerial;
            }
            evaluator.steps.Add(privilegedStep);
        }

        private static bool P1CaptureFresh(DirectFreshCaptureResult capture)
        {
            return capture != null
                && capture.poseEpoch == capture.producerPoseEpoch
                && capture.producerUnityFrame > capture.poseAppliedUnityFrame
                && capture.captureUnityFrame == capture.producerUnityFrame
                && capture.producerScanSerial > 0
                && capture.activeAggregatorCount == 0;
        }

        private static bool P1StepFresh(P1ActorStep step)
        {
            return step != null && step.frameIntegrityValid
                && step.poseEpoch == step.producerPoseEpoch
                && step.producerUnityFrame > step.poseAppliedUnityFrame
                && step.captureUnityFrame == step.producerUnityFrame
                && step.producerScanSerial > 0;
        }

        private void WriteP1Artifacts(
            P1ActorTrajectory actor,
            P1PrivilegedTrajectory evaluator,
            P1PilotEvaluation evaluation)
        {
            WriteSweepCountJson(Path.Combine(P1OutputDirectory, "trajectory_actor.json"), actor);
            WriteSweepCountJson(Path.Combine(P1OutputDirectory,
                "evaluator_only", "trajectory_privileged.json"), evaluator);
            WriteSweepCountJson(Path.Combine(P1OutputDirectory, "pilot_evaluation.json"), evaluation);
        }
    }
}
