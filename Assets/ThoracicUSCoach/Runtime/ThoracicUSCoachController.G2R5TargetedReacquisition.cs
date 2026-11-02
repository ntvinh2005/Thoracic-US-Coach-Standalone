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
        private const float G2R5LateralOffsetMm = -6f;
        private const float G2R5PositionToleranceMm = 0.76f;
        private const int G2R5MaximumMovements = 320;
        private const int G2R5MaximumFreshFrames = 240;
        private const int G2R5MaximumStartupFrames = 120;
        private static string g2R5ProtocolPathOverride = string.Empty;

        [Serializable]
        private sealed class G2R5Variant
        {
            public string variantId;
            public float surfaceYawDeg;
            public int scanDepthMm;
        }

        [Serializable]
        private sealed class G2R5Region
        {
            public string regionId;
            public string category;
            public float actorRelativeCenterMm;
            public float[] supportIntervalMm;
            public float[] windowMm;
            public bool windowClampedByWorkspace;
            public float requestedHalfWidthMm;
            public int[] supportingFrameIndices;
            public string[] supportingImages;
            public int distinctFrameCount;
            public bool outboundSupport;
            public bool repeatSupport;
            public float actualShadowSupportFraction;
            public string[] ambiguityReasons;
            public string expectedInformationGain;
            public List<G2R5Variant> variants = new();
            public string coordinateSource;
        }

        [Serializable]
        private sealed class G2R5Protocol
        {
            public string contract;
            public string inputIsolation;
            public string mode;
            public float homeRelativeOriginMm;
            public string directionConvention;
            public float[] safeActorCoverageMm;
            public float movementStepMm;
            public int maximumFreshFrames;
            public int estimatedFreshFrames;
            public string outputRoot;
            public List<G2R5Region> regions = new();
        }

        [Serializable]
        private sealed class G2R5Movement
        {
            public int movementIndex;
            public string phase;
            public string regionId;
            public string variantId;
            public string requestedAction;
            public float requestedDisplacementMm;
            public float achievedDisplacementMm;
            public float actorRelativeBeforeMm;
            public float actorRelativeAfterMm;
            public float commandedYawDeg;
            public bool accepted;
            public bool contactValid;
            public bool workspaceValid;
            public bool rollbackAttempted;
            public bool rollbackContactRestored;
            public bool safetyFailure;
            public string rejectionReason;
        }

        [Serializable]
        private sealed class G2R5Observation
        {
            public int observationIndex;
            public int movementIndexAtCapture;
            public string phase;
            public string regionId;
            public string variantId;
            public string direction;
            public float signedActorRelativeMm;
            public float surfaceYawDeg;
            public int scanDepthMm;
            public float scanTopWidthMm;
            public float scanFanAngleDeg;
            public float renderedFootprintWidthMm;
            public string image;
            public string imageSha256;
            public bool duplicateImageHash;
            public int imageWidth;
            public int imageHeight;
            public bool frameIntegrityValid;
            public bool contactValid;
            public bool workspaceValid;
            public long poseEpoch;
            public int poseAppliedUnityFrame;
            public long producerPoseEpoch;
            public int producerUnityFrame;
            public long producerScanSerial;
            public int captureUnityFrame;
            public int activeAggregatorCount;
        }

        [Serializable]
        private sealed class G2R5ActorTrace
        {
            public string contract = "thoracic_g2_r5_targeted_actor_v1";
            public string generatedUtc;
            public string mode = "ACTOR_GUIDED_DIAGNOSTIC_REACQUISITION";
            public string allowedInputs =
                "Actor-selected Home-relative windows, clean pixels, acquisition settings, and executed relative motion.";
            public string excludedInputs =
                "T labels, anatomy coordinates, LF identities, world poses, evaluator scores, renderer provenance, and calibrated T7.";
            public string protocolSha256;
            public float configuredStepMm;
            public float configuredLateralOffsetMm;
            public float finalActorRelativeMm;
            public int acceptedMovements;
            public int rejectedMovements;
            public int freshFrames;
            public int duplicateImageHashes;
            public string status = "RUNNING";
            public string stopReason;
            public List<G2R5Movement> movements = new();
            public List<G2R5Observation> observations = new();
        }

        [Serializable]
        private sealed class G2R5PrivilegedObservation
        {
            public int observationIndex;
            public Vector3 probePosition;
            public Quaternion probeRotation;
            public Vector3 contactPoint;
            public Vector3 surfaceNormal;
            public float evaluatorLongitudinalCoordinateMm;
            public float achievedSurfaceYawDeg;
            public float surfaceYawErrorDeg;
            public float normalAlignmentErrorDeg;
            public string provenance;
            public bool provenanceMatchedPublishedFrame;
            public int beamCount;
        }

        [Serializable]
        private sealed class G2R5EvaluatorTrace
        {
            public string contract = "thoracic_g2_r5_targeted_evaluator_v1";
            public string warning = "Evaluator-only world geometry and renderer provenance; never actor input.";
            public string generatedUtc;
            public Vector3 homeContactPoint;
            public Vector3 corridorAxis;
            public string sceneSha256;
            public List<G2R5PrivilegedObservation> observations = new();
        }

        [Serializable]
        private sealed class G2R5Summary
        {
            public string contract = "thoracic_g2_r5_targeted_summary_v1";
            public string generatedUtc;
            public string status = "RUNNING";
            public int selectedRegions;
            public int acceptedMovements;
            public int rejectedMovements;
            public int freshFrames;
            public int duplicateImageHashes;
            public bool returnedHome;
            public bool originalSettingsRestored;
            public string stopReason;
        }

        [Serializable]
        private sealed class G2R5FrameManifest
        {
            public string contract = "thoracic_g2_r5_frame_manifest_v1";
            public List<G2R5Observation> frames = new();
        }

        [Serializable]
        private sealed class G2R5MovementTrace
        {
            public string contract = "thoracic_g2_r5_movement_trace_v1";
            public List<G2R5Movement> movements = new();
        }

        private Coroutine g2R5Routine;
        private bool g2R5Complete;
        private string g2R5Failure = string.Empty;
        private string g2R5Status = "IDLE";
        private float g2R5ActorRelativeMm;
        private HashSet<string> g2R5ImageHashes = new();

        public bool G2R5Complete => g2R5Complete;
        public string G2R5Failure => g2R5Failure;
        public string G2R5Status => g2R5Status;

        public static void ConfigureG2R5TargetedExperiment(string protocolPath)
        {
            if (string.IsNullOrWhiteSpace(protocolPath))
                throw new ArgumentException("G2-R5 protocol path is required", nameof(protocolPath));
            g2R5ProtocolPathOverride = protocolPath;
        }

        public static void ResetG2R5TargetedExperimentConfiguration()
        {
            g2R5ProtocolPathOverride = string.Empty;
        }

        public bool StartG2R5TargetedReacquisition()
        {
            if (!Application.isPlaying || g2R5Routine != null) return false;
            g2R5Complete = false;
            g2R5Failure = string.Empty;
            g2R5Status = "RUNNING";
            g2R5Routine = StartCoroutine(RunG2R5Guarded());
            return true;
        }

        private IEnumerator RunG2R5Guarded()
        {
            Exception failure = null;
            Stack<IEnumerator> stack = new Stack<IEnumerator>();
            stack.Push(RunG2R5Core());
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
            g2R5Failure = failure.ToString();
            g2R5Status = "INFRASTRUCTURE_OR_SAFETY_FAILURE";
            g2R5Routine = null;
            g2R5Complete = true;
            Debug.LogError("[ThoracicUSCoach][G2R5] " + failure);
        }

        private IEnumerator RunG2R5Core()
        {
            AutoWire();
            string protocolPath = Path.GetFullPath(g2R5ProtocolPathOverride);
            if (!File.Exists(protocolPath))
                throw new FileNotFoundException("G2-R5 actor protocol does not exist", protocolPath);
            G2R5Protocol protocol = JsonUtility.FromJson<G2R5Protocol>(File.ReadAllText(protocolPath));
            ValidateG2R5Protocol(protocol);
            string outputRoot = Path.GetFullPath(protocol.outputRoot);
            string frameRoot = Path.Combine(outputRoot, "frames");
            string evaluatorRoot = Path.Combine(outputRoot, "evaluator_only");
            string provenanceRoot = Path.Combine(evaluatorRoot, "provenance");
            if (File.Exists(Path.Combine(outputRoot, "trajectory_actor.json"))
                || Directory.Exists(frameRoot) || Directory.Exists(provenanceRoot))
                throw new InvalidOperationException("G2-R5 output exists; refusing to overwrite it");

            Vector3 originalPosition = probeRoot != null ? probeRoot.position : Vector3.zero;
            Quaternion originalRotation = probeRoot != null ? probeRoot.rotation : Quaternion.identity;
            float originalStep = active2DStepMm;
            float originalLongitudinalExtent = active2DLongitudinalHalfExtentMm;
            float originalLateralExtent = active2DLateralHalfExtentMm;
            float originalSweepYaw = sweepCountYawDeg;
            Vector3 originalWorkspaceOrigin = active2DWorkspaceOrigin;
            Vector3 originalCaudalAxis = active2DCaudalAxis;
            Vector3 originalLateralAxis = active2DLateralAxis;
            int originalDepthCm = 0;
            G2R5ActorTrace actor = null;
            G2R5EvaluatorTrace evaluator = null;
            G2R5Summary summary = null;
            UltrasoundManager manager = null;
            try
            {
                int startupFrames = 0;
                while (startupFrames < G2R5MaximumStartupFrames)
                {
                    AutoWire();
                    if (ValidateG2R2AcquisitionReferences(out _)
                        && UltrasoundManager.TryGet(UltrasoundManager.Plane.Primary, out manager)
                        && manager != null && manager.isActiveAndEnabled) break;
                    startupFrames++;
                    yield return null;
                }
                if (!ValidateG2R2AcquisitionReferences(out string referenceError))
                    throw new InvalidOperationException(referenceError);
                if (manager == null || !manager.isActiveAndEnabled)
                    throw new InvalidOperationException("G2-R5 primary ultrasound manager unavailable");
                originalDepthCm = manager.Depth_cm;

                Transform[] sceneTransforms = Resources.FindObjectsOfTypeAll<Transform>()
                    .Where(item => item != null && item.gameObject.scene == gameObject.scene)
                    .ToArray();
                Renderer lfT2 = FindG2R2LfRenderer(sceneTransforms, "LF T2");
                Renderer lfT8 = FindG2R2LfRenderer(sceneTransforms, "LF T8");
                Vector3 corridorAxis = (lfT8.bounds.center - lfT2.bounds.center).normalized;
                SweepCountReferenceSet references = BuildSweepCountReferences();
                Vector3 centralProposal = 0.5f
                    * (references.references[0].contactPoint + references.references[6].contactPoint);
                RaycastHit homeHit = FindG2R2OffsetSurface(
                    centralProposal, corridorAxis, G2R5LateralOffsetMm, "fixed unlabeled Home");

                active2DStepMm = protocol.movementStepMm;
                sweepCountYawDeg = 90f;
                if (!PlaceSweepCountProbe(homeHit.point, homeHit.normal, corridorAxis,
                        out string placementFailure))
                    throw new InvalidOperationException("G2-R5 Home placement failed: " + placementFailure);
                active2DCaudalAxis = Vector3.ProjectOnPlane(
                    corridorAxis, CurrentSurfaceNormal()).normalized;
                active2DLateralAxis = Vector3.Cross(
                    CurrentSurfaceNormal(), active2DCaudalAxis).normalized;
                caudalTangent = active2DCaudalAxis;
                float coverageCenter = 0.5f
                    * (protocol.safeActorCoverageMm[0] + protocol.safeActorCoverageMm[1]);
                float coverageHalf = 0.5f
                    * (protocol.safeActorCoverageMm[1] - protocol.safeActorCoverageMm[0]);
                active2DWorkspaceOrigin = faceCenter.position + active2DCaudalAxis * coverageCenter;
                active2DLongitudinalHalfExtentMm = coverageHalf + 2f * protocol.movementStepMm;
                active2DLateralHalfExtentMm = Mathf.Max(originalLateralExtent, 10f);
                active2DRejectedMoves = 0;
                active2DContactViolations = 0;
                active2DWorkspaceViolations = 0;

                Directory.CreateDirectory(frameRoot);
                Directory.CreateDirectory(provenanceRoot);
                actor = new G2R5ActorTrace
                {
                    generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    protocolSha256 = Sha256Bytes(File.ReadAllBytes(protocolPath)),
                    configuredStepMm = protocol.movementStepMm,
                    configuredLateralOffsetMm = G2R5LateralOffsetMm
                };
                evaluator = new G2R5EvaluatorTrace
                {
                    generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    homeContactPoint = faceCenter.position,
                    corridorAxis = corridorAxis,
                    sceneSha256 = Sha256Bytes(File.ReadAllBytes(
                        Path.Combine(Application.dataPath,
                            "ThoracicUSCoach/Scenes/ThoracicUSCoach_POC.unity")))
                };
                summary = new G2R5Summary
                {
                    generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    selectedRegions = protocol.regions.Count
                };
                g2R5ActorRelativeMm = 0f;
                g2R5ImageHashes = new HashSet<string>();

                foreach (G2R5Region region in protocol.regions.OrderBy(item => item.windowMm[0]))
                {
                    yield return NavigateG2R5(actor, region.windowMm[0], 90f,
                        "TRANSIT_TO_REGION", region.regionId, string.Empty);
                    foreach (G2R5Variant variant in region.variants)
                    {
                        yield return AlignG2R5Yaw(actor, variant.surfaceYawDeg, region);
                        yield return ConfigureG2R5Depth(manager, variant.scanDepthMm);
                        yield return CaptureG2R5Observation(manager, actor, evaluator,
                            frameRoot, provenanceRoot, region, variant, "FORWARD", "WINDOW_BASELINE");
                        yield return ScanG2R5Window(manager, actor, evaluator, frameRoot,
                            provenanceRoot, region, variant, region.windowMm[1],
                            Active2DAction.Caudal, "FORWARD");
                        yield return ScanG2R5Window(manager, actor, evaluator, frameRoot,
                            provenanceRoot, region, variant, region.windowMm[0],
                            Active2DAction.Cranial, "REVERSE");
                    }
                }
                yield return ConfigureG2R5Depth(manager, 50);
                yield return NavigateG2R5(actor, 0f, 90f,
                    "PHYSICAL_RETURN_HOME", string.Empty, string.Empty);
                bool returnedHome = Mathf.Abs(g2R5ActorRelativeMm) <= G2R5PositionToleranceMm;
                if (!returnedHome)
                    throw new InvalidOperationException("G2-R5 physical return to Home failed");
                actor.finalActorRelativeMm = g2R5ActorRelativeMm;
                actor.status = "DIAGNOSTIC_CAPTURE_COMPLETE_PENDING_ACTOR_FUSION";
                summary.status = actor.status;
                summary.returnedHome = true;
                g2R5Status = actor.status;
            }
            finally
            {
                if (manager != null && originalDepthCm > 0) manager.Depth_cm = originalDepthCm;
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
                if (actor != null)
                {
                    actor.acceptedMovements = actor.movements.Count(item => item.accepted);
                    actor.rejectedMovements = actor.movements.Count(item => !item.accepted);
                    actor.freshFrames = actor.observations.Count;
                    actor.duplicateImageHashes = actor.observations.Count(
                        item => item.duplicateImageHash);
                }
                if (summary != null)
                {
                    summary.acceptedMovements = actor?.acceptedMovements ?? 0;
                    summary.rejectedMovements = actor?.rejectedMovements ?? 0;
                    summary.freshFrames = actor?.freshFrames ?? 0;
                    summary.duplicateImageHashes = actor?.duplicateImageHashes ?? 0;
                    summary.originalSettingsRestored = true;
                }
                WriteG2R5Artifacts(outputRoot, actor, evaluator, summary);
                g2R5Routine = null;
                g2R5Complete = true;
            }
        }

        private static void ValidateG2R5Protocol(G2R5Protocol protocol)
        {
            if (protocol == null || protocol.regions == null || protocol.regions.Count != 3)
                throw new InvalidOperationException("G2-R5 requires exactly three actor-selected regions");
            if (protocol.safeActorCoverageMm == null || protocol.safeActorCoverageMm.Length != 2
                || protocol.safeActorCoverageMm[0] >= protocol.safeActorCoverageMm[1])
                throw new InvalidOperationException("G2-R5 actor coverage is invalid");
            if (Mathf.Abs(protocol.movementStepMm - 1.5f) > 1e-4f)
                throw new InvalidOperationException("G2-R5 movement step must remain 1.5 mm");
            if (protocol.estimatedFreshFrames > G2R5MaximumFreshFrames
                || protocol.maximumFreshFrames > G2R5MaximumFreshFrames)
                throw new InvalidOperationException("G2-R5 fresh-frame budget exceeds 240");
            foreach (G2R5Region region in protocol.regions)
            {
                if (region.windowMm == null || region.windowMm.Length != 2
                    || region.windowMm[0] >= region.windowMm[1]
                    || region.windowMm[0] < protocol.safeActorCoverageMm[0]
                    || region.windowMm[1] > protocol.safeActorCoverageMm[1])
                    throw new InvalidOperationException("G2-R5 window is outside actor coverage");
                if (region.variants == null || region.variants.Count < 2 || region.variants.Count > 3)
                    throw new InvalidOperationException("G2-R5 region variant count is invalid");
                foreach (G2R5Variant variant in region.variants)
                {
                    if (!(Mathf.Approximately(variant.surfaceYawDeg, 90f)
                            || Mathf.Approximately(variant.surfaceYawDeg, 91f)))
                        throw new InvalidOperationException("G2-R5 yaw must be 90 or 91 degrees");
                    if (variant.scanDepthMm != 50 && variant.scanDepthMm != 60)
                        throw new InvalidOperationException("G2-R5 depth must be 50 or 60 mm");
                }
            }
        }

        private IEnumerator NavigateG2R5(
            G2R5ActorTrace actor, float targetMm, float yawDeg,
            string phase, string regionId, string variantId)
        {
            int guard = 0;
            while (Mathf.Abs(targetMm - g2R5ActorRelativeMm) > G2R5PositionToleranceMm)
            {
                float error = targetMm - g2R5ActorRelativeMm;
                Active2DAction action = error > 0f ? Active2DAction.Caudal : Active2DAction.Cranial;
                float predicted = g2R5ActorRelativeMm
                    + (action == Active2DAction.Caudal ? active2DStepMm : -active2DStepMm);
                if (Mathf.Abs(targetMm - predicted) >= Mathf.Abs(error)) break;
                ExecuteG2R5Movement(actor, action, yawDeg, phase, regionId, variantId);
                guard++;
                if (guard > G2R5MaximumMovements || actor.movements.Count > G2R5MaximumMovements)
                    throw new InvalidOperationException("G2-R5 movement budget exhausted");
                yield return null;
            }
        }

        private IEnumerator AlignG2R5Yaw(
            G2R5ActorTrace actor, float yawDeg, G2R5Region region)
        {
            float currentYaw = MeasureSweepCountSurfaceYawDeg(CurrentSurfaceNormal());
            if (Mathf.Abs(Mathf.DeltaAngle(currentYaw, yawDeg)) <= 0.25f) yield break;
            Active2DAction first = g2R5ActorRelativeMm + active2DStepMm <= region.windowMm[1]
                ? Active2DAction.Caudal : Active2DAction.Cranial;
            Active2DAction second = first == Active2DAction.Caudal
                ? Active2DAction.Cranial : Active2DAction.Caudal;
            ExecuteG2R5Movement(actor, first, yawDeg, "YAW_ALIGNMENT",
                region.regionId, string.Empty);
            yield return null;
            ExecuteG2R5Movement(actor, second, yawDeg, "YAW_ALIGNMENT_RETURN",
                region.regionId, string.Empty);
            yield return null;
        }

        private IEnumerator ConfigureG2R5Depth(UltrasoundManager manager, int depthMm)
        {
            int targetCm = depthMm / 10;
            if (manager.Depth_cm == targetCm) yield break;
            manager.Depth_cm = targetCm;
            yield return null;
            yield return null;
            if (manager.Depth_cm != targetCm)
                throw new InvalidOperationException("G2-R5 ultrasound depth did not update");
        }

        private IEnumerator ScanG2R5Window(
            UltrasoundManager manager,
            G2R5ActorTrace actor,
            G2R5EvaluatorTrace evaluator,
            string frameRoot,
            string provenanceRoot,
            G2R5Region region,
            G2R5Variant variant,
            float targetMm,
            Active2DAction action,
            string direction)
        {
            int guard = 0;
            while (Mathf.Abs(targetMm - g2R5ActorRelativeMm) > G2R5PositionToleranceMm)
            {
                float error = targetMm - g2R5ActorRelativeMm;
                float predicted = g2R5ActorRelativeMm
                    + (action == Active2DAction.Caudal ? active2DStepMm : -active2DStepMm);
                if (Mathf.Abs(targetMm - predicted) >= Mathf.Abs(error)) break;
                ExecuteG2R5Movement(actor, action, variant.surfaceYawDeg,
                    "LOCAL_SCAN_" + direction, region.regionId, variant.variantId);
                yield return CaptureG2R5Observation(manager, actor, evaluator,
                    frameRoot, provenanceRoot, region, variant, direction, "LOCAL_SCAN");
                guard++;
                if (guard > 32 || actor.movements.Count > G2R5MaximumMovements)
                    throw new InvalidOperationException("G2-R5 local scan movement budget exhausted");
            }
        }

        private void ExecuteG2R5Movement(
            G2R5ActorTrace actor,
            Active2DAction action,
            float yawDeg,
            string phase,
            string regionId,
            string variantId)
        {
            float before = g2R5ActorRelativeMm;
            BeginActive2DActionExecution(action);
            bool accepted = action == Active2DAction.Caudal
                ? TryActive2DMove(active2DCaudalAxis, yawDeg)
                : TryActive2DMove(-active2DCaudalAxis, yawDeg);
            Active2DActionExecution execution = active2DLastActionExecution;
            if (accepted)
            {
                float sign = action == Active2DAction.Caudal ? 1f : -1f;
                g2R5ActorRelativeMm += sign * execution.achievedDisplacementMm;
            }
            actor.movements.Add(new G2R5Movement
            {
                movementIndex = actor.movements.Count,
                phase = phase,
                regionId = regionId ?? string.Empty,
                variantId = variantId ?? string.Empty,
                requestedAction = action.ToString().ToUpperInvariant(),
                requestedDisplacementMm = execution.requestedDisplacementMm,
                achievedDisplacementMm = execution.achievedDisplacementMm,
                actorRelativeBeforeMm = before,
                actorRelativeAfterMm = g2R5ActorRelativeMm,
                commandedYawDeg = yawDeg,
                accepted = accepted,
                contactValid = execution.contactValidAfterExecution,
                workspaceValid = execution.workspaceValidAfterExecution,
                rollbackAttempted = execution.rollbackAttempted,
                rollbackContactRestored = execution.rollbackContactRestored,
                safetyFailure = execution.safetyFailure,
                rejectionReason = execution.rejectionReason
            });
            if (!accepted || execution.safetyFailure || !execution.contactValidAfterExecution
                || !execution.workspaceValidAfterExecution)
                throw new InvalidOperationException(
                    "G2-R5 stopped after rejected or unsafe movement: " + execution.rejectionReason);
        }

        private IEnumerator CaptureG2R5Observation(
            UltrasoundManager manager,
            G2R5ActorTrace actor,
            G2R5EvaluatorTrace evaluator,
            string frameRoot,
            string provenanceRoot,
            G2R5Region region,
            G2R5Variant variant,
            string direction,
            string phase)
        {
            if (actor.observations.Count >= G2R5MaximumFreshFrames)
                throw new InvalidOperationException("G2-R5 fresh-frame budget exhausted");
            int index = actor.observations.Count;
            string stem = "frame_" + index.ToString("D4", CultureInfo.InvariantCulture);
            DirectFreshPoseToken token = BeginDirectFreshCapturePose("g2_r5_" + stem, true);
            DirectFreshCaptureResult capture = new DirectFreshCaptureResult();
            yield return CaptureDirectProducerFreshFrame(token,
                "g2_r5_targeted_multiview", capture, true);
            if (!M0BFrameIntegrityValid(capture.image, capture.pngBytes))
                throw new InvalidOperationException("G2-R5 frame integrity failed at " + stem);
            if (!manager.TryGetPublishedScanProvenance(token.poseEpoch, capture.producerScanSerial,
                    out UltrasoundManager.PublishedScanProvenanceSnapshot provenance))
                throw new InvalidOperationException("G2-R5 provenance missing at " + stem);
            string imageFile = stem + ".png";
            string provenanceFile = stem + ".json";
            File.WriteAllBytes(Path.Combine(frameRoot, imageFile), capture.pngBytes);
            WriteSweepCountJson(Path.Combine(provenanceRoot, provenanceFile), provenance);
            string hash = Sha256Bytes(capture.pngBytes);
            bool duplicate = !g2R5ImageHashes.Add(hash);
            G2R5Observation observation = new G2R5Observation
            {
                observationIndex = index,
                movementIndexAtCapture = actor.movements.Count - 1,
                phase = phase,
                regionId = region.regionId,
                variantId = variant.variantId,
                direction = direction,
                signedActorRelativeMm = g2R5ActorRelativeMm,
                surfaceYawDeg = variant.surfaceYawDeg,
                scanDepthMm = manager.Depth_cm * 10,
                scanTopWidthMm = manager.Width,
                scanFanAngleDeg = manager.Angle,
                renderedFootprintWidthMm = manager.Width + 2f * manager.Depth_cm * 10f
                    * Mathf.Sin(manager.Angle * Mathf.Deg2Rad),
                image = "frames/" + imageFile,
                imageSha256 = hash,
                duplicateImageHash = duplicate,
                imageWidth = capture.image.width,
                imageHeight = capture.image.height,
                frameIntegrityValid = true,
                contactValid = MeasureSweepCountContact(),
                workspaceValid = IsActive2DWorkspaceValid(faceCenter.position),
                poseEpoch = capture.poseEpoch,
                poseAppliedUnityFrame = capture.poseAppliedUnityFrame,
                producerPoseEpoch = capture.producerPoseEpoch,
                producerUnityFrame = capture.producerUnityFrame,
                producerScanSerial = capture.producerScanSerial,
                captureUnityFrame = capture.captureUnityFrame,
                activeAggregatorCount = capture.activeAggregatorCount
            };
            actor.observations.Add(observation);

            Vector3 normal = CurrentSurfaceNormal();
            G2R5PrivilegedObservation privileged = new G2R5PrivilegedObservation
            {
                observationIndex = index,
                probePosition = capture.capturedPosition,
                probeRotation = capture.capturedRotation,
                contactPoint = faceCenter.position,
                surfaceNormal = normal,
                evaluatorLongitudinalCoordinateMm = Vector3.Dot(
                    faceCenter.position - evaluator.homeContactPoint, evaluator.corridorAxis),
                achievedSurfaceYawDeg = MeasureSweepCountSurfaceYawDeg(normal),
                surfaceYawErrorDeg = Mathf.DeltaAngle(
                    variant.surfaceYawDeg, MeasureSweepCountSurfaceYawDeg(normal)),
                normalAlignmentErrorDeg = Vector3.Angle(
                    (rearCenter.position - faceCenter.position).normalized, normal),
                provenance = "evaluator_only/provenance/" + provenanceFile,
                provenanceMatchedPublishedFrame = provenance.poseEpoch == capture.producerPoseEpoch
                    && provenance.producerUnityFrame == capture.producerUnityFrame
                    && provenance.producerScanSerial == capture.producerScanSerial,
                beamCount = provenance.beamCount
            };
            evaluator.observations.Add(privileged);
            if (!observation.contactValid || !observation.workspaceValid
                || !privileged.provenanceMatchedPublishedFrame
                || Mathf.Abs(privileged.surfaceYawErrorDeg) > SweepCountYawToleranceDeg)
                throw new InvalidOperationException(
                    "G2-R5 capture safety, yaw, freshness, or provenance failed at " + stem);
        }

        private void WriteG2R5Artifacts(
            string outputRoot,
            G2R5ActorTrace actor,
            G2R5EvaluatorTrace evaluator,
            G2R5Summary summary)
        {
            if (actor != null)
            {
                WriteSweepCountJson(Path.Combine(outputRoot, "trajectory_actor.json"), actor);
                WriteSweepCountJson(Path.Combine(outputRoot, "frame_manifest.json"),
                    new G2R5FrameManifest
                {
                    frames = actor.observations
                });
                WriteSweepCountJson(Path.Combine(outputRoot, "movement_trace.json"),
                    new G2R5MovementTrace
                {
                    movements = actor.movements
                });
            }
            if (evaluator != null)
                WriteSweepCountJson(Path.Combine(
                    outputRoot, "evaluator_only", "trajectory_privileged.json"), evaluator);
            if (summary != null)
                WriteSweepCountJson(Path.Combine(outputRoot, "session_summary.json"), summary);
        }
    }
}
