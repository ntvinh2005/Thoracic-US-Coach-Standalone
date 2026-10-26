using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CSSALT.ThoracicUSCoach
{
    public sealed partial class ThoracicUSCoachController
    {
        private R6FinalSprintManifest r6Manifest;
        private R6FinalSprintReport r6Report;
        private string r6FinalRoot;
        private string r6PartialRoot;
        private bool r6Complete;
        private int r6ImageIndex;

        public bool R6FinalSprintComplete => r6Complete;
        public string R6FinalSprintFailure => r6Report != null ? r6Report.infrastructureFailure : string.Empty;

        public bool StartR6FinalSprint(string manifestProjectRelativePath)
        {
            if (!Application.isPlaying || active2DRoutine != null || active2DCaptureRoutine != null) return false;
            try
            {
                AutoWire();
                string path = ResolveProjectPath(manifestProjectRelativePath);
                r6Manifest = JsonUtility.FromJson<R6FinalSprintManifest>(File.ReadAllText(path));
                ValidateR6Manifest(r6Manifest);
                r6FinalRoot = ResolveProjectPath(r6Manifest.outputDirectory);
                r6PartialRoot = r6FinalRoot + ".partial";
                if (Directory.Exists(r6FinalRoot) || Directory.Exists(r6PartialRoot))
                    throw new InvalidOperationException("R6 refuses to overwrite final or partial output");
                Directory.CreateDirectory(r6PartialRoot);
                Directory.CreateDirectory(Path.Combine(r6PartialRoot, "images"));
                File.Copy(path, Path.Combine(r6PartialRoot, "capture_input_manifest.json"), false);
                r6Report = new R6FinalSprintReport
                {
                    protocolId = r6Manifest.protocolId,
                    mode = r6Manifest.mode,
                    executionCommit = Environment.GetEnvironmentVariable("THORACIC_R6_EXECUTION_COMMIT") ?? string.Empty,
                    sourceCommit = r6Manifest.sourceCommit,
                    scene = SceneManager.GetActiveScene().path,
                    sceneSha256 = HashR6CanonicalText(r6Manifest.scene),
                    sceneMaterializedSha256 = HashR6Raw(ResolveProjectPath(r6Manifest.scene)),
                    evaluatorSha256 = HashR6CanonicalText("Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.cs"),
                    evaluatorMaterializedSha256 = HashR6Raw(ResolveProjectPath(
                        "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.cs")),
                    manifest = ProjectRelativePath(path),
                    manifestSha256 = HashR6Raw(path),
                    coordinatorProtocolSha256 = r6Manifest.coordinatorProtocolSha256,
                    auditProtocolSha256 = r6Manifest.auditProtocolSha256
                };
                r6ImageIndex = 0;
                r6Complete = false;
                active2DRoutine = StartCoroutine(RunR6FinalSprint());
                return true;
            }
            catch (Exception exception)
            {
                r6Report = r6Report ?? new R6FinalSprintReport();
                r6Report.infrastructureFailure = exception.ToString();
                r6Complete = true;
                Debug.LogError("[ThoracicUSCoach][R6] " + exception);
                return false;
            }
        }

        private IEnumerator RunR6FinalSprint()
        {
            IEnumerator work = r6Manifest.mode == ThoracicR6FinalSprintProtocol.BasinMapMode
                ? RunR6BasinMap() : RunR6Routes();
            try
            {
                while (true)
                {
                    object current;
                    try
                    {
                        if (!work.MoveNext()) break;
                        current = work.Current;
                    }
                    catch (Exception exception)
                    {
                        r6Report.infrastructureFailure = exception.ToString();
                        break;
                    }
                    yield return current;
                }
                r6Report.complete = string.IsNullOrEmpty(r6Report.infrastructureFailure);
            }
            finally
            {
                try
                {
                    if (r6Report.complete) SealR6Snapshot();
                    else SaveR6Report();
                }
                catch (Exception exception)
                {
                    r6Report.complete = false;
                    r6Report.immutableSnapshotSealed = false;
                    r6Report.infrastructureFailure = exception.ToString();
                    SaveR6Report();
                }
                r6Complete = true;
                active2DRoutine = null;
            }
        }

        private IEnumerator RunR6BasinMap()
        {
            Dictionary<string, R6PositiveControl> controls = r6Manifest.positiveControls
                .ToDictionary(item => item.controlId, StringComparer.Ordinal);
            Dictionary<string, R6ObservationRecord> bestByControl =
                new Dictionary<string, R6ObservationRecord>(StringComparer.Ordinal);
            foreach (R6PositiveControl control in r6Manifest.positiveControls)
            {
                R6AttemptRecord attempt = RestoreR6Control(control, "control:" + control.controlId);
                r6Report.attempts.Add(attempt);
                if (!attempt.accepted)
                {
                    r6Report.infrastructureFailure = "Historical positive control pose could not be restored: "
                        + control.controlId + "; " + attempt.rejectionReason;
                    yield break;
                }
                int firstRecord = r6Report.records.Count;
                for (int repeat = 0; repeat < control.repeatRenders; repeat++)
                    yield return CaptureR6Observation(
                        "positive_control", control.controlId, control.sourceCaseId, "control",
                        repeat, repeat, 0f, 0f, 0f, "RESTORE", true, true);
                bool reproduced = r6Report.records.Skip(firstRecord).All(record =>
                    record.acceptable && record.t7Valid && record.contactValid && record.workspaceValid);
                if (!reproduced)
                {
                    r6Report.infrastructureFailure = "Historical positive control did not reproduce: "
                        + control.controlId;
                    yield break;
                }
            }

            foreach (R6MapCandidate candidate in r6Manifest.mapCandidates.Where(item => item.stage == "coarse"))
            {
                r6Report.attemptedCandidatePoses++;
                if (!controls.TryGetValue(candidate.anchorControlId, out R6PositiveControl anchor))
                    throw new InvalidOperationException("R6 candidate references unknown anchor " + candidate.anchorControlId);
                R6AttemptRecord attempt = PlaceR6MapCandidate(anchor, candidate);
                r6Report.attempts.Add(attempt);
                if (!attempt.accepted) continue;
                for (int repeat = 0; repeat < candidate.repeatRenders; repeat++)
                    yield return CaptureR6Observation(
                        "basin_candidate", candidate.candidateId, candidate.anchorControlId, candidate.stage,
                        repeat, repeat, candidate.longitudinalOffsetMm, candidate.lateralOffsetMm,
                        candidate.yawDeg, "MAP", true, true);
                R6ObservationRecord record = r6Report.records[r6Report.records.Count - 1];
                record.anchorControlId = candidate.anchorControlId;
                if (record.t7Valid && record.contactValid && record.workspaceValid
                    && (!bestByControl.TryGetValue(candidate.anchorControlId, out R6ObservationRecord best)
                        || record.score.total > best.score.total
                        || (Mathf.Approximately(record.score.total, best.score.total)
                            && string.CompareOrdinal(record.sourceId, best.sourceId) < 0)))
                    bestByControl[candidate.anchorControlId] = record;
            }

            foreach (R6MapCandidate candidate in r6Manifest.mapCandidates.Where(item => item.stage == "fine"))
            {
                r6Report.attemptedCandidatePoses++;
                if (!bestByControl.TryGetValue(candidate.anchorControlId, out R6ObservationRecord center))
                {
                    r6Report.attempts.Add(RejectR6Attempt(new R6AttemptRecord
                    {
                        attemptId = "candidate:" + candidate.candidateId,
                        sourceKind = "marker_skin_basin_candidate",
                        sourceId = candidate.candidateId
                    }, "fine stage skipped: no physically valid coarse center"));
                    continue;
                }
                R6PositiveControl dynamicAnchor = new R6PositiveControl
                {
                    controlId = center.sourceId,
                    probePosition = center.probePosition,
                    probeRotation = center.probeRotation
                };
                R6AttemptRecord attempt = PlaceR6MapCandidate(dynamicAnchor, candidate);
                r6Report.attempts.Add(attempt);
                if (!attempt.accepted) continue;
                for (int repeat = 0; repeat < candidate.repeatRenders; repeat++)
                    yield return CaptureR6Observation(
                        "basin_candidate", candidate.candidateId, candidate.anchorControlId, candidate.stage,
                        repeat, repeat, candidate.longitudinalOffsetMm, candidate.lateralOffsetMm,
                        candidate.yawDeg, "MAP", true, true);
                R6ObservationRecord record = r6Report.records[r6Report.records.Count - 1];
                record.anchorControlId = candidate.anchorControlId;
                record.parentCandidateId = center.sourceId;
                if (record.t7Valid && record.contactValid && record.workspaceValid
                    && (!bestByControl.TryGetValue(candidate.anchorControlId, out R6ObservationRecord best)
                        || record.score.total > best.score.total
                        || (Mathf.Approximately(record.score.total, best.score.total)
                            && string.CompareOrdinal(record.sourceId, best.sourceId) < 0)))
                    bestByControl[candidate.anchorControlId] = record;
            }

            foreach (KeyValuePair<string, R6ObservationRecord> representative in bestByControl.OrderBy(item => item.Key))
            {
                R6PositiveControl dynamicControl = new R6PositiveControl
                {
                    controlId = representative.Value.sourceId,
                    probePosition = representative.Value.probePosition,
                    probeRotation = representative.Value.probeRotation
                };
                R6AttemptRecord restore = RestoreR6Control(
                    dynamicControl, "representative:" + representative.Value.sourceId);
                restore.sourceKind = "basin_representative";
                r6Report.attempts.Add(restore);
                if (!restore.accepted) continue;
                for (int repeat = 0; repeat < 3; repeat++)
                    yield return CaptureR6Observation(
                        "basin_representative", representative.Value.sourceId,
                        representative.Key, "repeat_stability", repeat, repeat,
                        0f, 0f, 0f, "RESTORE", true, true);
            }
        }

        private IEnumerator RunR6Routes()
        {
            foreach (R6CaptureRoute route in r6Manifest.routes)
            {
                ResetActive2DRunCounters();
                R6AttemptRecord start = PlaceR6MarkerSkinStart(route);
                r6Report.attempts.Add(start);
                r6Report.attemptedStarts++;
                if (!start.accepted) continue;
                r6Report.acceptedStarts++;
                yield return CaptureR6Observation(
                    "route", route.routeId, route.familyId, route.arm, 0, 0,
                    route.startLongitudinalOffsetMm, route.startLateralOffsetMm, route.startYawDeg,
                    "RESET", true, true);
                if (route.repeatRenderAtObservationIndices.Contains(0))
                    yield return CaptureR6Observation(
                        "route", route.routeId, route.familyId, route.arm, 0, 1,
                        route.startLongitudinalOffsetMm, route.startLateralOffsetMm, route.startYawDeg,
                        "OBSERVE", true, true);

                int actionIndex = 0;
                foreach (string actionName in route.actions)
                {
                    R6ActionRecord action = ExecuteR6TargetIndependentAction(actionName, actionIndex++);
                    r6Report.actions.Add(action);
                    if (action.accepted) r6Report.acceptedActions++; else r6Report.rejectedActions++;
                    if (action.safetyFailure) r6Report.safetyFailures++;
                    yield return CaptureR6Observation(
                        "route", route.routeId, route.familyId, route.arm, actionIndex, 0,
                        route.startLongitudinalOffsetMm, route.startLateralOffsetMm, route.startYawDeg,
                        action.plannedAction, action.accepted, action.completed);
                    if (route.repeatRenderAtObservationIndices.Contains(actionIndex))
                        yield return CaptureR6Observation(
                            "route", route.routeId, route.familyId, route.arm, actionIndex, 1,
                            route.startLongitudinalOffsetMm, route.startLateralOffsetMm, route.startYawDeg,
                            "OBSERVE", true, true);
                    if (action.safetyFailure) break;
                }
                r6Report.routesCompleted++;
                if (r6Report.safetyFailures > 0)
                {
                    r6Report.infrastructureFailure = "R6 route safety failure in " + route.routeId;
                    yield break;
                }
            }
        }

        private R6AttemptRecord RestoreR6Control(R6PositiveControl control, string attemptId)
        {
            R6AttemptRecord attempt = new R6AttemptRecord
            {
                attemptId = attemptId,
                sourceKind = "historical_positive_control",
                sourceId = control.controlId,
                requestedPosition = control.probePosition,
                requestedRotation = control.probeRotation
            };
            if (probeRoot == null || faceCenter == null) return RejectR6Attempt(attempt, "probe references missing");
            probeRoot.SetPositionAndRotation(control.probePosition, control.probeRotation);
            seedProbeRotation = control.probeRotation;
            Physics.SyncTransforms();
            bool contact = EnforceRigidContact();
            Physics.SyncTransforms();
            SetR6AxesFromMarkersAndSurface();
            active2DWorkspaceOrigin = faceCenter.position;
            Active2DPrivilegedFrame audit = BuildActive2DPrivilegedFrame(-1, null);
            attempt.resultingPosition = probeRoot.position;
            attempt.resultingRotation = probeRoot.rotation;
            attempt.surfacePoint = audit.contactPosition;
            attempt.surfaceNormal = CurrentSurfaceNormal();
            attempt.surfaceFound = true;
            attempt.contactValid = contact && audit.contactValid;
            attempt.workspaceValid = audit.workspaceValid;
            attempt.t7Valid = audit.t7LevelAcceptable;
            attempt.accepted = attempt.contactValid && attempt.workspaceValid;
            if (!attempt.accepted) attempt.rejectionReason = "positive control restore failed contact/workspace";
            return attempt;
        }

        private R6AttemptRecord PlaceR6MapCandidate(R6PositiveControl anchor, R6MapCandidate candidate)
        {
            R6AttemptRecord attempt = RestoreR6Control(anchor, "candidate:" + candidate.candidateId);
            attempt.sourceKind = "marker_skin_basin_candidate";
            attempt.sourceId = candidate.candidateId;
            if (!attempt.accepted) return attempt;
            Vector3 anchorContact = faceCenter.position;
            Vector3 normal = CurrentSurfaceNormal();
            Vector3 caudal = active2DCaudalAxis.normalized;
            Vector3 lateral = active2DLateralAxis.normalized;
            Vector3 requested = anchorContact
                + caudal * candidate.longitudinalOffsetMm
                + lateral * candidate.lateralOffsetMm;
            attempt.requestedPosition = requested;
            if (!FindSurfaceAt(requested, normal, out RaycastHit hit))
                return RejectR6Attempt(attempt, "candidate skin raycast failed");
            attempt.surfaceFound = true;
            attempt.surfacePoint = hit.point;
            attempt.surfaceNormal = hit.normal;
            AlignProbeToSurface(hit, candidate.yawDeg, 0f);
            attempt.requestedRotation = probeRoot.rotation;
            bool contact = EnforceRigidContact();
            Physics.SyncTransforms();
            Active2DPrivilegedFrame audit = BuildActive2DPrivilegedFrame(-1, null);
            attempt.resultingPosition = probeRoot.position;
            attempt.resultingRotation = probeRoot.rotation;
            attempt.contactValid = contact && audit.contactValid;
            attempt.workspaceValid = audit.workspaceValid;
            attempt.t7Valid = audit.t7LevelAcceptable;
            attempt.accepted = attempt.contactValid && attempt.workspaceValid;
            if (!attempt.accepted) attempt.rejectionReason = "candidate failed contact/workspace";
            return attempt;
        }

        private R6AttemptRecord PlaceR6MarkerSkinStart(R6CaptureRoute route)
        {
            R6AttemptRecord attempt = new R6AttemptRecord
            {
                attemptId = "start:" + route.routeId,
                sourceKind = "marker_skin_start",
                sourceId = route.routeId
            };
            if (probeRoot == null || faceCenter == null || skinCollider == null
                || t6RightMarker == null || t7RightSeed == null || t8RightMarker == null)
                return RejectR6Attempt(attempt, "marker/skin references missing");
            Vector3 annotation = (t8RightMarker.position - t6RightMarker.position).normalized;
            Vector3 provisionalNormal = (t7RightSeed.position - skinCollider.bounds.center).normalized;
            if (provisionalNormal.sqrMagnitude < 0.5f) provisionalNormal = Vector3.back;
            Vector3 lateral = Vector3.Cross(provisionalNormal, annotation).normalized;
            Vector3 requested = t7RightSeed.position
                + annotation * route.startLongitudinalOffsetMm
                + lateral * route.startLateralOffsetMm;
            attempt.requestedPosition = requested;
            if (!FindSurfaceAt(requested, Vector3.zero, out RaycastHit hit))
                return RejectR6Attempt(attempt, "start skin raycast failed");
            attempt.surfaceFound = true;
            attempt.surfacePoint = hit.point;
            attempt.surfaceNormal = hit.normal;
            seedProbeRotation = active2DCanonicalProbeRotation == default
                ? probeRoot.rotation : active2DCanonicalProbeRotation;
            active2DCaudalAxis = Vector3.ProjectOnPlane(annotation, hit.normal).normalized;
            active2DLateralAxis = Vector3.Cross(hit.normal, active2DCaudalAxis).normalized;
            caudalTangent = active2DCaudalAxis;
            AlignProbeToSurface(hit, route.startYawDeg, 0f);
            attempt.requestedRotation = probeRoot.rotation;
            bool contact = EnforceRigidContact();
            Physics.SyncTransforms();
            active2DWorkspaceOrigin = faceCenter.position;
            Active2DPrivilegedFrame audit = BuildActive2DPrivilegedFrame(-1, null);
            attempt.resultingPosition = probeRoot.position;
            attempt.resultingRotation = probeRoot.rotation;
            attempt.contactValid = contact && audit.contactValid;
            attempt.workspaceValid = audit.workspaceValid;
            attempt.t7Valid = audit.t7LevelAcceptable;
            attempt.accepted = attempt.contactValid && attempt.workspaceValid && attempt.t7Valid;
            if (!attempt.accepted) attempt.rejectionReason = "start failed contact/workspace/T7 validity";
            return attempt;
        }

        private R6ActionRecord ExecuteR6TargetIndependentAction(string actionName, int index)
        {
            if (!Enum.TryParse(actionName, true, out Active2DAction action))
                throw new InvalidOperationException("R6 action invalid: " + actionName);
            Vector3 beforePosition = probeRoot.position;
            Quaternion beforeRotation = probeRoot.rotation;
            bool accepted = TryActive2DAction(action);
            Active2DActionExecution execution = active2DLastActionExecution;
            return new R6ActionRecord
            {
                index = index,
                plannedAction = action.ToString().ToUpperInvariant(),
                executedAction = accepted ? action.ToString().ToUpperInvariant() : "NONE",
                accepted = accepted,
                completed = accepted && IsActive2DActionExecutionComplete(execution),
                rejectionReason = execution != null ? execution.rejectionReason : "missing execution record",
                requestedDisplacementMm = execution != null ? execution.requestedDisplacementMm : 0f,
                attemptedDisplacementMm = execution != null ? execution.attemptedDisplacementMm : 0f,
                achievedDisplacementMm = execution != null ? execution.achievedDisplacementMm : 0f,
                requestedRotationDeg = execution != null ? execution.requestedRotationDeg : 0f,
                achievedRotationDeg = execution != null ? execution.achievedRotationDeg : 0f,
                contactValidAfterExecution = execution != null && execution.contactValidAfterExecution,
                workspaceValidAfterExecution = execution != null && execution.workspaceValidAfterExecution,
                rollbackAttempted = execution != null && execution.rollbackAttempted,
                rollbackContactRestored = execution != null && execution.rollbackContactRestored,
                safetyFailure = execution != null && execution.safetyFailure,
                beforePosition = beforePosition,
                beforeRotation = beforeRotation,
                afterPosition = probeRoot.position,
                afterRotation = probeRoot.rotation
            };
        }

        private IEnumerator CaptureR6Observation(
            string sourceKind, string sourceId, string familyId, string arm,
            int observationIndex, int repeatIndex,
            float longitudinalOffsetMm, float lateralOffsetMm, float yawDeg,
            string action, bool accepted, bool completed)
        {
            Texture2D image = CaptureCleanImage();
            if (image == null) throw new InvalidOperationException("R6 clean image capture returned null");
            byte[] bytes = image.EncodeToPNG();
            string relative = "images/frame_" + r6ImageIndex.ToString("D4", CultureInfo.InvariantCulture) + "_us.png";
            File.WriteAllBytes(Path.Combine(r6PartialRoot, relative), bytes);
            ScoreBreakdown score = ScoreCandidate(image, SampleAnatomyLabels(labelGridSize));
            Active2DPrivilegedFrame label = BuildActive2DPrivilegedFrame(r6ImageIndex, null);
            label.finalTargetScore = score.total;
            label.finalTargetAcceptable = score.total >= r6Manifest.acceptableScoreFloor;
            Destroy(image);
            float margin = score.total - r6Manifest.acceptableScoreFloor;
            R6ObservationRecord record = new R6ObservationRecord
            {
                recordId = "r6_record_" + r6ImageIndex.ToString("D4", CultureInfo.InvariantCulture),
                sourceKind = sourceKind,
                sourceId = sourceId,
                familyId = familyId,
                arm = arm,
                observationIndex = observationIndex,
                repeatIndex = repeatIndex,
                longitudinalOffsetMm = longitudinalOffsetMm,
                lateralOffsetMm = lateralOffsetMm,
                yawDeg = yawDeg,
                image = relative.Replace('\\', '/'),
                imageSha256 = Sha256Bytes(bytes),
                probePosition = label.probePosition,
                probeRotation = label.probeRotation,
                contactPosition = label.contactPosition,
                contactValid = label.contactValid,
                workspaceValid = label.workspaceValid,
                t7Valid = label.t7LevelAcceptable,
                nearestLevel = label.nearestLevel,
                t7DistanceMm = label.t7DistanceMm,
                score = score,
                margin = margin,
                acceptable = score.total >= r6Manifest.acceptableScoreFloor,
                finalTargetAcceptable = label.finalTargetAcceptable,
                evaluatorFloorAgreement = label.finalTargetAcceptable
                    == (score.total >= r6Manifest.acceptableScoreFloor),
                incomingAction = action,
                actionAccepted = accepted,
                actionCompleted = completed
            };
            r6Report.records.Add(record);
            r6Report.observations++;
            if (record.t7Valid) r6Report.t7ValidObservations++;
            if (record.acceptable) r6Report.acceptableObservations++;
            if (margin >= 0f && margin <= 0.015f) r6Report.nearBoundaryPositiveObservations++;
            if (margin < 0f && margin >= -0.05f) r6Report.hardNegativeObservations++;
            if (!record.contactValid) r6Report.contactViolations++;
            if (!record.workspaceValid) r6Report.workspaceViolations++;
            r6ImageIndex++;
            yield return null;
        }

        private void SetR6AxesFromMarkersAndSurface()
        {
            Vector3 annotation = t6RightMarker != null && t8RightMarker != null
                ? (t8RightMarker.position - t6RightMarker.position).normalized : Vector3.down;
            Vector3 normal = CurrentSurfaceNormal();
            active2DCaudalAxis = Vector3.ProjectOnPlane(annotation, normal).normalized;
            if (active2DCaudalAxis.sqrMagnitude < 0.5f)
                active2DCaudalAxis = Vector3.ProjectOnPlane(Vector3.down, normal).normalized;
            active2DLateralAxis = Vector3.Cross(normal, active2DCaudalAxis).normalized;
            caudalTangent = active2DCaudalAxis;
        }

        private static R6AttemptRecord RejectR6Attempt(R6AttemptRecord attempt, string reason)
        {
            attempt.accepted = false;
            attempt.rejectionReason = reason;
            return attempt;
        }

        private void ValidateR6Manifest(R6FinalSprintManifest manifest)
        {
            ThoracicR6FinalSprintProtocol.Validate(manifest);
            if (manifest.scene != SceneManager.GetActiveScene().path || manifest.unityVersion != Application.unityVersion)
                throw new InvalidOperationException("R6 scene/Unity mismatch");
            if (HashR6CanonicalText(manifest.scene) != ThoracicR6FinalSprintProtocol.FrozenSceneSha256
                || HashR6CanonicalText("Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.cs")
                    != ThoracicR6FinalSprintProtocol.FrozenEvaluatorSha256)
                throw new InvalidOperationException("R6 frozen scene/evaluator identity mismatch");
            string execution = Environment.GetEnvironmentVariable("THORACIC_R6_EXECUTION_COMMIT") ?? string.Empty;
            if (string.IsNullOrEmpty(execution) || execution != manifest.sourceCommit)
                throw new InvalidOperationException("R6 execution commit mismatch");
            RequireR6CanonicalHash(manifest.coordinatorProtocolPath, manifest.coordinatorProtocolSha256);
            RequireR6CanonicalHash(manifest.auditProtocolPath, manifest.auditProtocolSha256);
            if (manifest.mode != ThoracicR6FinalSprintProtocol.BasinMapMode)
            {
                string evidence = ResolveProjectPath(manifest.prerequisiteEvidencePath);
                if (!HashR6Raw(evidence).Equals(manifest.prerequisiteEvidenceSha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("R6 prerequisite evidence hash mismatch");
                R6PrerequisiteDecision decision = JsonUtility.FromJson<R6PrerequisiteDecision>(File.ReadAllText(evidence));
                if (decision == null || !string.Equals(
                    decision.verdict, manifest.prerequisiteRequiredVerdict, StringComparison.Ordinal))
                    throw new InvalidOperationException("R6 prerequisite verdict missing; mode remains blocked");
            }
        }

        private void RequireR6CanonicalHash(string relativePath, string expected)
        {
            if (!HashR6CanonicalText(relativePath).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("R6 frozen protocol hash mismatch: " + relativePath);
        }

        private string HashR6CanonicalText(string relativePath)
        {
            string text = File.ReadAllText(ResolveProjectPath(relativePath))
                .Replace("\r\n", "\n").Replace("\r", "\n");
            return Sha256Bytes(new System.Text.UTF8Encoding(false).GetBytes(text));
        }

        private static string HashR6Raw(string absolutePath)
        {
            using (System.Security.Cryptography.SHA256 algorithm = System.Security.Cryptography.SHA256.Create())
                return BitConverter.ToString(algorithm.ComputeHash(File.ReadAllBytes(absolutePath)))
                    .Replace("-", string.Empty).ToLowerInvariant();
        }

        private void SaveR6Report()
        {
            if (r6Report == null || string.IsNullOrEmpty(r6PartialRoot) || !Directory.Exists(r6PartialRoot)) return;
            r6Report.generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            File.WriteAllText(Path.Combine(r6PartialRoot, "r6_unity_report.json"), JsonUtility.ToJson(r6Report, true));
        }

        private void SealR6Snapshot()
        {
            CopyR6FrozenSources();
            r6Report.immutableSnapshotSealed = true;
            SaveR6Report();
            R6EvidenceIndex index = new R6EvidenceIndex
            {
                protocolId = r6Manifest.protocolId,
                generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                sourceCommit = r6Manifest.sourceCommit,
                coordinatorProtocolSha256 = r6Manifest.coordinatorProtocolSha256,
                auditProtocolSha256 = r6Manifest.auditProtocolSha256
            };
            foreach (string path in Directory.EnumerateFiles(r6PartialRoot, "*", SearchOption.AllDirectories)
                .Where(path => !Path.GetFileName(path).Equals("evidence_index.json", StringComparison.OrdinalIgnoreCase)
                    && !Path.GetFileName(path).Equals("snapshot_seal.json", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.Ordinal))
            {
                FileInfo info = new FileInfo(path);
                index.files.Add(new R6FileIntegrityRecord
                {
                    path = path.Substring(r6PartialRoot.Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/'),
                    sha256 = HashR6Raw(path),
                    bytes = info.Length
                });
            }
            string indexPath = Path.Combine(r6PartialRoot, "evidence_index.json");
            File.WriteAllText(indexPath, JsonUtility.ToJson(index, true));
            string reportPath = Path.Combine(r6PartialRoot, "r6_unity_report.json");
            string manifestPath = Path.Combine(r6PartialRoot, "capture_input_manifest.json");
            R6SnapshotSeal seal = new R6SnapshotSeal
            {
                protocolId = r6Manifest.protocolId,
                generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                sourceCommit = r6Manifest.sourceCommit,
                evidenceIndexSha256 = HashR6Raw(indexPath),
                reportSha256 = HashR6Raw(reportPath),
                manifestSha256 = HashR6Raw(manifestPath),
                coordinatorProtocolCanonicalSha256 = r6Manifest.coordinatorProtocolSha256,
                coordinatorProtocolMaterializedSha256 = HashR6Raw(ResolveProjectPath(r6Manifest.coordinatorProtocolPath)),
                auditProtocolCanonicalSha256 = r6Manifest.auditProtocolSha256,
                auditProtocolMaterializedSha256 = HashR6Raw(ResolveProjectPath(r6Manifest.auditProtocolPath)),
                sceneCanonicalSha256 = r6Report.sceneSha256,
                sceneMaterializedSha256 = r6Report.sceneMaterializedSha256,
                evaluatorCanonicalSha256 = r6Report.evaluatorSha256,
                evaluatorMaterializedSha256 = r6Report.evaluatorMaterializedSha256,
                indexedFiles = index.files.Count,
                recordCount = r6Report.records.Count,
                imageCount = r6Report.records.Count(record => !string.IsNullOrEmpty(record.image)),
                actionCount = r6Report.actions.Count,
                attemptCount = r6Report.attempts.Count,
                complete = true,
                immutable = true
            };
            File.WriteAllText(Path.Combine(r6PartialRoot, "snapshot_seal.json"), JsonUtility.ToJson(seal, true));
            Directory.Move(r6PartialRoot, r6FinalRoot);
        }

        private void CopyR6FrozenSources()
        {
            string generator = Path.Combine(r6PartialRoot, "generator_sources");
            string contracts = Path.Combine(r6PartialRoot, "frozen_contracts");
            Directory.CreateDirectory(generator);
            Directory.CreateDirectory(contracts);
            foreach (string source in new[]
            {
                "Assets/ThoracicUSCoach/Runtime/ThoracicR6FinalSprintTypes.cs",
                "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.R6FinalSprint.cs",
                "Assets/ThoracicUSCoach/Editor/ThoracicR6FinalSprintBatch.cs",
                "Tools/ThoracicUSCoach/prepare_r6_basin_manifest.ps1",
                "Tools/ThoracicUSCoach/prepare_r6_wide_scout_manifest.ps1",
                "Tools/ThoracicUSCoach/run_r6_final_sprint.ps1"
            })
                File.Copy(ResolveProjectPath(source), Path.Combine(generator, Path.GetFileName(source)), false);
            foreach (string source in new[]
            {
                ThoracicR6FinalSprintProtocol.CoordinatorProtocolPath,
                ThoracicR6FinalSprintProtocol.AuditProtocolPath,
                "Tools/ThoracicUSCoach/experiments/g2_r6_contract_identity_v1.json"
            })
                File.Copy(ResolveProjectPath(source), Path.Combine(contracts, Path.GetFileName(source)), false);
        }
    }
}
