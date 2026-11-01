namespace CSSALT.ThoracicUSCoach.Editor
{
    public static class ThoracicG2R3ConfirmationBatch
    {
        private const string OutputRoot =
            "Artifacts/ThoracicUSCoach/sweep_count/G2_R3_TOPOLOGY_DISCRIMINATION_20261101_R02/confirmation_01/acquisition";
        private const float SurfaceYawDeg = 91f;

        public static void RunCli()
        {
            ThoracicG2R2ContinuousBatch.RunCliConfigured(OutputRoot, SurfaceYawDeg);
        }
    }
}
