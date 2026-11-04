#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace CSSALT.ThoracicUSCoach
{
    public sealed partial class ThoracicUSCoachController
    {
        private const string RandomStartDemoModel =
            "Artifacts/ThoracicUSCoach/sweep_count/G2_5_RANDOM_START_HOME_ROBUSTNESS_20261103_R01/actor_only_evidence/home_robust_primary_v2_verifier_bundle.joblib";
        private const string RandomStartDemoPythonPreference =
            "ThoracicUSCoach.RandomStartLandmarkDemo.PythonExecutable";
        private const string RandomStartDemoMarkerRoot =
            "Thoracic Landmark Demo Predictions";

        [Header("Random-start T2-T8 landmark demo")]
        public bool showRandomStartLandmarkDemo = true;
        [SerializeField] private string randomStartDemoSeedText = "20261203";
        [SerializeField] private string randomStartDemoMaximumMmText = "30";
        [SerializeField] private string randomStartDemoHomeJitterMmText = "12";
        [SerializeField] private string randomStartDemoLateralJitterMmText = "1";
        [SerializeField] private string randomStartDemoPythonExecutable = string.Empty;
        [SerializeField] private string randomStartDemoState = "READY";
        [SerializeField] private string randomStartDemoMessage =
            "Choose a seed, then run the physical Home-return and exploration demo.";

        [Serializable]
        public sealed class RandomStartLandmarkDemoLevel
        {
            public int ordinal;
            public string inferredLevel;
            public float centerRelativeMm;
            public List<float> uncertaintyIntervalMm = new();
            public List<int> supportingFrames = new();
            public string eventType;
            public bool accepted;
            public float classifierScore;
        }

        [Serializable]
        public sealed class RandomStartLandmarkDemoMap
        {
            public string contract;
            public float homeRelativeOriginMm;
            public string coordinateSource;
            public string directionConvention;
            public List<RandomStartLandmarkDemoLevel> levels = new();
            public float predictedT7RelativeMm;
            public string overallMapStatus;
            public string abstentionReason;
        }

        private Process randomStartDemoAnalysisProcess;
        private readonly object randomStartDemoAnalysisLogLock = new();
        private string randomStartDemoAnalysisOutput = string.Empty;
        private string randomStartDemoAnalysisError = string.Empty;
        private string randomStartDemoOutputRoot = string.Empty;
        private string randomStartDemoMapPath = string.Empty;
        private bool randomStartDemoRunActive;
        private bool randomStartDemoAnalysisStarted;
        private bool randomStartDemoAnalysisOnly;
        private bool randomStartDemoContinuePhase2;
        private RandomStartLandmarkDemoMap randomStartDemoMap;
        private GameObject randomStartDemoVisualizationRoot;
        private readonly List<Transform> randomStartDemoLabels = new();
        private readonly List<Material> randomStartDemoMaterials = new();
        private Camera randomStartDemoCamera;
        private Vector3 randomStartDemoOriginalCameraPosition;
        private Quaternion randomStartDemoOriginalCameraRotation;
        private Vector3 randomStartDemoCameraFocus;
        private float randomStartDemoCameraDistance;
        private float randomStartDemoCameraYaw;
        private float randomStartDemoCameraPitch;
        private bool randomStartDemoCameraOverride;

        private void InitializeRandomStartLandmarkDemo()
        {
            if (string.IsNullOrWhiteSpace(randomStartDemoPythonExecutable))
                randomStartDemoPythonExecutable = EditorPrefs.GetString(
                    RandomStartDemoPythonPreference, DiscoverPhase2PocPython());
            randomStartDemoState = "READY";
            randomStartDemoMessage =
                "Choose a seed, then run the physical Home-return and exploration demo.";
        }

        private void DestroyRandomStartLandmarkDemo()
        {
            StopRandomStartDemoAnalysis();
            DestroyRandomStartLandmarkVisualization();
        }

        private void UpdateRandomStartLandmarkDemo()
        {
            UpdateRandomStartDemoLabelFacing();
            if (!randomStartDemoRunActive) return;
            if (!randomStartDemoAnalysisOnly && !g2R2ContinuousComplete)
            {
                string motionPhase = g2R2ActiveActor?.observations.LastOrDefault()?.phase;
                if (!string.IsNullOrEmpty(motionPhase))
                {
                    randomStartDemoState = motionPhase == "POSITIONING_RETURN_TO_HOME"
                        ? "RETURNING HOME" : "EXPLORING";
                    randomStartDemoMessage = "Physical controller phase: " + motionPhase;
                }
                return;
            }
            if (!randomStartDemoAnalysisOnly && !string.IsNullOrEmpty(g2R2ContinuousFailure))
            {
                randomStartDemoState = "FAILED";
                randomStartDemoMessage = g2R2ContinuousFailure;
                randomStartDemoRunActive = false;
                return;
            }
            if (!randomStartDemoAnalysisStarted)
            {
                StartRandomStartDemoAnalysis();
                return;
            }
            if (randomStartDemoAnalysisProcess == null
                || !randomStartDemoAnalysisProcess.HasExited) return;
            CompleteRandomStartDemoAnalysis();
        }

        public bool StartRandomStartLandmarkDemo()
        {
            return StartRandomStartLandmarkDemo(false);
        }

        public bool StartRandomStartLandmarkDemo(bool continuePhase2)
        {
            if (!Application.isPlaying)
            {
                SetRandomStartDemoFailure("Enter Play Mode before running the demo.");
                return false;
            }
            if (randomStartDemoRunActive || g2R2ContinuousRoutine != null)
            {
                SetRandomStartDemoFailure("A physical exploration episode is already running.");
                return false;
            }
            if (!TryParseRandomStartDemoConfiguration(out int seed,
                    out float maximumRandomStartMm, out float homeJitterMm,
                    out float lateralJitterMm, out string error))
            {
                SetRandomStartDemoFailure(error);
                return false;
            }
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(projectRoot))
            {
                SetRandomStartDemoFailure("Could not resolve the Unity project root.");
                return false;
            }
            string modelPath = Path.Combine(projectRoot,
                RandomStartDemoModel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(modelPath))
            {
                SetRandomStartDemoFailure("Frozen actor model is missing: " + modelPath);
                return false;
            }
            string python = ResolvePhase2PocPython(randomStartDemoPythonExecutable);
            if (string.IsNullOrEmpty(python))
            {
                SetRandomStartDemoFailure("Select a Python executable with joblib/sklearn installed.");
                return false;
            }
            randomStartDemoPythonExecutable = python;
            EditorPrefs.SetString(RandomStartDemoPythonPreference, python);
            DestroyRandomStartLandmarkVisualization();
            StopRandomStartDemoAnalysis();
            ResetG2R2ContinuousExperimentConfiguration();
            randomStartDemoOutputRoot = Path.Combine("Artifacts", "ThoracicUSCoach",
                "sweep_count", "RANDOM_START_LANDMARK_DEMO_"
                    + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture));
            ConfigureGroundTruthFreeRandomStartThoracicExploration(
                randomStartDemoOutputRoot, 89.5f, 45, -53.5f,
                seed, maximumRandomStartMm);
            ConfigureGroundTruthFreeEpisodeHomeJitter(20261119, homeJitterMm);
            ConfigureGroundTruthFreeEpisodeHomeLateralJitter(20261108, lateralJitterMm);
            randomStartDemoMap = null;
            randomStartDemoMapPath = string.Empty;
            randomStartDemoAnalysisStarted = false;
            randomStartDemoAnalysisOnly = false;
            randomStartDemoContinuePhase2 = continuePhase2;
            randomStartDemoRunActive = true;
            randomStartDemoState = "RANDOM START";
            randomStartDemoMessage = "Preparing a seeded start and physical return to Home.";
            if (StartG2R2ContinuousSweep()) return true;
            randomStartDemoRunActive = false;
            SetRandomStartDemoFailure("The physical exploration controller refused to start.");
            return false;
        }

        public bool AnalyzeLatestRandomStartLandmarkDemo()
        {
            if (!Application.isPlaying || g2R2ContinuousRoutine != null
                || (randomStartDemoAnalysisProcess != null
                    && !randomStartDemoAnalysisProcess.HasExited)) return false;
            randomStartDemoRunActive = false;
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(randomStartDemoOutputRoot))
            {
                string experimentRoot = Path.Combine(projectRoot, "Artifacts",
                    "ThoracicUSCoach", "sweep_count");
                DirectoryInfo latest = Directory.Exists(experimentRoot)
                    ? new DirectoryInfo(experimentRoot)
                        .GetDirectories("RANDOM_START_LANDMARK_DEMO_*")
                        .Where(item => File.Exists(Path.Combine(
                            item.FullName, "trajectory_actor.json")))
                        .OrderByDescending(item => item.LastWriteTimeUtc).FirstOrDefault()
                    : null;
                if (latest == null)
                {
                    SetRandomStartDemoFailure("No completed random-start demo capture was found.");
                    return false;
                }
                randomStartDemoOutputRoot = Path.GetRelativePath(projectRoot, latest.FullName);
            }
            string actorPath = Path.Combine(projectRoot, randomStartDemoOutputRoot,
                "trajectory_actor.json");
            if (!File.Exists(actorPath))
            {
                SetRandomStartDemoFailure("The latest completed demo has no actor trajectory.");
                return false;
            }
            StopRandomStartDemoAnalysis();
            DestroyRandomStartLandmarkVisualization();
            randomStartDemoMap = null;
            randomStartDemoMapPath = string.Empty;
            randomStartDemoAnalysisStarted = false;
            randomStartDemoAnalysisOnly = true;
            randomStartDemoRunActive = true;
            if (TryPresentPersistedRandomStartDemoAnalysis()) return true;
            randomStartDemoState = "ANALYZING";
            randomStartDemoMessage = "Re-analyzing the latest completed physical capture.";
            return true;
        }

        private bool TryParseRandomStartDemoConfiguration(
            out int seed, out float maximumRandomStartMm,
            out float homeJitterMm, out float lateralJitterMm, out string error)
        {
            seed = 0;
            maximumRandomStartMm = 0f;
            homeJitterMm = 0f;
            lateralJitterMm = 0f;
            error = string.Empty;
            if (!int.TryParse(randomStartDemoSeedText, NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out seed))
                error = "Seed must be an integer.";
            else if (!float.TryParse(randomStartDemoMaximumMmText, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out maximumRandomStartMm)
                || maximumRandomStartMm < 6f || maximumRandomStartMm > 30f)
                error = "Random-start distance must be between 6 and 30 mm.";
            else if (!float.TryParse(randomStartDemoHomeJitterMmText, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out homeJitterMm)
                || homeJitterMm < 0f || homeJitterMm > 12f)
                error = "Longitudinal Home jitter must be between 0 and 12 mm.";
            else if (!float.TryParse(randomStartDemoLateralJitterMmText, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out lateralJitterMm)
                || lateralJitterMm < 0f || lateralJitterMm > 1f)
                error = "Lateral Home jitter must be between 0 and 1 mm for this demo.";
            return string.IsNullOrEmpty(error);
        }

        private void StartRandomStartDemoAnalysis()
        {
            randomStartDemoAnalysisStarted = true;
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            string absoluteOutput = Path.GetFullPath(Path.Combine(projectRoot,
                randomStartDemoOutputRoot));
            string actorPath = Path.Combine(absoluteOutput, "trajectory_actor.json");
            string modelPath = Path.Combine(projectRoot,
                RandomStartDemoModel.Replace('/', Path.DirectorySeparatorChar));
            string analysisRoot = Path.Combine(absoluteOutput, "actor_analysis");
            if (!File.Exists(actorPath))
            {
                SetRandomStartDemoFailure("Actor trajectory was not persisted: " + actorPath);
                randomStartDemoRunActive = false;
                return;
            }
            string[] arguments = BuildRandomStartLandmarkDemoAnalysisArguments(
                projectRoot, absoluteOutput, actorPath, modelPath, analysisRoot);
            ProcessStartInfo info = new ProcessStartInfo
            {
                FileName = randomStartDemoPythonExecutable,
                Arguments = string.Join(" ", arguments.Select(QuotePhase2PocArgument)),
                WorkingDirectory = Path.Combine(projectRoot, "Tools", "ThoracicUSCoach"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            lock (randomStartDemoAnalysisLogLock)
            {
                randomStartDemoAnalysisOutput = string.Empty;
                randomStartDemoAnalysisError = string.Empty;
            }
            try
            {
                randomStartDemoAnalysisProcess = new Process
                {
                    StartInfo = info,
                    EnableRaisingEvents = true
                };
                randomStartDemoAnalysisProcess.OutputDataReceived += (_, eventArgs) =>
                {
                    if (eventArgs.Data == null) return;
                    lock (randomStartDemoAnalysisLogLock)
                        randomStartDemoAnalysisOutput += eventArgs.Data + Environment.NewLine;
                };
                randomStartDemoAnalysisProcess.ErrorDataReceived += (_, eventArgs) =>
                {
                    if (eventArgs.Data == null) return;
                    lock (randomStartDemoAnalysisLogLock)
                        randomStartDemoAnalysisError += eventArgs.Data + Environment.NewLine;
                };
                if (!randomStartDemoAnalysisProcess.Start())
                    throw new InvalidOperationException("Process.Start returned false.");
                randomStartDemoAnalysisProcess.BeginOutputReadLine();
                randomStartDemoAnalysisProcess.BeginErrorReadLine();
                randomStartDemoState = "ANALYZING";
                randomStartDemoMessage =
                    "Running the frozen pixel-only landmark model; evaluator data is not an input.";
            }
            catch (Exception exception) when (exception is InvalidOperationException
                || exception is System.ComponentModel.Win32Exception)
            {
                StopRandomStartDemoAnalysis();
                SetRandomStartDemoFailure("Could not start actor analysis: " + exception.Message);
                randomStartDemoRunActive = false;
            }
        }

        public static string[] BuildRandomStartLandmarkDemoAnalysisArguments(
            string projectRoot, string sessionRoot, string actorPath,
            string modelPath, string analysisRoot)
        {
            return new[]
            {
                Path.Combine(projectRoot, "Tools", "ThoracicUSCoach",
                    "ground_truth_free_exploration.py"),
                "--session-root", sessionRoot,
                "--actor", actorPath,
                "--model", modelPath,
                "--output", analysisRoot
            };
        }

        private void CompleteRandomStartDemoAnalysis()
        {
            Process completed = randomStartDemoAnalysisProcess;
            randomStartDemoAnalysisProcess = null;
            int exitCode = completed.ExitCode;
            completed.Dispose();
            if (exitCode != 0)
            {
                string error;
                lock (randomStartDemoAnalysisLogLock)
                    error = randomStartDemoAnalysisError.Trim();
                SetRandomStartDemoFailure("Actor analysis failed with exit code " + exitCode
                    + (string.IsNullOrEmpty(error) ? "." : ": " + error));
                randomStartDemoRunActive = false;
                return;
            }
            if (TryPresentPersistedRandomStartDemoAnalysis()) return;
            SetRandomStartDemoFailure("Actor analysis produced no final relative map.");
            randomStartDemoRunActive = false;
        }

        private bool TryPresentPersistedRandomStartDemoAnalysis()
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            string analysisRoot = Path.Combine(projectRoot, randomStartDemoOutputRoot,
                "actor_analysis");
            if (!Directory.Exists(analysisRoot)) return false;
            DirectoryInfo finalCheckpoint = new DirectoryInfo(analysisRoot)
                .GetDirectories("checkpoint_*")
                .OrderBy(item => item.Name, StringComparer.Ordinal)
                .LastOrDefault();
            randomStartDemoMapPath = finalCheckpoint == null ? string.Empty
                : Path.Combine(finalCheckpoint.FullName, "relative_spine_map.json");
            if (string.IsNullOrEmpty(randomStartDemoMapPath)
                || !File.Exists(randomStartDemoMapPath))
                return false;
            randomStartDemoMap = JsonUtility.FromJson<RandomStartLandmarkDemoMap>(
                File.ReadAllText(randomStartDemoMapPath));
            if (!ValidateRandomStartLandmarkDemoMap(randomStartDemoMap, out string reason))
            {
                randomStartDemoState = "ABSTAINED";
                randomStartDemoMessage = reason;
                randomStartDemoRunActive = false;
                randomStartDemoAnalysisOnly = false;
                return true;
            }
            if (!BuildRandomStartLandmarkVisualization(out string visualizationError))
            {
                SetRandomStartDemoFailure(visualizationError);
                randomStartDemoRunActive = false;
                randomStartDemoAnalysisOnly = false;
                return true;
            }
            randomStartDemoState = "COMPLETE";
            randomStartDemoMessage = string.Format(CultureInfo.InvariantCulture,
                "Seven image-supported landmarks visualized. T7 = {0:+0.000;-0.000;0.000} mm from Home.",
                randomStartDemoMap.predictedT7RelativeMm);
            if (randomStartDemoContinuePhase2)
            {
                randomStartDemoState = "PHASE 1 COMPLETE";
                randomStartDemoMessage = string.Format(CultureInfo.InvariantCulture,
                    "T7 = {0:+0.000;-0.000;0.000} mm. Starting bounded physical Phase 2.",
                    randomStartDemoMap.predictedT7RelativeMm);
                if (StartPhase2BestViewFromPhaseOne()) return true;
                SetRandomStartDemoFailure("Phase 2 refused the validated Phase-1 handoff.");
            }
            randomStartDemoRunActive = false;
            randomStartDemoAnalysisOnly = false;
            ResetG2R2ContinuousExperimentConfiguration();
            return true;
        }

        public static bool ValidateRandomStartLandmarkDemoMap(
            RandomStartLandmarkDemoMap map, out string reason)
        {
            reason = string.Empty;
            if (map == null)
                reason = "The actor map could not be parsed.";
            else if (!string.Equals(map.overallMapStatus,
                         "ACCEPTED_UNIQUE_SEVEN_LEVEL_MAP", StringComparison.Ordinal))
                reason = "Actor abstained: " + (string.IsNullOrEmpty(map.abstentionReason)
                    ? map.overallMapStatus : map.abstentionReason);
            else if (map.levels == null || map.levels.Count != 7)
                reason = "The actor did not produce exactly seven supported map entries.";
            else
            {
                string[] expected = { "T2", "T3", "T4", "T5", "T6", "T7", "T8" };
                for (int index = 0; index < expected.Length; index++)
                {
                    RandomStartLandmarkDemoLevel level = map.levels[index];
                    if (level == null || !level.accepted
                        || level.ordinal != index + 1
                        || !string.Equals(level.inferredLevel, expected[index],
                            StringComparison.Ordinal))
                    {
                        reason = "The actor map is incomplete or ambiguously ordered at V"
                            + (index + 1) + ".";
                        break;
                    }
                }
            }
            return string.IsNullOrEmpty(reason);
        }

        private bool BuildRandomStartLandmarkVisualization(out string error)
        {
            error = string.Empty;
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            string sessionRoot = Path.Combine(projectRoot, randomStartDemoOutputRoot);
            string actorPath = Path.Combine(sessionRoot, "trajectory_actor.json");
            string physicalPath = Path.Combine(sessionRoot, "evaluator_only",
                "trajectory_privileged.json");
            if (!File.Exists(actorPath) || !File.Exists(physicalPath))
            {
                error = "The completed actor/physical trajectory pair is unavailable for visualization.";
                return false;
            }
            G2R2ActorTrajectory actor = JsonUtility.FromJson<G2R2ActorTrajectory>(
                File.ReadAllText(actorPath));
            G2R2PrivilegedTrajectory physical = JsonUtility.FromJson<G2R2PrivilegedTrajectory>(
                File.ReadAllText(physicalPath));
            if (actor?.observations == null || physical?.observations == null)
            {
                error = "The completed trajectory pair could not be parsed for visualization.";
                return false;
            }
            Dictionary<int, G2R2PrivilegedObservation> physicalByIndex =
                physical.observations.ToDictionary(item => item.observationIndex);
            var coordinates = new List<float>();
            var contacts = new List<Vector3>();
            var normals = new List<Vector3>();
            var pathPoints = new List<Vector3>();
            foreach (G2R2ActorObservation observation in actor.observations)
            {
                if (!observation.executedSuccessfully
                    || observation.phase == "POSITIONING_RETURN_TO_HOME"
                    || !physicalByIndex.TryGetValue(observation.observationIndex,
                        out G2R2PrivilegedObservation item)) continue;
                coordinates.Add(observation.signedActorRelativeMm);
                contacts.Add(item.contactPoint);
                normals.Add(item.surfaceNormal);
                pathPoints.Add(item.contactPoint + item.surfaceNormal.normalized * 0.002f);
            }
            if (coordinates.Count < 2)
            {
                error = "Too few executed exploration positions exist for marker projection.";
                return false;
            }
            DestroyRandomStartLandmarkVisualization();
            randomStartDemoVisualizationRoot = new GameObject(RandomStartDemoMarkerRoot)
            {
                hideFlags = HideFlags.DontSave
            };
            CreateRandomStartDemoPath(pathPoints);
            CreateRandomStartDemoMarker("HOME", physical.homeContactPoint,
                AverageNearbyNormal(0f, coordinates, normals), Color.white, 8f, "Home  0.000 mm");
            Color[] colors =
            {
                new Color(0.15f, 0.85f, 1f), new Color(0.2f, 0.45f, 1f),
                new Color(0.2f, 0.9f, 0.45f), new Color(1f, 0.9f, 0.15f),
                new Color(1f, 0.55f, 0.1f), new Color(1f, 0.1f, 0.85f),
                new Color(1f, 0.2f, 0.2f)
            };
            for (int index = 0; index < randomStartDemoMap.levels.Count; index++)
            {
                RandomStartLandmarkDemoLevel level = randomStartDemoMap.levels[index];
                if (!TryInterpolateRandomStartDemoContact(level.centerRelativeMm,
                        coordinates, contacts, normals, out Vector3 contact, out Vector3 normal))
                {
                    error = "Could not project " + level.inferredLevel
                        + " onto the executed physical path.";
                    DestroyRandomStartLandmarkVisualization();
                    return false;
                }
                string label = string.Format(CultureInfo.InvariantCulture,
                    "{0}  {1:+0.000;-0.000;0.000} mm  p={2:F2}",
                    level.inferredLevel, level.centerRelativeMm, level.classifierScore);
                CreateRandomStartDemoMarker(level.inferredLevel, contact, normal,
                    colors[index], level.inferredLevel == "T7" ? 14f : 10f, label);
            }
            FocusRandomStartDemoCamera();
            return true;
        }

        public static bool TryInterpolateRandomStartDemoContact(
            float targetMm, IReadOnlyList<float> coordinates,
            IReadOnlyList<Vector3> contacts, IReadOnlyList<Vector3> normals,
            out Vector3 contact, out Vector3 normal)
        {
            contact = Vector3.zero;
            normal = Vector3.zero;
            if (coordinates == null || contacts == null || normals == null
                || coordinates.Count == 0 || coordinates.Count != contacts.Count
                || coordinates.Count != normals.Count) return false;
            List<int> order = Enumerable.Range(0, coordinates.Count)
                .OrderBy(index => Mathf.Abs(coordinates[index] - targetMm)).ToList();
            float bestDistance = Mathf.Abs(coordinates[order[0]] - targetMm);
            List<int> nearby = order.Where(index =>
                Mathf.Abs(coordinates[index] - targetMm) <= Mathf.Max(1.6f, bestDistance + 0.1f))
                .Take(8).ToList();
            if (nearby.Count == 0) return false;
            foreach (int index in nearby)
            {
                contact += contacts[index];
                normal += normals[index];
            }
            contact /= nearby.Count;
            normal = normal.sqrMagnitude > 0.001f
                ? normal.normalized : normals[nearby[0]].normalized;
            return true;
        }

        private static Vector3 AverageNearbyNormal(float targetMm,
            IReadOnlyList<float> coordinates, IReadOnlyList<Vector3> normals)
        {
            List<int> order = Enumerable.Range(0, coordinates.Count)
                .OrderBy(index => Mathf.Abs(coordinates[index] - targetMm)).Take(8).ToList();
            Vector3 average = Vector3.zero;
            foreach (int index in order) average += normals[index];
            return average.sqrMagnitude > 0.001f ? average.normalized : Vector3.up;
        }

        private void CreateRandomStartDemoPath(IReadOnlyList<Vector3> points)
        {
            GameObject path = new GameObject("Executed Exploration Path")
            {
                hideFlags = HideFlags.DontSave
            };
            path.transform.SetParent(randomStartDemoVisualizationRoot.transform, false);
            LineRenderer line = path.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.positionCount = points.Count;
            line.SetPositions(points.ToArray());
            line.startWidth = 1.8f;
            line.endWidth = 1.8f;
            line.startColor = new Color(0.2f, 0.95f, 1f, 0.8f);
            line.endColor = new Color(0.2f, 0.95f, 1f, 0.8f);
            line.material = CreateRandomStartDemoMaterial(line.startColor);
        }

        private void CreateRandomStartDemoMarker(string name, Vector3 contact,
            Vector3 normal, Color color, float diameter, string label)
        {
            Vector3 outward = normal.sqrMagnitude > 0.001f ? normal.normalized : Vector3.up;
            Vector3 markerPosition = contact + outward * 15f;
            GameObject marker = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            marker.name = "Predicted " + name;
            marker.hideFlags = HideFlags.DontSave;
            marker.transform.SetParent(randomStartDemoVisualizationRoot.transform, true);
            marker.transform.position = markerPosition;
            marker.transform.localScale = Vector3.one * diameter;
            Collider markerCollider = marker.GetComponent<Collider>();
            if (markerCollider != null) Destroy(markerCollider);
            Renderer renderer = marker.GetComponent<Renderer>();
            renderer.material = CreateRandomStartDemoMaterial(color);

            GameObject stalk = new GameObject(name + " indicator")
            {
                hideFlags = HideFlags.DontSave
            };
            stalk.transform.SetParent(randomStartDemoVisualizationRoot.transform, false);
            LineRenderer line = stalk.AddComponent<LineRenderer>();
            line.useWorldSpace = true;
            line.positionCount = 2;
            line.SetPosition(0, contact);
            line.SetPosition(1, markerPosition);
            line.startWidth = 1.5f;
            line.endWidth = 2.5f;
            line.startColor = color;
            line.endColor = color;
            line.material = CreateRandomStartDemoMaterial(color);

            GameObject textObject = new GameObject(name + " label")
            {
                hideFlags = HideFlags.DontSave
            };
            textObject.transform.SetParent(randomStartDemoVisualizationRoot.transform, false);
            textObject.transform.position = markerPosition + outward * 12f;
            TextMesh text = textObject.AddComponent<TextMesh>();
            text.text = label;
            text.color = color;
            text.anchor = TextAnchor.MiddleCenter;
            text.alignment = TextAlignment.Center;
            text.fontSize = 52;
            text.characterSize = 1.2f;
            randomStartDemoLabels.Add(textObject.transform);
        }

        private Material CreateRandomStartDemoMaterial(Color color)
        {
            Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
            Material material = new Material(shader)
            {
                color = color,
                hideFlags = HideFlags.DontSave
            };
            randomStartDemoMaterials.Add(material);
            return material;
        }

        public void DestroyRandomStartLandmarkVisualization()
        {
            RestoreRandomStartDemoCamera();
            if (randomStartDemoVisualizationRoot != null)
                Destroy(randomStartDemoVisualizationRoot);
            randomStartDemoVisualizationRoot = null;
            randomStartDemoLabels.Clear();
            foreach (Material material in randomStartDemoMaterials)
                if (material != null) Destroy(material);
            randomStartDemoMaterials.Clear();
        }

        private void FocusRandomStartDemoCamera()
        {
            if (randomStartDemoVisualizationRoot == null) return;
            Camera camera = Camera.main;
            if (camera == null) return;
            if (!randomStartDemoCameraOverride)
            {
                randomStartDemoCamera = camera;
                randomStartDemoOriginalCameraPosition = camera.transform.position;
                randomStartDemoOriginalCameraRotation = camera.transform.rotation;
                randomStartDemoCameraOverride = true;
            }
            Renderer[] renderers = randomStartDemoVisualizationRoot
                .GetComponentsInChildren<Renderer>(true)
                .Where(item => item is MeshRenderer
                    && item.GetComponent<TextMesh>() == null).ToArray();
            if (renderers.Length == 0) return;
            Bounds bounds = renderers[0].bounds;
            for (int index = 1; index < renderers.Length; index++)
                bounds.Encapsulate(renderers[index].bounds);
            randomStartDemoCameraFocus = bounds.center;
            Vector3 lookEuler = Quaternion.LookRotation(
                randomStartDemoCameraFocus - camera.transform.position,
                Vector3.up).eulerAngles;
            randomStartDemoCameraPitch = NormalizeRandomStartDemoAngle(lookEuler.x);
            randomStartDemoCameraYaw = lookEuler.y;
            float radius = Mathf.Max(30f, bounds.extents.magnitude);
            float halfFov = Mathf.Max(10f, camera.fieldOfView * 0.5f) * Mathf.Deg2Rad;
            randomStartDemoCameraDistance = radius / Mathf.Tan(halfFov) * 1.35f;
            ApplyRandomStartDemoCamera();
        }

        private void OrbitRandomStartDemoCamera(float yawDelta, float pitchDelta)
        {
            if (!randomStartDemoCameraOverride) FocusRandomStartDemoCamera();
            if (!randomStartDemoCameraOverride) return;
            randomStartDemoCameraYaw += yawDelta;
            randomStartDemoCameraPitch = Mathf.Clamp(
                randomStartDemoCameraPitch + pitchDelta, -75f, 75f);
            ApplyRandomStartDemoCamera();
        }

        private void ZoomRandomStartDemoCamera(float factor)
        {
            if (!randomStartDemoCameraOverride) FocusRandomStartDemoCamera();
            if (!randomStartDemoCameraOverride) return;
            randomStartDemoCameraDistance = Mathf.Clamp(
                randomStartDemoCameraDistance * factor, 50f, 1200f);
            ApplyRandomStartDemoCamera();
        }

        private void ApplyRandomStartDemoCamera()
        {
            if (randomStartDemoCamera == null) return;
            Quaternion orbit = Quaternion.Euler(
                randomStartDemoCameraPitch, randomStartDemoCameraYaw, 0f);
            randomStartDemoCamera.transform.position = randomStartDemoCameraFocus
                - orbit * Vector3.forward * randomStartDemoCameraDistance;
            randomStartDemoCamera.transform.rotation = orbit;
        }

        private void RestoreRandomStartDemoCamera()
        {
            if (randomStartDemoCameraOverride && randomStartDemoCamera != null)
            {
                randomStartDemoCamera.transform.position = randomStartDemoOriginalCameraPosition;
                randomStartDemoCamera.transform.rotation = randomStartDemoOriginalCameraRotation;
            }
            randomStartDemoCamera = null;
            randomStartDemoCameraOverride = false;
        }

        private static float NormalizeRandomStartDemoAngle(float angle)
        {
            return angle > 180f ? angle - 360f : angle;
        }

        private void UpdateRandomStartDemoLabelFacing()
        {
            Camera camera = Camera.main;
            if (camera == null) return;
            foreach (Transform label in randomStartDemoLabels)
                if (label != null) label.rotation = camera.transform.rotation;
        }

        private void StopRandomStartDemoAnalysis()
        {
            Process process = randomStartDemoAnalysisProcess;
            randomStartDemoAnalysisProcess = null;
            if (process == null) return;
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(2000);
                }
            }
            catch (InvalidOperationException) { }
            finally
            {
                process.Dispose();
            }
        }

        private void SetRandomStartDemoFailure(string message)
        {
            randomStartDemoState = "FAILED";
            randomStartDemoMessage = message;
            Debug.LogError("[ThoracicUSCoach][LandmarkDemo] " + message);
        }

        private void DrawRandomStartLandmarkDemoGUI()
        {
            if (!showRandomStartLandmarkDemo) return;
            GUI.depth = -1000;
            float width = Mathf.Min(470f, Screen.width - 20f);
            float height = Mathf.Min(720f, Screen.height - 20f);
            GUILayout.BeginArea(new Rect(10f, 10f, width, height), GUI.skin.box);
            GUILayout.Label("Thoracic US Coach â€” Random Start Landmark Demo");
            GUILayout.Label("SIMULATOR FIXED-ANATOMY POC â€” actor predictions, not clinical labels");
            GUILayout.Space(4f);
            GUILayout.Label("1. Seeded random start  2. Physical return Home  3. Explore  4. Ultrasound map");
            GUI.enabled = !randomStartDemoRunActive;
            GUILayout.BeginHorizontal();
            GUILayout.Label("Random seed", GUILayout.Width(145f));
            randomStartDemoSeedText = GUILayout.TextField(randomStartDemoSeedText);
            if (GUILayout.Button("New seed", GUILayout.Width(90f)))
                randomStartDemoSeedText = DateTime.UtcNow.Ticks.GetHashCode()
                    .ToString(CultureInfo.InvariantCulture);
            GUILayout.EndHorizontal();
            DrawRandomStartDemoTextSetting("Random distance (6â€“30 mm)",
                ref randomStartDemoMaximumMmText);
            DrawRandomStartDemoTextSetting("Home longitudinal jitter (0â€“12)",
                ref randomStartDemoHomeJitterMmText);
            DrawRandomStartDemoTextSetting("Home lateral jitter (0â€“1)",
                ref randomStartDemoLateralJitterMmText);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Python", GUILayout.Width(145f));
            randomStartDemoPythonExecutable = GUILayout.TextField(
                randomStartDemoPythonExecutable ?? string.Empty);
            if (GUILayout.Button("Browse", GUILayout.Width(90f)))
            {
                string selected = EditorUtility.OpenFilePanel("Select Python executable",
                    Path.GetDirectoryName(randomStartDemoPythonExecutable) ?? string.Empty, "exe");
                if (!string.IsNullOrEmpty(selected))
                {
                    randomStartDemoPythonExecutable = selected;
                    EditorPrefs.SetString(RandomStartDemoPythonPreference, selected);
                }
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(5f);
            if (GUILayout.Button("RUN: RANDOM START â†’ HOME â†’ EXPLORE â†’ MARK LANDMARKS",
                    GUILayout.Height(38f)))
                StartRandomStartLandmarkDemo();
            if (GUILayout.Button("RUN FULL PIPELINE: PHASE 1 -> PHASE 2 BEST T7 VIEW",
                    GUILayout.Height(42f)))
                StartRandomStartLandmarkDemo(true);
            GUI.enabled = true;
            GUI.enabled = !randomStartDemoRunActive;
            if (GUILayout.Button("Analyze latest completed capture"))
                AnalyzeLatestRandomStartLandmarkDemo();
            GUI.enabled = true;
            if (GUILayout.Button("Clear landmark visualization"))
                DestroyRandomStartLandmarkVisualization();
            GUILayout.Space(6f);
            GUILayout.Label("State: " + randomStartDemoState);
            GUILayout.Label(randomStartDemoMessage);
            if (phase2BestViewResult != null)
            {
                GUILayout.Label("Phase 2: " + phase2BestViewState);
                GUILayout.Label(string.Format(CultureInfo.InvariantCulture,
                    "Candidates: {0}   best p={1:F3} quality={2:F3}",
                    phase2BestViewResult.observations.Count,
                    phase2BestViewResult.bestValidityProbability,
                    phase2BestViewResult.bestQualityScore));
                if (!string.IsNullOrEmpty(phase2BestViewResult.finalUltrasound))
                    GUILayout.Label("Final fresh ultrasound: "
                        + phase2BestViewResult.finalUltrasound);
            }
            if (g2R2ActiveActor != null)
            {
                GUILayout.Label(string.Format(CultureInfo.InvariantCulture,
                    "Motion: {0} accepted / {1} rejected   Frames: {2}",
                    g2R2ActiveActor.acceptedMovements, g2R2ActiveActor.rejectedMovements,
                    g2R2ActiveActor.observations.Count));
                GUILayout.Label(string.Format(CultureInfo.InvariantCulture,
                    "Initial start: {0:+0.000;-0.000;0.000} mm   Home residual: {1:F3} mm",
                    g2R2ActiveActor.initialActorRelativeMm,
                    g2R2ActiveActor.homeArrivalResidualMm));
            }
            if (randomStartDemoMap?.levels != null)
            {
                GUILayout.Space(5f);
                GUILayout.Label("Actor-only relative map (markers use the executed probe path):");
                foreach (RandomStartLandmarkDemoLevel level in randomStartDemoMap.levels)
                    GUILayout.Label(string.Format(CultureInfo.InvariantCulture,
                        "{0}: {1,9:+0.000;-0.000;0.000} mm   score {2:F3}",
                        level.inferredLevel, level.centerRelativeMm, level.classifierScore));
            }
            if (randomStartDemoVisualizationRoot != null)
            {
                GUILayout.Space(5f);
                GUILayout.Label("Demo camera (temporary; restored when markers are cleared):");
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Focus all landmarks")) FocusRandomStartDemoCamera();
                if (GUILayout.Button("Restore camera")) RestoreRandomStartDemoCamera();
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Left")) OrbitRandomStartDemoCamera(-20f, 0f);
                if (GUILayout.Button("Right")) OrbitRandomStartDemoCamera(20f, 0f);
                if (GUILayout.Button("Up")) OrbitRandomStartDemoCamera(0f, -12f);
                if (GUILayout.Button("Down")) OrbitRandomStartDemoCamera(0f, 12f);
                GUILayout.EndHorizontal();
                GUILayout.BeginHorizontal();
                if (GUILayout.Button("Zoom +")) ZoomRandomStartDemoCamera(0.8f);
                if (GUILayout.Button("Zoom -")) ZoomRandomStartDemoCamera(1.25f);
                GUILayout.EndHorizontal();
            }
            if (!string.IsNullOrEmpty(randomStartDemoOutputRoot))
            {
                GUILayout.Space(5f);
                GUILayout.Label("Evidence: " + randomStartDemoOutputRoot);
            }
            GUILayout.Space(5f);
            GUILayout.Label("T2â€“T8 naming is conditional on the controlled seven-level-region prior.");
            GUILayout.Label("No LF/T-level coordinate is used for movement, inference, or marker placement.");
            GUILayout.EndArea();
            GUI.enabled = true;
        }

        private static void DrawRandomStartDemoTextSetting(string label, ref string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(225f));
            value = GUILayout.TextField(value ?? string.Empty);
            GUILayout.EndHorizontal();
        }
    }
}
#endif

