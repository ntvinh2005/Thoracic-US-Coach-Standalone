using System;
using System.Collections.Generic;
using UnityEngine;

namespace CSSALT.ThoracicUSCoach
{
    [Serializable]
    public sealed class R6PositiveControl
    {
        public string controlId;
        public string sourceCaseId;
        public int sourceFrameIndex;
        public string sourceArm;
        public string sourceImageSha256;
        public string sourcePrivilegedAnnotationsSha256;
        public float sourceScore;
        public float sourceMargin;
        public Vector3 probePosition;
        public Quaternion probeRotation;
        public int repeatRenders = 3;
    }

    [Serializable]
    public sealed class R6MapCandidate
    {
        public string candidateId;
        public string anchorControlId;
        public string stage;
        public string parentCandidateId;
        public float longitudinalOffsetMm;
        public float lateralOffsetMm;
        public float yawDeg;
        public int repeatRenders = 1;
    }

    [Serializable]
    public sealed class R6CaptureRoute
    {
        public string routeId;
        public string familyId;
        public string arm;
        public int proposalSeed;
        public float startLongitudinalOffsetMm;
        public float startLateralOffsetMm;
        public float startYawDeg;
        public List<string> actions = new List<string>();
        public List<int> repeatRenderAtObservationIndices = new List<int>();
    }

    [Serializable]
    public sealed class R6FinalSprintManifest
    {
        public int schemaVersion = 1;
        public string protocolId;
        public string mode;
        public string scene;
        public string unityVersion;
        public string sourceCommit;
        public string baseCommit;
        public string captureBranch;
        public string datasetRunId;
        public string outputDirectory;
        public float acceptableScoreFloor;
        public string coordinatorProtocolPath;
        public string coordinatorProtocolSha256;
        public string auditProtocolPath;
        public string auditProtocolSha256;
        public string prerequisiteEvidencePath;
        public string prerequisiteEvidenceSha256;
        public string prerequisiteRequiredVerdict;
        public int maxCandidatePoses = 320;
        public int maxScoutRoutes = 8;
        public int maxMovementsPerScoutRoute = 60;
        public int maxScoutObservations = 512;
        public int maxDevelopmentTrajectories = 16;
        public int maxObservationsPerDevelopmentTrajectory = 121;
        public List<R6PositiveControl> positiveControls = new List<R6PositiveControl>();
        public List<R6MapCandidate> mapCandidates = new List<R6MapCandidate>();
        public List<R6CaptureRoute> routes = new List<R6CaptureRoute>();
    }

    [Serializable]
    public sealed class R6ActionRecord
    {
        public int index;
        public string plannedAction;
        public string executedAction;
        public bool accepted;
        public bool completed;
        public string rejectionReason;
        public float requestedDisplacementMm;
        public float attemptedDisplacementMm;
        public float achievedDisplacementMm;
        public float requestedRotationDeg;
        public float achievedRotationDeg;
        public bool contactValidAfterExecution;
        public bool workspaceValidAfterExecution;
        public bool rollbackAttempted;
        public bool rollbackContactRestored;
        public bool safetyFailure;
        public Vector3 beforePosition;
        public Quaternion beforeRotation;
        public Vector3 afterPosition;
        public Quaternion afterRotation;
    }

    [Serializable]
    public sealed class R6ObservationRecord
    {
        public string recordId;
        public string sourceKind;
        public string sourceId;
        public string anchorControlId;
        public string parentCandidateId;
        public string familyId;
        public string arm;
        public int observationIndex;
        public int repeatIndex;
        public float longitudinalOffsetMm;
        public float lateralOffsetMm;
        public float yawDeg;
        public string image;
        public string imageSha256;
        public Vector3 probePosition;
        public Quaternion probeRotation;
        public Vector3 contactPosition;
        public bool contactValid;
        public bool workspaceValid;
        public bool t7Valid;
        public string nearestLevel;
        public float t7DistanceMm;
        public ScoreBreakdown score = new ScoreBreakdown();
        public float margin;
        public bool acceptable;
        public bool finalTargetAcceptable;
        public bool evaluatorFloorAgreement;
        public string incomingAction;
        public bool actionAccepted;
        public bool actionCompleted;
        public string evidenceDomain;
        public bool synthetic;
        public bool faultInjected;
        public string freshCapturePurpose;
        public long poseEpoch;
        public int poseAppliedUnityFrame;
        public long producerPoseEpoch;
        public int producerUnityFrame;
        public long producerScanSerial;
        public int producerSamplesPublished;
        public int captureUnityFrame;
        public string capturePath;
        public string capturePathMode;
        public int activeAggregatorCount;
    }

