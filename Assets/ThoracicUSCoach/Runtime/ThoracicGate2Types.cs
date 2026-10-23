using System;
using System.Collections.Generic;
using UnityEngine;

namespace CSSALT.ThoracicUSCoach
{
    [Serializable]
    public sealed class Gate2Phase1Case
    {
        public string caseId;
        public int order;
        public string family;
        public int seed;
        public int startStratum;
        public string expectedStartLevel;
        public float plannedInterpolation;
        public float plannedLateralMm;
    }

    [Serializable]
    public sealed class Gate2Phase1Manifest
    {
        public int schemaVersion;
        public string protocolId;
        public string warning;
        public string sourceCommit;
        public string scene;
        public string unityVersion;
        public string checkpoint;
        public string checkpointSha256;
        public string policyServer;
        public string policyServerSha256;
        public float handoffThreshold;
        public bool phaseOneOnly;
        public int stepBudget;
        public string landmarkCheckpoint;
        public string stopCheckpoint;
        public bool hybridEnabled;
        public int preflightMaxSteps;
        public int requiredCaseCount;
        public int requiredPerStratum;
        public int minimumCorrectPreciseHandoffs;
        public int maximumWrongLevelHandoffs;
        public int maximumConfidentWrongLevelHandoffs;
        public float minimumRecognitionAfterPreciseEntry;
        public string output;
        public List<Gate2Phase1Case> cases = new List<Gate2Phase1Case>();
    }

    [Serializable]
    public sealed class Gate2StartAudit
    {
        public string expectedLevel;
        public string actualLevel;
        public bool expectedLabelMatches;
        public bool contactValid;
        public bool workspaceValid;
        public bool t7Reachable;
        public float t7DistanceMm;
        public float t7LongitudinalOffsetMm;
        public float t7LateralOffsetMm;
        public float t7PreciseRadiusMm;
        public float closestAttainableT7DistanceMm;
        public float longitudinalTravelToPreciseRegionMm;
        public int minimumLongitudinalSteps;
        public string t7Direction;
        public float caudalAxisAlignment;
        public float lateralAxisAlignment;
        public Vector3 contactPosition;
        public Vector3 t7TargetPosition;
        public Vector3 caudalAxis;
        public Vector3 lateralAxis;
        public Quaternion probeRotation;
        public float longitudinalHalfExtentMm;
        public float stepMm;
        public int preflightMaxSteps;
    }

    [Serializable]
    public sealed class Gate2PreflightOutcome
    {
        public string caseId;
        public int order;
        public int seed;
        public int startStratum;
        public float plannedInterpolation;
        public float plannedLateralMm;
        public bool placementSucceeded;
        public bool valid;
        public Gate2StartAudit start = new Gate2StartAudit();
        public bool physicalReachabilityTested;
        public bool physicalReachedPreciseRegion;
        public int physicalSteps;
        public int physicalRejectedMoves;
        public bool physicalUnsafe;
        public float closestObservedT7DistanceMm = -1f;
        public List<Gate2PreflightStep> physicalTrace = new List<Gate2PreflightStep>();
        public string failureReason;
    }

    [Serializable]
    public sealed class Gate2PreflightStep
    {
        public int step;
        public string action;
        public bool accepted;
        public bool contactValid;
        public bool workspaceValid;
        public bool safetyFailure;
        public float t7DistanceMm;
        public bool t7LevelAcceptable;
    }

