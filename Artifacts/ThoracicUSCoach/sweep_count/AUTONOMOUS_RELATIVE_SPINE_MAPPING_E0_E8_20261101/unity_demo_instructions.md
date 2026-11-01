# Unity execution status

The autonomous fixed-Home demo was not built because G2 failed before E5.

The reproducible diagnostic entry points are:

- `CSSALT.ThoracicUSCoach.Editor.ThoracicRelativeSpineAuditBatch.RunE0E1`
- `CSSALT.ThoracicUSCoach.Editor.ThoracicRelativeSpineGeometryBatch.RunE2Cli`
- `CSSALT.ThoracicUSCoach.Editor.ThoracicRelativeSpineSeparabilityBatch.RunE4Cli`
- `CSSALT.ThoracicUSCoach.Editor.ThoracicRelativeSpineSeparabilityP6Batch.RunE4P6Cli`
- `CSSALT.ThoracicUSCoach.Editor.ThoracicRelativeSpineObliqueBatch.RunE4ObliqueCli`

On this workstation Unity Personal was valid in normal CLI-with-graphics mode, while `-batchmode` incorrectly requested an unavailable headless entitlement. The completed diagnostic captures used CLI-with-graphics and exited through `EditorApplication.Exit`.

Do not invoke E5/E8 from these artifacts: no valid `predictedT7RelativeMm` was produced.
