using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace CSSALT.ThoracicUSCoach
{
    [Serializable] public class P3StageBOffset
    {
        public float longitudinalOffsetMm;
        public float lateralOffsetMm;
        public float yawDeg;
    }

    [Serializable] public sealed class P3StageBCandidate : P3StageBOffset
    {
        public string candidateId;
        public string anchorControlId;
        public string stage;
        public string centerCandidateId;
        public string role;
        public bool countsAsDistinctCell = true;
        public bool consumedHistoricalControl;
    }

    [Serializable] public sealed class P3StageBRefinementContract
    {
        public bool declaredBeforeCoarseCapture;
        public string selectionRule;
        public List<P3StageBOffset> offsetTemplate = new List<P3StageBOffset>();
        public string coarseSnapshotFile;
        public string selectionManifestFile;
        public int maximumSlotsPerAnchor;
        public bool postOutcomeExtensionAllowed;
    }

    [Serializable] public sealed class P3StageBFinalManifest
    {
        public int schemaVersion = 1;
        public string protocolId;
        public string mode;
        public string scene;
        public string unityVersion;
        public string sourceCommit;
        public string reviewedDevBaseCommit;
        public string captureBranch;
        public string datasetRunId;
        public string outputDirectory;
        public float acceptableScoreFloor;
        public string auditProtocolPath;
        public string auditProtocolCanonicalSha256;
        public string upstreamStageFIndependentAuditPath;
        public string upstreamStageFIndependentAuditSha256;
        public string upstreamRequiredVerdict;
        public string upstreamSnapshotSealSha256;
        public int maximumCandidatePoses = 320;
        public bool outcomeAdaptiveCandidateAddition;
        public List<R6PositiveControl> historicalControls = new List<R6PositiveControl>();
        public List<P3StageBCandidate> mapCandidates = new List<P3StageBCandidate>();
        public P3StageBRefinementContract refinement = new P3StageBRefinementContract();
    }

    [Serializable] public sealed class P3StageBCandidateResult
    {
        public string candidateId;
        public string anchorControlId;
        public string stage;
        public bool attempted;
        public bool accepted;
        public string rejectionReason;
        public bool actualResultingPoseRecorded;
        public Vector3 requestedPosition;
        public Quaternion requestedRotation;
        public Vector3 actualPosition;
        public Quaternion actualRotation;
        public Vector3 contactPosition;
        public Vector3 surfaceNormal;
        public List<string> recordIds = new List<string>();
    }

    [Serializable] public sealed class P3StageBRecord
    {
        public string recordId;
        public string sourceKind;
        public string candidateId;
        public string anchorControlId;
        public string stage;
        public string componentId;
        public string captureDisposition;
        public string discardReason;
        public int acceptedSlot;
        public int acquisitionOrder;
        public int unityFrameCount;
        public string evidenceDomain;
        public bool synthetic;
        public bool faultInjected;
        public string image;
        public string imageSha256;
        public long poseEpoch;
        public int poseAppliedUnityFrame;
        public double poseAppliedRealtime;
        public long producerPoseEpoch;
        public int producerUnityFrame;
        public long producerScanSerial;
        public int producerSamplesPublished;
        public int captureUnityFrame;
        public double captureRealtime;
        public string capturePath;
        public string capturePathMode;
        public int activeAggregatorCount;
        public Vector3 requestedProbePosition;
        public Quaternion requestedProbeRotation;
        public Vector3 probePosition;
        public Quaternion probeRotation;
        public Vector3 contactPosition;
        public bool contactValid;
        public bool workspaceValid;
        public bool t7Valid;
        public string nearestLevel;
        public float t7DistanceMm;
        public ScoreBreakdown score = new ScoreBreakdown();
        public bool acceptable;
        public bool finalTargetAcceptable;
        public bool evaluatorFloorAgreement;
    }

    [Serializable] public sealed class P3StageBRepresentativeRuntime
    {
        public string representativeId;
        public string sourceCandidateId;
        public List<string> component = new List<string>();
        public string rejectionReason;
        public List<string> recordIds = new List<string>();
    }

    [Serializable] public sealed class P3StageBRepresentativePlanItem
    {
        public string representativeId;
        public string sourceCandidateId;
        public List<string> component = new List<string>();
    }

    [Serializable] public sealed class P3StageBRepresentativePlan
    {
        public List<P3StageBRepresentativePlanItem> representatives = new List<P3StageBRepresentativePlanItem>();
    }

    [Serializable] public sealed class P3StageBRefinementSelectionManifest
    {
        public List<P3StageBCandidate> mapCandidates = new List<P3StageBCandidate>();
    }

    [Serializable] public sealed class P3StageBRuntimeReport
    {
        public int schemaVersion = 1;
        public string taskId = "PHASE2_FINAL_SPRINT_PROMPT3_STAGE_B_FINAL";
        public string protocolId;
        public string mode;
        public string generatedUtc;
        public string sourceCommit;
        public string manifestSha256;
        public string auditProtocolCanonicalSha256;
        public string upstreamStageFIndependentAuditSha256;
        public string upstreamSnapshotSealSha256;
        public string sceneCanonicalSha256;
        public string sceneMaterializedSha256;
        public string evaluatorCanonicalSha256;
        public string evaluatorMaterializedSha256;
        public bool complete;
        public bool immutableSnapshotSealed;
        public string infrastructureFailure;
        public List<string> attemptedCandidateIds = new List<string>();
        public List<P3StageBCandidateResult> candidateResults = new List<P3StageBCandidateResult>();
        public List<P3StageBRecord> records = new List<P3StageBRecord>();
        public List<P3StageBRepresentativeRuntime> representativeRuntime = new List<P3StageBRepresentativeRuntime>();
    }

    [Serializable] public sealed class P3StageBUpstreamAudit
    {
        public string verdict;
        public bool authorizedNextStage;
        public string integrityStatus;
    }

    [Serializable] public sealed class P3StageBFileIntegrityRecord
    {
        public string path;
        public string sha256;
        public long bytes;
    }

    [Serializable] public sealed class P3StageBEvidenceIndex
    {
        public int schemaVersion = 1;
        public string protocolId;
        public string sourceCommit;
        public List<P3StageBFileIntegrityRecord> files = new List<P3StageBFileIntegrityRecord>();
    }

    [Serializable] public sealed class P3StageBSnapshotSeal
    {
        public int schemaVersion = 1;
        public string protocolId;
        public string sourceCommit;
        public string evidenceIndexSha256;
        public string reportSha256;
        public string manifestSha256;
        public string coarseSnapshotSha256;
        public string refinementSelectionManifestSha256;
        public string auditProtocolCanonicalSha256;
        public string auditProtocolMaterializedSha256;
        public string upstreamStageFIndependentAuditSha256;
        public string upstreamSnapshotSealSha256;
        public string sceneCanonicalSha256;
        public string sceneMaterializedSha256;
        public string evaluatorCanonicalSha256;
        public string evaluatorMaterializedSha256;
        public int indexedFiles;
        public int recordCount;
        public int candidateResultCount;
        public int representativeCount;
        public int acceptedImageCount;
        public int discardedImageCount;
        public bool complete;
        public bool immutable;
        public bool failureSnapshot;
    }

    public static class ThoracicP3StageBFinalProtocol
    {
        public const string ProtocolId = "G2_PHASE2_FINAL_SPRINT_P3_V1";
        public const string Mode = "P3_STAGE_B_FINAL";
        public const string CaptureBranch = "codex/p2-unity-final";
        public const string ReviewedDevBaseCommit = "d362c50769068f2c57014aa26a0c9ace8abd2921";
        public const string ProtocolPath = "Tools/ThoracicUSCoach/experiments/g2_p3_stage_b_final_protocol_v1.json";
        public const string ProtocolCanonicalSha256 = "14967ccaef673152b3784fc33ccf184c527769403418db765c82ee54d6aa6278";
        public const string UpstreamAuditSha256 = "af574b9b515362e09f6044c4a21e01df216f8fe0cb7e8133514cd61212414d65";
        public const string UpstreamSnapshotSealSha256 = "93a15b1abb6b29ff4e4e3ceccc6b492ca0079b33a9bc8362a10e24a57c708f0c";
        public const string UpstreamRequiredVerdict = "STAGE_F_FINAL_PASS";
        public const string FrozenSceneSha256 = "6c86ff30a42e9719aff712a9e48b2ca6b5cce6004bace055abbc82b64db9a02d";
        public const string FrozenEvaluatorSha256 = "7480ab4ece46fbab6b70344b25212eb0c90621b2d458ed5c343e299a59d597fc";
        public const float FrozenAcceptableFloor = 0.5912307500839233f;
        public const string RefinementSelectionRule = "For each anchor, select the passing coarse/wider candidate with greatest minimum repeat score; ties resolve by candidateId ascending.";

        public static void Validate(P3StageBFinalManifest manifest)
        {
            if (manifest == null || manifest.protocolId != ProtocolId || manifest.mode != Mode)
                throw new InvalidOperationException("P3 Stage B protocol/mode mismatch");
            if (manifest.captureBranch != CaptureBranch || manifest.reviewedDevBaseCommit != ReviewedDevBaseCommit
                || manifest.auditProtocolPath != ProtocolPath || manifest.auditProtocolCanonicalSha256 != ProtocolCanonicalSha256)
                throw new InvalidOperationException("P3 Stage B source/protocol lineage mismatch");
            if (manifest.upstreamStageFIndependentAuditSha256 != UpstreamAuditSha256
                || manifest.upstreamSnapshotSealSha256 != UpstreamSnapshotSealSha256
                || manifest.upstreamRequiredVerdict != UpstreamRequiredVerdict)
                throw new InvalidOperationException("P3 Stage B upstream gate mismatch");
            if (manifest.outcomeAdaptiveCandidateAddition || manifest.maximumCandidatePoses != 320
                || manifest.mapCandidates == null || manifest.mapCandidates.Count != 212)
                throw new InvalidOperationException("P3 Stage B requires exactly 212 prefrozen base candidates");
            if (manifest.refinement == null || !manifest.refinement.declaredBeforeCoarseCapture
                || manifest.refinement.selectionRule != RefinementSelectionRule
                || manifest.refinement.offsetTemplate == null || manifest.refinement.offsetTemplate.Count != 27
                || manifest.refinement.maximumSlotsPerAnchor != 27 || manifest.refinement.postOutcomeExtensionAllowed)
                throw new InvalidOperationException("P3 Stage B refinement contract mismatch");
            if (Mathf.Abs(manifest.acceptableScoreFloor - FrozenAcceptableFloor) > 1e-6f)
                throw new InvalidOperationException("P3 Stage B evaluator floor changed");
            ValidateHistoricalControls(manifest.historicalControls);
            ValidateBaseCandidates(manifest.mapCandidates, manifest.historicalControls);
        }

        private static void ValidateHistoricalControls(List<R6PositiveControl> controls)
        {
            Dictionary<string, string> frozen = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "r6_control_hard_00_f0008", "46fe40c4c9af5848f6cad450840f7bae96d13c4d259307847a2cdcfc922a9c90" },
                { "r6_control_random_01_f0010", "eeb9360661573aa64d1f3cbb000ff8ecc3dd9ccb4d3ba05277b58aa26db688a8" },
                { "r6_control_random_05_f0016", "ddad5db3ec4ee58a758179eb8044ba80852871f9ba095e668b1abaea802bc6b7" },
                { "r6_control_hard_01_f0009", "126b14863d3b78a6a829e64b5185d081c09facec0824547ae9fbff1d26a7df0a" }
            };
            if (controls == null || controls.Count != frozen.Count) throw new InvalidOperationException("four controls required");
            foreach (R6PositiveControl control in controls)
                if (!frozen.TryGetValue(control.controlId, out string image)
                    || !string.Equals(image, control.sourceImageSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("historical control mismatch");
        }

        private static void ValidateBaseCandidates(List<P3StageBCandidate> candidates, List<R6PositiveControl> controls)
        {
            if (candidates.Select(item => item.candidateId).Distinct(StringComparer.Ordinal).Count() != candidates.Count)
                throw new InvalidOperationException("candidate IDs duplicated");
            foreach (R6PositiveControl control in controls)
            {
                List<P3StageBCandidate> group = candidates.Where(item => item.anchorControlId == control.controlId).ToList();
                if (group.Count != 53 || group.Count(item => item.stage == "anchor") != 1
                    || group.Count(item => item.stage == "coarse") != 26 || group.Count(item => item.stage == "wider") != 26)
                    throw new InvalidOperationException("base per-anchor grid mismatch");
                P3StageBCandidate anchor = group.Single(item => item.stage == "anchor");
                if (!anchor.consumedHistoricalControl || !Zero(anchor)) throw new InvalidOperationException("exact consumed anchor missing");
                foreach (P3StageBCandidate item in group.Where(item => item.stage != "anchor"))
                    if (!group.Any(other => other.stage == item.stage
                        && Mathf.Abs(other.longitudinalOffsetMm + item.longitudinalOffsetMm) <= 1e-6f
                        && Mathf.Abs(other.lateralOffsetMm + item.lateralOffsetMm) <= 1e-6f
                        && Mathf.Abs(other.yawDeg + item.yawDeg) <= 1e-6f))
                        throw new InvalidOperationException("base grid symmetry broken");
            }
        }

        private static bool Zero(P3StageBOffset value) => Mathf.Abs(value.longitudinalOffsetMm) <= 1e-6f
            && Mathf.Abs(value.lateralOffsetMm) <= 1e-6f && Mathf.Abs(value.yawDeg) <= 1e-6f;
    }
}
