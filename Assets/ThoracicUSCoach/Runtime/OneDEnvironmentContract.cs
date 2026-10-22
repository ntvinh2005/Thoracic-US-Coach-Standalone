using System;
using System.Collections.Generic;
using UnityEngine;

namespace CSSALT.ThoracicUSCoach
{
    /// <summary>
    /// Scene-independent definition of the version-3 one-dimensional task.
    /// Forward always increases the canonical corridor parameter and Backward
    /// always decreases it. Acceptability is an exact set, never a filled range.
    /// </summary>
    public static class OneDEnvironmentContract
    {
        public const string Version = "thoracic-1d-contract-v3";
        public const string TargetSemantics = "fixed-pose-exact-set";
        public const string ObservationSchema = "clean-current-png+previous-clean-png+executed-previous-action-v3";

        public static int StepIndex(int index, CoachAction action, int stepCount, int pathCount)
        {
            if (pathCount <= 0) return -1;
            int delta = action == CoachAction.Forward ? Mathf.Max(1, stepCount)
                : action == CoachAction.Backward ? -Mathf.Max(1, stepCount)
                : 0;
            return Mathf.Clamp(index + delta, 0, pathCount - 1);
        }

        public static bool IsAcceptable(IReadOnlyList<bool> acceptable, int index)
        {
            return acceptable != null && index >= 0 && index < acceptable.Count && acceptable[index];
        }

        public static int NearestAcceptableIndex(IReadOnlyList<bool> acceptable, int index)
        {
            if (acceptable == null) return -1;
            int nearest = -1;
            int bestDistance = int.MaxValue;
            for (int candidate = 0; candidate < acceptable.Count; candidate++)
            {
                if (!acceptable[candidate]) continue;
                int distance = Mathf.Abs(candidate - index);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                nearest = candidate;
            }
            return nearest;
        }

        public static float DistanceToAcceptableMm(
            IReadOnlyList<bool> acceptable,
            IReadOnlyList<float> pathMm,
            int index,
            float absentDistanceMm)
        {
            int nearest = NearestAcceptableIndex(acceptable, index);
            if (nearest < 0 || pathMm == null || index < 0 || index >= pathMm.Count || nearest >= pathMm.Count)
                return absentDistanceMm;
            return Mathf.Abs(pathMm[index] - pathMm[nearest]);
        }

        public static CoachAction OracleAction(IReadOnlyList<bool> acceptable, int index)
        {
            if (IsAcceptable(acceptable, index)) return CoachAction.Stop;
            int nearest = NearestAcceptableIndex(acceptable, index);
            if (nearest < 0) return CoachAction.Stop;
            return nearest > index ? CoachAction.Forward : CoachAction.Backward;
        }

        public static OneDStepResult Step(
            int index,
            CoachAction action,
            int stepCount,
            IReadOnlyList<bool> acceptable,
            IReadOnlyList<float> pathMm,
            bool contactValid,
            bool workspaceValid,
            int stepsTaken,
            int maxSteps)
        {
            int pathCount = pathMm != null ? pathMm.Count : 0;
            int nextIndex = StepIndex(index, action, stepCount, pathCount);
            bool invalidMotion = action != CoachAction.Stop && nextIndex == index;
            bool targetAcceptable = IsAcceptable(acceptable, nextIndex);
            bool stopped = action == CoachAction.Stop;
            bool success = stopped && targetAcceptable && contactValid && workspaceValid;
            bool terminated = stopped;
            bool truncated = !terminated && stepsTaken + 1 >= Mathf.Max(1, maxSteps);
            float absentDistance = pathCount > 1
                ? Mathf.Abs(pathMm[pathCount - 1] - pathMm[0]) + Mathf.Abs(pathMm[1] - pathMm[0])
                : 1f;
            float before = DistanceToAcceptableMm(acceptable, pathMm, index, absentDistance);
            float after = DistanceToAcceptableMm(acceptable, pathMm, nextIndex, absentDistance);
            float reward = -0.01f + 0.05f * (before - after);
            if (invalidMotion) reward -= 0.1f;
            if (stopped) reward += success ? 1f : -0.5f;
            if (!contactValid || !workspaceValid) reward -= 1f;
            return new OneDStepResult
            {
                previousIndex = index,
                pathIndex = nextIndex,
                action = action,
                targetAcceptable = targetAcceptable,
                contactValid = contactValid,
                workspaceValid = workspaceValid,
                invalidMotion = invalidMotion,
                patchBoundaryViolation = invalidMotion && (nextIndex == 0 || nextIndex == pathCount - 1),
                explicitStopSuccess = success,
                terminated = terminated,
                truncated = truncated,
                reward = reward,
                distanceToAcceptableMm = after
            };
        }
    }

    [Serializable]
    public struct OneDStepResult
    {
        public int previousIndex;
        public int pathIndex;
        public CoachAction action;
        public bool targetAcceptable;
        public bool contactValid;
        public bool workspaceValid;
        public bool invalidMotion;
        public bool patchBoundaryViolation;
        public bool explicitStopSuccess;
        public bool terminated;
        public bool truncated;
        public float reward;
        public float distanceToAcceptableMm;
        public bool Done => terminated || truncated;
    }
}
