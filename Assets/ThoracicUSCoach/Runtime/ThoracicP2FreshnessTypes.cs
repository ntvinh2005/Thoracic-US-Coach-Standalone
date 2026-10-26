using System;
using System.Collections.Generic;
using UnityEngine;

namespace CSSALT.ThoracicUSCoach
{
    [Serializable]
    public sealed class P2FreshnessManifest
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
        public string captureProtocolPath;
        public string captureProtocolSha256;
        public string auditProtocolPath;
        public string auditProtocolSha256;
        public int acceptedFreshCapturesPerTransition = 2;
        public int maximumRejectionProbeActions = 16;
        public List<R6PositiveControl> positiveControls = new List<R6PositiveControl>();
        public List<string> freshnessControlSequence = new List<string>();
        public List<string> movementFreshnessActions = new List<string>();
        public string rejectionProbeAction = "Lateral";
    }

    [Serializable]
    public sealed class P2FreshnessCaptureRecord
    {
        public string recordId;
        public string transitionId;
        public string visitId;
        public string sourceKind;
        public string sourceId;
        public string controlId;
        public string actionCategory;
        public string captureDisposition;
        public long poseEpoch;
        public int poseAppliedFrame;
        public int poseAppliedUnityFrame;
        public double poseAppliedRealtime;
        public long producerPoseEpoch;
        public int producerUnityFrame;
        public long producerScanSerial;
        public int producerSamplesPublished;
        public int aggregatorCount;
        public int aggregatorFreshSampleCount;
        public bool aggregatorHistoryReset;
        public int aggregatorHistoryLength;
        public bool aggregatorAllSamplesPoseEpochMatch;
        public int discardedWarmupCount;
        public string immediateDiscardImage;
        public string immediateDiscardSha256;
        public string publishedWarmupImage;
        public string publishedWarmupSha256;
        public int acquisitionOrder;
        public int acceptedSlot;
        public int captureUnityFrame;
        public int unityFrameCount;
        public double captureRealtime;
        public string image;
        public string imageSha256;
        public Vector3 probePosition;
        public Quaternion probeRotation;
        public Vector3 requestedProbePosition;
        public Quaternion requestedProbeRotation;
        public Vector3 beforeProbePosition;
        public Quaternion beforeProbeRotation;
        public bool actualResultingPoseRecorded;
        public bool contactValid;
        public bool workspaceValid;
        public bool t7Valid;
        public ScoreBreakdown score = new ScoreBreakdown();
        public bool acceptable;
        public bool finalTargetAcceptable;
        public string plannedAction;
        public string executedAction;
        public bool actionAccepted;
        public bool actionCompleted;
        public bool rollbackAttempted;
        public bool rollbackContactRestored;
        public bool safetyFailure;
        public string actionRejectionReason;
    }

    [Serializable]
    public sealed class P2FreshnessTransitionAttempt
    {
        public string transitionId;
        public string sourceKind;
        public string sourceId;
        public long poseEpoch;
        public int poseAppliedFrame;
        public double poseAppliedRealtime;
        public bool poseAccepted;
        public bool producerPublished;
        public string failure;
        public Vector3 resultingPosition;
        public Quaternion resultingRotation;
    }

    [Serializable]
    public sealed class P2FreshnessReport
    {
        public int schemaVersion = 1;
        public string taskId = "PHASE2_FINAL_SPRINT_PROMPT2_FRESHNESS";
        public string protocolId;
        public string mode;
        public string generatedUtc;
        public string sourceCommit;
        public string executionCommit;
        public string manifestSha256;
        public string sceneCanonicalSha256;
        public string evaluatorCanonicalSha256;
        public bool complete;
        public bool immutableSnapshotSealed;
        public string infrastructureFailure;
        public int poseEpochs;
        public int discardedWarmups;
        public int acceptedFreshCaptures;
        public int controlTransitionsAttempted;
        public int movementScenariosAttempted;
        public int rejectedActions;
        public int rollbackActions;
        public int safetyFailures;
        public List<P2FreshnessTransitionAttempt> attempts = new List<P2FreshnessTransitionAttempt>();
        public List<P2FreshnessCaptureRecord> records = new List<P2FreshnessCaptureRecord>();
        public List<R6ActionRecord> actions = new List<R6ActionRecord>();
    }

    [Serializable]
    public sealed class P2FreshnessEvidenceIndex
    {
        public int schemaVersion = 1;
        public string protocolId;
        public string generatedUtc;
        public string sourceCommit;
        public string captureProtocolSha256;
        public string auditProtocolSha256;
        public List<R6FileIntegrityRecord> files = new List<R6FileIntegrityRecord>();
    }

    [Serializable]
    public sealed class P2FreshnessSnapshotSeal
    {
        public int schemaVersion = 1;
        public string protocolId;
        public string generatedUtc;
        public string sourceCommit;
        public string evidenceIndexSha256;
        public string reportSha256;
        public string manifestSha256;
        public string captureProtocolCanonicalSha256;
        public string captureProtocolMaterializedSha256;
        public string auditProtocolCanonicalSha256;
        public string auditProtocolMaterializedSha256;
        public string sceneCanonicalSha256;
        public string sceneMaterializedSha256;
        public string evaluatorCanonicalSha256;
        public string evaluatorMaterializedSha256;
        public int indexedFiles;
        public int recordCount;
        public int acceptedImageCount;
        public int discardedImageCount;
        public int actionCount;
        public bool complete;
        public bool immutable;
    }

    public static class ThoracicP2FreshnessProtocol
    {
        public const string ProtocolId = "G2_PHASE2_FINAL_SPRINT_P2_V1";
        public const string Mode = "P2_IMAGE_POSE_FRESHNESS";
        public const string BaseCommit = "c2c4c34749ee8f1ae3b6bb9650ea0cb4c11a33d4";
        public const string CaptureBranch = "codex/p2-unity-final";
        public const float FrozenAcceptableFloor = 0.5912307500839233f;
        public const string FrozenSceneSha256 = "6c86ff30a42e9719aff712a9e48b2ca6b5cce6004bace055abbc82b64db9a02d";
        public const string FrozenEvaluatorSha256 = "7480ab4ece46fbab6b70344b25212eb0c90621b2d458ed5c343e299a59d597fc";

        public static void Validate(P2FreshnessManifest manifest)
        {
            if (manifest == null) throw new InvalidOperationException("P2 freshness manifest missing");
            if (manifest.protocolId != ProtocolId || manifest.mode != Mode)
                throw new InvalidOperationException("P2 freshness protocol/mode mismatch");
            if (manifest.baseCommit != BaseCommit || manifest.captureBranch != CaptureBranch)
                throw new InvalidOperationException("P2 freshness source lineage mismatch");
            if (Mathf.Abs(manifest.acceptableScoreFloor - FrozenAcceptableFloor) > 1e-6f)
                throw new InvalidOperationException("Frozen evaluator floor changed");
            if (string.IsNullOrWhiteSpace(manifest.scene) || string.IsNullOrWhiteSpace(manifest.unityVersion)
                || string.IsNullOrWhiteSpace(manifest.sourceCommit) || string.IsNullOrWhiteSpace(manifest.datasetRunId)
                || string.IsNullOrWhiteSpace(manifest.outputDirectory)
                || string.IsNullOrWhiteSpace(manifest.captureProtocolPath)
                || string.IsNullOrWhiteSpace(manifest.captureProtocolSha256)
                || string.IsNullOrWhiteSpace(manifest.auditProtocolPath)
                || string.IsNullOrWhiteSpace(manifest.auditProtocolSha256))
                throw new InvalidOperationException("P2 freshness provenance incomplete");
            if (manifest.positiveControls == null || manifest.positiveControls.Count != 4)
                throw new InvalidOperationException("P2 freshness requires the four frozen controls");
            if (manifest.freshnessControlSequence == null || manifest.freshnessControlSequence.Count < 5)
                throw new InvalidOperationException("P2 freshness transition sequence incomplete");
            if (manifest.freshnessControlSequence[0] != manifest.freshnessControlSequence[2])
                throw new InvalidOperationException("P2 freshness sequence must contain A-B-A transition");
            if (manifest.acceptedFreshCapturesPerTransition < 2 || manifest.acceptedFreshCapturesPerTransition > 3)
                throw new InvalidOperationException("P2 freshness accepted capture count invalid");
            if (manifest.maximumRejectionProbeActions < 1 || manifest.maximumRejectionProbeActions > 20)
                throw new InvalidOperationException("P2 rejection probe budget invalid");
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (R6PositiveControl control in manifest.positiveControls)
                if (control == null || string.IsNullOrWhiteSpace(control.controlId) || !ids.Add(control.controlId))
                    throw new InvalidOperationException("P2 freshness control identity invalid");
            foreach (string id in manifest.freshnessControlSequence)
                if (!ids.Contains(id)) throw new InvalidOperationException("P2 transition references unknown control");
        }
    }
}
