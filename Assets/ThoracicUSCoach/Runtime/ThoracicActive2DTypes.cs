using System;
using System.Collections.Generic;
using UnityEngine;

namespace CSSALT.ThoracicUSCoach
{
    public enum Active2DAction
    {
        Cranial,
        Caudal,
        Medial,
        Lateral,
        RotateClockwise,
        RotateCounterClockwise,
        PhaseHandoff,
        Stop
    }

    public enum Active2DPhase
    {
        Idle,
        Initializing,
        LongitudinalLocalization,
        LateralAcquisition,
        FinalSuccess,
        FinalFailure
    }

    [Serializable]
    public sealed class Active2DActorRequest
    {
        public int schemaVersion = 1;
        public string contract = "active2d_image_only_v1";
        public string requestId;
        public string episodeId;
        public bool reset;
        public string imagePath;
    }

    [Serializable]
    public sealed class Active2DActorResponse
    {
        public int schemaVersion = 1;
        public string contract;
        public string requestId;
        public string episodeId;
        public string action;
        public string phase;
        public float confidence;
        public float uncertainty;
        public int memoryStep;
        public string model;
        public string error;
    }

    [Serializable]
    public sealed class Active2DStreamFrame
    {
        public int index;
        public string image;
        public string imageSha256;
        public string issuedAction;
        public bool actionAccepted;
        public bool episodeReset;
    }

    [Serializable]
    public sealed class Active2DStreamManifest
    {
        public int schemaVersion = 1;
        public string contract = "active2d_image_only_v1";
        public string experimentId = "thoracic_active2d_image_only_v1_20261023";
        public string datasetRunId;
        public string episodeId;
        public string split;
        public string family;
        public int seed;
        public string captureStatus = "complete";
        public string imageSource = "primary-ultrasound-camera-1024";
        public string policyInputs = "clean ultrasound image stream plus policy-owned memory";
        public List<Active2DStreamFrame> frames = new List<Active2DStreamFrame>();
    }

    [Serializable]
    public sealed class Active2DPrivilegedFrame
    {
        public int index;
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
        public bool finalTargetAcceptable;
        public float finalTargetScore;
    }

    [Serializable]
    public sealed class Active2DPrivilegedAnnotations
    {
        public int schemaVersion = 1;
        public string contract = "active2d_image_only_v1";
        public string episodeId;
        public string warning = "TRAINING/EVALUATION ONLY. Never pass this file or its fields to the actor.";
        public Vector3 workspaceOrigin;
        public Vector3 actuatorCranialAxis;
        public Vector3 actuatorLateralAxis;
        public Vector3 t7TargetPosition;
        public float t7PreciseRadiusMm;
        public float stepMm;
        public float longitudinalHalfExtentMm;
        public float lateralHalfExtentMm;
        public List<Active2DPrivilegedFrame> frames = new List<Active2DPrivilegedFrame>();
    }

    [Serializable]
    public sealed class Active2DRunTrace
    {
        public int step;
        public string action;
        public string phase;
        public bool accepted;
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
        public float confidence;
        public float uncertainty;
        public string image;
    }

    [Serializable]
    public sealed class Active2DRunResult
    {
        public int schemaVersion = 1;
        public string contract = "active2d_image_only_v1";
        public string mode = "LEARNED / IMAGE-ONLY ACTIVE 2D";
        public string generatedUtc;
        public string episodeId;
        public string startDescription;
        public string finalPhase;
        public bool phaseOneHandoff;
        public bool correctLevelAtHandoff;
        public bool explicitStop;
        public bool finalTargetPass;
        public bool overallPass;
        public bool finalContactValid;
        public bool finalWorkspaceValid;
        public int steps;
        public int rejectedMovements;
        public int contactViolations;
        public int workspaceViolations;
        public string failureReason;
        public List<Active2DRunTrace> trace = new List<Active2DRunTrace>();
    }
}
