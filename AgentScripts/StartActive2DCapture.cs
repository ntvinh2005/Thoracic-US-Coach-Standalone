using System;
using CSSALT.ThoracicUSCoach;
using UnityEngine;

public static class StartActive2DCapture
{
    public static string Main(string datasetRunId, int episodes, int steps)
    {
        ThoracicUSCoachController controller =
            UnityEngine.Object.FindFirstObjectByType<ThoracicUSCoachController>();
        if (controller == null) throw new InvalidOperationException("ThoracicUSCoachController not found");
        controller.active2DDatasetRunId = datasetRunId;
        controller.active2DPilotEpisodes = episodes;
        controller.active2DCaptureSteps = steps;
        controller.StartActive2DPilotCapture();
        return datasetRunId + ": capture requested";
    }

    public static string PhaseTwo(string datasetRunId, int episodes, int steps, bool positiveEnrichment = false)
    {
        ThoracicUSCoachController controller =
            UnityEngine.Object.FindFirstObjectByType<ThoracicUSCoachController>();
        if (controller == null) throw new InvalidOperationException("ThoracicUSCoachController not found");
        controller.active2DPhaseTwoDatasetRunId = datasetRunId;
        controller.active2DPhaseTwoCaptureEpisodes = episodes;
        controller.active2DPhaseTwoCaptureSteps = steps;
        controller.active2DPhaseTwoPositiveEnrichment = positiveEnrichment;
        controller.StartActive2DPhaseTwoCapture();
        return datasetRunId + (positiveEnrichment
            ? ": Phase 2 positive-enrichment raster requested"
            : ": Phase 2 general raster requested");
    }

    public static string RunLive(bool phaseOneOnly, int stepBudget)
    {
        ThoracicUSCoachController controller =
            UnityEngine.Object.FindFirstObjectByType<ThoracicUSCoachController>();
        if (controller == null) throw new InvalidOperationException("ThoracicUSCoachController not found");
        controller.active2DPhaseOneOnly = phaseOneOnly;
        controller.active2DLiveStepBudget = stepBudget;
        controller.StartActive2DImageOnly();
        return phaseOneOnly ? "Phase 1 live run requested" : "Full Active 2D live run requested";
    }

    public static string Result()
    {
        ThoracicUSCoachController controller =
            UnityEngine.Object.FindFirstObjectByType<ThoracicUSCoachController>();
        if (controller == null) throw new InvalidOperationException("ThoracicUSCoachController not found");
        if (controller.Active2DRunning) return "RUNNING";
        return controller.Active2DResult == null
            ? "NO_RESULT"
            : JsonUtility.ToJson(controller.Active2DResult, true);
    }

    public static string Geometry()
    {
        ThoracicUSCoachController controller =
            UnityEngine.Object.FindFirstObjectByType<ThoracicUSCoachController>();
        if (controller == null) throw new InvalidOperationException("ThoracicUSCoachController not found");
        return string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "T6-T7={0:F3}mm T7-T8={1:F3}mm T6-T8={2:F3}mm T7=({3:F3},{4:F3},{5:F3})",
            Vector3.Distance(controller.t6RightMarker.position, controller.t7RightSeed.position),
            Vector3.Distance(controller.t7RightSeed.position, controller.t8RightMarker.position),
            Vector3.Distance(controller.t6RightMarker.position, controller.t8RightMarker.position),
            controller.t7RightSeed.position.x,
            controller.t7RightSeed.position.y,
            controller.t7RightSeed.position.z);
    }
}
