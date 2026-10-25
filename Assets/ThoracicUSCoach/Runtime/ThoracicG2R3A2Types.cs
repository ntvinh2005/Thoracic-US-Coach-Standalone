using System;
using System.Collections.Generic;
using UnityEngine;

namespace CSSALT.ThoracicUSCoach
{
    [Serializable]
    public sealed class G2R3A2Manifest
    {
        public int schemaVersion = 1;
        public string protocolId;
        public string generatedUtc;
        public string scene;
        public string unityVersion;
        public string outputDirectory;
        public float acceptableScoreFloor;
        public float positionToleranceMm;
        public float rotationToleranceDeg;
        public float contactPositionToleranceMm;
        public float signedDepthToleranceMm;
        public int repeatRenders;
        public string decisionProcedure;
        public string selectionSourceSha256;
        public string sceneSha256;
        public string evaluatorSha256;
        public string active2DControllerSha256;
        public List<G2R3A2Case> cases = new List<G2R3A2Case>();
    }

    [Serializable]
    public sealed class G2R3A2Case
    {
        public string caseId;
        public string family;
        public int sourceFrameIndex;
        public string selectionReason;
        public string sourceImage;
        public string sourceImageSha256;
        public float originalScore;
        public bool originalAcceptable;
        public Vector3 probePosition;
        public Quaternion probeRotation;
        public Vector3 contactPosition;
        public float signedDepthMm;
        public bool originalContactValid;
        public bool originalWorkspaceValid;
        public Vector3 workspaceOrigin;
        public Vector3 actuatorCranialAxis;
        public Vector3 actuatorLateralAxis;
        public float longitudinalHalfExtentMm;
        public float lateralHalfExtentMm;
    }

    [Serializable]
    public sealed class G2R3A2Observation
    {
        public int repeatIndex;
        public string image;
        public string imageSha256;
        public Vector3 actualProbePosition;
        public Quaternion actualProbeRotation;
        public Vector3 actualContactPosition;
        public float actualSignedDepthMm;
        public float positionErrorMm;
        public float rotationErrorDeg;
        public float contactPositionErrorMm;
        public float signedDepthErrorMm;
        public bool contactValid;
        public bool workspaceValid;
        public ScoreBreakdown score;
        public bool targetAcceptable;
        public float scoreDeltaFromOriginal;
    }

    [Serializable]
    public sealed class G2R3A2CaseResult
    {
        public string caseId;
        public string family;
        public int sourceFrameIndex;
        public string selectionReason;
        public string sourceImage;
        public string sourceImageSha256;
        public float originalScore;
        public bool originalAcceptable;
        public bool rigidContactApplied;
        public bool restorationValid;
        public string invalidReason;
        public List<G2R3A2Observation> observations = new List<G2R3A2Observation>();
    }

    [Serializable]
    public sealed class G2R3A2UnityReport
    {
        public int schemaVersion = 1;
        public string protocolId;
        public string generatedUtc;
        public string executionCommit;
        public string unityVersion;
        public string scene;
        public string manifest;
        public string manifestSha256;
        public string sceneSha256;
        public string evaluatorSha256;
        public string active2DControllerSha256;
        public string diagnosticControllerSha256;
        public float acceptableScoreFloor;
        public float positionToleranceMm;
        public float rotationToleranceDeg;
        public float contactPositionToleranceMm;
        public float signedDepthToleranceMm;
        public int repeatRenders;
        public bool complete;
        public string infrastructureFailure;
        public List<G2R3A2CaseResult> cases = new List<G2R3A2CaseResult>();
    }
}
