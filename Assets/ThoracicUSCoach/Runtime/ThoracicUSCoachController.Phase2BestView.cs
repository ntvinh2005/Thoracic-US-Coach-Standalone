#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace CSSALT.ThoracicUSCoach
{
    public sealed partial class ThoracicUSCoachController
    {
        private const string Phase2BestViewContract = "thoracic_phase2_best_view_score_v1";
        private const string Phase2BestViewModel =
            "Artifacts/ThoracicUSCoach/active2d/phase2_best_view_20261103/tilted_transverse_v5/models/phase2_best_view_adapted_global.joblib";
        private const float Phase2CoarseCranialMaximumMm = 12f;
        private const float Phase2CoarseCaudalMaximumMm = 6f;
        private const float Phase2CoarseRightMaximumMm = 27f;
        private const float Phase2FineLongitudinalHalfMm = 3f;
        private const float Phase2FineLateralHalfMm = 3f;
        private const float Phase2FineYawHalfDeg = 15f;
        private const float Phase2SearchStepMm = 1.5f;
        private const float Phase2AcquisitionTiltDeg = 3f;
        private const float Phase2CoordinateToleranceMm = 0.15f;

        [Serializable]
        private sealed class Phase2BestViewRequest
        {
            public string contract = Phase2BestViewContract;
            public string requestId;
            public string imagePath;
        }

        [Serializable]
        private sealed class Phase2BestViewResponse
        {
            public string contract;
            public string requestId;
            public string modelSchema;
            public string featureMode;
            public float threshold;
            public float latencyMs;
            public string error;
            public float validityProbability;
            public float qualityScore;
            public bool accepted;
        }

        [Serializable]
        private sealed class Phase2BestViewObservation
        {
            public int index;
            public string stage;
            public float longitudinalMm;
            public float lateralRightMm;
            public float yawOffsetDeg;
            public float tiltOffsetDeg;
            public string image;
            public string imageSha256;
            public long poseEpoch;
            public long producerScanSerial;
            public float validityProbability;
            public float qualityScore;
            public bool accepted;
        }

        [Serializable]
        private sealed class Phase2BestViewMovement
        {
            public string stage;
            public string action;
            public bool accepted;
            public float requestedMm;
            public float achievedMm;
            public float achievedRotationDeg;
            public bool rollbackAttempted;
            public bool contactValid;
            public bool workspaceValid;
            public string rejectionReason;
        }

        [Serializable]
        private sealed class Phase2BestViewActorResult
        {
            public string contract = "thoracic_phase1_phase2_full_pipeline_actor_v1";
            public string status;
            public string failureReason;
            public string phaseOneMap;
            public float predictedT7RelativeMm;
            public float coarseCranialMaximumMm = Phase2CoarseCranialMaximumMm;
            public float coarseCaudalMaximumMm = Phase2CoarseCaudalMaximumMm;
            public float coarseRightMaximumMm = Phase2CoarseRightMaximumMm;
            public float fineLongitudinalHalfMm = Phase2FineLongitudinalHalfMm;
            public float fineLateralHalfMm = Phase2FineLateralHalfMm;
            public float fineYawHalfDeg = Phase2FineYawHalfDeg;
            public float acquisitionTiltDeg = Phase2AcquisitionTiltDeg;
            public List<Phase2BestViewMovement> movements = new();
            public List<Phase2BestViewObservation> observations = new();
            public int bestObservationIndex = -1;
            public float bestLongitudinalMm;
            public float bestLateralRightMm;
            public float bestYawOffsetDeg;
            public float bestValidityProbability;
            public float bestQualityScore;
            public bool freshConfirmationPass;
            public string finalUltrasound;
        }

        [Serializable]
        private sealed class Phase2BestViewEvaluatorResult
        {
            public string contract = "thoracic_phase2_best_view_evaluator_v1";
            public string actorResultSha256;
            public float t7MarkerDistanceMm;
            public bool t7LevelAcceptable;
            public float finalTargetScore;
            public float acceptableScoreFloor;
            public bool finalTargetAcceptable;
            public bool overallPass;
            public List<Phase2BestViewEvaluatorObservation> observations = new();
        }

        [Serializable]
        private sealed class Phase2BestViewEvaluatorObservation
        {
            public int index;
            public string stage;
            public float longitudinalMm;
            public float lateralRightMm;
            public float yawOffsetDeg;
            public float tiltOffsetDeg;
            public Vector3 probePosition;
            public Quaternion probeRotation;
            public Vector3 contactPoint;
            public float t7MarkerDistanceMm;
            public bool t7LevelAcceptable;
            public float targetScore;
        }

        private Coroutine phase2BestViewRoutine;
        private Process phase2BestViewServer;
        private string phase2BestViewRoot = string.Empty;
        private string phase2BestViewBridge = string.Empty;
        private string phase2BestViewState = "NOT RUN";
        private string phase2BestViewMessage = string.Empty;
        private Phase2BestViewActorResult phase2BestViewResult;
        private float phase2CurrentLongitudinalMm;
        private float phase2CurrentLateralMm;
        private float phase2CurrentYawDeg;
        private float phase2CurrentTiltDeg;
        private bool phase2MoveFailed;
        private readonly List<Phase2BestViewEvaluatorObservation>
            phase2BestViewEvaluatorObservations = new();

        private void InitializePhase2BestView()
        {
            phase2BestViewState = "READY";
            phase2BestViewMessage = "Waiting for a valid Phase-1 T7 map.";
        }

        private void DestroyPhase2BestView()
        {
            StopPhase2BestViewServer();
        }

        public static List<Vector3> BuildPhase2CoarseTargets()
        {
            var targets = new List<Vector3>();
            float[] longitudinal = { -12f, -9f, -6f, -3f, 0f, 3f, 6f };
            for (int row = 0; row < longitudinal.Length; row++)
            {
                IEnumerable<int> lateralIndices = row % 2 == 0
                    ? Enumerable.Range(0, 10)
                    : Enumerable.Range(0, 10).Reverse();
                foreach (int lateralIndex in lateralIndices)
                    targets.Add(new Vector3(longitudinal[row], lateralIndex * 3f, 0f));
            }
            return targets;
        }

        public static List<Vector3> BuildPhase2FineTargets(Vector3 best)
        {
            var targets = new List<Vector3>();
            float[] longitudinalOffsets = { -1f, -0.5f, 0f, 0.5f, 1f };
            float[] lateralOffsets = { -1f, -0.5f, 0f, 0.5f, 1f };
            float[] yawOffsets =
                { -15f, -12f, -9f, -6f, -3f, 0f, 3f, 6f, 9f, 12f, 15f };
            foreach (float longitudinal in longitudinalOffsets)
                foreach (float lateral in lateralOffsets)
                    foreach (float yaw in yawOffsets)
                        targets.Add(new Vector3(
                            Mathf.Clamp(best.x + longitudinal * Phase2FineLongitudinalHalfMm,
                                -Phase2CoarseCranialMaximumMm, Phase2CoarseCaudalMaximumMm),
                            Mathf.Clamp(best.y + lateral * Phase2FineLateralHalfMm,
                                0f, Phase2CoarseRightMaximumMm),
                            Mathf.Clamp(best.z + yaw,
                                -Phase2FineYawHalfDeg, Phase2FineYawHalfDeg)));
            var distinct = new List<Vector3>();
            foreach (Vector3 target in targets)
                if (!distinct.Any(existing => Vector3.Distance(existing, target) < 0.05f))
                    distinct.Add(target);
            return distinct;
        }

        public static List<Vector3> BuildPhase2MicroTargets(Vector3 best)
        {
            var targets = new List<Vector3>();
            float[] longitudinalOffsets = { -0.75f, -0.375f, 0f, 0.375f, 0.75f };
            float[] lateralOffsets = { -1.5f, -0.75f, 0f, 0.75f, 1.5f };
            float[] yawOffsets = { -3f, 0f, 3f };
            foreach (float longitudinal in longitudinalOffsets)
                foreach (float lateral in lateralOffsets)
                    foreach (float yaw in yawOffsets)
                        targets.Add(new Vector3(
                            Mathf.Clamp(best.x + longitudinal,
                                -Phase2CoarseCranialMaximumMm, Phase2CoarseCaudalMaximumMm),
                            Mathf.Clamp(best.y + lateral,
                                0f, Phase2CoarseRightMaximumMm),
                            Mathf.Clamp(best.z + yaw,
                                -Phase2FineYawHalfDeg, Phase2FineYawHalfDeg)));
            var distinct = new List<Vector3>();
            foreach (Vector3 target in targets)
                if (!distinct.Any(existing => Vector3.Distance(existing, target) < 0.05f))
                    distinct.Add(target);
            return distinct;
        }

        public static List<Vector3> BuildPhase2BoundaryRefinementTargets()
        {
            var targets = new List<Vector3>();
            float[] longitudinal = { -3f, -1.5f, 0f, 1.5f, 3f };
            float[] lateral = { 24f, 25.5f, 27f };
            float[] yaw = { 0f, 3f, 6f, 9f, 12f, 15f };
            foreach (float longitudinalMm in longitudinal)
                foreach (float lateralMm in lateral)
                    foreach (float yawDeg in yaw)
                        targets.Add(new Vector3(
                            longitudinalMm, lateralMm, yawDeg));
            return targets;
        }

        public static Vector3 BuildPhase2MechanicalRightAxis(Vector3 surfaceNormal)
        {
            Vector3 right = Vector3.ProjectOnPlane(Vector3.right, surfaceNormal).normalized;
            if (right.sqrMagnitude < 0.5f)
                right = Vector3.ProjectOnPlane(Vector3.forward, surfaceNormal).normalized;
            return right;
        }

        public static bool HasPhase2FreshConfirmation(int confirmationPasses)
        {
            return confirmationPasses >= 1;
        }

        private bool StartPhase2BestViewFromPhaseOne()
        {
            if (phase2BestViewRoutine != null || randomStartDemoMap == null) return false;
            if (!ValidateRandomStartLandmarkDemoMap(randomStartDemoMap, out string reason))
            {
                phase2BestViewState = "ABSTAINED";
                phase2BestViewMessage = reason;
                return false;
            }
            phase2BestViewRoutine = StartCoroutine(RunPhase2BestViewGuarded());
            return true;
        }

        public bool StartPhase2BestViewFromPersistedPhaseOneMap(
            string relativeMapPath, string outputRoot)
        {
            if (!Application.isPlaying || phase2BestViewRoutine != null
                || string.IsNullOrWhiteSpace(relativeMapPath)
                || string.IsNullOrWhiteSpace(outputRoot)) return false;
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (string.IsNullOrEmpty(projectRoot)) return false;
            string mapPath = Path.IsPathRooted(relativeMapPath)
                ? relativeMapPath : Path.Combine(projectRoot,
                    relativeMapPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(mapPath)) return false;
            RandomStartLandmarkDemoMap map = JsonUtility.FromJson<RandomStartLandmarkDemoMap>(
                File.ReadAllText(mapPath));
            if (!ValidateRandomStartLandmarkDemoMap(map, out string reason))
            {
                phase2BestViewState = "ABSTAINED";
                phase2BestViewMessage = reason;
                return false;
            }
            randomStartDemoMap = map;
            randomStartDemoMapPath = mapPath;
            randomStartDemoOutputRoot = outputRoot;
            ResetG2R2ContinuousExperimentConfiguration();
            ConfigureGroundTruthFreeThoracicExploration(
                outputRoot, 89.5f, 45, -53.5f);
            ConfigureGroundTruthFreeEpisodeHomeJitter(20261119, 12f);
            ConfigureGroundTruthFreeEpisodeHomeLateralJitter(20261108, 1f);
            G2R2AcquisitionSetup setup = BuildG2R2GroundTruthFreeSetup();
            sweepCountYawDeg = 89.5f;
            if (!PlaceSweepCountProbe(setup.homeHit.point, setup.homeHit.normal,
                    setup.corridorAxis, out string placementFailure))
            {
                phase2BestViewState = "ABSTAINED";
                phase2BestViewMessage = placementFailure;
                ResetG2R2ContinuousExperimentConfiguration();
                return false;
            }
            return StartPhase2BestViewFromPhaseOne();
        }

        private IEnumerator RunPhase2BestViewGuarded()
        {
            Exception failure = null;
            var stack = new Stack<IEnumerator>();
            stack.Push(RunPhase2BestViewCore());
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
                    continue;
                }
                if (yielded is IEnumerator nested) stack.Push(nested);
                else yield return yielded;
            }
            StopPhase2BestViewServer();
            if (failure != null)
            {
                phase2BestViewResult ??= new Phase2BestViewActorResult();
                phase2BestViewResult.status = "ABSTAINED_OR_FAILED";
                phase2BestViewResult.failureReason = failure.Message;
                if (!string.IsNullOrEmpty(phase2BestViewRoot))
                {
                    Directory.CreateDirectory(phase2BestViewRoot);
                    string actorPath = Path.Combine(phase2BestViewRoot, "actor_result.json");
                    File.WriteAllText(actorPath, JsonUtility.ToJson(phase2BestViewResult, true));
                    WritePhase2BestViewEvaluatorResult(actorPath);
                }
                phase2BestViewState = "ABSTAINED";
                phase2BestViewMessage = failure.Message;
                Debug.LogError("[ThoracicUSCoach][Phase2BestView] " + failure);
            }
            randomStartDemoRunActive = false;
            randomStartDemoAnalysisOnly = false;
            ResetG2R2ContinuousExperimentConfiguration();
            phase2BestViewRoutine = null;
        }

        private IEnumerator RunPhase2BestViewCore()
        {
            phase2BestViewResult = new Phase2BestViewActorResult
            {
                status = "RUNNING",
                phaseOneMap = ProjectRelativePath(randomStartDemoMapPath),
                predictedT7RelativeMm = randomStartDemoMap.predictedT7RelativeMm
            };
            phase2BestViewEvaluatorObservations.Clear();
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            phase2BestViewRoot = Path.Combine(Path.GetFullPath(Path.Combine(
                projectRoot, randomStartDemoOutputRoot)), "phase2_best_view");
            phase2BestViewBridge = Path.Combine(phase2BestViewRoot, "bridge");
            Directory.CreateDirectory(Path.Combine(phase2BestViewRoot, "frames"));
            Directory.CreateDirectory(Path.Combine(phase2BestViewRoot, "evaluator_only"));
            string modelPath = Path.Combine(projectRoot,
                Phase2BestViewModel.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(modelPath))
                throw new FileNotFoundException("Frozen Phase-2 model is missing", modelPath);
            StartPhase2BestViewServer(modelPath);
            yield return null;

            phase2MoveFailed = false;
            if (!EnforceRigidContact())
                throw new InvalidOperationException(
                    "Phase-2 setup could not establish valid rigid skin contact");
            Vector3 normal = CurrentSurfaceNormal();
            active2DCaudalAxis = Vector3.ProjectOnPlane(Vector3.back, normal).normalized;
            if (active2DCaudalAxis.sqrMagnitude < 0.5f)
                active2DCaudalAxis = Vector3.ProjectOnPlane(Vector3.down, normal).normalized;
            active2DLateralAxis = BuildPhase2MechanicalRightAxis(normal);
            active2DWorkspaceOrigin = faceCenter.position;
            active2DLongitudinalHalfExtentMm = 70f;
            active2DLateralHalfExtentMm = 30f;

            phase2BestViewState = "MOVE TO PREDICTED T7";
            phase2BestViewMessage = string.Format(CultureInfo.InvariantCulture,
                "Physical handoff target {0:+0.000;-0.000;0.000} mm from Home.",
                randomStartDemoMap.predictedT7RelativeMm);
            yield return MovePhase2LongitudinalHandoff(randomStartDemoMap.predictedT7RelativeMm);
            RequirePhase2MovementSuccess();

            active2DWorkspaceOrigin = faceCenter.position;
            active2DLongitudinalHalfExtentMm = Phase2CoarseCranialMaximumMm + 2f;
            active2DLateralHalfExtentMm = Phase2CoarseRightMaximumMm + 2f;
            phase2CurrentLongitudinalMm = 0f;
            phase2CurrentLateralMm = 0f;
            phase2CurrentYawDeg = 0f;
            phase2CurrentTiltDeg = 0f;
            phase2BestViewState = "ROTATE TRANSVERSE";
            phase2BestViewMessage = "Physically rotating from longitudinal to transverse.";
            for (int index = 0; index < 30; index++)
            {
                if (!ExecutePhase2Action(
                        Active2DAction.RotateCounterClockwise, "rotate_transverse"))
                    break;
                if (active2DLastActionExecution.rollbackAttempted) phase2MoveFailed = true;
                yield return null;
            }
            RequirePhase2MovementSuccess();
            if (!ExecutePhase2Tilt(Phase2AcquisitionTiltDeg, "tilt_transverse"))
                phase2MoveFailed = true;
            RequirePhase2MovementSuccess();

            foreach (Vector3 target in BuildPhase2CoarseTargets())
            {
                phase2BestViewState = "PHASE 2 COARSE";
                yield return MovePhase2To(target, "coarse");
                RequirePhase2MovementSuccess();
                yield return CaptureAndScorePhase2("coarse");
                RequirePhase2MovementSuccess();
            }
            foreach (Vector3 target in BuildPhase2BoundaryRefinementTargets())
            {
                phase2BestViewState = "PHASE 2 FINE";
                yield return MovePhase2To(target, "fine");
                RequirePhase2MovementSuccess();
                yield return CaptureAndScorePhase2("fine");
                RequirePhase2MovementSuccess();
            }
            Vector3 microBest = phase2BestViewResult.bestObservationIndex >= 0
                ? BestPhase2Coordinate() : BestPhase2SearchProposalCoordinate();
            foreach (Vector3 target in BuildPhase2MicroTargets(microBest))
            {
                phase2BestViewState = "PHASE 2 MICRO";
                yield return MovePhase2To(target, "micro");
                RequirePhase2MovementSuccess();
                yield return CaptureAndScorePhase2("micro");
                RequirePhase2MovementSuccess();
            }
            if (phase2BestViewResult.bestObservationIndex < 0)
                throw new InvalidOperationException(
                    "Coarse and fine search found no image-only valid T7-right candidate");

            phase2BestViewState = "RETURN BEST";
            yield return MovePhase2To(BestPhase2Coordinate(), "return_best");
            RequirePhase2MovementSuccess();

            phase2BestViewState = "FRESH CONFIRM";
            int confirmationPasses = 0;
            Phase2BestViewObservation acceptedConfirmation = null;
            float minimumQuality = phase2BestViewResult.bestQualityScore - 0.08f;
            for (int index = 0; index < 2; index++)
            {
                yield return CaptureAndScorePhase2("fresh_confirmation");
                RequirePhase2MovementSuccess();
                Phase2BestViewObservation observation =
                    phase2BestViewResult.observations.Last();
                if (observation.accepted && observation.qualityScore >= minimumQuality)
                {
                    confirmationPasses++;
                    acceptedConfirmation = observation;
                }
            }
            if (!HasPhase2FreshConfirmation(confirmationPasses))
                throw new InvalidOperationException(
                    "Returned best pose produced no fresh image-only confirmation");

            phase2BestViewResult.freshConfirmationPass = true;
            phase2BestViewResult.finalUltrasound = acceptedConfirmation.image;
            phase2BestViewResult.status = "ACTOR_SUCCESS_FROZEN";
            string actorPath = Path.Combine(phase2BestViewRoot, "actor_result.json");
            File.WriteAllText(actorPath, JsonUtility.ToJson(phase2BestViewResult, true));

            Phase2BestViewEvaluatorResult evaluator =
                WritePhase2BestViewEvaluatorResult(actorPath);
            phase2BestViewState = evaluator.overallPass ? "COMPLETE" : "EVALUATOR FAIL";
            phase2BestViewMessage = evaluator.overallPass
                ? "Full Phase 1 to Phase 2 pipeline passed this development episode."
                : "Actor completed, but independent Unity evaluation rejected the final view.";
        }

        private Phase2BestViewEvaluatorResult WritePhase2BestViewEvaluatorResult(
            string actorPath)
        {
            ScoreBreakdown evaluatorScore = ScoreCandidate(
                CaptureCleanImage(), SampleAnatomyLabels(labelGridSize));
            float floor = discoveryAcceptableScoreFloor;
            if (floor <= 0f && TryLoadGuided2DCandidate(
                    out CandidateRecord _, out float loadedFloor)) floor = loadedFloor;
            float t7Distance = t7RightSeed != null
                ? Vector3.Distance(faceCenter.position, t7RightSeed.position)
                : float.PositiveInfinity;
            var evaluator = new Phase2BestViewEvaluatorResult
            {
                actorResultSha256 = Sha256Phase2File(actorPath),
                t7MarkerDistanceMm = t7Distance,
                t7LevelAcceptable = t7RightSeed != null
                    && t7Distance <= Active2DT7PreciseRadiusMm(),
                finalTargetScore = evaluatorScore.total,
                acceptableScoreFloor = floor,
                finalTargetAcceptable = floor > 0f && evaluatorScore.total >= floor
            };
            evaluator.observations.AddRange(phase2BestViewEvaluatorObservations);
            evaluator.overallPass = evaluator.t7LevelAcceptable
                && evaluator.finalTargetAcceptable
                && phase2BestViewResult.freshConfirmationPass;
            string evaluatorRoot = Path.Combine(phase2BestViewRoot, "evaluator_only");
            Directory.CreateDirectory(evaluatorRoot);
            File.WriteAllText(Path.Combine(evaluatorRoot, "final_evaluation.json"),
                JsonUtility.ToJson(evaluator, true));
            return evaluator;
        }

        private Vector3 BestPhase2Coordinate()
        {
            return new Vector3(
                phase2BestViewResult.bestLongitudinalMm,
                phase2BestViewResult.bestLateralRightMm,
                phase2BestViewResult.bestYawOffsetDeg);
        }

        private Vector3 BestPhase2SearchProposalCoordinate()
        {
            Phase2BestViewObservation proposal = phase2BestViewResult.observations
                .OrderByDescending(item => item.validityProbability)
                .ThenByDescending(item => item.qualityScore)
                .First();
            return new Vector3(
                proposal.longitudinalMm, proposal.lateralRightMm, proposal.yawOffsetDeg);
        }

        private List<Vector3> Phase2FineSearchSeeds()
        {
            var seeds = new List<Vector3>
            {
                phase2BestViewResult.bestObservationIndex >= 0
                    ? BestPhase2Coordinate() : BestPhase2SearchProposalCoordinate()
            };
            Phase2BestViewObservation qualityProposal = phase2BestViewResult.observations
                .Where(item => item.stage == "coarse")
                .OrderByDescending(item => item.qualityScore)
                .ThenByDescending(item => item.validityProbability)
                .First();
            Vector3 qualitySeed = new Vector3(
                qualityProposal.longitudinalMm,
                qualityProposal.lateralRightMm,
                qualityProposal.yawOffsetDeg);
            if (!seeds.Any(existing => Vector3.Distance(existing, qualitySeed) < 0.05f))
                seeds.Add(qualitySeed);
            return seeds;
        }

        private IEnumerator MovePhase2LongitudinalHandoff(float targetMm)
        {
            phase2MoveFailed = false;
            float current = 0f;
            while (Mathf.Abs(targetMm - current) > 0.05f)
            {
                float delta = targetMm - current;
                float originalStep = active2DStepMm;
                active2DStepMm = Mathf.Min(Phase2SearchStepMm, Mathf.Abs(delta));
                Active2DAction action = delta > 0f
                    ? Active2DAction.Caudal : Active2DAction.Cranial;
                BeginActive2DActionExecution(action);
                bool accepted = TryActive2DMove(
                    delta > 0f ? active2DCaudalAxis : -active2DCaudalAxis, 89.5f);
                active2DStepMm = originalStep;
                RecordPhase2Movement("phase1_t7_handoff", action, accepted);
                if (!accepted || active2DLastActionExecution.rollbackAttempted)
                {
                    phase2MoveFailed = true;
                    yield break;
                }
                current += Mathf.Sign(delta) * active2DLastActionExecution.achievedDisplacementMm;
                yield return null;
            }
        }

        private IEnumerator MovePhase2To(Vector3 target, string stage)
        {
            phase2MoveFailed = false;
            while (Mathf.Abs(target.x - phase2CurrentLongitudinalMm)
                    > Phase2CoordinateToleranceMm
                || Mathf.Abs(target.y - phase2CurrentLateralMm)
                    > Phase2CoordinateToleranceMm
                || Mathf.Abs(target.z - phase2CurrentYawDeg) > 0.05f)
            {
                Active2DAction action;
                if (Mathf.Abs(target.x - phase2CurrentLongitudinalMm)
                    > Phase2CoordinateToleranceMm)
                    action = target.x > phase2CurrentLongitudinalMm
                        ? Active2DAction.Caudal : Active2DAction.Cranial;
                else if (Mathf.Abs(target.y - phase2CurrentLateralMm)
                    > Phase2CoordinateToleranceMm)
                    action = target.y > phase2CurrentLateralMm
                        ? Active2DAction.Lateral : Active2DAction.Medial;
                else
                    action = target.z > phase2CurrentYawDeg
                        ? Active2DAction.RotateClockwise
                        : Active2DAction.RotateCounterClockwise;
                float originalStep = active2DStepMm;
                if (action == Active2DAction.Caudal || action == Active2DAction.Cranial)
                    active2DStepMm = Mathf.Min(Phase2SearchStepMm,
                        Mathf.Abs(target.x - phase2CurrentLongitudinalMm));
                else if (action == Active2DAction.Lateral || action == Active2DAction.Medial)
                    active2DStepMm = Mathf.Min(3f,
                        Mathf.Abs(target.y - phase2CurrentLateralMm));
                bool accepted = ExecutePhase2Action(action, stage);
                active2DStepMm = originalStep;
                if (!accepted || active2DLastActionExecution.rollbackAttempted)
                {
                    phase2MoveFailed = true;
                    yield break;
                }
                float achieved = active2DLastActionExecution.achievedDisplacementMm;
                if (action == Active2DAction.Caudal) phase2CurrentLongitudinalMm += achieved;
                else if (action == Active2DAction.Cranial) phase2CurrentLongitudinalMm -= achieved;
                else if (action == Active2DAction.Lateral) phase2CurrentLateralMm += achieved;
                else if (action == Active2DAction.Medial) phase2CurrentLateralMm -= achieved;
                else if (action == Active2DAction.RotateClockwise)
                    phase2CurrentYawDeg += active2DLastActionExecution.achievedRotationDeg;
                else if (action == Active2DAction.RotateCounterClockwise)
                    phase2CurrentYawDeg -= active2DLastActionExecution.achievedRotationDeg;
                yield return null;
            }
        }

        private bool ExecutePhase2Action(Active2DAction action, string stage)
        {
            bool translation = action == Active2DAction.Cranial
                || action == Active2DAction.Caudal
                || action == Active2DAction.Medial
                || action == Active2DAction.Lateral;
            bool accepted;
            if (translation)
            {
                BeginActive2DActionExecution(action);
                Vector3 direction = action == Active2DAction.Cranial
                    ? -active2DCaudalAxis
                    : action == Active2DAction.Caudal
                        ? active2DCaudalAxis
                        : action == Active2DAction.Medial
                            ? -active2DLateralAxis
                            : active2DLateralAxis;
                accepted = TryPhase2Move(direction);
            }
            else
            {
                accepted = TryActive2DAction(action);
            }
            RecordPhase2Movement(stage, action, accepted);
            return accepted;
        }

        private bool TryPhase2Move(Vector3 referenceDirection)
        {
            Vector3 oldPosition = probeRoot.position;
            Quaternion oldRotation = probeRoot.rotation;
            Vector3 oldContact = faceCenter.position;
            Vector3 normal = CurrentSurfaceNormal();
            Vector3 direction = Vector3.ProjectOnPlane(referenceDirection, normal).normalized;
            if (direction.sqrMagnitude < 0.5f)
                return RejectActive2DActionWithoutPoseChange(
                    "Phase-2 movement tangent is degenerate");
            if (!FindSurfaceAt(oldContact + direction * active2DStepMm,
                    normal, out RaycastHit hit))
                return RejectActive2DActionWithoutPoseChange(
                    "Phase-2 surface raycast failed");
            caudalTangent = Vector3.ProjectOnPlane(active2DCaudalAxis, hit.normal).normalized;
            AlignProbeToSurface(
                hit, phase2CurrentYawDeg, phase2CurrentTiltDeg);
            if (!EnforceRigidContact())
            {
                active2DContactViolations++;
                active2DLastActionExecution.attemptedDisplacementMm =
                    Vector3.Distance(oldContact, faceCenter.position);
                return RejectActive2DActionAndRollback(
                    oldPosition, oldRotation, oldContact,
                    "Phase-2 contact enforcement failed");
            }
            float moved = Vector3.Distance(oldContact, faceCenter.position);
            active2DLastActionExecution.attemptedDisplacementMm = moved;
            if (!RefreshActive2DSafetyState(out string safetyReason))
            {
                if (!active2DLastActionExecution.contactValidAfterExecution)
                    active2DContactViolations++;
                if (!active2DLastActionExecution.workspaceValidAfterExecution)
                    active2DWorkspaceViolations++;
                return RejectActive2DActionAndRollback(
                    oldPosition, oldRotation, oldContact,
                    "Phase-2 candidate failed safety validation: " + safetyReason);
            }
            if (Thoracic2DNavigation.IsTeleportLike(moved, active2DStepMm, 2.5f))
                return RejectActive2DActionAndRollback(
                    oldPosition, oldRotation, oldContact,
                    "Phase-2 translation exceeded discontinuity allowance");
            if (!Thoracic2DNavigation.HasMeaningfulProgress(active2DStepMm, moved))
                return RejectActive2DActionAndRollback(
                    oldPosition, oldRotation, oldContact,
                    "Phase-2 translation made less than minimum meaningful progress");
            active2DLastActionExecution.accepted = true;
            active2DLastActionExecution.achievedDisplacementMm = moved;
            return true;
        }

        private bool ExecutePhase2Tilt(float angleDeg, string stage)
        {
            Vector3 oldPosition = probeRoot.position;
            Quaternion oldRotation = probeRoot.rotation;
            Vector3 oldContact = faceCenter.position;
            active2DLastActionExecution = new Active2DActionExecution
            {
                action = Active2DAction.Observe,
                requestedRotationDeg = angleDeg
            };
            probeRoot.RotateAround(oldContact, active2DLateralAxis.normalized, angleDeg);
            bool contactEnforced = EnforceRigidContact();
            float contactShift = Vector3.Distance(oldContact, faceCenter.position);
            active2DLastActionExecution.attemptedDisplacementMm = contactShift;
            active2DLastActionExecution.achievedDisplacementMm = contactShift;
            active2DLastActionExecution.achievedRotationDeg =
                Quaternion.Angle(oldRotation, probeRoot.rotation);
            bool safe = RefreshActive2DSafetyState(out string safetyReason);
            bool accepted = contactEnforced && safe && contactShift <= 2.5f;
            if (!accepted)
            {
                string reason = !contactEnforced
                    ? "tilt contact enforcement failed"
                    : !safe
                        ? "tilt candidate failed safety validation: " + safetyReason
                        : "tilt shifted contact beyond allowance";
                accepted = RejectActive2DActionAndRollback(
                    oldPosition, oldRotation, oldContact, reason);
            }
            else
            {
                active2DLastActionExecution.accepted = true;
                phase2CurrentTiltDeg += Mathf.Sign(angleDeg)
                    * active2DLastActionExecution.achievedRotationDeg;
            }
            phase2BestViewResult.movements.Add(new Phase2BestViewMovement
            {
                stage = stage,
                action = angleDeg >= 0f ? "TiltPositive" : "TiltNegative",
                accepted = accepted,
                requestedMm = 0f,
                achievedMm = active2DLastActionExecution.achievedDisplacementMm,
                achievedRotationDeg = active2DLastActionExecution.achievedRotationDeg,
                rollbackAttempted = active2DLastActionExecution.rollbackAttempted,
                contactValid = active2DLastActionExecution.contactValidAfterExecution,
                workspaceValid = active2DLastActionExecution.workspaceValidAfterExecution,
                rejectionReason = active2DLastActionExecution.rejectionReason
            });
            return accepted;
        }

        private void RecordPhase2Movement(
            string stage, Active2DAction action, bool accepted)
        {
            phase2BestViewResult.movements.Add(new Phase2BestViewMovement
            {
                stage = stage,
                action = action.ToString(),
                accepted = accepted,
                requestedMm = active2DLastActionExecution.requestedDisplacementMm,
                achievedMm = active2DLastActionExecution.achievedDisplacementMm,
                achievedRotationDeg = active2DLastActionExecution.achievedRotationDeg,
                rollbackAttempted = active2DLastActionExecution.rollbackAttempted,
                contactValid = active2DLastActionExecution.contactValidAfterExecution,
                workspaceValid = active2DLastActionExecution.workspaceValidAfterExecution,
                rejectionReason = active2DLastActionExecution.rejectionReason
            });
        }

        private void RequirePhase2MovementSuccess()
        {
            if (phase2MoveFailed)
                throw new InvalidOperationException(
                    "Phase-2 movement failed or attempted a rollback");
        }

        private IEnumerator CaptureAndScorePhase2(string stage)
        {
            phase2MoveFailed = false;
            int index = phase2BestViewResult.observations.Count;
            DirectFreshPoseToken token = BeginDirectFreshCapturePose(
                "phase2_best_view_" + stage + "_" + index.ToString("D4"), true);
            var capture = new DirectFreshCaptureResult();
            yield return CaptureDirectProducerFreshFrame(
                token, "phase2_best_view_" + stage, capture, true);
            string framePath = Path.Combine(phase2BestViewRoot, "frames",
                "frame_" + index.ToString("D4") + ".png");
            File.WriteAllBytes(framePath, capture.pngBytes);
            Phase2BestViewResponse response = null;
            yield return RequestPhase2BestViewScore(framePath, value => response = value);
            if (response == null || !string.IsNullOrEmpty(response.error))
            {
                phase2MoveFailed = true;
                yield break;
            }
            var observation = new Phase2BestViewObservation
            {
                index = index,
                stage = stage,
                longitudinalMm = phase2CurrentLongitudinalMm,
                lateralRightMm = phase2CurrentLateralMm,
                yawOffsetDeg = phase2CurrentYawDeg,
                tiltOffsetDeg = phase2CurrentTiltDeg,
                image = ProjectRelativePath(framePath),
                imageSha256 = Sha256Phase2File(framePath),
                poseEpoch = capture.poseEpoch,
                producerScanSerial = capture.producerScanSerial,
                validityProbability = response.validityProbability,
                qualityScore = response.qualityScore,
                accepted = response.accepted
            };
            phase2BestViewResult.observations.Add(observation);
            ScoreBreakdown evaluatorScore = ScoreCandidate(
                capture.image, SampleAnatomyLabels(labelGridSize));
            float t7Distance = t7RightSeed != null
                ? Vector3.Distance(faceCenter.position, t7RightSeed.position)
                : float.PositiveInfinity;
            phase2BestViewEvaluatorObservations.Add(
                new Phase2BestViewEvaluatorObservation
                {
                    index = index,
                    stage = stage,
                    longitudinalMm = phase2CurrentLongitudinalMm,
                    lateralRightMm = phase2CurrentLateralMm,
                    yawOffsetDeg = phase2CurrentYawDeg,
                    tiltOffsetDeg = phase2CurrentTiltDeg,
                    probePosition = probeRoot.position,
                    probeRotation = probeRoot.rotation,
                    contactPoint = faceCenter.position,
                    t7MarkerDistanceMm = t7Distance,
                    t7LevelAcceptable = t7RightSeed != null
                        && t7Distance <= Active2DT7PreciseRadiusMm(),
                    targetScore = evaluatorScore.total
                });
            if (observation.accepted
                && (phase2BestViewResult.bestObservationIndex < 0
                    || observation.qualityScore > phase2BestViewResult.bestQualityScore))
            {
                phase2BestViewResult.bestObservationIndex = observation.index;
                phase2BestViewResult.bestLongitudinalMm = observation.longitudinalMm;
                phase2BestViewResult.bestLateralRightMm = observation.lateralRightMm;
                phase2BestViewResult.bestYawOffsetDeg = observation.yawOffsetDeg;
                phase2BestViewResult.bestValidityProbability = observation.validityProbability;
                phase2BestViewResult.bestQualityScore = observation.qualityScore;
            }
            randomStartDemoMessage = string.Format(CultureInfo.InvariantCulture,
                "{0}: frame {1}, p={2:F3}, quality={3:F3}, best={4:F3}",
                stage, index, observation.validityProbability, observation.qualityScore,
                phase2BestViewResult.bestQualityScore);
        }

        private IEnumerator RequestPhase2BestViewScore(
            string imagePath, Action<Phase2BestViewResponse> setResponse)
        {
            string requestId = Guid.NewGuid().ToString("N");
            string requestPath = Path.Combine(
                phase2BestViewBridge, "phase2_best_view_request.json");
            string responsePath = Path.Combine(
                phase2BestViewBridge, "phase2_best_view_response.json");
            string temporary = requestPath + "." + requestId + ".tmp";
            File.WriteAllText(temporary, JsonUtility.ToJson(new Phase2BestViewRequest
            {
                requestId = requestId,
                imagePath = Path.GetFullPath(imagePath)
            }, true));
            IOException writeFailure = null;
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    if (File.Exists(requestPath))
                        File.Replace(temporary, requestPath, null, true);
                    else File.Move(temporary, requestPath);
                    writeFailure = null;
                    break;
                }
                catch (IOException exception)
                {
                    writeFailure = exception;
                    System.Threading.Thread.Sleep(10);
                }
            }
            if (writeFailure != null)
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                throw writeFailure;
            }
            float deadline = Time.realtimeSinceStartup + 20f;
            while (Time.realtimeSinceStartup < deadline)
            {
                if (phase2BestViewServer == null || phase2BestViewServer.HasExited)
                    yield break;
                if (File.Exists(responsePath))
                {
                    Phase2BestViewResponse response = null;
                    try
                    {
                        response = JsonUtility.FromJson<Phase2BestViewResponse>(
                            File.ReadAllText(responsePath));
                    }
                    catch (IOException) { }
                    catch (ArgumentException) { }
                    if (response != null && response.contract == Phase2BestViewContract
                        && response.requestId == requestId)
                    {
                        setResponse(response);
                        yield break;
                    }
                }
                yield return null;
            }
        }

        private void StartPhase2BestViewServer(string modelPath)
        {
            StopPhase2BestViewServer();
            Directory.CreateDirectory(phase2BestViewBridge);
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            var info = new ProcessStartInfo
            {
                FileName = randomStartDemoPythonExecutable,
                Arguments = string.Join(" ", new[]
                {
                    QuotePhase2PocArgument(Path.Combine(projectRoot, "Tools",
                        "ThoracicUSCoach", "run_phase2_best_view_server.py")),
                    "--model", QuotePhase2PocArgument(modelPath),
                    "--bridge", QuotePhase2PocArgument(phase2BestViewBridge)
                }),
                WorkingDirectory = Path.Combine(projectRoot, "Tools", "ThoracicUSCoach"),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            phase2BestViewServer = new Process { StartInfo = info };
            phase2BestViewServer.OutputDataReceived += (_, eventArgs) =>
            {
                if (!string.IsNullOrEmpty(eventArgs.Data))
                    Debug.Log("[ThoracicUSCoach][Phase2Server] " + eventArgs.Data);
            };
            phase2BestViewServer.ErrorDataReceived += (_, eventArgs) =>
            {
                if (!string.IsNullOrEmpty(eventArgs.Data))
                    Debug.LogError("[ThoracicUSCoach][Phase2Server] " + eventArgs.Data);
            };
            if (!phase2BestViewServer.Start())
                throw new InvalidOperationException("Could not start the Phase-2 actor server");
            phase2BestViewServer.BeginOutputReadLine();
            phase2BestViewServer.BeginErrorReadLine();
        }

        private void StopPhase2BestViewServer()
        {
            Process process = phase2BestViewServer;
            phase2BestViewServer = null;
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

        private static string Sha256Phase2File(string path)
        {
            using var stream = File.OpenRead(path);
            using var sha = System.Security.Cryptography.SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(stream))
                .Replace("-", string.Empty).ToLowerInvariant();
        }
    }
}
#endif