    [Serializable]
    public sealed class R6AttemptRecord
    {
        public string attemptId;
        public string sourceKind;
        public string sourceId;
        public bool surfaceFound;
        public bool contactValid;
        public bool workspaceValid;
        public bool t7Valid;
        public bool accepted;
        public string rejectionReason;
        public Vector3 requestedPosition;
        public Quaternion requestedRotation;
        public Vector3 resultingPosition;
        public Quaternion resultingRotation;
        public Vector3 surfacePoint;
        public Vector3 surfaceNormal;
    }

    [Serializable]
    public sealed class R6FinalSprintReport
    {
        public int schemaVersion = 1;
        public string taskId = "PHASE2_FINAL_SPRINT_R6";
        public string protocolId;
        public string mode;
        public string generatedUtc;
        public string executionCommit;
        public string sourceCommit;
        public string scene;
        public string sceneSha256;
        public string sceneMaterializedSha256;
        public string evaluatorSha256;
        public string evaluatorMaterializedSha256;
        public string manifest;
        public string manifestSha256;
        public string coordinatorProtocolSha256;
        public string auditProtocolSha256;
        public int attemptedCandidatePoses;
        public int attemptedStarts;
        public int acceptedStarts;
        public int routesCompleted;
        public int observations;
        public int t7ValidObservations;
        public int acceptableObservations;
        public int nearBoundaryPositiveObservations;
        public int hardNegativeObservations;
        public int acceptedActions;
        public int rejectedActions;
        public int safetyFailures;
        public int contactViolations;
        public int workspaceViolations;
        public bool complete;
        public bool immutableSnapshotSealed;
        public string infrastructureFailure;
        public List<R6AttemptRecord> attempts = new List<R6AttemptRecord>();
        public List<R6ActionRecord> actions = new List<R6ActionRecord>();
        public List<R6ObservationRecord> records = new List<R6ObservationRecord>();
    }

    [Serializable]
    public sealed class R6FileIntegrityRecord
    {
        public string path;
        public string sha256;
        public long bytes;
    }

    [Serializable]
    public sealed class R6EvidenceIndex
    {
        public int schemaVersion = 1;
        public string protocolId;
        public string generatedUtc;
        public string sourceCommit;
        public string coordinatorProtocolSha256;
        public string auditProtocolSha256;
        public List<R6FileIntegrityRecord> files = new List<R6FileIntegrityRecord>();
    }

    [Serializable]
    public sealed class R6SnapshotSeal
    {
        public int schemaVersion = 1;
        public string protocolId;
        public string generatedUtc;
        public string sourceCommit;
        public string evidenceIndexSha256;
        public string reportSha256;
        public string manifestSha256;
        public string coordinatorProtocolCanonicalSha256;
        public string coordinatorProtocolMaterializedSha256;
        public string auditProtocolCanonicalSha256;
        public string auditProtocolMaterializedSha256;
        public string sceneCanonicalSha256;
        public string sceneMaterializedSha256;
        public string evaluatorCanonicalSha256;
        public string evaluatorMaterializedSha256;
        public int indexedFiles;
        public int recordCount;
        public int imageCount;
        public int actionCount;
        public int attemptCount;
        public bool complete;
        public bool immutable;
    }

    [Serializable]
    public sealed class R6PrerequisiteDecision
    {
        public string verdict;
    }

    public static class ThoracicR6FinalSprintProtocol
    {
        public const string BasinMapMode = "R6_POSITIVE_CONTROL_BASIN";
        public const string WideScoutMode = "R6_TARGET_INDEPENDENT_WIDE_SCOUT";
        public const string DevelopmentMode = "R6_BOUNDED_DEVELOPMENT";
        public const string ProtocolId = "G2_R6_FINAL_SPRINT_V1";
        public const string CaptureBranch = "codex/r6-unity-final";
        public const float FrozenAcceptableFloor = 0.5912307500839233f;
        public const string FrozenBaseCommit = "8a61f33947a29296658cf6eff44742fac4811301";
        public const string CoordinatorProtocolPath = "Tools/ThoracicUSCoach/experiments/g2_r6_final_sprint_protocol_v1.json";
        public const string CoordinatorProtocolSha256 = "d201227ab95e5a858003fa2985d5c118ae724de0b1740972a82c68d1f4eaf7c9";
        public const string AuditProtocolPath = "Tools/ThoracicUSCoach/experiments/g2_r6_audit_protocol_v1.json";
        public const string AuditProtocolSha256 = "a01d30e294939eda46f6f2090a11761bd5b37f26d6b516d05b3d3c00c19e07a5";
        public const string FrozenSceneSha256 = "6c86ff30a42e9719aff712a9e48b2ca6b5cce6004bace055abbc82b64db9a02d";
        public const string FrozenEvaluatorSha256 = "7480ab4ece46fbab6b70344b25212eb0c90621b2d458ed5c343e299a59d597fc";

