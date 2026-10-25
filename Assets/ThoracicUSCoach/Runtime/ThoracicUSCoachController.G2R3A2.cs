using System;
using System.Collections;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CSSALT.ThoracicUSCoach
{
    public sealed partial class ThoracicUSCoachController
    {
        private bool g2R3A2Complete;
        private G2R3A2Manifest g2R3A2Manifest;
        private G2R3A2UnityReport g2R3A2Report;
        private string g2R3A2OutputRoot;

        public bool G2R3A2Complete => g2R3A2Complete;
        public string G2R3A2Failure => g2R3A2Report != null ? g2R3A2Report.infrastructureFailure : string.Empty;

        public bool StartG2R3A2(string manifestProjectRelativePath)
        {
            if (!Application.isPlaying || active2DRoutine != null || active2DCaptureRoutine != null) return false;
            try
            {
                AutoWire();
                string path = ResolveProjectPath(manifestProjectRelativePath);
                g2R3A2Manifest = JsonUtility.FromJson<G2R3A2Manifest>(File.ReadAllText(path));
                ValidateG2R3A2Manifest(g2R3A2Manifest);
                g2R3A2OutputRoot = ResolveProjectPath(g2R3A2Manifest.outputDirectory);
                Directory.CreateDirectory(g2R3A2OutputRoot);
                g2R3A2Report = new G2R3A2UnityReport
                {
                    protocolId = g2R3A2Manifest.protocolId,
                    generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    executionCommit = Environment.GetEnvironmentVariable("THORACIC_G2_R3_A2_EXECUTION_COMMIT") ?? string.Empty,
                    unityVersion = Application.unityVersion,
                    scene = SceneManager.GetActiveScene().path,
                    manifest = ProjectRelativePath(path),
                    manifestSha256 = Sha256Bytes(File.ReadAllBytes(path)),
                    sceneSha256 = HashG2R3A2ProjectFile(g2R3A2Manifest.scene),
                    evaluatorSha256 = HashG2R3A2ProjectFile("Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.cs"),
                    active2DControllerSha256 = HashG2R3A2ProjectFile("Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.Active2D.cs"),
                    diagnosticControllerSha256 = HashG2R3A2ProjectFile("Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.G2R3A2.cs"),
                    acceptableScoreFloor = g2R3A2Manifest.acceptableScoreFloor,
                    positionToleranceMm = g2R3A2Manifest.positionToleranceMm,
                    rotationToleranceDeg = g2R3A2Manifest.rotationToleranceDeg,
                    contactPositionToleranceMm = g2R3A2Manifest.contactPositionToleranceMm,
                    signedDepthToleranceMm = g2R3A2Manifest.signedDepthToleranceMm,
                    repeatRenders = g2R3A2Manifest.repeatRenders
                };
                g2R3A2Complete = false;
                active2DRoutine = StartCoroutine(RunG2R3A2());
                return true;
            }
            catch (Exception exception)
            {
                g2R3A2Report = g2R3A2Report ?? new G2R3A2UnityReport();
                g2R3A2Report.infrastructureFailure = exception.Message;
                g2R3A2Complete = true;
                Debug.LogError("[ThoracicUSCoach][G2-R3-A2] " + exception);
                return false;
            }
        }

        private IEnumerator RunG2R3A2()
        {
            IEnumerator cases = RunG2R3A2Cases();
            try
            {
                while (true)
                {
                    object current;
                    try
                    {
                        if (!cases.MoveNext()) break;
                        current = cases.Current;
                    }
                    catch (Exception exception)
                    {
                        g2R3A2Report.infrastructureFailure = exception.ToString();
                        break;
                    }
                    yield return current;
                }
                g2R3A2Report.complete = string.IsNullOrEmpty(g2R3A2Report.infrastructureFailure);
            }
            finally
            {
                if (!g2R3A2Report.complete && string.IsNullOrEmpty(g2R3A2Report.infrastructureFailure))
                    g2R3A2Report.infrastructureFailure = "G2-R3-A2 coroutine ended before completion";
                SaveG2R3A2Report(g2R3A2Report.complete);
                g2R3A2Complete = true;
                active2DRoutine = null;
            }
        }

        private IEnumerator RunG2R3A2Cases()
        {
            foreach (G2R3A2Case selected in g2R3A2Manifest.cases)
            {
                G2R3A2CaseResult result = new G2R3A2CaseResult
                {
                    caseId = selected.caseId,
                    family = selected.family,
                    sourceFrameIndex = selected.sourceFrameIndex,
                    selectionReason = selected.selectionReason,
                    sourceImage = selected.sourceImage,
                    sourceImageSha256 = selected.sourceImageSha256,
                    originalScore = selected.originalScore,
                    originalAcceptable = selected.originalAcceptable
                };
                g2R3A2Report.cases.Add(result);
                yield return CaptureG2R3A2Case(selected, result);
                SaveG2R3A2Report(false);
            }
        }

        private IEnumerator CaptureG2R3A2Case(G2R3A2Case selected, G2R3A2CaseResult result)
        {
            probeRoot.SetPositionAndRotation(selected.probePosition, selected.probeRotation);
            seedProbeRotation = selected.probeRotation;
            active2DCaudalAxis = -selected.actuatorCranialAxis.normalized;
            active2DLateralAxis = selected.actuatorLateralAxis.normalized;
            caudalTangent = active2DCaudalAxis;
            active2DWorkspaceOrigin = selected.workspaceOrigin;
            active2DLongitudinalHalfExtentMm = selected.longitudinalHalfExtentMm;
            active2DLateralHalfExtentMm = selected.lateralHalfExtentMm;
            Physics.SyncTransforms();
            result.rigidContactApplied = EnforceRigidContact();
            Physics.SyncTransforms();
            yield return null;

            float initialPositionError = Vector3.Distance(selected.probePosition, probeRoot.position);
            float initialRotationError = Quaternion.Angle(selected.probeRotation, probeRoot.rotation);
            float initialContactError = Vector3.Distance(selected.contactPosition, faceCenter.position);
            float initialDepthError = Mathf.Abs(selected.signedDepthMm - (skinIntersection != null ? skinIntersection.SignedDepth : 0f));
            bool initialContact = IsCurrentContactValid();
            bool initialWorkspace = IsActive2DWorkspaceValid(faceCenter.position);
            result.restorationValid = result.rigidContactApplied
                && initialPositionError <= g2R3A2Manifest.positionToleranceMm
                && initialRotationError <= g2R3A2Manifest.rotationToleranceDeg
                && initialContactError <= g2R3A2Manifest.contactPositionToleranceMm
                && initialDepthError <= g2R3A2Manifest.signedDepthToleranceMm
                && initialContact && initialWorkspace;
            if (!result.restorationValid)
            {
                result.invalidReason = "restore mismatch: position=" + initialPositionError.ToString("R", CultureInfo.InvariantCulture)
                    + " rotation=" + initialRotationError.ToString("R", CultureInfo.InvariantCulture)
                    + " contact=" + initialContactError.ToString("R", CultureInfo.InvariantCulture)
                    + " depth=" + initialDepthError.ToString("R", CultureInfo.InvariantCulture)
                    + " contactValid=" + initialContact + " workspaceValid=" + initialWorkspace;
                yield break;
            }

            string caseDirectory = Path.Combine(g2R3A2OutputRoot, selected.caseId);
            Directory.CreateDirectory(caseDirectory);
            for (int repeat = 0; repeat < g2R3A2Manifest.repeatRenders; repeat++)
            {
                Texture2D image = CaptureCleanImage();
                if (image == null) throw new InvalidOperationException("Clean renderer returned null for " + selected.caseId);
                byte[] bytes = image.EncodeToPNG();
                string filename = "repeat_" + repeat.ToString("D2", CultureInfo.InvariantCulture) + "_us.png";
                string absolute = Path.Combine(caseDirectory, filename);
                File.WriteAllBytes(absolute, bytes);
                ScoreBreakdown score = ScoreCandidate(image, SampleAnatomyLabels(labelGridSize));
                Destroy(image);
                float positionError = Vector3.Distance(selected.probePosition, probeRoot.position);
                float rotationError = Quaternion.Angle(selected.probeRotation, probeRoot.rotation);
                float contactError = Vector3.Distance(selected.contactPosition, faceCenter.position);
                float depth = skinIntersection != null ? skinIntersection.SignedDepth : 0f;
                result.observations.Add(new G2R3A2Observation
                {
                    repeatIndex = repeat,
                    image = ProjectRelativePath(absolute),
                    imageSha256 = Sha256Bytes(bytes),
                    actualProbePosition = probeRoot.position,
                    actualProbeRotation = probeRoot.rotation,
                    actualContactPosition = faceCenter.position,
                    actualSignedDepthMm = depth,
                    positionErrorMm = positionError,
                    rotationErrorDeg = rotationError,
                    contactPositionErrorMm = contactError,
                    signedDepthErrorMm = Mathf.Abs(selected.signedDepthMm - depth),
                    contactValid = IsCurrentContactValid(),
                    workspaceValid = IsActive2DWorkspaceValid(faceCenter.position),
                    score = score,
                    targetAcceptable = score.total >= g2R3A2Manifest.acceptableScoreFloor,
                    scoreDeltaFromOriginal = score.total - selected.originalScore
                });
                yield return null;
            }
        }

        private void ValidateG2R3A2Manifest(G2R3A2Manifest manifest)
        {
            if (manifest == null || manifest.cases == null || manifest.cases.Count < 2 || manifest.cases.Count > 16)
                throw new InvalidOperationException("G2-R3-A2 requires 2..16 predeclared unique poses");
            if (manifest.cases.Select(item => item.caseId).Distinct().Count() != manifest.cases.Count)
                throw new InvalidOperationException("G2-R3-A2 case IDs must be unique");
            if (manifest.scene != SceneManager.GetActiveScene().path || manifest.unityVersion != Application.unityVersion)
                throw new InvalidOperationException("G2-R3-A2 scene or Unity version mismatch");
            if (manifest.repeatRenders < 1 || manifest.repeatRenders > 6)
                throw new InvalidOperationException("G2-R3-A2 repeat count must be in [1,6]");
            if (manifest.positionToleranceMm <= 0f || manifest.positionToleranceMm > 0.05f
                || manifest.rotationToleranceDeg <= 0f || manifest.rotationToleranceDeg > 0.05f)
                throw new InvalidOperationException("G2-R3-A2 pose tolerances exceed the existing U08 0.05 contract");
            if (manifest.acceptableScoreFloor <= 0f || manifest.acceptableScoreFloor >= 1f)
                throw new InvalidOperationException("G2-R3-A2 score floor is invalid");
            if (!manifest.cases.Any(item => item.family == "train_family_0090" && item.sourceFrameIndex == 100)
                || !manifest.cases.Any(item => item.family == "train_family_0090" && item.sourceFrameIndex == 101))
                throw new InvalidOperationException("G2-R3-A2 mandatory family 0090 cases missing");
            ResolveProjectPath(manifest.outputDirectory);
        }

        private string HashG2R3A2ProjectFile(string relativePath)
        {
            return Sha256Bytes(File.ReadAllBytes(ResolveProjectPath(relativePath)));
        }

        private void SaveG2R3A2Report(bool final)
        {
            if (g2R3A2Report == null || string.IsNullOrEmpty(g2R3A2OutputRoot)) return;
            g2R3A2Report.generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            string path = Path.Combine(g2R3A2OutputRoot, "g2_r3_a2_unity_report.json");
            File.WriteAllText(path, JsonUtility.ToJson(g2R3A2Report, true));
            if (final) Debug.Log("[ThoracicUSCoach][G2-R3-A2] report=" + ProjectRelativePath(path));
        }
    }
}
