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
        private const string SweepCountContract = "thoracic_longitudinal_sweep_count_m1_v1";
        private Coroutine sweepCountRoutine;
        private bool sweepCountComplete;
        private string sweepCountInfrastructureFailure = string.Empty;
        private string sweepCountOutputRoot = string.Empty;
        private string sweepCountScientificStatus = "NOT_RUN";
        private float sweepCountYawDeg;

        public bool SweepCountMilestoneRunning => sweepCountRoutine != null;
        public bool SweepCountMilestoneComplete => sweepCountComplete;
        public string SweepCountInfrastructureFailure => sweepCountInfrastructureFailure;
        public string SweepCountOutputRoot => sweepCountOutputRoot;
        public string SweepCountScientificStatus => sweepCountScientificStatus;

        [Serializable]
        private sealed class SweepCountReference
        {
            public string level;
            public string labelObject;
            public string anatomyObject;
            public string anatomyEvidence;
            public bool ultrasoundVisible;
            public string ultrasoundRenderType;
            public Vector3 labelPosition;
            public Vector3 anatomyBoundsCenter;
            public float labelToAnatomyBoundsMm;
            public Vector3 contactPoint;
            public Vector3 surfaceNormal;
            public float corridorCoordinateMm;
            public bool surfaceProjectionValid;
            public bool contactValid;
            public bool frameValid;
            public float sampledMeanLuminance;
            public float sampledLuminanceStdDev;
            public string referenceImage;
            public string referenceImageSha256;
        }

        [Serializable]
        private sealed class SweepCountReferenceSet
        {
            public string contract;
            public string generatedUtc;
            public string warning;
            public string boneObject;
            public string boneRenderType;
            public bool boneUltrasoundVisible;
            public float corridorLengthMm;
            public float stepMm;
            public float runtimeLongitudinalHalfExtentMm;
            public float orientationYawDeg;
            public List<SweepCountReference> references = new List<SweepCountReference>();
        }

        [Serializable]
        private sealed class SweepCountGateReport
        {
            public string gate;
            public string status;
            public string generatedUtc;
            public string rationale;
            public int requiredReferenceCount;
            public int resolvedReferenceCount;
            public int validReferenceFrameCount;
            public bool orderedT2ToT8;
            public bool allReferenceAnatomyUltrasoundVisible;
            public bool boneShadowSourcePresent;
            public bool safeContactAtEveryReference;
            public bool freshCaptureAtEveryReference;
            public float corridorLengthMm;
            public List<string> failures = new List<string>();
        }

        [Serializable]
        private sealed class SweepCountActorObservation
        {
            public int observationIndex;
            public string requestedAction;
            public bool executedSuccessfully;
            public float requestedDisplacementMm;
            public float attemptedDisplacementMm;
            public float achievedDisplacementMm;
            public float cumulativeActorRelativeTravelMm;
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
            public bool frameValid;
            public string freshCapturePurpose;
            public long poseEpoch;
            public int poseAppliedUnityFrame;
            public long producerPoseEpoch;
            public int producerUnityFrame;
            public long producerScanSerial;
            public int captureUnityFrame;
            public string capturePath;
            public string capturePathMode;
            public int activeAggregatorCount;
        }

        [Serializable]
        private sealed class SweepCountActorTrace
        {
            public string contract;
            public string generatedUtc;
            public string episodeId;
            public string inputContract;
            public string actionContract;
            public float configuredStepMm;
            public string status;
            public string stopReason;
            public int acceptedMovements;
            public int rejectedMovements;
            public float cumulativeActorRelativeTravelMm;
            public List<SweepCountActorObservation> observations = new List<SweepCountActorObservation>();
        }

        [Serializable]
        private sealed class SweepCountPrivilegedObservation
        {
            public int observationIndex;
            public Vector3 probePosition;
            public Quaternion probeRotation;
            public Vector3 contactPoint;
            public float corridorCoordinateMm;
            public string nearestReferenceLevel;
            public float nearestReferenceDistanceMm;
            public float signedContactDepthMm;
            public float contactFraction;
        }

        [Serializable]
        private sealed class SweepCountPrivilegedTrace
        {
            public string contract;
            public string generatedUtc;
            public string warning;
            public List<SweepCountPrivilegedObservation> observations = new List<SweepCountPrivilegedObservation>();
        }

        [Serializable]
        private sealed class SweepCountRunSummary
        {
            public string contract;
            public string generatedUtc;
            public string episodeId;
            public string g0Status;
            public string g1Status;
            public string g2Status;
            public string g3Status;
            public string g4Status;
            public string scientificStatus;
            public string outputRoot;
            public string trace;
            public string privilegedTrace;
            public string references;
            public int acceptedMovements;
            public int rejectedMovements;
            public float cumulativeTravelMm;
            public float finalDistanceToT8Mm;
            public List<string> visitedReferenceLevels = new List<string>();
            public string note;
        }

        public bool StartSweepCountMilestone1(float orientationYawDeg = 0f)
        {
            if (!Application.isPlaying || sweepCountRoutine != null) return false;
            sweepCountYawDeg = Mathf.Clamp(orientationYawDeg, -6f, 6f);
            sweepCountComplete = false;
            sweepCountInfrastructureFailure = string.Empty;
            sweepCountScientificStatus = "RUNNING";
            sweepCountRoutine = StartCoroutine(RunSweepCountMilestone1());
            return true;
        }

        private IEnumerator RunSweepCountMilestone1()
        {
            Stack<IEnumerator> stack = new Stack<IEnumerator>();
            stack.Push(RunSweepCountMilestone1Core());
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
                sweepCountInfrastructureFailure = failure.ToString();
                sweepCountScientificStatus = "INFRASTRUCTURE_FAILURE";
                sweepCountRoutine = null;
                sweepCountComplete = true;
                Debug.LogError("[ThoracicUSCoach][SweepCount] " + failure);
            }
        }

        private IEnumerator RunSweepCountMilestone1Core()
        {
            Vector3 originalPosition = probeRoot != null ? probeRoot.position : Vector3.zero;
            Quaternion originalRotation = probeRoot != null ? probeRoot.rotation : Quaternion.identity;
            float originalLongitudinalExtent = active2DLongitudinalHalfExtentMm;
            try
            {
                AutoWire();
                if (probeRoot == null || faceCenter == null || skinCollider == null)
                    throw new InvalidOperationException("Sweep-count requires probe, face center, and exact skin collider");

                string episodeId = "SWEEP_COUNT_M1_" + DateTime.UtcNow.ToString(
                    "yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
                sweepCountOutputRoot = Path.Combine(ArtifactRoot(), "sweep_count", episodeId);
                string evaluatorRoot = Path.Combine(sweepCountOutputRoot, "evaluator_only");
                string referenceFrameRoot = Path.Combine(evaluatorRoot, "reference_frames");
                string frameRoot = Path.Combine(sweepCountOutputRoot, "frames");
                Directory.CreateDirectory(referenceFrameRoot);
                Directory.CreateDirectory(frameRoot);

                SweepCountReferenceSet referenceSet = BuildSweepCountReferences();
                SweepCountGateReport g0 = BuildInitialG0Report(referenceSet);
                if (referenceSet.references.Count == 7)
                {
                    SweepCountReference first = referenceSet.references[0];
                    SweepCountReference last = referenceSet.references[referenceSet.references.Count - 1];
                    Vector3 provisionalAxis = (last.contactPoint - first.contactPoint).normalized;
                    if (provisionalAxis.sqrMagnitude > 0.5f)
                    {
                        for (int index = 0; index < referenceSet.references.Count; index++)
                        {
                            SweepCountReference reference = referenceSet.references[index];
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
                                "sweep_count_g0_reference_" + reference.level, true);
                            yield return CaptureDirectProducerFreshFrame(
                                token, "evaluator_reference", capture);
                            string relative = "evaluator_only/reference_frames/" + reference.level + ".png";
                            string absolute = Path.Combine(sweepCountOutputRoot,
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
                WriteSweepCountJson(Path.Combine(sweepCountOutputRoot, "g0.json"), g0);
                if (g0.status != "PASS")
                {
                    sweepCountScientificStatus = "G0_" + g0.status;
                    WriteSweepCountSummary(new SweepCountRunSummary
                    {
                        contract = SweepCountContract,
                        generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                        episodeId = episodeId,
                        g0Status = g0.status,
                        g1Status = "NOT_RUN",
                        g2Status = "NOT_RUN",
                        g3Status = "NOT_RUN",
                        g4Status = "NOT_RUN",
                        scientificStatus = sweepCountScientificStatus,
                        outputRoot = ProjectRelativePath(sweepCountOutputRoot),
                        references = "evaluator_only/references.json",
                        note = "Stopped at G0 as required; no physical sweep was attempted."
                    });
                    yield break;
                }

                SweepCountReference t2 = referenceSet.references[0];
                SweepCountReference t8 = referenceSet.references[referenceSet.references.Count - 1];
                Vector3 corridorAxis = (t8.contactPoint - t2.contactPoint).normalized;
                if (!PlaceSweepCountProbe(t2.contactPoint, t2.surfaceNormal, corridorAxis,
                        out string startFailure))
                    throw new InvalidOperationException("G1 T2 physical start failed: " + startFailure);

                active2DWorkspaceOrigin = faceCenter.position;
                active2DCaudalAxis = Vector3.ProjectOnPlane(corridorAxis, CurrentSurfaceNormal()).normalized;
                active2DLateralAxis = Vector3.Cross(CurrentSurfaceNormal(), active2DCaudalAxis).normalized;
                caudalTangent = active2DCaudalAxis;
                referenceSet.corridorLengthMm = Vector3.Dot(t8.contactPoint - t2.contactPoint, corridorAxis);
                referenceSet.runtimeLongitudinalHalfExtentMm = referenceSet.corridorLengthMm + active2DStepMm * 4f;
                active2DLongitudinalHalfExtentMm = referenceSet.runtimeLongitudinalHalfExtentMm;
                WriteSweepCountJson(Path.Combine(evaluatorRoot, "references.json"), referenceSet);

                SweepCountActorTrace actorTrace = new SweepCountActorTrace
                {
                    contract = SweepCountContract,
                    generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    episodeId = episodeId,
                    inputContract = "clean ultrasound PNG plus actually executed action history only",
                    actionContract = "physical CAUDAL surface-following moves; rejected moves are retained and never counted as successful",
                    configuredStepMm = active2DStepMm,
                    status = "running"
                };
                SweepCountPrivilegedTrace privilegedTrace = new SweepCountPrivilegedTrace
                {
                    contract = SweepCountContract,
                    generatedUtc = actorTrace.generatedUtc,
                    warning = "PRIVILEGED EVALUATOR ONLY. Never supplied to the image-only actor or signal extractor."
                };

                float cumulativeTravel = 0f;
                int accepted = 0;
                int rejected = 0;
                yield return CaptureSweepCountObservation(actorTrace, privilegedTrace,
                    referenceSet, frameRoot, sweepCountOutputRoot, Active2DAction.Observe,
                    true, cumulativeTravel);

                int maximumMoves = Mathf.CeilToInt(referenceSet.corridorLengthMm
                    / Mathf.Max(0.5f, active2DStepMm)) + 12;
                string stopReason = string.Empty;
                for (int move = 0; move < maximumMoves; move++)
                {
                    float coordinate = Vector3.Dot(faceCenter.position - t2.contactPoint, corridorAxis);
                    if (coordinate >= referenceSet.corridorLengthMm - active2DStepMm * 0.75f)
                    {
                        stopReason = "bounded physical sweep corridor end reached";
                        break;
                    }

                    bool moved = TryActive2DAction(Active2DAction.Caudal);
                    if (moved)
                    {
                        accepted++;
                        cumulativeTravel += Mathf.Max(0f, active2DLastActionExecution.achievedDisplacementMm);
                    }
                    else rejected++;
                    yield return CaptureSweepCountObservation(actorTrace, privilegedTrace,
                        referenceSet, frameRoot, sweepCountOutputRoot, Active2DAction.Caudal,
                        moved, cumulativeTravel);
                    if (active2DLastActionExecution.safetyFailure)
                    {
                        stopReason = "safety failure: " + active2DLastActionExecution.rejectionReason;
                        break;
                    }
                    if (!moved && rejected >= 3)
                    {
                        stopReason = "three rejected physical moves prevented further caudal progress";
                        break;
                    }
                }
                if (string.IsNullOrEmpty(stopReason)) stopReason = "bounded movement budget exhausted";

                actorTrace.acceptedMovements = accepted;
                actorTrace.rejectedMovements = rejected;
                actorTrace.cumulativeActorRelativeTravelMm = cumulativeTravel;
                actorTrace.stopReason = stopReason;
                float finalDistance = Vector3.Distance(faceCenter.position, t8.contactPoint);
                List<string> visited = SweepCountVisitedLevels(privilegedTrace, referenceSet);
                bool allFramesValid = actorTrace.observations.All(item => item.frameValid);
                bool allFresh = actorTrace.observations.All(item =>
                    item.producerPoseEpoch == item.poseEpoch
                    && item.producerUnityFrame > item.poseAppliedUnityFrame
                    && item.activeAggregatorCount == 0);
                bool g1Pass = accepted > 0 && rejected < 3 && allFramesValid && allFresh
                    && visited.Count == 7 && finalDistance <= active2DStepMm * 2f;
                actorTrace.status = g1Pass ? "complete" : "incomplete";
                WriteSweepCountJson(Path.Combine(sweepCountOutputRoot, "trace.json"), actorTrace);
                WriteSweepCountJson(Path.Combine(evaluatorRoot, "trace_privileged.json"), privilegedTrace);

                sweepCountScientificStatus = g1Pass ? "G1_PASS_G2_PENDING" : "G1_FAIL";
                WriteSweepCountSummary(new SweepCountRunSummary
                {
                    contract = SweepCountContract,
                    generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    episodeId = episodeId,
                    g0Status = "PASS",
                    g1Status = g1Pass ? "PASS" : "FAIL",
                    g2Status = g1Pass ? "PENDING_ANALYSIS" : "NOT_RUN",
                    g3Status = "NOT_RUN",
                    g4Status = "NOT_RUN",
                    scientificStatus = sweepCountScientificStatus,
                    outputRoot = ProjectRelativePath(sweepCountOutputRoot),
                    trace = "trace.json",
                    privilegedTrace = "evaluator_only/trace_privileged.json",
                    references = "evaluator_only/references.json",
                    acceptedMovements = accepted,
                    rejectedMovements = rejected,
                    cumulativeTravelMm = cumulativeTravel,
                    finalDistanceToT8Mm = finalDistance,
                    visitedReferenceLevels = visited,
                    note = g1Pass
                        ? "G2 requires independent classical-signal analysis of these real frames."
                        : "Stopped before G2 because the physical sweep gate did not pass."
                });
            }
            finally
            {
                active2DLongitudinalHalfExtentMm = originalLongitudinalExtent;
                if (probeRoot != null && skinCollider != null)
                {
                    probeRoot.SetPositionAndRotation(originalPosition, originalRotation);
                    EnforceRigidContact();
                }
                sweepCountRoutine = null;
                sweepCountComplete = true;
            }
        }

        private SweepCountReferenceSet BuildSweepCountReferences()
        {
            Transform[] sceneTransforms = Resources.FindObjectsOfTypeAll<Transform>()
                .Where(item => item != null && item.gameObject.scene == gameObject.scene)
                .ToArray();
            UltrasoundVisibility bone = Resources.FindObjectsOfTypeAll<UltrasoundVisibility>()
                .FirstOrDefault(item => item != null && item.gameObject.scene == gameObject.scene
                    && item.gameObject.name == "Skeleton");
            SweepCountReferenceSet result = new SweepCountReferenceSet
            {
                contract = SweepCountContract,
                generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                warning = "All named levels, world positions, contact points, and anatomical correspondences are evaluator-only.",
                boneObject = bone != null ? bone.gameObject.name : string.Empty,
                boneRenderType = bone != null ? bone.Type.ToString() : string.Empty,
                boneUltrasoundVisible = bone != null && bone.ShowOnUltrasound,
                stepMm = active2DStepMm,
                orientationYawDeg = sweepCountYawDeg
            };
            for (int number = 2; number <= 8; number++)
            {
                string level = "T" + number.ToString(CultureInfo.InvariantCulture);
                Transform label = sceneTransforms.FirstOrDefault(item => item.name == level
                    && item.CompareTag("Label"));
                Transform anatomy = sceneTransforms.FirstOrDefault(item => item.name == "LF " + level);
                Renderer anatomyRenderer = anatomy != null
                    ? anatomy.GetComponentInChildren<Renderer>(true) : null;
                UltrasoundVisibility visibility = anatomy != null
                    ? anatomy.GetComponentInChildren<UltrasoundVisibility>(true) : null;
                SweepCountReference reference = new SweepCountReference
                {
                    level = level,
                    labelObject = label != null ? label.name : string.Empty,
                    anatomyObject = anatomy != null ? anatomy.name : string.Empty,
                    anatomyEvidence = anatomy != null
                        ? "Level-specific ligamentum flavum mesh plus the combined Skeleton bone Shadow source"
                        : string.Empty,
                    ultrasoundVisible = visibility != null && visibility.ShowOnUltrasound,
                    ultrasoundRenderType = visibility != null ? visibility.Type.ToString() : string.Empty,
                    labelPosition = label != null ? label.position : Vector3.zero,
                    anatomyBoundsCenter = anatomyRenderer != null ? anatomyRenderer.bounds.center : Vector3.zero
                };
                if (label != null && anatomyRenderer != null)
                    reference.labelToAnatomyBoundsMm = Vector3.Distance(
                        label.position, anatomyRenderer.bounds.ClosestPoint(label.position));
                if (label != null && FindSurfaceAt(label.position, Vector3.zero, out RaycastHit hit))
                {
                    reference.surfaceProjectionValid = true;
                    reference.contactPoint = hit.point;
                    reference.surfaceNormal = hit.normal;
                }
                result.references.Add(reference);
            }
            if (result.references.All(item => item.surfaceProjectionValid))
            {
                Vector3 axis = (result.references[6].contactPoint
                    - result.references[0].contactPoint).normalized;
                foreach (SweepCountReference reference in result.references)
                    reference.corridorCoordinateMm = Vector3.Dot(
                        reference.contactPoint - result.references[0].contactPoint, axis);
                result.corridorLengthMm = result.references[6].corridorCoordinateMm;
            }
            return result;
        }

        private static SweepCountGateReport BuildInitialG0Report(SweepCountReferenceSet references)
        {
            SweepCountGateReport report = new SweepCountGateReport
            {
                gate = "G0",
                status = "INCONCLUSIVE",
                generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                requiredReferenceCount = 7,
                resolvedReferenceCount = references.references.Count(item =>
                    !string.IsNullOrEmpty(item.labelObject) && !string.IsNullOrEmpty(item.anatomyObject)),
                boneShadowSourcePresent = references.boneUltrasoundVisible
                    && references.boneRenderType == UltrasoundVisibility.RenderType.Shadow.ToString(),
                corridorLengthMm = references.corridorLengthMm
            };
            foreach (SweepCountReference reference in references.references)
            {
                if (string.IsNullOrEmpty(reference.labelObject)) report.failures.Add(reference.level + " label missing");
                if (string.IsNullOrEmpty(reference.anatomyObject)) report.failures.Add(reference.level + " level-specific LF anatomy missing");
                if (!reference.ultrasoundVisible) report.failures.Add(reference.level + " LF anatomy is not ultrasound-visible");
                if (!reference.surfaceProjectionValid) report.failures.Add(reference.level + " cannot project to skin");
            }
            if (!report.boneShadowSourcePresent)
                report.failures.Add("Combined Skeleton bone Shadow source is missing or not ultrasound-visible");
            return report;
        }

        private static void FinalizeG0Report(
            SweepCountReferenceSet references, SweepCountGateReport report)
        {
            report.validReferenceFrameCount = references.references.Count(item => item.frameValid);
            report.allReferenceAnatomyUltrasoundVisible = references.references.All(item => item.ultrasoundVisible);
            report.safeContactAtEveryReference = references.references.All(item => item.contactValid);
            report.freshCaptureAtEveryReference = references.references.All(item =>
                item.frameValid && !string.IsNullOrEmpty(item.referenceImageSha256));
            report.orderedT2ToT8 = references.references.Count == 7
                && references.references.Select(item => item.corridorCoordinateMm)
                    .Zip(references.references.Skip(1).Select(item => item.corridorCoordinateMm),
                        (before, after) => after > before + 1f).All(item => item);
            if (!report.safeContactAtEveryReference) report.failures.Add("Not every T2-T8 reference accepted rigid contact");
            if (!report.freshCaptureAtEveryReference) report.failures.Add("Not every T2-T8 reference produced a valid fresh frame");
            if (!report.orderedT2ToT8) report.failures.Add("Projected T2-T8 reference coordinates are not strictly ordered");
            report.status = report.failures.Count == 0 ? "PASS" : "FAIL";
            report.rationale = report.status == "PASS"
                ? "Seven independently named LF anatomy meshes correspond to seven hidden level labels; all project to an ordered skin corridor, retain rigid contact, and yield valid fresh ultrasound frames with a visible Skeleton Shadow source."
                : "One or more required anatomy, corridor, contact, or real-frame checks failed.";
        }

        private bool PlaceSweepCountProbe(
            Vector3 proposed, Vector3 expectedNormal, Vector3 corridorAxis, out string failure)
        {
            failure = string.Empty;
            if (!FindSurfaceAt(proposed, expectedNormal, out RaycastHit hit))
            {
                failure = "skin surface projection failed";
                return false;
            }
            seedProbeRotation = active2DCanonicalProbeRotation == default
                ? probeRoot.rotation : active2DCanonicalProbeRotation;
            caudalTangent = Vector3.ProjectOnPlane(corridorAxis, hit.normal).normalized;
            AlignProbeToSurface(hit, sweepCountYawDeg, 0f);
            if (!EnforceRigidContact())
            {
                failure = "rigid contact enforcement failed";
                return false;
            }
            Physics.SyncTransforms();
            if (!MeasureSweepCountContact())
            {
                failure = "contact measurement failed after placement";
                return false;
            }
            return true;
        }

        private bool MeasureSweepCountContact()
        {
            if (skinIntersection == null)
                return skinCollider != null && faceCenter != null
                    && Vector3.Distance(faceCenter.position,
                        skinCollider.ClosestPoint(faceCenter.position)) <= 1f;
            skinIntersection.Measure();
            return Thoracic2DNavigation.IsContactValid(
                skinIntersection.AnyHit, skinIntersection.SignedDepth, maxPenetrationMm);
        }

        private static bool SweepCountFrameHasContent(
            Texture2D image, out float mean, out float standardDeviation)
        {
            mean = 0f;
            standardDeviation = 0f;
            if (image == null || IsLikelyMagentaArtifact(image)) return false;
            Color32[] pixels = image.GetPixels32();
            if (pixels.Length == 0) return false;
            int stride = Mathf.Max(1, pixels.Length / 8192);
            int count = 0;
            double sum = 0d;
            double sumSquares = 0d;
            for (int index = 0; index < pixels.Length; index += stride)
            {
                Color32 pixel = pixels[index];
                double luminance = (0.2126d * pixel.r + 0.7152d * pixel.g + 0.0722d * pixel.b) / 255d;
                sum += luminance;
                sumSquares += luminance * luminance;
                count++;
            }
            if (count == 0) return false;
            mean = (float)(sum / count);
            standardDeviation = (float)Math.Sqrt(Math.Max(0d, sumSquares / count - mean * mean));
            return standardDeviation >= 0.005f;
        }

        private IEnumerator CaptureSweepCountObservation(
            SweepCountActorTrace actorTrace,
            SweepCountPrivilegedTrace privilegedTrace,
            SweepCountReferenceSet referenceSet,
            string frameRoot,
            string runRoot,
            Active2DAction action,
            bool accepted,
            float cumulativeTravel)
        {
            int index = actorTrace.observations.Count;
            DirectFreshCaptureResult capture = new DirectFreshCaptureResult();
            DirectFreshPoseToken token = BeginDirectFreshCapturePose(
                "sweep_count_" + index.ToString("D4", CultureInfo.InvariantCulture), true);
            yield return CaptureDirectProducerFreshFrame(token,
                action == Active2DAction.Observe ? "observe" : accepted ? "completed_movement" : "rejected_rollback",
                capture);
            string fileName = "frame_" + index.ToString("D4", CultureInfo.InvariantCulture) + ".png";
            string absolute = Path.Combine(frameRoot, fileName);
            File.WriteAllBytes(absolute, capture.pngBytes);
            bool valid = SweepCountFrameHasContent(capture.image, out float unusedMean, out float unusedStdDev);
            Active2DActionExecution execution = active2DLastActionExecution;
            actorTrace.observations.Add(new SweepCountActorObservation
            {
                observationIndex = index,
                requestedAction = action.ToString().ToUpperInvariant(),
                executedSuccessfully = accepted,
                requestedDisplacementMm = action == Active2DAction.Observe ? 0f : execution.requestedDisplacementMm,
                attemptedDisplacementMm = action == Active2DAction.Observe ? 0f : execution.attemptedDisplacementMm,
                achievedDisplacementMm = action == Active2DAction.Observe ? 0f : execution.achievedDisplacementMm,
                cumulativeActorRelativeTravelMm = cumulativeTravel,
                contactValidAfterExecution = action == Active2DAction.Observe ? MeasureSweepCountContact() : execution.contactValidAfterExecution,
                workspaceValidAfterExecution = action == Active2DAction.Observe || execution.workspaceValidAfterExecution,
                rollbackAttempted = action != Active2DAction.Observe && execution.rollbackAttempted,
                rollbackContactRestored = action != Active2DAction.Observe && execution.rollbackContactRestored,
                safetyFailure = action != Active2DAction.Observe && execution.safetyFailure,
                rejectionReason = action == Active2DAction.Observe ? string.Empty : execution.rejectionReason,
                image = "frames/" + fileName,
                imageSha256 = Sha256Bytes(capture.pngBytes),
                imageWidth = capture.image.width,
                imageHeight = capture.image.height,
                frameValid = valid,
                freshCapturePurpose = capture.purpose,
                poseEpoch = capture.poseEpoch,
                poseAppliedUnityFrame = capture.poseAppliedUnityFrame,
                producerPoseEpoch = capture.producerPoseEpoch,
                producerUnityFrame = capture.producerUnityFrame,
                producerScanSerial = capture.producerScanSerial,
                captureUnityFrame = capture.captureUnityFrame,
                capturePath = capture.capturePath,
                capturePathMode = capture.capturePathMode,
                activeAggregatorCount = capture.activeAggregatorCount
            });

            Vector3 axis = (referenceSet.references[6].contactPoint
                - referenceSet.references[0].contactPoint).normalized;
            float coordinate = Vector3.Dot(faceCenter.position
                - referenceSet.references[0].contactPoint, axis);
            SweepCountReference nearest = referenceSet.references
                .OrderBy(item => Mathf.Abs(item.corridorCoordinateMm - coordinate)).First();
            privilegedTrace.observations.Add(new SweepCountPrivilegedObservation
            {
                observationIndex = index,
                probePosition = capture.capturedPosition,
                probeRotation = capture.capturedRotation,
                contactPoint = faceCenter.position,
                corridorCoordinateMm = coordinate,
                nearestReferenceLevel = nearest.level,
                nearestReferenceDistanceMm = Mathf.Abs(nearest.corridorCoordinateMm - coordinate),
                signedContactDepthMm = skinIntersection != null ? skinIntersection.SignedDepth : 0f,
                contactFraction = skinIntersection != null ? skinIntersection.ContactPercent : 0f
            });
        }

        private static List<string> SweepCountVisitedLevels(
            SweepCountPrivilegedTrace trace, SweepCountReferenceSet references)
        {
            List<string> visited = new List<string>();
            for (int index = 0; index < references.references.Count; index++)
            {
                float before = index == 0 ? float.NegativeInfinity
                    : 0.5f * (references.references[index - 1].corridorCoordinateMm
                        + references.references[index].corridorCoordinateMm);
                float after = index == references.references.Count - 1 ? float.PositiveInfinity
                    : 0.5f * (references.references[index].corridorCoordinateMm
                        + references.references[index + 1].corridorCoordinateMm);
                if (trace.observations.Any(item => item.corridorCoordinateMm >= before
                    && item.corridorCoordinateMm < after))
                    visited.Add(references.references[index].level);
            }
            return visited;
        }

        private void WriteSweepCountSummary(SweepCountRunSummary summary)
        {
            WriteSweepCountJson(Path.Combine(sweepCountOutputRoot, "run_summary.json"), summary);
        }

        private static void WriteSweepCountJson(string path, object value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, JsonUtility.ToJson(value, true));
        }
    }
}
