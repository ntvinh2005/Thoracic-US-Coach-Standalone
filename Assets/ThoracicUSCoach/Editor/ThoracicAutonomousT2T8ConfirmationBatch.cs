namespace CSSALT.ThoracicUSCoach.Editor
{
    public static class ThoracicAutonomousT2T8ConfirmationBatch
    {
        private const string OutputRoot =
            "Artifacts/ThoracicUSCoach/sweep_count/AUTONOMOUS_T2_T8_DISCOVERY_20261103_R01/final_confirmation_01_yaw89_5";

        public static void RunCli()
        {
            ThoracicG2R2ContinuousBatch.RunCliConfigured(
                OutputRoot, 89.5f, 45, true);
        }
    }
}
