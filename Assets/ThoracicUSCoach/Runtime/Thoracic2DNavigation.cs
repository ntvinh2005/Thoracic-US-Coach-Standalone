using System;
using UnityEngine;

namespace CSSALT.ThoracicUSCoach
{
    public enum Guided2DPhase
    {
        Idle,
        Initializing,
        LongitudinalSearch,
        T7Acquired,
        LateralSearch,
        FinePlacement,
        FinalSuccess,
        FinalFailure
    }

    public enum Guided2DStart
    {
        NearT6,
        NearT8,
        RandomT6T8
    }

    [Serializable]
    public sealed class Guided2DTracePoint
    {
        public int step;
        public string utc;
        public string phase;
        public string direction;
        public Vector3 probePosition;
        public Vector3 contactPosition;
        public float longitudinalMm;
        public float lateralMm;
        public float distanceToPhaseTargetMm;
        public float stepDistanceMm;
        public bool contactValid;
        public bool workspaceValid;
        public bool accepted;
        public string note;
    }

    [Serializable]
    public sealed class Guided2DRunResult
    {
        public int schemaVersion = 1;
        public string mode = "GUIDED / ORACLE-ASSISTED 2D NAVIGATION";
        public string generatedUtc;
        public string unityVersion;
        public string scene;
        public string start;
        public int seed;
        public bool longitudinalPass;
        public bool lateralPass;
        public bool finePlacementPass;
        public bool finalStopPass;
        public bool overallPass;
        public int contactViolations;
        public int workspaceViolations;
        public int rejectedMovements;
        public int totalSteps;
        public float longitudinalDisplacementMm;
        public float lateralDisplacementMm;
        public float maximumStepDistanceMm;
        public float finalReferenceScore;
        public float requiredReferenceScore;
        public string failureReason;
        public string lastValidatedTransition;
        public System.Collections.Generic.List<Guided2DTracePoint> trace = new System.Collections.Generic.List<Guided2DTracePoint>();
    }

    [Serializable]
    public sealed class Guided2DBridgeRequest
    {
        public string requestId;
        public string command;
        public string start = "NearT6";
        public int seed = 20261022;
        public float speed = 1f;
        public string manualDirection;
    }

    [Serializable]
    public sealed class Guided2DBridgeResponse
    {
        public string requestId;
        public string phase;
        public string status;
        public bool running;
        public bool paused;
        public bool done;
        public bool success;
        public string resultPath;
        public string error;
    }

    public static class Thoracic2DNavigation
    {
        // Active2D translations are 3 mm by default. Requiring at least ten
        // percent progress (with a 0.25 mm absolute floor) rejects silent
        // no-ops while leaving ample allowance for valid curved-surface motion.
        public const float MinimumProgressAbsoluteMm = 0.25f;
        public const float MinimumProgressFraction = 0.10f;

        public static Vector3 TangentToward(Vector3 from, Vector3 to, Vector3 normal)
        {
            Vector3 tangent = Vector3.ProjectOnPlane(to - from, normal);
            return tangent.sqrMagnitude > 0.000001f ? tangent.normalized : Vector3.zero;
        }

        public static float SignedCoordinate(Vector3 origin, Vector3 point, Vector3 axis)
        {
            return Vector3.Dot(point - origin, axis.normalized);
        }

        public static bool IsTeleportLike(float actualDistanceMm, float commandedStepMm, float projectionAllowanceMm)
        {
            return actualDistanceMm > Mathf.Max(0.01f, commandedStepMm) + Mathf.Max(0f, projectionAllowanceMm);
        }

        public static bool IsContactValid(bool anyHit, float signedDepthMm, float maxPenetrationMm)
        {
            return anyHit && Mathf.Abs(signedDepthMm) <= Mathf.Max(0f, maxPenetrationMm);
        }

        public static float MinimumMeaningfulProgressMm(float requestedDisplacementMm)
        {
            return Mathf.Max(MinimumProgressAbsoluteMm,
                Mathf.Abs(requestedDisplacementMm) * MinimumProgressFraction);
        }

        public static bool HasMeaningfulProgress(
            float requestedDisplacementMm,
            float achievedDisplacementMm)
        {
            if (Mathf.Abs(requestedDisplacementMm) <= Mathf.Epsilon) return true;
            return achievedDisplacementMm >= MinimumMeaningfulProgressMm(requestedDisplacementMm);
        }

        public static bool IsTerminalAcceptanceValid(
            bool domainSuccess,
            bool contactValid,
            bool workspaceValid)
        {
            return domainSuccess && contactValid && workspaceValid;
        }

        public static bool HasTwoAxisMotion(float longitudinalMm, float lateralMm, float epsilonMm = 0.25f)
        {
            return Mathf.Abs(longitudinalMm) > epsilonMm && Mathf.Abs(lateralMm) > epsilonMm;
        }
    }
}
