using UnityEditor;

namespace CSSALT.ThoracicUSCoach.Editor
{
    public static class ThoracicAutonomousT2T8DevelopmentBatch
    {
        private const string OutputRoot =
            "Artifacts/ThoracicUSCoach/sweep_count/AUTONOMOUS_T2_T8_DISCOVERY_20261103_R01/development_session_04_yaw90_5";

        public static void RunCli()
        {
            ThoracicG2R2ContinuousBatch.RunCliConfigured(OutputRoot, 90.5f, 45, true);
        }
    }
}
