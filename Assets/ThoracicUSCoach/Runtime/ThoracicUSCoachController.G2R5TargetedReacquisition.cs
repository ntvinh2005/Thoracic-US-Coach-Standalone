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
        private const string G2R5Completed = "COMPLETED";
        private const string G2R5InconclusiveAcquisition = "INCONCLUSIVE_ACQUISITION";
        private const string G2R5AbortedSafety = "ABORTED_SAFETY";
        private const string G2R5AbortedInfrastructure = "ABORTED_INFRASTRUCTURE";
        private const string G2R5RecoveryFailed = "RECOVERY_FAILED";
        private const float G2R61MinimumCentralPixelMae = 0.01f;
        private const float G2R61MinimumLocalGradientMae = 0.005f;
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
            public float minimumInitialCrossPlanePixelMae;
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
            public float signedScanWidthAgainstTravelAxis;
            public float scanWidthAgainstTravelAxis;
            public float longitudinalFootprintWidthMm;
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
            public bool alternativeViewQualityEvaluated;
            public float globalPixelMeanAbsoluteDifference;
            public float centralPixelMeanAbsoluteDifference;
            public float localGradientMeanAbsoluteDifference;
            public bool structurallyInformativeView;
            public string viewQualityDecision;
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
            public string protocolContract;
            public float configuredStepMm;
            public float configuredLateralOffsetMm;
            public float finalActorRelativeMm;
            public int acceptedMovements;
            public int rejectedMovements;
            public int freshFrames;
            public int duplicateImageHashes;
            public float initialCrossPlanePixelMeanAbsoluteDifference;
            public bool initialCrossPlaneQualityCheckPassed;
            public bool recoveryAttempted;
            public bool recoverySucceeded;
            public string recoveryFailureReason;
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
            public bool recoveryAttempted;
            public bool recoverySucceeded;
            public bool originalSettingsRestored;
            public string stopReason;
            public string recoveryFailureReason;
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

        private sealed class G2R5RunContext
        {
            public string outputRoot;
            public G2R5ActorTrace actor;
            public G2R5EvaluatorTrace evaluator;
            public G2R5Summary summary;
            public UltrasoundManager manager;
            public float originalStep;
            public float originalLongitudinalExtent;
            public float originalLateralExtent;
            public float originalSweepYaw;
            public Vector3 originalWorkspaceOrigin;
            public Vector3 originalCaudalAxis;
            public Vector3 originalLateralAxis;
            public int originalDepthCm;
            public bool diagnosticPoseInitialized;
        }

        private Coroutine g2R5Routine;
        private bool g2R5Complete;
        private string g2R5Failure = string.Empty;
        private string g2R5Status = "IDLE";
        private float g2R5ActorRelativeMm;
        private HashSet<string> g2R5ImageHashes = new();
        private int g2R5RunMaximumFreshFrames = G2R5MaximumFreshFrames;
        private float g2R5MinimumInitialCrossPlanePixelMae;
        private Color32[] g2R5InitialLongitudinalPixels;
        private int g2R5InitialLongitudinalWidth;
        private int g2R5InitialLongitudinalHeight;
        private string g2R5InitialQualityRegionId = string.Empty;
        private G2R5RunContext g2R5ActiveContext;
        private bool g2R5StopRequested;
        private string g2R5RequestedStopStatus = string.Empty;
        private string g2R5RequestedStopReason = string.Empty;

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
            g2R5ActiveContext = null;
            g2R5StopRequested = false;
            g2R5RequestedStopStatus = string.Empty;
            g2R5RequestedStopReason = string.Empty;
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
            while (stack.Count > 0)
            {
                try { (stack.Pop() as IDisposable)?.Dispose(); }
                catch (Exception) { }
            }
            if (failure != null)
            {
                g2R5Failure = failure.ToString();
                string failureStatus = ClassifyG2R5Failure(failure);
                g2R5Status = failureStatus;
                if (g2R5ActiveContext?.actor != null)
                {
                    g2R5ActiveContext.actor.status = failureStatus;
                    g2R5ActiveContext.actor.stopReason = failure.Message;
                }
                if (g2R5ActiveContext?.summary != null)
                {
                    g2R5ActiveContext.summary.status = failureStatus;
                    g2R5ActiveContext.summary.stopReason = failure.Message;
                }
                Exception recoveryFailure = null;
                bool recoveryAttempted = g2R5ActiveContext != null
                    && g2R5ActiveContext.diagnosticPoseInitialized
                    && g2R5ActiveContext.actor != null;
                if (recoveryAttempted)
                {
                    g2R5ActiveContext.actor.recoveryAttempted = true;
                    g2R5ActiveContext.summary.recoveryAttempted = true;
                    Stack<IEnumerator> recoveryStack = new Stack<IEnumerator>();
                    recoveryStack.Push(RecoverG2R5PhysicallyToHome(g2R5ActiveContext.actor));
                    while (recoveryStack.Count > 0)
                    {
                        IEnumerator current = recoveryStack.Peek();
                        bool advanced;
                        object yielded = null;
                        try
                        {
                            advanced = current.MoveNext();
                            if (advanced) yielded = current.Current;
                        }
                        catch (Exception exception)
                        {
                            recoveryFailure = exception;
                            break;
                        }
                        if (!advanced)
                        {
                            recoveryStack.Pop();
                            (current as IDisposable)?.Dispose();
                        }
                        else if (yielded is IEnumerator nested) recoveryStack.Push(nested);
                        else yield return yielded;
                    }
                    while (recoveryStack.Count > 0)
                    {
                        try { (recoveryStack.Pop() as IDisposable)?.Dispose(); }
                        catch (Exception) { }
                    }
                }
                bool recovered = recoveryAttempted && recoveryFailure == null
                    && Mathf.Abs(g2R5ActorRelativeMm) <= G2R5PositionToleranceMm;
                if (g2R5ActiveContext?.actor != null)
                {
                    g2R5ActiveContext.actor.recoverySucceeded = recovered;
                    g2R5ActiveContext.summary.recoverySucceeded = recovered;
                    g2R5ActiveContext.summary.returnedHome = recovered;
                }
                if (recoveryFailure != null || (recoveryAttempted && !recovered))
                {
                    string recoveryReason = recoveryFailure?.Message
                        ?? "Bounded physical recovery did not reach Home";
                    g2R5ActiveContext.actor.status = G2R5RecoveryFailed;
                    g2R5ActiveContext.actor.recoveryFailureReason = recoveryReason;
                    g2R5ActiveContext.summary.status = G2R5RecoveryFailed;
                    g2R5ActiveContext.summary.recoveryFailureReason = recoveryReason;
                    g2R5Status = G2R5RecoveryFailed;
                }
                Debug.LogError("[ThoracicUSCoach][G2R5] " + failure);
            }
            FinalizeG2R5Run(g2R5ActiveContext);
        }

        private static string ClassifyG2R5Failure(Exception failure)
        {
            string message = failure?.Message ?? string.Empty;
            return message.IndexOf("unsafe", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("contact", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("workspace", StringComparison.OrdinalIgnoreCase) >= 0
                || message.IndexOf("rejected", StringComparison.OrdinalIgnoreCase) >= 0
                ? G2R5AbortedSafety : G2R5AbortedInfrastructure;
        }

        private IEnumerator RecoverG2R5PhysicallyToHome(G2R5ActorTrace actor)
        {
            if (!MeasureSweepCountContact() || !IsActive2DWorkspaceValid(faceCenter.position))
                throw new InvalidOperationException(
                    "G2-R5 physical recovery cannot start from invalid contact or workspace");
            yield return NavigateG2R5(actor, 0f, 90f,
                "PHYSICAL_FAILURE_RECOVERY_HOME", string.Empty, string.Empty);
            if (Mathf.Abs(g2R5ActorRelativeMm) > G2R5PositionToleranceMm)
                throw new InvalidOperationException("G2-R5 bounded physical recovery missed Home");
        }

        private IEnumerator RunG2R5Core()
        {
            AutoWire();
            string protocolPath = Path.GetFullPath(g2R5ProtocolPathOverride);
            if (!File.Exists(protocolPath))
                throw new FileNotFoundException("G2-R5 actor protocol does not exist", protocolPath);
            G2R5Protocol protocol = JsonUtility.FromJson<G2R5Protocol>(File.ReadAllText(protocolPath));
            ValidateG2R5Protocol(protocol);
            g2R5RunMaximumFreshFrames = protocol.maximumFreshFrames;
            g2R5MinimumInitialCrossPlanePixelMae = protocol.minimumInitialCrossPlanePixelMae;
            g2R5InitialLongitudinalPixels = null;
            g2R5InitialQualityRegionId = string.Empty;
            string outputRoot = Path.GetFullPath(protocol.outputRoot);
            string frameRoot = Path.Combine(outputRoot, "frames");
            string evaluatorRoot = Path.Combine(outputRoot, "evaluator_only");
            string provenanceRoot = Path.Combine(evaluatorRoot, "provenance");
            if (File.Exists(Path.Combine(outputRoot, "trajectory_actor.json"))
                || Directory.Exists(frameRoot) || Directory.Exists(provenanceRoot))
                throw new InvalidOperationException("G2-R5 output exists; refusing to overwrite it");

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
            g2R5ActiveContext = new G2R5RunContext
            {
                outputRoot = outputRoot,
                originalStep = originalStep,
                originalLongitudinalExtent = originalLongitudinalExtent,
                originalLateralExtent = originalLateralExtent,
                originalSweepYaw = originalSweepYaw,
                originalWorkspaceOrigin = originalWorkspaceOrigin,
                originalCaudalAxis = originalCaudalAxis,
                originalLateralAxis = originalLateralAxis
            };
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
                g2R5ActiveContext.manager = manager;
                g2R5ActiveContext.originalDepthCm = originalDepthCm;

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
                g2R5ActiveContext.diagnosticPoseInitialized = true;
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
                    mode = protocol.mode,
                    protocolSha256 = Sha256Bytes(File.ReadAllBytes(protocolPath)),
                    protocolContract = protocol.contract,
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
                g2R5ActiveContext.actor = actor;
                g2R5ActiveContext.evaluator = evaluator;
                g2R5ActiveContext.summary = summary;
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
                        if (g2R5StopRequested) break;
                        yield return ScanG2R5Window(manager, actor, evaluator, frameRoot,
                            provenanceRoot, region, variant, region.windowMm[1],
                            Active2DAction.Caudal, "FORWARD");
                        if (g2R5StopRequested) break;
                        yield return ScanG2R5Window(manager, actor, evaluator, frameRoot,
                            provenanceRoot, region, variant, region.windowMm[0],
                            Active2DAction.Cranial, "REVERSE");
                        if (g2R5StopRequested) break;
                    }
                    if (g2R5StopRequested) break;
                }
                yield return ConfigureG2R5Depth(manager, 50);
                yield return NavigateG2R5(actor, 0f, 90f,
                    "PHYSICAL_RETURN_HOME", string.Empty, string.Empty);
                bool returnedHome = Mathf.Abs(g2R5ActorRelativeMm) <= G2R5PositionToleranceMm;
                if (!returnedHome)
                    throw new InvalidOperationException("G2-R5 physical return to Home failed");
                actor.finalActorRelativeMm = g2R5ActorRelativeMm;
                actor.status = g2R5StopRequested
                    ? g2R5RequestedStopStatus : G2R5Completed;
                actor.stopReason = g2R5StopRequested ? g2R5RequestedStopReason : string.Empty;
                summary.status = actor.status;
                summary.stopReason = actor.stopReason;
                summary.returnedHome = true;
                g2R5Status = actor.status;
            }
        }

        private void FinalizeG2R5Run(G2R5RunContext context)
        {
            if (context != null)
            {
                if (context.manager != null && context.originalDepthCm > 0)
                    context.manager.Depth_cm = context.originalDepthCm;
                active2DStepMm = context.originalStep;
                active2DLongitudinalHalfExtentMm = context.originalLongitudinalExtent;
                active2DLateralHalfExtentMm = context.originalLateralExtent;
                sweepCountYawDeg = context.originalSweepYaw;
                active2DWorkspaceOrigin = context.originalWorkspaceOrigin;
                active2DCaudalAxis = context.originalCaudalAxis;
                active2DLateralAxis = context.originalLateralAxis;
                if (context.actor != null)
                {
                    context.actor.finalActorRelativeMm = g2R5ActorRelativeMm;
                    context.actor.acceptedMovements = context.actor.movements.Count(
                        item => item.accepted);
                    context.actor.rejectedMovements = context.actor.movements.Count(
                        item => !item.accepted);
                    context.actor.freshFrames = context.actor.observations.Count;
                    context.actor.duplicateImageHashes = context.actor.observations.Count(
                        item => item.duplicateImageHash);
                    g2R5Status = context.actor.status;
                }
                if (context.summary != null)
                {
                    context.summary.acceptedMovements = context.actor?.acceptedMovements ?? 0;
                    context.summary.rejectedMovements = context.actor?.rejectedMovements ?? 0;
                    context.summary.freshFrames = context.actor?.freshFrames ?? 0;
                    context.summary.duplicateImageHashes = context.actor?.duplicateImageHashes ?? 0;
                    context.summary.originalSettingsRestored = true;
                }
                WriteG2R5Artifacts(
                    context.outputRoot, context.actor, context.evaluator, context.summary);
            }
            g2R5RunMaximumFreshFrames = G2R5MaximumFreshFrames;
            g2R5MinimumInitialCrossPlanePixelMae = 0f;
            g2R5InitialLongitudinalPixels = null;
            g2R5InitialQualityRegionId = string.Empty;
            g2R5Routine = null;
            g2R5Complete = true;
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
            bool repairedCrossPlane = protocol.mode
                == "ACTOR_GUIDED_REPAIRED_CROSS_PLANE_DISCRIMINATION";
            bool crossPlane = protocol.mode == "ACTOR_GUIDED_CROSS_PLANE_DISCRIMINATION"
                || repairedCrossPlane;
            if (crossPlane && (protocol.estimatedFreshFrames > 160
                    || protocol.maximumFreshFrames > 160))
                throw new InvalidOperationException("G2-R6 fresh-frame budget exceeds 160");
            if (crossPlane && (protocol.minimumInitialCrossPlanePixelMae < 0.01f
                    || protocol.minimumInitialCrossPlanePixelMae > 0.25f))
                throw new InvalidOperationException("G2-R6 initial image-change criterion is invalid");
            foreach (G2R5Region region in protocol.regions)
            {
                if (region.windowMm == null || region.windowMm.Length != 2
                    || region.windowMm[0] >= region.windowMm[1]
                    || region.windowMm[0] < protocol.safeActorCoverageMm[0]
                    || region.windowMm[1] > protocol.safeActorCoverageMm[1])
                    throw new InvalidOperationException("G2-R5 window is outside actor coverage");
                if (region.variants == null || region.variants.Count < 2 || region.variants.Count > 3)
                    throw new InvalidOperationException("G2-R5 region variant count is invalid");
                if (crossPlane && (region.variants.Count != 2
                        || !Mathf.Approximately(region.variants[0].surfaceYawDeg, 90f)
                        || (!repairedCrossPlane
                            && !Mathf.Approximately(region.variants[1].surfaceYawDeg, 60f))
                        || (repairedCrossPlane
                            && !Mathf.Approximately(region.variants[1].surfaceYawDeg, 65f))
                        || region.variants.Any(variant => variant.scanDepthMm != 60)))
                    throw new InvalidOperationException(
                        repairedCrossPlane
                            ? "G2-R6.1 requires frozen yaw 90/65 views at matched 60 mm depth"
                            : "G2-R6 requires frozen yaw 90/60 views at matched 60 mm depth");
                foreach (G2R5Variant variant in region.variants)
                {
                    if (!(Mathf.Approximately(variant.surfaceYawDeg, 90f)
                            || Mathf.Approximately(variant.surfaceYawDeg, 91f)
                            || (crossPlane && !repairedCrossPlane
                                && Mathf.Approximately(variant.surfaceYawDeg, 60f))
                            || (repairedCrossPlane
                                && Mathf.Approximately(variant.surfaceYawDeg, 65f))))
                        throw new InvalidOperationException("G2-R5/R6 yaw is outside the frozen set");
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
                if (g2R5StopRequested) yield break;
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
            if (actor.observations.Count >= g2R5RunMaximumFreshFrames)
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
            string hash = Sha256Bytes(capture.pngBytes);
            bool duplicate = !g2R5ImageHashes.Add(hash);
            float renderedFootprintWidthMm = manager.Width + 2f * manager.Depth_cm * 10f
                * Mathf.Sin(manager.Angle * Mathf.Deg2Rad);
            Vector3 surfaceNormal = CurrentSurfaceNormal();
            Vector3 scanWidthAxis = Vector3.ProjectOnPlane(probeRoot.right, surfaceNormal).normalized;
            float signedScanWidthAgainstTravelAxis = Vector3.Dot(
                scanWidthAxis, active2DCaudalAxis);
            float scanWidthAgainstTravelAxis = Mathf.Abs(signedScanWidthAgainstTravelAxis);
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
                renderedFootprintWidthMm = renderedFootprintWidthMm,
                signedScanWidthAgainstTravelAxis = signedScanWidthAgainstTravelAxis,
                scanWidthAgainstTravelAxis = scanWidthAgainstTravelAxis,
                longitudinalFootprintWidthMm = renderedFootprintWidthMm
                    * scanWidthAgainstTravelAxis,
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
            if (!observation.contactValid || !observation.workspaceValid
                || !privileged.provenanceMatchedPublishedFrame
                || Mathf.Abs(privileged.surfaceYawErrorDeg) > SweepCountYawToleranceDeg)
                throw new InvalidOperationException(
                    "G2-R5 capture safety, yaw, freshness, or provenance failed at " + stem);
            File.WriteAllBytes(Path.Combine(frameRoot, imageFile), capture.pngBytes);
            WriteSweepCountJson(Path.Combine(provenanceRoot, provenanceFile), provenance);
            actor.observations.Add(observation);
            evaluator.observations.Add(privileged);
            if (phase == "WINDOW_BASELINE" && g2R5MinimumInitialCrossPlanePixelMae > 0f)
                EvaluateG2R5InitialCrossPlaneImageChange(
                    capture.image, actor, observation, region, variant);
        }

        private void EvaluateG2R5InitialCrossPlaneImageChange(
            Texture2D image, G2R5ActorTrace actor, G2R5Observation observation,
            G2R5Region region, G2R5Variant variant)
        {
            if (g2R5InitialLongitudinalPixels == null
                && Mathf.Approximately(variant.surfaceYawDeg, 90f))
            {
                g2R5InitialLongitudinalPixels = image.GetPixels32();
                g2R5InitialLongitudinalWidth = image.width;
                g2R5InitialLongitudinalHeight = image.height;
                g2R5InitialQualityRegionId = region.regionId;
                return;
            }
            if (actor.initialCrossPlaneQualityCheckPassed
                || g2R5InitialQualityRegionId != region.regionId
                || Mathf.Approximately(variant.surfaceYawDeg, 90f)) return;
            observation.alternativeViewQualityEvaluated = true;
            if (image.width != g2R5InitialLongitudinalWidth
                || image.height != g2R5InitialLongitudinalHeight)
            {
                observation.viewQualityDecision = "INCONCLUSIVE_DIMENSION_MISMATCH";
                g2R5StopRequested = true;
                g2R5RequestedStopStatus = G2R5InconclusiveAcquisition;
                g2R5RequestedStopReason = "Cross-plane image dimensions changed after persistence";
                return;
            }
            Color32[] current = image.GetPixels32();
            double difference = 0.0;
            double centralDifference = 0.0;
            double gradientDifference = 0.0;
            int samples = 0;
            int centralSamples = 0;
            int gradientSamples = 0;
            for (int index = 0; index < current.Length; index += 16)
            {
                Color32 baseline = g2R5InitialLongitudinalPixels[index];
                Color32 oblique = current[index];
                difference += (Math.Abs(baseline.r - oblique.r)
                    + Math.Abs(baseline.g - oblique.g)
                    + Math.Abs(baseline.b - oblique.b)) / (3.0 * 255.0);
                samples++;
                int x = index % image.width;
                int y = index / image.width;
                if (x >= image.width / 4 && x < 3 * image.width / 4
                    && y >= image.height / 5 && y < 9 * image.height / 10)
                {
                    centralDifference += (Math.Abs(baseline.r - oblique.r)
                        + Math.Abs(baseline.g - oblique.g)
                        + Math.Abs(baseline.b - oblique.b)) / (3.0 * 255.0);
                    centralSamples++;
                    if (x + 1 < image.width)
                    {
                        Color32 baselineNext = g2R5InitialLongitudinalPixels[index + 1];
                        Color32 currentNext = current[index + 1];
                        double baselineGradient = Math.Abs(baseline.r - baselineNext.r) / 255.0;
                        double currentGradient = Math.Abs(oblique.r - currentNext.r) / 255.0;
                        gradientDifference += Math.Abs(baselineGradient - currentGradient);
                        gradientSamples++;
                    }
                }
            }
            actor.initialCrossPlanePixelMeanAbsoluteDifference = samples > 0
                ? (float)(difference / samples) : 0f;
            observation.globalPixelMeanAbsoluteDifference =
                actor.initialCrossPlanePixelMeanAbsoluteDifference;
            observation.centralPixelMeanAbsoluteDifference = centralSamples > 0
                ? (float)(centralDifference / centralSamples) : 0f;
            observation.localGradientMeanAbsoluteDifference = gradientSamples > 0
                ? (float)(gradientDifference / gradientSamples) : 0f;
            actor.initialCrossPlaneQualityCheckPassed = observation.structurallyInformativeView =
                observation.globalPixelMeanAbsoluteDifference >= g2R5MinimumInitialCrossPlanePixelMae
                || observation.centralPixelMeanAbsoluteDifference >= G2R61MinimumCentralPixelMae
                || observation.localGradientMeanAbsoluteDifference >= G2R61MinimumLocalGradientMae;
            observation.viewQualityDecision = observation.structurallyInformativeView
                ? "PERSISTED_INTERPRETABLE_FOR_OFFLINE_TOPOLOGY"
                : "PERSISTED_INCONCLUSIVE_ACQUISITION";
            if (!actor.initialCrossPlaneQualityCheckPassed)
            {
                g2R5StopRequested = true;
                g2R5RequestedStopStatus = G2R5InconclusiveAcquisition;
                g2R5RequestedStopReason =
                    "Persisted alternative view lacked predeclared global, central, and local-gradient change";
            }
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
