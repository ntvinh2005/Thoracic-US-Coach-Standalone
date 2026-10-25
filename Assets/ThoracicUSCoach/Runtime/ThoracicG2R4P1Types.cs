using System;
using System.Collections.Generic;
using UnityEngine;

namespace CSSALT.ThoracicUSCoach
{
    [Serializable]
    public sealed class G2R4P1Case
    {
        public string caseId;
        public string arm;
        public string familyId;
        public int captureSeed;
        public bool countsAsNewIndependentFamily;
        public string planningReference;
        public U08P1StartConfig baseStart;
        public List<string> prepositionActions = new List<string>();
        public List<string> trajectoryActions = new List<string>();
    }

    [Serializable]
    public sealed class G2R4P1Manifest
    {
        public int schemaVersion = 1;
        public string protocolId;
        public string scene;
        public string unityVersion;
        public string datasetRunId;
        public string outputDirectory;
        public float acceptableScoreFloor;
        public int maxIndependentFamilies = 16;
        public int maxObservationsPerIndependentFamily = 121;
        public int minHardCausalObservations = 48;
        public int maxHardCausalObservations = 72;
        public List<string> historicallyLockedFamilies = new List<string>();
        public List<G2R4P1Case> cases = new List<G2R4P1Case>();
    }

    [Serializable]
    public sealed class G2R4P1ActionAudit
    {
        public int sequenceIndex;
        public string stage;
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
    }

    [Serializable]
    public sealed class G2R4P1ObservationAudit
    {
        public int index;
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
        public float t7DistanceMm;
        public bool t7LevelAcceptable;
        public ScoreBreakdown score = new ScoreBreakdown();
        public float marginToAcceptableFloor;
        public bool finalTargetAcceptable;
        public bool evaluatorFloorAgreement;
    }

    [Serializable]
    public sealed class G2R4P1CaseResult
    {
        public string caseId;
        public string arm;
        public string familyId;
        public int captureSeed;
        public bool countsAsNewIndependentFamily;
        public string planningReference;
        public bool basePlacementValid;
        public bool randomizedStartValid;
        public bool targetAcceptableAtStart;
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
        public List<G2R4P1ActionAudit> actions = new List<G2R4P1ActionAudit>();
        public List<G2R4P1ObservationAudit> observations = new List<G2R4P1ObservationAudit>();
    }

    [Serializable]
    public sealed class G2R4P1UnityReport
    {
        public int schemaVersion = 1;
        public string taskId = "G2-R4-REVISED-P1";
        public string protocolId;
        public string generatedUtc;
        public string executionCommit;
        public string unityVersion;
        public string scene;
        public string sceneSha256;
        public string evaluatorSha256;
        public string active2DControllerSha256;
        public string captureControllerSha256;
        public string manifest;
        public string manifestSha256;
        public float acceptableScoreFloor;
        public int requestedCases;
        public int completedCases;
        public int failedCases;
        public int newIndependentFamilies;
        public int newFamiliesWithPositive;
        public int totalObservations;
        public int hardCausalObservations;
        public int acceptedActions;
        public int rejectedActions;
        public int incompleteActions;
        public int safetyFailures;
        public int contactViolations;
        public int workspaceViolations;
        public bool complete;
        public string infrastructureFailure;
        public List<G2R4P1CaseResult> cases = new List<G2R4P1CaseResult>();
    }
}
