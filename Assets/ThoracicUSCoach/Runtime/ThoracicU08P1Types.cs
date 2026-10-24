using System;
using System.Collections.Generic;
using UnityEngine;

namespace CSSALT.ThoracicUSCoach
{
    [Serializable]
    public sealed class U08P1StartConfig
    {
        public string startId;
        public int order;
        public string sourceEpisode;
        public string sourceFamily;
        public int seed;
        public string configSha256;
        public string streamManifestSha256;
        public string privilegedAnnotationsSha256;
        public string startAuditSha256;
        public Vector3 probePosition;
        public Quaternion probeRotation;
        public Vector3 workspaceOrigin;
        public Vector3 actuatorCranialAxis;
        public Vector3 actuatorLateralAxis;
        public float longitudinalHalfExtentMm;
        public float lateralHalfExtentMm;
    }

    [Serializable]
    public sealed class U08P1Manifest
    {
        public int schemaVersion;
        public string protocolId;
        public string warning;
        public string sourceCommit;
        public string scene;
        public string unityVersion;
        public string phaseOneCheckpoint;
        public string phaseOneCheckpointSha256;
        public string stopCheckpoint;
        public string stopCheckpointSha256;
        public float stopThreshold;
        public string actionCheckpoint;
        public string actionCheckpointSha256;
        public string policyServer;
        public string policyServerSha256;
        public int stepBudget;
        public int requiredStarts;
        public string outputDirectory;
        public List<U08P1StartConfig> starts = new List<U08P1StartConfig>();
    }

    [Serializable]
    public sealed class U08P1StartAudit
    {
        public string startId;
        public string expectedConfigSha256;
        public Vector3 requestedProbePosition;
        public Quaternion requestedProbeRotation;
        public Vector3 actualProbePosition;
        public Quaternion actualProbeRotation;
        public float positionErrorMm;
        public float rotationErrorDeg;
        public bool contactValid;
        public bool workspaceValid;
        public bool t7LevelAcceptable;
        public bool targetInitiallyAcceptable;
        public float t7DistanceMm;
        public float initialTargetScore;
        public bool valid;
        public string failureReason;
    }

    [Serializable]
    public sealed class U08P1EpisodeOutcome
    {
        public string startId;
        public string policy;
        public int seed;
        public string episodeId;
        public U08P1StartAudit start = new U08P1StartAudit();
        public string rawResult;
        public string privilegedTrace;
        public bool completed;
        public bool explicitStop;
        public bool acquisitionSuccess;
        public bool wrongStop;
        public bool targetObserved;
        public int firstTargetObservationStep = -1;
        public int phaseTwoSteps;
        public int acceptedMovements;
        public int rejectedMovements;
        public int candidateChoices;
        public int fallbacks;
        public int returnToBestActions;
        public int safetyFailures;
        public int contactViolations;
        public int workspaceViolations;
        public bool finalContactValid;
        public bool finalWorkspaceValid;
        public string failureReason;
    }

    [Serializable]
    public sealed class U08P1UnityReport
    {
        public int schemaVersion = 1;
        public string protocolId;
        public string generatedUtc;
        public string unityVersion;
        public string scene;
        public string executionCommit;
        public string policy;
        public string manifest;
        public string manifestSha256;
        public string phaseOneCheckpointSha256;
        public string stopCheckpointSha256;
        public string actionCheckpointSha256;
        public string policyServerSha256;
        public float stopThreshold;
        public int stepBudget;
        public bool complete;
        public string infrastructureFailure;
        public List<U08P1EpisodeOutcome> episodes = new List<U08P1EpisodeOutcome>();
    }
}
