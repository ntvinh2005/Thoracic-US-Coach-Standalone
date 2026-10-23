using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SMMARTS;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace CSSALT.ThoracicUSCoach
{
    [DefaultExecutionOrder(50)]
    public sealed partial class ThoracicUSCoachController : MonoBehaviour
    {
        [Header("Auto-wired simulator references")]
        public Transform probeRoot;
        public Transform faceCenter;
        public Transform rearCenter;
        public Transform scanningPlane;
        public Transform t7RightSeed;
        public Transform t6RightMarker;
        public Transform t8RightMarker;
        public Transform targetZone;
        public Transform displayOverlayRoot;
        public Collider skinCollider;
        public SkinIntersectionCompression skinIntersection;
        public Camera ultrasoundCamera;
        public MeshRenderer ultrasoundImageRenderer;

        [Header("Bounded search (millimetres/degrees)")]
        [Min(1f)] public float caudalHalfExtentMm = 15f;
        [Min(1f)] public float lateralHalfExtentMm = 12f;
        [Min(1f)] public float coarseStepMm = 7.5f;
        [Range(0f, 25f)] public float yawHalfExtentDeg = 10f;
        [Range(0f, 12f)] public float tiltHalfExtentDeg = 4f;
        [Range(0.1f, 3f)] public float maxPenetrationMm = 0.5f;
        [Range(8, 32)] public int labelGridSize = 16;
        [Range(1, 12)] public int savedCandidateCount = 6;
        [Range(0.4f, 0.995f)] public float acceptableRelativeToBest = 0.97f;

        [Header("Skin-following coaching path")]
        [Range(5, 41)] public int pathPointCount = 17;
        [Range(5f, 35f)] public float pathHalfExtentMm = 20f;
        [Range(0.5f, 5f)] public float actionStepMm = 1.5f;
        [Range(0.25f, 5f)] public float acceptableStoppingErrorMm = 2.5f;

        [Header("Capture / bridge")]
        public string artifactFolder = "Artifacts/ThoracicUSCoach";
        public string datasetExperimentId = DiverseTrajectorySchedule.DefaultExperimentId;
        public string datasetRunId = DiverseTrajectorySchedule.DefaultDatasetRunId;
        [Min(0)] public int diverseCaptureSequence;
        [Range(1, 120)] public int pilotSweepCount = 84;
        [Range(20f, 50f)] public float captureCaudalPatchHalfExtentMm = 34f;
        [Range(25f, 60f)] public float captureLateralPatchHalfExtentMm = 44f;
        [Min(0.05f)] public float pythonPolicyTimeoutSeconds = 3f;
        public bool showGroundTruthOverlay;
        [Tooltip("Developer-only IMGUI controls/status. Keep disabled when validating the production scene UI.")]
        public bool showDeveloperOverlay;

        [Header("Runtime status (read-only)")]
        [SerializeField] private string status = "Idle";
        [SerializeField] private int pathIndex;
        [SerializeField] private float currentReferenceScore;
        [SerializeField] private CoachAction lastRecommendation = CoachAction.Stop;
        [SerializeField] private float lastModelConfidence;
        [SerializeField] private string lastModelName = "none";
        [SerializeField] private bool busy;

        private readonly List<CandidateRecord> candidates = new List<CandidateRecord>();
        private readonly List<PathPose> path = new List<PathPose>();
        private Quaternion seedProbeRotation;
        private Vector3 seedNormal;
        private Vector3 caudalTangent;
        private Vector3 lateralTangent;
        private RenderTexture captureTexture;
        private Texture2D readbackTexture;
        private Texture2D overlayTexture;
        private string previousPolicyImage;
        private CoachAction previousPolicyAction;
        private string lastEnvironmentRequestId;
        private bool environmentBridgeBusy;
        private bool deferDatasetIndexWrite;
        private float nextEnvironmentPollTime;
        private float discoveryAcceptableScoreFloor;
        private CandidateRecord bestDiscoveredCandidate;
        private TrajectoryConfigurationRecord currentTrajectory;
        private bool regionEnteredThisEpisode;
        private int invalidMotionAttempts;
        private int currentEpisodeStep;
        private int currentEpisodeMaxSteps;
        private string lastGenerationRejection;

        [Serializable]
        private sealed class PathPose
        {
            public Vector3 position;
            public Quaternion rotation;
            public Vector3 contact;
            public Vector3 normal;
            public float pathMm;
            public float caudalOffsetMm;
            public float lateralOffsetMm;
            public float yawDeg;
            public float tiltDeg;
            public float score;
            public ScoreBreakdown scoreDetails;
            public bool acceptable;
            public bool contactValid;
            public bool workspaceValid;
        }

        private sealed class LabelSample
        {
            public int x;
            public int y;
            public LabelKind kind;
        }

        private enum LabelKind
        {
            None,
            Other,
            Pleura,
            Bone,
            Target
        }

        public string Status => status;
        public int PathIndex => pathIndex;
        public int PathCount => path.Count;
        public CoachAction LastRecommendation => lastRecommendation;

        private void Reset()
        {
            AutoWire();
        }

        private void Awake()
        {
            // Automated capture must keep advancing when the Editor is driven through MCP and is not the focused window.
            Application.runInBackground = true;
            IgnoreStaleEnvironmentRequest();
            AutoWire();
            diverseCaptureSequence = Mathf.Max(diverseCaptureSequence, ExistingDiverseSweepCount());
            InitializeGuided2D();
            InitializeActive2D();
        }

        private void IgnoreStaleEnvironmentRequest()
        {
            // Bridge files survive domain reloads and Editor restarts. A request
            // from a dead Python process must not begin a long regeneration as
            // soon as Play Mode starts or race candidate discovery.
            environmentBridgeBusy = false;
            string requestPath = Path.Combine(ArtifactRoot(), "bridge", "env_request.json");
            if (!File.Exists(requestPath)) return;
            try
            {
                EnvironmentRequest stale = JsonUtility.FromJson<EnvironmentRequest>(File.ReadAllText(requestPath));
                if (stale != null) lastEnvironmentRequestId = stale.requestId;
            }
            catch (IOException) { }
            catch (ArgumentException) { }
        }

        private void OnDestroy()
        {
            DestroyGuided2DResources();
            if (captureTexture != null) captureTexture.Release();
            if (readbackTexture != null) Destroy(readbackTexture);
            if (overlayTexture != null) Destroy(overlayTexture);
        }

        private void Update()
        {
            PollEnvironmentBridge();
            UpdateGuided2D();
            UpdateActive2D();
            if (busy) return;
            if (Input.GetKeyDown(KeyCode.D)) StartDiscovery();
            if (Input.GetKeyDown(KeyCode.C)) StartCaptureSweep();
            if (Input.GetKeyDown(KeyCode.P)) StartPolicyDemo();
            if (Input.GetKeyDown(KeyCode.UpArrow)) ApplyAction(CoachAction.Forward);
            if (Input.GetKeyDown(KeyCode.DownArrow)) ApplyAction(CoachAction.Backward);
            if (Input.GetKeyDown(KeyCode.Space)) ApplyAction(CoachAction.Stop);
            if (Input.GetKeyDown(KeyCode.G)) showGroundTruthOverlay = !showGroundTruthOverlay;
            if (Input.GetKeyDown(KeyCode.R)) ResetToPathStart();
        }

        private void OnGUI()
        {
            if (!showDeveloperOverlay) return;

            DrawGuided2DGUI();
            DrawActive2DGUI();
            GUILayout.BeginArea(new Rect(20, Mathf.Max(510, Screen.height - (showGroundTruthOverlay ? 255 : 200)), 470, showGroundTruthOverlay ? 245 : 190), GUI.skin.box);
            GUILayout.Label("Thoracic US Coach — provisional T7-right axial PoC");
            GUILayout.Label("Status: " + status);
            GUILayout.Label(string.Format(CultureInfo.InvariantCulture,
                "Model: {0}  Action: {1}  Confidence: {2:F2}",
                lastModelName, lastRecommendation, lastModelConfidence));
            GUILayout.Label(string.Format(CultureInfo.InvariantCulture,
                "Path: {0}/{1}  Reference score: {2:F3}", pathIndex, Mathf.Max(0, path.Count - 1), currentReferenceScore));
            GUILayout.Label("D discover | C capture | P model loop | ↑/↓/Space move | R reset | G GT");
            if (showGroundTruthOverlay)
            {
                GUILayout.Space(4);
                GUILayout.Label("GROUND TRUTH (separate from policy): pose/geometry is used only for discovery, labels, rewards, and constraint enforcement. The policy request contains PNG pixels, previous PNG, and previous action only.");
            }
            GUILayout.EndArea();
        }

        [ContextMenu("Auto Wire References")]
        public void AutoWire()
        {
            if (probeRoot == null)
            {
                GameObject go = GameObject.Find("/Ultrasound 1/Tracked Ultrasound Probe");
                if (go != null) probeRoot = go.transform;
            }

            if (probeRoot != null)
            {
                if (skinIntersection == null) skinIntersection = probeRoot.GetComponentInChildren<SkinIntersectionCompression>(true);
                if (skinIntersection != null)
                {
                    if (faceCenter == null) faceCenter = skinIntersection.CenterTransform;
                    if (rearCenter == null) rearCenter = skinIntersection.RearCenterTransform;
                }
                if (scanningPlane == null)
                {
                    Transform found = probeRoot.Find("Primary Scanning Plane");
                    if (found != null) scanningPlane = found;
                }
            }

            Transform markerRoot = FindTransformByPath("/IndexPlate/Anatomy/RA Anatomy Set/Desired TPVB Skin Puncture Points");
            if (markerRoot != null)
            {
                if (t7RightSeed == null) t7RightSeed = markerRoot.Find("Skin Puncture Point T7R");
                if (t6RightMarker == null) t6RightMarker = markerRoot.Find("Skin Puncture Point T6R");
                if (t8RightMarker == null) t8RightMarker = markerRoot.Find("Skin Puncture Point T8R");
            }

            if (skinCollider == null)
            {
                foreach (GameObject skin in GameObject.FindGameObjectsWithTag("Skin"))
                {
                    if (skin.name == "Skin")
                    {
                        skinCollider = skin.GetComponent<Collider>();
                        if (skinCollider != null) break;
                    }
                }
            }

            // Some standalone checkouts contain a prefab whose serialized
            // contact-provider MonoBehaviour cannot be resolved after the SDK
            // scripts moved into an assembly definition. Recreate only that
            // stateless provider at runtime from the already serialized probe,
            // face/rear markers, and exact skin collider. This does not alter
            // the scene asset or introduce a target/label input to the policy.
            if (Application.isPlaying && skinIntersection == null && probeRoot != null
                && faceCenter != null && rearCenter != null && skinCollider != null)
            {
                skinIntersection = probeRoot.gameObject.AddComponent<SkinIntersectionCompression>();
                skinIntersection.CenterTransform = faceCenter;
                skinIntersection.RearCenterTransform = rearCenter;
                skinIntersection.SkinCollider = skinCollider;
                skinIntersection.SkinLayer = 1 << skinCollider.gameObject.layer;
                skinIntersection.GridCols = 30;
                skinIntersection.GridRows = 5;
                skinIntersection.GridWidth_mm = 50f;
                skinIntersection.GridHeight_mm = 8f;
                skinIntersection.RayStartBackoff_mm = 50f;
                skinIntersection.ExtraRayLength_mm = 100f;
                skinIntersection.MinDepthThreshold_mm = 0.01f;
                Debug.LogWarning("[ThoracicUSCoach] Recreated missing runtime SkinIntersectionCompression provider.");
            }

            UltrasoundDisplayController[] displays = FindObjectsByType<UltrasoundDisplayController>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            UltrasoundDisplayController display = displays.FirstOrDefault(item =>
                item.Ultrasound_Image_Camera != null && AncestorNameContains(item.Ultrasound_Image_Camera.transform, "Primary"));
            if (display != null)
            {
                ultrasoundCamera = display.Ultrasound_Image_Camera;
                ultrasoundImageRenderer = display.RenderTexture;
            }

            if (targetZone == null && t7RightSeed != null)
            {
                Transform[] all = FindObjectsByType<Transform>(FindObjectsInactive.Include, FindObjectsSortMode.None);
                float best = float.PositiveInfinity;
                foreach (Transform item in all)
                {
                    if (!item.name.StartsWith("Intercostal Pleural Depression Zone Right", StringComparison.Ordinal)) continue;
                    float d = Vector3.SqrMagnitude(item.position - t7RightSeed.position);
                    if (d < best)
                    {
                        best = d;
                        targetZone = item;
                    }
                }
            }

            if (displayOverlayRoot == null)
                displayOverlayRoot = FindTransformByPath("/Ultrasound 1/Primary US Image Creation/Ultrasound Display Components");
        }

        [ContextMenu("Start Candidate Discovery")]
        public void StartDiscovery()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[ThoracicUSCoach] Candidate discovery runs in Play Mode.");
                return;
            }
            if (busy) return;
            StartCoroutine(DiscoverCandidates());
        }

        [ContextMenu("Capture Path Sweep")]
        public void StartCaptureSweep()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[ThoracicUSCoach] Capture runs in Play Mode.");
                return;
            }
            if (busy) return;
            StartCoroutine(CapturePathSweep());
        }

        [ContextMenu("Capture Diverse Pilot Dataset")]
        public void StartCapturePilot()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[ThoracicUSCoach] Pilot capture runs in Play Mode.");
                return;
            }
            if (busy) return;
            StartCoroutine(CapturePilotDataset());
        }

        [ContextMenu("Run Image Policy Demo")]
        public void StartPolicyDemo()
        {
            if (!Application.isPlaying)
            {
                Debug.LogWarning("[ThoracicUSCoach] Policy demo runs in Play Mode.");
                return;
            }
            if (busy) return;
            StartCoroutine(PolicyDemoLoop());
        }

        public void ApplyAction(CoachAction action)
        {
            lastRecommendation = action;
            if (path.Count == 0) return;
            int stepCount = ActionStepCount();
            if (action == CoachAction.Forward) pathIndex = Mathf.Min(path.Count - 1, pathIndex + stepCount);
            else if (action == CoachAction.Backward) pathIndex = Mathf.Max(0, pathIndex - stepCount);
            ApplyPathPose(pathIndex);
        }

        public void ResetToPathStart()
        {
            if (path.Count == 0) return;
            pathIndex = 0;
            ApplyPathPose(pathIndex);
            status = "Reset to bounded path start";
        }

        private IEnumerator DiscoverCandidates()
        {
            busy = true;
            status = "Validating scene references";
            AutoWire();
            if (!ValidateReferences(out string error))
            {
                status = "Discovery failed: " + error;
                Debug.LogError("[ThoracicUSCoach] " + status);
                busy = false;
                yield break;
            }

            candidates.Clear();
            path.Clear();
            if (!FindSurfaceAt(t7RightSeed.position, Vector3.zero, out RaycastHit seedHit))
            {
                status = "Discovery failed: no skin surface near T7R marker";
                Debug.LogError("[ThoracicUSCoach] " + status);
                busy = false;
                yield break;
            }

            seedNormal = seedHit.normal.normalized;
            caudalTangent = DetermineCaudalTangent(seedNormal);
            lateralTangent = Vector3.Cross(seedNormal, caudalTangent).normalized;
            if (lateralTangent.sqrMagnitude < 0.5f) lateralTangent = Vector3.Cross(seedNormal, Vector3.right).normalized;

            AlignProbeToSurface(seedHit, 0f, 0f);
            seedProbeRotation = probeRoot.rotation;
            yield return null;

            status = "Coarse candidate search";
            List<CandidateRecord> coarse = new List<CandidateRecord>();
            int coarseIndex = 0;
            for (float caudal = -caudalHalfExtentMm; caudal <= caudalHalfExtentMm + 0.01f; caudal += coarseStepMm)
            {
                for (float lateral = -lateralHalfExtentMm; lateral <= lateralHalfExtentMm + 0.01f; lateral += coarseStepMm)
                {
                    for (float yaw = -yawHalfExtentDeg; yaw <= yawHalfExtentDeg + 0.01f; yaw += Mathf.Max(1f, yawHalfExtentDeg))
                    {
                        CandidateRecord record = null;
                        yield return EvaluateCandidate("coarse_" + coarseIndex.ToString("D4"), caudal, lateral, yaw, 0f, value => record = value);
                        coarseIndex++;
                        if (record != null) coarse.Add(record);
                    }
                }
            }

            coarse.Sort((a, b) => b.score.total.CompareTo(a.score.total));
            List<CandidateRecord> seeds = coarse.Take(Mathf.Min(4, coarse.Count)).ToList();
            candidates.AddRange(coarse);

            status = "Local refinement";
            int refineIndex = 0;
            float refineStep = coarseStepMm * 0.5f;
            foreach (CandidateRecord seed in seeds)
            {
                for (int dc = -1; dc <= 1; dc++)
                for (int dl = -1; dl <= 1; dl++)
                for (int dt = -1; dt <= 1; dt++)
                {
                    float caudal = seed.pose.caudalOffsetMm + dc * refineStep;
                    float lateral = seed.pose.lateralOffsetMm + dl * refineStep;
                    float tilt = dt * tiltHalfExtentDeg;
                    if (Mathf.Abs(caudal) > caudalHalfExtentMm || Mathf.Abs(lateral) > lateralHalfExtentMm) continue;
                    CandidateRecord record = null;
                    yield return EvaluateCandidate("refine_" + refineIndex.ToString("D4"), caudal, lateral, seed.pose.yawDeg, tilt, value => record = value);
                    refineIndex++;
                    if (record != null) candidates.Add(record);
                }
            }

            candidates.Sort((a, b) => b.score.total.CompareTo(a.score.total));
            if (candidates.Count == 0)
            {
                status = "Discovery found no contact-valid candidate";
                busy = false;
                yield break;
            }

            float floor = candidates[0].score.total * acceptableRelativeToBest;
            // Once an experiment has committed its first sweep, its target
            // definition is immutable.  This prevents a resume after a domain
            // reload from shifting every later label because of render jitter.
            if (TryReadExperimentReferenceFloor(out float lockedFloor)) floor = lockedFloor;
            discoveryAcceptableScoreFloor = floor;
            bestDiscoveredCandidate = candidates[0];
            foreach (CandidateRecord candidate in candidates) candidate.acceptable = candidate.score.total >= floor;
            yield return SaveTopCandidates(floor);
            yield return BuildPath(candidates[0]);
            pathIndex = 0;
            ApplyPathPose(pathIndex);
            status = string.Format(CultureInfo.InvariantCulture,
                "Discovery complete: {0} valid, best {1:F3}, acceptable ≥ {2:F3}", candidates.Count, candidates[0].score.total, floor);
            Debug.Log("[ThoracicUSCoach] " + status);
            busy = false;
        }

        private IEnumerator EvaluateCandidate(string id, float caudal, float lateral, float yaw, float tilt, Action<CandidateRecord> done)
        {
            Vector3 proposed = t7RightSeed.position + caudalTangent * caudal + lateralTangent * lateral;
            if (!FindSurfaceAt(proposed, seedNormal, out RaycastHit hit))
            {
                done(null);
                yield break;
            }

            if (Vector3.Distance(hit.point, t7RightSeed.position) > Mathf.Sqrt(caudalHalfExtentMm * caudalHalfExtentMm + lateralHalfExtentMm * lateralHalfExtentMm) + 6f)
            {
                done(null);
                yield break;
            }

            AlignProbeToSurface(hit, yaw, tilt);
            yield return null;
            if (!EnforceRigidContact())
            {
                done(null);
                yield break;
            }
            yield return null;

            Texture2D image = CaptureCleanImage();
            if (image == null)
            {
                done(null);
                yield break;
            }

            List<LabelSample> labels = SampleAnatomyLabels(labelGridSize);
            ScoreBreakdown score = ScoreCandidate(image, labels);
            CandidateRecord result = new CandidateRecord
            {
                candidateId = id,
                pose = CurrentPose(hit.point, hit.normal, caudal, lateral, yaw, tilt),
                score = score
            };
            done(result);
        }

        private IEnumerator SaveTopCandidates(float floor)
        {
            string root = ArtifactRoot();
            string candidateDir = Path.Combine(root, "candidates");
            Directory.CreateDirectory(candidateDir);
            int count = Mathf.Min(savedCandidateCount, candidates.Count);
            CandidateManifest manifest = new CandidateManifest
            {
                generatedUtc = DateTime.UtcNow.ToString("O"),
                scene = gameObject.scene.path,
                curriculumBasis = "UF thoracic curriculum slides 5-10 and 16-20; axial/transverse T7-right starting hypothesis",
                acceptableScoreFloor = floor
            };

            for (int i = 0; i < count; i++)
            {
                CandidateRecord candidate = candidates[i];
                ApplyRecordedPose(candidate.pose);
                yield return null;
                Texture2D image = CaptureCleanImage();
                List<LabelSample> labels = SampleAnatomyLabels(labelGridSize);
                Texture2D overlay = BuildOverlay(labels, labelGridSize);
                string stem = i.ToString("D2") + "_" + SafeName(candidate.candidateId);
                string imagePath = Path.Combine(candidateDir, stem + "_us.png");
                string overlayPath = Path.Combine(candidateDir, stem + "_labels.png");
                File.WriteAllBytes(imagePath, image.EncodeToPNG());
                File.WriteAllBytes(overlayPath, overlay.EncodeToPNG());
                candidate.cleanImage = RelativeToProject(imagePath);
                candidate.anatomyOverlay = RelativeToProject(overlayPath);
                manifest.candidates.Add(candidate);
            }

            File.WriteAllText(Path.Combine(candidateDir, "candidate_manifest.json"), JsonUtility.ToJson(manifest, true));
        }

        private IEnumerator BuildPath(CandidateRecord best)
        {
            path.Clear();
            // Cross the provisional view laterally so the short path contains off-target images on both sides.
            // This is essential for a genuine Forward/Backward/Stop task when the discovery optimum lies on
            // the caudal edge of its clinically motivated 5-15 mm search window.
            Vector3 bestCenter = t7RightSeed.position
                + caudalTangent * best.pose.caudalOffsetMm
                + lateralTangent * best.pose.lateralOffsetMm;
            for (int i = 0; i < pathPointCount; i++)
            {
                float t = pathPointCount == 1 ? 0.5f : i / (float)(pathPointCount - 1);
                float pathOffset = Mathf.Lerp(-pathHalfExtentMm, pathHalfExtentMm, t);
                Vector3 proposed = bestCenter + lateralTangent * pathOffset;
                if (!FindSurfaceAt(proposed, seedNormal, out RaycastHit hit)) continue;
                AlignProbeToSurface(hit, best.pose.yawDeg, 0f);
                yield return null;
                EnforceRigidContact();
                yield return null;
                Texture2D image = CaptureCleanImage();
                ScoreBreakdown score = ScoreCandidate(image, SampleAnatomyLabels(labelGridSize));
                path.Add(new PathPose
                {
                    position = probeRoot.position,
                    rotation = probeRoot.rotation,
                    contact = faceCenter.position,
                    normal = hit.normal,
                    pathMm = pathOffset,
                    caudalOffsetMm = best.pose.caudalOffsetMm,
                    lateralOffsetMm = best.pose.lateralOffsetMm + pathOffset,
                    yawDeg = best.pose.yawDeg,
                    tiltDeg = 0f,
                    score = score.total,
                    scoreDetails = score,
                    contactValid = IsCurrentContactValid(),
                    workspaceValid = true
                });
            }

            path.Sort((a, b) => a.pathMm.CompareTo(b.pathMm));
            float floor = path.Count > 0 ? path.Max(item => item.score) * acceptableRelativeToBest : 1f;
            foreach (PathPose pose in path) pose.acceptable = pose.score >= floor;
            currentTrajectory = new TrajectoryConfigurationRecord
            {
                experimentId = datasetExperimentId,
                datasetRunId = datasetRunId,
                splitScheduleVersion = DiverseTrajectorySchedule.SplitScheduleVersion,
                configurationFamily = "deterministic-discovery-path",
                evaluationGroup = "development",
                sampleCount = path.Count,
                pathLengthMm = path.Count > 1 ? path[path.Count - 1].pathMm - path[0].pathMm : 0f,
                pathDirection = 1,
                referenceScoreFloor = floor,
                targetReachable = path.Any(item => item.acceptable)
            };
        }

        private IEnumerator CapturePathSweep()
        {
            busy = true;
            if (bestDiscoveredCandidate == null || candidates.Count == 0)
            {
                status = "Capture needs discovery first (press D)";
                busy = false;
                yield break;
            }

            bool captured = false;
            yield return GenerateAndCaptureSequence(diverseCaptureSequence, value => captured = value);
            if (captured) diverseCaptureSequence++;
            busy = false;
        }

        private IEnumerator CapturePilotDataset()
        {
            busy = true;
            if (bestDiscoveredCandidate == null || candidates.Count == 0)
            {
                status = "Pilot capture needs discovery first (press D)";
                busy = false;
                yield break;
            }

            int target = Mathf.Max(pilotSweepCount, diverseCaptureSequence);
            deferDatasetIndexWrite = true;
            while (diverseCaptureSequence < target)
            {
                bool captured = false;
                yield return GenerateAndCaptureSequence(diverseCaptureSequence, value => captured = value);
                if (!captured)
                {
                    deferDatasetIndexWrite = false;
                    WriteDatasetIndex();
                    status = "Pilot stopped: could not generate a valid trajectory for sequence " + diverseCaptureSequence;
                    busy = false;
                    yield break;
                }
                diverseCaptureSequence++;
            }
            deferDatasetIndexWrite = false;
            WriteDatasetIndex();
            status = "Diverse capture complete: " + diverseCaptureSequence + " configurations for "
                + datasetExperimentId + "/" + datasetRunId;
            busy = false;
        }

        private IEnumerator GenerateAndCaptureSequence(int sequence, Action<bool> done)
        {
            const int maximumAttempts = 32;
            List<string> rejectionLog = new List<string>();
            for (int attempt = 0; attempt < maximumAttempts; attempt++)
            {
                TrajectoryConfigurationRecord configuration = CreateTrajectoryConfiguration(sequence, attempt);
                lastGenerationRejection = string.Empty;
                bool pathValid = false;
                yield return BuildVariedPath(configuration, value => pathValid = value);
                if (!pathValid)
                {
                    rejectionLog.Add("attempt " + attempt + " seed " + configuration.randomSeed + ": "
                        + (string.IsNullOrEmpty(lastGenerationRejection) ? "path validation failed" : lastGenerationRejection));
                    continue;
                }

                configuration.generationAttempt = attempt;
                configuration.generationRejections = new List<string>(rejectionLog);
                bool captured = false;
                yield return CaptureVariedPathSweep(configuration, value => captured = value);
                if (captured)
                {
                    done(true);
                    yield break;
                }
                rejectionLog.Add("attempt " + attempt + " seed " + configuration.randomSeed + ": " + status);
            }
            lastGenerationRejection = string.Join(" | ", rejectionLog.ToArray());
            status = "Generation rejected sequence " + sequence + " after " + maximumAttempts
                + " attempts: " + lastGenerationRejection;
            done(false);
        }

        private TrajectoryConfigurationRecord CreateTrajectoryConfiguration(int sequence, int attempt, int forcedSeed = 0)
        {
            int seed = forcedSeed != 0 ? forcedSeed : DiverseTrajectorySchedule.SeedForSequence(sequence, attempt);
            System.Random random = new System.Random(seed);
            string split = DiverseTrajectorySchedule.SplitForSequence(sequence);
            string evaluationGroup = DiverseTrajectorySchedule.EvaluationGroupForSequence(sequence);
            bool stress = evaluationGroup == "stress";
            float caudalJitter = RandomRange(random, stress ? -7f : -5f, stress ? 7f : 5f);
            float lateralJitter = RandomRange(random, stress ? -8f : -5f, stress ? 8f : 5f);
            if (stress && sequence % 3 == 0) caudalJitter -= 18f;
            float angle = RandomRange(random, stress ? -25f : -15f, stress ? 25f : 15f);
            float length = RandomRange(random, stress ? 42f : 30f, stress ? 52f : 46f);
            int sampleCount = 13 + 2 * random.Next(0, 5);
            float yawCenter = RandomRange(random, stress ? -9f : -6f, stress ? 9f : 6f);
            float tiltCenter = RandomRange(random, stress ? -4f : -2.5f, stress ? 4f : 2.5f);
            string[] startSides = { "before", "after", "inside", "near-boundary", "far" };
            return new TrajectoryConfigurationRecord
            {
                experimentId = datasetExperimentId,
                datasetRunId = datasetRunId,
                splitScheduleVersion = DiverseTrajectorySchedule.SplitScheduleVersion,
                split = split,
                configurationFamily = DiverseTrajectorySchedule.FamilyForSequence(sequence)
                    + (forcedSeed != 0 ? "_seed_" + seed : string.Empty),
                evaluationGroup = evaluationGroup,
                randomSeed = seed,
                sampleCount = sampleCount,
                centerCaudalOffsetMm = bestDiscoveredCandidate.pose.caudalOffsetMm + caudalJitter,
                centerLateralOffsetMm = bestDiscoveredCandidate.pose.lateralOffsetMm + lateralJitter,
                pathLengthMm = length,
                pathAngleDeg = angle,
                pathDirection = random.Next(0, 2) == 0 ? -1 : 1,
                yawStartDeg = yawCenter + RandomRange(random, -2f, 2f),
                yawEndDeg = yawCenter + RandomRange(random, -2f, 2f),
                tiltStartDeg = tiltCenter + RandomRange(random, -1.5f, 1.5f),
                tiltEndDeg = tiltCenter + RandomRange(random, -1.5f, 1.5f),
                requestedStartSide = startSides[random.Next(0, startSides.Length)],
                referenceScoreFloor = discoveryAcceptableScoreFloor
            };
        }

        private IEnumerator BuildVariedPath(TrajectoryConfigurationRecord configuration, Action<bool> done)
        {
            path.Clear();
            float radians = configuration.pathAngleDeg * Mathf.Deg2Rad;
            Vector3 direction = (lateralTangent * Mathf.Cos(radians) + caudalTangent * Mathf.Sin(radians)).normalized;
            direction *= configuration.pathDirection;
            Vector3 center = t7RightSeed.position
                + caudalTangent * configuration.centerCaudalOffsetMm
                + lateralTangent * configuration.centerLateralOffsetMm;
            Vector3 previousContact = Vector3.zero;

            for (int i = 0; i < configuration.sampleCount; i++)
            {
                float progress = configuration.sampleCount == 1 ? 0.5f : i / (float)(configuration.sampleCount - 1);
                float along = Mathf.Lerp(-configuration.pathLengthMm * 0.5f, configuration.pathLengthMm * 0.5f, progress);
                Vector3 proposed = center + direction * along;
                Vector3 relative = proposed - t7RightSeed.position;
                float caudal = Vector3.Dot(relative, caudalTangent);
                float lateral = Vector3.Dot(relative, lateralTangent);
                if (Mathf.Abs(caudal) > captureCaudalPatchHalfExtentMm || Mathf.Abs(lateral) > captureLateralPatchHalfExtentMm)
                {
                    path.Clear();
                    lastGenerationRejection = "path left declared capture patch";
                    done(false);
                    yield break;
                }
                if (!FindSurfaceAt(proposed, seedNormal, out RaycastHit hit))
                {
                    path.Clear();
                    lastGenerationRejection = "skin surface projection failed";
                    done(false);
                    yield break;
                }

                float yaw = Mathf.Lerp(configuration.yawStartDeg, configuration.yawEndDeg, progress);
                float tilt = Mathf.Lerp(configuration.tiltStartDeg, configuration.tiltEndDeg, progress);
                AlignProbeToSurface(hit, yaw, tilt);
                yield return null;
                if (!EnforceRigidContact())
                {
                    path.Clear();
                    lastGenerationRejection = "rigid contact constraint failed";
                    done(false);
                    yield break;
                }
                yield return null;
                if (i > 0 && Vector3.Distance(previousContact, faceCenter.position) > 8f)
                {
                    path.Clear();
                    lastGenerationRejection = "adjacent projected contacts jumped more than 8 mm";
                    done(false);
                    yield break;
                }
                previousContact = faceCenter.position;
                Texture2D image = CaptureCleanImage();
                if (image == null)
                {
                    path.Clear();
                    lastGenerationRejection = "ultrasound source image unavailable";
                    done(false);
                    yield break;
                }
                ScoreBreakdown score = ScoreCandidate(image, SampleAnatomyLabels(labelGridSize));
                path.Add(new PathPose
                {
                    position = probeRoot.position,
                    rotation = probeRoot.rotation,
                    contact = faceCenter.position,
                    normal = hit.normal,
                    // Canonical corridor coordinate: Forward always increases this
                    // value, regardless of the order used to generate the poses.
                    pathMm = configuration.pathDirection * along,
                    caudalOffsetMm = caudal,
                    lateralOffsetMm = lateral,
                    yawDeg = yaw,
                    tiltDeg = tilt,
                    score = score.total,
                    scoreDetails = score,
                    contactValid = IsCurrentContactValid(),
                    workspaceValid = true
                });
            }

            path.Sort((left, right) => left.pathMm.CompareTo(right.pathMm));

            List<int> acceptable = Enumerable.Range(0, path.Count)
                .Where(index => path[index].score >= configuration.referenceScoreFloor).ToList();
            foreach (int index in acceptable) path[index].acceptable = true;
            configuration.targetReachable = acceptable.Count > 0;
            if (!configuration.targetReachable && configuration.evaluationGroup != "stress")
            {
                path.Clear();
                lastGenerationRejection = "reachable group contained no acceptable target pose";
                done(false);
                yield break;
            }
            SetTrajectoryStartAndClass(configuration, acceptable);
            currentTrajectory = configuration;
            pathIndex = Mathf.Clamp(configuration.defaultStartIndex, 0, Mathf.Max(0, path.Count - 1));
            if (path.Count > 0) ApplyPathPose(pathIndex);
            done(true);
        }

        private void SetTrajectoryStartAndClass(TrajectoryConfigurationRecord configuration, List<int> acceptable)
        {
            if (acceptable.Count == 0)
            {
                configuration.defaultStartIndex = 0;
                configuration.reachabilityClass = "target-absent";
                return;
            }
            switch (configuration.requestedStartSide)
            {
                case "after": configuration.defaultStartIndex = path.Count - 1; break;
                case "inside": configuration.defaultStartIndex = acceptable[acceptable.Count / 2]; break;
                case "near-boundary":
                    configuration.defaultStartIndex = NearestUnacceptableNeighbor(acceptable);
                    break;
                case "far":
                    configuration.defaultStartIndex = Enumerable.Range(0, path.Count)
                        .OrderByDescending(index => acceptable.Min(target => Mathf.Abs(target - index))).First();
                    break;
                default: configuration.defaultStartIndex = 0; break;
            }
            configuration.reachabilityClass = acceptable.Contains(configuration.defaultStartIndex)
                ? "initially-acceptable"
                : "reachable-target";
        }

        private int NearestUnacceptableNeighbor(List<int> acceptable)
        {
            HashSet<int> exact = new HashSet<int>(acceptable);
            foreach (int target in acceptable)
            {
                if (target > 0 && !exact.Contains(target - 1)) return target - 1;
                if (target + 1 < path.Count && !exact.Contains(target + 1)) return target + 1;
            }
            return acceptable[0];
        }

        private IEnumerator CaptureVariedPathSweep(TrajectoryConfigurationRecord configuration, Action<bool> done)
        {
            string sweepId = "T7R_CV3_" + SafeName(datasetRunId) + "_"
                + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff") + "_S" + configuration.randomSeed;
            string split = configuration.split;
            string finalDir = Path.Combine(ArtifactRoot(), "dataset", split, sweepId);
            string partialDir = finalDir + ".partial";
            Directory.CreateDirectory(partialDir);
            CaptureManifest manifest = new CaptureManifest
            {
                generatedUtc = DateTime.UtcNow.ToString("O"),
                scene = gameObject.scene.path,
                experimentId = datasetExperimentId,
                datasetRunId = datasetRunId,
                splitScheduleVersion = DiverseTrajectorySchedule.SplitScheduleVersion,
                sweepId = sweepId,
                split = split,
                trajectory = configuration,
                configurationSha256 = ConfigurationHash(configuration),
                labelsSha256 = LabelsHash()
            };

            for (int i = 0; i < path.Count; i++)
            {
                status = "Capturing diverse " + split + " sweep " + (diverseCaptureSequence + 1) + "/" + pilotSweepCount
                    + " frame " + (i + 1) + "/" + path.Count;
                pathIndex = i;
                ApplyPathPose(i);
                yield return null;
                Texture2D image = CaptureCleanImage();
                if (image == null || IsLikelyMagentaArtifact(image))
                {
                    if (Directory.Exists(partialDir)) Directory.Delete(partialDir, true);
                    status = "Capture rejected: ultrasound source was unavailable or magenta";
                    done(false);
                    yield break;
                }
                List<LabelSample> labels = SampleAnatomyLabels(labelGridSize);
                Texture2D overlay = BuildOverlay(labels, labelGridSize);
                ScoreBreakdown observedScore = ScoreCandidate(image, labels);
                string frameId = "frame_" + i.ToString("D4");
                string imageName = frameId + "_us.png";
                string overlayName = frameId + "_labels.png";
                byte[] imageBytes = image.EncodeToPNG();
                File.WriteAllBytes(Path.Combine(partialDir, imageName), imageBytes);
                File.WriteAllBytes(Path.Combine(partialDir, overlayName), overlay.EncodeToPNG());
                PathPose pose = path[i];
                bool contactValid = skinIntersection != null && skinIntersection.AnyHit
                    && Mathf.Abs(skinIntersection.SignedDepth) <= maxPenetrationMm;
                manifest.frames.Add(new CaptureRecord
                {
                    frameId = frameId,
                    sweepId = sweepId,
                    split = split,
                    stepIndex = i,
                    image = RelativeToProject(Path.Combine(finalDir, imageName)),
                    imageSha256 = Sha256Bytes(imageBytes),
                    anatomyOverlay = RelativeToProject(Path.Combine(finalDir, overlayName)),
                    pose = CurrentPose(pose.contact, pose.normal, pose.caudalOffsetMm, pose.lateralOffsetMm, pose.yawDeg, pose.tiltDeg),
                    // Fixed-pose score is the only target definition used by
                    // labels, reward, distance, and success. observedScore is
                    // diagnostic only and never changes the label.
                    score = pose.scoreDetails,
                    observedScore = observedScore,
                    targetAcceptable = pose.acceptable,
                    teacherActionDefined = path.Any(item => item.acceptable),
                    contactValid = contactValid,
                    workspaceValid = IsCurrentWorkspaceValid(),
                    pathMm = pose.pathMm,
                    pathProgress = path.Count == 1 ? 0f : i / (float)(path.Count - 1)
                });
            }

            int uniqueFrameCount = manifest.frames.Select(frame => frame.imageSha256).Distinct().Count();
            int requiredUniqueFrames = Mathf.Max(2, Mathf.CeilToInt(manifest.frames.Count * 0.8f));
            if (uniqueFrameCount < requiredUniqueFrames)
            {
                if (Directory.Exists(partialDir)) Directory.Delete(partialDir, true);
                status = "Capture rejected: only " + uniqueFrameCount + "/" + manifest.frames.Count
                    + " unique ultrasound frames (required " + requiredUniqueFrames + ")";
                done(false);
                yield break;
            }

            List<int> acceptable = Enumerable.Range(0, path.Count).Where(index => path[index].acceptable).ToList();
            for (int i = 0; i < manifest.frames.Count; i++)
            {
                CaptureRecord frame = manifest.frames[i];
                frame.teacherActionDefined = acceptable.Count > 0;
                if (!frame.teacherActionDefined) frame.teacherAction = CoachAction.Stop;
                else frame.teacherAction = OneDEnvironmentContract.OracleAction(
                    path.Select(item => item.acceptable).ToList(), i, ActionStepCount());
            }

            yield return CaptureGenuineTransitionEpisode(partialDir, finalDir, manifest, acceptable);

            string manifestPath = Path.Combine(partialDir, "sweep_manifest.json");
            File.WriteAllText(manifestPath, JsonUtility.ToJson(manifest, true));
            // Antivirus/indexing on Windows can briefly retain a just-written PNG or
            // manifest.  Keep the transaction unregistered and retry the atomic
            // directory commit instead of leaking a half sweep into the index.
            bool finalized = false;
            string finalizeError = string.Empty;
            for (int attempt = 0; attempt < 8 && !finalized; attempt++)
            {
                try
                {
                    Directory.Move(partialDir, finalDir);
                    finalized = true;
                }
                catch (IOException exception)
                {
                    finalizeError = exception.Message;
                }
                catch (UnauthorizedAccessException exception)
                {
                    finalizeError = exception.Message;
                }

                if (!finalized) yield return new WaitForSecondsRealtime(0.25f * (attempt + 1));
            }
            if (!finalized)
            {
                status = "Capture commit failed; retained .partial for diagnosis: " + finalizeError;
                done(false);
                yield break;
            }
            if (!deferDatasetIndexWrite) WriteDatasetIndex();
            currentTrajectory = configuration;
            pathIndex = Mathf.Clamp(configuration.defaultStartIndex, 0, path.Count - 1);
            ApplyPathPose(pathIndex);
            status = "Captured diverse " + split + " sweep: " + sweepId;
            done(true);
        }

        private IEnumerator CaptureGenuineTransitionEpisode(
            string partialDir,
            string finalDir,
            CaptureManifest manifest,
            List<int> acceptable)
        {
            // Every history row below is captured around the action that was
            // actually applied. The short diagnostic prefix guarantees reset,
            // reverse movement, and boundary clamping are represented.
            pathIndex = 0;
            ApplyPathPose(pathIndex);
            yield return CaptureTransition(partialDir, finalDir, manifest, CoachAction.Backward, "boundary-clamp", false, false);

            pathIndex = Mathf.Clamp(manifest.trajectory.defaultStartIndex, 0, path.Count - 1);
            ApplyPathPose(pathIndex);
            yield return CaptureTransition(partialDir, finalDir, manifest, CoachAction.Stop, "reset", false, false);

            CoachAction probe = pathIndex < path.Count - 1 ? CoachAction.Forward : CoachAction.Backward;
            yield return CaptureTransition(partialDir, finalDir, manifest, probe, "active-probe", false, false);
            CoachAction reverse = probe == CoachAction.Forward ? CoachAction.Backward : CoachAction.Forward;
            yield return CaptureTransition(partialDir, finalDir, manifest, reverse, "reverse", false, false);

            List<bool> mask = path.Select(item => item.acceptable).ToList();
            int budget = Mathf.Max(1, path.Count * 2);
            for (int step = 0; step < budget; step++)
            {
                CoachAction action = OneDEnvironmentContract.OracleAction(mask, pathIndex, ActionStepCount());
                if (acceptable.Count == 0) action = CoachAction.Forward;
                bool terminal = action == CoachAction.Stop;
                int next = OneDEnvironmentContract.StepIndex(pathIndex, action, ActionStepCount(), path.Count);
                bool truncated = !terminal && (step + 1 >= budget || next == pathIndex);
                yield return CaptureTransition(partialDir, finalDir, manifest, action, "oracle-rollout", terminal, truncated);
                if (terminal || truncated) break;
            }
        }

        private IEnumerator CaptureTransition(
            string partialDir,
            string finalDir,
            CaptureManifest manifest,
            CoachAction action,
            string kind,
            bool terminated,
            bool truncated)
        {
            int ordinal = manifest.transitions.Count;
            string id = "transition_" + ordinal.ToString("D4");
            int previousIndex = pathIndex;
            yield return null;
            Texture2D previousTexture = CaptureCleanImage();
            byte[] previousBytes = previousTexture.EncodeToPNG();
            string previousName = id + "_previous.png";
            File.WriteAllBytes(Path.Combine(partialDir, previousName), previousBytes);

            if (!string.Equals(kind, "reset", StringComparison.Ordinal)) ApplyAction(action);
            yield return null;
            Texture2D currentTexture = CaptureCleanImage();
            byte[] currentBytes = currentTexture.EncodeToPNG();
            string currentName = id + "_current.png";
            File.WriteAllBytes(Path.Combine(partialDir, currentName), currentBytes);

            List<int> acceptable = Enumerable.Range(0, path.Count).Where(index => path[index].acceptable).ToList();
            bool teacherDefined = acceptable.Count > 0;
            CoachAction teacher = teacherDefined
                ? OneDEnvironmentContract.OracleAction(
                    path.Select(item => item.acceptable).ToList(), pathIndex, ActionStepCount())
                : CoachAction.Stop;
            manifest.transitions.Add(new TransitionRecord
            {
                transitionId = id,
                transitionKind = kind,
                previousImage = RelativeToProject(Path.Combine(finalDir, previousName)),
                previousImageSha256 = Sha256Bytes(previousBytes),
                previousIndex = previousIndex,
                executedAction = action,
                actionWasExecuted = !string.Equals(kind, "reset", StringComparison.Ordinal),
                currentImage = RelativeToProject(Path.Combine(finalDir, currentName)),
                currentImageSha256 = Sha256Bytes(currentBytes),
                currentIndex = pathIndex,
                teacherAction = teacher,
                teacherActionDefined = teacherDefined,
                targetAcceptable = path[pathIndex].acceptable,
                contactValid = IsCurrentContactValid(),
                workspaceValid = IsCurrentWorkspaceValid(),
                boundaryClamped = action != CoachAction.Stop && previousIndex == pathIndex,
                terminated = terminated,
                truncated = truncated
            });
        }

        private IEnumerator PolicyDemoLoop()
        {
            busy = true;
            if (path.Count == 0)
            {
                status = "Policy demo needs discovery first (press D)";
                busy = false;
                yield break;
            }

            string bridge = Path.Combine(ArtifactRoot(), "bridge");
            Directory.CreateDirectory(bridge);
            previousPolicyImage = null;
            previousPolicyAction = CoachAction.Stop;
            int maxSteps = path.Count * 2;
            for (int step = 0; step < maxSteps; step++)
            {
                status = "Image policy step " + (step + 1) + "/" + maxSteps;
                yield return null;
                Texture2D image = CaptureCleanImage();
                string requestId = DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);
                string imagePath = Path.Combine(bridge, "frame_" + requestId + ".png");
                File.WriteAllBytes(imagePath, image.EncodeToPNG());
                string requestPath = Path.Combine(bridge, "policy_request.json");
                string responsePath = Path.Combine(bridge, "policy_response.json");
                if (File.Exists(responsePath)) File.Delete(responsePath);
                PolicyRequest request = new PolicyRequest
                {
                    requestId = requestId,
                    imagePath = imagePath,
                    previousImagePath = previousPolicyImage,
                    previousAction = (int)previousPolicyAction
                };
                File.WriteAllText(requestPath, JsonUtility.ToJson(request, true));

                float started = Time.realtimeSinceStartup;
                PolicyResponse response = null;
                while (Time.realtimeSinceStartup - started < pythonPolicyTimeoutSeconds)
                {
                    if (File.Exists(responsePath))
                    {
                        try
                        {
                            PolicyResponse candidate = JsonUtility.FromJson<PolicyResponse>(File.ReadAllText(responsePath));
                            if (candidate != null && candidate.requestId == requestId) response = candidate;
                        }
                        catch (IOException) { }
                        if (response != null) break;
                    }
                    yield return null;
                }

                if (response == null)
                {
                    status = "Policy bridge timeout. Start Tools/ThoracicUSCoach/run_policy_server.py";
                    lastModelName = "timeout";
                    break;
                }

                lastRecommendation = (CoachAction)Mathf.Clamp(response.action, -1, 1);
                lastModelConfidence = response.confidence;
                lastModelName = string.IsNullOrEmpty(response.model) ? "python-image-policy" : response.model;
                ApplyAction(lastRecommendation);
                previousPolicyImage = imagePath;
                previousPolicyAction = lastRecommendation;
                if (lastRecommendation == CoachAction.Stop)
                {
                    status = "Policy stopped; GT overlay remains optional/separate";
                    break;
                }
                yield return new WaitForSeconds(0.1f);
            }
            if (status.StartsWith("Image policy step", StringComparison.Ordinal))
                status = "Policy demo reached the step limit without an explicit STOP";
            busy = false;
        }

        private void PollEnvironmentBridge()
        {
            // Regeneration requests must remain serviceable after a rejected
            // constrained trajectory has cleared the candidate path.
            if (!Application.isPlaying || busy || environmentBridgeBusy || Time.realtimeSinceStartup < nextEnvironmentPollTime) return;
            nextEnvironmentPollTime = Time.realtimeSinceStartup + 0.03f;
            string bridge = Path.Combine(ArtifactRoot(), "bridge");
            string requestPath = Path.Combine(bridge, "env_request.json");
            if (!File.Exists(requestPath)) return;
            try
            {
                EnvironmentRequest request = JsonUtility.FromJson<EnvironmentRequest>(File.ReadAllText(requestPath));
                if (request == null || string.IsNullOrEmpty(request.requestId) || request.requestId == lastEnvironmentRequestId) return;
                lastEnvironmentRequestId = request.requestId;
                StartCoroutine(HandleEnvironmentRequest(request, bridge));
            }
            catch (IOException) { }
            catch (ArgumentException error)
            {
                Debug.LogWarning("[ThoracicUSCoach] Invalid environment request: " + error.Message);
            }
        }

        private IEnumerator HandleEnvironmentRequest(EnvironmentRequest request, string bridge)
        {
            environmentBridgeBusy = true;
            if (string.Equals(request.command, "reset", StringComparison.OrdinalIgnoreCase)
                && request.replayFrozenScenario)
            {
                if (!TryLoadFrozenScenario(request.frozenScenario, request.maxSteps, out string replayError))
                {
                    EnvironmentResponse failed = new EnvironmentResponse
                    {
                        requestId = request.requestId,
                        done = true,
                        error = "Frozen scenario replay rejected: " + replayError
                    };
                    WriteEnvironmentResponse(bridge, failed);
                    environmentBridgeBusy = false;
                    yield break;
                }
            }
            else if (string.Equals(request.command, "reset", StringComparison.OrdinalIgnoreCase) && request.regenerateTrajectory)
            {
                bool generated = false;
                int attempts = request.trajectorySeed == 0 ? 8 : 1;
                for (int attempt = 0; attempt < attempts && !generated; attempt++)
                {
                    TrajectoryConfigurationRecord configuration = CreateTrajectoryConfiguration(
                        Mathf.Max(0, request.scenarioIndex), attempt, request.trajectorySeed);
                    yield return BuildVariedPath(configuration, value => generated = value);
                }
                if (!generated)
                {
                    EnvironmentResponse failed = new EnvironmentResponse
                    {
                        requestId = request.requestId,
                        done = true,
                        error = "Could not generate requested constrained trajectory"
                    };
                    WriteEnvironmentResponse(bridge, failed);
                    environmentBridgeBusy = false;
                    yield break;
                }
            }
            if (path.Count == 0)
            {
                EnvironmentResponse failed = new EnvironmentResponse
                {
                    requestId = request.requestId,
                    done = true,
                    error = "No valid path is available; send reset with regenerateTrajectory=true"
                };
                WriteEnvironmentResponse(bridge, failed);
                environmentBridgeBusy = false;
                yield break;
            }
            List<bool> acceptableMask = path.Select(item => item.acceptable).ToList();
            List<float> pathCoordinates = path.Select(item => item.pathMm).ToList();
            bool terminated = false;
            bool truncated = false;
            bool explicitStopSuccess = false;
            float reward = 0f;
            string error = string.Empty;
            bool invalidMotion = false;
            bool patchBoundaryViolation = false;

            if (string.Equals(request.command, "reset", StringComparison.OrdinalIgnoreCase))
            {
                pathIndex = Mathf.Clamp(request.startIndex, 0, path.Count - 1);
                ApplyPathPose(pathIndex);
                regionEnteredThisEpisode = OneDEnvironmentContract.IsAcceptable(acceptableMask, pathIndex);
                invalidMotionAttempts = 0;
                currentEpisodeStep = 0;
                currentEpisodeMaxSteps = request.maxSteps > 0 ? request.maxSteps : Mathf.Max(1, path.Count * 2);
            }
            else if (string.Equals(request.command, "step", StringComparison.OrdinalIgnoreCase))
            {
                CoachAction action = (CoachAction)Mathf.Clamp(request.action, -1, 1);
                int previousIndex = pathIndex;
                ApplyAction(action);
                bool contactValid = IsCurrentContactValid();
                bool workspaceValid = IsCurrentWorkspaceValid();
                OneDStepResult stepResult = OneDEnvironmentContract.Step(
                    previousIndex, action, ActionStepCount(), acceptableMask, pathCoordinates,
                    contactValid, workspaceValid, currentEpisodeStep, currentEpisodeMaxSteps);
                pathIndex = stepResult.pathIndex;
                currentEpisodeStep++;
                reward = stepResult.reward;
                terminated = stepResult.terminated;
                truncated = stepResult.truncated;
                explicitStopSuccess = stepResult.explicitStopSuccess;
                regionEnteredThisEpisode |= stepResult.targetAcceptable;
                invalidMotion = stepResult.invalidMotion;
                patchBoundaryViolation = stepResult.patchBoundaryViolation;
                if (invalidMotion)
                {
                    invalidMotionAttempts++;
                }
            }
            else if (string.Equals(request.command, "describe", StringComparison.OrdinalIgnoreCase))
            {
                // Privileged evaluation metadata only. It is never part of a
                // policy request or observation feature.
            }
            else
            {
                error = "command must be reset, step, or describe";
                terminated = true;
            }

            yield return null;
            Texture2D image = CaptureCleanImage();
            string observation = Path.Combine(bridge, "env_observation_" + SafeName(request.requestId) + ".png");
            File.WriteAllBytes(observation, image.EncodeToPNG());
            bool contact = IsCurrentContactValid();
            bool workspace = IsCurrentWorkspaceValid();
            bool violation = !contact || !workspace;
            ScoreBreakdown observed = ScoreCandidate(image, SampleAnatomyLabels(labelGridSize));
            float floor = CurrentReferenceFloor();
            float absentDistance = path.Count > 1
                ? Mathf.Abs(path[path.Count - 1].pathMm - path[0].pathMm) + PathSpacingMm()
                : PathSpacingMm();
            EnvironmentResponse response = new EnvironmentResponse
            {
                requestId = request.requestId,
                imagePath = observation,
                reward = reward,
                done = terminated || truncated,
                terminated = terminated,
                truncated = truncated,
                targetAcceptable = path[pathIndex].acceptable,
                regionEntered = regionEnteredThisEpisode,
                explicitStopSuccess = explicitStopSuccess,
                constraintViolation = violation,
                contactValid = contact,
                workspaceValid = workspace,
                patchBoundaryViolation = patchBoundaryViolation,
                invalidMotion = invalidMotion,
                referenceScore = path[pathIndex].score,
                observedScore = observed.total,
                currentImageAcceptable = observed.total >= floor,
                distanceToAcceptableMm = OneDEnvironmentContract.DistanceToAcceptableMm(
                    acceptableMask, pathCoordinates, pathIndex, absentDistance),
                pathSpacingMm = PathSpacingMm(),
                pathIndex = pathIndex,
                pathCount = path.Count,
                configurationFamily = currentTrajectory != null ? currentTrajectory.configurationFamily : "deterministic-smoke",
                configurationSha256 = currentTrajectory != null ? ConfigurationHash(currentTrajectory) : string.Empty,
                labelsSha256 = LabelsHash(),
                targetSemantics = OneDEnvironmentContract.TargetSemantics,
                scenario = string.Equals(request.command, "describe", StringComparison.OrdinalIgnoreCase)
                    ? BuildScenarioDescription() : null,
                error = error
            };
            WriteEnvironmentResponse(bridge, response);
            environmentBridgeBusy = false;
        }

        private void WriteEnvironmentResponse(string bridge, EnvironmentResponse response)
        {
            string destination = Path.Combine(bridge, "env_response.json");
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string json = JsonUtility.ToJson(response, true);
            string lastError = string.Empty;
            try
            {
                File.WriteAllText(temporary, json);
                for (int attempt = 0; attempt < 12; attempt++)
                {
                    try
                    {
                        if (File.Exists(destination)) File.Replace(temporary, destination, null);
                        else File.Move(temporary, destination);
                        return;
                    }
                    catch (IOException error)
                    {
                        lastError = error.Message;
                    }
                    catch (UnauthorizedAccessException error)
                    {
                        lastError = error.Message;
                    }
                    System.Threading.Thread.Sleep(5 * (attempt + 1));
                }
            }
            catch (IOException error)
            {
                lastError = error.Message;
            }
            catch (UnauthorizedAccessException error)
            {
                lastError = error.Message;
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            status = "Environment response write failed after retries: " + lastError;
            Debug.LogError("[ThoracicUSCoach] " + status);
        }

        private bool TryLoadFrozenScenario(FrozenScenarioDescription frozen, int requestedMaxSteps, out string error)
        {
            error = string.Empty;
            if (frozen == null || frozen.trajectory == null) { error = "scenario/trajectory missing"; return false; }
            if (frozen.environmentContractVersion != OneDEnvironmentContract.Version
                || frozen.observationSchema != OneDEnvironmentContract.ObservationSchema
                || frozen.targetSemantics != OneDEnvironmentContract.TargetSemantics)
            { error = "contract identity mismatch"; return false; }
            if (frozen.actionOrder == null || !frozen.actionOrder.SequenceEqual(new[] { -1, 0, 1 }))
            { error = "action schema mismatch"; return false; }
            if (frozen.poses == null || frozen.pathMm == null || frozen.referenceScores == null
                || frozen.poses.Count == 0 || frozen.poses.Count != frozen.pathMm.Count
                || frozen.poses.Count != frozen.referenceScores.Count)
            { error = "pose/path/reference-score lengths mismatch"; return false; }
            if (frozen.acceptableIndices == null || frozen.acceptableIndices.Any(index => index < 0 || index >= frozen.poses.Count))
            { error = "acceptable index out of range"; return false; }
            if (frozen.scene != gameObject.scene.path || frozen.unityVersion != Application.unityVersion
                || frozen.colorSpace != QualitySettings.activeColorSpace.ToString())
            { error = "scene/render environment mismatch"; return false; }
            if (frozen.renderWidth != CaptureSourceWidth() || frozen.renderHeight != CaptureSourceHeight())
            { error = "render dimensions mismatch"; return false; }
            if (frozen.actionStepCount != ActionStepCount()
                || !Mathf.Approximately(frozen.actionStepMm, actionStepMm)
                || !Mathf.Approximately(frozen.maxPenetrationMm, maxPenetrationMm)
                || !Mathf.Approximately(frozen.acceptableRelativeToBest, acceptableRelativeToBest)
                || !Mathf.Approximately(frozen.acceptableStoppingErrorMm, acceptableStoppingErrorMm)
                || !Mathf.Approximately(frozen.captureCaudalPatchHalfExtentMm, captureCaudalPatchHalfExtentMm)
                || !Mathf.Approximately(frozen.captureLateralPatchHalfExtentMm, captureLateralPatchHalfExtentMm))
            { error = "dynamics/tolerance mismatch"; return false; }
            if (requestedMaxSteps > 0 && frozen.episodeBudget > 0 && requestedMaxSteps != frozen.episodeBudget)
            { error = "episode budget mismatch"; return false; }
            if (ConfigurationHash(frozen.trajectory) != frozen.configurationSha256)
            { error = "trajectory configuration hash mismatch"; return false; }

            HashSet<int> acceptable = new HashSet<int>(frozen.acceptableIndices);
            path.Clear();
            for (int index = 0; index < frozen.poses.Count; index++)
            {
                ProbePoseRecord pose = frozen.poses[index];
                float score = frozen.referenceScores[index];
                if (float.IsNaN(score) || float.IsInfinity(score))
                { error = "non-finite reference score"; path.Clear(); return false; }
                path.Add(new PathPose
                {
                    position = pose.position,
                    rotation = pose.rotation,
                    contact = pose.contactPoint,
                    normal = pose.surfaceNormal,
                    pathMm = frozen.pathMm[index],
                    caudalOffsetMm = pose.caudalOffsetMm,
                    lateralOffsetMm = pose.lateralOffsetMm,
                    yawDeg = pose.yawDeg,
                    tiltDeg = pose.tiltDeg,
                    score = score,
                    scoreDetails = new ScoreBreakdown { total = score, notes = "Frozen reference score replay" },
                    acceptable = acceptable.Contains(index),
                    contactValid = true,
                    workspaceValid = true
                });
            }
            currentTrajectory = frozen.trajectory;
            discoveryAcceptableScoreFloor = frozen.referenceScoreFloor;
            if (LabelsHash() != frozen.labelsSha256)
            {
                error = "exact label/reference-score hash mismatch";
                path.Clear();
                currentTrajectory = null;
                return false;
            }
            pathIndex = 0;
            ApplyPathPose(pathIndex);
            return true;
        }

        private ScoreBreakdown ScoreCandidate(Texture2D image, List<LabelSample> labels)
        {
            int total = Mathf.Max(1, labels.Count);
            int pleura = labels.Count(v => v.kind == LabelKind.Pleura);
            int bone = labels.Count(v => v.kind == LabelKind.Bone);
            int target = labels.Count(v => v.kind == LabelKind.Target);
            int labeled = labels.Count(v => v.kind != LabelKind.None);
            float pleuraBrightness = MeanBrightness(image, labels, LabelKind.Pleura);

            Color32[] pixels = image.GetPixels32();
            int width = image.width;
            int height = image.height;
            float nonBlack = 0f;
            float lowerBlack = 0f;
            float gradient = 0f;
            int lowerCount = 0;
            for (int y = 0; y < height; y += 2)
            for (int x = 0; x < width; x += 2)
            {
                float lum = Luminance(pixels[y * width + x]);
                if (lum > 0.03f) nonBlack += 1f;
                if (y < height / 2)
                {
                    lowerCount++;
                    if (lum < 0.025f) lowerBlack += 1f;
                }
                if (x + 2 < width)
                    gradient += Mathf.Abs(lum - Luminance(pixels[y * width + x + 2]));
            }
            float sampled = Mathf.Ceil(width / 2f) * Mathf.Ceil(height / 2f);
            nonBlack /= Mathf.Max(1f, sampled);
            float shadowFraction = lowerBlack / Mathf.Max(1, lowerCount);
            gradient = Mathf.Clamp01(gradient / Mathf.Max(1f, sampled) * 8f);

            float pleuraFraction = pleura / (float)total;
            float boneFraction = bone / (float)total;
            float anatomy = Mathf.Clamp01(Mathf.Min(pleuraFraction / 0.025f, 1f) * 0.55f + Mathf.Min(boneFraction / 0.05f, 1f) * 0.45f);
            if (pleura == 0 || bone == 0) anatomy *= 0.25f;
            float pleuraScore = Mathf.Clamp01(pleuraFraction / 0.04f) * Mathf.Clamp01(pleuraBrightness * 2.5f);

            TargetProjection(out float targetPlaneDistance, out float targetBoundsPenalty, out float targetLateralPosition);
            float targetScore = Mathf.Exp(-targetPlaneDistance / 6f) * Mathf.Exp(-targetBoundsPenalty / 0.20f);
            if (target > 0) targetScore = Mathf.Max(targetScore, Mathf.Clamp01(target / (float)total * 16f));

            float acoustic = Mathf.Clamp01(nonBlack * 1.3f) * Mathf.Lerp(0.35f, 1f, gradient) * (1f - Mathf.Clamp01((shadowFraction - 0.45f) / 0.45f));
            float lateralEntryMargin = 1f - Mathf.Clamp01(Mathf.Abs(targetLateralPosition - 0.65f) / 0.65f);
            float needle = Mathf.Clamp01(targetScore * lateralEntryMargin);
            float shadowPenalty = Mathf.Clamp01((shadowFraction - 0.55f) / 0.35f);
            float contactPenalty = skinIntersection == null ? 1f :
                (!skinIntersection.AnyHit ? 1f : Mathf.Clamp01(Mathf.Max(0f, skinIntersection.SignedDepth - maxPenetrationMm) / 2f));

            float totalScore = 0.25f * anatomy + 0.23f * pleuraScore + 0.20f * targetScore + 0.17f * acoustic + 0.15f * needle;
            totalScore -= 0.18f * shadowPenalty + 0.5f * contactPenalty;
            return new ScoreBreakdown
            {
                anatomyRelationship = anatomy,
                pleuraVisibility = pleuraScore,
                targetRegion = targetScore,
                acousticWindow = acoustic,
                needleGeometry = needle,
                shadowPenalty = shadowPenalty,
                contactPenalty = contactPenalty,
                total = Mathf.Clamp01(totalScore),
                pleuraSamples = pleura,
                boneSamples = bone,
                targetSamples = target,
                labeledSamples = labeled,
                gridSamples = total,
                notes = "Rule-based provisional score: rendered pixels + programmatic image-to-mesh labels + target/scan-plane geometry. Not a clinician approval."
            };
        }

        private List<LabelSample> SampleAnatomyLabels(int grid)
        {
            List<LabelSample> result = new List<LabelSample>(grid * grid);
            if (!UltrasoundManager.TryGet(UltrasoundManager.Plane.Primary, out UltrasoundManager manager)) return result;
            for (int gy = 0; gy < grid; gy++)
            for (int gx = 0; gx < grid; gx++)
            {
                float px = (gx + 0.5f) * manager.Resolution / grid;
                float py = (gy + 0.5f) * manager.Resolution / grid;
                Vector3 world = manager.ScreenPointToWorldCoordinate(new Vector2(px, py));
                GameObject found = IdentifyVisibleObjectAtPoint(manager, world);
                result.Add(new LabelSample { x = gx, y = gy, kind = Categorize(found) });
            }
            return result;
        }

        private static GameObject IdentifyVisibleObjectAtPoint(UltrasoundManager manager, Vector3 point)
        {
            if (manager == null || manager.CenterOfProbeRear == null || manager.ProbeIsFarFromSkin) return null;
            Vector3 start = manager.CenterOfProbeRear.transform.position;
            Vector3 delta = point - start;
            float distance = delta.magnitude;
            if (distance < 0.001f) return null;
            RaycastHit[] forwardHits = Physics.RaycastAll(start, delta, distance, manager.ImageableLayers);
            RaycastHit[] backwardHits = Physics.RaycastAll(point, -delta, distance, manager.ImageableLayers);
            Array.Sort(forwardHits, (a, b) => a.distance.CompareTo(b.distance));
            List<GameObject> forward = new List<GameObject>();
            foreach (RaycastHit hit in forwardHits)
            {
                UltrasoundVisibility visibility = hit.transform.GetComponent<UltrasoundVisibility>();
                if (visibility != null && visibility.ShowOnUltrasound) forward.Add(hit.transform.gameObject);
            }
            foreach (RaycastHit hit in backwardHits)
            {
                UltrasoundVisibility visibility = hit.transform.GetComponent<UltrasoundVisibility>();
                if (visibility == null || !visibility.ShowOnUltrasound) continue;
                for (int i = forward.Count - 1; i >= 0; i--)
                {
                    if (forward[i].GetInstanceID() == hit.transform.gameObject.GetInstanceID())
                    {
                        forward.RemoveAt(i);
                        break;
                    }
                }
            }
            return forward.Count == 0 ? null : forward[forward.Count - 1];
        }

        private static LabelKind Categorize(GameObject go)
        {
            if (go == null) return LabelKind.None;
            UltrasoundVisibility visibility = go.GetComponent<UltrasoundVisibility>();
            string message = visibility != null ? visibility.TouchDisplayMessage : string.Empty;
            string key = (go.name + " " + message).ToLowerInvariant();
            if (key.Contains("paravertebral") || key.Contains("intercostal pleural depression") || key.Contains("target zone")) return LabelKind.Target;
            if (key.Contains("pleura") || (visibility != null && visibility.Type == UltrasoundVisibility.RenderType.Lung)) return LabelKind.Pleura;
            if (key.Contains("rib") || key.Contains("transverse") || key.Contains("lamina") || key.Contains("vertebra") || key.Contains("bone") ||
                (visibility != null && visibility.Type == UltrasoundVisibility.RenderType.Shadow)) return LabelKind.Bone;
            return LabelKind.Other;
        }

        private Texture2D CaptureCleanImage()
        {
            // Match the production simulator capture path.  The renderer's
            // runtime mainTexture is an internal 256x256 intermediate and is
            // not equivalent to the complete 1024x1024 ultrasound image
            // rendered by the primary ultrasound camera.
            if (ultrasoundCamera == null) return null;
            int width = ultrasoundCamera.targetTexture != null ? ultrasoundCamera.targetTexture.width : 256;
            int height = ultrasoundCamera.targetTexture != null ? ultrasoundCamera.targetTexture.height : 256;
            EnsureCaptureTextures(width, height);
            RenderTexture previousTarget = ultrasoundCamera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;
            bool overlayWasActive = displayOverlayRoot != null && displayOverlayRoot.gameObject.activeSelf;
            if (displayOverlayRoot != null) displayOverlayRoot.gameObject.SetActive(false);
            ultrasoundCamera.targetTexture = captureTexture;
            ultrasoundCamera.Render();
            RenderTexture.active = captureTexture;
            readbackTexture.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            readbackTexture.Apply(false, false);
            ultrasoundCamera.targetTexture = previousTarget;
            RenderTexture.active = previousActive;
            if (displayOverlayRoot != null) displayOverlayRoot.gameObject.SetActive(overlayWasActive);
            return readbackTexture;
        }

        private static bool IsLikelyMagentaArtifact(Texture2D image)
        {
            if (image == null) return true;
            Color32[] pixels = image.GetPixels32();
            if (pixels.Length == 0) return true;
            int magenta = 0;
            int stride = Mathf.Max(1, pixels.Length / 4096);
            int sampled = 0;
            for (int i = 0; i < pixels.Length; i += stride)
            {
                Color32 pixel = pixels[i];
                if (pixel.r > 120 && pixel.b > 120 && pixel.g + 45 < Mathf.Min(pixel.r, pixel.b)) magenta++;
                sampled++;
            }
            return sampled > 0 && magenta >= Mathf.CeilToInt(sampled * 0.35f);
        }

        private Texture2D BuildOverlay(List<LabelSample> labels, int grid)
        {
            int size = readbackTexture != null ? readbackTexture.width : 256;
            if (overlayTexture == null || overlayTexture.width != size || overlayTexture.height != size)
            {
                if (overlayTexture != null) Destroy(overlayTexture);
                overlayTexture = new Texture2D(size, size, TextureFormat.RGBA32, false, true)
                {
                    filterMode = FilterMode.Point
                };
            }
            Color32[] colors = Enumerable.Repeat(new Color32(0, 0, 0, 0), size * size).ToArray();
            foreach (LabelSample sample in labels)
            {
                Color32 color;
                switch (sample.kind)
                {
                    case LabelKind.Pleura: color = new Color32(0, 220, 255, 255); break;
                    case LabelKind.Bone: color = new Color32(255, 180, 0, 255); break;
                    case LabelKind.Target: color = new Color32(255, 0, 180, 255); break;
                    case LabelKind.Other: color = new Color32(90, 90, 90, 255); break;
                    default: color = new Color32(0, 0, 0, 0); break;
                }
                int x0 = sample.x * size / grid;
                int x1 = (sample.x + 1) * size / grid;
                int y0 = sample.y * size / grid;
                int y1 = (sample.y + 1) * size / grid;
                for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                    colors[y * size + x] = color;
            }
            overlayTexture.SetPixels32(colors);
            overlayTexture.Apply(false, false);
            return overlayTexture;
        }

        private void EnsureCaptureTextures(int width, int height)
        {
            if (captureTexture == null || captureTexture.width != width || captureTexture.height != height)
            {
                if (captureTexture != null) captureTexture.Release();
                captureTexture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                captureTexture.Create();
            }
            if (readbackTexture == null || readbackTexture.width != width || readbackTexture.height != height)
            {
                if (readbackTexture != null) Destroy(readbackTexture);
                readbackTexture = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            }
        }

        private bool FindSurfaceAt(Vector3 proposed, Vector3 expectedNormal, out RaycastHit bestHit)
        {
            bestHit = default;
            if (skinCollider == null) return false;
            float bestDistance = float.PositiveInfinity;
            if (expectedNormal.sqrMagnitude > 0.5f)
            {
                TrySurfaceRay(proposed, expectedNormal.normalized, ref bestHit, ref bestDistance);
                TrySurfaceRay(proposed, -expectedNormal.normalized, ref bestHit, ref bestDistance);
            }
            else
            {
                Vector3[] directions =
                {
                    Vector3.up, Vector3.down, Vector3.left, Vector3.right, Vector3.forward, Vector3.back,
                    new Vector3(1,1,1).normalized, new Vector3(-1,1,1).normalized,
                    new Vector3(1,-1,1).normalized, new Vector3(1,1,-1).normalized,
                    new Vector3(-1,-1,1).normalized, new Vector3(-1,1,-1).normalized,
                    new Vector3(1,-1,-1).normalized, new Vector3(-1,-1,-1).normalized
                };
                foreach (Vector3 direction in directions) TrySurfaceRay(proposed, direction, ref bestHit, ref bestDistance);
            }
            return bestDistance < float.PositiveInfinity;
        }

        private void TrySurfaceRay(Vector3 point, Vector3 outward, ref RaycastHit bestHit, ref float bestDistance)
        {
            const float reach = 120f;
            Ray ray = new Ray(point + outward * reach, -outward);
            if (!skinCollider.Raycast(ray, out RaycastHit hit, reach * 2f)) return;
            float distance = Vector3.Distance(point, hit.point);
            if (distance >= bestDistance) return;
            bestDistance = distance;
            bestHit = hit;
        }

        private void AlignProbeToSurface(RaycastHit hit, float yawDeg, float tiltDeg)
        {
            probeRoot.rotation = seedProbeRotation == default ? probeRoot.rotation : seedProbeRotation;
            Vector3 outward = (rearCenter.position - faceCenter.position).normalized;
            probeRoot.rotation = Quaternion.FromToRotation(outward, hit.normal) * probeRoot.rotation;
            Vector3 localCaudal = Vector3.ProjectOnPlane(caudalTangent, hit.normal).normalized;
            probeRoot.RotateAround(faceCenter.position, hit.normal, yawDeg);
            if (localCaudal.sqrMagnitude > 0.5f) probeRoot.RotateAround(faceCenter.position, localCaudal, tiltDeg);
            probeRoot.position += hit.point - faceCenter.position;
        }

        private bool EnforceRigidContact()
        {
            if (skinIntersection == null) return Vector3.Distance(faceCenter.position, skinCollider.ClosestPoint(faceCenter.position)) <= 1f;
            skinIntersection.Measure();
            if (!skinIntersection.AnyHit) return false;
            Vector3 outward = skinIntersection.CompressionNormal.normalized;
            probeRoot.position += outward * skinIntersection.SignedDepth;
            skinIntersection.Measure();
            return skinIntersection.AnyHit && skinIntersection.SignedDepth <= maxPenetrationMm && skinIntersection.SignedDepth >= -maxPenetrationMm;
        }

        private Vector3 DetermineCaudalTangent(Vector3 normal)
        {
            Vector3 axis = t6RightMarker != null && t8RightMarker != null
                ? t8RightMarker.position - t6RightMarker.position
                : Vector3.down;
            axis = Vector3.ProjectOnPlane(axis, normal).normalized;
            return axis.sqrMagnitude > 0.5f ? axis : Vector3.ProjectOnPlane(Vector3.down, normal).normalized;
        }

        private ProbePoseRecord CurrentPose(Vector3 contact, Vector3 normal, float caudal, float lateral, float yaw, float tilt)
        {
            return new ProbePoseRecord
            {
                position = probeRoot.position,
                rotation = probeRoot.rotation,
                contactPoint = contact,
                surfaceNormal = normal,
                caudalOffsetMm = caudal,
                lateralOffsetMm = lateral,
                yawDeg = yaw,
                tiltDeg = tilt,
                signedDepthMm = skinIntersection != null ? skinIntersection.SignedDepth : 0f,
                contactFraction = skinIntersection != null ? skinIntersection.ContactPercent : 0f
            };
        }

        private void ApplyRecordedPose(ProbePoseRecord pose)
        {
            probeRoot.SetPositionAndRotation(pose.position, pose.rotation);
            EnforceRigidContact();
        }

        private void ApplyPathPose(int index)
        {
            if (index < 0 || index >= path.Count) return;
            PathPose pose = path[index];
            probeRoot.SetPositionAndRotation(pose.position, pose.rotation);
            EnforceRigidContact();
            currentReferenceScore = pose.score;
        }

        private int ActionStepCount()
        {
            return Mathf.Max(1, Mathf.RoundToInt(actionStepMm / Mathf.Max(0.01f, PathSpacingMm())));
        }

        private bool IsCurrentContactValid()
        {
            return skinIntersection != null && skinIntersection.AnyHit
                && Mathf.Abs(skinIntersection.SignedDepth) <= maxPenetrationMm;
        }

        private bool IsCurrentWorkspaceValid()
        {
            if (pathIndex < 0 || pathIndex >= path.Count) return false;
            PathPose pose = path[pathIndex];
            return pose.workspaceValid
                && Mathf.Abs(pose.caudalOffsetMm) <= captureCaudalPatchHalfExtentMm
                && Mathf.Abs(pose.lateralOffsetMm) <= captureLateralPatchHalfExtentMm;
        }

        private float CurrentReferenceFloor()
        {
            if (currentTrajectory != null && currentTrajectory.referenceScoreFloor > 0f)
                return currentTrajectory.referenceScoreFloor;
            return path.Count > 0 ? path.Max(item => item.score) * acceptableRelativeToBest : 1f;
        }

        private int BestPathIndex()
        {
            int best = 0;
            for (int i = 1; i < path.Count; i++) if (path[i].score > path[best].score) best = i;
            return best;
        }

        private float PathSpacingMm()
        {
            if (path.Count < 2) return actionStepMm;
            return Mathf.Max(0.01f, Mathf.Abs(path[path.Count - 1].pathMm - path[0].pathMm) / (path.Count - 1));
        }

        private static float MeanBrightness(Texture2D image, List<LabelSample> labels, LabelKind kind)
        {
            List<LabelSample> chosen = labels.Where(v => v.kind == kind).ToList();
            if (chosen.Count == 0) return 0f;
            Color32[] pixels = image.GetPixels32();
            float sum = 0f;
            int grid = Mathf.RoundToInt(Mathf.Sqrt(labels.Count));
            foreach (LabelSample sample in chosen)
            {
                int x = Mathf.Clamp(Mathf.RoundToInt((sample.x + 0.5f) * image.width / grid), 0, image.width - 1);
                int y = Mathf.Clamp(Mathf.RoundToInt((sample.y + 0.5f) * image.height / grid), 0, image.height - 1);
                sum += Luminance(pixels[y * image.width + x]);
            }
            return sum / chosen.Count;
        }

        private static float Luminance(Color32 c)
        {
            return (0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b) / 255f;
        }

        private void TargetProjection(out float planeDistance, out float boundsPenalty, out float lateralPosition)
        {
            planeDistance = 30f;
            boundsPenalty = 1f;
            lateralPosition = 0.5f;
            if (targetZone == null || !UltrasoundManager.TryGet(UltrasoundManager.Plane.Primary, out UltrasoundManager manager)) return;

            int resolution = manager.Resolution;
            Vector3 origin = manager.ScreenPointToWorldCoordinate(Vector2.zero);
            Vector3 widthEnd = manager.ScreenPointToWorldCoordinate(new Vector2(resolution, 0f));
            Vector3 depthEnd = manager.ScreenPointToWorldCoordinate(new Vector2(0f, resolution));
            Vector3 widthVector = widthEnd - origin;
            Vector3 depthVector = depthEnd - origin;
            Vector3 normal = Vector3.Cross(widthVector, depthVector).normalized;
            if (normal.sqrMagnitude < 0.5f) return;

            Vector3 relative = targetZone.position - origin;
            planeDistance = Mathf.Abs(Vector3.Dot(relative, normal));
            float widthLengthSq = Mathf.Max(0.001f, widthVector.sqrMagnitude);
            float depthLengthSq = Mathf.Max(0.001f, depthVector.sqrMagnitude);
            lateralPosition = Vector3.Dot(relative, widthVector) / widthLengthSq;
            float depthPosition = Vector3.Dot(relative, depthVector) / depthLengthSq;
            float outsideWidth = lateralPosition < 0f ? -lateralPosition : lateralPosition > 1f ? lateralPosition - 1f : 0f;
            float outsideDepth = depthPosition < 0f ? -depthPosition : depthPosition > 1f ? depthPosition - 1f : 0f;
            boundsPenalty = outsideWidth + outsideDepth;
        }

        private FrozenScenarioDescription BuildScenarioDescription()
        {
            FrozenScenarioDescription description = new FrozenScenarioDescription
            {
                configurationSha256 = currentTrajectory != null ? ConfigurationHash(currentTrajectory) : string.Empty,
                labelsSha256 = LabelsHash(),
                trajectorySeed = currentTrajectory != null ? currentTrajectory.randomSeed : 0,
                trajectory = currentTrajectory,
                unityVersion = Application.unityVersion,
                scene = gameObject.scene.path,
                renderWidth = CaptureSourceWidth(),
                renderHeight = CaptureSourceHeight(),
                colorSpace = QualitySettings.activeColorSpace.ToString(),
                // The simulator renderer has random draws that are not exposed
                // through a single seed. Fixed pose labels are therefore frozen
                // separately from current-image diagnostic scores.
                rendererFullySeeded = false,
                actionStepCount = ActionStepCount(),
                actionStepMm = actionStepMm,
                pathSpacingMm = PathSpacingMm(),
                episodeBudget = currentEpisodeMaxSteps,
                maxPenetrationMm = maxPenetrationMm,
                acceptableRelativeToBest = acceptableRelativeToBest,
                acceptableStoppingErrorMm = acceptableStoppingErrorMm,
                captureCaudalPatchHalfExtentMm = captureCaudalPatchHalfExtentMm,
                captureLateralPatchHalfExtentMm = captureLateralPatchHalfExtentMm,
                referenceScoreFloor = CurrentReferenceFloor()
            };
            for (int index = 0; index < path.Count; index++)
            {
                PathPose pose = path[index];
                description.pathMm.Add(pose.pathMm);
                description.referenceScores.Add(pose.score);
                if (pose.acceptable) description.acceptableIndices.Add(index);
                description.poses.Add(new ProbePoseRecord
                {
                    position = pose.position,
                    rotation = pose.rotation,
                    contactPoint = pose.contact,
                    surfaceNormal = pose.normal,
                    caudalOffsetMm = pose.caudalOffsetMm,
                    lateralOffsetMm = pose.lateralOffsetMm,
                    yawDeg = pose.yawDeg,
                    tiltDeg = pose.tiltDeg
                });
            }
            if (path.Count > 0)
            {
                description.canonicalStarts.Add(0);
                description.canonicalStarts.Add(path.Count - 1);
            }
            if (description.acceptableIndices.Count > 0)
            {
                description.canonicalStarts.Add(description.acceptableIndices[description.acceptableIndices.Count / 2]);
                description.canonicalStarts.Add(NearestUnacceptableNeighbor(description.acceptableIndices));
                description.canonicalStarts.Add(Enumerable.Range(0, path.Count)
                    .OrderByDescending(index => description.acceptableIndices.Min(target => Mathf.Abs(target - index))).First());
            }
            description.canonicalStarts = description.canonicalStarts.Distinct().ToList();
            return description;
        }

        private int CaptureSourceWidth()
        {
            Texture source = ultrasoundImageRenderer != null ? ultrasoundImageRenderer.material.mainTexture : null;
            if (source != null) return source.width;
            return ultrasoundCamera != null && ultrasoundCamera.targetTexture != null
                ? ultrasoundCamera.targetTexture.width : 256;
        }

        private int CaptureSourceHeight()
        {
            Texture source = ultrasoundImageRenderer != null ? ultrasoundImageRenderer.material.mainTexture : null;
            if (source != null) return source.height;
            return ultrasoundCamera != null && ultrasoundCamera.targetTexture != null
                ? ultrasoundCamera.targetTexture.height : 256;
        }

        private string ConfigurationHash(TrajectoryConfigurationRecord configuration)
        {
            return configuration == null ? string.Empty : Sha256Text(JsonUtility.ToJson(configuration, false));
        }

        private string LabelsHash()
        {
            StringBuilder builder = new StringBuilder(OneDEnvironmentContract.Version)
                .Append('|').Append(OneDEnvironmentContract.TargetSemantics)
                .Append('|').Append(CurrentReferenceFloor().ToString("R", CultureInfo.InvariantCulture));
            for (int index = 0; index < path.Count; index++)
            {
                builder.Append('|').Append(index).Append(':')
                    .Append(path[index].acceptable ? '1' : '0').Append(':')
                    .Append(path[index].score.ToString("R", CultureInfo.InvariantCulture));
            }
            return Sha256Text(builder.ToString());
        }

        private static string Sha256Text(string value)
        {
            return Sha256Bytes(Encoding.UTF8.GetBytes(value ?? string.Empty));
        }

        private static string Sha256Bytes(byte[] value)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(value)).Replace("-", string.Empty).ToLowerInvariant();
        }

        private bool ValidateReferences(out string error)
        {
            if (probeRoot == null || faceCenter == null || rearCenter == null) { error = "probe/face/rear marker missing"; return false; }
            if (skinCollider == null) { error = "main Skin collider missing"; return false; }
            if (t7RightSeed == null) { error = "Desired TPVB T7R marker missing"; return false; }
            if (ultrasoundCamera == null) { error = "primary ultrasound image camera missing"; return false; }
            if (ultrasoundImageRenderer == null) { error = "primary ultrasound image renderer missing"; return false; }
            if (skinIntersection == null) { error = "SkinIntersectionCompression missing"; return false; }
            if (scanningPlane == null) { error = "primary scanning plane missing"; return false; }
            error = null;
            return true;
        }

        private string ArtifactRoot()
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string path = Path.Combine(projectRoot, artifactFolder.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(path);
            return path;
        }

        private void WriteDatasetIndex()
        {
            string dataset = Path.Combine(ArtifactRoot(), "dataset");
            string indexPath = Path.Combine(dataset, "dataset_index.json");
            List<string> previousEntries = new List<string>();
            if (File.Exists(indexPath))
            {
                try
                {
                    DatasetIndex previous = JsonUtility.FromJson<DatasetIndex>(File.ReadAllText(indexPath));
                    if (previous != null && previous.sweepManifests != null) previousEntries = previous.sweepManifests;
                }
                catch (ArgumentException) { }
            }

            List<string> manifests = new List<string>();
            List<CaptureManifest> parsed = new List<CaptureManifest>();
            if (Directory.Exists(dataset))
            {
                foreach (string manifestPath in Directory.GetFiles(dataset, "sweep_manifest.json", SearchOption.AllDirectories))
                {
                    if (manifestPath.IndexOf(".partial", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    CaptureManifest manifest;
                    try { manifest = JsonUtility.FromJson<CaptureManifest>(File.ReadAllText(manifestPath)); }
                    catch (ArgumentException) { continue; }
                    if (manifest == null || manifest.frames == null || manifest.frames.Count == 0) continue;
                    bool complete = manifest.frames.All(frame =>
                        !string.IsNullOrEmpty(frame.image) && !string.IsNullOrEmpty(frame.anatomyOverlay)
                        && File.Exists(ProjectAbsolutePath(frame.image)) && File.Exists(ProjectAbsolutePath(frame.anatomyOverlay)));
                    if (!complete) continue;
                    manifests.Add(RelativeToProject(manifestPath));
                    parsed.Add(manifest);
                }
            }
            manifests.Sort(StringComparer.Ordinal);
            DatasetIndex index = new DatasetIndex
            {
                generatedUtc = DateTime.UtcNow.ToString("O"),
                sweepManifests = manifests,
                excludedMissingOrIncomplete = previousEntries.Except(manifests).OrderBy(value => value).ToList()
            };
            foreach (IGrouping<string, CaptureManifest> group in parsed.GroupBy(manifest =>
                string.IsNullOrEmpty(manifest.experimentId) ? "legacy_fixed_path_smoke" : manifest.experimentId))
            {
                DatasetExperimentIndex summary = new DatasetExperimentIndex
                {
                    experimentId = group.Key,
                    sweepCount = group.Count(),
                    frameCount = group.Sum(item => item.frames.Count),
                    trainSweeps = group.Count(item => ManifestSplit(item) == "train"),
                    devSweeps = group.Count(item => ManifestSplit(item) == "dev"),
                    testSweeps = group.Count(item => ManifestSplit(item) == "test")
                };
                index.experiments.Add(summary);
            }
            string temporary = indexPath + ".tmp";
            File.WriteAllText(temporary, JsonUtility.ToJson(index, true));
            string replaceError = string.Empty;
            for (int attempt = 0; attempt < 8; attempt++)
            {
                try
                {
                    if (File.Exists(indexPath)) File.Replace(temporary, indexPath, null, true);
                    else File.Move(temporary, indexPath);
                    replaceError = string.Empty;
                    break;
                }
                catch (IOException exception)
                {
                    replaceError = exception.Message;
                    System.Threading.Thread.Sleep(50 * (attempt + 1));
                }
                catch (UnauthorizedAccessException exception)
                {
                    replaceError = exception.Message;
                    System.Threading.Thread.Sleep(50 * (attempt + 1));
                }
            }
            // A stale but valid index is safer than aborting capture after its
            // directory transaction already committed.  The next sweep rebuilds
            // the full index, and loaders also verify every manifest directly.
            if (!string.IsNullOrEmpty(replaceError))
                Debug.LogWarning("[ThoracicUSCoach] Dataset index update deferred: " + replaceError);
        }

        [Serializable]
        private sealed class DatasetIndex
        {
            public int schemaVersion = 2;
            public string generatedUtc;
            public string invariant = "Complete sweeps and trajectory configuration families remain in one split";
            public List<string> sweepManifests = new List<string>();
            public List<string> excludedMissingOrIncomplete = new List<string>();
            public List<DatasetExperimentIndex> experiments = new List<DatasetExperimentIndex>();
        }

        [Serializable]
        private sealed class DatasetExperimentIndex
        {
            public string experimentId;
            public int sweepCount;
            public int frameCount;
            public int trainSweeps;
            public int devSweeps;
            public int testSweeps;
        }

        private int ExistingDiverseSweepCount()
        {
            string dataset = Path.Combine(ArtifactRoot(), "dataset");
            if (!Directory.Exists(dataset)) return 0;
            int count = 0;
            foreach (string pathName in Directory.GetFiles(dataset, "sweep_manifest.json", SearchOption.AllDirectories))
            {
                if (pathName.IndexOf(".partial", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                try
                {
                    CaptureManifest manifest = JsonUtility.FromJson<CaptureManifest>(File.ReadAllText(pathName));
                    if (manifest != null && manifest.experimentId == datasetExperimentId
                        && manifest.datasetRunId == datasetRunId && manifest.captureStatus == "complete") count++;
                }
                catch (ArgumentException) { }
            }
            return count;
        }

        private bool TryReadExperimentReferenceFloor(out float floor)
        {
            floor = 0f;
            string dataset = Path.Combine(ArtifactRoot(), "dataset");
            if (!Directory.Exists(dataset)) return false;
            foreach (string pathName in Directory.GetFiles(dataset, "sweep_manifest.json", SearchOption.AllDirectories)
                         .OrderBy(value => value, StringComparer.Ordinal))
            {
                if (pathName.IndexOf(".partial", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                try
                {
                    CaptureManifest manifest = JsonUtility.FromJson<CaptureManifest>(File.ReadAllText(pathName));
                    if (manifest != null && manifest.experimentId == datasetExperimentId
                        && manifest.datasetRunId == datasetRunId
                        && manifest.captureStatus == "complete" && manifest.trajectory != null
                        && manifest.trajectory.referenceScoreFloor > 0f)
                    {
                        floor = manifest.trajectory.referenceScoreFloor;
                        return true;
                    }
                }
                catch (ArgumentException) { }
                catch (IOException) { }
            }
            return false;
        }

        private static string ManifestSplit(CaptureManifest manifest)
        {
            if (!string.IsNullOrEmpty(manifest.split)) return manifest.split;
            return manifest.frames != null && manifest.frames.Count > 0 ? manifest.frames[0].split : string.Empty;
        }

        private static string ProjectAbsolutePath(string relative)
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.Combine(projectRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        }

        private static float RandomRange(System.Random random, float minimum, float maximum)
        {
            return minimum + (float)random.NextDouble() * (maximum - minimum);
        }

        private static string RelativeToProject(string path)
        {
            string root = Directory.GetParent(Application.dataPath).FullName.Replace('\\', '/').TrimEnd('/') + "/";
            return path.Replace('\\', '/').Replace(root, string.Empty);
        }

        private static string SafeName(string value)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) value = value.Replace(c, '_');
            return value;
        }

        private static Transform FindTransformByPath(string path)
        {
            GameObject found = GameObject.Find(path);
            return found != null ? found.transform : null;
        }

        private static bool AncestorNameContains(Transform current, string text)
        {
            while (current != null)
            {
                if (current.name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0) return true;
                current = current.parent;
            }
            return false;
        }
    }
}
