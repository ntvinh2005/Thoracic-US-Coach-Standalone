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
        private const string M0BOutputDirectory =
            "Artifacts/ThoracicUSCoach/boundary_t7/M0B_VISUAL_BOUNDARY_FEASIBILITY_20261030";
        private const float M0BYawDeg = 0f;
        private const float M0BReversalToleranceMm = 6f;
        private const int M0BEndpointReverseSteps = 8;

        [Serializable]
        private sealed class M0BGeometryAndWorkspacePreflight
        {
            public string contract = "thoracic_visual_boundary_m0b_preflight_v1";
            public string generatedUtc = string.Empty;
            public string scene = string.Empty;
            public string status = "BLOCKED";
            public string warning = "Evaluator-only geometry and workspace plan; never an actor input.";
            public float stepMm;
            public float yawDeg;
            public Vector3 startContactPoint;
            public Vector3 corridorAxis;
            public float validatedCranialLimitMm;
            public float validatedCaudalLimitMm;
            public int plannedCaudalOutboundSteps;
            public int plannedCaudalReverseSteps;
            public int plannedCranialOutboundSteps;
            public int plannedCranialReverseSteps;
            public int plannedMovementBudget;
            public float plannedCranialEndpointMm;
            public float plannedCaudalEndpointMm;
            public float originalLongitudinalHalfExtentMm;
            public float diagnosticLongitudinalHalfExtentMm;
            public float diagnosticWorkspaceCenterOffsetMm;
            public bool diagnosticWorkspaceWithinValidatedSkinCorridor;
            public bool skeletonAabbExcludedFromStoppingRule = true;
            public float reversalToleranceMm;
            public string startMethod = string.Empty;
            public string decision = string.Empty;
        }

        [Serializable]
        private sealed class M0BActorObservation
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
        private sealed class M0BActorTrajectory
        {
            public string contract = "thoracic_visual_boundary_m0b_actor_v1";
            public string generatedUtc = string.Empty;
            public string scheduleDisclosure =
                "Evaluator-planned bounded route; analysis inputs are pixels and executed relative movement only.";
            public string allowedInputs =
                "Clean ultrasound pixels, executed action, action result, and accumulated relative executed displacement.";
            public string excludedInputs =
                "World pose, T-levels, anatomy bounds, mesh identities, renderer provenance, and evaluator coordinates.";
            public float configuredStepMm;
            public float commandedYawDeg;
            public int movementBudget;
            public string status = "running";
            public string stopReason = string.Empty;
            public int acceptedMovements;
            public int rejectedMovements;
            public List<M0BActorObservation> observations = new List<M0BActorObservation>();
        }

        [Serializable]
        private sealed class M0BPrivilegedObservation
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
            public float signedContactDepthMm;
            public float contactFraction;
            public string provenance = string.Empty;
            public bool provenanceMatchedPublishedFrame;
        }

        [Serializable]
        private sealed class M0BPrivilegedTrajectory
        {
            public string contract = "thoracic_visual_boundary_m0b_evaluator_v1";
            public string generatedUtc = string.Empty;
            public string warning = "Evaluator-only geometry and renderer provenance; never an actor input.";
            public Vector3 startContactPoint;
            public Vector3 corridorAxis;
            public Vector3 originalWorkspaceOrigin;
            public Vector3 diagnosticWorkspaceOrigin;
            public float originalLongitudinalHalfExtentMm;
            public float diagnosticLongitudinalHalfExtentMm;
            public List<M0BPrivilegedObservation> observations = new List<M0BPrivilegedObservation>();
        }

        [Serializable]
        private sealed class M0BFrameManifest
        {
            public string contract = "thoracic_visual_boundary_m0b_frame_manifest_v1";
            public string generatedUtc = string.Empty;
            public string inputIsolation =
                "No world pose, anatomy label, mesh identity, or evaluator coordinate is present.";
            public List<M0BActorObservation> frames = new List<M0BActorObservation>();
        }

        [Serializable]
        private sealed class M0BRunSummary
        {
            public string contract = "thoracic_visual_boundary_m0b_summary_v1";
            public string generatedUtc = string.Empty;
            public string status = "running";
            public string physicalSampling = "NOT_RUN";
            public string imageIntegrity = "NOT_RUN";
            public string caudalVisualBoundary = "PENDING_BLIND_ANALYSIS";
            public string cranialVisualBoundary = "PENDING_BLIND_ANALYSIS";
            public string reversalRepeatability = "PENDING_BLIND_ANALYSIS";
            public string overallM0B = "PENDING_BLIND_ANALYSIS";
            public string stopReason = string.Empty;
            public int acceptedMovements;
            public int rejectedMovements;
            public int validFrames;
            public int totalFrames;
            public float minimumEvaluatorCoordinateMm;
            public float maximumEvaluatorCoordinateMm;
            public float plannedCranialEndpointMm;
            public float plannedCaudalEndpointMm;
            public float maximumYawErrorDeg;
            public float maximumNormalAlignmentErrorDeg;
            public bool originalWorkspaceRestored;
            public string g2Status = "FAIL";
            public string g3Status = "NOT_RUN";
            public string g4Status = "NOT_RUN";
        }

        private Coroutine m0BVisualBoundaryRoutine;
        private bool m0BVisualBoundaryComplete;
        private string m0BVisualBoundaryInfrastructureFailure = string.Empty;
        private string m0BVisualBoundaryStatus = "idle";

        public bool M0BVisualBoundaryComplete => m0BVisualBoundaryComplete;
        public string M0BVisualBoundaryInfrastructureFailure =>
            m0BVisualBoundaryInfrastructureFailure;
        public string M0BVisualBoundaryStatus => m0BVisualBoundaryStatus;
        public string M0BVisualBoundaryOutputRoot => M0BOutputDirectory;

        public bool StartM0BVisualBoundaryPilot()
        {
            if (!Application.isPlaying || m0BVisualBoundaryRoutine != null) return false;
            m0BVisualBoundaryComplete = false;
            m0BVisualBoundaryInfrastructureFailure = string.Empty;
            m0BVisualBoundaryStatus = "starting";
            m0BVisualBoundaryRoutine = StartCoroutine(RunM0BVisualBoundaryPilot());
            return true;
        }

        private IEnumerator RunM0BVisualBoundaryPilot()
        {
            Stack<IEnumerator> stack = new Stack<IEnumerator>();
            stack.Push(RunM0BVisualBoundaryPilotCore());
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
            m0BVisualBoundaryInfrastructureFailure = failure.ToString();
            m0BVisualBoundaryStatus = "INFRASTRUCTURE_OR_SAFETY_FAILURE";
            m0BVisualBoundaryRoutine = null;
            m0BVisualBoundaryComplete = true;
            Debug.LogError("[ThoracicUSCoach][M0B] " + failure);
        }

        private IEnumerator RunM0BVisualBoundaryPilotCore()
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
            M0BActorTrajectory actor = null;
            M0BPrivilegedTrajectory evaluator = null;
            M0BRunSummary summary = null;
            string evaluatorRoot = Path.Combine(M0BOutputDirectory, "evaluator_only");
            string frameRoot = Path.Combine(M0BOutputDirectory, "frames");
            string provenanceRoot = Path.Combine(evaluatorRoot, "provenance");
            try
            {
                if (!ValidateReferences(out string referenceError))
                    throw new InvalidOperationException(referenceError);
                if (probeRoot == null || faceCenter == null || rearCenter == null
                    || skinCollider == null || skinIntersection == null)
                    throw new InvalidOperationException(
                        "M0-B requires probe, face/rear centers, exact skin collider, and contact measurement");
                if (Directory.Exists(M0BOutputDirectory))
                    throw new InvalidOperationException(
                        "M0-B output already exists; refusing to overwrite or rerun the single approved pilot");
                Directory.CreateDirectory(frameRoot);
                Directory.CreateDirectory(provenanceRoot);

                SweepCountReferenceSet references = BuildSweepCountReferences();
                Vector3 t2 = references.references[0].contactPoint;
                Vector3 t8 = references.references[6].contactPoint;
                Vector3 corridorAxis = (t8 - t2).normalized;
                Vector3 proposedOrigin = 0.5f * (t2 + t8);
                if (!FindSurfaceAt(proposedOrigin, Vector3.zero, out RaycastHit originHit))
                    throw new InvalidOperationException("M0-B could not project its central diagnostic start");
                Vector3 startContact = originHit.point;
                List<BoundarySurfaceSample> cranialSurface = TraceBoundarySurface(
                    originHit, -corridorAxis, startContact, corridorAxis,
                    BoundaryMaximumPreflightStepsPerDirection);
                List<BoundarySurfaceSample> caudalSurface = TraceBoundarySurface(
                    originHit, corridorAxis, startContact, corridorAxis,
                    BoundaryMaximumPreflightStepsPerDirection);
                if (cranialSurface.Count < 12 || caudalSurface.Count < 12)
                    throw new InvalidOperationException(
                        "M0-B connected skin corridor is too short for endpoint persistence sampling");

                int caudalOutboundSteps = caudalSurface.Count - 2;
                int cranialOutboundSteps = cranialSurface.Count - 2;
                int movementBudget = caudalOutboundSteps * 2
                    + cranialOutboundSteps + M0BEndpointReverseSteps;
                float plannedCranialEndpoint = cranialSurface[cranialOutboundSteps]
                    .signedLongitudinalMm;
                float plannedCaudalEndpoint = caudalSurface[caudalOutboundSteps]
                    .signedLongitudinalMm;
                CalculateM0BDiagnosticWorkspace(
                    cranialSurface.Last().signedLongitudinalMm,
                    caudalSurface.Last().signedLongitudinalMm,
                    out float diagnosticCenterOffset,
                    out float diagnosticHalfExtent);
                M0BGeometryAndWorkspacePreflight preflight =
                    new M0BGeometryAndWorkspacePreflight
                    {
                        generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                        scene = gameObject.scene.path,
                        status = "PASS",
                        stepMm = BoundaryPreflightStepMm,
                        yawDeg = M0BYawDeg,
                        startContactPoint = startContact,
                        corridorAxis = corridorAxis,
                        validatedCranialLimitMm = cranialSurface.Last().signedLongitudinalMm,
                        validatedCaudalLimitMm = caudalSurface.Last().signedLongitudinalMm,
                        plannedCaudalOutboundSteps = caudalOutboundSteps,
                        plannedCaudalReverseSteps = caudalOutboundSteps,
                        plannedCranialOutboundSteps = cranialOutboundSteps,
                        plannedCranialReverseSteps = M0BEndpointReverseSteps,
                        plannedMovementBudget = movementBudget,
                        plannedCranialEndpointMm = plannedCranialEndpoint,
                        plannedCaudalEndpointMm = plannedCaudalEndpoint,
                        originalLongitudinalHalfExtentMm = originalLongitudinalExtent,
                        diagnosticLongitudinalHalfExtentMm = diagnosticHalfExtent,
                        diagnosticWorkspaceCenterOffsetMm = diagnosticCenterOffset,
                        diagnosticWorkspaceWithinValidatedSkinCorridor = true,
                        reversalToleranceMm = M0BReversalToleranceMm,
                        startMethod =
                            "Evaluator-only midpoint of projected T2-T8 contacts, reprojected to Skin; no label or pose enters actor trace.",
                        decision =
                            "Both endpoint regions are connected by safe 3 mm surface projections. Skeleton AABB is recorded but is not a stopping criterion."
                    };
                WriteSweepCountJson(Path.Combine(
                    M0BOutputDirectory, "geometry_and_workspace_preflight.json"), preflight);

                active2DStepMm = BoundaryPreflightStepMm;
                sweepCountYawDeg = M0BYawDeg;
                if (!PlaceSweepCountProbe(startContact, originHit.normal, corridorAxis,
                        out string placementFailure))
                    throw new InvalidOperationException("M0-B start placement failed: " + placementFailure);
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

                if (!UltrasoundManager.TryGet(UltrasoundManager.Plane.Primary,
                        out UltrasoundManager manager) || manager == null)
                    throw new InvalidOperationException("M0-B primary ultrasound manager is unavailable");
                MiniZigzagInstrumentationCheck instrumentation =
                    RunMiniZigzagInstrumentationCheck(manager, evaluatorRoot);
                WriteSweepCountJson(Path.Combine(evaluatorRoot, "instrumentation_check.json"),
                    instrumentation);
                if (!instrumentation.exactPixelParity || !instrumentation.metadataMatched)
                    throw new InvalidOperationException(
                        "M0-B provenance instrumentation parity or metadata check failed");

                actor = new M0BActorTrajectory
                {
                    generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    configuredStepMm = active2DStepMm,
                    commandedYawDeg = M0BYawDeg,
                    movementBudget = movementBudget
                };
                evaluator = new M0BPrivilegedTrajectory
                {
                    generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    startContactPoint = faceCenter.position,
                    corridorAxis = corridorAxis,
                    originalWorkspaceOrigin = originalWorkspaceOrigin,
                    diagnosticWorkspaceOrigin = active2DWorkspaceOrigin,
                    originalLongitudinalHalfExtentMm = originalLongitudinalExtent,
                    diagnosticLongitudinalHalfExtentMm = diagnosticHalfExtent
                };
                summary = new M0BRunSummary
                {
                    generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    plannedCranialEndpointMm = plannedCranialEndpoint,
                    plannedCaudalEndpointMm = plannedCaudalEndpoint
                };

                float signedRelative = 0f;
                float cumulativeTravel = 0f;
                yield return CaptureM0BObservation(
                    manager, actor, evaluator, frameRoot, provenanceRoot,
                    "baseline", Active2DAction.Observe, true,
                    signedRelative, cumulativeTravel);
                yield return RunM0BRouteLeg(
                    manager, actor, evaluator, frameRoot, provenanceRoot,
                    "caudal_outbound", Active2DAction.Caudal, caudalOutboundSteps,
                    corridorAxis,
                    value => signedRelative = value,
                    () => signedRelative,
                    value => cumulativeTravel = value,
                    () => cumulativeTravel);
                EnsureM0BRouteHealthy(actor, "caudal outbound");
                yield return RunM0BRouteLeg(
                    manager, actor, evaluator, frameRoot, provenanceRoot,
                    "caudal_reverse", Active2DAction.Cranial, caudalOutboundSteps,
                    corridorAxis,
                    value => signedRelative = value,
                    () => signedRelative,
                    value => cumulativeTravel = value,
                    () => cumulativeTravel);
                EnsureM0BRouteHealthy(actor, "caudal reverse");
                yield return RunM0BRouteLeg(
                    manager, actor, evaluator, frameRoot, provenanceRoot,
                    "cranial_outbound", Active2DAction.Cranial, cranialOutboundSteps,
                    corridorAxis,
                    value => signedRelative = value,
                    () => signedRelative,
                    value => cumulativeTravel = value,
                    () => cumulativeTravel);
                EnsureM0BRouteHealthy(actor, "cranial outbound");
                yield return RunM0BRouteLeg(
                    manager, actor, evaluator, frameRoot, provenanceRoot,
                    "cranial_reverse", Active2DAction.Caudal, M0BEndpointReverseSteps,
                    corridorAxis,
                    value => signedRelative = value,
                    () => signedRelative,
                    value => cumulativeTravel = value,
                    () => cumulativeTravel);
                EnsureM0BRouteHealthy(actor, "cranial reverse");

                bool allFresh = actor.observations.All(M0BObservationFresh);
                bool allFramesValid = actor.observations.All(item => item.frameIntegrityValid);
                bool allSafe = actor.observations.All(item =>
                    item.contactValidAfterExecution && item.workspaceValidAfterExecution
                    && !item.safetyFailure);
                bool allPoseValid = evaluator.observations.All(item =>
                    Mathf.Abs(item.surfaceYawErrorDeg) <= SweepCountYawToleranceDeg
                    && item.normalAlignmentErrorDeg <= MiniZigzagNormalToleranceDeg
                    && item.provenanceMatchedPublishedFrame);
                float minimum = evaluator.observations.Min(
                    item => item.evaluatorLongitudinalCoordinateMm);
                float maximum = evaluator.observations.Max(
                    item => item.evaluatorLongitudinalCoordinateMm);
                bool coverage = minimum <= plannedCranialEndpoint + active2DStepMm
                    && maximum >= plannedCaudalEndpoint - active2DStepMm;
                bool complete = actor.acceptedMovements == movementBudget
                    && actor.rejectedMovements == 0
                    && actor.observations.Count == movementBudget + 1;
                bool eligible = complete && coverage && allFresh && allFramesValid
                    && allSafe && allPoseValid;
                actor.status = eligible ? "complete_eligible" : "complete_ineligible";
                actor.stopReason = eligible
                    ? "bounded route completed"
                    : "route, endpoint coverage, safety, freshness, integrity, or provenance gate failed";
                summary.status = actor.status;
                summary.physicalSampling = eligible ? "PASS" : "FAIL";
                summary.imageIntegrity = allFresh && allFramesValid && allPoseValid
                    ? "PASS" : "FAIL";
                summary.stopReason = actor.stopReason;
                summary.acceptedMovements = actor.acceptedMovements;
                summary.rejectedMovements = actor.rejectedMovements;
                summary.validFrames = actor.observations.Count(item => item.frameIntegrityValid);
                summary.totalFrames = actor.observations.Count;
                summary.minimumEvaluatorCoordinateMm = minimum;
                summary.maximumEvaluatorCoordinateMm = maximum;
                summary.maximumYawErrorDeg = evaluator.observations.Max(
                    item => Mathf.Abs(item.surfaceYawErrorDeg));
                summary.maximumNormalAlignmentErrorDeg = evaluator.observations.Max(
                    item => item.normalAlignmentErrorDeg);
                m0BVisualBoundaryStatus = eligible
                    ? "PILOT_ELIGIBLE_PENDING_BLIND_ANALYSIS"
                    : "PILOT_INELIGIBLE";

                WriteM0BArtifacts(actor, evaluator, summary);
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
                    WriteSweepCountJson(Path.Combine(M0BOutputDirectory, "run_summary.json"),
                        summary);
                }
                m0BVisualBoundaryRoutine = null;
                m0BVisualBoundaryComplete = true;
            }
        }

        private IEnumerator RunM0BRouteLeg(
            UltrasoundManager manager,
            M0BActorTrajectory actor,
            M0BPrivilegedTrajectory evaluator,
            string frameRoot,
            string provenanceRoot,
            string phase,
            Active2DAction action,
            int steps,
            Vector3 corridorAxis,
            Action<float> setSignedRelative,
            Func<float> getSignedRelative,
            Action<float> setCumulativeTravel,
            Func<float> getCumulativeTravel)
        {
            for (int step = 0; step < steps; step++)
            {
                bool accepted = TryM0BPhysicalAction(action, M0BYawDeg);
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
                yield return CaptureM0BObservation(
                    manager, actor, evaluator, frameRoot, provenanceRoot,
                    phase, action, true, getSignedRelative(), getCumulativeTravel());
                if (active2DLastActionExecution.safetyFailure) yield break;
            }
        }

        private IEnumerator CaptureM0BObservation(
            UltrasoundManager manager,
            M0BActorTrajectory actor,
            M0BPrivilegedTrajectory evaluator,
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
                "m0b_visual_boundary_" + index.ToString("D4", CultureInfo.InvariantCulture), true);
            yield return CaptureDirectProducerFreshFrame(token,
                action == Active2DAction.Observe ? "observe" : "completed_movement",
                capture, true);
            if (!manager.TryGetPublishedScanProvenance(
                    capture.producerPoseEpoch, capture.producerScanSerial,
                    out UltrasoundManager.PublishedScanProvenanceSnapshot provenance))
                throw new InvalidOperationException(
                    "M0-B provenance does not match the published pose epoch and scan serial");
            string fileName = "frame_" + index.ToString("D4", CultureInfo.InvariantCulture) + ".png";
            File.WriteAllBytes(Path.Combine(frameRoot, fileName), capture.pngBytes);
            string provenanceFile = "frame_" + index.ToString("D4", CultureInfo.InvariantCulture) + ".json";
            WriteSweepCountJson(Path.Combine(provenanceRoot, provenanceFile), provenance);
            bool observe = action == Active2DAction.Observe;
            Active2DActionExecution execution = active2DLastActionExecution;
            actor.observations.Add(new M0BActorObservation
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
            float achievedYaw = MeasureSweepCountSurfaceYawDeg(normal);
            Vector3 outward = (rearCenter.position - faceCenter.position).normalized;
            evaluator.observations.Add(new M0BPrivilegedObservation
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
                achievedSurfaceYawDeg = achievedYaw,
                surfaceYawErrorDeg = Mathf.DeltaAngle(M0BYawDeg, achievedYaw),
                normalAlignmentErrorDeg = Vector3.Angle(outward, normal),
                signedContactDepthMm = skinIntersection.SignedDepth,
                contactFraction = skinIntersection.ContactPercent,
                provenance = "evaluator_only/provenance/" + provenanceFile,
                provenanceMatchedPublishedFrame = provenance.poseEpoch == capture.producerPoseEpoch
                    && provenance.producerUnityFrame == capture.producerUnityFrame
                    && provenance.producerScanSerial == capture.producerScanSerial
            });
            M0BActorObservation stored = actor.observations.Last();
            if (!stored.frameIntegrityValid || !M0BObservationFresh(stored)
                || !stored.contactValidAfterExecution || !stored.workspaceValidAfterExecution
                || !evaluator.observations.Last().provenanceMatchedPublishedFrame)
                throw new InvalidOperationException(
                    "M0-B frame integrity, freshness, safety, or provenance check failed at observation "
                    + index.ToString(CultureInfo.InvariantCulture));
        }

        private bool TryM0BPhysicalAction(Active2DAction action, float surfaceYawDeg)
        {
            BeginActive2DActionExecution(action);
            if (action == Active2DAction.Caudal)
                return TryActive2DMove(active2DCaudalAxis, surfaceYawDeg);
            if (action == Active2DAction.Cranial)
                return TryActive2DMove(-active2DCaudalAxis, surfaceYawDeg);
            return RejectActive2DActionWithoutPoseChange(
                "M0-B accepts only cranial or caudal physical translation");
        }

        private static bool M0BFrameIntegrityValid(Texture2D image, byte[] pngBytes)
        {
            return image != null && image.width > 0 && image.height > 0
                && pngBytes != null && pngBytes.Length > 8
                && pngBytes[0] == 137 && pngBytes[1] == 80
                && pngBytes[2] == 78 && pngBytes[3] == 71
                && !IsLikelyMagentaArtifact(image);
        }

        private static bool M0BObservationFresh(M0BActorObservation item)
        {
            return item.poseEpoch == item.producerPoseEpoch
                && item.producerUnityFrame > item.poseAppliedUnityFrame
                && item.captureUnityFrame == item.producerUnityFrame
                && item.producerScanSerial > 0
                && item.activeAggregatorCount == 0;
        }

        private static void CalculateM0BDiagnosticWorkspace(
            float minimumCoordinate,
            float maximumCoordinate,
            out float centerOffset,
            out float halfExtent)
        {
            if (!(minimumCoordinate < 0f && maximumCoordinate > 0f
                    && maximumCoordinate > minimumCoordinate))
                throw new ArgumentException("M0-B workspace requires ordered two-sided limits");
            centerOffset = 0.5f * (minimumCoordinate + maximumCoordinate);
            halfExtent = 0.5f * (maximumCoordinate - minimumCoordinate);
        }

        private static void EnsureM0BRouteHealthy(M0BActorTrajectory actor, string leg)
        {
            if (actor.rejectedMovements > 0)
                throw new InvalidOperationException(
                    "M0-B stopped after the first rejected movement during " + leg);
            if (actor.observations.Any(item => item.safetyFailure
                    || !item.contactValidAfterExecution
                    || !item.workspaceValidAfterExecution
                    || !item.frameIntegrityValid))
                throw new InvalidOperationException(
                    "M0-B stopped after the first safety or integrity failure during " + leg);
        }

        private void WriteM0BArtifacts(
            M0BActorTrajectory actor,
            M0BPrivilegedTrajectory evaluator,
            M0BRunSummary summary)
        {
            if (actor != null)
            {
                WriteSweepCountJson(Path.Combine(M0BOutputDirectory, "trajectory_actor.json"), actor);
                WriteSweepCountJson(Path.Combine(M0BOutputDirectory, "frame_manifest.json"),
                    new M0BFrameManifest
                    {
                        generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                        frames = actor.observations
                    });
            }
            if (evaluator != null)
                WriteSweepCountJson(Path.Combine(
                    M0BOutputDirectory, "evaluator_only", "trajectory_privileged.json"), evaluator);
            if (summary != null)
                WriteSweepCountJson(Path.Combine(M0BOutputDirectory, "run_summary.json"), summary);
        }
    }
}
