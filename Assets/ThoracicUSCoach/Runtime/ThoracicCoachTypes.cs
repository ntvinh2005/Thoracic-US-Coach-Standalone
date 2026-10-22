using System;
using System.Collections.Generic;
using UnityEngine;

namespace CSSALT.ThoracicUSCoach
{
    public enum CoachAction
    {
        Backward = -1,
        Stop = 0,
        Forward = 1
    }

    [Serializable]
    public sealed class ScoreBreakdown
    {
        public float anatomyRelationship;
        public float pleuraVisibility;
        public float targetRegion;
        public float acousticWindow;
        public float needleGeometry;
        public float shadowPenalty;
        public float contactPenalty;
        public float total;
        public int pleuraSamples;
        public int boneSamples;
        public int targetSamples;
        public int labeledSamples;
        public int gridSamples;
        public string notes;
    }

    [Serializable]
    public sealed class ProbePoseRecord
    {
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 contactPoint;
        public Vector3 surfaceNormal;
        public float caudalOffsetMm;
        public float lateralOffsetMm;
        public float yawDeg;
        public float tiltDeg;
        public float signedDepthMm;
        public float contactFraction;
    }

    [Serializable]
    public sealed class CandidateRecord
    {
        public string candidateId;
        public string anatomicalLevel = "T7 right";
        public string approach = "axial/transverse, provisional";
        public ProbePoseRecord pose = new ProbePoseRecord();
        public ScoreBreakdown score = new ScoreBreakdown();
        public string cleanImage;
        public string anatomyOverlay;
        public bool acceptable;
    }

    [Serializable]
    public sealed class CandidateManifest
    {
        public string generatedUtc;
        public string scene;
        public string curriculumBasis;
        public string reviewStatus = "Provisional; requires clinician review";
        public float acceptableScoreFloor;
        public List<CandidateRecord> candidates = new List<CandidateRecord>();
    }

    [Serializable]
    public sealed class CaptureRecord
    {
        public string frameId;
        public string sweepId;
        public string split;
        public int stepIndex;
        public string image;
        public string imageSha256;
        public string anatomyOverlay;
        public CoachAction teacherAction;
        public ProbePoseRecord pose;
        public ScoreBreakdown score;
        public ScoreBreakdown observedScore;
        public bool targetAcceptable;
        public bool teacherActionDefined = true;
        public bool contactValid;
        public bool workspaceValid;
        public float pathMm;
        public float pathProgress;
    }

    [Serializable]
    public sealed class TransitionRecord
    {
        public string transitionId;
        public string transitionKind;
        public string previousImage;
        public string previousImageSha256;
        public int previousIndex;
        public CoachAction executedAction;
        public bool actionWasExecuted = true;
        public string currentImage;
        public string currentImageSha256;
        public int currentIndex;
        public CoachAction teacherAction;
        public bool teacherActionDefined = true;
        public bool targetAcceptable;
        public bool contactValid;
        public bool workspaceValid;
        public bool boundaryClamped;
        public bool terminated;
        public bool truncated;
    }

    [Serializable]
    public sealed class TrajectoryConfigurationRecord
    {
        public string experimentId;
        public string split;
        public string configurationFamily;
        public string evaluationGroup = "heldout";
        public string anatomicalLevel = "T7 right";
        public string referenceViewDefinition = "Provisional axial/transverse simulator score; not clinically approved";
        public int randomSeed;
        public int sampleCount;
        public float centerCaudalOffsetMm;
        public float centerLateralOffsetMm;
        public float pathLengthMm;
        public float pathAngleDeg;
        public int pathDirection = 1;
        public float yawStartDeg;
        public float yawEndDeg;
        public float tiltStartDeg;
        public float tiltEndDeg;
        public string requestedStartSide;
        public int defaultStartIndex;
        public bool targetReachable;
        public string reachabilityClass;
        public float referenceScoreFloor;
    }

