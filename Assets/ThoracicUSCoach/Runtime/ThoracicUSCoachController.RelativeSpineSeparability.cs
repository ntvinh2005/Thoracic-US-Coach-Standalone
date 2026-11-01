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
        [Serializable]
        private sealed class RelativeSpineE4Capture
        {
            public string anchorId;
            public string anchorType;
            public string cranialLevel;
            public string caudalLevel;
            public string repeat;
            public string image;
            public string imageSha256;
            public string provenance;
            public float evaluatorCorridorCoordinateMm;
            public float commandedLateralOffsetMm;
            public float achievedLateralOffsetMm;
            public float commandedSurfaceYawDeg;
            public float achievedSurfaceYawDeg;
            public float surfaceYawErrorDeg;
            public float normalAlignmentErrorDeg;
            public float scanWidthAgainstCorridor;
            public Vector3 probePosition;
            public Quaternion probeRotation;
            public Vector3 contactPoint;
            public bool contactValid;
            public bool workspaceValid;
            public bool frameIntegrityValid;
            public bool provenanceMatched;
            public long poseEpoch;
            public int producerUnityFrame;
            public long producerScanSerial;
            public int captureUnityFrame;
        }

        [Serializable]
        private sealed class RelativeSpineE4Manifest
        {
            public string contract = "thoracic_relative_spine_e4_separability_capture_v1";
            public string generatedUtc;
            public string status = "RUNNING";
            public string geometry = "PARAMEDIAN_LONGITUDINAL_M6";
            public string actorUse = "PROHIBITED_EVALUATOR_GUIDED_DIAGNOSTIC_ONLY";
            public string method =
                "Original-scene level-center and interlevel-midpoint acquisitions, two fresh independent renders per pose.";
            public List<RelativeSpineE4Capture> captures = new();
        }

        public bool StartRelativeSpineE4Separability()
        {
            return StartRelativeSpineE4Separability(
                -RelativeSpineParamedianOffsetMm, 90f,
                "e4_separability", "PARAMEDIAN_LONGITUDINAL_M6");
        }

        public bool StartRelativeSpineE4SeparabilityP6()
        {
            return StartRelativeSpineE4Separability(
                RelativeSpineParamedianOffsetMm, 90f,
                "e4_separability_p6", "PARAMEDIAN_LONGITUDINAL_P6");
        }

        public bool StartRelativeSpineE4ObliqueM6()
        {
            return StartRelativeSpineE4Separability(
                -RelativeSpineParamedianOffsetMm, 60f,
                "e4_separability_oblique_m6_yaw60", "PARAMEDIAN_OBLIQUE_M6_YAW60");
        }

        private bool StartRelativeSpineE4Separability(
            float lateralOffsetMm, float surfaceYawDeg, string outputTag, string geometry)
        {
            if (relativeSpineAuditRoutine != null || relativeSpineAuditComplete) return false;
            relativeSpineAuditFailure = string.Empty;
            relativeSpineAuditStatus = "RUNNING_E4_SEPARABILITY";
            relativeSpineAuditRoutine = StartCoroutine(
                RunRelativeSpineE4Guarded(lateralOffsetMm, surfaceYawDeg, outputTag, geometry));
            return true;
        }

        private IEnumerator RunRelativeSpineE4Guarded(
            float lateralOffsetMm, float surfaceYawDeg, string outputTag, string geometry)
        {
            Exception failure = null;
            Stack<IEnumerator> stack = new Stack<IEnumerator>();
            stack.Push(RunRelativeSpineE4Core(lateralOffsetMm, surfaceYawDeg, outputTag, geometry));
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
            relativeSpineAuditStatus = "E4_INFRASTRUCTURE_FAILURE";
            relativeSpineAuditRoutine = null;
            relativeSpineAuditComplete = true;
            Debug.LogError("[ThoracicUSCoach][RelativeSpineSeparability] " + failure);
        }

        private IEnumerator RunRelativeSpineE4Core(
            float lateralOffsetMm, float surfaceYawDeg, string outputTag, string geometry)
        {
            AutoWire();
            string outputRoot = RelativeSpineAuditOutputRoot;
            string e4Root = Path.Combine(outputRoot, "evaluator_only", outputTag);
            string frameRoot = Path.Combine(e4Root, "frames");
            string provenanceRoot = Path.Combine(e4Root, "provenance");
            string registryPath = Path.Combine(outputRoot, "experiment_registry.json");
            Vector3 originalPosition = probeRoot != null ? probeRoot.position : Vector3.zero;
            Quaternion originalRotation = probeRoot != null ? probeRoot.rotation : Quaternion.identity;
            float originalSweepYaw = sweepCountYawDeg;
            try
            {
                if (Directory.Exists(e4Root))
                    throw new InvalidOperationException("Relative-spine E4 output exists; refusing to overwrite evidence");
                if (!File.Exists(registryPath))
                    throw new InvalidOperationException("Relative-spine E4 registry is missing");
                RelativeSpineExperimentRegistry registry = JsonUtility.FromJson<RelativeSpineExperimentRegistry>(
                    File.ReadAllText(registryPath));
                string expectedE4State = surfaceYawDeg < 89f
                    ? "M6_P6_AMBIGUOUS_OBLIQUE_RUNNING"
                    : lateralOffsetMm < 0f ? "RUNNING" : "M6_AMBIGUOUS_P6_RUNNING";
                if (registry == null || registry.e2 != "PASS_TARGETED_REFERENCE_SEPARABILITY_G2_STILL_FAIL"
                    || registry.e4 != expectedE4State)
                    throw new InvalidOperationException("Relative-spine E4 requires the frozen E2 diagnostic decision");
                if (!ValidateReferences(out string referenceError))
                    throw new InvalidOperationException(referenceError);
                if (!UltrasoundManager.TryGet(UltrasoundManager.Plane.Primary,
                        out UltrasoundManager manager) || manager == null)
                    throw new InvalidOperationException("Relative-spine E4 primary ultrasound manager is unavailable");
                Directory.CreateDirectory(frameRoot);
                Directory.CreateDirectory(provenanceRoot);
                SweepCountReferenceSet references = BuildSweepCountReferences();
                Vector3 t2 = references.references[0].contactPoint;
                Vector3 corridorAxis = (references.references[6].contactPoint - t2).normalized;
                RelativeSpineE4Manifest manifest = new RelativeSpineE4Manifest
                {
                    generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    geometry = geometry
                };
                for (int index = 0; index < references.references.Count; index++)
                {
                    SweepCountReference current = references.references[index];
                    yield return CaptureRelativeSpineE4Anchor(
                        manager, manifest, "CENTER_" + current.level, "LEVEL_CENTER",
                        current.level, current.level, current.contactPoint, current.surfaceNormal,
                        t2, corridorAxis, lateralOffsetMm, surfaceYawDeg,
                        outputTag, frameRoot, provenanceRoot);
                    if (index >= references.references.Count - 1) continue;
                    SweepCountReference next = references.references[index + 1];
                    Vector3 midpoint = 0.5f * (current.contactPoint + next.contactPoint);
                    Vector3 expectedNormal = (current.surfaceNormal + next.surfaceNormal).normalized;
                    yield return CaptureRelativeSpineE4Anchor(
                        manager, manifest, "INTERLEVEL_" + current.level + "_" + next.level,
                        "INTERLEVEL_MIDPOINT", current.level, next.level, midpoint, expectedNormal,
                        t2, corridorAxis, lateralOffsetMm, surfaceYawDeg,
                        outputTag, frameRoot, provenanceRoot);
                }
                manifest.status = "CAPTURED_PENDING_OFFLINE_SEPARABILITY_REVIEW";
                WriteSweepCountJson(Path.Combine(e4Root, "capture_manifest.json"), manifest);
                registry.generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                registry.state = surfaceYawDeg < 89f
                    ? "E4_OBLIQUE_CAPTURE_COMPLETE"
                    : lateralOffsetMm < 0f ? "E4_M6_CAPTURE_COMPLETE" : "E4_P6_CAPTURE_COMPLETE";
                registry.e4 = surfaceYawDeg < 89f
                    ? "OBLIQUE_CAPTURE_COMPLETE_PENDING_OFFLINE_REVIEW"
                    : lateralOffsetMm < 0f
                        ? "CAPTURE_COMPLETE_PENDING_OFFLINE_REVIEW"
                        : "P6_CAPTURE_COMPLETE_PENDING_OFFLINE_REVIEW";
                registry.diagnosticUnityRunsConsumed += 1;
                if (surfaceYawDeg < 89f) registry.newGeometryConfigurationsConsumed += 1;
                WriteSweepCountJson(registryPath, registry);
                relativeSpineAuditStatus = "E4_CAPTURE_COMPLETE_PENDING_OFFLINE_REVIEW";
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

        private IEnumerator CaptureRelativeSpineE4Anchor(
            UltrasoundManager manager,
            RelativeSpineE4Manifest manifest,
            string anchorId,
            string anchorType,
            string cranialLevel,
            string caudalLevel,
            Vector3 centralProposal,
            Vector3 expectedNormal,
            Vector3 t2Contact,
            Vector3 corridorAxis,
            float lateralOffsetMm,
            float surfaceYawDeg,
            string outputTag,
            string frameRoot,
            string provenanceRoot)
        {
            if (!FindSurfaceAt(centralProposal, expectedNormal, out RaycastHit centralHit))
                throw new InvalidOperationException("Relative-spine E4 central projection failed at " + anchorId);
            Vector3 localCorridor = Vector3.ProjectOnPlane(corridorAxis, centralHit.normal).normalized;
            Vector3 lateralAxis = Vector3.Cross(centralHit.normal, localCorridor).normalized;
            Vector3 offsetProposal = centralHit.point + lateralAxis * lateralOffsetMm;
            if (!FindSurfaceAt(offsetProposal, centralHit.normal, out RaycastHit offsetHit))
                throw new InvalidOperationException("Relative-spine E4 offset projection failed at " + anchorId);
            sweepCountYawDeg = surfaceYawDeg;
            if (!PlaceSweepCountProbe(offsetHit.point, offsetHit.normal, corridorAxis,
                    out string placementFailure))
                throw new InvalidOperationException(
                    "Relative-spine E4 placement failed at " + anchorId + ": " + placementFailure);
            Vector3 normal = CurrentSurfaceNormal();
            localCorridor = Vector3.ProjectOnPlane(corridorAxis, normal).normalized;
            lateralAxis = Vector3.Cross(normal, localCorridor).normalized;
            Vector3 scanWidth = Vector3.ProjectOnPlane(probeRoot.right, normal).normalized;
            float widthAgainstCorridor = Mathf.Abs(Vector3.Dot(scanWidth, localCorridor));
            float achievedOffset = Vector3.Dot(faceCenter.position - centralHit.point, lateralAxis);
            float expectedCorridorAlignment = Mathf.Abs(Mathf.Sin(surfaceYawDeg * Mathf.Deg2Rad));
            if (Mathf.Abs(widthAgainstCorridor - expectedCorridorAlignment) > 0.05f
                || Mathf.Abs(achievedOffset - lateralOffsetMm) > 1.5f)
                throw new InvalidOperationException("Relative-spine E4 acquisition geometry failed at " + anchorId);
            for (int repeatIndex = 0; repeatIndex < 2; repeatIndex++)
            {
                string repeat = repeatIndex == 0 ? "repeat_a" : "repeat_b";
                DirectFreshPoseToken token = BeginDirectFreshCapturePose(
                    "relative_spine_e4_" + anchorId + "_" + repeat, true);
                DirectFreshCaptureResult capture = new DirectFreshCaptureResult();
                yield return CaptureDirectProducerFreshFrame(
                    token, "relative_spine_e4_targeted_separability", capture, true);
                if (!M0BFrameIntegrityValid(capture.image, capture.pngBytes))
                    throw new InvalidOperationException("Relative-spine E4 frame integrity failed at " + anchorId);
                if (!manager.TryGetPublishedScanProvenance(token.poseEpoch, capture.producerScanSerial,
                        out UltrasoundManager.PublishedScanProvenanceSnapshot provenance))
                    throw new InvalidOperationException("Relative-spine E4 provenance is missing at " + anchorId);
                string stem = anchorId + "_" + repeat;
                File.WriteAllBytes(Path.Combine(frameRoot, stem + ".png"), capture.pngBytes);
                WriteSweepCountJson(Path.Combine(provenanceRoot, stem + ".json"), provenance);
                float achievedYaw = MeasureSweepCountSurfaceYawDeg(normal);
                manifest.captures.Add(new RelativeSpineE4Capture
                {
                    anchorId = anchorId,
                    anchorType = anchorType,
                    cranialLevel = cranialLevel,
                    caudalLevel = caudalLevel,
                    repeat = repeat,
                    image = "evaluator_only/" + outputTag + "/frames/" + stem + ".png",
                    imageSha256 = Sha256Bytes(capture.pngBytes),
                    provenance = "evaluator_only/" + outputTag + "/provenance/" + stem + ".json",
                    evaluatorCorridorCoordinateMm = Vector3.Dot(centralHit.point - t2Contact, corridorAxis),
                    commandedLateralOffsetMm = lateralOffsetMm,
                    achievedLateralOffsetMm = achievedOffset,
                    commandedSurfaceYawDeg = surfaceYawDeg,
                    achievedSurfaceYawDeg = achievedYaw,
                    surfaceYawErrorDeg = Mathf.DeltaAngle(surfaceYawDeg, achievedYaw),
                    normalAlignmentErrorDeg = Vector3.Angle(
                        (rearCenter.position - faceCenter.position).normalized, normal),
                    scanWidthAgainstCorridor = widthAgainstCorridor,
                    probePosition = probeRoot.position,
                    probeRotation = probeRoot.rotation,
                    contactPoint = faceCenter.position,
                    contactValid = MeasureSweepCountContact(),
                    workspaceValid = IsActive2DWorkspaceValid(faceCenter.position),
                    frameIntegrityValid = true,
                    provenanceMatched = provenance.poseEpoch == capture.producerPoseEpoch
                        && provenance.producerUnityFrame == capture.producerUnityFrame
                        && provenance.producerScanSerial == capture.producerScanSerial,
                    poseEpoch = capture.poseEpoch,
                    producerUnityFrame = capture.producerUnityFrame,
                    producerScanSerial = capture.producerScanSerial,
                    captureUnityFrame = capture.captureUnityFrame
                });
            }
        }
    }
}