    [Serializable]
    public sealed class Gate2CaseOutcome
    {
        public string caseId;
        public int order;
        public string family;
        public int seed;
        public int startStratum;
        public float plannedInterpolation;
        public float plannedLateralMm;
        public Gate2StartAudit start = new Gate2StartAudit();
        public string rawResult;
        public string privilegedTrace;
        public bool completed;
        public bool phaseOneHandoff;
        public bool correctPreciseHandoff;
        public bool wrongLevelHandoff;
        public bool confidentWrongLevelHandoff;
        public bool preciseRegionEntered;
        public bool recognitionAfterPreciseEntry;
        public int firstPreciseEntryStep = -1;
        public int handoffStep = -1;
        public float handoffConfidence;
        public float handoffT7DistanceMm = -1f;
        public string handoffNearestLevel;
        public bool handoffNearestLevelCorrect;
        public bool handoffT7LevelAcceptable;
        public bool contactValidAtTerminal;
        public bool workspaceValidAtTerminal;
        public int rejectedMovements;
        public int contactViolations;
        public int workspaceViolations;
        public int unsafeAcceptedOrTerminalPoses;
        public int privilegedUnsafeFrames;
        public int safetyFailures;
        public bool timeout;
        public bool abstention;
        public bool failure;
        public int actorPrivilegedLeakage;
        public bool rawVerdictMismatch;
        public List<Gate2NamedCount> actionCounts = new List<Gate2NamedCount>();
        public int steps;
        public string failureReason;
    }

    [Serializable]
    public sealed class Gate2SmokeOutcome
    {
        public string family;
        public int seed;
        public int stepBudget;
        public string rawResult;
        public string privilegedTrace;
        public int actorActions;
        public bool infrastructurePass;
        public string failureReason;
    }

    [Serializable]
    public sealed class Gate2CompletionSummary
    {
        public int requiredCases;
        public int completedCases;
        public int t6Starts;
        public int t7Starts;
        public int t8Starts;
        public int outsideStarts;
        public int validStarts;
        public int correctPreciseHandoffs;
        public int wrongLevelHandoffs;
        public int confidentWrongLevelHandoffs;
        public int preciseRegionEntries;
        public int recognitionsAfterPreciseEntry;
        public float recognitionAfterPreciseEntryRate;
        public int finalContactValidCount;
        public int finalWorkspaceValidCount;
        public int unsafeAcceptedOrTerminalPoses;
        public int privilegedUnsafeFrames;
        public int safetyFailures;
        public int rejectedMovements;
        public int contactViolations;
        public int workspaceViolations;
        public int timeouts;
        public int abstentions;
        public int failures;
        public int actorPrivilegedLeakage;
        public int rawVerdictMismatches;
        public List<Gate2NamedCount> actionCounts = new List<Gate2NamedCount>();
        public List<Gate2StratumSummary> perStratum = new List<Gate2StratumSummary>();
        public bool protocolComplete;
        public bool thresholdsPass;
    }

    [Serializable]
    public sealed class Gate2NamedCount
    {
        public string name;
        public int count;
    }

    [Serializable]
    public sealed class Gate2StratumSummary
    {
        public int startStratum;
        public string expectedLevel;
        public int cases;
        public int validStarts;
        public int correctPreciseHandoffs;
        public int wrongLevelHandoffs;
        public int abstentions;
    }

    [Serializable]
    public sealed class Gate2Phase1Report
    {
        public int schemaVersion = 1;
        public string protocolId;
        public string generatedUtc;
        public string unityVersion;
        public string scene;
        public string manifestSourceCommit;
        public string executionCommit;
        public string runNonce;
        public string manifest;
        public string manifestSha256;
        public string checkpoint;
        public string checkpointSha256;
        public string policyServer;
        public string policyServerSha256;
        public float handoffThreshold;
        public bool phaseOneOnly;
        public int stepBudget;
        public float stepMm;
        public float maxPenetrationMm;
        public float t7PreciseRadiusMm;
        public string t7LevelAcceptableDefinition;
        public string actorInputContract = "requestId,episodeId,reset,imagePath";
        public bool complete;
        public string infrastructureFailure;
        public Gate2SmokeOutcome smoke = new Gate2SmokeOutcome();
        public List<Gate2PreflightOutcome> preflight = new List<Gate2PreflightOutcome>();
        public List<Gate2CaseOutcome> cases = new List<Gate2CaseOutcome>();
        public Gate2CompletionSummary summary = new Gate2CompletionSummary();
    }
}
