using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace CSSALT.ThoracicUSCoach
{
    public sealed partial class ThoracicUSCoachController
    {
        private const string Active2DContract = "active2d_image_only_v1";
        private const string Active2DExperiment = "thoracic_active2d_image_only_v1_20261023";

        private sealed class Active2DActionExecution
        {
            public Active2DAction action;
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
            public string rejectionReason = string.Empty;
        }

        [Header("Learned / image-only active 2D")]
        public string active2DDatasetRunId = "active2d_phase1_balanced_v1_20261023";
        [Range(1, 240)] public int active2DPilotEpisodes = 40;
        [Range(12, 160)] public int active2DCaptureSteps = 48;
        public string active2DPhaseTwoDatasetRunId = "active2d_phase2_raster_v2_20261023";
        [Range(1, 240)] public int active2DPhaseTwoCaptureEpisodes = 40;
        [Range(50, 200)] public int active2DPhaseTwoCaptureSteps = 120;
        public bool active2DPhaseTwoPositiveEnrichment;
        [Range(0.5f, 5f)] public float active2DStepMm = 3f;
        [Range(10f, 90f)] public float active2DLongitudinalHalfExtentMm = 48f;
        [Range(10f, 90f)] public float active2DLateralHalfExtentMm = 36f;
        [Range(12, 360)] public int active2DLiveStepBudget = 300;
        [Range(0.05f, 8f)] public float active2DPolicyTimeoutSeconds = 3f;
        public bool active2DPhaseOneOnly;
        [SerializeField] private Active2DPhase active2DPhase = Active2DPhase.Idle;
        [SerializeField] private string active2DStatus = "idle";
        [SerializeField] private string active2DLastAction = "STOP";
        [SerializeField] private float active2DConfidence;
        [SerializeField] private float active2DUncertainty = 1f;

        private Coroutine active2DRoutine;
        private Coroutine active2DCaptureRoutine;
        private Vector3 active2DWorkspaceOrigin;
        private Vector3 active2DCaudalAxis;
        private Vector3 active2DLateralAxis;
        private int active2DRejectedMoves;
        private int active2DContactViolations;
        private int active2DWorkspaceViolations;
        private string active2DEpisodeId;
        private Active2DRunResult active2DResult;
        private Active2DPrivilegedAnnotations active2DLivePrivilegedAudit;
        private string active2DLastBridgeRequestId;
        private Quaternion active2DCanonicalProbeRotation;
        private Active2DActionExecution active2DLastActionExecution = new Active2DActionExecution();

        public Active2DPhase ActivePhase => active2DPhase;
        public bool Active2DRunning => active2DRoutine != null;
        public Active2DRunResult Active2DResult => active2DResult;

        private void InitializeActive2D()
        {
            if (probeRoot != null) active2DCanonicalProbeRotation = probeRoot.rotation;
            string requestPath = Path.Combine(ArtifactRoot(), "bridge", "active2d_request.json");
            if (!File.Exists(requestPath)) return;
            try
            {
                Active2DActorRequest stale = JsonUtility.FromJson<Active2DActorRequest>(File.ReadAllText(requestPath));
                if (stale != null) active2DLastBridgeRequestId = stale.requestId;
            }
            catch (IOException) { }
            catch (ArgumentException) { }
        }

        private void UpdateActive2D()
        {
            if (Input.GetKeyDown(KeyCode.F6)) StartActive2DImageOnly();
            if (Input.GetKeyDown(KeyCode.F7)) ResetActive2DImageOnly();
        }

        [ContextMenu("Capture Active 2D Pilot Dataset")]
        public void StartActive2DPilotCapture()
        {
            if (!Application.isPlaying || active2DCaptureRoutine != null || active2DRoutine != null) return;
            active2DCaptureRoutine = StartCoroutine(CaptureActive2DDataset(
                active2DDatasetRunId, active2DPilotEpisodes, active2DCaptureSteps, false));
        }

        [ContextMenu("Capture Active 2D Phase 2 Raster Dataset")]
        public void StartActive2DPhaseTwoCapture()
        {
            if (!Application.isPlaying || active2DCaptureRoutine != null || active2DRoutine != null) return;
            active2DCaptureRoutine = StartCoroutine(CaptureActive2DDataset(
                active2DPhaseTwoDatasetRunId, active2DPhaseTwoCaptureEpisodes,
                active2DPhaseTwoCaptureSteps, true));
        }

        [ContextMenu("Run Learned Image-Only Active 2D")]
        public void StartActive2DImageOnly()
        {
            if (!Application.isPlaying || active2DRoutine != null || active2DCaptureRoutine != null) return;
            // The validated scan-then-return controller needs enough budget to
            // complete its 199-action raster and physically revisit the best
            // image-supported cell. Keep UI/F6 runs aligned with evaluation.
            active2DLiveStepBudget = Mathf.Max(active2DLiveStepBudget, 300);
            active2DRoutine = StartCoroutine(RunActive2DImageOnly());
        }

        [ContextMenu("Reset Learned Image-Only Active 2D")]
        public void ResetActive2DImageOnly()
        {
            if (active2DRoutine != null) StopCoroutine(active2DRoutine);
            active2DRoutine = null;
            active2DPhase = Active2DPhase.Idle;
            active2DStatus = "reset; ready";
            active2DLastAction = "STOP";
            active2DConfidence = 0f;
            active2DUncertainty = 1f;
            active2DResult = null;
        }

        private IEnumerator CaptureActive2DDataset(
            string datasetRunId,
            int episodeCount,
            int captureSteps,
            bool phaseTwoRaster)
        {
            AutoWire();
            if (!ValidateReferences(out string error) || t6RightMarker == null || t8RightMarker == null)
            {
                active2DStatus = "pilot failed: " + (error ?? "T6/T8 annotations unavailable");
                active2DCaptureRoutine = null;
                yield break;
            }

            active2DStatus = phaseTwoRaster
                ? "capturing target-independent T7 raster for Phase 2"
                : "capturing balanced absolute-level Phase 1 dataset";
            int existing = ExistingActive2DEpisodeCount(datasetRunId);
            for (int episodeOffset = 0; episodeOffset < episodeCount; episodeOffset++)
            {
                int sequence = existing + episodeOffset;
                int seed = 20261023 + sequence * 7919;
                System.Random random = new System.Random(seed);
                string split = Active2DSplit(sequence);
                string episodeId = string.Format(CultureInfo.InvariantCulture,
                    "A2D_{0}_{1:D4}_S{2}", datasetRunId, sequence, seed);
                int startStratum = Active2DStartStratum(sequence);
                bool placed = phaseTwoRaster
                    ? TryPlaceActive2DPhaseTwoStart(
                        random, sequence, active2DPhaseTwoPositiveEnrichment, out string startDescription)
                    : TryPlaceActive2DStart(random, startStratum, out startDescription);
                if (!placed)
                {
                    active2DStatus = "pilot stopped: no valid randomized start";
                    break;
                }
                yield return null;

                string datasetRoot = Path.Combine(ArtifactRoot(), "active2d_dataset", split);
                string finalDirectory = Path.Combine(datasetRoot, episodeId);
                string partialDirectory = finalDirectory + ".partial";
                if (Directory.Exists(finalDirectory)) continue;
                if (Directory.Exists(partialDirectory)) Directory.Delete(partialDirectory, true);
                Directory.CreateDirectory(partialDirectory);

                Active2DStreamManifest stream = new Active2DStreamManifest
                {
                    datasetRunId = datasetRunId,
                    episodeId = episodeId,
                    split = split,
                    family = split + "_family_" + sequence.ToString("D4", CultureInfo.InvariantCulture),
                    seed = seed
                };
                Active2DPrivilegedAnnotations privileged = new Active2DPrivilegedAnnotations
                {
                    episodeId = episodeId,
                    workspaceOrigin = active2DWorkspaceOrigin,
                    actuatorCranialAxis = -active2DCaudalAxis,
                    actuatorLateralAxis = active2DLateralAxis,
                    longitudinalHalfExtentMm = active2DLongitudinalHalfExtentMm,
                    lateralHalfExtentMm = active2DLateralHalfExtentMm
                };

                int stored = 0;
                yield return StoreActive2DObservation(partialDirectory, stream, privileged, stored++, "RESET", true, true);
                bool initialCaudal = random.Next(0, 2) == 0;
                for (int step = 0; step < captureSteps; step++)
                {
                    Active2DAction action = phaseTwoRaster
                        ? Active2DPhaseTwoExplorationAction(step, initialCaudal)
                        : Active2DExplorationAction(step, initialCaudal);
                    bool accepted = TryActive2DAction(action);
                    if (active2DLastActionExecution.safetyFailure)
                    {
                        active2DStatus = "pilot stopped: " + active2DLastActionExecution.rejectionReason;
                        active2DCaptureRoutine = null;
                        yield break;
                    }
                    if (!accepted)
                    {
                        Active2DAction alternate = action == Active2DAction.Caudal ? Active2DAction.Cranial
                            : action == Active2DAction.Cranial ? Active2DAction.Caudal
                            : action == Active2DAction.Lateral ? Active2DAction.Medial
                            : Active2DAction.Lateral;
                        action = alternate;
                        accepted = TryActive2DAction(action);
                        if (active2DLastActionExecution.safetyFailure)
                        {
                            active2DStatus = "pilot stopped: " + active2DLastActionExecution.rejectionReason;
                            active2DCaptureRoutine = null;
                            yield break;
                        }
                    }
                    yield return null;
                    if (!accepted) continue;
                    yield return StoreActive2DObservation(partialDirectory, stream, privileged, stored++,
                        action.ToString().ToUpperInvariant(), true, false);
                }

                File.WriteAllText(Path.Combine(partialDirectory, "stream_manifest.json"), JsonUtility.ToJson(stream, true));
                File.WriteAllText(Path.Combine(partialDirectory, "privileged_annotations.json"), JsonUtility.ToJson(privileged, true));
                File.WriteAllText(Path.Combine(partialDirectory, "start_audit.txt"),
                    startDescription + Environment.NewLine
                    + "The actor never receives this file, physical pose, marker identity, score, or target direction." + Environment.NewLine);
                Directory.Move(partialDirectory, finalDirectory);
                active2DStatus = string.Format(CultureInfo.InvariantCulture,
                    "captured active2d {0}/{1}: {2}", episodeOffset + 1, episodeCount, episodeId);
                yield return null;
            }
            active2DStatus = phaseTwoRaster
                ? "active2d Phase 2 raster capture complete"
                : "active2d Phase 1 capture complete";
            active2DCaptureRoutine = null;
        }

        private IEnumerator StoreActive2DObservation(
            string directory,
            Active2DStreamManifest stream,
            Active2DPrivilegedAnnotations privileged,
            int index,
            string action,
            bool accepted,
            bool reset)
        {
            Texture2D image = CaptureCleanImage();
            if (image == null) yield break;
            byte[] bytes = image.EncodeToPNG();
            string fileName = "frame_" + index.ToString("D4", CultureInfo.InvariantCulture) + "_us.png";
            string absolute = Path.Combine(directory, fileName);
            File.WriteAllBytes(absolute, bytes);
            // The episode is written transactionally under *.partial and then
            // atomically renamed. Persist the final path, not the staging path.
            string relative = ProjectRelativePath(absolute).Replace(".partial/", "/");
            stream.frames.Add(new Active2DStreamFrame
            {
                index = index,
                image = relative,
                imageSha256 = Sha256Bytes(bytes),
                issuedAction = action,
                actionAccepted = accepted,
                episodeReset = reset
            });
            privileged.frames.Add(BuildActive2DPrivilegedFrame(index, image));
            yield return null;
        }

        private Active2DPrivilegedFrame BuildActive2DPrivilegedFrame(int index, Texture2D image)
        {
            Transform nearest = null;
            string level = "OUTSIDE_T6_T8";
            float nearestDistance = float.PositiveInfinity;
            foreach (KeyValuePair<string, Transform> item in new[]
            {
                new KeyValuePair<string, Transform>("T6", t6RightMarker),
                new KeyValuePair<string, Transform>("T7", t7RightSeed),
                new KeyValuePair<string, Transform>("T8", t8RightMarker)
            })
            {
                if (item.Value == null) continue;
                float distance = Vector3.Distance(faceCenter.position, item.Value.position);
                if (distance >= nearestDistance) continue;
                nearest = item.Value;
                level = item.Key;
                nearestDistance = distance;
            }
            float adjacentSpacing = Active2DAdjacentLevelSpacingMm();
            if (nearestDistance > Mathf.Max(8f, adjacentSpacing * 0.62f)) level = "OUTSIDE_T6_T8";
            ScoreBreakdown score = image != null
                ? ScoreCandidate(image, SampleAnatomyLabels(labelGridSize))
                : new ScoreBreakdown { total = 0f };
            float floor = discoveryAcceptableScoreFloor;
            if (floor <= 0f && TryLoadGuided2DCandidate(out CandidateRecord _, out float loadedFloor)) floor = loadedFloor;
            bool targetAcceptable = floor > 0f && score.total >= floor;
            return new Active2DPrivilegedFrame
            {
                index = index,
                probePosition = probeRoot.position,
                probeRotation = probeRoot.rotation,
                contactPosition = faceCenter.position,
                signedDepthMm = skinIntersection != null ? skinIntersection.SignedDepth : 0f,
                contactValid = IsCurrentContactValid(),
                workspaceValid = IsActive2DWorkspaceValid(faceCenter.position),
                nearestLevel = level,
                nearestLevelDistanceMm = nearestDistance,
                t7DistanceMm = t7RightSeed != null ? Vector3.Distance(faceCenter.position, t7RightSeed.position) : float.PositiveInfinity,
                t7LevelAcceptable = nearest == t7RightSeed && nearestDistance <= Active2DT7PreciseRadiusMm(),
                finalTargetAcceptable = targetAcceptable,
                finalTargetScore = score.total
            };
        }

        private float Active2DAdjacentLevelSpacingMm()
        {
            return t6RightMarker != null && t7RightSeed != null && t8RightMarker != null
                ? 0.5f * (Vector3.Distance(t6RightMarker.position, t7RightSeed.position)
                    + Vector3.Distance(t8RightMarker.position, t7RightSeed.position))
                : 20f;
        }

        private float Active2DT7PreciseRadiusMm()
        {
            return Mathf.Max(4f, Active2DAdjacentLevelSpacingMm() * 0.42f);
        }

        private bool TryPlaceActive2DStart(System.Random random, int startStratum, out string description)
        {
            description = string.Empty;
            if (t6RightMarker == null || t8RightMarker == null) return false;
            float along;
            string stratumName;
            switch (startStratum)
            {
                case 0:
                    along = Mathf.Lerp(-0.15f, 0.20f, (float)random.NextDouble());
                    stratumName = "T6-neighborhood";
                    break;
                case 1:
                    along = Mathf.Lerp(0.35f, 0.65f, (float)random.NextDouble());
                    stratumName = "T7-neighborhood";
                    break;
                case 2:
                    along = Mathf.Lerp(0.80f, 1.15f, (float)random.NextDouble());
                    stratumName = "T8-neighborhood";
                    break;
                case 3:
                    bool cranialOutside = random.Next(0, 2) == 0;
                    along = cranialOutside
                        ? Mathf.Lerp(-0.35f, -0.15f, (float)random.NextDouble())
                        : Mathf.Lerp(1.15f, 1.35f, (float)random.NextDouble());
                    stratumName = "outside-T6-T8";
                    break;
                default:
                    along = Mathf.Lerp(-0.35f, 1.35f, (float)random.NextDouble());
                    stratumName = "unstratified-live";
                    break;
            }
            float lateral = Mathf.Lerp(-18f, 18f, (float)random.NextDouble());
            return TryPlaceActive2DStartExact(along, lateral, stratumName, 0, out description);
        }

        private bool TryPlaceActive2DStartExact(
            float along,
            float lateral,
            string stratumName,
            int seed,
            out string description)
        {
            description = string.Empty;
            if (t6RightMarker == null || t8RightMarker == null) return false;
            Vector3 annotationAxis = (t8RightMarker.position - t6RightMarker.position).normalized;
            Vector3 provisionalNormal = skinCollider != null
                ? (t6RightMarker.position - skinCollider.bounds.center).normalized
                : Vector3.back;
            Vector3 annotationLateral = Vector3.Cross(provisionalNormal, annotationAxis).normalized;
            Vector3 proposal = Vector3.LerpUnclamped(t6RightMarker.position, t8RightMarker.position, along)
                + annotationLateral * lateral;
            if (!FindSurfaceAt(proposal, Vector3.zero, out RaycastHit hit)) return false;
            seedProbeRotation = active2DCanonicalProbeRotation == default
                ? probeRoot.rotation
                : active2DCanonicalProbeRotation;
            active2DCaudalAxis = Vector3.ProjectOnPlane(Vector3.down, hit.normal).normalized;
            if (active2DCaudalAxis.sqrMagnitude < 0.5f)
                active2DCaudalAxis = Vector3.ProjectOnPlane(probeRoot.up, hit.normal).normalized;
            active2DLateralAxis = Vector3.Cross(hit.normal.normalized, active2DCaudalAxis).normalized;
            if (Vector3.Dot(active2DLateralAxis, probeRoot.right) < 0f) active2DLateralAxis = -active2DLateralAxis;
            caudalTangent = active2DCaudalAxis;
            AlignProbeToSurface(hit, 0f, 0f);
            if (!EnforceRigidContact()) return false;
            active2DWorkspaceOrigin = faceCenter.position;
            description = seed == 0
                ? string.Format(CultureInfo.InvariantCulture,
                    "randomized physical start: stratum={0}, interpolation={1:F3}, lateralPerturbationMm={2:F3}; actor was not told these values",
                    stratumName, along, lateral)
                : string.Format(CultureInfo.InvariantCulture,
                    "deterministic physical start: stratum={0}, seed={1}, interpolation={2:R}, lateralPerturbationMm={3:R}; actor was not told these values",
                    stratumName, seed, along, lateral);
            return true;
        }

        private bool TryPlaceActive2DPhaseTwoStart(
            System.Random random,
            int sequence,
            bool positiveEnrichment,
            out string description)
        {
            description = string.Empty;
            if (t7RightSeed == null || t6RightMarker == null || t8RightMarker == null) return false;
            int startStratum = positiveEnrichment ? 3 : Active2DStartStratum(sequence);
            int splitSlot = ((sequence % 5) + 5) % 5;
            int block = Mathf.FloorToInt(sequence / 5f);
            float[] yawOffsets = { -6f, -3f, 0f, 3f, 6f };
            int yawIndex = ((block + splitSlot) % yawOffsets.Length + yawOffsets.Length) % yawOffsets.Length;
            float yaw = positiveEnrichment
                ? (((block + splitSlot) % 2 == 0) ? -6f : 6f)
                : yawOffsets[yawIndex];
            float[] longitudinalOffsets = { -9f, -3f, 3f, 9f };
            float longitudinal = longitudinalOffsets[Mathf.Clamp(startStratum, 0, 3)]
                + Mathf.Lerp(-1.5f, 1.5f, (float)random.NextDouble());
            Vector3 annotationAxis = (t8RightMarker.position - t6RightMarker.position).normalized;
            Vector3 provisionalNormal = skinCollider != null
                ? (t7RightSeed.position - skinCollider.bounds.center).normalized
                : Vector3.back;
            Vector3 annotationLateral = Vector3.Cross(provisionalNormal, annotationAxis).normalized;
            float lateral = Mathf.Lerp(-12f, 12f, (float)random.NextDouble());
            Vector3 proposal = t7RightSeed.position + annotationAxis * longitudinal + annotationLateral * lateral;
            if (!FindSurfaceAt(proposal, Vector3.zero, out RaycastHit hit)) return false;
            seedProbeRotation = active2DCanonicalProbeRotation == default
                ? probeRoot.rotation
                : active2DCanonicalProbeRotation;
            active2DCaudalAxis = Vector3.ProjectOnPlane(Vector3.down, hit.normal).normalized;
            if (active2DCaudalAxis.sqrMagnitude < 0.5f)
                active2DCaudalAxis = Vector3.ProjectOnPlane(annotationAxis, hit.normal).normalized;
            active2DLateralAxis = Vector3.Cross(hit.normal.normalized, active2DCaudalAxis).normalized;
            if (Vector3.Dot(active2DLateralAxis, probeRoot.right) < 0f) active2DLateralAxis = -active2DLateralAxis;
            caudalTangent = active2DCaudalAxis;
            AlignProbeToSurface(hit, yaw, 0f);
            if (!EnforceRigidContact()) return false;
            active2DWorkspaceOrigin = faceCenter.position;
            description = string.Format(CultureInfo.InvariantCulture,
                "T7-level randomized raster start: coverageMode={0}, longitudinalStratum={1}, longitudinalOffsetMm={2:F3}, lateralPerturbationMm={3:F3}, yawStratumDeg={4:F1}; fixed raster never receives target coordinates, score, or direction",
                positiveEnrichment ? "positive-enrichment-fixed-strata" : "general-grid",
                startStratum, longitudinal, lateral, yaw);
            return true;
        }

        private static string Active2DSplit(int sequence)
        {
            // Each five-episode block contains three train, one dev, and one test
            // family. Start strata rotate only after a complete block, so every
            // split sees every absolute-level stratum without sharing families.
            int slot = ((sequence % 5) + 5) % 5;
            return slot < 3 ? "train" : slot == 3 ? "dev" : "test";
        }

        private static int Active2DStartStratum(int sequence)
        {
            int block = Mathf.FloorToInt(sequence / 5f);
            return ((block % 4) + 4) % 4;
        }

        private static Active2DAction Active2DExplorationAction(int step, bool initialCaudal)
        {
            const int rowLength = 8;
            int within = step % (rowLength + 1);
            int row = step / (rowLength + 1);
            if (within == rowLength) return row % 4 < 2 ? Active2DAction.Lateral : Active2DAction.Medial;
            bool caudal = (row % 2 == 0) == initialCaudal;
            return caudal ? Active2DAction.Caudal : Active2DAction.Cranial;
        }

        private static Active2DAction Active2DPhaseTwoExplorationAction(int step, bool initialCaudal)
        {
            // A fixed, target-independent full-width raster. Each 51-step row
            // moves from center to +36 mm, across to -36 mm, and back to center,
            // then obtains two physically perturbed rotation views and advances
            // one longitudinal step. No label or score affects this schedule.
            const int rowLength = 51;
            int within = step % rowLength;
            int row = step / rowLength;
            if (within < 12) return Active2DAction.Lateral;
            if (within < 36) return Active2DAction.Medial;
            if (within < 48) return Active2DAction.Lateral;
            if (within == 48) return Active2DAction.RotateClockwise;
            if (within == 49) return Active2DAction.RotateCounterClockwise;
            bool caudal = (row % 4 < 2) == initialCaudal;
            return caudal ? Active2DAction.Caudal : Active2DAction.Cranial;
        }

        private bool TryActive2DAction(Active2DAction action)
        {
            BeginActive2DActionExecution(action);
            switch (action)
            {
                case Active2DAction.Cranial: return TryActive2DMove(-active2DCaudalAxis);
                case Active2DAction.Caudal: return TryActive2DMove(active2DCaudalAxis);
                case Active2DAction.Medial: return TryActive2DMove(-active2DLateralAxis);
                case Active2DAction.Lateral: return TryActive2DMove(active2DLateralAxis);
                case Active2DAction.RotateClockwise: return TryActive2DRotate(3f);
                case Active2DAction.RotateCounterClockwise: return TryActive2DRotate(-3f);
                default:
                    active2DLastActionExecution.accepted = true;
                    return true;
            }
        }

        private void BeginActive2DActionExecution(Active2DAction action)
        {
            active2DLastActionExecution = new Active2DActionExecution { action = action };
            switch (action)
            {
                case Active2DAction.Cranial:
                case Active2DAction.Caudal:
                case Active2DAction.Medial:
                case Active2DAction.Lateral:
                    active2DLastActionExecution.requestedDisplacementMm = active2DStepMm;
                    break;
                case Active2DAction.RotateClockwise:
                    active2DLastActionExecution.requestedRotationDeg = 3f;
                    break;
                case Active2DAction.RotateCounterClockwise:
                    active2DLastActionExecution.requestedRotationDeg = -3f;
                    break;
            }
        }

        private bool RefreshActive2DSafetyState(out string reason)
        {
            bool contactValid = false;
            if (skinIntersection != null)
            {
                skinIntersection.Measure();
                contactValid = Thoracic2DNavigation.IsContactValid(
                    skinIntersection.AnyHit, skinIntersection.SignedDepth, maxPenetrationMm);
            }
            bool workspaceValid = faceCenter != null && IsActive2DWorkspaceValid(faceCenter.position);
            active2DLastActionExecution.contactValidAfterExecution = contactValid;
            active2DLastActionExecution.workspaceValidAfterExecution = workspaceValid;
            if (!contactValid)
            {
                reason = skinIntersection == null || !skinIntersection.AnyHit
                    ? "skin contact measurement has no hit"
                    : "skin contact depth exceeds configured tolerance";
                return false;
            }
            if (!workspaceValid)
            {
                reason = "contact position is outside the active workspace";
                return false;
            }
            reason = string.Empty;
            return true;
        }

        private bool RejectActive2DActionWithoutPoseChange(string reason)
        {
            active2DRejectedMoves++;
            active2DLastActionExecution.rejectionReason = reason;
            if (!RefreshActive2DSafetyState(out string safetyReason))
            {
                active2DLastActionExecution.safetyFailure = true;
                active2DLastActionExecution.rejectionReason += "; unsafe retained pose: " + safetyReason;
                if (!active2DLastActionExecution.contactValidAfterExecution) active2DContactViolations++;
                if (!active2DLastActionExecution.workspaceValidAfterExecution) active2DWorkspaceViolations++;
            }
            return false;
        }

        private bool RejectActive2DActionAndRollback(
            Vector3 oldPosition,
            Quaternion oldRotation,
            Vector3 oldContact,
            string reason)
        {
            active2DRejectedMoves++;
            active2DLastActionExecution.rollbackAttempted = true;
            active2DLastActionExecution.rejectionReason = reason;
            probeRoot.SetPositionAndRotation(oldPosition, oldRotation);
            bool contactRestored = EnforceRigidContact();
            bool safeRestored = RefreshActive2DSafetyState(out string restorationReason);
            active2DLastActionExecution.achievedDisplacementMm =
                faceCenter != null ? Vector3.Distance(oldContact, faceCenter.position) : 0f;
            active2DLastActionExecution.achievedRotationDeg =
                Quaternion.Angle(oldRotation, probeRoot.rotation);
            active2DLastActionExecution.rollbackContactRestored =
                contactRestored && active2DLastActionExecution.contactValidAfterExecution;
            if (!active2DLastActionExecution.rollbackContactRestored || !safeRestored)
            {
                active2DLastActionExecution.safetyFailure = true;
                if (string.IsNullOrEmpty(restorationReason) && !contactRestored)
                    restorationReason = "contact enforcement returned false";
                active2DLastActionExecution.rejectionReason += "; rollback safety restoration failed: "
                    + restorationReason;
                if (!active2DLastActionExecution.contactValidAfterExecution) active2DContactViolations++;
                if (!active2DLastActionExecution.workspaceValidAfterExecution) active2DWorkspaceViolations++;
            }
            return false;
        }

        private bool TryActive2DMove(Vector3 referenceDirection)
        {
            Vector3 oldPosition = probeRoot.position;
            Quaternion oldRotation = probeRoot.rotation;
            Vector3 oldContact = faceCenter.position;
            Vector3 normal = CurrentSurfaceNormal();
            Vector3 direction = Vector3.ProjectOnPlane(referenceDirection, normal).normalized;
            if (direction.sqrMagnitude < 0.5f)
                return RejectActive2DActionWithoutPoseChange("movement tangent is degenerate");
            if (!FindSurfaceAt(oldContact + direction * active2DStepMm, normal, out RaycastHit hit))
                return RejectActive2DActionWithoutPoseChange("surface raycast failed");
            caudalTangent = Vector3.ProjectOnPlane(active2DCaudalAxis, hit.normal).normalized;
            AlignProbeToSurface(hit, 0f, 0f);
            if (!EnforceRigidContact())
            {
                active2DContactViolations++;
                active2DLastActionExecution.attemptedDisplacementMm =
                    Vector3.Distance(oldContact, faceCenter.position);
                return RejectActive2DActionAndRollback(
                    oldPosition, oldRotation, oldContact, "contact enforcement failed");
            }
            float moved = Vector3.Distance(oldContact, faceCenter.position);
            active2DLastActionExecution.attemptedDisplacementMm = moved;
            if (!RefreshActive2DSafetyState(out string safetyReason))
            {
                if (!active2DLastActionExecution.contactValidAfterExecution) active2DContactViolations++;
                if (!active2DLastActionExecution.workspaceValidAfterExecution) active2DWorkspaceViolations++;
                return RejectActive2DActionAndRollback(
                    oldPosition, oldRotation, oldContact, "candidate pose failed safety validation: " + safetyReason);
            }
            if (Thoracic2DNavigation.IsTeleportLike(moved, active2DStepMm, 2.5f))
                return RejectActive2DActionAndRollback(
                    oldPosition, oldRotation, oldContact, "translation exceeded discontinuity allowance");
            if (!Thoracic2DNavigation.HasMeaningfulProgress(active2DStepMm, moved))
                return RejectActive2DActionAndRollback(
                    oldPosition, oldRotation, oldContact, "translation made less than minimum meaningful progress");
            active2DLastActionExecution.accepted = true;
            active2DLastActionExecution.achievedDisplacementMm = moved;
            return true;
        }

        private bool TryActive2DRotate(float angleDeg)
        {
            Vector3 oldPosition = probeRoot.position;
            Quaternion oldRotation = probeRoot.rotation;
            Vector3 contact = faceCenter.position;
            Vector3 normal = CurrentSurfaceNormal();
            probeRoot.RotateAround(contact, normal, angleDeg);
            bool contactEnforced = EnforceRigidContact();
            float contactShift = Vector3.Distance(contact, faceCenter.position);
            active2DLastActionExecution.attemptedDisplacementMm = contactShift;
            active2DLastActionExecution.achievedRotationDeg = Quaternion.Angle(oldRotation, probeRoot.rotation);
            bool safe = RefreshActive2DSafetyState(out string safetyReason);
            if (!contactEnforced || !safe || contactShift > 2.5f)
            {
                if (!contactEnforced || !active2DLastActionExecution.contactValidAfterExecution)
                    active2DContactViolations++;
                if (!active2DLastActionExecution.workspaceValidAfterExecution)
                    active2DWorkspaceViolations++;
                string reason = !contactEnforced
                    ? "rotation contact enforcement failed"
                    : !safe
                        ? "rotation candidate failed safety validation: " + safetyReason
                        : "rotation shifted contact beyond allowance";
                return RejectActive2DActionAndRollback(oldPosition, oldRotation, contact, reason);
            }
            active2DLastActionExecution.accepted = true;
            active2DLastActionExecution.achievedDisplacementMm = contactShift;
            return true;
        }

        private bool IsActive2DWorkspaceValid(Vector3 contact)
        {
            float longitudinal = Vector3.Dot(contact - active2DWorkspaceOrigin, active2DCaudalAxis);
            float lateral = Vector3.Dot(contact - active2DWorkspaceOrigin, active2DLateralAxis);
            return Mathf.Abs(longitudinal) <= active2DLongitudinalHalfExtentMm
                && Mathf.Abs(lateral) <= active2DLateralHalfExtentMm;
        }

        private bool ValidateActive2DTerminalSafety(Active2DAction action, out string reason)
        {
            BeginActive2DActionExecution(action);
            bool valid = RefreshActive2DSafetyState(out reason);
            active2DLastActionExecution.accepted = valid;
            if (valid) return true;
            active2DLastActionExecution.safetyFailure = true;
            active2DLastActionExecution.rejectionReason = action + " rejected: " + reason;
            if (!active2DLastActionExecution.contactValidAfterExecution) active2DContactViolations++;
            if (!active2DLastActionExecution.workspaceValidAfterExecution) active2DWorkspaceViolations++;
            return false;
        }

        private int ExistingActive2DEpisodeCount(string datasetRunId)
        {
            string root = Path.Combine(ArtifactRoot(), "active2d_dataset");
            if (!Directory.Exists(root)) return 0;
            int count = 0;
            foreach (string manifest in Directory.GetFiles(root, "stream_manifest.json", SearchOption.AllDirectories))
            {
                try
                {
                    Active2DStreamManifest parsed = JsonUtility.FromJson<Active2DStreamManifest>(File.ReadAllText(manifest));
                    if (parsed != null && parsed.datasetRunId == datasetRunId && parsed.captureStatus == "complete") count++;
                }
                catch (IOException) { }
                catch (ArgumentException) { }
            }
            return count;
        }

        private string ProjectRelativePath(string absolute)
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            Uri rootUri = new Uri(projectRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(new Uri(absolute)).ToString()).Replace('\\', '/');
        }

        private IEnumerator RunActive2DImageOnly()
        {
            yield return RunActive2DImageOnlyCore(null);
        }

        private IEnumerator RunActive2DImageOnlyCore(Gate2Phase1Case evaluationCase)
        {
            AutoWire();
            ResetActive2DRunCounters();
            active2DPhase = Active2DPhase.Initializing;
            active2DEpisodeId = evaluationCase == null
                ? "LIVE_A2D_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture)
                : Gate2EpisodeId(evaluationCase);
            active2DResult = new Active2DRunResult
            {
                generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                episodeId = active2DEpisodeId
            };
            bool placed;
            string startDescription;
            if (evaluationCase == null)
            {
                System.Random random = new System.Random(Environment.TickCount);
                placed = TryPlaceActive2DStart(random, -1, out startDescription);
            }
            else
            {
                placed = TryPlaceActive2DStartExact(
                    evaluationCase.plannedInterpolation,
                    evaluationCase.plannedLateralMm,
                    "gate2-" + evaluationCase.expectedStartLevel,
                    evaluationCase.seed,
                    out startDescription);
            }
            active2DResult.startDescription = startDescription;
            if (!placed)
            {
                FailActive2D("could not establish randomized valid start");
                yield break;
            }
            active2DLivePrivilegedAudit = new Active2DPrivilegedAnnotations
            {
                episodeId = active2DEpisodeId,
                workspaceOrigin = active2DWorkspaceOrigin,
                actuatorCranialAxis = -active2DCaudalAxis,
                actuatorLateralAxis = active2DLateralAxis,
                t7TargetPosition = t7RightSeed.position,
                t7PreciseRadiusMm = Active2DT7PreciseRadiusMm(),
                stepMm = active2DStepMm,
                longitudinalHalfExtentMm = active2DLongitudinalHalfExtentMm,
                lateralHalfExtentMm = active2DLateralHalfExtentMm,
                warning = "PRIVILEGED LIVE AUDIT ONLY. Written after sensing for diagnosis; never passed to actor."
            };
            if (evaluationCase != null && !RecordAndValidateGate2Start(evaluationCase))
            {
                FailActive2D("Gate2 start failed independent label/contact/workspace/reachability validation");
                yield break;
            }
            active2DPhase = Active2DPhase.LongitudinalLocalization;
            active2DStatus = "LEARNED / IMAGE-ONLY: longitudinal localization";

            for (int step = 0; step < active2DLiveStepBudget; step++)
            {
                Texture2D image = CaptureCleanImage();
                if (image == null)
                {
                    FailActive2D("ultrasound capture failed");
                    yield break;
                }
                Active2DPrivilegedFrame privilegedAudit = BuildActive2DPrivilegedFrame(step, image);
                active2DLivePrivilegedAudit.frames.Add(privilegedAudit);
                string imagePath = SaveActive2DBridgeImage(image, step);
                string requestId = active2DEpisodeId + "_" + step.ToString("D4", CultureInfo.InvariantCulture);
                Active2DActorRequest request = new Active2DActorRequest
                {
                    requestId = requestId,
                    episodeId = active2DEpisodeId,
                    reset = step == 0,
                    imagePath = imagePath
                };
                try
                {
                    WriteActive2DRequest(request);
                }
                catch (IOException exception)
                {
                    FailActive2D("bridge request write failed after retries: " + exception.Message);
                    yield break;
                }
                Active2DActorResponse response = null;
                float deadline = Time.realtimeSinceStartup + active2DPolicyTimeoutSeconds;
                while (Time.realtimeSinceStartup < deadline)
                {
                    if (TryReadActive2DResponse(requestId, out response)) break;
                    yield return null;
                }
                if (response == null || !string.IsNullOrEmpty(response.error))
                {
                    FailActive2D(response != null ? response.error : "image-only policy timeout");
                    yield break;
                }

                active2DConfidence = response.confidence;
                active2DUncertainty = response.uncertainty;
                active2DLastAction = response.action;
                if (!Enum.TryParse(response.action, true, out Active2DAction action))
                {
                    FailActive2D("policy returned unsupported action: " + response.action);
                    yield break;
                }
                bool accepted = true;
                if (action == Active2DAction.PhaseHandoff)
                {
                    if (!ValidateActive2DTerminalSafety(action, out string safetyReason))
                    {
                        active2DResult.finalContactValid = active2DLastActionExecution.contactValidAfterExecution;
                        active2DResult.finalWorkspaceValid = active2DLastActionExecution.workspaceValidAfterExecution;
                        RecordActive2DTrace(step, action, false, imagePath);
                        FailActive2D("Phase handoff safety validation failed: " + safetyReason);
                        yield break;
                    }
                    active2DResult.phaseOneHandoff = true;
                    active2DResult.correctLevelAtHandoff = privilegedAudit.t7LevelAcceptable;
                    // Validate against the Phase 1 workspace before establishing
                    // the Phase 2 origin. Reversing these statements would make
                    // every handoff position appear to be at the new origin.
                    active2DWorkspaceOrigin = faceCenter.position;
                    if (active2DPhaseOneOnly)
                    {
                        active2DResult.finalContactValid = active2DLastActionExecution.contactValidAfterExecution;
                        active2DResult.finalWorkspaceValid = active2DLastActionExecution.workspaceValidAfterExecution;
                        active2DResult.overallPass = Thoracic2DNavigation.IsTerminalAcceptanceValid(
                            active2DResult.correctLevelAtHandoff,
                            active2DResult.finalContactValid,
                            active2DResult.finalWorkspaceValid);
                        active2DPhase = active2DResult.overallPass
                            ? Active2DPhase.FinalSuccess
                            : Active2DPhase.FinalFailure;
                        active2DResult.finalPhase = active2DPhase.ToString();
                        active2DResult.failureReason = active2DResult.overallPass
                            ? string.Empty
                            : "Phase 1 handoff failed privileged evaluator";
                        RecordActive2DTrace(step, action, true, imagePath);
                        SaveActive2DRunResult();
                        active2DStatus = active2DResult.overallPass
                            ? "IMAGE-ONLY PHASE 1 HANDOFF: SUCCESS"
                            : "IMAGE-ONLY PHASE 1 HANDOFF: FAILED";
                        if (!gate2EvaluationRunning) active2DRoutine = null;
                        yield break;
                    }
                    active2DPhase = Active2DPhase.LateralAcquisition;
                    active2DStatus = "LEARNED / IMAGE-ONLY: lateral acquisition";
                }
                else if (action == Active2DAction.Stop)
                {
                    if (!ValidateActive2DTerminalSafety(action, out string safetyReason))
                    {
                        active2DResult.finalContactValid = active2DLastActionExecution.contactValidAfterExecution;
                        active2DResult.finalWorkspaceValid = active2DLastActionExecution.workspaceValidAfterExecution;
                        RecordActive2DTrace(step, action, false, imagePath);
                        FailActive2D("STOP safety validation failed: " + safetyReason);
                        yield break;
                    }
                    active2DResult.explicitStop = true;
                    active2DResult.finalTargetPass = privilegedAudit.finalTargetAcceptable;
                    active2DResult.finalContactValid = active2DLastActionExecution.contactValidAfterExecution;
                    active2DResult.finalWorkspaceValid = active2DLastActionExecution.workspaceValidAfterExecution;
                    bool domainSuccess = active2DResult.phaseOneHandoff
                        && active2DResult.correctLevelAtHandoff && privilegedAudit.finalTargetAcceptable;
                    active2DResult.overallPass = Thoracic2DNavigation.IsTerminalAcceptanceValid(
                        domainSuccess,
                        active2DResult.finalContactValid,
                        active2DResult.finalWorkspaceValid);
                    active2DPhase = active2DResult.overallPass ? Active2DPhase.FinalSuccess : Active2DPhase.FinalFailure;
                    active2DResult.finalPhase = active2DPhase.ToString();
                    active2DResult.failureReason = active2DResult.overallPass ? string.Empty : "explicit STOP failed privileged evaluator";
                    RecordActive2DTrace(step, action, true, imagePath);
                    SaveActive2DRunResult();
                    active2DStatus = active2DResult.overallPass ? "IMAGE-ONLY FINAL STOP: SUCCESS" : "IMAGE-ONLY FINAL STOP: FAILED";
                    if (!gate2EvaluationRunning) active2DRoutine = null;
                    yield break;
                }
                else
                {
                    accepted = TryActive2DAction(action);
                    yield return null;
                }
                RecordActive2DTrace(step, action, accepted, imagePath);
                if (active2DLastActionExecution.safetyFailure)
                {
                    FailActive2D(active2DLastActionExecution.rejectionReason);
                    yield break;
                }
            }
            FailActive2D("step budget exhausted without explicit STOP");
        }

        private void ResetActive2DRunCounters()
        {
            active2DRejectedMoves = 0;
            active2DContactViolations = 0;
            active2DWorkspaceViolations = 0;
            active2DConfidence = 0f;
            active2DUncertainty = 1f;
            active2DResult = null;
            active2DLivePrivilegedAudit = null;
            active2DLastActionExecution = new Active2DActionExecution();
        }

        private string SaveActive2DBridgeImage(Texture2D image, int step)
        {
            string bridge = Path.Combine(ArtifactRoot(), "bridge", "active2d");
            Directory.CreateDirectory(bridge);
            string path = Path.Combine(bridge, "observation_" + active2DEpisodeId + "_" + step.ToString("D4", CultureInfo.InvariantCulture) + ".png");
            File.WriteAllBytes(path, image.EncodeToPNG());
            return path;
        }

        private void WriteActive2DRequest(Active2DActorRequest request)
        {
            string bridge = Path.Combine(ArtifactRoot(), "bridge");
            Directory.CreateDirectory(bridge);
            string destination = Path.Combine(bridge, "active2d_request.json");
            string temporary = destination + "." + request.requestId + ".tmp";
            File.WriteAllText(temporary, JsonUtility.ToJson(request, true));
            IOException lastError = null;
            for (int attempt = 0; attempt < 12; attempt++)
            {
                try
                {
                    if (File.Exists(destination)) File.Replace(temporary, destination, null, true);
                    else File.Move(temporary, destination);
                    lastError = null;
                    break;
                }
                catch (IOException exception)
                {
                    lastError = exception;
                    System.Threading.Thread.Sleep(5);
                }
            }
            if (lastError != null)
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                throw lastError;
            }
            active2DLastBridgeRequestId = request.requestId;
        }

        private bool TryReadActive2DResponse(string requestId, out Active2DActorResponse response)
        {
            response = null;
            string path = Path.Combine(ArtifactRoot(), "bridge", "active2d_response.json");
            if (!File.Exists(path)) return false;
            try
            {
                Active2DActorResponse parsed = JsonUtility.FromJson<Active2DActorResponse>(File.ReadAllText(path));
                if (parsed == null || parsed.requestId != requestId || parsed.contract != Active2DContract) return false;
                response = parsed;
                return true;
            }
            catch (IOException) { return false; }
            catch (ArgumentException) { return false; }
        }

        private void RecordActive2DTrace(int step, Active2DAction action, bool accepted, string imagePath)
        {
            if (active2DResult == null) return;
            active2DResult.trace.Add(new Active2DRunTrace
            {
                step = step,
                action = action.ToString().ToUpperInvariant(),
                phase = active2DPhase.ToString(),
                accepted = accepted,
                requestedDisplacementMm = active2DLastActionExecution.requestedDisplacementMm,
                attemptedDisplacementMm = active2DLastActionExecution.attemptedDisplacementMm,
                achievedDisplacementMm = active2DLastActionExecution.achievedDisplacementMm,
                requestedRotationDeg = active2DLastActionExecution.requestedRotationDeg,
                achievedRotationDeg = active2DLastActionExecution.achievedRotationDeg,
                contactValidAfterExecution = active2DLastActionExecution.contactValidAfterExecution,
                workspaceValidAfterExecution = active2DLastActionExecution.workspaceValidAfterExecution,
                rollbackAttempted = active2DLastActionExecution.rollbackAttempted,
                rollbackContactRestored = active2DLastActionExecution.rollbackContactRestored,
                safetyFailure = active2DLastActionExecution.safetyFailure,
                rejectionReason = active2DLastActionExecution.rejectionReason,
                confidence = active2DConfidence,
                uncertainty = active2DUncertainty,
                image = ProjectRelativePath(imagePath)
            });
            active2DResult.steps = active2DResult.trace.Count;
            active2DResult.rejectedMovements = active2DRejectedMoves;
            active2DResult.contactViolations = active2DContactViolations;
            active2DResult.workspaceViolations = active2DWorkspaceViolations;
        }

        private void SaveActive2DRunResult()
        {
            if (active2DResult == null) return;
            string directory = Path.Combine(ArtifactRoot(), "active2d", "runs");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, active2DEpisodeId + ".json");
            File.WriteAllText(path, JsonUtility.ToJson(active2DResult, true));
            if (active2DLivePrivilegedAudit != null)
            {
                string auditDirectory = Path.Combine(ArtifactRoot(), "active2d", "runs_privileged");
                Directory.CreateDirectory(auditDirectory);
                string auditPath = Path.Combine(auditDirectory, active2DEpisodeId + ".json");
                File.WriteAllText(auditPath, JsonUtility.ToJson(active2DLivePrivilegedAudit, true));
            }
        }

        private void FailActive2D(string reason)
        {
            active2DPhase = Active2DPhase.FinalFailure;
            active2DStatus = "IMAGE-ONLY FAILED: " + reason;
            if (active2DResult != null)
            {
                active2DResult.finalPhase = active2DPhase.ToString();
                active2DResult.failureReason = reason;
                active2DResult.rejectedMovements = active2DRejectedMoves;
                active2DResult.contactViolations = active2DContactViolations;
                active2DResult.workspaceViolations = active2DWorkspaceViolations;
                if (skinIntersection != null)
                {
                    skinIntersection.Measure();
                    active2DResult.finalContactValid = Thoracic2DNavigation.IsContactValid(
                        skinIntersection.AnyHit, skinIntersection.SignedDepth, maxPenetrationMm);
                }
                else active2DResult.finalContactValid = false;
                active2DResult.finalWorkspaceValid = faceCenter != null
                    && IsActive2DWorkspaceValid(faceCenter.position);
                SaveActive2DRunResult();
            }
            if (!gate2EvaluationRunning) active2DRoutine = null;
        }

        private void DrawActive2DGUI()
        {
            const float width = 380f;
            float guidedPanelX = Mathf.Max(510f, Screen.width - 520f);
            // Reserve the rightmost 500 px for the app sidebar / Guided panel.
            // This keeps the learned panel clear even when Guided is hidden.
            float x = Mathf.Max(20f, guidedPanelX - width - 10f);
            float height = active2DResult == null ? 205f : 285f;
            GUILayout.BeginArea(new Rect(x, 20f, width, height), GUI.skin.box);
            GUILayout.Label("LEARNED / IMAGE-ONLY ACTIVE 2D");
            GUILayout.Label("Contract: " + Active2DContract);
            GUILayout.Label("Phase: " + active2DPhase + "   Action: " + active2DLastAction);
            GUILayout.Label(string.Format(CultureInfo.InvariantCulture,
                "Confidence: {0:F2}   Uncertainty: {1:F2}", active2DConfidence, active2DUncertainty));
            GUILayout.Label(active2DStatus);
            GUI.enabled = active2DRoutine == null && active2DCaptureRoutine == null;
            if (GUILayout.Button("Start Image-Only Active 2D (F6)")) StartActive2DImageOnly();
            if (GUILayout.Button("Capture Active 2D Pilot")) StartActive2DPilotCapture();
            GUI.enabled = true;
            if (GUILayout.Button("Reset Image-Only Mode (F7)")) ResetActive2DImageOnly();
            if (active2DResult != null)
            {
                GUILayout.Space(4f);
                GUILayout.Label("INDEPENDENT EVALUATOR RESULT");
                GUILayout.Label("Correct T7 handoff: " + PassFail(active2DResult.correctLevelAtHandoff)
                    + " | Explicit STOP: " + PassFail(active2DResult.explicitStop));
                GUILayout.Label("Final target: " + PassFail(active2DResult.finalTargetPass)
                    + " | Overall: " + PassFail(active2DResult.overallPass));
                if (!string.IsNullOrEmpty(active2DResult.failureReason))
                    GUILayout.Label("Reason: " + active2DResult.failureReason);
            }
            GUILayout.EndArea();
        }
    }
}
