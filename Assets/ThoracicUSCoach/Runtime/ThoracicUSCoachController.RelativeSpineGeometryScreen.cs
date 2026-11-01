using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SMMARTS;
using UnityEngine;

namespace CSSALT.ThoracicUSCoach
{
    public sealed partial class ThoracicUSCoachController
    {
        private const float RelativeSpineParamedianOffsetMm = 6f;

        [Serializable]
        private sealed class RelativeSpineGeometryCapture
        {
            public string level;
            public string geometry;
            public string repeat;
            public string image;
            public string imageSha256;
            public string provenance;
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
            public long poseEpoch;
            public int poseAppliedUnityFrame;
            public int producerUnityFrame;
            public long producerScanSerial;
            public int captureUnityFrame;
        }

        [Serializable]
        private sealed class RelativeSpineGeometryManifest
        {
            public string contract = "thoracic_relative_spine_e2_targeted_geometry_v1";
            public string generatedUtc;
            public string status = "RUNNING";
            public string actorUse = "PROHIBITED_EVALUATOR_GUIDED_DIAGNOSTIC_ONLY";
            public string method =
                "Seven evaluator-only reference regions, two independent fresh captures per pose, original unchanged scene.";
            public int newGeometryConfigurations;
            public List<RelativeSpineGeometryCapture> captures = new();
        }

        public bool StartRelativeSpineE2GeometryScreen()
        {
            if (relativeSpineAuditRoutine != null || relativeSpineAuditComplete) return false;
            relativeSpineAuditFailure = string.Empty;
            relativeSpineAuditStatus = "RUNNING_E2_GEOMETRY_SCREEN";
            relativeSpineAuditRoutine = StartCoroutine(RunRelativeSpineE2Guarded());
            return true;
        }

        private IEnumerator RunRelativeSpineE2Guarded()
        {
            Exception failure = null;
            Stack<IEnumerator> stack = new Stack<IEnumerator>();
            stack.Push(RunRelativeSpineE2Core());
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
            relativeSpineAuditStatus = "E2_INFRASTRUCTURE_FAILURE";
            relativeSpineAuditRoutine = null;
            relativeSpineAuditComplete = true;
            Debug.LogError("[ThoracicUSCoach][RelativeSpineGeometry] " + failure);
        }

