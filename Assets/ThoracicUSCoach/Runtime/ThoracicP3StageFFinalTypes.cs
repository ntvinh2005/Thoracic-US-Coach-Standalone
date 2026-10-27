using System;
using System.Collections.Generic;
using UnityEngine;

namespace CSSALT.ThoracicUSCoach
{
    [Serializable]
    public sealed class P3StageFFinalManifest
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
        public int acceptedFreshCapturesPerTransition = 2;
        public int maximumRejectionProbeActions = 16;
        public List<R6PositiveControl> positiveControls = new List<R6PositiveControl>();
        public List<string> physicalControlSequence = new List<string>();
        public List<string> physicalMovementActions = new List<string>();
        public string rejectionProbeAction = "Lateral";
        public P3SyntheticFaultPlan syntheticFaultPlan = new P3SyntheticFaultPlan();
    }

    [Serializable]
    public sealed class P3SyntheticFaultPlan
    {
        public string faultId;
        public string trialId;
        public string issuedAction;
        public string actualExecutedAction;
        public P3ActionDelta issuedDelta = new P3ActionDelta();
        public P3ActionDelta actualDelta = new P3ActionDelta();
        public List<string> preVisitedCellIds = new List<string>();
        public string plannedFullMoveTargetCellId;
        public P3BestMemory preBestMemory = new P3BestMemory();
        public string liveTestArtifactPath;
        public string productionReducerArtifactPath;
    }

    [Serializable]
    public sealed class P3ActionDelta
    {
        public float lateralMm;
        public float longitudinalMm;
        public float yawDeg;

        public P3ActionDelta Clone()
        {
            return new P3ActionDelta
            {
                lateralMm = lateralMm,
                longitudinalMm = longitudinalMm,
                yawDeg = yawDeg
            };
        }
    }

    [Serializable]
    public sealed class P3BestMemory
    {
        public string recordId;
        public string canonicalSha256;

        public P3BestMemory Clone()
        {
            return new P3BestMemory { recordId = recordId, canonicalSha256 = canonicalSha256 };
        }
    }

    [Serializable]
    public sealed class P3SafeReturn
    {
        public string returnAnchorSource;
        public bool assumesFullIssuedMove;
        public bool contactValid;
        public bool workspaceValid;
    }

    [Serializable]
    public sealed class P3CausalActionHistoryEntry
    {
        public string issuedAction;
        public string executedAction;
        public P3ActionDelta executedDelta = new P3ActionDelta();
        public bool accepted;
        public bool completed;
        public bool feedbackObservedBeforeAppend;
    }

    [Serializable]
    public sealed class P3TransitionSemantics
    {
        public P3ActionDelta odometryDeltaApplied = new P3ActionDelta();
        public List<string> postVisitedCellIds = new List<string>();
        public P3BestMemory postBestMemory = new P3BestMemory();
        public string controllerDecision;
        public bool stopIssued;
        public P3CausalActionHistoryEntry causalHistoryTail = new P3CausalActionHistoryEntry();
    }

    [Serializable]
    public sealed class P3SyntheticTrial
    {
        public string trialId;
        public string faultId;
        public string evidenceDomain;
        public bool synthetic;
        public bool faultInjected;
        public bool decisionSupportEligible;
        public bool trainingEligible;
        public string issuedAction;
        public string actualExecutedAction;
        public bool actionAccepted;
        public bool actionCompleted;
        public bool factualResultingDeltaProvided;
        public P3ActionDelta issuedDelta = new P3ActionDelta();
        public P3ActionDelta actualDelta = new P3ActionDelta();
        public P3ActionDelta odometryDeltaApplied = new P3ActionDelta();
        public List<string> preVisitedCellIds = new List<string>();
        public List<string> postVisitedCellIds = new List<string>();
        public string plannedFullMoveTargetCellId;
        public P3BestMemory preBestMemory = new P3BestMemory();
        public P3BestMemory postBestMemory = new P3BestMemory();
        public P3SafeReturn safeReturn = new P3SafeReturn();
        public string controllerDecision;
        public bool stopIssued;
        public List<P3CausalActionHistoryEntry> causalActionHistory = new List<P3CausalActionHistoryEntry>();
        public P3TransitionSemantics liveTransitionSemantics = new P3TransitionSemantics();
        public P3TestEvidence bridgeTestEvidence = new P3TestEvidence();
        public P3TestEvidence productionReducerTestEvidence = new P3TestEvidence();
    }

    [Serializable]
    public sealed class P3TestEvidence
    {
        public string exportedBy;
        public string assembly;
        public string module;
        public string testName;
        public string result;
        public string sourceCommit;
        public string artifact;
        public string artifactSha256;
    }

    [Serializable]
    public sealed class P3LiveTestArtifact
    {
        public int schemaVersion = 1;
        public string sourceCommit;
        public string exportedBy;
        public string assembly;
        public string testName;
        public string result;
        public P3BridgeFeedback bridgeFeedback = new P3BridgeFeedback();
    }

    [Serializable]
    public sealed class P3BridgeFeedback
    {
        public bool accepted;
        public bool completed;
        public bool factualResultingDeltaProvided;
        public P3ActionDelta actualDelta = new P3ActionDelta();
    }

    [Serializable]
    public sealed class P3ProductionReducerArtifact
    {
        public int schemaVersion = 1;
        public string sourceCommit;
        public string exportedBy;
        public string module;
        public string testName;
        public string result;
        public P3SyntheticTrial trial = new P3SyntheticTrial();
    }

    public static class P3ActionFeedbackTransition
    {
        public const string FaultId = "P3_ACCEPTED_INCOMPLETE_AFTER_PARTIAL_EXECUTION_V1";

        public static P3SyntheticTrial ApplyAcceptedIncomplete(P3SyntheticFaultPlan plan)
        {
            if (plan == null || plan.faultId != FaultId)
                throw new InvalidOperationException("P3 deterministic fault plan identity mismatch");
            if (plan.issuedDelta == null || plan.actualDelta == null
                || (Mathf.Approximately(plan.issuedDelta.lateralMm, 0f)
                    && Mathf.Approximately(plan.issuedDelta.longitudinalMm, 0f)
                    && Mathf.Approximately(plan.issuedDelta.yawDeg, 0f)))
                throw new InvalidOperationException("P3 deterministic fault requires a nonzero issued delta");
            if (Mathf.Approximately(plan.issuedDelta.lateralMm, plan.actualDelta.lateralMm)
                && Mathf.Approximately(plan.issuedDelta.longitudinalMm, plan.actualDelta.longitudinalMm)
                && Mathf.Approximately(plan.issuedDelta.yawDeg, plan.actualDelta.yawDeg))
                throw new InvalidOperationException("P3 incomplete fault must differ from the issued move");
            if (plan.preBestMemory == null || string.IsNullOrWhiteSpace(plan.preBestMemory.recordId)
                || string.IsNullOrWhiteSpace(plan.preBestMemory.canonicalSha256))
                throw new InvalidOperationException("P3 confirmed best memory missing");

            List<string> visited = new List<string>(plan.preVisitedCellIds ?? new List<string>());
            P3BestMemory best = plan.preBestMemory.Clone();
            P3CausalActionHistoryEntry feedback = new P3CausalActionHistoryEntry
            {
                issuedAction = plan.issuedAction,
                executedAction = plan.actualExecutedAction,
                executedDelta = plan.actualDelta.Clone(),
                accepted = true,
                completed = false,
                feedbackObservedBeforeAppend = true
            };
            P3TransitionSemantics transition = new P3TransitionSemantics
            {
                odometryDeltaApplied = plan.actualDelta.Clone(),
                postVisitedCellIds = new List<string>(visited),
                postBestMemory = best.Clone(),
                controllerDecision = "ABSTAIN",
                stopIssued = false,
                causalHistoryTail = Clone(feedback)
            };
            return new P3SyntheticTrial
            {
                trialId = plan.trialId,
                faultId = plan.faultId,
                evidenceDomain = "DETERMINISTIC_FAULT_INJECTION",
                synthetic = true,
                faultInjected = true,
                decisionSupportEligible = false,
                trainingEligible = false,
                issuedAction = plan.issuedAction,
                actualExecutedAction = plan.actualExecutedAction,
                actionAccepted = true,
                actionCompleted = false,
                factualResultingDeltaProvided = false,
                issuedDelta = plan.issuedDelta.Clone(),
                actualDelta = plan.actualDelta.Clone(),
                odometryDeltaApplied = plan.actualDelta.Clone(),
                preVisitedCellIds = new List<string>(visited),
                postVisitedCellIds = new List<string>(visited),
                plannedFullMoveTargetCellId = plan.plannedFullMoveTargetCellId,
                preBestMemory = best.Clone(),
                postBestMemory = best.Clone(),
                safeReturn = new P3SafeReturn
                {
                    returnAnchorSource = "last_confirmed_actual_pose",
                    assumesFullIssuedMove = false,
                    contactValid = true,
                    workspaceValid = true
                },
                controllerDecision = "ABSTAIN",
                stopIssued = false,
                causalActionHistory = new List<P3CausalActionHistoryEntry> { Clone(feedback) },
                liveTransitionSemantics = Clone(transition)
            };
        }

        private static P3CausalActionHistoryEntry Clone(P3CausalActionHistoryEntry value)
        {
            return new P3CausalActionHistoryEntry
            {
                issuedAction = value.issuedAction,
                executedAction = value.executedAction,
                executedDelta = value.executedDelta.Clone(),
                accepted = value.accepted,
                completed = value.completed,
                feedbackObservedBeforeAppend = value.feedbackObservedBeforeAppend
            };
        }

        private static P3TransitionSemantics Clone(P3TransitionSemantics value)
        {
            return new P3TransitionSemantics
            {
                odometryDeltaApplied = value.odometryDeltaApplied.Clone(),
                postVisitedCellIds = new List<string>(value.postVisitedCellIds),
                postBestMemory = value.postBestMemory.Clone(),
                controllerDecision = value.controllerDecision,
                stopIssued = value.stopIssued,
                causalHistoryTail = Clone(value.causalHistoryTail)
            };
        }
    }

    public static class ThoracicP3StageFFinalProtocol
    {
        public const string ProtocolId = "G2_PHASE2_FINAL_SPRINT_P3_V1";
        public const string Mode = "P3_STAGE_F_FINAL";
        public const string ReviewedDevBaseCommit = "d362c50769068f2c57014aa26a0c9ace8abd2921";
        public const string CaptureBranch = "codex/p2-unity-final";
        public const string HistoricalF3Verdict = "IMAGE_POSE_FRESHNESS_FAIL";
        public const float FrozenAcceptableFloor = 0.5912307500839233f;
        public const string FrozenSceneSha256 = "6c86ff30a42e9719aff712a9e48b2ca6b5cce6004bace055abbc82b64db9a02d";
        public const string FrozenEvaluatorSha256 = "7480ab4ece46fbab6b70344b25212eb0c90621b2d458ed5c343e299a59d597fc";
        public const string AuditProtocolCanonicalSha256 = "270a10e7b39d3318e9c6470d98a0620b2091a88b42ba75f011ed78d810ee9b1b";

        public static void Validate(P3StageFFinalManifest manifest)
        {
            if (manifest == null || manifest.protocolId != ProtocolId || manifest.mode != Mode)
                throw new InvalidOperationException("P3 Stage F-Final protocol/mode mismatch");
            if (manifest.reviewedDevBaseCommit != ReviewedDevBaseCommit
                || manifest.captureBranch != CaptureBranch)
                throw new InvalidOperationException("P3 Stage F-Final source lineage mismatch");
            if (Mathf.Abs(manifest.acceptableScoreFloor - FrozenAcceptableFloor) > 1e-6f)
                throw new InvalidOperationException("P3 frozen evaluator floor changed");
            if (manifest.auditProtocolCanonicalSha256 != AuditProtocolCanonicalSha256)
                throw new InvalidOperationException("P3 audit protocol hash mismatch");
            if (manifest.physicalControlSequence == null || manifest.physicalControlSequence.Count != 5
                || manifest.physicalControlSequence[0] != manifest.physicalControlSequence[2])
                throw new InvalidOperationException("P3 physical A-B-A-C-D control sequence invalid");
            if (manifest.physicalMovementActions == null
                || !manifest.physicalMovementActions.Contains("Lateral")
                || !manifest.physicalMovementActions.Contains("Cranial")
                || !manifest.physicalMovementActions.Contains("RotateClockwise")
                || !manifest.physicalMovementActions.Contains("Observe"))
                throw new InvalidOperationException("P3 physical movement coverage incomplete");
            if (manifest.acceptedFreshCapturesPerTransition < 2
                || manifest.acceptedFreshCapturesPerTransition > 3)
                throw new InvalidOperationException("P3 accepted capture count invalid");
            if (manifest.maximumRejectionProbeActions < 1 || manifest.maximumRejectionProbeActions > 20)
                throw new InvalidOperationException("P3 physical rejection budget invalid");
            if (manifest.syntheticFaultPlan == null
                || manifest.syntheticFaultPlan.faultId != P3ActionFeedbackTransition.FaultId)
                throw new InvalidOperationException("P3 synthetic fault plan mismatch");
        }
    }
}
