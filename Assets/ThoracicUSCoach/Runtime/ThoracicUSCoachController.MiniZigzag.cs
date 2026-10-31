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
        private const string MiniZigzagContract = "thoracic_mini_zigzag_g2_feasibility_v1";
        private const float MiniZigzagYawToleranceDeg = 0.25f;
        private const float MiniZigzagNormalToleranceDeg = 0.25f;
        private const float MiniZigzagOffsetToleranceMm = 0.5f;
        private const float MiniZigzagCenterReturnToleranceMm = 1f;
        private Coroutine miniZigzagRoutine;
        private bool miniZigzagComplete;
        private string miniZigzagInfrastructureFailure = string.Empty;
        private string miniZigzagOutputRoot = string.Empty;
        private string miniZigzagStatus = "NOT_RUN";
        private float miniZigzagYawDeg;

        public bool MiniZigzagRunning => miniZigzagRoutine != null;
        public bool MiniZigzagComplete => miniZigzagComplete;
        public string MiniZigzagInfrastructureFailure => miniZigzagInfrastructureFailure;
        public string MiniZigzagOutputRoot => miniZigzagOutputRoot;
        public string MiniZigzagStatus => miniZigzagStatus;

        [Serializable]
        private sealed class MiniZigzagActorObservation
        {
            public int observationIndex;
            public string observationKind;
            public string profileToken;
            public string requestedAction;
            public bool executedSuccessfully;
            public float requestedDisplacementMm;
            public float attemptedDisplacementMm;
            public float achievedDisplacementMm;
            public float cumulativeLongitudinalTravelMm;
            public float relativeWithinProfileOffsetMm;
            public bool contactValidAfterExecution;
            public bool workspaceValidAfterExecution;
            public bool rollbackAttempted;
            public bool rollbackContactRestored;
            public bool safetyFailure;
            public string rejectionReason;
            public string image;
            public string imageSha256;
            public bool frameValid;
            public long poseEpoch;
            public int poseAppliedUnityFrame;
            public long producerPoseEpoch;
            public int producerUnityFrame;
            public long producerScanSerial;
            public int captureUnityFrame;
            public int activeAggregatorCount;
        }

        [Serializable]
        private sealed class MiniZigzagActorTrace
        {
            public string contract;
            public string generatedUtc;
            public string episodeId;
            public string scheduleDisclosure;
            public string allowedInputs;
            public string excludedDiagnosticFeatures;
            public float configuredStepMm;
            public float commandedYawDeg;
            public string status;
            public string stopReason;
            public int acceptedMovements;
            public int rejectedMovements;
            public List<MiniZigzagActorObservation> observations = new List<MiniZigzagActorObservation>();
        }

        [Serializable]
        private sealed class MiniZigzagPrivilegedObservation
        {
            public int observationIndex;
            public string profileToken;
            public string diagnosticReferenceLevel;
            public Vector3 probePosition;
            public Quaternion probeRotation;
            public Vector3 contactPoint;
            public Vector3 surfaceNormal;
            public float corridorCoordinateMm;
            public float actualLateralOffsetMm;
            public float expectedLateralOffsetMm;
            public float lateralOffsetErrorMm;
            public float achievedSurfaceYawDeg;
            public float surfaceYawErrorDeg;
            public bool surfaceYawWithinTolerance;
            public float normalAlignmentErrorDeg;
            public bool normalAlignmentWithinTolerance;
            public float signedContactDepthMm;
            public float contactFraction;
            public string provenance;
            public bool provenanceMatchedPublishedFrame;
        }

        [Serializable]
        private sealed class MiniZigzagPrivilegedTrace
        {
            public string contract;
            public string generatedUtc;
            public string warning;
            public string movementSchedule;
            public List<MiniZigzagPrivilegedObservation> observations = new List<MiniZigzagPrivilegedObservation>();
        }

        [Serializable]
        private sealed class MiniZigzagProfileSummary
        {
            public string profileToken;
            public string diagnosticReferenceLevel;
            public int centerObservationIndex;
            public int observationCount;
            public float referenceCorridorCoordinateMm;
            public float anchorCorridorCoordinateMm;
            public float anchorCoordinateErrorMm;
            public float maximumLateralOffsetErrorMm;
            public float centerReturnErrorMm;
            public bool eligible;
        }

        [Serializable]
        private sealed class MiniZigzagInstrumentationCheck
        {
            public string method;
            public string disabledImage;
            public string enabledImage;
            public string disabledSha256;
            public string enabledSha256;
            public bool exactPixelParity;
            public int differingPixels;
            public float maximumChannelDifference;
            public long poseEpoch;
            public int producerUnityFrame;
            public long producerScanSerial;
            public bool metadataMatched;
            public int provenanceBeamCount;
            public string note;
        }

        [Serializable]
        private sealed class MiniZigzagRunSummary
        {
            public string contract;
            public string generatedUtc;
            public string episodeId;
            public string status;
            public string g0Status;
            public string g1Status;
            public string g2Status;
            public string g3Status;
            public string g4Status;
            public string lateralInformationGain;
            public string anatomicalIdentity;
            public bool evaluatorGuidedSchedule;
            public bool actorReceivedPrivilegedMetadata;
            public int acceptedMovements;
            public int rejectedMovements;
            public int validFrames;
            public int profileCount;
            public int eligibleProfileCount;
            public string outputRoot;
            public string stopReason;
            public float commandedYawDeg;
            public List<MiniZigzagProfileSummary> profiles = new List<MiniZigzagProfileSummary>();
        }

        public bool StartMiniZigzagYawZeroPilot()
        {
            return StartMiniZigzagPilot(0f);
        }

        public bool StartMiniZigzagPilot(float orientationYawDeg)
        {
            if (!Application.isPlaying || miniZigzagRoutine != null || sweepCountRoutine != null) return false;
            miniZigzagYawDeg = Mathf.Clamp(orientationYawDeg, -6f, 6f);
            sweepCountYawDeg = miniZigzagYawDeg;
            miniZigzagComplete = false;
            miniZigzagInfrastructureFailure = string.Empty;
            miniZigzagStatus = "RUNNING";
            miniZigzagRoutine = StartCoroutine(RunMiniZigzagYawZeroPilot());
            return true;
        }

        private IEnumerator RunMiniZigzagYawZeroPilot()
        {
            Stack<IEnumerator> stack = new Stack<IEnumerator>();
            stack.Push(RunMiniZigzagYawZeroPilotCore());
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
            miniZigzagInfrastructureFailure = failure.ToString();
            miniZigzagStatus = "INFRASTRUCTURE_FAILURE";
            miniZigzagRoutine = null;
            miniZigzagComplete = true;
            Debug.LogError("[ThoracicUSCoach][MiniZigzag] " + failure);
        }

        private IEnumerator RunMiniZigzagYawZeroPilotCore()
        {
            Vector3 originalPosition = probeRoot != null ? probeRoot.position : Vector3.zero;
            Quaternion originalRotation = probeRoot != null ? probeRoot.rotation : Quaternion.identity;
            float originalLongitudinalExtent = active2DLongitudinalHalfExtentMm;
            float originalLateralExtent = active2DLateralHalfExtentMm;
            string episodeId = "MINI_ZIGZAG_" + MiniZigzagYawTag(miniZigzagYawDeg) + "_"
                + DateTime.UtcNow.ToString(
                "yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
            MiniZigzagActorTrace actorTrace = null;
            MiniZigzagPrivilegedTrace privilegedTrace = null;
            MiniZigzagRunSummary summary = null;
            try
            {
                AutoWire();
                if (probeRoot == null || faceCenter == null || rearCenter == null || skinCollider == null)
                    throw new InvalidOperationException("Mini-zigzag requires probe, face/rear centers, and exact skin collider");

                miniZigzagOutputRoot = Path.Combine(ArtifactRoot(), "sweep_count", episodeId);
                string evaluatorRoot = Path.Combine(miniZigzagOutputRoot, "evaluator_only");
                string referenceFrameRoot = Path.Combine(evaluatorRoot, "reference_frames");
                string frameRoot = Path.Combine(miniZigzagOutputRoot, "frames");
                string provenanceRoot = Path.Combine(evaluatorRoot, "provenance");
                Directory.CreateDirectory(referenceFrameRoot);
                Directory.CreateDirectory(frameRoot);
                Directory.CreateDirectory(provenanceRoot);

                SweepCountReferenceSet referenceSet = BuildSweepCountReferences();
                SweepCountGateReport g0 = BuildInitialG0Report(referenceSet);
                if (referenceSet.references.Count == 7)
                {
                    Vector3 provisionalAxis = (referenceSet.references[6].contactPoint
                        - referenceSet.references[0].contactPoint).normalized;
                    if (provisionalAxis.sqrMagnitude > 0.5f)
                    {
                        foreach (SweepCountReference reference in referenceSet.references)
                        {
                            if (!reference.surfaceProjectionValid) continue;
                            if (!PlaceSweepCountProbe(reference.contactPoint, reference.surfaceNormal,
                                    provisionalAxis, out string placementFailure))
                            {
                                g0.failures.Add(reference.level + ": " + placementFailure);
                                continue;
                            }
                            reference.contactValid = MeasureSweepCountContact();
                            DirectFreshCaptureResult capture = new DirectFreshCaptureResult();
                            DirectFreshPoseToken token = BeginDirectFreshCapturePose(
                                "mini_zigzag_g0_reference_" + reference.level, true);
                            yield return CaptureDirectProducerFreshFrame(token, "evaluator_reference", capture);
                            string relative = "evaluator_only/reference_frames/" + reference.level + ".png";
                            string absolute = Path.Combine(miniZigzagOutputRoot,
                                relative.Replace('/', Path.DirectorySeparatorChar));
                            File.WriteAllBytes(absolute, capture.pngBytes);
                            reference.referenceImage = relative;
                            reference.referenceImageSha256 = Sha256Bytes(capture.pngBytes);
                            reference.frameValid = SweepCountFrameHasContent(capture.image,
                                out reference.sampledMeanLuminance, out reference.sampledLuminanceStdDev);
                        }
                    }
                    else g0.failures.Add("T2-to-T8 projected corridor axis is degenerate");
                }
                FinalizeG0Report(referenceSet, g0);
                WriteSweepCountJson(Path.Combine(evaluatorRoot, "references.json"), referenceSet);
                WriteSweepCountJson(Path.Combine(miniZigzagOutputRoot, "g0.json"), g0);
                if (g0.status != "PASS")
                {
                    miniZigzagStatus = "G0_FAIL";
                    yield break;
                }

                SweepCountReference t2 = referenceSet.references[0];
                SweepCountReference t8 = referenceSet.references[6];
                Vector3 corridorAxis = (t8.contactPoint - t2.contactPoint).normalized;
                if (!PlaceSweepCountProbe(t2.contactPoint, t2.surfaceNormal, corridorAxis,
                        out string startFailure))
                    throw new InvalidOperationException("Mini-zigzag T2 diagnostic start failed: " + startFailure);

                active2DWorkspaceOrigin = faceCenter.position;
                active2DCaudalAxis = Vector3.ProjectOnPlane(corridorAxis, CurrentSurfaceNormal()).normalized;
                active2DLateralAxis = Vector3.Cross(CurrentSurfaceNormal(), active2DCaudalAxis).normalized;
                caudalTangent = active2DCaudalAxis;
                active2DLongitudinalHalfExtentMm = referenceSet.corridorLengthMm + active2DStepMm * 4f;
                active2DLateralHalfExtentMm = 9f;
                active2DRejectedMoves = 0;
                active2DContactViolations = 0;
                active2DWorkspaceViolations = 0;

                if (!UltrasoundManager.TryGet(UltrasoundManager.Plane.Primary,
                        out UltrasoundManager manager)
                    || manager == null || !manager.isActiveAndEnabled)
                    throw new InvalidOperationException(
                        "Mini-zigzag provenance preflight requires the active primary ultrasound manager");
                MiniZigzagInstrumentationCheck instrumentation = RunMiniZigzagInstrumentationCheck(
                    manager, evaluatorRoot);
                WriteSweepCountJson(Path.Combine(evaluatorRoot, "instrumentation_check.json"), instrumentation);
                if (!instrumentation.exactPixelParity || !instrumentation.metadataMatched)
                {
                    miniZigzagStatus = "PREFLIGHT_FAIL";
                    yield break;
                }

                actorTrace = new MiniZigzagActorTrace
                {
                    contract = MiniZigzagContract,
                    generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    episodeId = episodeId,
                    scheduleDisclosure = "Evaluator-guided diagnostic anchors trigger fixed lateral profiles. This is not autonomous localization or counting.",
                    allowedInputs = "Clean ultrasound pixels, executed actions, action outcomes, and relative within-profile motion only.",
                    excludedDiagnosticFeatures = "Anchor ordinal, frame index, absolute longitudinal coordinate, T-level values, and level-encoding identifiers are excluded from separability features.",
                    configuredStepMm = active2DStepMm,
                    commandedYawDeg = miniZigzagYawDeg,
                    status = "running"
                };
                privilegedTrace = new MiniZigzagPrivilegedTrace
                {
                    contract = MiniZigzagContract,
                    generatedUtc = actorTrace.generatedUtc,
                    warning = "PRIVILEGED EVALUATOR ONLY. Reference levels, world pose, absolute corridor coordinate, and renderer provenance are never actor-side inputs.",
                    movementSchedule = "At evaluator-selected T2-T8 anchors: 0,-3,-6,-3,0,+3,+6,+3,0 mm; reverse sign order on alternating anchors."
                };
                summary = new MiniZigzagRunSummary
                {
                    contract = MiniZigzagContract,
                    generatedUtc = actorTrace.generatedUtc,
                    episodeId = episodeId,
                    status = "RUNNING",
                    g0Status = "PASS",
                    g1Status = "RUNNING",
                    g2Status = "FAIL",
                    g3Status = "NOT_RUN",
                    g4Status = "NOT_RUN",
                    lateralInformationGain = "PENDING_OFFLINE_ANALYSIS",
                    anatomicalIdentity = "PENDING_EVALUATOR_PROVENANCE_REVIEW",
                    evaluatorGuidedSchedule = true,
                    actorReceivedPrivilegedMetadata = false,
                    commandedYawDeg = miniZigzagYawDeg,
                    outputRoot = ProjectRelativePath(miniZigzagOutputRoot)
                };

                float cumulativeLongitudinalTravel = 0f;
                int acceptedMovements = 0;
                int rejectedMovements = 0;
                yield return CaptureMiniZigzagObservation(
                    manager, actorTrace, privilegedTrace, referenceSet, frameRoot, provenanceRoot,
                    Active2DAction.Observe, true, cumulativeLongitudinalTravel, 0f,
                    string.Empty, string.Empty, "centerline");

                int nextAnchor = 0;
                int maximumCaudalMoves = Mathf.CeilToInt(referenceSet.corridorLengthMm
                    / Mathf.Max(0.5f, active2DStepMm)) + 12;
                int caudalMoves = 0;
                string stopReason = string.Empty;
                while (caudalMoves <= maximumCaudalMoves && nextAnchor < referenceSet.references.Count)
                {
                    float coordinate = MiniZigzagCorridorCoordinate(referenceSet);
                    SweepCountReference reference = referenceSet.references[nextAnchor];
                    if (coordinate >= reference.corridorCoordinateMm - active2DStepMm * 0.5f)
                    {
                        string profileToken = "P" + nextAnchor.ToString("D2", CultureInfo.InvariantCulture);
                        MiniZigzagActorObservation centerActor = actorTrace.observations.Last();
                        MiniZigzagPrivilegedObservation centerPrivileged = privilegedTrace.observations.Last();
                        centerActor.observationKind = "centerline_profile_center";
                        centerActor.profileToken = profileToken;
                        centerActor.relativeWithinProfileOffsetMm = 0f;
                        centerPrivileged.profileToken = profileToken;
                        centerPrivileged.diagnosticReferenceLevel = reference.level;
                        centerPrivileged.expectedLateralOffsetMm = 0f;
                        centerPrivileged.actualLateralOffsetMm = 0f;

                        MiniZigzagProfileSummary profile = new MiniZigzagProfileSummary
                        {
                            profileToken = profileToken,
                            diagnosticReferenceLevel = reference.level,
                            centerObservationIndex = centerActor.observationIndex,
                            referenceCorridorCoordinateMm = reference.corridorCoordinateMm,
                            anchorCorridorCoordinateMm = coordinate,
                            anchorCoordinateErrorMm = Mathf.Abs(coordinate - reference.corridorCoordinateMm)
                        };
                        Vector3 profileCenter = faceCenter.position;
                        float[] offsets = MiniZigzagProfileOffsets(nextAnchor);
                        bool profileEligible = true;
                        for (int offsetIndex = 1; offsetIndex < offsets.Length; offsetIndex++)
                        {
                            float previousOffset = offsets[offsetIndex - 1];
                            float expectedOffset = offsets[offsetIndex];
                            Active2DAction action = expectedOffset > previousOffset
                                ? Active2DAction.Lateral : Active2DAction.Medial;
                            bool moved = TryMiniZigzagPhysicalAction(action, miniZigzagYawDeg);
                            if (moved) acceptedMovements++;
                            else rejectedMovements++;
                            yield return CaptureMiniZigzagObservation(
                                manager, actorTrace, privilegedTrace, referenceSet, frameRoot, provenanceRoot,
                                action, moved, cumulativeLongitudinalTravel, expectedOffset,
                                profileToken, reference.level, "lateral_profile");
                            MiniZigzagPrivilegedObservation observation = privilegedTrace.observations.Last();
                            profile.maximumLateralOffsetErrorMm = Mathf.Max(
                                profile.maximumLateralOffsetErrorMm, observation.lateralOffsetErrorMm);
                            if (!moved || active2DLastActionExecution.safetyFailure
                                || !observation.surfaceYawWithinTolerance
                                || !observation.normalAlignmentWithinTolerance
                                || !observation.provenanceMatchedPublishedFrame
                                || observation.lateralOffsetErrorMm > MiniZigzagOffsetToleranceMm)
                            {
                                profileEligible = false;
                                stopReason = "lateral profile safety, pose, offset, or provenance validation failed";
                                break;
                            }
                        }
                        profile.observationCount = actorTrace.observations.Count - profile.centerObservationIndex;
                        profile.centerReturnErrorMm = Vector3.Distance(profileCenter, faceCenter.position);
                        profile.eligible = profileEligible
                            && profile.centerReturnErrorMm <= MiniZigzagCenterReturnToleranceMm;
                        summary.profiles.Add(profile);
                        if (!profile.eligible)
                        {
                            if (string.IsNullOrEmpty(stopReason))
                                stopReason = "profile failed centerline return tolerance";
                            break;
                        }
                        nextAnchor++;
                        if (nextAnchor >= referenceSet.references.Count)
                        {
                            stopReason = "all seven evaluator-guided diagnostic profiles completed";
                            break;
                        }
                    }

                    bool caudalMoved = TryMiniZigzagPhysicalAction(
                        Active2DAction.Caudal, miniZigzagYawDeg);
                    caudalMoves++;
                    if (caudalMoved)
                    {
                        acceptedMovements++;
                        cumulativeLongitudinalTravel += Mathf.Max(
                            0f, active2DLastActionExecution.achievedDisplacementMm);
                    }
                    else rejectedMovements++;
                    yield return CaptureMiniZigzagObservation(
                        manager, actorTrace, privilegedTrace, referenceSet, frameRoot, provenanceRoot,
                        Active2DAction.Caudal, caudalMoved, cumulativeLongitudinalTravel, 0f,
                        string.Empty, string.Empty, "centerline");
                    MiniZigzagPrivilegedObservation caudalObservation = privilegedTrace.observations.Last();
                    if (!caudalMoved || active2DLastActionExecution.safetyFailure
                        || !caudalObservation.surfaceYawWithinTolerance
                        || !caudalObservation.normalAlignmentWithinTolerance
                        || !caudalObservation.provenanceMatchedPublishedFrame)
                    {
                        stopReason = "caudal safety, pose, freshness, or provenance validation failed";
                        break;
                    }
                }
                if (string.IsNullOrEmpty(stopReason)) stopReason = "bounded movement budget exhausted";

                actorTrace.acceptedMovements = acceptedMovements;
                actorTrace.rejectedMovements = rejectedMovements;
                actorTrace.stopReason = stopReason;
                bool allFramesValid = actorTrace.observations.All(item => item.frameValid);
                bool allFresh = actorTrace.observations.All(item =>
                    item.producerPoseEpoch == item.poseEpoch
                    && item.producerUnityFrame > item.poseAppliedUnityFrame
                    && item.producerScanSerial > 0
                    && item.captureUnityFrame == item.producerUnityFrame
                    && item.activeAggregatorCount == 0);
                bool allSafe = actorTrace.observations.All(item =>
                    item.contactValidAfterExecution && item.workspaceValidAfterExecution
                    && !item.safetyFailure);
                bool allPoseValid = privilegedTrace.observations.All(item =>
                    item.surfaceYawWithinTolerance && item.normalAlignmentWithinTolerance
                    && item.provenanceMatchedPublishedFrame);
                bool pilotEligible = summary.profiles.Count == 7
                    && summary.profiles.All(item => item.eligible)
                    && rejectedMovements == 0 && allFramesValid && allFresh && allSafe && allPoseValid;
                actorTrace.status = pilotEligible ? "complete_eligible" : "complete_ineligible";
                summary.status = actorTrace.status;
                summary.g1Status = pilotEligible ? "PASS" : "FAIL";
                summary.acceptedMovements = acceptedMovements;
                summary.rejectedMovements = rejectedMovements;
                summary.validFrames = actorTrace.observations.Count(item => item.frameValid);
                summary.profileCount = summary.profiles.Count;
                summary.eligibleProfileCount = summary.profiles.Count(item => item.eligible);
                summary.stopReason = stopReason;
                miniZigzagStatus = pilotEligible ? "PILOT_ELIGIBLE_PENDING_BLIND_ANALYSIS" : "PILOT_INELIGIBLE";

                WriteSweepCountJson(Path.Combine(miniZigzagOutputRoot, "trace.json"), actorTrace);
                WriteSweepCountJson(Path.Combine(evaluatorRoot, "trace_privileged.json"), privilegedTrace);
                WriteSweepCountJson(Path.Combine(miniZigzagOutputRoot, "run_summary.json"), summary);
            }
            finally
            {
                active2DLongitudinalHalfExtentMm = originalLongitudinalExtent;
                active2DLateralHalfExtentMm = originalLateralExtent;
                if (actorTrace != null && !File.Exists(Path.Combine(miniZigzagOutputRoot, "trace.json")))
                    WriteSweepCountJson(Path.Combine(miniZigzagOutputRoot, "trace.json"), actorTrace);
                if (privilegedTrace != null
                    && !File.Exists(Path.Combine(miniZigzagOutputRoot, "evaluator_only", "trace_privileged.json")))
                    WriteSweepCountJson(Path.Combine(
                        miniZigzagOutputRoot, "evaluator_only", "trace_privileged.json"), privilegedTrace);
                if (summary != null && !File.Exists(Path.Combine(miniZigzagOutputRoot, "run_summary.json")))
                    WriteSweepCountJson(Path.Combine(miniZigzagOutputRoot, "run_summary.json"), summary);
                if (probeRoot != null && skinCollider != null)
                {
                    probeRoot.SetPositionAndRotation(originalPosition, originalRotation);
                    EnforceRigidContact();
                }
                miniZigzagRoutine = null;
                miniZigzagComplete = true;
            }
        }

        private MiniZigzagInstrumentationCheck RunMiniZigzagInstrumentationCheck(
            UltrasoundManager manager, string evaluatorRoot)
        {
            string checkRoot = Path.Combine(evaluatorRoot, "instrumentation_parity");
            Directory.CreateDirectory(checkRoot);
            UnityEngine.Random.State randomState = UnityEngine.Random.state;
            long disabledEpoch = ++directFreshPoseEpoch;
            Physics.SyncTransforms();
            if (!manager.PublishCurrentPose(disabledEpoch))
                throw new InvalidOperationException("Uninstrumented parity scan did not publish");
            Texture2D disabled = CaptureCleanImage();
            byte[] disabledBytes = disabled.EncodeToPNG();
            string disabledPath = Path.Combine(checkRoot, "instrumentation_disabled.png");
            File.WriteAllBytes(disabledPath, disabledBytes);

            UnityEngine.Random.state = randomState;
            manager.ArmNextPublishedScanProvenance();
            long enabledEpoch = ++directFreshPoseEpoch;
            Physics.SyncTransforms();
            if (!manager.PublishCurrentPose(enabledEpoch))
                throw new InvalidOperationException("Instrumented parity scan did not publish");
            Texture2D enabled = CaptureCleanImage();
            byte[] enabledBytes = enabled.EncodeToPNG();
            string enabledPath = Path.Combine(checkRoot, "instrumentation_enabled.png");
            File.WriteAllBytes(enabledPath, enabledBytes);

            Color32[] disabledPixels = disabled.GetPixels32();
            Color32[] enabledPixels = enabled.GetPixels32();
            int differingPixels = 0;
            int maximumDifference = 0;
            for (int index = 0; index < Mathf.Min(disabledPixels.Length, enabledPixels.Length); index++)
            {
                Color32 before = disabledPixels[index];
                Color32 after = enabledPixels[index];
                int difference = Mathf.Max(
                    Mathf.Abs(before.r - after.r),
                    Mathf.Abs(before.g - after.g),
                    Mathf.Abs(before.b - after.b),
                    Mathf.Abs(before.a - after.a));
                if (difference > 0) differingPixels++;
                maximumDifference = Mathf.Max(maximumDifference, difference);
            }
            bool metadataMatched = manager.TryGetPublishedScanProvenance(
                enabledEpoch, manager.PublishedScanSerial,
                out UltrasoundManager.PublishedScanProvenanceSnapshot snapshot)
                && snapshot.producerUnityFrame == manager.PublishedUnityFrame;
            if (snapshot != null)
                WriteSweepCountJson(Path.Combine(checkRoot, "instrumentation_enabled_provenance.json"), snapshot);
            return new MiniZigzagInstrumentationCheck
            {
                method = "Two same-pose scans with UnityEngine.Random.state restored before the instrumented scan.",
                disabledImage = "evaluator_only/instrumentation_parity/instrumentation_disabled.png",
                enabledImage = "evaluator_only/instrumentation_parity/instrumentation_enabled.png",
                disabledSha256 = Sha256Bytes(disabledBytes),
                enabledSha256 = Sha256Bytes(enabledBytes),
                exactPixelParity = disabledPixels.Length == enabledPixels.Length && differingPixels == 0,
                differingPixels = differingPixels,
                maximumChannelDifference = maximumDifference,
                poseEpoch = enabledEpoch,
                producerUnityFrame = manager.PublishedUnityFrame,
                producerScanSerial = manager.PublishedScanSerial,
                metadataMatched = metadataMatched,
                provenanceBeamCount = snapshot != null ? snapshot.beamCount : 0,
                note = "Restoring renderer RNG isolates instrumentation effects; provenance does not consume random values or issue raycasts."
            };
        }

        private IEnumerator CaptureMiniZigzagObservation(
            UltrasoundManager manager,
            MiniZigzagActorTrace actorTrace,
            MiniZigzagPrivilegedTrace privilegedTrace,
            SweepCountReferenceSet referenceSet,
            string frameRoot,
            string provenanceRoot,
            Active2DAction action,
            bool accepted,
            float cumulativeLongitudinalTravel,
            float expectedLateralOffset,
            string profileToken,
            string diagnosticReferenceLevel,
            string observationKind)
        {
            int index = actorTrace.observations.Count;
            DirectFreshCaptureResult capture = new DirectFreshCaptureResult();
            DirectFreshPoseToken token = BeginDirectFreshCapturePose(
                "mini_zigzag_" + index.ToString("D4", CultureInfo.InvariantCulture), true);
            yield return CaptureDirectProducerFreshFrame(token,
                action == Active2DAction.Observe ? "observe"
                    : accepted ? "completed_movement" : "rejected_rollback",
                capture,
                true);
            if (!manager.TryGetPublishedScanProvenance(
                    capture.producerPoseEpoch, capture.producerScanSerial,
                    out UltrasoundManager.PublishedScanProvenanceSnapshot provenance))
                throw new InvalidOperationException(
                    "Published provenance does not match captured pose epoch and scan serial");

            string fileName = "frame_" + index.ToString("D4", CultureInfo.InvariantCulture) + ".png";
            File.WriteAllBytes(Path.Combine(frameRoot, fileName), capture.pngBytes);
            string provenanceFile = "frame_" + index.ToString("D4", CultureInfo.InvariantCulture) + ".json";
            WriteSweepCountJson(Path.Combine(provenanceRoot, provenanceFile), provenance);
            bool valid = SweepCountFrameHasContent(
                capture.image, out float unusedMean, out float unusedStandardDeviation);
            Active2DActionExecution execution = active2DLastActionExecution;
            bool observe = action == Active2DAction.Observe;
            actorTrace.observations.Add(new MiniZigzagActorObservation
            {
                observationIndex = index,
                observationKind = observationKind,
                profileToken = profileToken,
                requestedAction = action.ToString().ToUpperInvariant(),
                executedSuccessfully = accepted,
                requestedDisplacementMm = observe ? 0f : execution.requestedDisplacementMm,
                attemptedDisplacementMm = observe ? 0f : execution.attemptedDisplacementMm,
                achievedDisplacementMm = observe ? 0f : execution.achievedDisplacementMm,
                cumulativeLongitudinalTravelMm = cumulativeLongitudinalTravel,
                relativeWithinProfileOffsetMm = expectedLateralOffset,
                contactValidAfterExecution = observe ? MeasureSweepCountContact()
                    : execution.contactValidAfterExecution,
                workspaceValidAfterExecution = observe || execution.workspaceValidAfterExecution,
                rollbackAttempted = !observe && execution.rollbackAttempted,
                rollbackContactRestored = !observe && execution.rollbackContactRestored,
                safetyFailure = !observe && execution.safetyFailure,
                rejectionReason = observe ? string.Empty : execution.rejectionReason,
                image = "frames/" + fileName,
                imageSha256 = Sha256Bytes(capture.pngBytes),
                frameValid = valid,
                poseEpoch = capture.poseEpoch,
                poseAppliedUnityFrame = capture.poseAppliedUnityFrame,
                producerPoseEpoch = capture.producerPoseEpoch,
                producerUnityFrame = capture.producerUnityFrame,
                producerScanSerial = capture.producerScanSerial,
                captureUnityFrame = capture.captureUnityFrame,
                activeAggregatorCount = capture.activeAggregatorCount
            });

            Vector3 surfaceNormal = CurrentSurfaceNormal();
            float achievedYaw = MeasureSweepCountSurfaceYawDeg(surfaceNormal);
            float yawError = Mathf.DeltaAngle(miniZigzagYawDeg, achievedYaw);
            Vector3 outward = (rearCenter.position - faceCenter.position).normalized;
            float normalError = Vector3.Angle(outward, surfaceNormal);
            float actualLateralOffset = 0f;
            if (!string.IsNullOrEmpty(profileToken))
            {
                MiniZigzagPrivilegedObservation profileCenter = privilegedTrace.observations
                    .FirstOrDefault(item => item.profileToken == profileToken);
                if (profileCenter != null)
                    actualLateralOffset = Vector3.Dot(
                        faceCenter.position - profileCenter.contactPoint, active2DLateralAxis);
            }
            privilegedTrace.observations.Add(new MiniZigzagPrivilegedObservation
            {
                observationIndex = index,
                profileToken = profileToken,
                diagnosticReferenceLevel = diagnosticReferenceLevel,
                probePosition = capture.capturedPosition,
                probeRotation = capture.capturedRotation,
                contactPoint = faceCenter.position,
                surfaceNormal = surfaceNormal,
                corridorCoordinateMm = MiniZigzagCorridorCoordinate(referenceSet),
                actualLateralOffsetMm = actualLateralOffset,
                expectedLateralOffsetMm = expectedLateralOffset,
                lateralOffsetErrorMm = Mathf.Abs(actualLateralOffset - expectedLateralOffset),
                achievedSurfaceYawDeg = achievedYaw,
                surfaceYawErrorDeg = yawError,
                surfaceYawWithinTolerance = Mathf.Abs(yawError) <= MiniZigzagYawToleranceDeg,
                normalAlignmentErrorDeg = normalError,
                normalAlignmentWithinTolerance = normalError <= MiniZigzagNormalToleranceDeg,
                signedContactDepthMm = skinIntersection != null ? skinIntersection.SignedDepth : 0f,
                contactFraction = skinIntersection != null ? skinIntersection.ContactPercent : 0f,
                provenance = "evaluator_only/provenance/" + provenanceFile,
                provenanceMatchedPublishedFrame = provenance.poseEpoch == capture.producerPoseEpoch
                    && provenance.producerUnityFrame == capture.producerUnityFrame
                    && provenance.producerScanSerial == capture.producerScanSerial
            });
        }

        private bool TryMiniZigzagPhysicalAction(Active2DAction action, float surfaceYawDeg)
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
                        "mini-zigzag accepts only physical translation actions");
            }
        }

        private static float[] MiniZigzagProfileOffsets(int profileIndex)
        {
            return profileIndex % 2 == 0
                ? new[] { 0f, -3f, -6f, -3f, 0f, 3f, 6f, 3f, 0f }
                : new[] { 0f, 3f, 6f, 3f, 0f, -3f, -6f, -3f, 0f };
        }

        private static string MiniZigzagYawTag(float yawDeg)
        {
            if (Mathf.Abs(yawDeg) < 0.0001f) return "Z0";
            string magnitude = Mathf.Abs(yawDeg).ToString("0.###", CultureInfo.InvariantCulture)
                .Replace('.', 'p');
            return (yawDeg < 0f ? "ZM" : "ZP") + magnitude;
        }

        private float MiniZigzagCorridorCoordinate(SweepCountReferenceSet referenceSet)
        {
            Vector3 axis = (referenceSet.references[6].contactPoint
                - referenceSet.references[0].contactPoint).normalized;
            return Vector3.Dot(faceCenter.position - referenceSet.references[0].contactPoint, axis);
        }
    }
}