        private IEnumerator RunRelativeSpineE2Core()
        {
            AutoWire();
            string outputRoot = RelativeSpineAuditOutputRoot;
            string geometryRoot = Path.Combine(outputRoot, "evaluator_only", "e2_geometry");
            string registryPath = Path.Combine(outputRoot, "experiment_registry.json");
            Vector3 originalPosition = probeRoot != null ? probeRoot.position : Vector3.zero;
            Quaternion originalRotation = probeRoot != null ? probeRoot.rotation : Quaternion.identity;
            float originalSweepYaw = sweepCountYawDeg;
            try
            {
                if (!File.Exists(registryPath))
                    throw new InvalidOperationException("Relative-spine E2 registry is missing");
                RelativeSpineExperimentRegistry registry = JsonUtility.FromJson<RelativeSpineExperimentRegistry>(
                    File.ReadAllText(registryPath));
                if (registry == null || registry.e0 != "PASS" || registry.e1 != "PASS")
                    throw new InvalidOperationException(
                        "Relative-spine E2 requires frozen E0 and E1 PASS review results");
                if (registry.e2 != "NOT_RUN")
                    throw new InvalidOperationException("Relative-spine E2 has already consumed its targeted diagnostic run");
                if (Directory.Exists(geometryRoot))
                    throw new InvalidOperationException("Relative-spine E2 output exists; refusing to overwrite evidence");
                if (!ValidateReferences(out string referenceError))
                    throw new InvalidOperationException(referenceError);
                if (probeRoot == null || faceCenter == null || rearCenter == null
                    || skinCollider == null || skinIntersection == null)
                    throw new InvalidOperationException(
                        "Relative-spine E2 requires probe, face/rear centers, exact skin collider, and contact measurement");
                if (!UltrasoundManager.TryGet(UltrasoundManager.Plane.Primary,
                        out UltrasoundManager manager) || manager == null)
                    throw new InvalidOperationException("Relative-spine E2 primary ultrasound manager is unavailable");

                Directory.CreateDirectory(geometryRoot);
                RelativeSpineGeometryManifest manifest = new RelativeSpineGeometryManifest
                {
                    generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    newGeometryConfigurations = 2
                };
                SweepCountReferenceSet references = BuildSweepCountReferences();
                Vector3 corridorAxis = (references.references[6].contactPoint
                    - references.references[0].contactPoint).normalized;
                float[] offsets = { RelativeSpineParamedianOffsetMm, -RelativeSpineParamedianOffsetMm };
                foreach (float offset in offsets)
                {
                    string geometry = offset > 0f
                        ? "PARAMEDIAN_LONGITUDINAL_P6" : "PARAMEDIAN_LONGITUDINAL_M6";
                    string frameRoot = Path.Combine(geometryRoot, geometry, "frames");
                    string provenanceRoot = Path.Combine(geometryRoot, geometry, "provenance");
                    Directory.CreateDirectory(frameRoot);
                    Directory.CreateDirectory(provenanceRoot);
                    foreach (SweepCountReference reference in references.references)
                    {
                        if (!FindSurfaceAt(reference.contactPoint, reference.surfaceNormal,
                                out RaycastHit centralHit))
                            throw new InvalidOperationException(
                                "Relative-spine E2 central projection failed at " + reference.level);
                        Vector3 localCorridor = Vector3.ProjectOnPlane(
                            corridorAxis, centralHit.normal).normalized;
                        Vector3 lateralAxis = Vector3.Cross(
                            centralHit.normal, localCorridor).normalized;
                        if (localCorridor.sqrMagnitude < 0.5f || lateralAxis.sqrMagnitude < 0.5f)
                            throw new InvalidOperationException(
                                "Relative-spine E2 local surface axes are degenerate at " + reference.level);
                        Vector3 proposal = centralHit.point + lateralAxis * offset;
                        if (!FindSurfaceAt(proposal, centralHit.normal, out RaycastHit geometryHit))
                            throw new InvalidOperationException(
                                "Relative-spine E2 offset projection failed at " + reference.level
                                + " for " + geometry);
                        sweepCountYawDeg = 90f;
                        if (!PlaceSweepCountProbe(geometryHit.point, geometryHit.normal,
                                corridorAxis, out string placementFailure))
                            throw new InvalidOperationException(
                                "Relative-spine E2 placement failed at " + reference.level
                                + " for " + geometry + ": " + placementFailure);
                        Vector3 normal = CurrentSurfaceNormal();
                        localCorridor = Vector3.ProjectOnPlane(corridorAxis, normal).normalized;
                        lateralAxis = Vector3.Cross(normal, localCorridor).normalized;
                        Vector3 scanWidth = Vector3.ProjectOnPlane(
                            probeRoot.right, normal).normalized;
                        float widthAgainstCorridor = Mathf.Abs(Vector3.Dot(scanWidth, localCorridor));
                        if (widthAgainstCorridor < 0.95f)
                            throw new InvalidOperationException(
                                "Relative-spine E2 scan plane is not longitudinal at " + reference.level
                                + " for " + geometry);
                        float achievedOffset = Vector3.Dot(
                            faceCenter.position - centralHit.point, lateralAxis);
                        if (Mathf.Abs(achievedOffset - offset) > 1.5f)
                            throw new InvalidOperationException(
                                "Relative-spine E2 lateral offset error exceeded 1.5 mm at "
                                + reference.level + " for " + geometry);
                        for (int repeat = 0; repeat < 2; repeat++)
                        {
                            yield return CaptureRelativeSpineE2Geometry(
                                manager, manifest, reference.level, geometry,
                                repeat == 0 ? "repeat_a" : "repeat_b", offset, achievedOffset,
                                localCorridor, lateralAxis, scanWidth, widthAgainstCorridor,
                                frameRoot, provenanceRoot);
                        }
                    }
                }
                manifest.status = "CAPTURED_PENDING_OFFLINE_GEOMETRY_REVIEW";
                WriteSweepCountJson(Path.Combine(geometryRoot, "capture_manifest.json"), manifest);
                registry.generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                registry.state = "E2_CAPTURE_COMPLETE";
                registry.e2 = "CAPTURE_COMPLETE_PENDING_OFFLINE_REVIEW";
                registry.diagnosticUnityRunsConsumed += 1;
                registry.newGeometryConfigurationsConsumed += 2;
                WriteSweepCountJson(registryPath, registry);
                relativeSpineAuditStatus = "E2_CAPTURE_COMPLETE_PENDING_OFFLINE_REVIEW";
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

        private IEnumerator CaptureRelativeSpineE2Geometry(
            UltrasoundManager manager,
            RelativeSpineGeometryManifest manifest,
            string level,
            string geometry,
            string repeat,
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
                "relative_spine_e2_" + geometry + "_" + level + "_" + repeat, true);
            DirectFreshCaptureResult capture = new DirectFreshCaptureResult();
            yield return CaptureDirectProducerFreshFrame(token,
                "relative_spine_e2_targeted_geometry", capture, true);
            if (!M0BFrameIntegrityValid(capture.image, capture.pngBytes))
                throw new InvalidOperationException(
                    "Relative-spine E2 frame integrity failed at " + level + " for " + geometry);
            if (!manager.TryGetPublishedScanProvenance(token.poseEpoch, capture.producerScanSerial,
                    out UltrasoundManager.PublishedScanProvenanceSnapshot provenance))
                throw new InvalidOperationException(
                    "Relative-spine E2 provenance is missing at " + level + " for " + geometry);
            string stem = level + "_" + repeat;
            string imageFile = stem + ".png";
            string provenanceFile = stem + ".json";
            File.WriteAllBytes(Path.Combine(frameRoot, imageFile), capture.pngBytes);
            WriteSweepCountJson(Path.Combine(provenanceRoot, provenanceFile), provenance);
            Vector3 normal = CurrentSurfaceNormal();
            float achievedYaw = MeasureSweepCountSurfaceYawDeg(normal);
            manifest.captures.Add(new RelativeSpineGeometryCapture
            {
                level = level,
                geometry = geometry,
                repeat = repeat,
                image = "evaluator_only/e2_geometry/" + geometry + "/frames/" + imageFile,
                imageSha256 = Sha256Bytes(capture.pngBytes),
                provenance = "evaluator_only/e2_geometry/" + geometry + "/provenance/" + provenanceFile,
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
                poseEpoch = capture.poseEpoch,
                poseAppliedUnityFrame = capture.poseAppliedUnityFrame,
                producerUnityFrame = capture.producerUnityFrame,
                producerScanSerial = capture.producerScanSerial,
                captureUnityFrame = capture.captureUnityFrame
            });
        }
    }
}
