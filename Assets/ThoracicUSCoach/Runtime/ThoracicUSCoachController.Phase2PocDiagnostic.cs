#if UNITY_EDITOR
using System;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace CSSALT.ThoracicUSCoach
{
    public sealed partial class ThoracicUSCoachController
    {
        private const string Phase2PocManifest =
            "Tools/ThoracicUSCoach/experiments/phase2_fixed_anatomy_engineering_poc_v1.json";
        private const string Phase2PocPythonPreference =
            "ThoracicUSCoach.Phase2Poc.PythonExecutable";
        private static readonly string[] Phase2PocCaseNames =
        {
            "Positive 01", "Positive 05", "Hard Negative 06"
        };

        [Header("Phase 2 fixed-anatomy diagnostic POC")]
        public bool showPhase2PocDiagnostic = true;
        [SerializeField, Range(0, 2)] private int phase2PocSelectedCase;
        [SerializeField] private string phase2PocPythonExecutable = string.Empty;
        [SerializeField] private string phase2PocDisplayState = "READY";
        [SerializeField] private string phase2PocDisplayMessage = "Select a consumed development case.";

        private Process phase2PocPolicyProcess;
        private Coroutine phase2PocStartupRoutine;
        private U08P1EpisodeOutcome phase2PocLastOutcome;
        private bool phase2PocComplete;
        private bool phase2PocEpisodeRunning;
        private bool phase2PocBridgeOwned;
        private string phase2PocPolicyOutput = string.Empty;
        private string phase2PocPolicyError = string.Empty;
        private string phase2PocLastOutputDirectory = string.Empty;
        private readonly object phase2PocPolicyLogLock = new object();

        public bool Phase2PocPanelVisible => showPhase2PocDiagnostic;
        public bool Phase2PocInteractiveRunning => phase2PocEpisodeRunning
            || phase2PocStartupRoutine != null;
        public bool Phase2PocInteractiveComplete => phase2PocComplete;
        public bool Phase2PocOwnedPolicyRunning => phase2PocPolicyProcess != null
            && !phase2PocPolicyProcess.HasExited;
        public U08P1EpisodeOutcome Phase2PocLastOutcome => phase2PocLastOutcome;
        public string Phase2PocDisplayState => phase2PocDisplayState;
        public string Phase2PocLastOutputDirectory => phase2PocLastOutputDirectory;

        private void InitializePhase2PocDiagnostic()
        {
            if (string.IsNullOrWhiteSpace(phase2PocPythonExecutable))
                phase2PocPythonExecutable = EditorPrefs.GetString(
                    Phase2PocPythonPreference, DiscoverPhase2PocPython());
            phase2PocDisplayState = "READY";
            phase2PocDisplayMessage = "Select a consumed development case.";
        }

        private void DestroyPhase2PocDiagnostic()
        {
            StopPhase2PocOwnedPolicy();
            ReleasePhase2PocBridge();
        }

        public bool StartPhase2PocInteractive(int caseIndex, string pythonExecutable = "")
        {
            if (!Application.isPlaying)
            {
                SetPhase2PocFailure("Enter Play Mode before starting the POC.");
                return false;
            }
            if (Phase2PocInteractiveRunning || active2DCaptureRoutine != null)
            {
                SetPhase2PocFailure("A diagnostic or capture coroutine is already running.");
                return false;
            }
            phase2PocSelectedCase = Mathf.Clamp(caseIndex, 0, Phase2PocCaseNames.Length - 1);
            if (!string.IsNullOrWhiteSpace(pythonExecutable))
                phase2PocPythonExecutable = pythonExecutable.Trim();
            EditorPrefs.SetString(Phase2PocPythonPreference, phase2PocPythonExecutable ?? string.Empty);
            phase2PocComplete = false;
            phase2PocLastOutcome = null;
            phase2PocDisplayState = "RECOGNIZE";
            phase2PocDisplayMessage = "Starting the owned image-only policy server...";
            if (!StartPhase2PocOwnedPolicy()) return false;
            phase2PocStartupRoutine = StartCoroutine(WaitForPhase2PocPolicyAndStart());
            return true;
        }

        public void ResetPhase2PocInteractive()
        {
            if (phase2PocStartupRoutine != null)
                StopCoroutine(phase2PocStartupRoutine);
            phase2PocStartupRoutine = null;
            if (active2DRoutine != null) StopCoroutine(active2DRoutine);
            active2DRoutine = null;
            u08P1EvaluationRunning = false;
            u08P1EvaluationComplete = false;
            phase2PocEpisodeRunning = false;
            phase2PocComplete = false;
            phase2PocLastOutcome = null;
            active2DPhase = Active2DPhase.Idle;
            active2DStatus = "interactive POC reset; ready";
            active2DLastAction = "STOP";
            active2DConfidence = 0f;
            active2DUncertainty = 1f;
            active2DResult = null;
            StopPhase2PocOwnedPolicy();
            ReleasePhase2PocBridge();
            phase2PocDisplayState = "READY";
            phase2PocDisplayMessage = "Reset complete. Select a case and start again.";
        }

        public bool RunPhase2PocAgain()
        {
            int selected = phase2PocSelectedCase;
            string python = phase2PocPythonExecutable;
            ResetPhase2PocInteractive();
            return StartPhase2PocInteractive(selected, python);
        }

        private IEnumerator WaitForPhase2PocPolicyAndStart()
        {
            float deadline = Time.realtimeSinceStartup + 8f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (phase2PocPolicyProcess == null || phase2PocPolicyProcess.HasExited)
                {
                    string error = Phase2PocPolicyErrorText();
                    phase2PocStartupRoutine = null;
                    StopPhase2PocOwnedPolicy();
                    ReleasePhase2PocBridge();
                    SetPhase2PocFailure("Python policy server exited during startup. " + error);
                    yield break;
                }
                string output;
                lock (phase2PocPolicyLogLock) output = phase2PocPolicyOutput;
                if (output.IndexOf("Watching restricted image-only request",
                    StringComparison.OrdinalIgnoreCase) >= 0) break;
                yield return null;
            }
            string readyOutput;
            lock (phase2PocPolicyLogLock) readyOutput = phase2PocPolicyOutput;
            if (readyOutput.IndexOf("Watching restricted image-only request",
                StringComparison.OrdinalIgnoreCase) < 0)
            {
                phase2PocStartupRoutine = null;
                StopPhase2PocOwnedPolicy();
                ReleasePhase2PocBridge();
                SetPhase2PocFailure("Python policy server did not become ready. "
                    + Phase2PocPolicyErrorText());
                yield break;
            }
            phase2PocStartupRoutine = null;
            if (!BeginPhase2PocEpisode())
            {
                StopPhase2PocOwnedPolicy();
                ReleasePhase2PocBridge();
                yield break;
            }
        }

        private bool BeginPhase2PocEpisode()
        {
            try
            {
                string absoluteManifest = ResolveProjectPath(Phase2PocManifest);
                U08P1Manifest manifest = JsonUtility.FromJson<U08P1Manifest>(
                    File.ReadAllText(absoluteManifest));
                ValidateU08P1Manifest(manifest, "candidate");
                if (manifest.starts == null || phase2PocSelectedCase >= manifest.starts.Count)
                    throw new InvalidOperationException("Selected case is absent from the frozen manifest.");
                string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff",
                    CultureInfo.InvariantCulture);
                string output = Path.Combine(ArtifactRoot(), "active2d", "interactive_poc",
                    stamp + "_" + manifest.starts[phase2PocSelectedCase].startId);
                phase2PocLastOutputDirectory = output;
                InitializeU08P1Session(manifest, "interactive_" + stamp,
                    absoluteManifest, output);
                phase2PocEpisodeRunning = true;
                phase2PocDisplayState = "RECOGNIZE";
                phase2PocDisplayMessage = "Fresh image capture and image-only recognition started.";
                active2DRoutine = StartCoroutine(RunPhase2PocInteractiveCase(
                    manifest.starts[phase2PocSelectedCase]));
                return true;
            }
            catch (Exception exception) when (exception is IOException
                || exception is ArgumentException || exception is InvalidOperationException)
            {
                SetPhase2PocFailure(exception.Message);
                return false;
            }
        }

        private IEnumerator RunPhase2PocInteractiveCase(U08P1StartConfig start)
        {
            bool previousPhaseOneOnly = active2DPhaseOneOnly;
            int previousBudget = active2DLiveStepBudget;
            active2DPhaseOneOnly = false;
            active2DLiveStepBudget = u08P1Manifest.stepBudget;
            try
            {
                u08P1CurrentStartAudit = null;
                yield return RunActive2DImageOnlyCore(null, start, u08P1Policy);
                phase2PocLastOutcome = BuildU08P1Outcome(start);
                u08P1Report.episodes.Add(phase2PocLastOutcome);
                u08P1Report.complete = phase2PocLastOutcome.start.valid;
                SaveU08P1Report(true);
                phase2PocComplete = true;
                if (phase2PocLastOutcome.acquisitionSuccess
                    && phase2PocLastOutcome.explicitStop
                    && !phase2PocLastOutcome.wrongStop)
                {
                    phase2PocDisplayState = "STOP";
                    phase2PocDisplayMessage = "Image-only return, fresh confirmation and evaluator-approved STOP.";
                }
                else if (phase2PocLastOutcome.abstained && !phase2PocLastOutcome.wrongStop)
                {
                    phase2PocDisplayState = "ABSTAIN";
                    phase2PocDisplayMessage = "SAFE ABSTAIN — no stop-eligible image was recognized.";
                }
                else
                {
                    SetPhase2PocFailure(string.IsNullOrEmpty(phase2PocLastOutcome.failureReason)
                        ? "Interactive POC did not reach an accepted terminal state."
                        : phase2PocLastOutcome.failureReason);
                }
            }
            finally
            {
                active2DPhaseOneOnly = previousPhaseOneOnly;
                active2DLiveStepBudget = previousBudget;
                u08P1EvaluationRunning = false;
                u08P1EvaluationComplete = true;
                phase2PocEpisodeRunning = false;
                active2DRoutine = null;
                StopPhase2PocOwnedPolicy();
                ReleasePhase2PocBridge();
            }
        }

        private bool StartPhase2PocOwnedPolicy()
        {
            StopPhase2PocOwnedPolicy();
            ClearPhase2PocBridge();
            phase2PocBridgeOwned = true;
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string python = ResolvePhase2PocPython(phase2PocPythonExecutable);
            if (string.IsNullOrEmpty(python))
            {
                ReleasePhase2PocBridge();
                SetPhase2PocFailure("Python executable was not found. Set it in the panel or THORACIC_POC_PYTHON.");
                return false;
            }
            phase2PocPythonExecutable = python;
            EditorPrefs.SetString(Phase2PocPythonPreference, python);
            string manifestPath = Path.Combine(projectRoot, Phase2PocManifest.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(manifestPath))
            {
                ReleasePhase2PocBridge();
                SetPhase2PocFailure("Frozen POC manifest is missing: " + manifestPath);
                return false;
            }
            U08P1Manifest manifest = JsonUtility.FromJson<U08P1Manifest>(File.ReadAllText(manifestPath));
            string[] arguments = BuildPhase2PocPolicyArguments(projectRoot, manifest);
            ProcessStartInfo info = new ProcessStartInfo
            {
                FileName = python,
                Arguments = string.Join(" ", arguments.Select(QuotePhase2PocArgument)),
                WorkingDirectory = Path.Combine(projectRoot, "Tools", "ThoracicUSCoach"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            lock (phase2PocPolicyLogLock)
            {
                phase2PocPolicyOutput = string.Empty;
                phase2PocPolicyError = string.Empty;
            }
            try
            {
                phase2PocPolicyProcess = new Process { StartInfo = info, EnableRaisingEvents = true };
                phase2PocPolicyProcess.OutputDataReceived += (_, eventArgs) =>
                {
                    if (eventArgs.Data == null) return;
                    lock (phase2PocPolicyLogLock)
                        phase2PocPolicyOutput += eventArgs.Data + Environment.NewLine;
                };
                phase2PocPolicyProcess.ErrorDataReceived += (_, eventArgs) =>
                {
                    if (eventArgs.Data == null) return;
                    lock (phase2PocPolicyLogLock)
                        phase2PocPolicyError += eventArgs.Data + Environment.NewLine;
                };
                if (!phase2PocPolicyProcess.Start())
                    throw new InvalidOperationException("Process.Start returned false.");
                phase2PocPolicyProcess.BeginOutputReadLine();
                phase2PocPolicyProcess.BeginErrorReadLine();
                return true;
            }
            catch (Exception exception) when (exception is InvalidOperationException
                || exception is System.ComponentModel.Win32Exception)
            {
                StopPhase2PocOwnedPolicy();
                ReleasePhase2PocBridge();
                SetPhase2PocFailure("Could not start Python policy server: " + exception.Message);
                return false;
            }
        }

        public static string[] BuildPhase2PocPolicyArguments(
            string projectRoot, U08P1Manifest manifest)
        {
            string Resolve(string relative) => Path.GetFullPath(Path.Combine(projectRoot,
                relative.Replace('/', Path.DirectorySeparatorChar)));
            return new[]
            {
                Resolve(manifest.policyServer),
                "--checkpoint", Resolve(manifest.phaseOneCheckpoint),
                "--handoff-threshold", "0.85",
                "--diagnostic-template-checkpoint", Resolve(manifest.diagnosticStopCheckpoint),
                "--phase2-action-checkpoint", Resolve(manifest.actionCheckpoint),
                "--phase-two-only-pilot",
                "--phase2-exploration-limit",
                manifest.diagnosticExplorationLimit.ToString(CultureInfo.InvariantCulture),
                "--bridge", Path.Combine(projectRoot, "Artifacts", "ThoracicUSCoach", "bridge")
            };
        }

        private static string QuotePhase2PocArgument(string value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static string DiscoverPhase2PocPython()
        {
            string configured = Environment.GetEnvironmentVariable("THORACIC_POC_PYTHON");
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
            string conda = Environment.GetEnvironmentVariable("CONDA_PREFIX");
            if (!string.IsNullOrWhiteSpace(conda))
            {
                string candidate = Path.Combine(conda, "python.exe");
                if (File.Exists(candidate)) return candidate;
            }
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (string relative in new[] { "anaconda3/python.exe", "miniconda3/python.exe" })
            {
                string candidate = Path.Combine(userProfile,
                    relative.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate)) return candidate;
            }
            return ResolvePhase2PocPython("python.exe") ?? string.Empty;
        }

        private static string ResolvePhase2PocPython(string configured)
        {
            if (string.IsNullOrWhiteSpace(configured)) return DiscoverPhase2PocPython();
            string expanded = Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"'));
            if (Path.IsPathRooted(expanded)) return File.Exists(expanded) ? expanded : null;
            foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                .Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory)) continue;
                string candidate = Path.Combine(directory.Trim(), expanded);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        private void StopPhase2PocOwnedPolicy()
        {
            Process process = phase2PocPolicyProcess;
            phase2PocPolicyProcess = null;
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
            finally { process.Dispose(); }
        }

        private void ClearPhase2PocBridge()
        {
            string bridge = Path.Combine(ArtifactRoot(), "bridge");
            foreach (string fileName in new[] { "active2d_request.json", "active2d_response.json" })
            {
                try
                {
                    string path = Path.Combine(bridge, fileName);
                    if (File.Exists(path)) File.Delete(path);
                }
                catch (IOException exception)
                {
                    Debug.LogWarning("[ThoracicUSCoach][Phase2POC] Bridge cleanup: " + exception.Message);
                }
            }
        }

        private void ReleasePhase2PocBridge()
        {
            if (!phase2PocBridgeOwned) return;
            ClearPhase2PocBridge();
            phase2PocBridgeOwned = false;
        }

        private string Phase2PocPolicyErrorText()
        {
            lock (phase2PocPolicyLogLock)
            {
                string text = string.IsNullOrWhiteSpace(phase2PocPolicyError)
                    ? phase2PocPolicyOutput : phase2PocPolicyError;
                text = (text ?? string.Empty).Trim();
                return text.Length <= 320 ? text : text.Substring(text.Length - 320);
            }
        }

        private void SetPhase2PocFailure(string reason)
        {
            phase2PocDisplayState = "FAILED";
            phase2PocDisplayMessage = reason ?? "Unknown failure";
            phase2PocComplete = true;
        }

        private string CurrentPhase2PocDisplayState()
        {
            if (!phase2PocEpisodeRunning) return phase2PocDisplayState;
            if (active2DLastAction == "OBSERVE"
                || active2DCandidateFallbackReason == "state_machine_confirm") return "CONFIRM";
            if (active2DCandidateFallbackReason == "state_machine_return") return "RETURN";
            if (active2DResult != null && active2DResult.trace.Count > 2) return "EXPLORE";
            return "RECOGNIZE";
        }

        private void DrawPhase2PocDiagnosticGUI()
        {
            if (!showPhase2PocDiagnostic) return;
            GUI.depth = -1000;
            float width = Mathf.Min(440f, Screen.width - 20f);
            float height = Mathf.Min(455f, Screen.height - 20f);
            float reservedRight = Screen.width >= 1100f ? 510f : 10f;
            float x = Mathf.Clamp(Screen.width - reservedRight - width, 10f,
                Mathf.Max(10f, Screen.width - width - 10f));
            GUILayout.BeginArea(new Rect(x, 10f, width, height), GUI.skin.box);
            GUILayout.Label("Thoracic US Coach — Phase 2 POC (Diagnostic)");
            GUILayout.Label("FIXED-ANATOMY DIAGNOSTIC — NOT CLINICALLY VALIDATED");
            GUILayout.Space(3f);
            GUI.enabled = !Phase2PocInteractiveRunning;
            phase2PocSelectedCase = GUILayout.SelectionGrid(phase2PocSelectedCase,
                Phase2PocCaseNames, 1);
            if (phase2PocSelectedCase < 2)
                GUILayout.Label("Known positive development start — not autonomous target discovery");
            GUILayout.BeginHorizontal();
            phase2PocPythonExecutable = GUILayout.TextField(
                phase2PocPythonExecutable ?? string.Empty, GUILayout.MinWidth(250f));
            if (GUILayout.Button("Python...", GUILayout.Width(82f)))
            {
                string selected = EditorUtility.OpenFilePanel("Select Python executable",
                    Path.GetDirectoryName(phase2PocPythonExecutable) ?? string.Empty, "exe");
                if (!string.IsNullOrEmpty(selected))
                {
                    phase2PocPythonExecutable = selected;
                    EditorPrefs.SetString(Phase2PocPythonPreference, selected);
                }
            }
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Start POC"))
                StartPhase2PocInteractive(phase2PocSelectedCase, phase2PocPythonExecutable);
            GUI.enabled = !Phase2PocInteractiveRunning && phase2PocComplete;
            if (GUILayout.Button("Run Again")) RunPhase2PocAgain();
            GUI.enabled = true;
            if (GUILayout.Button("Reset")) ResetPhase2PocInteractive();
            GUILayout.EndHorizontal();

            Active2DRunTrace latest = active2DResult != null
                ? active2DResult.trace.LastOrDefault() : null;
            float best = active2DResult != null && active2DResult.trace.Count > 0
                ? active2DResult.trace.Max(item => item.confidence) : active2DConfidence;
            int returns = active2DResult == null ? 0 : active2DResult.trace.Count(item =>
                item.candidateFallbackReason == "state_machine_return" && item.accepted
                && item.executionFeedbackCompleted);
            Active2DRunTrace confirmation = active2DResult == null ? null
                : active2DResult.trace.LastOrDefault(item => item.freshCapturePurpose == "confirmation");
            bool confirmationFresh = confirmation != null
                && confirmation.poseEpoch == confirmation.producerPoseEpoch
                && confirmation.producerUnityFrame > confirmation.poseAppliedUnityFrame
                && confirmation.captureUnityFrame == confirmation.producerUnityFrame;
            GUILayout.Space(4f);
            GUILayout.Label("Current phase: " + CurrentPhase2PocDisplayState());
            GUILayout.Label(string.Format(CultureInfo.InvariantCulture,
                "Recognition: {0:F6}   Best view: {1:F6}", active2DConfidence, best));
            GUILayout.Label("Action: " + active2DLastAction + "   Step: "
                + (active2DResult != null ? active2DResult.trace.Count : 0));
            string returnStatus = phase2PocLastOutcome != null && phase2PocLastOutcome.returnedToBest
                ? "COMPLETED" : CurrentPhase2PocDisplayState() == "RETURN"
                    ? "IN PROGRESS" : "not started";
            GUILayout.Label("Return-to-best: " + returnStatus + " (" + returns
                + " physically completed actions)");
            GUILayout.Label("Fresh confirmation: " + (confirmationFresh
                ? string.Format(CultureInfo.InvariantCulture, "PASS ({0:F6})", confirmation.confidence)
                : CurrentPhase2PocDisplayState() == "CONFIRM" ? "PENDING" : "—"));
            GUILayout.Label("Contact/workspace: " + (latest == null ? "not sampled"
                : (active2DResult.contactViolations == 0 ? "contact PASS" : "contact FAIL")
                    + " / " + (active2DResult.workspaceViolations == 0 ? "workspace PASS" : "workspace FAIL")));
            GUILayout.Label("Policy process: " + (Phase2PocOwnedPolicyRunning ? "RUNNING (owned)"
                : Phase2PocInteractiveRunning ? "STARTING" : "STOPPED"));
            GUILayout.Space(4f);
            GUILayout.Label("Result: " + phase2PocDisplayMessage);
            if (phase2PocLastOutcome != null && !string.IsNullOrEmpty(phase2PocLastOutcome.failureReason))
                GUILayout.Label("Reason: " + phase2PocLastOutcome.failureReason);
            GUILayout.EndArea();
            GUI.enabled = true;
        }
    }
}
#endif
