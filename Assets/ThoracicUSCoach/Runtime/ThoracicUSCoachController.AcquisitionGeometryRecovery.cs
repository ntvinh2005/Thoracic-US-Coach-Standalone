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
        private const string G2GeometryRoot =
            "Artifacts/ThoracicUSCoach/sweep_count/G2_ACQUISITION_GEOMETRY_RECOVERY_20261031";
        private const int G2GeometryEndpointReverseSteps = 8;
        private const float G2GeometryParamedianOffsetMm = 6f;

        [Serializable]
        private sealed class G2GeometryConfig
        {
            public string mode = string.Empty;
            public string outputTag = string.Empty;
            public float surfaceYawDeg;
            public float lateralOffsetMm;
            public string intendedScanPlane = string.Empty;
        }

        [Serializable]
        private sealed class G2GeometryPreflight
        {
            public string contract = "thoracic_g2_acquisition_geometry_preflight_v1";
            public string generatedUtc = string.Empty;
            public string warning = "Evaluator-only geometry; never an actor input.";
            public string mode = string.Empty;
            public string intendedScanPlane = string.Empty;
            public string status = "BLOCKED";
            public float surfaceYawDeg;
            public float lateralOffsetMm;
            public float stepMm;
            public Vector3 centralContactPoint;
            public Vector3 geometryStartContactPoint;
            public Vector3 corridorAxis;
            public Vector3 lateralAxis;
            public Vector3 scanPlaneWidthAxis;
            public float scanWidthAgainstCorridor;
            public float scanWidthAgainstLateral;
            public float validatedCranialLimitMm;
            public float validatedCaudalLimitMm;
            public int plannedMovementBudget;
            public float originalLongitudinalHalfExtentMm;
            public float diagnosticLongitudinalHalfExtentMm;
            public float diagnosticWorkspaceCenterOffsetMm;
            public bool lateralOffsetWithinConfiguredSafetyExtent;
            public bool connectedTwoSidedSkinCorridor;
            public string decision = string.Empty;
        }

        [Serializable]
        private sealed class G2GeometryActorObservation
        {
            public int observationIndex;
            public string phase = string.Empty;
            public string requestedAction = string.Empty;
            public bool executedSuccessfully;
            public float requestedDisplacementMm;
            public float attemptedDisplacementMm;
            public float achievedDisplacementMm;
            public float signedActorRelativeMm;
            public float cumulativeExecutedTravelMm;
            public bool contactValidAfterExecution;
            public bool workspaceValidAfterExecution;
            public bool rollbackAttempted;
            public bool rollbackContactRestored;
            public bool safetyFailure;
            public string rejectionReason = string.Empty;
            public string image = string.Empty;
            public string imageSha256 = string.Empty;
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
        private sealed class G2GeometryActorTrajectory
        {
            public string contract = "thoracic_g2_acquisition_geometry_actor_v1";
            public string generatedUtc = string.Empty;
            public string scheduleDisclosure =
                "Evaluator-planned geometry recovery route; actor analysis receives pixels and executed relative movement only.";
            public string allowedInputs =
                "Clean ultrasound pixels, executed action, action result, and accumulated relative executed displacement.";
            public string excludedInputs =
                "World pose, T-levels, anatomy bounds, mesh identities, renderer provenance, and evaluator coordinates.";
            public float configuredStepMm;
            public int movementBudget;
            public string status = "running";
            public string stopReason = string.Empty;
            public int acceptedMovements;
            public int rejectedMovements;
            public List<G2GeometryActorObservation> observations =
                new List<G2GeometryActorObservation>();
        }

        [Serializable]
        private sealed class G2GeometryPrivilegedObservation
        {
            public int observationIndex;
            public string phase = string.Empty;
            public Vector3 probePosition;
            public Quaternion probeRotation;
            public Vector3 contactPoint;
            public Vector3 surfaceNormal;
            public float evaluatorLongitudinalCoordinateMm;
            public float achievedSurfaceYawDeg;
            public float surfaceYawErrorDeg;
            public float normalAlignmentErrorDeg;
            public Vector3 scanPlaneWidthAxis;
            public float scanWidthAgainstCorridor;
            public float scanWidthAgainstLateral;
            public float signedContactDepthMm;
            public float contactFraction;
            public string provenance = string.Empty;
            public bool provenanceMatchedPublishedFrame;
        }

        [Serializable]
        private sealed class G2GeometryPrivilegedTrajectory
        {
            public string contract = "thoracic_g2_acquisition_geometry_evaluator_v1";
            public string generatedUtc = string.Empty;
            public string warning = "Evaluator-only pose, geometry, and renderer provenance; never an actor input.";
            public string mode = string.Empty;
            public string intendedScanPlane = string.Empty;
            public float commandedSurfaceYawDeg;
            public float evaluatorSetupLateralOffsetMm;
            public Vector3 startContactPoint;
            public Vector3 corridorAxis;
            public Vector3 lateralAxis;
            public Vector3 originalWorkspaceOrigin;
            public Vector3 diagnosticWorkspaceOrigin;
            public float originalLongitudinalHalfExtentMm;
            public float diagnosticLongitudinalHalfExtentMm;
            public List<G2GeometryPrivilegedObservation> observations =
                new List<G2GeometryPrivilegedObservation>();
        }

        [Serializable]
        private sealed class G2GeometryFrameManifest
        {
            public string contract = "thoracic_g2_acquisition_geometry_frame_manifest_v1";
            public string generatedUtc = string.Empty;
            public string inputIsolation =
                "No world pose, anatomical label, mesh identity, geometry mode, yaw, lateral setup offset, or evaluator coordinate is present.";
            public List<G2GeometryActorObservation> frames =
                new List<G2GeometryActorObservation>();
        }

        [Serializable]
        private sealed class G2GeometryRunSummary
        {
            public string contract = "thoracic_g2_acquisition_geometry_summary_v1";
            public string generatedUtc = string.Empty;
            public string mode = string.Empty;
            public string intendedScanPlane = string.Empty;
            public string status = "running";
            public string physicalSampling = "NOT_RUN";
            public string imageIntegrity = "NOT_RUN";
            public string scientificStatus = "G2_PENDING_OFFLINE_ANALYSIS";
            public string stopReason = string.Empty;
            public int acceptedMovements;
            public int rejectedMovements;
            public int validFrames;
            public int totalFrames;
            public float minimumEvaluatorCoordinateMm;
            public float maximumEvaluatorCoordinateMm;
            public float maximumYawErrorDeg;
            public float maximumNormalAlignmentErrorDeg;
            public float minimumScanWidthAgainstExpectedAxis;
            public bool originalWorkspaceRestored;
        }

        private Coroutine g2GeometryRoutine;
        private bool g2GeometryComplete;
        private string g2GeometryFailure = string.Empty;
        private string g2GeometryStatus = "idle";
        private string g2GeometryOutputRoot = string.Empty;

        public bool G2GeometryComplete => g2GeometryComplete;
        public string G2GeometryFailure => g2GeometryFailure;
        public string G2GeometryStatus => g2GeometryStatus;
        public string G2GeometryOutputRoot => g2GeometryOutputRoot;

        public bool StartG2AcquisitionGeometryRecovery(string mode)
        {
            if (!Application.isPlaying || g2GeometryRoutine != null) return false;
            G2GeometryConfig config = G2GeometryConfiguration(mode);
            g2GeometryComplete = false;
            g2GeometryFailure = string.Empty;
            g2GeometryStatus = "starting";
            g2GeometryOutputRoot = Path.Combine(G2GeometryRoot, config.outputTag);
            g2GeometryRoutine = StartCoroutine(RunG2Geometry(config));
            return true;
        }

        private static G2GeometryConfig G2GeometryConfiguration(string mode)
        {
            string normalized = (mode ?? string.Empty).Trim().ToUpperInvariant();
            if (normalized == "TRANSVERSE_PARAMEDIAN_P6")
                return new G2GeometryConfig
                {
                    mode = normalized,
                    outputTag = normalized,
                    surfaceYawDeg = 0f,
                    lateralOffsetMm = G2GeometryParamedianOffsetMm,
                    intendedScanPlane = "TRANSVERSE_PARAMEDIAN"
                };
            if (normalized == "PARASAGITTAL_CENTER")
                return new G2GeometryConfig
                {
                    mode = normalized,
                    outputTag = normalized,
                    surfaceYawDeg = 90f,
                    lateralOffsetMm = 0f,
                    intendedScanPlane = "LONGITUDINAL_PARASAGITTAL"
                };
            throw new ArgumentException("Unsupported G2 acquisition geometry mode: " + mode);
        }

        private IEnumerator RunG2Geometry(G2GeometryConfig config)
        {
            Stack<IEnumerator> stack = new Stack<IEnumerator>();
            stack.Push(RunG2GeometryCore(config));
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
            g2GeometryFailure = failure.ToString();
            g2GeometryStatus = "INFRASTRUCTURE_OR_SAFETY_FAILURE";
            g2GeometryRoutine = null;
            g2GeometryComplete = true;
            Debug.LogError("[ThoracicUSCoach][G2Geometry] " + failure);
        }

        private IEnumerator RunG2GeometryCore(G2GeometryConfig config)
        {
            AutoWire();
            Vector3 originalProbePosition = probeRoot != null ? probeRoot.position : Vector3.zero;
            Quaternion originalProbeRotation = probeRoot != null ? probeRoot.rotation : Quaternion.identity;
            float originalStep = active2DStepMm;
            float originalLongitudinalExtent = active2DLongitudinalHalfExtentMm;
            float originalLateralExtent = active2DLateralHalfExtentMm;
            float originalSweepYaw = sweepCountYawDeg;
            Vector3 originalWorkspaceOrigin = active2DWorkspaceOrigin;
            Vector3 originalCaudalAxis = active2DCaudalAxis;
            Vector3 originalLateralAxis = active2DLateralAxis;
            G2GeometryActorTrajectory actor = null;
            G2GeometryPrivilegedTrajectory evaluator = null;
            G2GeometryRunSummary summary = null;
            string evaluatorRoot = Path.Combine(g2GeometryOutputRoot, "evaluator_only");
            string frameRoot = Path.Combine(g2GeometryOutputRoot, "frames");
            string provenanceRoot = Path.Combine(evaluatorRoot, "provenance");
            try
            {
                if (!ValidateReferences(out string referenceError))
                    throw new InvalidOperationException(referenceError);
                if (probeRoot == null || faceCenter == null || rearCenter == null
                    || skinCollider == null || skinIntersection == null)
                    throw new InvalidOperationException(
                        "G2 geometry recovery requires probe, face/rear centers, exact skin collider, and contact measurement");
                if (Directory.Exists(g2GeometryOutputRoot))
                    throw new InvalidOperationException(
                        "G2 geometry output already exists; refusing to overwrite or reset the development registry");
                Directory.CreateDirectory(frameRoot);
                Directory.CreateDirectory(provenanceRoot);

                SweepCountReferenceSet references = BuildSweepCountReferences();
                Vector3 t2 = references.references[0].contactPoint;
                Vector3 t8 = references.references[6].contactPoint;
                Vector3 corridorAxis = (t8 - t2).normalized;
                Vector3 centralProposal = 0.5f * (t2 + t8);
                if (!FindSurfaceAt(centralProposal, Vector3.zero, out RaycastHit centralHit))
                    throw new InvalidOperationException("G2 geometry could not project the central Home contact");
                Vector3 lateralAxis = Vector3.Cross(centralHit.normal, corridorAxis).normalized;
                if (lateralAxis.sqrMagnitude < 0.5f)
                    throw new InvalidOperationException("G2 geometry lateral setup axis is degenerate");
                Vector3 geometryProposal = centralHit.point + lateralAxis * config.lateralOffsetMm;
                if (!FindSurfaceAt(geometryProposal, centralHit.normal, out RaycastHit geometryHit))
                    throw new InvalidOperationException("G2 geometry could not project its setup offset to Skin");
                Vector3 startContact = geometryHit.point;
                corridorAxis = Vector3.ProjectOnPlane(corridorAxis, geometryHit.normal).normalized;
                lateralAxis = Vector3.Cross(geometryHit.normal, corridorAxis).normalized;
                List<BoundarySurfaceSample> cranialSurface = TraceBoundarySurface(
                    geometryHit, -corridorAxis, startContact, corridorAxis,
                    BoundaryMaximumPreflightStepsPerDirection);
                List<BoundarySurfaceSample> caudalSurface = TraceBoundarySurface(
                    geometryHit, corridorAxis, startContact, corridorAxis,
                    BoundaryMaximumPreflightStepsPerDirection);
                if (cranialSurface.Count < 12 || caudalSurface.Count < 12)
                    throw new InvalidOperationException(
                        "G2 geometry connected skin corridor is too short for full two-sided sampling");

                int caudalOutboundSteps = caudalSurface.Count - 2;
                int cranialOutboundSteps = cranialSurface.Count - 2;
                int movementBudget = caudalOutboundSteps * 2
                    + cranialOutboundSteps + G2GeometryEndpointReverseSteps;
                CalculateM0BDiagnosticWorkspace(
                    cranialSurface.Last().signedLongitudinalMm,
                    caudalSurface.Last().signedLongitudinalMm,
                    out float diagnosticCenterOffset,
                    out float diagnosticHalfExtent);

                active2DStepMm = BoundaryPreflightStepMm;
                sweepCountYawDeg = config.surfaceYawDeg;
                if (!PlaceSweepCountProbe(startContact, geometryHit.normal, corridorAxis,
                        out string placementFailure))
                    throw new InvalidOperationException("G2 geometry start placement failed: " + placementFailure);
                active2DCaudalAxis = Vector3.ProjectOnPlane(
                    corridorAxis, CurrentSurfaceNormal()).normalized;
                active2DLateralAxis = Vector3.Cross(
                    CurrentSurfaceNormal(), active2DCaudalAxis).normalized;
                caudalTangent = active2DCaudalAxis;
                active2DWorkspaceOrigin = faceCenter.position
                    + active2DCaudalAxis * diagnosticCenterOffset;
                active2DLongitudinalHalfExtentMm = diagnosticHalfExtent;
                active2DRejectedMoves = 0;
                active2DContactViolations = 0;
                active2DWorkspaceViolations = 0;
                Vector3 scanWidth = Vector3.ProjectOnPlane(
                    probeRoot.right, CurrentSurfaceNormal()).normalized;
                float widthAgainstCorridor = Mathf.Abs(Vector3.Dot(scanWidth, active2DCaudalAxis));
                float widthAgainstLateral = Mathf.Abs(Vector3.Dot(scanWidth, active2DLateralAxis));
                float expectedAlignment = config.intendedScanPlane == "LONGITUDINAL_PARASAGITTAL"
                    ? widthAgainstCorridor : widthAgainstLateral;
                if (expectedAlignment < 0.95f)
                    throw new InvalidOperationException(
                        "G2 geometry scan-plane width does not match the intended acquisition plane");

                G2GeometryPreflight preflight = new G2GeometryPreflight
                {
                    generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    mode = config.mode,
                    intendedScanPlane = config.intendedScanPlane,
                    status = "PASS",
                    surfaceYawDeg = config.surfaceYawDeg,
                    lateralOffsetMm = config.lateralOffsetMm,
                    stepMm = active2DStepMm,
                    centralContactPoint = centralHit.point,
                    geometryStartContactPoint = faceCenter.position,
                    corridorAxis = active2DCaudalAxis,
                    lateralAxis = active2DLateralAxis,
                    scanPlaneWidthAxis = scanWidth,
                    scanWidthAgainstCorridor = widthAgainstCorridor,
                    scanWidthAgainstLateral = widthAgainstLateral,
                    validatedCranialLimitMm = cranialSurface.Last().signedLongitudinalMm,
                    validatedCaudalLimitMm = caudalSurface.Last().signedLongitudinalMm,
                    plannedMovementBudget = movementBudget,
                    originalLongitudinalHalfExtentMm = originalLongitudinalExtent,
                    diagnosticLongitudinalHalfExtentMm = diagnosticHalfExtent,
                    diagnosticWorkspaceCenterOffsetMm = diagnosticCenterOffset,
                    lateralOffsetWithinConfiguredSafetyExtent =
                        Mathf.Abs(config.lateralOffsetMm) <= originalLateralExtent,
                    connectedTwoSidedSkinCorridor = true,
                    decision =
                        "The setup offset and yaw are evaluator-only acquisition controls. The actor trace receives neither value."
                };
                WriteSweepCountJson(Path.Combine(g2GeometryOutputRoot, "geometry_preflight.json"), preflight);

                if (!UltrasoundManager.TryGet(UltrasoundManager.Plane.Primary,
                        out UltrasoundManager manager) || manager == null)
                    throw new InvalidOperationException("G2 geometry primary ultrasound manager is unavailable");
                MiniZigzagInstrumentationCheck instrumentation =
                    RunMiniZigzagInstrumentationCheck(manager, evaluatorRoot);
                WriteSweepCountJson(Path.Combine(evaluatorRoot, "instrumentation_check.json"),
                    instrumentation);
                if (!instrumentation.exactPixelParity || !instrumentation.metadataMatched)
                    throw new InvalidOperationException(
                        "G2 geometry provenance instrumentation parity or metadata check failed");

                actor = new G2GeometryActorTrajectory
                {
                    generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    configuredStepMm = active2DStepMm,
                    movementBudget = movementBudget
                };
                evaluator = new G2GeometryPrivilegedTrajectory
                {
                    generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    mode = config.mode,
                    intendedScanPlane = config.intendedScanPlane,
                    commandedSurfaceYawDeg = config.surfaceYawDeg,
                    evaluatorSetupLateralOffsetMm = config.lateralOffsetMm,
                    startContactPoint = faceCenter.position,
                    corridorAxis = active2DCaudalAxis,
                    lateralAxis = active2DLateralAxis,
                    originalWorkspaceOrigin = originalWorkspaceOrigin,
                    diagnosticWorkspaceOrigin = active2DWorkspaceOrigin,
                    originalLongitudinalHalfExtentMm = originalLongitudinalExtent,
                    diagnosticLongitudinalHalfExtentMm = diagnosticHalfExtent
                };
                summary = new G2GeometryRunSummary
                {
                    generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    mode = config.mode,
                    intendedScanPlane = config.intendedScanPlane
                };

                float signedRelative = 0f;
                float cumulativeTravel = 0f;
                yield return CaptureG2GeometryObservation(
                    config, manager, actor, evaluator, frameRoot, provenanceRoot,
                    "baseline", Active2DAction.Observe, true, signedRelative, cumulativeTravel);
                yield return RunG2GeometryLeg(config, manager, actor, evaluator, frameRoot,
                    provenanceRoot, "caudal_outbound", Active2DAction.Caudal,
                    caudalOutboundSteps, value => signedRelative = value, () => signedRelative,
                    value => cumulativeTravel = value, () => cumulativeTravel);
                EnsureG2GeometryRouteHealthy(actor, "caudal outbound");
                yield return RunG2GeometryLeg(config, manager, actor, evaluator, frameRoot,
                    provenanceRoot, "caudal_reverse", Active2DAction.Cranial,
                    caudalOutboundSteps, value => signedRelative = value, () => signedRelative,
                    value => cumulativeTravel = value, () => cumulativeTravel);
                EnsureG2GeometryRouteHealthy(actor, "caudal reverse");
                yield return RunG2GeometryLeg(config, manager, actor, evaluator, frameRoot,
                    provenanceRoot, "cranial_outbound", Active2DAction.Cranial,
                    cranialOutboundSteps, value => signedRelative = value, () => signedRelative,
                    value => cumulativeTravel = value, () => cumulativeTravel);
                EnsureG2GeometryRouteHealthy(actor, "cranial outbound");
                yield return RunG2GeometryLeg(config, manager, actor, evaluator, frameRoot,
                    provenanceRoot, "cranial_reverse", Active2DAction.Caudal,
                    G2GeometryEndpointReverseSteps,
                    value => signedRelative = value, () => signedRelative,
                    value => cumulativeTravel = value, () => cumulativeTravel);
                EnsureG2GeometryRouteHealthy(actor, "cranial reverse");

                bool allFresh = actor.observations.All(G2GeometryObservationFresh);
                bool allFramesValid = actor.observations.All(item => item.frameIntegrityValid);
                bool allSafe = actor.observations.All(item =>
                    item.contactValidAfterExecution && item.workspaceValidAfterExecution
                    && !item.safetyFailure);
                bool allPoseValid = evaluator.observations.All(item =>
                    Mathf.Abs(item.surfaceYawErrorDeg) <= SweepCountYawToleranceDeg
                    && item.normalAlignmentErrorDeg <= MiniZigzagNormalToleranceDeg
                    && item.provenanceMatchedPublishedFrame);
                float minimumExpectedAlignment = evaluator.observations.Min(item =>
                    config.intendedScanPlane == "LONGITUDINAL_PARASAGITTAL"
                        ? item.scanWidthAgainstCorridor : item.scanWidthAgainstLateral);
                bool planeValid = minimumExpectedAlignment >= 0.95f;
                bool complete = actor.acceptedMovements == movementBudget
                    && actor.rejectedMovements == 0
                    && actor.observations.Count == movementBudget + 1;
                bool eligible = complete && allFresh && allFramesValid && allSafe
                    && allPoseValid && planeValid;
                actor.status = eligible ? "complete_eligible" : "complete_ineligible";
                actor.stopReason = eligible
                    ? "bounded geometry route completed"
                    : "route, safety, freshness, integrity, orientation, or scan-plane gate failed";
                summary.status = actor.status;
                summary.physicalSampling = eligible ? "PASS" : "FAIL";
                summary.imageIntegrity = allFresh && allFramesValid && allPoseValid
                    ? "PASS" : "FAIL";
                summary.scientificStatus = eligible
                    ? "G2_PENDING_BLIND_ANALYSIS" : "G1_GEOMETRY_FAIL_G2_NOT_RUN";
                summary.stopReason = actor.stopReason;
                summary.acceptedMovements = actor.acceptedMovements;
                summary.rejectedMovements = actor.rejectedMovements;
                summary.validFrames = actor.observations.Count(item => item.frameIntegrityValid);
                summary.totalFrames = actor.observations.Count;
                summary.minimumEvaluatorCoordinateMm = evaluator.observations.Min(
                    item => item.evaluatorLongitudinalCoordinateMm);
                summary.maximumEvaluatorCoordinateMm = evaluator.observations.Max(
                    item => item.evaluatorLongitudinalCoordinateMm);
                summary.maximumYawErrorDeg = evaluator.observations.Max(
                    item => Mathf.Abs(item.surfaceYawErrorDeg));
                summary.maximumNormalAlignmentErrorDeg = evaluator.observations.Max(
                    item => item.normalAlignmentErrorDeg);
                summary.minimumScanWidthAgainstExpectedAxis = minimumExpectedAlignment;
                g2GeometryStatus = eligible
                    ? "PILOT_ELIGIBLE_PENDING_BLIND_ANALYSIS" : "PILOT_INELIGIBLE";
                WriteG2GeometryArtifacts(actor, evaluator, summary);
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
                    probeRoot.SetPositionAndRotation(originalProbePosition, originalProbeRotation);
                    EnforceRigidContact();
                }
                if (summary != null)
                {
                    summary.originalWorkspaceRestored =
                        Mathf.Approximately(active2DLongitudinalHalfExtentMm,
                            originalLongitudinalExtent)
                        && active2DWorkspaceOrigin == originalWorkspaceOrigin;
                    WriteSweepCountJson(Path.Combine(g2GeometryOutputRoot, "run_summary.json"),
                        summary);
                }
                g2GeometryRoutine = null;
                g2GeometryComplete = true;
            }
        }

        private IEnumerator RunG2GeometryLeg(
            G2GeometryConfig config,
            UltrasoundManager manager,
            G2GeometryActorTrajectory actor,
            G2GeometryPrivilegedTrajectory evaluator,
            string frameRoot,
            string provenanceRoot,
            string phase,
            Active2DAction action,
            int steps,
            Action<float> setSignedRelative,
            Func<float> getSignedRelative,
            Action<float> setCumulativeTravel,
            Func<float> getCumulativeTravel)
        {
            for (int step = 0; step < steps; step++)
            {
                bool accepted = TryG2GeometryPhysicalAction(action, config.surfaceYawDeg);
                if (!accepted)
                {
                    actor.rejectedMovements++;
                    actor.stopReason = phase + " movement rejected: "
                        + active2DLastActionExecution.rejectionReason;
                    yield break;
                }
                actor.acceptedMovements++;
                float direction = action == Active2DAction.Caudal ? 1f : -1f;
                setSignedRelative(getSignedRelative()
                    + direction * active2DLastActionExecution.achievedDisplacementMm);
                setCumulativeTravel(getCumulativeTravel()
                    + active2DLastActionExecution.achievedDisplacementMm);
                yield return CaptureG2GeometryObservation(
                    config, manager, actor, evaluator, frameRoot, provenanceRoot,
                    phase, action, true, getSignedRelative(), getCumulativeTravel());
                if (active2DLastActionExecution.safetyFailure) yield break;
            }
        }

        private IEnumerator CaptureG2GeometryObservation(
            G2GeometryConfig config,
            UltrasoundManager manager,
            G2GeometryActorTrajectory actor,
            G2GeometryPrivilegedTrajectory evaluator,
            string frameRoot,
            string provenanceRoot,
            string phase,
            Active2DAction action,
            bool accepted,
            float signedRelative,
            float cumulativeTravel)
        {
            int index = actor.observations.Count;
            DirectFreshCaptureResult capture = new DirectFreshCaptureResult();
            DirectFreshPoseToken token = BeginDirectFreshCapturePose(
                "g2_geometry_" + index.ToString("D4", CultureInfo.InvariantCulture), true);
            yield return CaptureDirectProducerFreshFrame(token,
                action == Active2DAction.Observe ? "observe" : "completed_movement",
                capture, true);
            if (!manager.TryGetPublishedScanProvenance(
                    capture.producerPoseEpoch, capture.producerScanSerial,
                    out UltrasoundManager.PublishedScanProvenanceSnapshot provenance))
                throw new InvalidOperationException(
                    "G2 geometry provenance does not match the published pose epoch and scan serial");
            string fileName = "frame_" + index.ToString("D4", CultureInfo.InvariantCulture) + ".png";
            File.WriteAllBytes(Path.Combine(frameRoot, fileName), capture.pngBytes);
            string provenanceFile = "frame_" + index.ToString("D4", CultureInfo.InvariantCulture) + ".json";
            WriteSweepCountJson(Path.Combine(provenanceRoot, provenanceFile), provenance);
            bool observe = action == Active2DAction.Observe;
            Active2DActionExecution execution = active2DLastActionExecution;
            actor.observations.Add(new G2GeometryActorObservation
            {
                observationIndex = index,
                phase = phase,
                requestedAction = action.ToString().ToUpperInvariant(),
                executedSuccessfully = accepted,
                requestedDisplacementMm = observe ? 0f : execution.requestedDisplacementMm,
                attemptedDisplacementMm = observe ? 0f : execution.attemptedDisplacementMm,
                achievedDisplacementMm = observe ? 0f : execution.achievedDisplacementMm,
                signedActorRelativeMm = signedRelative,
                cumulativeExecutedTravelMm = cumulativeTravel,
                contactValidAfterExecution = observe ? MeasureSweepCountContact()
                    : execution.contactValidAfterExecution,
                workspaceValidAfterExecution = observe
                    ? IsActive2DWorkspaceValid(faceCenter.position)
                    : execution.workspaceValidAfterExecution,
                rollbackAttempted = !observe && execution.rollbackAttempted,
                rollbackContactRestored = !observe && execution.rollbackContactRestored,
                safetyFailure = !observe && execution.safetyFailure,
                rejectionReason = observe ? string.Empty : execution.rejectionReason,
                image = "frames/" + fileName,
                imageSha256 = Sha256Bytes(capture.pngBytes),
                imageWidth = capture.image != null ? capture.image.width : 0,
                imageHeight = capture.image != null ? capture.image.height : 0,
                frameIntegrityValid = M0BFrameIntegrityValid(capture.image, capture.pngBytes),
                poseEpoch = capture.poseEpoch,
                poseAppliedUnityFrame = capture.poseAppliedUnityFrame,
                producerPoseEpoch = capture.producerPoseEpoch,
                producerUnityFrame = capture.producerUnityFrame,
                producerScanSerial = capture.producerScanSerial,
                captureUnityFrame = capture.captureUnityFrame,
                activeAggregatorCount = capture.activeAggregatorCount
            });
            Vector3 normal = CurrentSurfaceNormal();
            Vector3 outward = (rearCenter.position - faceCenter.position).normalized;
            Vector3 scanWidth = Vector3.ProjectOnPlane(probeRoot.right, normal).normalized;
            evaluator.observations.Add(new G2GeometryPrivilegedObservation
            {
                observationIndex = index,
                phase = phase,
                probePosition = capture.capturedPosition,
                probeRotation = capture.capturedRotation,
                contactPoint = faceCenter.position,
                surfaceNormal = normal,
                evaluatorLongitudinalCoordinateMm = Vector3.Dot(
                    faceCenter.position - evaluator.startContactPoint,
                    evaluator.corridorAxis),
                achievedSurfaceYawDeg = MeasureSweepCountSurfaceYawDeg(normal),
                surfaceYawErrorDeg = Mathf.DeltaAngle(
                    config.surfaceYawDeg, MeasureSweepCountSurfaceYawDeg(normal)),
                normalAlignmentErrorDeg = Vector3.Angle(outward, normal),
                scanPlaneWidthAxis = scanWidth,
                scanWidthAgainstCorridor = Mathf.Abs(Vector3.Dot(scanWidth, evaluator.corridorAxis)),
                scanWidthAgainstLateral = Mathf.Abs(Vector3.Dot(scanWidth, evaluator.lateralAxis)),
                signedContactDepthMm = skinIntersection.SignedDepth,
                contactFraction = skinIntersection.ContactPercent,
                provenance = "evaluator_only/provenance/" + provenanceFile,
                provenanceMatchedPublishedFrame = provenance.poseEpoch == capture.producerPoseEpoch
                    && provenance.producerUnityFrame == capture.producerUnityFrame
                    && provenance.producerScanSerial == capture.producerScanSerial
            });
            G2GeometryActorObservation stored = actor.observations.Last();
            if (!stored.frameIntegrityValid || !G2GeometryObservationFresh(stored)
                || !stored.contactValidAfterExecution || !stored.workspaceValidAfterExecution
                || !evaluator.observations.Last().provenanceMatchedPublishedFrame)
                throw new InvalidOperationException(
                    "G2 geometry frame integrity, freshness, safety, or provenance failed at observation "
                    + index.ToString(CultureInfo.InvariantCulture));
        }

        private bool TryG2GeometryPhysicalAction(Active2DAction action, float surfaceYawDeg)
        {
            BeginActive2DActionExecution(action);
            if (action == Active2DAction.Caudal)
                return TryActive2DMove(active2DCaudalAxis, surfaceYawDeg);
            if (action == Active2DAction.Cranial)
                return TryActive2DMove(-active2DCaudalAxis, surfaceYawDeg);
            return RejectActive2DActionWithoutPoseChange(
                "G2 geometry recovery accepts only cranial or caudal translation");
        }

        private static bool G2GeometryObservationFresh(G2GeometryActorObservation item)
        {
            return item.poseEpoch == item.producerPoseEpoch
                && item.producerUnityFrame > item.poseAppliedUnityFrame
                && item.captureUnityFrame == item.producerUnityFrame
                && item.producerScanSerial > 0
                && item.activeAggregatorCount == 0;
        }

        private static void EnsureG2GeometryRouteHealthy(
            G2GeometryActorTrajectory actor, string leg)
        {
            if (actor.rejectedMovements > 0)
                throw new InvalidOperationException(
                    "G2 geometry stopped after the first rejected movement during " + leg);
            if (actor.observations.Any(item => item.safetyFailure
                    || !item.contactValidAfterExecution
                    || !item.workspaceValidAfterExecution
                    || !item.frameIntegrityValid))
                throw new InvalidOperationException(
                    "G2 geometry stopped after the first safety or integrity failure during " + leg);
        }

        private void WriteG2GeometryArtifacts(
            G2GeometryActorTrajectory actor,
            G2GeometryPrivilegedTrajectory evaluator,
            G2GeometryRunSummary summary)
        {
            if (actor != null)
            {
                WriteSweepCountJson(Path.Combine(g2GeometryOutputRoot, "trajectory_actor.json"), actor);
                WriteSweepCountJson(Path.Combine(g2GeometryOutputRoot, "frame_manifest.json"),
                    new G2GeometryFrameManifest
                    {
                        generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                        frames = actor.observations
                    });
            }
            if (evaluator != null)
                WriteSweepCountJson(Path.Combine(
                    g2GeometryOutputRoot, "evaluator_only", "trajectory_privileged.json"), evaluator);
            if (summary != null)
                WriteSweepCountJson(Path.Combine(g2GeometryOutputRoot, "run_summary.json"), summary);
        }
    }
}
