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
        private const string G2R2OutputRoot =
            "Artifacts/ThoracicUSCoach/sweep_count/G2_R2_CONTINUOUS_RECESS_MAPPING_20261101_DEV03";
        private const float G2R2StepMm = 1.5f;
        private const float G2R2LateralOffsetMm = -6f;
        private const float G2R2SurfaceYawDeg = 90f;
        private const float G2R2EndpointMarginMm = 4.5f;
        private const float G2R2ReturnToleranceMm = 3f;
        private const float G2R2GroundTruthFreeMechanicalExtentMm = 120f;
        private const float G2R2GroundTruthFreeBoundaryReserveMm = 6f;
        private const float G2R2MinimumRandomStartMm = 6f;
        private const float G2R2MaximumRandomStartMm = 60f;
        private const float G2R2MinimumRandomStartLateralMm = 3f;
        private const float G2R2MaximumRandomStartLateralMm = 12f;
        private const int G2R2MaximumStartupFrames = 120;
        private static string g2R2OutputRootOverride = string.Empty;
        private static float? g2R2SurfaceYawOverrideDeg;
        private static int? g2R2ScanTopWidthOverrideMm;
        private static bool g2R2RequirePhysicalFinalization;
        private static bool g2R2GroundTruthFreeExploration;
        private static float g2R2GroundTruthFreeHomeOffsetMm = G2R2LateralOffsetMm;
        private static bool g2R2RandomStartEnabled;
        private static int g2R2RandomStartSeed;
        private static float g2R2RandomStartMaximumMm = 30f;
        private static float g2R2RandomStartMaximumLateralMm = 12f;
        private static float g2R2RandomStartInitialHoldSeconds;
        private static float g2R2RandomStartStepDelaySeconds;
        private static float g2R2RandomStartHomeHoldSeconds;
        private static bool g2R2EpisodeHomeJitterEnabled;
        private static int g2R2EpisodeHomeJitterSeed;
        private static float g2R2EpisodeHomeJitterMaximumMm = 12f;
        private static bool g2R2EpisodeHomeLateralJitterEnabled;
        private static int g2R2EpisodeHomeLateralJitterSeed;
        private static float g2R2EpisodeHomeLateralJitterMaximumMm = 1.5f;
        private static float g2R2ActiveRelativeMm;
        private static float g2R2ActiveLateralMm;
        private static Action g2R2DeferredSettingsRestore;
        private G2R2ActorTrajectory g2R2ActiveActor;
        private G2R2PrivilegedTrajectory g2R2ActiveEvaluator;
        private G2R2RunSummary g2R2ActiveSummary;

        private static string G2R2ResolvedOutputRoot =>
            string.IsNullOrWhiteSpace(g2R2OutputRootOverride)
                ? G2R2OutputRoot
                : g2R2OutputRootOverride;

        private static float G2R2ResolvedSurfaceYawDeg =>
            g2R2SurfaceYawOverrideDeg ?? G2R2SurfaceYawDeg;

        public static int? G2R2ResolvedScanTopWidthMm =>
            g2R2ScanTopWidthOverrideMm;

        public static bool G2R2PhysicalFinalizationRequired =>
            g2R2RequirePhysicalFinalization;

        public static bool G2R2GroundTruthFreeExplorationEnabled =>
            g2R2GroundTruthFreeExploration;

        [Serializable]
        private sealed class G2R2AcquisitionSetup
        {
            public RaycastHit homeHit;
            public RaycastHit initialHit;
            public RaycastHit cranialHit;
            public RaycastHit caudalHit;
            public Vector3 corridorAxis;
            public float cranialCoordinate;
            public float caudalCoordinate;
            public float workspaceCenterOffset;
            public float workspaceHalfExtent;
            public int caudalMovementCount;
            public int cranialMovementCount;
            public float initialActorRelativeMm;
            public float initialActorLateralMm;
            public int initialHomeReturnMovementBudget;
            public int randomStartSeed;
            public bool randomStartEnabled;
            public int episodeHomeJitterSeed;
            public float episodeHomeJitterMm;
            public int episodeHomeLateralJitterSeed;
            public float episodeHomeLateralJitterMm;
            public string setupIsolation;
            public string decision;
        }

        [Serializable]
        private sealed class G2R2Preflight
        {
            public string contract = "thoracic_g2_r2_continuous_m6_preflight_v1";
            public string generatedUtc;
            public string status = "BLOCKED";
            public string setupIsolation =
                "Evaluator-only LF-associated recess endpoints define diagnostic coverage; no label or pose enters actor observations.";
            public float stepMm;
            public float lateralOffsetMm;
            public float surfaceYawDeg;
            public float endpointMarginMm;
            public int caudalOutboundMovementBudget;
            public int caudalReturnMovementBudget;
            public int cranialOutboundMovementBudget;
            public int cranialReturnMovementBudget;
            public float plannedCranialCoordinateMm;
            public float plannedCaudalCoordinateMm;
            public float diagnosticWorkspaceHalfExtentMm;
            public bool randomStartEnabled;
            public int randomStartSeed;
            public float plannedInitialActorRelativeMm;
            public float plannedInitialActorLateralMm;
            public int initialHomeReturnMovementBudget;
            public int episodeHomeJitterSeed;
            public float episodeHomeJitterMm;
            public int episodeHomeLateralJitterSeed;
            public float episodeHomeLateralJitterMm;
            public Vector3 evaluatorHomeContact;
            public Vector3 evaluatorCranialCoverageContact;
            public Vector3 evaluatorCaudalCoverageContact;
            public Vector3 evaluatorCorridorAxis;
            public string decision;
        }

        [Serializable]
        private sealed class G2R2ActorObservation
        {
            public int observationIndex;
            public string phase;
            public string requestedAction;
            public bool executedSuccessfully;
            public float requestedDisplacementMm;
            public float attemptedDisplacementMm;
            public float achievedDisplacementMm;
            public float signedActorRelativeMm;
            public float signedActorLateralMm;
            public float cumulativeExecutedTravelMm;
            public bool contactValidAfterExecution;
            public bool workspaceValidAfterExecution;
            public bool rollbackAttempted;
            public bool rollbackContactRestored;
            public bool safetyFailure;
            public string rejectionReason;
            public string image;
            public string imageSha256;
            public int imageWidth;
            public int imageHeight;
            public bool frameIntegrityValid;
            public long poseEpoch;
            public int poseAppliedUnityFrame;
            public long producerPoseEpoch;
            public int producerUnityFrame;
            public long producerScanSerial;
            public int captureUnityFrame;
            public int activeAggregatorCount;
        }

        [Serializable]
        private sealed class G2R2ActorTrajectory
        {
            public string contract = "thoracic_g2_r2_continuous_m6_actor_v1";
            public string generatedUtc;
            public string allowedInputs =
                "Clean ultrasound pixels, accepted action, achieved displacement, direction, and accumulated actor-relative movement.";
            public string excludedInputs =
                "T labels, world pose, LF or Skeleton identities, renderer provenance, target transforms, and evaluator coordinates.";
            public string scheduleDisclosure =
                "Evaluator-planned G2 diagnostic coverage; the future detector receives only this actor trace and referenced clean images.";
            public float configuredStepMm;
            public float commandedYawDeg;
            public float commandedLateralOffsetMm;
            public float scanTopWidthMm;
            public float scanDepthMm;
            public float scanFanAngleDeg;
            public float renderedFootprintWidthMm;
            public int movementBudget;
            public int acceptedMovements;
            public int rejectedMovements;
            public bool randomStartEnabled;
            public int randomStartSeed;
            public float initialActorRelativeMm;
            public float initialActorLateralMm;
            public int initialHomeReturnAcceptedMovements;
            public float homeArrivalResidualMm;
            public float homeArrivalLateralResidualMm;
            public bool initialHomeReturnCompleted;
            public int episodeHomeJitterSeed;
            public float episodeHomeJitterMm;
            public int episodeHomeLateralJitterSeed;
            public float episodeHomeLateralJitterMm;
            public string status = "RUNNING";
            public string stopReason;
            public List<G2R2ActorObservation> observations = new();
        }

        [Serializable]
        private sealed class G2R2PrivilegedObservation
        {
            public int observationIndex;
            public string phase;
            public Vector3 probePosition;
            public Quaternion probeRotation;
            public Vector3 contactPoint;
            public Vector3 surfaceNormal;
            public float evaluatorLongitudinalCoordinateMm;
            public float achievedSurfaceYawDeg;
            public float surfaceYawErrorDeg;
            public float normalAlignmentErrorDeg;
            public float scanWidthAgainstCorridor;
            public float signedContactDepthMm;
            public float contactFraction;
            public string provenance;
            public bool provenanceMatchedPublishedFrame;
            public int beamCount;
            public int beamsWithSkeletonBeforeOrAtFirstShadow;
            public int beamsWithLfBeforeOrAtFirstShadow;
        }

        [Serializable]
        private sealed class G2R2PrivilegedTrajectory
        {
            public string contract = "thoracic_g2_r2_continuous_m6_evaluator_v1";
            public string generatedUtc;
            public string warning = "Evaluator-only geometry and renderer provenance; never an actor input.";
            public string fixedHomeContract =
                "Repeatable central robotic setup; anatomically unlabeled to the actor.";
            public string diagnosticCranialCoverageReference = "LF_T2_ASSOCIATED_RECESS_MINUS_MARGIN";
            public string diagnosticCaudalCoverageReference = "LF_T8_ASSOCIATED_RECESS_PLUS_MARGIN";
            public Vector3 homeContactPoint;
            public Vector3 cranialCoverageContactPoint;
            public Vector3 caudalCoverageContactPoint;
            public Vector3 corridorAxis;
            public Vector3 originalWorkspaceOrigin;
            public Vector3 diagnosticWorkspaceOrigin;
            public float originalLongitudinalHalfExtentMm;
            public float diagnosticLongitudinalHalfExtentMm;
            public List<G2R2PrivilegedObservation> observations = new();
        }

        [Serializable]
        private sealed class G2R2FrameManifest
        {
            public string contract = "thoracic_g2_r2_continuous_m6_frame_manifest_v1";
            public string generatedUtc;
            public string inputIsolation =
                "No world pose, anatomy label, mesh identity, renderer provenance, or evaluator coordinate is present.";
            public List<G2R2ActorObservation> frames = new();
        }

        [Serializable]
        private sealed class G2R2RunSummary
        {
            public string contract = "thoracic_g2_r2_continuous_m6_summary_v1";
            public string generatedUtc;
            public string status = "RUNNING";
            public string g0 = "PASS_WITH_COMBINED_SKELETON_IDENTITY_LIMITATION";
            public string g1 = "PENDING";
            public string g2 = "PENDING_BLIND_ANALYSIS";
            public string g3 = "NOT_RUN";
            public string g4 = "NOT_RUN";
            public int acceptedMovements;
            public int rejectedMovements;
            public int validFrames;
            public float maximumYawErrorDeg;
            public float maximumNormalAlignmentErrorDeg;
            public float finalActorRelativeMm;
            public bool returnedWithinTolerance;
            public bool originalSettingsRestored;
            public string stopReason;
        }

        [Serializable]
        private sealed class G2R2RecoveryAction
        {
            public string action;
            public float achievedDisplacementMm;
            public float signedActorRelativeMm;
            public float signedActorLateralMm;
            public bool contactValidAfterExecution;
            public bool workspaceValidAfterExecution;
        }

        [Serializable]
        private sealed class G2R2RecoveryTrace
        {
            public string contract = "thoracic_g2_r2_physical_recovery_v1";
            public List<G2R2RecoveryAction> actions = new();
            public float finalActorRelativeMm;
            public float finalActorLateralMm;
            public bool recovered;
        }

        private Coroutine g2R2ContinuousRoutine;
        private bool g2R2ContinuousComplete;
        private string g2R2ContinuousFailure = string.Empty;
        private string g2R2ContinuousStatus = "IDLE";

        public bool G2R2ContinuousComplete => g2R2ContinuousComplete;
        public string G2R2ContinuousFailure => g2R2ContinuousFailure;
        public string G2R2ContinuousStatus => g2R2ContinuousStatus;

        public static void ConfigureG2R2ContinuousExperiment(
            string outputRoot, float surfaceYawDeg)
        {
            if (string.IsNullOrWhiteSpace(outputRoot))
                throw new ArgumentException("A non-empty output root is required", nameof(outputRoot));
            if (!float.IsFinite(surfaceYawDeg))
                throw new ArgumentOutOfRangeException(nameof(surfaceYawDeg));
            g2R2OutputRootOverride = outputRoot;
            g2R2SurfaceYawOverrideDeg = surfaceYawDeg;
        }

        public static void ConfigureG2R2ContinuousExperiment(
            string outputRoot, float surfaceYawDeg, int scanTopWidthMm,
            bool requirePhysicalFinalization)
        {
            ConfigureG2R2ContinuousExperiment(outputRoot, surfaceYawDeg);
            if (scanTopWidthMm <= 0)
                throw new ArgumentOutOfRangeException(nameof(scanTopWidthMm));
            g2R2ScanTopWidthOverrideMm = scanTopWidthMm;
            g2R2RequirePhysicalFinalization = requirePhysicalFinalization;
        }

        public static void ConfigureGroundTruthFreeThoracicExploration(
            string outputRoot, float surfaceYawDeg, int scanTopWidthMm)
        {
            ConfigureG2R2ContinuousExperiment(
                outputRoot, surfaceYawDeg, scanTopWidthMm, true);
            g2R2GroundTruthFreeExploration = true;
        }

        public static void ConfigureGroundTruthFreeThoracicExploration(
            string outputRoot, float surfaceYawDeg, int scanTopWidthMm,
            bool shiftOneFootprintMedial)
        {
            ConfigureGroundTruthFreeThoracicExploration(outputRoot, surfaceYawDeg,
                scanTopWidthMm, shiftOneFootprintMedial
                    ? G2R2LateralOffsetMm - 50f
                    : G2R2LateralOffsetMm);
        }

        public static void ConfigureGroundTruthFreeThoracicExploration(
            string outputRoot, float surfaceYawDeg, int scanTopWidthMm,
            float mechanicalHomeOffsetMm)
        {
            if (!float.IsFinite(mechanicalHomeOffsetMm)
                || mechanicalHomeOffsetMm < -60f || mechanicalHomeOffsetMm > 10f)
                throw new ArgumentOutOfRangeException(nameof(mechanicalHomeOffsetMm));
            ConfigureGroundTruthFreeThoracicExploration(
                outputRoot, surfaceYawDeg, scanTopWidthMm);
            g2R2GroundTruthFreeHomeOffsetMm = mechanicalHomeOffsetMm;
        }

        public static void ConfigureGroundTruthFreeRandomStartThoracicExploration(
            string outputRoot, float surfaceYawDeg, int scanTopWidthMm,
            float mechanicalHomeOffsetMm, int randomStartSeed,
            float maximumRandomStartMm)
        {
            ConfigureGroundTruthFreeRandomStartThoracicExploration(
                outputRoot, surfaceYawDeg, scanTopWidthMm, mechanicalHomeOffsetMm,
                randomStartSeed, maximumRandomStartMm,
                G2R2MaximumRandomStartLateralMm);
        }

        public static void ConfigureGroundTruthFreeRandomStartThoracicExploration(
            string outputRoot, float surfaceYawDeg, int scanTopWidthMm,
            float mechanicalHomeOffsetMm, int randomStartSeed,
            float maximumRandomStartMm, float maximumRandomStartLateralMm)
        {
            if (!float.IsFinite(maximumRandomStartMm)
                || maximumRandomStartMm < G2R2MinimumRandomStartMm
                || maximumRandomStartMm > G2R2MaximumRandomStartMm)
                throw new ArgumentOutOfRangeException(nameof(maximumRandomStartMm));
            if (!float.IsFinite(maximumRandomStartLateralMm)
                || maximumRandomStartLateralMm < G2R2MinimumRandomStartLateralMm
                || maximumRandomStartLateralMm > G2R2MaximumRandomStartLateralMm)
                throw new ArgumentOutOfRangeException(
                    nameof(maximumRandomStartLateralMm));
            ConfigureGroundTruthFreeThoracicExploration(
                outputRoot, surfaceYawDeg, scanTopWidthMm, mechanicalHomeOffsetMm);
            g2R2RandomStartEnabled = true;
            g2R2RandomStartSeed = randomStartSeed;
            g2R2RandomStartMaximumMm = maximumRandomStartMm;
            g2R2RandomStartMaximumLateralMm = maximumRandomStartLateralMm;
        }

        public static void ConfigureGroundTruthFreeEpisodeHomeJitter(
            int episodeHomeJitterSeed, float maximumEpisodeHomeJitterMm)
        {
            if (!g2R2GroundTruthFreeExploration)
                throw new InvalidOperationException(
                    "Episode Home jitter requires ground-truth-free exploration mode");
            if (!float.IsFinite(maximumEpisodeHomeJitterMm)
                || maximumEpisodeHomeJitterMm < 0f
                || maximumEpisodeHomeJitterMm > 18f)
                throw new ArgumentOutOfRangeException(nameof(maximumEpisodeHomeJitterMm));
            g2R2EpisodeHomeJitterEnabled = maximumEpisodeHomeJitterMm > 0f;
            g2R2EpisodeHomeJitterSeed = episodeHomeJitterSeed;
            g2R2EpisodeHomeJitterMaximumMm = maximumEpisodeHomeJitterMm;
        }

        public static void ConfigureG2R2RandomStartDemoPacing(
            float initialHoldSeconds, float stepDelaySeconds, float homeHoldSeconds)
        {
            if (!float.IsFinite(initialHoldSeconds) || initialHoldSeconds < 0f
                || initialHoldSeconds > 3f)
                throw new ArgumentOutOfRangeException(nameof(initialHoldSeconds));
            if (!float.IsFinite(stepDelaySeconds) || stepDelaySeconds < 0f
                || stepDelaySeconds > 1f)
                throw new ArgumentOutOfRangeException(nameof(stepDelaySeconds));
            if (!float.IsFinite(homeHoldSeconds) || homeHoldSeconds < 0f
                || homeHoldSeconds > 3f)
                throw new ArgumentOutOfRangeException(nameof(homeHoldSeconds));
            g2R2RandomStartInitialHoldSeconds = initialHoldSeconds;
            g2R2RandomStartStepDelaySeconds = stepDelaySeconds;
            g2R2RandomStartHomeHoldSeconds = homeHoldSeconds;
        }

        public static void ConfigureGroundTruthFreeEpisodeHomeLateralJitter(
            int episodeHomeLateralJitterSeed, float maximumLateralJitterMm)
        {
            if (!g2R2GroundTruthFreeExploration)
                throw new InvalidOperationException(
                    "Episode Home lateral jitter requires ground-truth-free exploration mode");
            if (!float.IsFinite(maximumLateralJitterMm)
                || maximumLateralJitterMm < 0f || maximumLateralJitterMm > 3f)
                throw new ArgumentOutOfRangeException(nameof(maximumLateralJitterMm));
            g2R2EpisodeHomeLateralJitterEnabled = maximumLateralJitterMm > 0f;
            g2R2EpisodeHomeLateralJitterSeed = episodeHomeLateralJitterSeed;
            g2R2EpisodeHomeLateralJitterMaximumMm = maximumLateralJitterMm;
        }

        public static void ResetG2R2ContinuousExperimentConfiguration()
        {
            g2R2OutputRootOverride = string.Empty;
            g2R2SurfaceYawOverrideDeg = null;
            g2R2ScanTopWidthOverrideMm = null;
            g2R2RequirePhysicalFinalization = false;
            g2R2GroundTruthFreeExploration = false;
            g2R2GroundTruthFreeHomeOffsetMm = G2R2LateralOffsetMm;
            g2R2RandomStartEnabled = false;
            g2R2RandomStartSeed = 0;
            g2R2RandomStartMaximumMm = 30f;
            g2R2RandomStartMaximumLateralMm = 12f;
            g2R2RandomStartInitialHoldSeconds = 0f;
            g2R2RandomStartStepDelaySeconds = 0f;
            g2R2RandomStartHomeHoldSeconds = 0f;
            g2R2EpisodeHomeJitterEnabled = false;
            g2R2EpisodeHomeJitterSeed = 0;
            g2R2EpisodeHomeJitterMaximumMm = 12f;
            g2R2EpisodeHomeLateralJitterEnabled = false;
            g2R2EpisodeHomeLateralJitterSeed = 0;
            g2R2EpisodeHomeLateralJitterMaximumMm = 1.5f;
            g2R2ActiveRelativeMm = 0f;
            g2R2ActiveLateralMm = 0f;
            g2R2DeferredSettingsRestore = null;
        }

        public bool StartG2R2ContinuousSweep()
        {
            if (!Application.isPlaying || g2R2ContinuousRoutine != null) return false;
            g2R2ContinuousComplete = false;
            g2R2ContinuousFailure = string.Empty;
            g2R2ContinuousStatus = "RUNNING";
            g2R2ActiveRelativeMm = 0f;
            g2R2ActiveLateralMm = 0f;
            g2R2ActiveActor = null;
            g2R2ActiveEvaluator = null;
            g2R2ActiveSummary = null;
            g2R2ContinuousRoutine = StartCoroutine(RunG2R2ContinuousGuarded());
            return true;
        }

        private IEnumerator RunG2R2ContinuousGuarded()
        {
            Exception failure = null;
            Stack<IEnumerator> stack = new Stack<IEnumerator>();
            stack.Push(RunG2R2ContinuousCore());
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
            if (G2R2PhysicalFinalizationRequired)
            {
                IEnumerator recovery = RecoverG2R2PhysicallyToHome();
                Exception recoveryFailure = null;
                while (true)
                {
                    bool advanced;
                    object yielded = null;
                    try
                    {
                        advanced = recovery.MoveNext();
                        if (advanced) yielded = recovery.Current;
                    }
                    catch (Exception exception)
                    {
                        recoveryFailure = exception;
                        break;
                    }
                    if (!advanced) break;
                    yield return yielded;
                }
                (recovery as IDisposable)?.Dispose();
                if (recoveryFailure != null)
                {
                    g2R2DeferredSettingsRestore?.Invoke();
                    g2R2DeferredSettingsRestore = null;
                    g2R2ContinuousFailure = failure + Environment.NewLine
                        + "Physical recovery failed: " + recoveryFailure;
                    g2R2ContinuousStatus = "RECOVERY_FAILED";
                    FinalizeG2R2FailureArtifacts(
                        g2R2ContinuousStatus, g2R2ContinuousFailure, false);
                    g2R2ContinuousRoutine = null;
                    g2R2ContinuousComplete = true;
                    yield break;
                }
            }
            g2R2DeferredSettingsRestore?.Invoke();
            g2R2DeferredSettingsRestore = null;
            g2R2ContinuousFailure = failure.ToString();
            g2R2ContinuousStatus = "INFRASTRUCTURE_OR_SAFETY_FAILURE";
            FinalizeG2R2FailureArtifacts(
                g2R2ContinuousStatus, g2R2ContinuousFailure,
                Mathf.Abs(g2R2ActiveRelativeMm) <= 0.05f
                    && Mathf.Abs(g2R2ActiveLateralMm) <= 0.05f);
            g2R2ContinuousRoutine = null;
            g2R2ContinuousComplete = true;
            Debug.LogError("[ThoracicUSCoach][G2R2] " + failure);
        }

        private IEnumerator RunG2R2ContinuousCore()
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
            G2R2ActorTrajectory actor = null;
            G2R2PrivilegedTrajectory evaluator = null;
            G2R2RunSummary summary = null;
            UltrasoundManager manager = null;
            int originalScanTopWidthMm = 0;
            string frameRoot = Path.Combine(G2R2ResolvedOutputRoot, "frames");
            string evaluatorRoot = Path.Combine(G2R2ResolvedOutputRoot, "evaluator_only");
            string provenanceRoot = Path.Combine(evaluatorRoot, "provenance");
            try
            {
                if (File.Exists(Path.Combine(G2R2ResolvedOutputRoot, "trajectory_actor.json"))
                    || Directory.Exists(frameRoot) || Directory.Exists(provenanceRoot))
                    throw new InvalidOperationException(
                        "G2-R2 output exists; refusing to overwrite the approved development sweep");
                int startupFrames = 0;
                while (startupFrames < G2R2MaximumStartupFrames)
                {
                    AutoWire();
                    if (ValidateG2R2AcquisitionReferences(out _)
                        && UltrasoundManager.TryGet(UltrasoundManager.Plane.Primary,
                            out manager) && manager != null && manager.isActiveAndEnabled)
                        break;
                    startupFrames++;
                    yield return null;
                }
                if (!ValidateG2R2AcquisitionReferences(out string referenceError))
                    throw new InvalidOperationException(referenceError);
                if (probeRoot == null || faceCenter == null || rearCenter == null
                    || skinCollider == null || skinIntersection == null)
                    throw new InvalidOperationException(
                        "G2-R2 requires probe, face/rear centers, exact skin collider, and contact measurement");
                if (manager == null || !manager.isActiveAndEnabled)
                    throw new InvalidOperationException(
                        "G2-R2 primary ultrasound manager did not become ready within 120 frames");
                originalScanTopWidthMm = manager.Width;
                if (G2R2ResolvedScanTopWidthMm.HasValue)
                {
                    manager.Width = G2R2ResolvedScanTopWidthMm.Value;
                    yield return null;
                    yield return null;
                    if (manager.Width != G2R2ResolvedScanTopWidthMm.Value)
                        throw new InvalidOperationException(
                            "G2-R2 scan-width override was not applied");
                }

                G2R2AcquisitionSetup setup = g2R2GroundTruthFreeExploration
                    ? BuildG2R2GroundTruthFreeSetup()
                    : BuildG2R2EvaluatorPlannedSetup();
                Vector3 corridorAxis = setup.corridorAxis;
                RaycastHit cranialHit = setup.cranialHit;
                RaycastHit caudalHit = setup.caudalHit;
                RaycastHit homeHit = setup.homeHit;
                RaycastHit initialHit = setup.initialHit;
                float cranialCoordinate = setup.cranialCoordinate;
                float caudalCoordinate = setup.caudalCoordinate;
                int caudalMovementCount = setup.caudalMovementCount;
                int cranialMovementCount = setup.cranialMovementCount;
                float workspaceCenterOffset = setup.workspaceCenterOffset;
                float diagnosticHalfExtent = setup.workspaceHalfExtent;

                Directory.CreateDirectory(frameRoot);
                Directory.CreateDirectory(provenanceRoot);
                WriteSweepCountJson(Path.Combine(G2R2ResolvedOutputRoot, "preflight.json"),
                    new G2R2Preflight
                    {
                        generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                        status = "PASS",
                        setupIsolation = setup.setupIsolation,
                        stepMm = G2R2StepMm,
                        lateralOffsetMm = g2R2GroundTruthFreeExploration
                            ? g2R2GroundTruthFreeHomeOffsetMm : G2R2LateralOffsetMm,
                        surfaceYawDeg = G2R2ResolvedSurfaceYawDeg,
                        endpointMarginMm = G2R2EndpointMarginMm,
                        caudalOutboundMovementBudget = caudalMovementCount,
                        caudalReturnMovementBudget = caudalMovementCount,
                        cranialOutboundMovementBudget = cranialMovementCount,
                        cranialReturnMovementBudget = cranialMovementCount,
                        plannedCranialCoordinateMm = cranialCoordinate,
                        plannedCaudalCoordinateMm = caudalCoordinate,
                        diagnosticWorkspaceHalfExtentMm = diagnosticHalfExtent,
                        randomStartEnabled = setup.randomStartEnabled,
                        randomStartSeed = setup.randomStartSeed,
                        plannedInitialActorRelativeMm = setup.initialActorRelativeMm,
                        plannedInitialActorLateralMm = setup.initialActorLateralMm,
                        initialHomeReturnMovementBudget = setup.initialHomeReturnMovementBudget,
                        episodeHomeJitterSeed = setup.episodeHomeJitterSeed,
                        episodeHomeJitterMm = setup.episodeHomeJitterMm,
                        episodeHomeLateralJitterSeed = setup.episodeHomeLateralJitterSeed,
                        episodeHomeLateralJitterMm = setup.episodeHomeLateralJitterMm,
                        evaluatorHomeContact = homeHit.point,
                        evaluatorCranialCoverageContact = cranialHit.point,
                        evaluatorCaudalCoverageContact = caudalHit.point,
                        evaluatorCorridorAxis = corridorAxis,
                        decision = setup.decision
                    });

                active2DStepMm = G2R2StepMm;
                sweepCountYawDeg = G2R2ResolvedSurfaceYawDeg;
                if (!PlaceSweepCountProbe(initialHit.point, initialHit.normal, corridorAxis,
                        out string placementFailure))
                    throw new InvalidOperationException("G2-R2 start placement failed: " + placementFailure);
                active2DCaudalAxis = Vector3.ProjectOnPlane(
                    corridorAxis, CurrentSurfaceNormal()).normalized;
                active2DLateralAxis = Vector3.Cross(
                    CurrentSurfaceNormal(), active2DCaudalAxis).normalized;
                caudalTangent = active2DCaudalAxis;
                active2DWorkspaceOrigin = homeHit.point
                    + active2DCaudalAxis * workspaceCenterOffset;
                active2DLongitudinalHalfExtentMm = diagnosticHalfExtent;
                active2DLateralHalfExtentMm = Mathf.Max(originalLateralExtent,
                    g2R2RandomStartMaximumLateralMm + 2f * G2R2StepMm);
                active2DRejectedMoves = 0;
                active2DContactViolations = 0;
                active2DWorkspaceViolations = 0;

                MiniZigzagInstrumentationCheck instrumentation =
                    RunMiniZigzagInstrumentationCheck(manager, evaluatorRoot);
                WriteSweepCountJson(Path.Combine(evaluatorRoot, "instrumentation_check.json"),
                    instrumentation);
                if (!instrumentation.exactPixelParity || !instrumentation.metadataMatched)
                    throw new InvalidOperationException(
                        "G2-R2 provenance instrumentation parity or metadata check failed");

                actor = new G2R2ActorTrajectory
                {
                    generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    scheduleDisclosure = g2R2GroundTruthFreeExploration
                        ? "Fixed scene-authored operator Home, robot/world-down surface tangent, independently traced skin-continuity limits, and a symmetric 120 mm mechanical resource ceiling; no anatomy references plan acquisition."
                        : "Evaluator-planned G2 diagnostic coverage; the future detector receives only this actor trace and referenced clean images.",
                    configuredStepMm = G2R2StepMm,
                    commandedYawDeg = G2R2ResolvedSurfaceYawDeg,
                    commandedLateralOffsetMm = g2R2GroundTruthFreeExploration
                        ? g2R2GroundTruthFreeHomeOffsetMm : G2R2LateralOffsetMm,
                    scanTopWidthMm = manager.Width,
                    scanDepthMm = manager.Depth_cm * 10f,
                    scanFanAngleDeg = manager.Angle,
                    renderedFootprintWidthMm = manager.Width + 2f * manager.Depth_cm * 10f
                        * Mathf.Sin(manager.Angle * Mathf.Deg2Rad),
                    movementBudget = 2 * (caudalMovementCount + cranialMovementCount)
                        + setup.initialHomeReturnMovementBudget,
                    randomStartEnabled = setup.randomStartEnabled,
                    randomStartSeed = setup.randomStartSeed,
                    initialActorRelativeMm = setup.initialActorRelativeMm,
                    initialActorLateralMm = setup.initialActorLateralMm,
                    episodeHomeJitterSeed = setup.episodeHomeJitterSeed,
                    episodeHomeJitterMm = setup.episodeHomeJitterMm,
                    episodeHomeLateralJitterSeed = setup.episodeHomeLateralJitterSeed,
                    episodeHomeLateralJitterMm = setup.episodeHomeLateralJitterMm
                };
                evaluator = new G2R2PrivilegedTrajectory
                {
                    generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    fixedHomeContract = g2R2GroundTruthFreeExploration
                        ? "Scene-authored operator setup projected to generic skin contact; no anatomical label was queried."
                        : "Repeatable central robotic setup; anatomically unlabeled to the actor.",
                    diagnosticCranialCoverageReference = g2R2GroundTruthFreeExploration
                        ? "SYMMETRIC_MECHANICAL_LIMIT_NEGATIVE"
                        : "LF_T2_ASSOCIATED_RECESS_MINUS_MARGIN",
                    diagnosticCaudalCoverageReference = g2R2GroundTruthFreeExploration
                        ? "SYMMETRIC_MECHANICAL_LIMIT_POSITIVE"
                        : "LF_T8_ASSOCIATED_RECESS_PLUS_MARGIN",
                    homeContactPoint = homeHit.point,
                    cranialCoverageContactPoint = cranialHit.point,
                    caudalCoverageContactPoint = caudalHit.point,
                    corridorAxis = corridorAxis,
                    originalWorkspaceOrigin = originalWorkspaceOrigin,
                    diagnosticWorkspaceOrigin = active2DWorkspaceOrigin,
                    originalLongitudinalHalfExtentMm = originalLongitudinalExtent,
                    diagnosticLongitudinalHalfExtentMm = diagnosticHalfExtent
                };
                summary = new G2R2RunSummary
                {
                    generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
                };
                g2R2ActiveActor = actor;
                g2R2ActiveEvaluator = evaluator;
                g2R2ActiveSummary = summary;

                float relative = setup.initialActorRelativeMm;
                float lateralRelative = setup.initialActorLateralMm;
                g2R2ActiveRelativeMm = relative;
                g2R2ActiveLateralMm = lateralRelative;
                float travel = 0f;
                if (setup.randomStartEnabled)
                {
                    yield return CaptureG2R2Observation(manager, actor, evaluator,
                        frameRoot, provenanceRoot, "random_start_baseline", Active2DAction.Observe,
                        true, relative, travel, lateralRelative);
                    if (g2R2RandomStartInitialHoldSeconds > 0f)
                        yield return new WaitForSecondsRealtime(
                            g2R2RandomStartInitialHoldSeconds);
                    yield return RunG2R2InitialHomeReturn(manager, actor, evaluator,
                        frameRoot, provenanceRoot,
                        value => relative = value, () => relative,
                        value => lateralRelative = value, () => lateralRelative,
                        value => travel = value, () => travel);
                    EnsureG2R2Healthy(actor, "initial physical return to Home");
                    actor.homeArrivalResidualMm = relative;
                    actor.homeArrivalLateralResidualMm = lateralRelative;
                    actor.initialHomeReturnCompleted = Mathf.Abs(relative) <= 0.05f
                        && Mathf.Abs(lateralRelative) <= 0.05f;
                    if (!actor.initialHomeReturnCompleted)
                        throw new InvalidOperationException(
                            "Ground-truth-free random start did not physically reach Home");
                    relative = 0f;
                    lateralRelative = 0f;
                    g2R2ActiveRelativeMm = 0f;
                    g2R2ActiveLateralMm = 0f;
                    yield return CaptureG2R2Observation(manager, actor, evaluator,
                        frameRoot, provenanceRoot, "home_arrival_baseline",
                        Active2DAction.Observe, true, relative, travel);
                    if (g2R2RandomStartHomeHoldSeconds > 0f)
                        yield return new WaitForSecondsRealtime(
                            g2R2RandomStartHomeHoldSeconds);
                }
                if (!setup.randomStartEnabled)
                    yield return CaptureG2R2Observation(manager, actor, evaluator,
                        frameRoot, provenanceRoot, "forward_baseline", Active2DAction.Observe,
                        true, relative, travel);
                yield return RunG2R2Leg(manager, actor, evaluator, frameRoot, provenanceRoot,
                    "caudal_outbound", Active2DAction.Caudal, caudalMovementCount,
                    value => relative = value, () => relative,
                    value => travel = value, () => travel);
                EnsureG2R2Healthy(actor, "caudal outbound");
                int caudalReturnCount = g2R2GroundTruthFreeExploration
                    ? Mathf.CeilToInt(Mathf.Abs(relative) / G2R2StepMm)
                    : caudalMovementCount;
                yield return RunG2R2Leg(manager, actor, evaluator, frameRoot, provenanceRoot,
                    "caudal_return", Active2DAction.Cranial, caudalReturnCount,
                    value => relative = value, () => relative,
                    value => travel = value, () => travel);
                EnsureG2R2Healthy(actor, "caudal return");
                yield return RunG2R2Leg(manager, actor, evaluator, frameRoot, provenanceRoot,
                    "cranial_outbound", Active2DAction.Cranial, cranialMovementCount,
                    value => relative = value, () => relative,
                    value => travel = value, () => travel);
                EnsureG2R2Healthy(actor, "cranial outbound");
                int cranialReturnCount = g2R2GroundTruthFreeExploration
                    ? Mathf.CeilToInt(Mathf.Abs(relative) / G2R2StepMm)
                    : cranialMovementCount;
                yield return RunG2R2Leg(manager, actor, evaluator, frameRoot, provenanceRoot,
                    "cranial_return", Active2DAction.Caudal, cranialReturnCount,
                    value => relative = value, () => relative,
                    value => travel = value, () => travel);
                EnsureG2R2Healthy(actor, "cranial return");

                bool returned = Mathf.Abs(relative) <= G2R2ReturnToleranceMm;
                actor.status = returned ? "CAPTURE_COMPLETE_PENDING_BLIND_ANALYSIS"
                    : "RETURN_COORDINATE_MISMATCH";
                actor.stopReason = returned ? string.Empty
                    : "Physical reverse traversal did not return within the predeclared 3 mm tolerance";
                summary.status = actor.status;
                summary.g1 = returned ? "PASS" : "FAIL";
                summary.acceptedMovements = actor.acceptedMovements;
                summary.rejectedMovements = actor.rejectedMovements;
                summary.validFrames = actor.observations.Count(item => item.frameIntegrityValid);
                summary.maximumYawErrorDeg = evaluator.observations.Max(
                    item => Mathf.Abs(item.surfaceYawErrorDeg));
                summary.maximumNormalAlignmentErrorDeg = evaluator.observations.Max(
                    item => item.normalAlignmentErrorDeg);
                summary.finalActorRelativeMm = relative;
                summary.returnedWithinTolerance = returned;
                summary.stopReason = actor.stopReason;
                if (!returned)
                    throw new InvalidOperationException(actor.stopReason);
                g2R2ContinuousStatus = actor.status;
            }
            finally
            {
                bool guardedFinalizationPending = G2R2PhysicalFinalizationRequired
                    && g2R2ContinuousStatus == "RUNNING";
                Action restoreSettings = () =>
                {
                    active2DStepMm = originalStep;
                    active2DLongitudinalHalfExtentMm = originalLongitudinalExtent;
                    active2DLateralHalfExtentMm = originalLateralExtent;
                    sweepCountYawDeg = originalSweepYaw;
                    active2DWorkspaceOrigin = originalWorkspaceOrigin;
                    active2DCaudalAxis = originalCaudalAxis;
                    active2DLateralAxis = originalLateralAxis;
                    if (probeRoot != null && skinCollider != null
                        && !G2R2PhysicalFinalizationRequired)
                    {
                        probeRoot.SetPositionAndRotation(originalPosition, originalRotation);
                        EnforceRigidContact();
                    }
                    if (manager != null && originalScanTopWidthMm > 0)
                        manager.Width = originalScanTopWidthMm;
                    if (summary != null) summary.originalSettingsRestored = true;
                };
                if (guardedFinalizationPending)
                    g2R2DeferredSettingsRestore = restoreSettings;
                else restoreSettings();
                WriteG2R2Artifacts(actor, evaluator, summary);
                if (!guardedFinalizationPending)
                {
                    g2R2ContinuousRoutine = null;
                    g2R2ContinuousComplete = true;
                }
            }
        }

        private G2R2AcquisitionSetup BuildG2R2GroundTruthFreeSetup()
        {
            if (!FindSurfaceAt(faceCenter.position, Vector3.zero, out RaycastHit sceneContact))
                throw new InvalidOperationException(
                    "Ground-truth-free setup could not project the scene-authored operator Home to skin");
            Vector3 corridorAxis = Vector3.ProjectOnPlane(
                Vector3.back, sceneContact.normal).normalized;
            if (corridorAxis.sqrMagnitude < 0.5f)
                corridorAxis = Vector3.ProjectOnPlane(
                    Vector3.down, sceneContact.normal).normalized;
            if (corridorAxis.sqrMagnitude < 0.5f)
                throw new InvalidOperationException(
                    "Ground-truth-free robot/world-back surface tangent is degenerate");
            float episodeHomeLateralJitterMm = 0f;
            if (g2R2EpisodeHomeLateralJitterEnabled)
            {
                System.Random lateralRandom = new System.Random(
                    g2R2EpisodeHomeLateralJitterSeed);
                int maximumHalfMillimeterSteps = Mathf.Max(1,
                    Mathf.FloorToInt(g2R2EpisodeHomeLateralJitterMaximumMm / 0.5f));
                int selectedHalfMillimeterSteps = lateralRandom.Next(
                    1, maximumHalfMillimeterSteps + 1);
                episodeHomeLateralJitterMm = (lateralRandom.Next(0, 2) == 1 ? 1f : -1f)
                    * selectedHalfMillimeterSteps * 0.5f;
            }
            float resolvedHomeLateralOffsetMm = g2R2GroundTruthFreeHomeOffsetMm
                + episodeHomeLateralJitterMm;
            if (resolvedHomeLateralOffsetMm < -60f || resolvedHomeLateralOffsetMm > 10f)
                throw new InvalidOperationException(
                    "Ground-truth-free episode Home lateral jitter exceeds the safe setup bound");
            RaycastHit fixedHomeHit = FindG2R2OffsetSurface(
                sceneContact.point, corridorAxis, resolvedHomeLateralOffsetMm,
                "scene-authored operator Home");
            RaycastHit homeHit = fixedHomeHit;
            float episodeHomeJitterMm = 0f;
            if (g2R2EpisodeHomeJitterEnabled)
            {
                int jitterTraceSteps = Mathf.CeilToInt(
                    g2R2EpisodeHomeJitterMaximumMm / BoundaryPreflightStepMm) + 1;
                List<BoundarySurfaceSample> jitterCranial = TraceBoundarySurface(
                    fixedHomeHit, -corridorAxis, fixedHomeHit.point, corridorAxis,
                    jitterTraceSteps);
                List<BoundarySurfaceSample> jitterCaudal = TraceBoundarySurface(
                    fixedHomeHit, corridorAxis, fixedHomeHit.point, corridorAxis,
                    jitterTraceSteps);
                System.Random jitterRandom = new System.Random(g2R2EpisodeHomeJitterSeed);
                bool jitterCaudally = jitterRandom.Next(0, 2) == 1;
                List<BoundarySurfaceSample> jitterSurface = jitterCaudally
                    ? jitterCaudal : jitterCranial;
                int maximumJitterIndex = Mathf.Min(jitterSurface.Count - 1,
                    Mathf.FloorToInt(g2R2EpisodeHomeJitterMaximumMm
                        / BoundaryPreflightStepMm));
                if (maximumJitterIndex < 1)
                    throw new InvalidOperationException(
                        "Ground-truth-free episode Home jitter has no safe surface sample");
                int jitterIndex = jitterRandom.Next(1, maximumJitterIndex + 1);
                BoundarySurfaceSample jitterSample = jitterSurface[jitterIndex];
                if (!FindSurfaceAt(jitterSample.contactPoint, jitterSample.surfaceNormal,
                        out homeHit))
                    throw new InvalidOperationException(
                        "Ground-truth-free episode Home jitter could not be revalidated");
                episodeHomeJitterMm = (jitterCaudally ? 1f : -1f)
                    * jitterSample.accumulatedSurfaceTravelMm;
            }
            int traceSteps = Mathf.CeilToInt(
                G2R2GroundTruthFreeMechanicalExtentMm / BoundaryPreflightStepMm);
            List<BoundarySurfaceSample> cranialSurface = TraceBoundarySurface(
                homeHit, -corridorAxis, homeHit.point, corridorAxis, traceSteps);
            List<BoundarySurfaceSample> caudalSurface = TraceBoundarySurface(
                homeHit, corridorAxis, homeHit.point, corridorAxis, traceSteps);
            int reserveSamples = Mathf.CeilToInt(
                G2R2GroundTruthFreeBoundaryReserveMm / BoundaryPreflightStepMm);
            int cranialIndex = cranialSurface.Count - 1 - reserveSamples;
            int caudalIndex = caudalSurface.Count - 1 - reserveSamples;
            if (cranialIndex < 1 || caudalIndex < 1)
                throw new InvalidOperationException(
                    "Ground-truth-free skin-continuity preflight has no safe two-sided range");
            BoundarySurfaceSample cranialLimit = cranialSurface[cranialIndex];
            BoundarySurfaceSample caudalLimit = caudalSurface[caudalIndex];
            if (!FindSurfaceAt(cranialLimit.contactPoint, cranialLimit.surfaceNormal,
                    out RaycastHit cranialHit)
                || !FindSurfaceAt(caudalLimit.contactPoint, caudalLimit.surfaceNormal,
                    out RaycastHit caudalHit))
                throw new InvalidOperationException(
                    "Ground-truth-free skin-continuity endpoints could not be revalidated");
            float cranialTravelMm = cranialLimit.accumulatedSurfaceTravelMm;
            float caudalTravelMm = caudalLimit.accumulatedSurfaceTravelMm;
            int cranialMovementCount = Mathf.FloorToInt(cranialTravelMm / G2R2StepMm);
            int caudalMovementCount = Mathf.FloorToInt(caudalTravelMm / G2R2StepMm);
            if (cranialMovementCount < 2 || caudalMovementCount < 2)
                throw new InvalidOperationException(
                    "Ground-truth-free skin-continuity movement budget is too short");
            float workspaceHalfExtent = Mathf.Max(
                Mathf.Abs(Vector3.Dot(cranialHit.point - homeHit.point, corridorAxis)),
                Mathf.Abs(Vector3.Dot(caudalHit.point - homeHit.point, corridorAxis)))
                + 2f * G2R2StepMm;
            RaycastHit initialHit = homeHit;
            float initialActorRelativeMm = 0f;
            float initialActorLateralMm = 0f;
            int initialHomeReturnMovementBudget = 0;
            if (g2R2RandomStartEnabled)
            {
                System.Random randomStart = new System.Random(g2R2RandomStartSeed);
                bool startCaudally = randomStart.Next(0, 2) == 1;
                List<BoundarySurfaceSample> startSurface = startCaudally
                    ? caudalSurface : cranialSurface;
                int safeEndpointIndex = startCaudally ? caudalIndex : cranialIndex;
                int maximumStartIndex = Mathf.Min(safeEndpointIndex,
                    Mathf.FloorToInt(g2R2RandomStartMaximumMm
                        / BoundaryPreflightStepMm));
                int minimumStartIndex = Mathf.CeilToInt(
                    G2R2MinimumRandomStartMm / BoundaryPreflightStepMm);
                if (maximumStartIndex < minimumStartIndex)
                    throw new InvalidOperationException(
                        "Ground-truth-free random start has no safe bounded surface sample");
                int startIndex = randomStart.Next(
                    minimumStartIndex, maximumStartIndex + 1);
                BoundarySurfaceSample startSample = startSurface[startIndex];
                Vector3 startCorridor = Vector3.ProjectOnPlane(
                    corridorAxis, startSample.surfaceNormal).normalized;
                Vector3 startLateral = Vector3.Cross(
                    startSample.surfaceNormal.normalized, startCorridor).normalized;
                if (startCorridor.sqrMagnitude < 0.5f
                    || startLateral.sqrMagnitude < 0.5f)
                    throw new InvalidOperationException(
                        "Ground-truth-free 2D random-start axes are degenerate");
                int maximumLateralSteps = Mathf.FloorToInt(
                    g2R2RandomStartMaximumLateralMm / G2R2StepMm);
                int minimumLateralSteps = Mathf.CeilToInt(
                    G2R2MinimumRandomStartLateralMm / G2R2StepMm);
                int lateralSteps = randomStart.Next(
                    minimumLateralSteps, maximumLateralSteps + 1);
                initialActorLateralMm = (randomStart.Next(0, 2) == 1 ? 1f : -1f)
                    * lateralSteps * G2R2StepMm;
                Vector3 twoDimensionalProposal = startSample.contactPoint
                    + startLateral * initialActorLateralMm;
                if (!FindSurfaceAt(twoDimensionalProposal, startSample.surfaceNormal,
                        out initialHit))
                    throw new InvalidOperationException(
                        "Ground-truth-free 2D random start could not be revalidated");
                initialActorRelativeMm = (startCaudally ? 1f : -1f)
                    * startSample.accumulatedSurfaceTravelMm;
                initialHomeReturnMovementBudget = Mathf.CeilToInt(
                    (Mathf.Abs(initialActorRelativeMm)
                        + Mathf.Abs(initialActorLateralMm)) / G2R2StepMm) + 4;
            }
            return new G2R2AcquisitionSetup
            {
                homeHit = homeHit,
                initialHit = initialHit,
                cranialHit = cranialHit,
                caudalHit = caudalHit,
                corridorAxis = corridorAxis,
                cranialCoordinate = -cranialTravelMm,
                caudalCoordinate = caudalTravelMm,
                workspaceCenterOffset = 0f,
                workspaceHalfExtent = workspaceHalfExtent,
                caudalMovementCount = caudalMovementCount,
                cranialMovementCount = cranialMovementCount,
                initialActorRelativeMm = initialActorRelativeMm,
                initialActorLateralMm = initialActorLateralMm,
                initialHomeReturnMovementBudget = initialHomeReturnMovementBudget,
                randomStartSeed = g2R2RandomStartSeed,
                randomStartEnabled = g2R2RandomStartEnabled,
                episodeHomeJitterSeed = g2R2EpisodeHomeJitterSeed,
                episodeHomeJitterMm = episodeHomeJitterMm,
                episodeHomeLateralJitterSeed = g2R2EpisodeHomeLateralJitterSeed,
                episodeHomeLateralJitterMm = episodeHomeLateralJitterMm,
                setupIsolation =
                    "Scene-authored operator Home, generic skin contact, robot/world-back surface tangent, and symmetric mechanical resource limits; no LF or T-level reference is queried.",
                decision =
                    "A seed-bounded two-dimensional simulator start differs from the episode Home in both robot-local longitudinal and lateral coordinates; the actor physically returns using positioning-relative displacement before the open-loop baseline follows both robot-local longitudinal directions to independently traced skin-continuity limits, preserves a 6 mm safety reserve, and physically revisits Home after each leg."
            };
        }

        private G2R2AcquisitionSetup BuildG2R2EvaluatorPlannedSetup()
        {
            Transform[] sceneTransforms = Resources.FindObjectsOfTypeAll<Transform>()
                .Where(item => item != null && item.gameObject.scene == gameObject.scene)
                .ToArray();
            Renderer lfT2 = FindG2R2LfRenderer(sceneTransforms, "LF T2");
            Renderer lfT8 = FindG2R2LfRenderer(sceneTransforms, "LF T8");
            Vector3 corridorAxis = (lfT8.bounds.center - lfT2.bounds.center).normalized;
            if (corridorAxis.sqrMagnitude < 0.5f)
                throw new InvalidOperationException("G2-R2 LF-derived corridor axis is degenerate");
            RaycastHit cranialHit = FindG2R2OffsetSurface(
                lfT2.bounds.center - corridorAxis * G2R2EndpointMarginMm,
                corridorAxis, G2R2LateralOffsetMm, "cranial start");
            RaycastHit caudalHit = FindG2R2OffsetSurface(
                lfT8.bounds.center + corridorAxis * G2R2EndpointMarginMm,
                corridorAxis, G2R2LateralOffsetMm, "caudal end");
            SweepCountReferenceSet references = BuildSweepCountReferences();
            Vector3 centralProposal = 0.5f
                * (references.references[0].contactPoint + references.references[6].contactPoint);
            RaycastHit homeHit = FindG2R2OffsetSurface(
                centralProposal, corridorAxis, G2R2LateralOffsetMm, "fixed unlabeled Home");
            float cranialCoordinate = Vector3.Dot(
                cranialHit.point - homeHit.point, corridorAxis);
            float caudalCoordinate = Vector3.Dot(
                caudalHit.point - homeHit.point, corridorAxis);
            float span = caudalCoordinate - cranialCoordinate;
            if (span < 120f)
                throw new InvalidOperationException(
                    "G2-R2 validated T2-T8 diagnostic span is unexpectedly short: "
                    + span.ToString("F3", CultureInfo.InvariantCulture) + " mm");
            if (!(cranialCoordinate < 0f && caudalCoordinate > 0f))
                throw new InvalidOperationException(
                    "G2-R2 fixed Home does not lie inside the validated two-sided coverage corridor");
            int caudalMovementCount = Mathf.CeilToInt(caudalCoordinate / G2R2StepMm);
            int cranialMovementCount = Mathf.CeilToInt(-cranialCoordinate / G2R2StepMm);
            CalculateM0BDiagnosticWorkspace(cranialCoordinate, caudalCoordinate,
                out float workspaceCenterOffset, out float diagnosticHalfExtent);
            diagnosticHalfExtent += 2f * G2R2StepMm;
            return new G2R2AcquisitionSetup
            {
                homeHit = homeHit,
                initialHit = homeHit,
                cranialHit = cranialHit,
                caudalHit = caudalHit,
                corridorAxis = corridorAxis,
                cranialCoordinate = cranialCoordinate,
                caudalCoordinate = caudalCoordinate,
                workspaceCenterOffset = workspaceCenterOffset,
                workspaceHalfExtent = diagnosticHalfExtent,
                caudalMovementCount = caudalMovementCount,
                cranialMovementCount = cranialMovementCount,
                initialActorRelativeMm = 0f,
                initialHomeReturnMovementBudget = 0,
                randomStartEnabled = false,
                setupIsolation =
                    "Evaluator-only LF-associated recess endpoints define diagnostic coverage; no label or pose enters actor observations.",
                decision =
                    "One continuous M6 episode starts at fixed unlabeled Home, samples both directions, and physically revisits Home after each endpoint."
            };
        }

        private Renderer FindG2R2LfRenderer(Transform[] sceneTransforms, string objectName)
        {
            Transform target = sceneTransforms.FirstOrDefault(item => item.name == objectName);
            Renderer renderer = target != null ? target.GetComponentInChildren<Renderer>(true) : null;
            if (renderer == null)
                throw new InvalidOperationException("G2-R2 missing evaluator setup renderer " + objectName);
            return renderer;
        }

        private bool ValidateG2R2AcquisitionReferences(out string error)
        {
            if (probeRoot == null || faceCenter == null || rearCenter == null)
            {
                error = "G2-R2 probe/face/rear marker missing";
                return false;
            }
            if (skinCollider == null || skinIntersection == null)
            {
                error = "G2-R2 skin collider or contact provider missing";
                return false;
            }
            if (ultrasoundCamera == null)
            {
                error = "G2-R2 primary ultrasound image camera missing";
                return false;
            }
            if (scanningPlane == null)
            {
                error = "G2-R2 primary scanning plane missing";
                return false;
            }
            error = null;
            return true;
        }

        private RaycastHit FindG2R2OffsetSurface(
            Vector3 anatomyPoint, Vector3 corridorAxis, float lateralOffsetMm, string label)
        {
            if (!FindSurfaceAt(anatomyPoint, Vector3.zero, out RaycastHit centralHit))
                throw new InvalidOperationException("G2-R2 surface projection failed at " + label);
            Vector3 localCorridor = Vector3.ProjectOnPlane(
                corridorAxis, centralHit.normal).normalized;
            Vector3 lateral = Vector3.Cross(centralHit.normal, localCorridor).normalized;
            if (localCorridor.sqrMagnitude < 0.5f || lateral.sqrMagnitude < 0.5f)
                throw new InvalidOperationException("G2-R2 local axes are degenerate at " + label);
            if (!FindSurfaceAt(centralHit.point + lateral * lateralOffsetMm,
                    centralHit.normal, out RaycastHit offsetHit))
                throw new InvalidOperationException("G2-R2 offset projection failed at " + label);
            return offsetHit;
        }

        private IEnumerator RunG2R2Leg(
            UltrasoundManager manager,
            G2R2ActorTrajectory actor,
            G2R2PrivilegedTrajectory evaluator,
            string frameRoot,
            string provenanceRoot,
            string phase,
            Active2DAction action,
            int movementCount,
            Action<float> setRelative,
            Func<float> getRelative,
            Action<float> setTravel,
            Func<float> getTravel)
        {
            for (int step = 0; step < movementCount; step++)
            {
                bool accepted = TryG2R2PhysicalAction(action);
                Active2DActionExecution execution = active2DLastActionExecution;
                float relative = getRelative();
                float travel = getTravel();
                if (accepted)
                {
                    float sign = action == Active2DAction.Caudal ? 1f : -1f;
                    relative += sign * execution.achievedDisplacementMm;
                    travel += execution.achievedDisplacementMm;
                    actor.acceptedMovements++;
                }
                else actor.rejectedMovements++;
                setRelative(relative);
                g2R2ActiveRelativeMm = relative;
                setTravel(travel);
                yield return CaptureG2R2Observation(manager, actor, evaluator,
                    frameRoot, provenanceRoot, phase, action, accepted, relative, travel);
                if (!accepted) yield break;
            }
        }

        private IEnumerator RunG2R2InitialHomeReturn(
            UltrasoundManager manager,
            G2R2ActorTrajectory actor,
            G2R2PrivilegedTrajectory evaluator,
            string frameRoot,
            string provenanceRoot,
            Action<float> setRelative,
            Func<float> getRelative,
            Action<float> setLateralRelative,
            Func<float> getLateralRelative,
            Action<float> setTravel,
            Func<float> getTravel)
        {
            int maximumActions = Mathf.CeilToInt(
                (Mathf.Abs(getRelative()) + Mathf.Abs(getLateralRelative()))
                / G2R2StepMm) + 4;
            for (int index = 0;
                 index < maximumActions
                    && (Mathf.Abs(getRelative()) > 0.05f
                        || Mathf.Abs(getLateralRelative()) > 0.05f);
                 index++)
            {
                float relative = getRelative();
                float lateralRelative = getLateralRelative();
                bool returnLateralFirst = Mathf.Abs(lateralRelative) > 0.05f;
                Active2DAction action = returnLateralFirst
                    ? lateralRelative > 0f
                        ? Active2DAction.Medial : Active2DAction.Lateral
                    : relative > 0f
                        ? Active2DAction.Cranial : Active2DAction.Caudal;
                float componentResidual = returnLateralFirst
                    ? Mathf.Abs(lateralRelative) : Mathf.Abs(relative);
                active2DStepMm = Mathf.Min(G2R2StepMm, componentResidual);
                bool accepted;
                try
                {
                    accepted = TryG2R2PhysicalAction(action);
                }
                finally
                {
                    active2DStepMm = G2R2StepMm;
                }
                Active2DActionExecution execution = active2DLastActionExecution;
                float travel = getTravel();
                if (accepted)
                {
                    if (action == Active2DAction.Caudal
                        || action == Active2DAction.Cranial)
                    {
                        float sign = action == Active2DAction.Caudal ? 1f : -1f;
                        relative += sign * execution.achievedDisplacementMm;
                    }
                    else
                    {
                        float sign = action == Active2DAction.Lateral ? 1f : -1f;
                        lateralRelative += sign * execution.achievedDisplacementMm;
                    }
                    travel += execution.achievedDisplacementMm;
                    actor.acceptedMovements++;
                    actor.initialHomeReturnAcceptedMovements++;
                }
                else actor.rejectedMovements++;
                setRelative(relative);
                setLateralRelative(lateralRelative);
                g2R2ActiveRelativeMm = relative;
                g2R2ActiveLateralMm = lateralRelative;
                setTravel(travel);
                yield return CaptureG2R2Observation(manager, actor, evaluator,
                    frameRoot, provenanceRoot, "initial_home_return", action,
                    accepted, relative, travel, lateralRelative);
                if (g2R2RandomStartStepDelaySeconds > 0f)
                    yield return new WaitForSecondsRealtime(
                        g2R2RandomStartStepDelaySeconds);
                if (!accepted) yield break;
            }
        }

        private bool TryG2R2PhysicalAction(Active2DAction action)
        {
            BeginActive2DActionExecution(action);
            if (action == Active2DAction.Caudal)
                return TryActive2DMove(active2DCaudalAxis, G2R2ResolvedSurfaceYawDeg);
            if (action == Active2DAction.Cranial)
                return TryActive2DMove(-active2DCaudalAxis, G2R2ResolvedSurfaceYawDeg);
            if (action == Active2DAction.Lateral)
                return TryActive2DMove(active2DLateralAxis, G2R2ResolvedSurfaceYawDeg);
            if (action == Active2DAction.Medial)
                return TryActive2DMove(-active2DLateralAxis, G2R2ResolvedSurfaceYawDeg);
            return RejectActive2DActionWithoutPoseChange(
                "G2-R2 accepts only robot-local 2D physical translation");
        }

        private IEnumerator RecoverG2R2PhysicallyToHome()
        {
            G2R2RecoveryTrace trace = new G2R2RecoveryTrace();
            int maximumActions = Mathf.CeilToInt(
                (Mathf.Abs(g2R2ActiveRelativeMm) + Mathf.Abs(g2R2ActiveLateralMm))
                / G2R2StepMm) + 4;
            for (int index = 0;
                 index < maximumActions
                    && (Mathf.Abs(g2R2ActiveRelativeMm) > 0.05f
                        || Mathf.Abs(g2R2ActiveLateralMm) > 0.05f);
                 index++)
            {
                if (!MeasureSweepCountContact() || !IsActive2DWorkspaceValid(faceCenter.position))
                    throw new InvalidOperationException(
                        "G2-R2 physical recovery cannot continue from invalid contact or workspace");
                bool recoverLateralFirst = Mathf.Abs(g2R2ActiveLateralMm) > 0.05f;
                Active2DAction action = recoverLateralFirst
                    ? g2R2ActiveLateralMm > 0f
                        ? Active2DAction.Medial : Active2DAction.Lateral
                    : g2R2ActiveRelativeMm > 0f
                        ? Active2DAction.Cranial : Active2DAction.Caudal;
                bool accepted = TryG2R2PhysicalAction(action);
                Active2DActionExecution execution = active2DLastActionExecution;
                if (!accepted)
                    throw new InvalidOperationException(
                        "G2-R2 physical recovery movement was rejected: "
                        + execution.rejectionReason);
                if (action == Active2DAction.Caudal || action == Active2DAction.Cranial)
                {
                    float sign = action == Active2DAction.Caudal ? 1f : -1f;
                    g2R2ActiveRelativeMm += sign * execution.achievedDisplacementMm;
                }
                else
                {
                    float sign = action == Active2DAction.Lateral ? 1f : -1f;
                    g2R2ActiveLateralMm += sign * execution.achievedDisplacementMm;
                }
                trace.actions.Add(new G2R2RecoveryAction
                {
                    action = action.ToString(),
                    achievedDisplacementMm = execution.achievedDisplacementMm,
                    signedActorRelativeMm = g2R2ActiveRelativeMm,
                    signedActorLateralMm = g2R2ActiveLateralMm,
                    contactValidAfterExecution = execution.contactValidAfterExecution,
                    workspaceValidAfterExecution = execution.workspaceValidAfterExecution
                });
                yield return null;
            }
            trace.finalActorRelativeMm = g2R2ActiveRelativeMm;
            trace.finalActorLateralMm = g2R2ActiveLateralMm;
            trace.recovered = Mathf.Abs(g2R2ActiveRelativeMm) <= 0.05f
                && Mathf.Abs(g2R2ActiveLateralMm) <= 0.05f;
            WriteSweepCountJson(Path.Combine(G2R2ResolvedOutputRoot, "recovery_trace.json"),
                trace);
            if (Mathf.Abs(g2R2ActiveRelativeMm) > 0.05f
                || Mathf.Abs(g2R2ActiveLateralMm) > 0.05f)
                throw new InvalidOperationException(
                    "G2-R2 bounded physical recovery missed Home");
        }

        private IEnumerator CaptureG2R2Observation(
            UltrasoundManager manager,
            G2R2ActorTrajectory actor,
            G2R2PrivilegedTrajectory evaluator,
            string frameRoot,
            string provenanceRoot,
            string phase,
            Active2DAction action,
            bool accepted,
            float relative,
            float travel,
            float lateralRelative = 0f)
        {
            int index = actor.observations.Count;
            string stem = "frame_" + index.ToString("D4", CultureInfo.InvariantCulture);
            DirectFreshPoseToken token = BeginDirectFreshCapturePose("g2_r2_" + stem, true);
            DirectFreshCaptureResult capture = new DirectFreshCaptureResult();
            yield return CaptureDirectProducerFreshFrame(token, "g2_r2_continuous_m6", capture, true);
            if (!M0BFrameIntegrityValid(capture.image, capture.pngBytes))
                throw new InvalidOperationException("G2-R2 frame integrity failed at " + stem);
            if (!manager.TryGetPublishedScanProvenance(token.poseEpoch, capture.producerScanSerial,
                    out UltrasoundManager.PublishedScanProvenanceSnapshot provenance))
                throw new InvalidOperationException("G2-R2 provenance is missing at " + stem);
            string imageFile = stem + ".png";
            string provenanceFile = stem + ".json";
            File.WriteAllBytes(Path.Combine(frameRoot, imageFile), capture.pngBytes);
            WriteSweepCountJson(Path.Combine(provenanceRoot, provenanceFile), provenance);
            Active2DActionExecution execution = active2DLastActionExecution;
            G2R2ActorObservation observation = new G2R2ActorObservation
            {
                observationIndex = index,
                phase = phase,
                requestedAction = action.ToString(),
                executedSuccessfully = accepted,
                requestedDisplacementMm = action == Active2DAction.Observe
                    ? 0f : execution.requestedDisplacementMm,
                attemptedDisplacementMm = action == Active2DAction.Observe
                    ? 0f : execution.attemptedDisplacementMm,
                achievedDisplacementMm = action == Active2DAction.Observe
                    ? 0f : execution.achievedDisplacementMm,
                signedActorRelativeMm = relative,
                signedActorLateralMm = lateralRelative,
                cumulativeExecutedTravelMm = travel,
                contactValidAfterExecution = action == Active2DAction.Observe
                    ? MeasureSweepCountContact() : execution.contactValidAfterExecution,
                workspaceValidAfterExecution = action == Active2DAction.Observe
                    ? IsActive2DWorkspaceValid(faceCenter.position) : execution.workspaceValidAfterExecution,
                rollbackAttempted = action != Active2DAction.Observe && execution.rollbackAttempted,
                rollbackContactRestored = action == Active2DAction.Observe
                    || execution.rollbackContactRestored,
                safetyFailure = action != Active2DAction.Observe && execution.safetyFailure,
                rejectionReason = action == Active2DAction.Observe
                    ? string.Empty : execution.rejectionReason,
                image = "frames/" + imageFile,
                imageSha256 = Sha256Bytes(capture.pngBytes),
                imageWidth = capture.image.width,
                imageHeight = capture.image.height,
                frameIntegrityValid = true,
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
            Vector3 scanWidth = Vector3.ProjectOnPlane(probeRoot.right, normal).normalized;
            Vector3 localCorridor = Vector3.ProjectOnPlane(
                evaluator.corridorAxis, normal).normalized;
            int skeletonVisible = 0;
            int lfVisible = 0;
            foreach (UltrasoundManager.PublishedScanProvenanceBeam beam in provenance.beams)
            {
                if (beam.interfaces.Any(item => item.objectName == "Skeleton"
                        && item.reachedBeforeOrAtFirstShadow)) skeletonVisible++;
                if (beam.interfaces.Any(item => item.objectName.StartsWith("LF T", StringComparison.Ordinal)
                        && item.reachedBeforeOrAtFirstShadow)) lfVisible++;
            }
            evaluator.observations.Add(new G2R2PrivilegedObservation
            {
                observationIndex = index,
                phase = phase,
                probePosition = capture.capturedPosition,
                probeRotation = capture.capturedRotation,
                contactPoint = faceCenter.position,
                surfaceNormal = normal,
                evaluatorLongitudinalCoordinateMm = Vector3.Dot(
                    faceCenter.position - evaluator.homeContactPoint, evaluator.corridorAxis),
                achievedSurfaceYawDeg = MeasureSweepCountSurfaceYawDeg(normal),
                surfaceYawErrorDeg = Mathf.DeltaAngle(
                    G2R2ResolvedSurfaceYawDeg, MeasureSweepCountSurfaceYawDeg(normal)),
                normalAlignmentErrorDeg = Vector3.Angle(
                    (rearCenter.position - faceCenter.position).normalized, normal),
                scanWidthAgainstCorridor = localCorridor.sqrMagnitude < 0.5f
                    ? 0f : Mathf.Abs(Vector3.Dot(scanWidth, localCorridor)),
                signedContactDepthMm = skinIntersection.SignedDepth,
                contactFraction = skinIntersection.ContactPercent,
                provenance = "evaluator_only/provenance/" + provenanceFile,
                provenanceMatchedPublishedFrame = provenance.poseEpoch == capture.producerPoseEpoch
                    && provenance.producerUnityFrame == capture.producerUnityFrame
                    && provenance.producerScanSerial == capture.producerScanSerial,
                beamCount = provenance.beamCount,
                beamsWithSkeletonBeforeOrAtFirstShadow = skeletonVisible,
                beamsWithLfBeforeOrAtFirstShadow = lfVisible
            });
            if (!M0BObservationFresh(observation) || !observation.contactValidAfterExecution
                || !observation.workspaceValidAfterExecution
                || !evaluator.observations.Last().provenanceMatchedPublishedFrame
                || Mathf.Abs(evaluator.observations.Last().surfaceYawErrorDeg) > SweepCountYawToleranceDeg
                || evaluator.observations.Last().scanWidthAgainstCorridor < 0.95f)
                throw new InvalidOperationException(
                    "G2-R2 freshness, safety, yaw, scan-plane, or provenance check failed at " + stem);
        }

        private static bool M0BObservationFresh(G2R2ActorObservation item)
        {
            return item.poseEpoch == item.producerPoseEpoch
                && item.producerUnityFrame > item.poseAppliedUnityFrame
                && item.captureUnityFrame == item.producerUnityFrame
                && item.producerScanSerial > 0
                && item.activeAggregatorCount == 0;
        }

        private static void EnsureG2R2Healthy(G2R2ActorTrajectory actor, string leg)
        {
            if (!g2R2GroundTruthFreeExploration && actor.rejectedMovements > 0)
                throw new InvalidOperationException(
                    "G2-R2 stopped after the first rejected movement during " + leg);
            if (g2R2GroundTruthFreeExploration && actor.observations.Any(item =>
                    !item.executedSuccessfully && (item.safetyFailure
                        || !item.rollbackContactRestored
                        || !item.contactValidAfterExecution
                        || !item.workspaceValidAfterExecution)))
                throw new InvalidOperationException(
                    "Ground-truth-free boundary rejection did not retain a safe rollback during "
                    + leg);
            if (actor.observations.Any(item => item.safetyFailure
                    || !item.contactValidAfterExecution
                    || !item.workspaceValidAfterExecution
                    || !item.frameIntegrityValid))
                throw new InvalidOperationException(
                    "G2-R2 stopped after the first safety or integrity failure during " + leg);
        }

        private void WriteG2R2Artifacts(
            G2R2ActorTrajectory actor,
            G2R2PrivilegedTrajectory evaluator,
            G2R2RunSummary summary)
        {
            if (actor != null)
            {
                WriteSweepCountJson(Path.Combine(G2R2ResolvedOutputRoot, "trajectory_actor.json"), actor);
                WriteSweepCountJson(Path.Combine(G2R2ResolvedOutputRoot, "frame_manifest.json"),
                    new G2R2FrameManifest
                    {
                        generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                        frames = actor.observations
                    });
            }
            if (evaluator != null)
                WriteSweepCountJson(Path.Combine(
                    G2R2ResolvedOutputRoot, "evaluator_only", "trajectory_privileged.json"), evaluator);
            if (summary != null)
                WriteSweepCountJson(Path.Combine(G2R2ResolvedOutputRoot, "run_summary.json"), summary);
        }

        private void FinalizeG2R2FailureArtifacts(
            string status, string failureReason, bool returnedWithinTolerance)
        {
            if (g2R2ActiveActor != null)
            {
                g2R2ActiveActor.status = status;
                g2R2ActiveActor.stopReason = failureReason;
            }
            if (g2R2ActiveSummary != null)
            {
                g2R2ActiveSummary.status = status;
                g2R2ActiveSummary.g1 = "FAIL";
                g2R2ActiveSummary.acceptedMovements =
                    g2R2ActiveActor != null ? g2R2ActiveActor.acceptedMovements : 0;
                g2R2ActiveSummary.rejectedMovements =
                    g2R2ActiveActor != null ? g2R2ActiveActor.rejectedMovements : 0;
                g2R2ActiveSummary.validFrames = g2R2ActiveActor != null
                    ? g2R2ActiveActor.observations.Count(item => item.frameIntegrityValid) : 0;
                g2R2ActiveSummary.finalActorRelativeMm = g2R2ActiveRelativeMm;
                g2R2ActiveSummary.returnedWithinTolerance = returnedWithinTolerance;
                g2R2ActiveSummary.stopReason = failureReason;
            }
            WriteG2R2Artifacts(g2R2ActiveActor, g2R2ActiveEvaluator, g2R2ActiveSummary);
        }
    }
}