        public static void Validate(R6FinalSprintManifest manifest)
        {
            if (manifest == null) throw new InvalidOperationException("R6 manifest missing");
            if (manifest.protocolId != ProtocolId) throw new InvalidOperationException("R6 protocol mismatch");
            if (manifest.mode != BasinMapMode && manifest.mode != WideScoutMode && manifest.mode != DevelopmentMode)
                throw new InvalidOperationException("R6 mode unsupported");
            if (Mathf.Abs(manifest.acceptableScoreFloor - FrozenAcceptableFloor) > 1e-6f)
                throw new InvalidOperationException("Frozen evaluator floor changed");
            if (string.IsNullOrWhiteSpace(manifest.scene)
                || string.IsNullOrWhiteSpace(manifest.unityVersion)
                || string.IsNullOrWhiteSpace(manifest.sourceCommit)
                || manifest.baseCommit != FrozenBaseCommit
                || manifest.captureBranch != CaptureBranch
                || string.IsNullOrWhiteSpace(manifest.datasetRunId)
                || string.IsNullOrWhiteSpace(manifest.outputDirectory)
                || manifest.coordinatorProtocolPath != CoordinatorProtocolPath
                || manifest.coordinatorProtocolSha256 != CoordinatorProtocolSha256
                || manifest.auditProtocolPath != AuditProtocolPath
                || manifest.auditProtocolSha256 != AuditProtocolSha256)
                throw new InvalidOperationException("R6 provenance contract is incomplete");
            RequireSha(manifest.coordinatorProtocolSha256, "coordinator protocol");
            RequireSha(manifest.auditProtocolSha256, "audit protocol");
            if (manifest.mode == BasinMapMode)
            {
                if (manifest.positiveControls == null || manifest.positiveControls.Count != 4)
                    throw new InvalidOperationException("R6 basin map requires the four frozen positive controls");
                int candidates = manifest.mapCandidates != null ? manifest.mapCandidates.Count : 0;
                if (candidates != 312 || candidates > manifest.maxCandidatePoses
                    || manifest.maxCandidatePoses > 320)
                    throw new InvalidOperationException("R6 basin grid must contain the frozen 312 candidates");
                if (manifest.positiveControls.Exists(item => item.repeatRenders < 2 || item.repeatRenders > 3)
                    || manifest.mapCandidates.Exists(item => item.repeatRenders < 1 || item.repeatRenders > 3))
                    throw new InvalidOperationException("R6 repeat-render budget invalid");
                HashSet<string> controlIds = new HashSet<string>(StringComparer.Ordinal);
                Dictionary<string, string> frozenControls = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    { "r6_control_hard_00_f0008", "46fe40c4c9af5848f6cad450840f7bae96d13c4d259307847a2cdcfc922a9c90" },
                    { "r6_control_random_01_f0010", "eeb9360661573aa64d1f3cbb000ff8ecc3dd9ccb4d3ba05277b58aa26db688a8" },
                    { "r6_control_random_05_f0016", "ddad5db3ec4ee58a758179eb8044ba80852871f9ba095e668b1abaea802bc6b7" },
                    { "r6_control_hard_01_f0009", "126b14863d3b78a6a829e64b5185d081c09facec0824547ae9fbff1d26a7df0a" }
                };
                foreach (R6PositiveControl control in manifest.positiveControls)
                {
                    if (control == null || string.IsNullOrWhiteSpace(control.controlId)
                        || string.IsNullOrWhiteSpace(control.sourceCaseId)
                        || !controlIds.Add(control.controlId))
                        throw new InvalidOperationException("R6 positive-control identity invalid");
                    if (!frozenControls.TryGetValue(control.controlId, out string frozenImage)
                        || !string.Equals(control.sourceImageSha256, frozenImage, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("R6 positive control differs from frozen contract");
                    RequireSha(control.sourceImageSha256, "positive-control image");
                    RequireSha(control.sourcePrivilegedAnnotationsSha256, "positive-control annotation");
                    if (control.sourceScore < FrozenAcceptableFloor || control.sourceMargin < 0f)
                        throw new InvalidOperationException("R6 positive control was not historically acceptable");
                }
                if (controlIds.Count != frozenControls.Count)
                    throw new InvalidOperationException("R6 frozen positive-control set is incomplete");
                HashSet<string> candidateIds = new HashSet<string>(StringComparer.Ordinal);
                int coarseCandidates = 0;
                int fineCandidates = 0;
                foreach (R6MapCandidate candidate in manifest.mapCandidates)
                {
                    if (candidate == null || string.IsNullOrWhiteSpace(candidate.candidateId)
                        || !candidateIds.Add(candidate.candidateId)
                        || !controlIds.Contains(candidate.anchorControlId)
                        || (candidate.stage != "coarse" && candidate.stage != "fine"))
                        throw new InvalidOperationException("R6 basin candidate identity invalid");
                    if (candidate.stage == "coarse") coarseCandidates++; else fineCandidates++;
                }
                if (coarseCandidates != 240 || fineCandidates != 72)
                    throw new InvalidOperationException("R6 coarse/fine basin grid count changed");
            }
            else
            {
                if (string.IsNullOrWhiteSpace(manifest.prerequisiteEvidencePath)
                    || string.IsNullOrWhiteSpace(manifest.prerequisiteRequiredVerdict))
                    throw new InvalidOperationException("R6 prerequisite evidence gate missing");
                RequireSha(manifest.prerequisiteEvidenceSha256, "prerequisite evidence");
                if (manifest.routes == null || manifest.routes.Count < 1) throw new InvalidOperationException("R6 routes missing");
                int observations = 0;
                int randomized = 0;
                int enriched = 0;
                HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
                foreach (R6CaptureRoute route in manifest.routes)
                {
                    if (route == null || string.IsNullOrWhiteSpace(route.routeId)
                        || string.IsNullOrWhiteSpace(route.familyId)
                        || route.actions == null || route.repeatRenderAtObservationIndices == null
                        || !ids.Add(route.routeId))
                        throw new InvalidOperationException("R6 route identity or lists invalid");
                    int repeats = route.repeatRenderAtObservationIndices != null
                        ? route.repeatRenderAtObservationIndices.Count : 0;
                    int count = 1 + route.actions.Count + repeats;
                    observations += count;
                    HashSet<int> repeatIndices = new HashSet<int>();
                    foreach (int repeatIndex in route.repeatRenderAtObservationIndices)
                        if (repeatIndex < 0 || repeatIndex > route.actions.Count
                            || !repeatIndices.Add(repeatIndex))
                            throw new InvalidOperationException("R6 repeat-render index invalid");
                    foreach (string action in route.actions)
                    {
                        if (!Enum.TryParse(action, true, out Active2DAction parsed)
                            || parsed == Active2DAction.Stop || parsed == Active2DAction.PhaseHandoff
                            || parsed == Active2DAction.Observe)
                            throw new InvalidOperationException("R6 forbidden route action " + action);
                    }
                    if (manifest.mode == WideScoutMode && route.actions.Count > 60)
                        throw new InvalidOperationException("R6 scout movement budget exceeded");
                    if (manifest.mode == WideScoutMode && route.arm != "wide_scout")
                        throw new InvalidOperationException("R6 scout arm invalid");
                    if (manifest.mode == DevelopmentMode)
                    {
                        if (count > 121) throw new InvalidOperationException("R6 development observation budget exceeded");
                        if (route.arm == "randomized_start") randomized++;
                        else if (route.arm == "target_enriched") enriched++;
                        else throw new InvalidOperationException("R6 development arm invalid");
                    }
                }
                if (manifest.mode == WideScoutMode
                    && (manifest.routes.Count > 8 || manifest.maxScoutRoutes > 8
                        || manifest.maxMovementsPerScoutRoute > 60 || observations > 512
                        || observations > manifest.maxScoutObservations || manifest.maxScoutObservations > 512))
                    throw new InvalidOperationException("R6 scout global budget exceeded");
                if (manifest.mode == DevelopmentMode
                    && (manifest.routes.Count > 16 || manifest.maxDevelopmentTrajectories > 16
                        || randomized > 8 || enriched > 8
                        || manifest.maxObservationsPerDevelopmentTrajectory > 121))
                    throw new InvalidOperationException("R6 development budget exceeded");
            }
        }

        private static void RequireSha(string value, string label)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length != 64)
                throw new InvalidOperationException("R6 " + label + " SHA-256 missing");
            foreach (char character in value)
                if (!Uri.IsHexDigit(character)) throw new InvalidOperationException("R6 " + label + " SHA-256 malformed");
        }
    }
}