    [Serializable]
    public sealed class CaptureManifest
    {
        public int schemaVersion = 3;
        public string environmentContractVersion = OneDEnvironmentContract.Version;
        public string observationSchema = OneDEnvironmentContract.ObservationSchema;
        public string targetSemantics = OneDEnvironmentContract.TargetSemantics;
        public string configurationSha256;
        public string labelsSha256;
        public string generatedUtc;
        public string scene;
        public string experimentId;
        public string sweepId;
        public string split;
        public string captureStatus = "complete";
        public string splitRule = "Assigned to a trajectory configuration family before rendering; a family belongs to exactly one split";
        public TrajectoryConfigurationRecord trajectory = new TrajectoryConfigurationRecord();
        public List<CaptureRecord> frames = new List<CaptureRecord>();
        public List<TransitionRecord> transitions = new List<TransitionRecord>();
    }

    [Serializable]
    public sealed class PolicyRequest
    {
        public string requestId;
        public string imagePath;
        public string previousImagePath;
        public int previousAction;
    }

    [Serializable]
    public sealed class PolicyResponse
    {
        public string requestId;
        public int action;
        public float confidence;
        public float[] probabilities;
        public float recognitionProbability;
        public float latencyMs;
        public string model;
        public string error;
    }

    [Serializable]
    public sealed class EnvironmentRequest
    {
        public string requestId;
        public string command;
        public int action;
        public int startIndex;
        public int trajectorySeed;
        public int scenarioIndex;
        public bool regenerateTrajectory;
        public int maxSteps;
    }

    [Serializable]
    public sealed class FrozenScenarioDescription
    {
        public int schemaVersion = 1;
        public string environmentContractVersion = OneDEnvironmentContract.Version;
        public string targetSemantics = OneDEnvironmentContract.TargetSemantics;
        public string configurationSha256;
        public string labelsSha256;
        public int trajectorySeed;
        public TrajectoryConfigurationRecord trajectory;
        public List<ProbePoseRecord> poses = new List<ProbePoseRecord>();
        public List<float> pathMm = new List<float>();
        public List<int> acceptableIndices = new List<int>();
        public List<int> canonicalStarts = new List<int>();
        public string unityVersion;
        public string scene;
        public int renderWidth;
        public int renderHeight;
        public string colorSpace;
        public bool rendererFullySeeded;
    }

    [Serializable]
    public sealed class EnvironmentResponse
    {
        public string requestId;
        public string imagePath;
        public float reward;
        public bool done;
        public bool terminated;
        public bool truncated;
        public bool targetAcceptable;
        public bool regionEntered;
        public bool explicitStopSuccess;
        public bool constraintViolation;
        public bool contactValid;
        public bool workspaceValid;
        public bool patchBoundaryViolation;
        public bool invalidMotion;
        public float referenceScore;
        public float observedScore;
        public bool currentImageAcceptable;
        public float distanceToAcceptableMm;
        public float pathSpacingMm;
        public int pathIndex;
        public int pathCount;
        public string configurationFamily;
        public string configurationSha256;
        public string labelsSha256;
        public string targetSemantics;
        public FrozenScenarioDescription scenario;
        public string error;
    }

    public static class DiverseTrajectorySchedule
    {
        public const string HistoricalV2ExperimentId = "thoracic_diverse_v2_20261020";
        public const string DefaultExperimentId = "thoracic_1d_contract_v3_20261022";

        public static string SplitForSequence(int sequence)
        {
            int slot = Mathf.Abs(sequence) % 21;
            if (slot < 12) return "train";
            if (slot < 15) return "dev";
            return "test";
        }

        public static string EvaluationGroupForSequence(int sequence)
        {
            int slot = Mathf.Abs(sequence) % 21;
            if (slot < 12) return "training";
            if (slot < 15) return "development";
            return slot >= 18 ? "stress" : "heldout";
        }

        public static string FamilyForSequence(int sequence)
        {
            string split = SplitForSequence(sequence);
            string group = EvaluationGroupForSequence(sequence);
            return split + "_" + group + "_family_" + Mathf.Abs(sequence).ToString("D3");
        }

        public static int SeedForSequence(int sequence, int attempt = 0)
        {
            return 20261020 + sequence * 7919 + attempt * 104729;
        }
    }

    public static class SweepSplit
    {
        public static string ForSweep(string sweepId)
        {
            unchecked
            {
                uint hash = 2166136261;
                for (int i = 0; i < sweepId.Length; i++)
                {
                    hash ^= sweepId[i];
                    hash *= 16777619;
                }

                int bucket = (int)(hash % 10);
                if (bucket <= 6) return "train";
                if (bucket <= 8) return "dev";
                return "test";
            }
        }
    }
}
