using System;
using System.Collections.Generic;
using UnityEngine;

namespace CSSALT.ThoracicUSCoach
{
    [Serializable]
    public sealed class R5CaptureRoute
    {
        public string routeId;
        public string familyId;
        public string arm;
        public string coverageIntent;
        public string startStratum;
        public int proposalSeed;
        public int maxStartAttempts = 6;
        public List<string> actions = new List<string>();
        public List<int> repeatRenderAtObservationIndices = new List<int>();
    }

    [Serializable]
    public sealed class R5CaptureManifest
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
        public int proposalSeed = 20261026;
        public string outputDirectory;
        public string sharedContractPath;
        public string sharedContractSha256;
        public string sharedContractMaterializedSha256;
        public string scientificCriteriaPath;
        public string scientificCriteriaSha256;
        public string scientificCriteriaMaterializedSha256;
        public string independentAuditPath;
        public string independentAuditSha256;
        public string independentAuditMaterializedSha256;
        public float acceptableScoreFloor;
        public int maxStartProposals;
        public int maxRoutes;
        public int maxMovementsPerRoute;
        public int maxImages;
        public int maxObservationsPerRoute;
        public List<R5CaptureRoute> routes = new List<R5CaptureRoute>();
    }

    [Serializable]
    public sealed class R5StartProposalAudit
    {
        public int proposalIndex;
        public string routeId;
        public int attemptWithinRoute;
        public int proposalSeed;
        public string startStratum;
        public string derivation = "T6/T7/T8 markers + skin raycast/local normal; no prior P1 pose";
        public float longitudinalOffsetMm;
        public float lateralOffsetMm;
        public float yawDeg;
        public Vector3 proposedWorldPoint;
        public bool surfaceRaycastFound;
        public Vector3 surfacePoint;
        public Vector3 surfaceNormal;
        public bool rigidContactApplied;
        public bool contactValid;
        public bool workspaceValid;
        public bool t7LevelAcceptable;
        public bool acceptedAsStart;
        public Vector3 resultingProbePosition;
        public Quaternion resultingProbeRotation;
        public Vector3 resultingContactPosition;
        public string rejectionReason;
    }

    [Serializable]
    public sealed class R5ActionAudit
    {
        public int sequenceIndex;
        public string action;
        public bool accepted;
        public bool completed;
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
        public string rejectionReason;
        public Vector3 beforePosition;
        public Quaternion beforeRotation;
        public Vector3 afterPosition;
        public Quaternion afterRotation;
    }

    [Serializable]
    public sealed class R5ObservationAudit
    {
        public int index;
        public int sourceObservationIndex;
        public int repeatIndex;
        public string image;
        public string imageSha256;
        public string incomingAction;
        public bool actionAccepted;
        public bool actionCompleted;
        public bool episodeReset;
        public Vector3 probePosition;
        public Quaternion probeRotation;
        public Vector3 contactPosition;
        public float signedDepthMm;
        public bool contactValid;
        public bool workspaceValid;
        public string nearestLevel;
        public float nearestLevelDistanceMm;
        public float t7DistanceMm;
        public bool t7LevelAcceptable;
        public ScoreBreakdown score = new ScoreBreakdown();
        public float marginToAcceptableFloor;
        public bool finalTargetAcceptable;
        public bool evaluatorFloorAgreement;
    }

    [Serializable]
    public sealed class R5RouteResult
    {
        public string routeId;
        public string familyId;
        public string arm;
        public string coverageIntent;
        public string startStratum;
        public int proposalSeed;
        public string acceptedStartProposalId;
        public string captureStatus;
        public string failureReason;
        public int acceptedActions;
        public int rejectedActions;
        public int incompleteActions;
        public int safetyFailures;
        public int positiveObservations;
        public int nearBoundaryPositiveObservations;
        public int nearBoundaryNegativeObservations;
        public int hardNegativeObservations;
        public float minimumScore = float.PositiveInfinity;
        public float maximumScore = float.NegativeInfinity;
        public List<R5StartProposalAudit> startProposals = new List<R5StartProposalAudit>();
        public List<R5ActionAudit> actions = new List<R5ActionAudit>();
        public List<R5ObservationAudit> observations = new List<R5ObservationAudit>();
    }

    [Serializable]
    public sealed class R5FileIntegrityRecord
    {
        public string path;
        public string sha256;
        public long bytes;
    }

    [Serializable]
    public sealed class R5EvidenceIndex
    {
        public int schemaVersion = 1;
        public string protocolId;
        public string generatedUtc;
        public string sourceCommit;
        public string sharedContractSha256;
        public string scientificCriteriaSha256;
        public List<R5FileIntegrityRecord> files = new List<R5FileIntegrityRecord>();
    }

    [Serializable]
    public sealed class R5SnapshotSeal
    {
        public int schemaVersion = 1;
        public string protocolId;
        public string generatedUtc;
        public string sourceCommit;
        public string evidenceIndex = "evidence_index.json";
        public string evidenceIndexSha256;
        public string reportSha256;
        public string evidenceManifestSha256;
        public string sharedContractSha256;
        public string scientificCriteriaSha256;
        public string independentAuditSha256;
        public int indexedFiles;
    }

    [Serializable]
    public sealed class R5UnityReport
    {
        public int schemaVersion = 1;
        public string taskId = "G2-R5";
        public string protocolId;
        public string mode;
        public string generatedUtc;
        public string executionCommit;
        public string sourceCommit;
        public string unityVersion;
        public string scene;
        public string sceneSha256;
        public string evaluatorSha256;
        public string active2DControllerSha256;
        public string captureControllerSha256;
        public string manifest;
        public string manifestSha256;
        public string sharedContract;
        public string sharedContractSha256;
        public string sharedContractMaterializedSha256;
        public string scientificCriteria;
        public string scientificCriteriaSha256;
        public string scientificCriteriaMaterializedSha256;
        public string independentAudit;
        public string independentAuditSha256;
        public string independentAuditMaterializedSha256;
        public float acceptableScoreFloor;
        public int requestedRoutes;
        public int completedRoutes;
        public int failedRoutes;
        public int attemptedStartProposals;
        public int acceptedStarts;
        public int totalObservations;
        public int positiveObservations;
        public int hardNegativeObservations;
        public int acceptedActions;
        public int rejectedActions;
        public int incompleteActions;
        public int safetyFailures;
        public int contactViolations;
        public int workspaceViolations;
        public bool complete;
        public bool immutableSnapshotSealed;
        public string evidenceIndexSha256;
        public string snapshotSealSha256;
        public string infrastructureFailure;
        public List<R5StartProposalAudit> allStartProposals = new List<R5StartProposalAudit>();
        public List<R5RouteResult> routes = new List<R5RouteResult>();
    }

    [Serializable]
    public sealed class R5EvidenceGeneratorFile
    {
        public string path;
        public string sha256;
    }

    [Serializable]
    public sealed class R5EvidenceSource
    {
        public string baseCommit;
        public string captureCommit;
        public string captureBranch;
        public List<R5EvidenceGeneratorFile> generatorFiles = new List<R5EvidenceGeneratorFile>();
    }

    [Serializable]
    public sealed class R5EvidenceScene
    {
        public string path;
        public string sha256CanonicalLf;
        public string sha256Materialized;
        public string unityVersion;
        public string evaluatorSource;
        public string evaluatorSha256CanonicalLf;
        public string evaluatorSha256Materialized;
        public float acceptableScoreFloor;
    }

    [Serializable]
    public sealed class R5EvidenceProposalSummary
    {
        public int seed;
        public string generatorVersion;
        public int attemptedStarts;
        public int acceptedStarts;
        public int rejectedStarts;
    }

    [Serializable]
    public sealed class R5EvidenceDevelopmentRoute
    {
        public string strategy;
        public int expectedAdditionalIndependentPositiveComponents;
        public int expectedAdditionalIndependentNegativeComponents;
        public List<string> supportingRecordIds = new List<string>();
        public string whyScoutSupportsIt;
    }

    [Serializable]
    public sealed class R5EvidenceStartAttempt
    {
        public string proposalId;
        public string landmarkStratum = "T7_REGION";
        public List<string> landmarkObjects = new List<string>();
        public Vector3 generatedPosition;
        public Quaternion generatedRotation;
        public Vector3 skinHitPoint;
        public Vector3 skinNormal;
        public bool accepted;
        public string rejectionReason;
        public bool t7Valid;
        public bool contactValid;
        public bool workspaceValid;
        public bool copiedPriorBaseTransform;
    }

    [Serializable]
    public sealed class R5EvidenceSourceAncestry
    {
        public string origin;
        public List<string> landmarkObjects = new List<string>();
        public bool copiedPriorBaseTransform;
    }

    [Serializable]
    public sealed class R5EvidencePoseTrace
    {
        public Vector3 beforePosition;
        public Quaternion beforeRotation;
        public Vector3 afterPosition;
        public Quaternion afterRotation;
    }

    [Serializable]
    public sealed class R5EvidenceImage
    {
        public string relativePath;
        public string sha256;
        public bool cleanUltrasoundOnly = true;
    }

    [Serializable]
    public sealed class R5EvidenceOfflineAnnotation
    {
        public float targetScore;
        public bool acceptable;
        public ScoreBreakdown scoreComponents = new ScoreBreakdown();
        public bool availableToActor;
    }

    [Serializable]
    public sealed class R5EvidenceRecord
    {
        public string recordId;
        public string routeId;
        public string startProposalId;
        public int observationIndex;
        public R5EvidenceSourceAncestry sourceAncestry = new R5EvidenceSourceAncestry();
        public string plannedAction;
        public string executedAction;
        public bool actionAccepted;
        public string rejectionReason;
        public R5EvidencePoseTrace poseTrace = new R5EvidencePoseTrace();
        public bool t7Valid;
        public bool contactValid;
        public bool workspaceValid;
        public R5EvidenceImage image = new R5EvidenceImage();
        public R5EvidenceOfflineAnnotation offlineAnnotation = new R5EvidenceOfflineAnnotation();
    }

    [Serializable]
    public sealed class R5EvidenceIntegrity
    {
        public int recordCount;
        public int imageCount;
        public string manifestSha256;
        public string filesSha256;
        public bool complete;
        public bool immutable;
    }

    [Serializable]
    public sealed class R5EvidenceSnapshot
    {
        public string schemaVersion = "g2-r5-evidence-v1";
        public string protocolId = "G2_R5_FIXED_ANATOMY_DIVERSITY_V1";
        public string protocolSha256;
        public string snapshotId;
        public string snapshotRole;
        public string createdUtc;
        public R5EvidenceSource source = new R5EvidenceSource();
        public R5EvidenceScene scene = new R5EvidenceScene();
        public R5EvidenceProposalSummary proposal = new R5EvidenceProposalSummary();
        public List<R5EvidenceStartAttempt> startAttempts = new List<R5EvidenceStartAttempt>();
        public R5EvidenceDevelopmentRoute developmentRoute = new R5EvidenceDevelopmentRoute();
        public List<R5EvidenceRecord> records = new List<R5EvidenceRecord>();
        public R5EvidenceIntegrity integrity = new R5EvidenceIntegrity();
    }

    public static class ThoracicR5CaptureProtocol
    {
        public const string ScoutMode = "R5_A_SCOUT";
        public const string DevelopmentMode = "R5_B_DEVELOPMENT";
        public const string ProtocolId = "G2_R5_FIXED_ANATOMY_DIVERSITY_V1";
        public const float FrozenAcceptableScoreFloor = 0.5912307500839233f;
        public const string FrozenBaseCommit = "bdbb6e90ebe7e057b704391ede9961815a8bdbb5";
        public const string FrozenSceneSha256 = "6c86ff30a42e9719aff712a9e48b2ca6b5cce6004bace055abbc82b64db9a02d";
        public const string FrozenEvaluatorSha256 = "7480ab4ece46fbab6b70344b25212eb0c90621b2d458ed5c343e299a59d597fc";
        public const string FrozenProtocolSha256 = "04782fd9d216a15f0d67cdc9eaf25a556c3a31d69ecd3359db8eee955a046d5d";
        public const string FrozenSchemaSha256 = "eb66798b4348c7d335090f6d8c210aa1b4cd9409c9f8aa64e800bdd05643e0b0";
        public const string FrozenAuditSha256 = "17e30dd075af8510809ff405a1489a020847f36385ae5cc6f535b269757ed582";

        public static void ValidateStructure(R5CaptureManifest manifest)
        {
            if (manifest == null || manifest.routes == null)
                throw new InvalidOperationException("R5 manifest is missing");
            if (manifest.schemaVersion != 1)
                throw new InvalidOperationException("Unsupported R5 schema version");
            if (manifest.protocolId != ProtocolId)
                throw new InvalidOperationException("Unexpected R5 protocol identity");
            if (manifest.mode != ScoutMode && manifest.mode != DevelopmentMode)
                throw new InvalidOperationException("Unsupported R5 capture mode");
            if (Mathf.Abs(manifest.acceptableScoreFloor - FrozenAcceptableScoreFloor) > 1e-6f)
                throw new InvalidOperationException("Frozen acceptable score floor changed");
            if (string.IsNullOrWhiteSpace(manifest.protocolId)
                || string.IsNullOrWhiteSpace(manifest.sourceCommit)
                || string.IsNullOrWhiteSpace(manifest.baseCommit)
                || string.IsNullOrWhiteSpace(manifest.captureBranch)
                || string.IsNullOrWhiteSpace(manifest.sharedContractPath)
                || string.IsNullOrWhiteSpace(manifest.sharedContractSha256)
                || string.IsNullOrWhiteSpace(manifest.sharedContractMaterializedSha256)
                || string.IsNullOrWhiteSpace(manifest.scientificCriteriaPath)
                || string.IsNullOrWhiteSpace(manifest.scientificCriteriaSha256)
                || string.IsNullOrWhiteSpace(manifest.scientificCriteriaMaterializedSha256)
                || string.IsNullOrWhiteSpace(manifest.independentAuditPath)
                || string.IsNullOrWhiteSpace(manifest.independentAuditSha256)
                || string.IsNullOrWhiteSpace(manifest.independentAuditMaterializedSha256))
                throw new InvalidOperationException("R5 provenance contract is incomplete");
            if (manifest.routes.Count == 0 || manifest.routes.Count > manifest.maxRoutes)
                throw new InvalidOperationException("R5 route budget exceeded");
            HashSet<string> routeIds = new HashSet<string>(StringComparer.Ordinal);
            HashSet<string> familyIds = new HashSet<string>(StringComparer.Ordinal);
            int totalImages = 0;
            int totalProposals = 0;
            int randomized = 0;
            int enriched = 0;
            foreach (R5CaptureRoute route in manifest.routes)
            {
                if (route == null || string.IsNullOrWhiteSpace(route.routeId)
                    || string.IsNullOrWhiteSpace(route.familyId)
                    || !routeIds.Add(route.routeId) || !familyIds.Add(route.familyId))
                    throw new InvalidOperationException("R5 route/family IDs must be present and unique");
                if (route.maxStartAttempts < 1) throw new InvalidOperationException("Start attempt budget must be positive");
                if (route.actions == null || route.repeatRenderAtObservationIndices == null)
                    throw new InvalidOperationException("R5 action/repeat lists must be present");
                totalProposals += route.maxStartAttempts;
                int repeats = route.repeatRenderAtObservationIndices != null
                    ? route.repeatRenderAtObservationIndices.Count : 0;
                int observations = 1 + route.actions.Count + repeats;
                totalImages += observations;
                if (route.actions.Count > manifest.maxMovementsPerRoute
                    || observations > manifest.maxObservationsPerRoute)
                    throw new InvalidOperationException("Per-route movement/image budget exceeded for " + route.routeId);
                HashSet<int> repeatIndices = new HashSet<int>();
                foreach (int index in route.repeatRenderAtObservationIndices)
                {
                    if (index < 0 || index > route.actions.Count || !repeatIndices.Add(index))
                        throw new InvalidOperationException("Invalid repeat-render index in " + route.routeId);
                }
                foreach (string action in route.actions)
                {
                    if (!Enum.TryParse(action, true, out Active2DAction parsed)
                        || parsed == Active2DAction.Stop || parsed == Active2DAction.PhaseHandoff
                        || parsed == Active2DAction.Observe)
                        throw new InvalidOperationException("Forbidden R5 action " + action);
                }
                if (manifest.mode == ScoutMode && route.arm != "scout")
                    throw new InvalidOperationException("R5-A routes must use the scout arm");
                if (manifest.mode == DevelopmentMode)
                {
                    if (route.arm == "randomized_start") randomized++;
                    else if (route.arm == "target_enriched") enriched++;
                    else throw new InvalidOperationException("Invalid R5-B arm " + route.arm);
                }
            }
            if (totalProposals > manifest.maxStartProposals)
                throw new InvalidOperationException("R5 start proposal budget exceeded");
            if (totalImages > manifest.maxImages)
                throw new InvalidOperationException("R5 image budget exceeded");
            if (manifest.mode == ScoutMode
                && (manifest.maxRoutes > 8 || manifest.maxMovementsPerRoute > 12
                    || manifest.maxImages > 96 || manifest.maxStartProposals > 48
                    || manifest.proposalSeed != 20261026))
                throw new InvalidOperationException("R5-A frozen maximum budget exceeded");
            if (manifest.mode == DevelopmentMode
                && (manifest.maxRoutes > 16 || manifest.maxObservationsPerRoute > 81
                    || manifest.maxImages > 1296 || randomized > 8 || enriched > 8))
                throw new InvalidOperationException("R5-B frozen maximum budget exceeded");
        }

        public static void StartOffsets(
            string stratum, int seed, int attempt, float markerSpacingMm,
            out float longitudinalMm, out float lateralMm, out float yawDeg)
        {
            int stratumIndex;
            switch ((stratum ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "far_cranial": stratumIndex = -2; break;
                case "near_cranial": stratumIndex = -1; break;
                case "near_caudal": stratumIndex = 1; break;
                case "far_caudal": stratumIndex = 2; break;
                case "center": stratumIndex = 0; break;
                default: throw new InvalidOperationException("Unknown R5 start stratum " + stratum);
            }
            int mixed = unchecked(seed * 486187739 + attempt * 16777619 + 31);
            System.Random random = new System.Random(mixed);
            float spacing = Mathf.Clamp(markerSpacingMm, 6f, 40f);
            float center = stratumIndex * spacing * 0.22f;
            longitudinalMm = center + Mathf.Lerp(-spacing * 0.09f, spacing * 0.09f, (float)random.NextDouble());
            lateralMm = Mathf.Lerp(-12f, 12f, (float)random.NextDouble());
            yawDeg = Mathf.Lerp(-9f, 9f, (float)random.NextDouble());
        }
    }
}
