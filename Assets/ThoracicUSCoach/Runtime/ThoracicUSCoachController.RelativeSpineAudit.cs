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
        private const string RelativeSpineAuditOutputDirectory =
            "Artifacts/ThoracicUSCoach/sweep_count/AUTONOMOUS_RELATIVE_SPINE_MAPPING_E0_E8_20261101";
        private Coroutine relativeSpineAuditRoutine;
        private bool relativeSpineAuditComplete;
        private string relativeSpineAuditFailure = string.Empty;
        private string relativeSpineAuditStatus = "NOT_RUN";

        public bool RelativeSpineAuditComplete => relativeSpineAuditComplete;
        public string RelativeSpineAuditFailure => relativeSpineAuditFailure;
        public string RelativeSpineAuditStatus => relativeSpineAuditStatus;
        public string RelativeSpineAuditOutputRoot => Path.GetFullPath(
            Path.Combine(Application.dataPath, "..", RelativeSpineAuditOutputDirectory));

        [Serializable]
        private sealed class RelativeSpineSceneObject
        {
            public string objectName;
            public string parentPath;
            public int instanceId;
            public int layer;
            public string classification;
            public bool showOnUltrasound;
            public string renderType;
            public float anisotropyIndex;
            public float edgeThickness;
            public bool rendererPresent;
            public string meshName;
            public Vector3 boundsCenter;
            public Vector3 boundsSize;
            public bool colliderPresent;
        }

        [Serializable]
        private sealed class RelativeSpineReferenceAudit
        {
            public string level;
            public string labelObject;
            public string labelTag;
            public Vector3 labelPosition;
            public string levelSpecificObject;
            public string levelSpecificClassification;
            public string levelSpecificRenderType;
            public bool levelSpecificUltrasoundVisible;
            public Vector3 levelSpecificBoundsCenter;
            public Vector3 levelSpecificBoundsSize;
            public float labelToLevelSpecificBoundsMm;
            public string boneSourceObject;
            public string boneSourceMesh;
            public string boneSourceRenderType;
            public bool boneSourceIsCombinedMesh;
            public string perLevelBoneObject;
            public string associationEvidence;
            public Vector3 contactPoint;
            public Vector3 surfaceNormal;
            public bool surfaceProjectionValid;
        }

        [Serializable]
        private sealed class RelativeSpineAnatomyAudit
        {
            public string contract = "thoracic_relative_spine_e0_anatomy_audit_v1";
            public string generatedUtc;
            public string scene;
            public string unityVersion;
            public string referenceIsolation =
                "Evaluator-only. No object names, labels, transforms, bounds, or mesh identities enter the actor.";
            public string keyFinding =
                "T2-T8 are label markers; LF T2-T8 are level-named ligament objects; vertebral bone is rendered by one combined Skeleton Shadow object without per-level bone identities.";
            public List<RelativeSpineReferenceAudit> references = new();
            public List<RelativeSpineSceneObject> ultrasoundObjects = new();
        }

        [Serializable]
        private sealed class RelativeSpineDiagnosticCapture
        {
            public string level;
            public string geometry;
            public string variant;
            public string image;
            public string imageSha256;
            public string provenance;
            public string[] disabledUltrasoundObjects;
            public long poseEpoch;
            public int poseAppliedUnityFrame;
            public int producerUnityFrame;
            public long producerScanSerial;
            public int captureUnityFrame;
            public Vector3 probePosition;
            public Quaternion probeRotation;
            public Vector3 contactPoint;
            public float achievedSurfaceYawDeg;
            public float surfaceYawErrorDeg;
            public float normalAlignmentErrorDeg;
            public bool contactValid;
            public bool workspaceValid;
            public bool evaluatorGuidedDiagnosticPlacement;
            public bool frameIntegrityValid;
            public bool provenanceMatched;
        }

        [Serializable]
        private sealed class RelativeSpineE1Manifest
        {
            public string contract = "thoracic_relative_spine_e1_causal_capture_v1";
            public string generatedUtc;
            public string status;
            public string geometry = "TRANSVERSE_CENTER_REFERENCE_DIAGNOSTIC";
            public string method =
                "Evaluator-guided same-pose renders. Counterfactuals restore identical Unity RNG state and temporarily set ShowOnUltrasound=false; all visibility state is restored.";
            public string actorUse = "PROHIBITED_DIAGNOSTIC_ONLY";
            public List<RelativeSpineDiagnosticCapture> captures = new();
        }

        [Serializable]
        private sealed class RelativeSpineExperimentRegistry
        {
            public string contract = "thoracic_relative_spine_e0_e8_registry_v1";
            public string generatedUtc;
            public string state;
            public string e0;
            public string e1;
            public string e2 = "NOT_RUN";
            public string e3 = "NOT_RUN";
            public string e4 = "NOT_RUN";
            public string e5 = "NOT_RUN";
            public string e6 = "NOT_RUN";
            public string e7 = "NOT_RUN";
            public string e8 = "NOT_RUN";
            public int diagnosticUnityRunsConsumed;
            public int newGeometryConfigurationsConsumed;
            public int fullPhysicalDevelopmentSweepsConsumed;
            public int confirmationEpisodesConsumed;
        }

        public bool StartRelativeSpineE0E1Audit()
        {
            if (relativeSpineAuditRoutine != null || relativeSpineAuditComplete) return false;
            relativeSpineAuditFailure = string.Empty;
            relativeSpineAuditStatus = "RUNNING_E0_E1";
            relativeSpineAuditRoutine = StartCoroutine(RunRelativeSpineE0E1Guarded());
            return true;
        }

        private IEnumerator RunRelativeSpineE0E1Guarded()
        {
            Exception failure = null;
            Stack<IEnumerator> stack = new Stack<IEnumerator>();
            stack.Push(RunRelativeSpineE0E1Core());
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
            relativeSpineAuditStatus = "E0_E1_INFRASTRUCTURE_FAILURE";
            relativeSpineAuditRoutine = null;
            relativeSpineAuditComplete = true;
            Debug.LogError("[ThoracicUSCoach][RelativeSpineAudit] " + failure);
        }

        private IEnumerator RunRelativeSpineE0E1Core()
        {
            AutoWire();
            string outputRoot = RelativeSpineAuditOutputRoot;
            string evaluatorRoot = Path.Combine(outputRoot, "evaluator_only");
            string frameRoot = Path.Combine(evaluatorRoot, "e1_causal", "frames");
            string provenanceRoot = Path.Combine(evaluatorRoot, "e1_causal", "provenance");
            Vector3 originalPosition = probeRoot != null ? probeRoot.position : Vector3.zero;
            Quaternion originalRotation = probeRoot != null ? probeRoot.rotation : Quaternion.identity;
            float originalSweepYaw = sweepCountYawDeg;
            Dictionary<UltrasoundVisibility, bool> originalVisibility = new();
            RelativeSpineExperimentRegistry registry = new RelativeSpineExperimentRegistry
            {
                generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                state = "E0_E1_RUNNING",
                e0 = "RUNNING",
                e1 = "NOT_RUN",
                diagnosticUnityRunsConsumed = 1
            };
            try
            {
                if (!ValidateReferences(out string referenceError))
                    throw new InvalidOperationException(referenceError);
                if (probeRoot == null || faceCenter == null || rearCenter == null
                    || skinCollider == null || skinIntersection == null)
                    throw new InvalidOperationException(
                        "Relative-spine audit requires probe, face/rear centers, exact skin collider, and contact measurement");
                if (Directory.Exists(outputRoot))
                    throw new InvalidOperationException(
                        "Relative-spine E0-E8 output already exists; refusing to overwrite evidence or reset budgets");
                Directory.CreateDirectory(frameRoot);
                Directory.CreateDirectory(provenanceRoot);
                WriteSweepCountJson(Path.Combine(outputRoot, "experiment_registry.json"), registry);

                SweepCountReferenceSet references = BuildSweepCountReferences();
                RelativeSpineAnatomyAudit anatomyAudit = BuildRelativeSpineAnatomyAudit(references);
                WriteSweepCountJson(Path.Combine(evaluatorRoot, "anatomy_reference_audit.json"), anatomyAudit);
                registry.e0 = "CAPTURED_PENDING_OFFLINE_REVIEW";
                registry.e1 = "RUNNING";
                WriteSweepCountJson(Path.Combine(outputRoot, "experiment_registry.json"), registry);

                UltrasoundVisibility[] visibilityObjects = Resources.FindObjectsOfTypeAll<UltrasoundVisibility>()
                    .Where(item => item != null && item.gameObject.scene == gameObject.scene)
                    .ToArray();
                foreach (UltrasoundVisibility item in visibilityObjects)
                    originalVisibility[item] = item.ShowOnUltrasound;
                UltrasoundVisibility skeleton = visibilityObjects.FirstOrDefault(
                    item => item.gameObject.name == "Skeleton");
                if (skeleton == null || !skeleton.ShowOnUltrasound
                    || skeleton.Type != UltrasoundVisibility.RenderType.Shadow)
                    throw new InvalidOperationException(
                        "Relative-spine E1 requires the active combined Skeleton Shadow source");
                UltrasoundVisibility[] superficial = visibilityObjects.Where(item =>
                    item.gameObject.name.IndexOf("Trapezius", StringComparison.OrdinalIgnoreCase) >= 0
                    || item.gameObject.name.IndexOf("Erector Spinae", StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToArray();
                if (superficial.Length == 0)
                    throw new InvalidOperationException("Relative-spine E1 found no superficial muscle visibility objects");
                if (!UltrasoundManager.TryGet(UltrasoundManager.Plane.Primary,
                        out UltrasoundManager manager) || manager == null)
                    throw new InvalidOperationException("Relative-spine E1 primary ultrasound manager is unavailable");

                Vector3 corridorAxis = (references.references[6].contactPoint
                    - references.references[0].contactPoint).normalized;
                sweepCountYawDeg = 0f;
                if (!PlaceSweepCountProbe(references.references[0].contactPoint,
                        references.references[0].surfaceNormal, corridorAxis, out string parityPlacementFailure))
                    throw new InvalidOperationException(
                        "Relative-spine instrumentation parity placement failed: " + parityPlacementFailure);
                MiniZigzagInstrumentationCheck parity = RunMiniZigzagInstrumentationCheck(manager, evaluatorRoot);
                WriteSweepCountJson(Path.Combine(evaluatorRoot, "instrumentation_check.json"), parity);
                if (!parity.exactPixelParity || !parity.metadataMatched)
                    throw new InvalidOperationException(
                        "Relative-spine full-interface instrumentation changed pixels or mismatched publication metadata");

                RelativeSpineE1Manifest manifest = new RelativeSpineE1Manifest
                {
                    generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    status = "RUNNING"
                };
                foreach (SweepCountReference reference in references.references)
                {
                    RestoreRelativeSpineVisibility(originalVisibility);
                    if (!PlaceSweepCountProbe(reference.contactPoint, reference.surfaceNormal,
                            corridorAxis, out string placementFailure))
                        throw new InvalidOperationException(
                            "Relative-spine E1 placement failed at " + reference.level + ": " + placementFailure);
                    UltrasoundVisibility levelObject = visibilityObjects.FirstOrDefault(
                        item => item.gameObject.name == "LF " + reference.level);
                    if (levelObject == null || !levelObject.ShowOnUltrasound)
                        throw new InvalidOperationException(
                            "Relative-spine E1 level object is missing or hidden at " + reference.level);

                    UnityEngine.Random.State noiseState = UnityEngine.Random.state;
                    yield return CaptureRelativeSpineDiagnostic(manager, manifest, reference.level,
                        "noise_a", Array.Empty<UltrasoundVisibility>(), null,
                        frameRoot, provenanceRoot);
                    yield return CaptureRelativeSpineDiagnostic(manager, manifest, reference.level,
                        "noise_b", Array.Empty<UltrasoundVisibility>(), null,
                        frameRoot, provenanceRoot);

                    UnityEngine.Random.State controlledState = UnityEngine.Random.state;
                    yield return CaptureRelativeSpineDiagnostic(manager, manifest, reference.level,
                        "baseline_controlled", Array.Empty<UltrasoundVisibility>(), controlledState,
                        frameRoot, provenanceRoot);
                    yield return CaptureRelativeSpineDiagnostic(manager, manifest, reference.level,
                        "level_lf_disabled", new[] { levelObject }, controlledState,
                        frameRoot, provenanceRoot);
                    yield return CaptureRelativeSpineDiagnostic(manager, manifest, reference.level,
                        "combined_skeleton_disabled", new[] { skeleton }, controlledState,
                        frameRoot, provenanceRoot);
                    yield return CaptureRelativeSpineDiagnostic(manager, manifest, reference.level,
                        "superficial_muscle_disabled", superficial, controlledState,
                        frameRoot, provenanceRoot);
                    UnityEngine.Random.state = noiseState;
                }
                RestoreRelativeSpineVisibility(originalVisibility);
                manifest.status = "CAPTURED_PENDING_OFFLINE_CAUSAL_ANALYSIS";
                WriteSweepCountJson(Path.Combine(evaluatorRoot, "e1_capture_manifest.json"), manifest);
                registry.e0 = "CAPTURE_COMPLETE";
                registry.e1 = "CAPTURE_COMPLETE_PENDING_OFFLINE_REVIEW";
                registry.state = "E1_CAPTURE_COMPLETE";
                WriteSweepCountJson(Path.Combine(outputRoot, "experiment_registry.json"), registry);
                relativeSpineAuditStatus = "E1_CAPTURE_COMPLETE_PENDING_OFFLINE_REVIEW";
            }
            finally
            {
                RestoreRelativeSpineVisibility(originalVisibility);
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

        private RelativeSpineAnatomyAudit BuildRelativeSpineAnatomyAudit(
            SweepCountReferenceSet references)
        {
            UltrasoundVisibility[] visibilityObjects = Resources.FindObjectsOfTypeAll<UltrasoundVisibility>()
                .Where(item => item != null && item.gameObject.scene == gameObject.scene)
                .OrderBy(item => item.gameObject.name, StringComparer.Ordinal)
                .ToArray();
            UltrasoundVisibility skeleton = visibilityObjects.FirstOrDefault(
                item => item.gameObject.name == "Skeleton");
            Renderer skeletonRenderer = skeleton != null
                ? skeleton.GetComponentInChildren<Renderer>(true) : null;
            MeshFilter skeletonMeshFilter = skeleton != null
                ? skeleton.GetComponentInChildren<MeshFilter>(true) : null;
            SkinnedMeshRenderer skeletonSkinned = skeleton != null
                ? skeleton.GetComponentInChildren<SkinnedMeshRenderer>(true) : null;
            string skeletonMesh = skeletonMeshFilter != null && skeletonMeshFilter.sharedMesh != null
                ? skeletonMeshFilter.sharedMesh.name
                : skeletonSkinned != null && skeletonSkinned.sharedMesh != null
                    ? skeletonSkinned.sharedMesh.name : string.Empty;
            RelativeSpineAnatomyAudit audit = new RelativeSpineAnatomyAudit
            {
                generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                scene = gameObject.scene.path,
                unityVersion = Application.unityVersion
            };
            foreach (UltrasoundVisibility item in visibilityObjects)
            {
                Renderer renderer = item.GetComponentInChildren<Renderer>(true);
                MeshFilter meshFilter = item.GetComponentInChildren<MeshFilter>(true);
                SkinnedMeshRenderer skinned = item.GetComponentInChildren<SkinnedMeshRenderer>(true);
                Collider collider = item.GetComponentInChildren<Collider>(true);
                audit.ultrasoundObjects.Add(new RelativeSpineSceneObject
                {
                    objectName = item.gameObject.name,
                    parentPath = RelativeSpineTransformPath(item.transform),
                    instanceId = item.gameObject.GetInstanceID(),
                    layer = item.gameObject.layer,
                    classification = RelativeSpineObjectClassification(item.gameObject.name),
                    showOnUltrasound = item.ShowOnUltrasound,
                    renderType = item.Type.ToString(),
                    anisotropyIndex = item.AnisoIndex,
                    edgeThickness = item.EdgeThickness,
                    rendererPresent = renderer != null,
                    meshName = meshFilter != null && meshFilter.sharedMesh != null
                        ? meshFilter.sharedMesh.name
                        : skinned != null && skinned.sharedMesh != null ? skinned.sharedMesh.name : string.Empty,
                    boundsCenter = renderer != null ? renderer.bounds.center : Vector3.zero,
                    boundsSize = renderer != null ? renderer.bounds.size : Vector3.zero,
                    colliderPresent = collider != null
                });
            }
            Transform[] transforms = Resources.FindObjectsOfTypeAll<Transform>()
                .Where(item => item != null && item.gameObject.scene == gameObject.scene)
                .ToArray();
            foreach (SweepCountReference reference in references.references)
            {
                Transform label = transforms.FirstOrDefault(item => item.name == reference.level
                    && item.CompareTag("Label"));
                Transform lf = transforms.FirstOrDefault(item => item.name == "LF " + reference.level);
                Renderer lfRenderer = lf != null ? lf.GetComponentInChildren<Renderer>(true) : null;
                UltrasoundVisibility lfVisibility = lf != null
                    ? lf.GetComponentInChildren<UltrasoundVisibility>(true) : null;
                audit.references.Add(new RelativeSpineReferenceAudit
                {
                    level = reference.level,
                    labelObject = label != null ? label.name : string.Empty,
                    labelTag = label != null ? label.tag : string.Empty,
                    labelPosition = label != null ? label.position : Vector3.zero,
                    levelSpecificObject = lf != null ? lf.name : string.Empty,
                    levelSpecificClassification = "LIGAMENTUM_FLAVUM_NAME_BASED",
                    levelSpecificRenderType = lfVisibility != null ? lfVisibility.Type.ToString() : string.Empty,
                    levelSpecificUltrasoundVisible = lfVisibility != null && lfVisibility.ShowOnUltrasound,
                    levelSpecificBoundsCenter = lfRenderer != null ? lfRenderer.bounds.center : Vector3.zero,
                    levelSpecificBoundsSize = lfRenderer != null ? lfRenderer.bounds.size : Vector3.zero,
                    labelToLevelSpecificBoundsMm = label != null && lfRenderer != null
                        ? Vector3.Distance(label.position, lfRenderer.bounds.ClosestPoint(label.position)) : -1f,
                    boneSourceObject = skeleton != null ? skeleton.gameObject.name : string.Empty,
                    boneSourceMesh = skeletonMesh,
                    boneSourceRenderType = skeleton != null ? skeleton.Type.ToString() : string.Empty,
                    boneSourceIsCombinedMesh = skeleton != null,
                    perLevelBoneObject = string.Empty,
                    associationEvidence =
                        "Label and LF object share a T-level name; no separately named per-level vertebral bone renderer exists. Bone pixels originate from combined Skeleton geometry.",
                    contactPoint = reference.contactPoint,
                    surfaceNormal = reference.surfaceNormal,
                    surfaceProjectionValid = reference.surfaceProjectionValid
                });
            }
            if (skeletonRenderer == null)
                audit.keyFinding += " Combined Skeleton renderer was not resolved at runtime.";
            return audit;
        }

        private IEnumerator CaptureRelativeSpineDiagnostic(
            UltrasoundManager manager,
            RelativeSpineE1Manifest manifest,
            string level,
            string variant,
            UltrasoundVisibility[] disabled,
            UnityEngine.Random.State? controlledState,
            string frameRoot,
            string provenanceRoot)
        {
            Dictionary<UltrasoundVisibility, bool> states = disabled
                .Where(item => item != null).Distinct().ToDictionary(item => item, item => item.ShowOnUltrasound);
            foreach (UltrasoundVisibility item in states.Keys) item.ShowOnUltrasound = false;
            Physics.SyncTransforms();
            DirectFreshPoseToken token = BeginDirectFreshCapturePose(
                "relative_spine_e1_" + level + "_" + variant, true);
            yield return null;
            ValidateDirectFreshPose(token);
            if (controlledState.HasValue) UnityEngine.Random.state = controlledState.Value;
            manager.ArmNextPublishedScanProvenance();
            Physics.SyncTransforms();
            if (!manager.PublishCurrentPose(token.poseEpoch))
                throw new InvalidOperationException(
                    "Relative-spine diagnostic producer did not publish for " + level + " " + variant);
            if (manager.PublishedPoseEpoch != token.poseEpoch
                || manager.PublishedUnityFrame != Time.frameCount
                || manager.PublishedUnityFrame <= token.poseAppliedUnityFrame
                || manager.PublishedScanSerial <= token.minimumProducerScanSerial)
                throw new InvalidOperationException(
                    "Relative-spine diagnostic freshness contract failed for " + level + " " + variant);
            Texture2D image = CaptureCleanImage();
            byte[] png = image.EncodeToPNG();
            if (!M0BFrameIntegrityValid(image, png))
                throw new InvalidOperationException(
                    "Relative-spine diagnostic frame integrity failed for " + level + " " + variant);
            if (!manager.TryGetPublishedScanProvenance(token.poseEpoch, manager.PublishedScanSerial,
                    out UltrasoundManager.PublishedScanProvenanceSnapshot provenance))
                throw new InvalidOperationException(
                    "Relative-spine diagnostic provenance is missing for " + level + " " + variant);
            string stem = level + "_" + variant;
            string imageFile = stem + ".png";
            string provenanceFile = stem + ".json";
            File.WriteAllBytes(Path.Combine(frameRoot, imageFile), png);
            WriteSweepCountJson(Path.Combine(provenanceRoot, provenanceFile), provenance);
            Vector3 normal = CurrentSurfaceNormal();
            float yaw = MeasureSweepCountSurfaceYawDeg(normal);
            manifest.captures.Add(new RelativeSpineDiagnosticCapture
            {
                level = level,
                geometry = "TRANSVERSE_CENTER_REFERENCE_DIAGNOSTIC",
                variant = variant,
                image = "evaluator_only/e1_causal/frames/" + imageFile,
                imageSha256 = Sha256Bytes(png),
                provenance = "evaluator_only/e1_causal/provenance/" + provenanceFile,
                disabledUltrasoundObjects = states.Keys.Select(item => item.gameObject.name).ToArray(),
                poseEpoch = token.poseEpoch,
                poseAppliedUnityFrame = token.poseAppliedUnityFrame,
                producerUnityFrame = manager.PublishedUnityFrame,
                producerScanSerial = manager.PublishedScanSerial,
                captureUnityFrame = Time.frameCount,
                probePosition = probeRoot.position,
                probeRotation = probeRoot.rotation,
                contactPoint = faceCenter.position,
                achievedSurfaceYawDeg = yaw,
                surfaceYawErrorDeg = Mathf.DeltaAngle(0f, yaw),
                normalAlignmentErrorDeg = Vector3.Angle(
                    (rearCenter.position - faceCenter.position).normalized, normal),
                contactValid = MeasureSweepCountContact(),
                workspaceValid = IsActive2DWorkspaceValid(faceCenter.position),
                evaluatorGuidedDiagnosticPlacement = true,
                frameIntegrityValid = true,
                provenanceMatched = provenance.poseEpoch == token.poseEpoch
                    && provenance.producerUnityFrame == manager.PublishedUnityFrame
                    && provenance.producerScanSerial == manager.PublishedScanSerial
            });
            foreach (KeyValuePair<UltrasoundVisibility, bool> state in states)
                if (state.Key != null) state.Key.ShowOnUltrasound = state.Value;
        }

        private static void RestoreRelativeSpineVisibility(
            Dictionary<UltrasoundVisibility, bool> originalVisibility)
        {
            foreach (KeyValuePair<UltrasoundVisibility, bool> state in originalVisibility)
                if (state.Key != null) state.Key.ShowOnUltrasound = state.Value;
            Physics.SyncTransforms();
        }

        private static string RelativeSpineObjectClassification(string name)
        {
            if (name == "Skeleton") return "COMBINED_BONE_MESH";
            if (name.StartsWith("LF T", StringComparison.Ordinal)) return "LIGAMENTUM_FLAVUM_NAME_BASED";
            if (name.IndexOf("Trapezius", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Erector Spinae", StringComparison.OrdinalIgnoreCase) >= 0)
                return "MUSCLE";
            if (name.IndexOf("Pleura", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Lung", StringComparison.OrdinalIgnoreCase) >= 0)
                return "PLEURA_OR_LUNG";
            return "OTHER_OR_UNRESOLVED";
        }

        private static string RelativeSpineTransformPath(Transform transform)
        {
            List<string> names = new List<string>();
            Transform current = transform;
            while (current != null)
            {
                names.Add(current.name);
                current = current.parent;
            }
            names.Reverse();
            return string.Join("/", names);
        }
    }
}
