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
        private const string G2R1OutputRoot =
            "Artifacts/ThoracicUSCoach/sweep_count/G2_R1_ANATOMICAL_REFERENCE_RENDERER_RECOVERY_20261101";

        [Serializable]
        private sealed class G2R1ObservabilityCapture
        {
            public string level;
            public string referenceType = "LF_ASSOCIATED_POSTERIOR_RECESS_REGION";
            public string geometry;
            public string repeat;
            public string image;
            public string imageSha256;
            public string provenance;
            public Vector3 lfBoundsCenter;
            public Vector3 lfBoundsSize;
            public float commandedLateralOffsetMm;
            public float achievedLateralOffsetMm;
            public float commandedSurfaceYawDeg;
            public float achievedSurfaceYawDeg;
            public float surfaceYawErrorDeg;
            public Vector3 probePosition;
            public Quaternion probeRotation;
            public Vector3 contactPoint;
            public Vector3 surfaceNormal;
            public Vector3 corridorAxis;
            public Vector3 lateralAxis;
            public Vector3 scanPlaneWidthAxis;
            public float scanWidthAgainstCorridor;
            public float scanWidthAgainstLateral;
            public float normalAlignmentErrorDeg;
            public bool contactValid;
            public bool workspaceValid;
            public bool frameIntegrityValid;
            public bool provenanceMatched;
            public int beamCount;
            public int beamsWithVisibleInterface;
            public int beamsWithSkeletonBeforeOrAtFirstShadow;
            public int beamsWithLfBeforeOrAtFirstShadow;
            public int beamsOccludedAfterEarlierShadow;
            public long poseEpoch;
            public int poseAppliedUnityFrame;
            public int producerUnityFrame;
            public long producerScanSerial;
            public int captureUnityFrame;
        }

        [Serializable]
        private sealed class G2R1ObservabilityManifest
        {
            public string contract = "thoracic_g2_r1_anatomical_observability_v1";
            public string generatedUtc;
            public string status = "RUNNING";
            public string actorUse = "PROHIBITED_EVALUATOR_GUIDED_DIAGNOSTIC_ONLY";
            public string diagnosticPlacement =
                "Skin projection above independently modeled LF T2-T8 bounds; LF centers were cross-validated post-freeze against blind combined-Skeleton posterior recessions.";
            public string referenceMap;
            public string referenceMapSha256;
            public int geometryCount;
            public int repeatCountPerPose = 2;
            public List<G2R1ObservabilityCapture> captures = new();
        }

        public bool StartG2R1ObservabilityDiagnostic()
        {
            if (relativeSpineAuditRoutine != null || relativeSpineAuditComplete) return false;
            relativeSpineAuditFailure = string.Empty;
            relativeSpineAuditStatus = "RUNNING_G2_R1_OBSERVABILITY";
            relativeSpineAuditRoutine = StartCoroutine(RunG2R1ObservabilityGuarded());
            return true;
        }

        private IEnumerator RunG2R1ObservabilityGuarded()
        {
            Exception failure = null;
            Stack<IEnumerator> stack = new Stack<IEnumerator>();
            stack.Push(RunG2R1ObservabilityCore());
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
            relativeSpineAuditFailure = failure.ToString();
            relativeSpineAuditStatus = "G2_R1_INFRASTRUCTURE_FAILURE";
            relativeSpineAuditRoutine = null;
            relativeSpineAuditComplete = true;
            Debug.LogError("[ThoracicUSCoach][G2R1] " + failure);
        }

        private IEnumerator RunG2R1ObservabilityCore()
        {
            AutoWire();
            string diagnosticRoot = Path.Combine(G2R1OutputRoot, "evaluator_only", "r1b_observability");
            string manifestPath = Path.Combine(diagnosticRoot, "capture_manifest.json");
            string referenceMap = Path.Combine(G2R1OutputRoot, "bone_structure_reference_map.json");
            Vector3 originalPosition = probeRoot != null ? probeRoot.position : Vector3.zero;
            Quaternion originalRotation = probeRoot != null ? probeRoot.rotation : Quaternion.identity;
            float originalSweepYaw = sweepCountYawDeg;
            try
            {
                if (File.Exists(manifestPath))
                    throw new InvalidOperationException("G2-R1 observability evidence exists; refusing to overwrite it");
                if (!File.Exists(referenceMap))
                    throw new InvalidOperationException("G2-R1 anatomical reference map is missing");
                if (!ValidateReferences(out string referenceError))
                    throw new InvalidOperationException(referenceError);
                if (probeRoot == null || faceCenter == null || rearCenter == null
                    || skinCollider == null || skinIntersection == null)
                    throw new InvalidOperationException(
                        "G2-R1 requires probe, face/rear centers, exact skin collider, and contact measurement");
                if (!UltrasoundManager.TryGet(UltrasoundManager.Plane.Primary,
                        out UltrasoundManager manager) || manager == null)
                    throw new InvalidOperationException("G2-R1 primary ultrasound manager is unavailable");

                Directory.CreateDirectory(diagnosticRoot);
                G2R1ObservabilityManifest manifest = new G2R1ObservabilityManifest
                {
                    generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    referenceMap = "bone_structure_reference_map.json",
                    referenceMapSha256 = Sha256Bytes(File.ReadAllBytes(referenceMap)),
                    geometryCount = 3
                };
                SweepCountReferenceSet references = BuildSweepCountReferences();
                Vector3 corridorAxis = (references.references[6].contactPoint
                    - references.references[0].contactPoint).normalized;
                Transform[] sceneTransforms = Resources.FindObjectsOfTypeAll<Transform>()
                    .Where(item => item != null && item.gameObject.scene == gameObject.scene)
                    .ToArray();
                (string geometry, float offset)[] geometries =
                {
                    ("PARAMEDIAN_LONGITUDINAL_M6", -6f),
                    ("PARAMEDIAN_LONGITUDINAL_P6", 6f),
                    ("ANATOMY_DERIVED_LONGITUDINAL_M3", -3f)
                };
                foreach ((string geometry, float offset) in geometries)
                {
                    string frameRoot = Path.Combine(diagnosticRoot, geometry, "frames");
                    string provenanceRoot = Path.Combine(diagnosticRoot, geometry, "provenance");
                    Directory.CreateDirectory(frameRoot);
                    Directory.CreateDirectory(provenanceRoot);
                    foreach (SweepCountReference reference in references.references)
                    {
                        Transform lf = sceneTransforms.FirstOrDefault(
                            item => item.name == "LF " + reference.level);
                        Renderer lfRenderer = lf != null ? lf.GetComponentInChildren<Renderer>(true) : null;
                        if (lfRenderer == null)
                            throw new InvalidOperationException("Missing LF renderer at " + reference.level);
                        if (!FindSurfaceAt(lfRenderer.bounds.center, Vector3.zero, out RaycastHit centralHit))
                            throw new InvalidOperationException(
                                "G2-R1 LF-region surface projection failed at " + reference.level);
                        Vector3 localCorridor = Vector3.ProjectOnPlane(
                            corridorAxis, centralHit.normal).normalized;
                        Vector3 lateralAxis = Vector3.Cross(
                            centralHit.normal, localCorridor).normalized;
                        if (localCorridor.sqrMagnitude < 0.5f || lateralAxis.sqrMagnitude < 0.5f)
                            throw new InvalidOperationException(
                                "G2-R1 local surface axes are degenerate at " + reference.level);
                        Vector3 proposal = centralHit.point + lateralAxis * offset;
                        if (!FindSurfaceAt(proposal, centralHit.normal, out RaycastHit geometryHit))
                            throw new InvalidOperationException(
                                "G2-R1 offset projection failed at " + reference.level + " for " + geometry);
                        sweepCountYawDeg = 90f;
                        if (!PlaceSweepCountProbe(geometryHit.point, geometryHit.normal,
                                corridorAxis, out string placementFailure))
                            throw new InvalidOperationException(
                                "G2-R1 placement failed at " + reference.level + " for "
                                + geometry + ": " + placementFailure);
                        Vector3 normal = CurrentSurfaceNormal();
                        localCorridor = Vector3.ProjectOnPlane(corridorAxis, normal).normalized;
                        lateralAxis = Vector3.Cross(normal, localCorridor).normalized;
                        Vector3 scanWidth = Vector3.ProjectOnPlane(probeRoot.right, normal).normalized;
                        float widthAgainstCorridor = Mathf.Abs(Vector3.Dot(scanWidth, localCorridor));
                        if (widthAgainstCorridor < 0.95f)
                            throw new InvalidOperationException(
                                "G2-R1 scan plane is not longitudinal at " + reference.level
                                + " for " + geometry);
                        float achievedOffset = Vector3.Dot(
                            faceCenter.position - centralHit.point, lateralAxis);
                        if (Mathf.Abs(achievedOffset - offset) > 1.5f)
                            throw new InvalidOperationException(
                                "G2-R1 lateral offset error exceeded 1.5 mm at "
                                + reference.level + " for " + geometry);
                        if (!MeasureSweepCountContact())
                            throw new InvalidOperationException(
                                "G2-R1 rigid contact validation failed at "
                                + reference.level + " for " + geometry);
                        if (!IsActive2DWorkspaceValid(faceCenter.position))
                            throw new InvalidOperationException(
                                "G2-R1 workspace validation failed at "
                                + reference.level + " for " + geometry);
                        for (int repeat = 0; repeat < 2; repeat++)
                        {
                            yield return CaptureG2R1Observation(
                                manager, manifest, reference.level, geometry,
                                repeat == 0 ? "repeat_a" : "repeat_b", lfRenderer.bounds,
                                offset, achievedOffset, localCorridor, lateralAxis,
                                scanWidth, widthAgainstCorridor, frameRoot, provenanceRoot);
                        }
                    }
                }
                manifest.status = "CAPTURED_PENDING_OFFLINE_CAUSAL_REVIEW";
                WriteSweepCountJson(manifestPath, manifest);
                relativeSpineAuditStatus = "G2_R1_CAPTURE_COMPLETE_PENDING_OFFLINE_REVIEW";
            }
            finally
            {
                sweepCountYawDeg = originalSweepYaw;
                if (probeRoot != null && skinCollider != null)
                {
                    probeRoot.SetPositionAndRotation(originalPosition, originalRotation);
                    EnforceRigidContact();
                }
                relativeSpineAuditRoutine = null;
                relativeSpineAuditComplete = true;
            }
        }

        private IEnumerator CaptureG2R1Observation(
            UltrasoundManager manager,
            G2R1ObservabilityManifest manifest,
            string level,
            string geometry,
            string repeat,
            Bounds lfBounds,
            float commandedOffset,
            float achievedOffset,
            Vector3 corridorAxis,
            Vector3 lateralAxis,
            Vector3 scanWidth,
            float widthAgainstCorridor,
            string frameRoot,
            string provenanceRoot)
        {
            DirectFreshPoseToken token = BeginDirectFreshCapturePose(
                "g2_r1_" + geometry + "_" + level + "_" + repeat, true);
            DirectFreshCaptureResult capture = new DirectFreshCaptureResult();
            yield return CaptureDirectProducerFreshFrame(token,
                "g2_r1_anatomical_observability", capture, true);
            if (!M0BFrameIntegrityValid(capture.image, capture.pngBytes))
                throw new InvalidOperationException(
                    "G2-R1 frame integrity failed at " + level + " for " + geometry);
            if (!manager.TryGetPublishedScanProvenance(token.poseEpoch, capture.producerScanSerial,
                    out UltrasoundManager.PublishedScanProvenanceSnapshot provenance))
                throw new InvalidOperationException(
                    "G2-R1 provenance is missing at " + level + " for " + geometry);
            string stem = level + "_" + repeat;
            string imageFile = stem + ".png";
            string provenanceFile = stem + ".json";
            File.WriteAllBytes(Path.Combine(frameRoot, imageFile), capture.pngBytes);
            WriteSweepCountJson(Path.Combine(provenanceRoot, provenanceFile), provenance);
            Vector3 normal = CurrentSurfaceNormal();
            float achievedYaw = MeasureSweepCountSurfaceYawDeg(normal);
            int skeletonVisible = 0;
            int lfVisible = 0;
            int occluded = 0;
            foreach (UltrasoundManager.PublishedScanProvenanceBeam beam in provenance.beams)
            {
                if (beam.interfaces.Any(item => item.objectName == "Skeleton"
                        && item.reachedBeforeOrAtFirstShadow)) skeletonVisible++;
                if (beam.interfaces.Any(item => item.objectName.StartsWith("LF T", StringComparison.Ordinal)
                        && item.reachedBeforeOrAtFirstShadow)) lfVisible++;
                if (beam.interfaces.Any(item => item.occludedByEarlierShadow)) occluded++;
            }
            manifest.captures.Add(new G2R1ObservabilityCapture
            {
                level = level,
                geometry = geometry,
                repeat = repeat,
                image = "evaluator_only/r1b_observability/" + geometry + "/frames/" + imageFile,
                imageSha256 = Sha256Bytes(capture.pngBytes),
                provenance = "evaluator_only/r1b_observability/" + geometry + "/provenance/" + provenanceFile,
                lfBoundsCenter = lfBounds.center,
                lfBoundsSize = lfBounds.size,
                commandedLateralOffsetMm = commandedOffset,
                achievedLateralOffsetMm = achievedOffset,
                commandedSurfaceYawDeg = 90f,
                achievedSurfaceYawDeg = achievedYaw,
                surfaceYawErrorDeg = Mathf.DeltaAngle(90f, achievedYaw),
                probePosition = probeRoot.position,
                probeRotation = probeRoot.rotation,
                contactPoint = faceCenter.position,
                surfaceNormal = normal,
                corridorAxis = corridorAxis,
                lateralAxis = lateralAxis,
                scanPlaneWidthAxis = scanWidth,
                scanWidthAgainstCorridor = widthAgainstCorridor,
                scanWidthAgainstLateral = Mathf.Abs(Vector3.Dot(scanWidth, lateralAxis)),
                normalAlignmentErrorDeg = Vector3.Angle(
                    (rearCenter.position - faceCenter.position).normalized, normal),
                contactValid = MeasureSweepCountContact(),
                workspaceValid = IsActive2DWorkspaceValid(faceCenter.position),
                frameIntegrityValid = true,
                provenanceMatched = provenance.poseEpoch == capture.producerPoseEpoch
                    && provenance.producerUnityFrame == capture.producerUnityFrame
                    && provenance.producerScanSerial == capture.producerScanSerial,
                beamCount = provenance.beamCount,
                beamsWithVisibleInterface = provenance.beams.Count(item => item.hasVisibleInterface),
                beamsWithSkeletonBeforeOrAtFirstShadow = skeletonVisible,
                beamsWithLfBeforeOrAtFirstShadow = lfVisible,
                beamsOccludedAfterEarlierShadow = occluded,
                poseEpoch = capture.poseEpoch,
                poseAppliedUnityFrame = capture.poseAppliedUnityFrame,
                producerUnityFrame = capture.producerUnityFrame,
                producerScanSerial = capture.producerScanSerial,
                captureUnityFrame = capture.captureUnityFrame
            });
        }
    }
}
