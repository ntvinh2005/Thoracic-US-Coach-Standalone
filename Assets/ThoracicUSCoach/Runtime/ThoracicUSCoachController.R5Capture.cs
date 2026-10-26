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
        private bool r5CaptureComplete;
        private R5CaptureManifest r5Manifest;
        private R5UnityReport r5Report;
        private string r5FinalRoot;
        private string r5PartialRoot;

        public bool R5CaptureComplete => r5CaptureComplete;
        public string R5CaptureFailure => r5Report != null ? r5Report.infrastructureFailure : string.Empty;

        public bool StartR5Capture(string manifestProjectRelativePath)
        {
            if (!Application.isPlaying || active2DRoutine != null || active2DCaptureRoutine != null) return false;
            try
            {
                AutoWire();
                string manifestPath = ResolveProjectPath(manifestProjectRelativePath);
                r5Manifest = JsonUtility.FromJson<R5CaptureManifest>(File.ReadAllText(manifestPath));
                ValidateR5Manifest(r5Manifest);
                r5FinalRoot = ResolveProjectPath(r5Manifest.outputDirectory);
                r5PartialRoot = r5FinalRoot + ".partial";
                if (Directory.Exists(r5FinalRoot) || Directory.Exists(r5PartialRoot))
                    throw new InvalidOperationException("R5 capture refuses to overwrite existing final or partial evidence");
                Directory.CreateDirectory(r5PartialRoot);
                File.Copy(manifestPath, Path.Combine(r5PartialRoot, "capture_input_manifest.json"), false);
                r5Report = new R5UnityReport
                {
                    protocolId = r5Manifest.protocolId,
                    mode = r5Manifest.mode,
                    generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    executionCommit = Environment.GetEnvironmentVariable("THORACIC_R5_EXECUTION_COMMIT") ?? string.Empty,
                    sourceCommit = r5Manifest.sourceCommit,
                    unityVersion = Application.unityVersion,
                    scene = SceneManager.GetActiveScene().path,
                    sceneSha256 = HashR5CanonicalTextFile(r5Manifest.scene),
                    evaluatorSha256 = HashR5CanonicalTextFile("Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.cs"),
                    active2DControllerSha256 = HashR5CanonicalTextFile("Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.Active2D.cs"),
                    captureControllerSha256 = HashR5CanonicalTextFile("Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.R5Capture.cs"),
                    manifest = ProjectRelativePath(manifestPath),
                    manifestSha256 = Sha256Bytes(File.ReadAllBytes(manifestPath)),
                    sharedContract = r5Manifest.sharedContractPath,
                    sharedContractSha256 = r5Manifest.sharedContractSha256,
                    sharedContractMaterializedSha256 = HashR5ProjectFile(r5Manifest.sharedContractPath),
                    scientificCriteria = r5Manifest.scientificCriteriaPath,
                    scientificCriteriaSha256 = r5Manifest.scientificCriteriaSha256,
                    scientificCriteriaMaterializedSha256 = HashR5ProjectFile(r5Manifest.scientificCriteriaPath),
                    independentAudit = r5Manifest.independentAuditPath,
                    independentAuditSha256 = r5Manifest.independentAuditSha256,
                    independentAuditMaterializedSha256 = HashR5ProjectFile(r5Manifest.independentAuditPath),
                    acceptableScoreFloor = r5Manifest.acceptableScoreFloor,
                    requestedRoutes = r5Manifest.routes.Count
                };
                r5CaptureComplete = false;
                active2DRoutine = StartCoroutine(RunR5Capture());
                return true;
            }
            catch (Exception exception)
            {
                r5Report = r5Report ?? new R5UnityReport();
                r5Report.infrastructureFailure = exception.ToString();
                r5CaptureComplete = true;
                Debug.LogError("[ThoracicUSCoach][G2-R5] " + exception);
                return false;
            }
        }

        private IEnumerator RunR5Capture()
        {
            IEnumerator routes = RunR5Routes();
            try
            {
                while (true)
                {
                    object current;
                    try
                    {
                        if (!routes.MoveNext()) break;
                        current = routes.Current;
                    }
                    catch (Exception exception)
                    {
                        r5Report.infrastructureFailure = exception.ToString();
                        break;
                    }
                    yield return current;
                }
                r5Report.complete = string.IsNullOrEmpty(r5Report.infrastructureFailure)
                    && r5Report.completedRoutes + r5Report.failedRoutes == r5Report.requestedRoutes;
            }
            finally
            {
                if (r5Report.complete)
                {
                    try
                    {
                        SealR5Snapshot();
                    }
                    catch (Exception exception)
                    {
                        r5Report.complete = false;
                        r5Report.immutableSnapshotSealed = false;
                        r5Report.infrastructureFailure = "Snapshot sealing failed: " + exception;
                        SaveR5Report(r5PartialRoot);
                    }
                }
                else
                {
                    if (string.IsNullOrEmpty(r5Report.infrastructureFailure))
                        r5Report.infrastructureFailure = "R5 capture ended before all routes completed";
                    SaveR5Report(r5PartialRoot);
                }
                r5CaptureComplete = true;
                active2DRoutine = null;
            }
        }

        private IEnumerator RunR5Routes()
        {
            foreach (R5CaptureRoute route in r5Manifest.routes)
            {
                R5RouteResult result = new R5RouteResult
                {
                    routeId = route.routeId,
                    familyId = route.familyId,
                    arm = route.arm,
                    coverageIntent = route.coverageIntent,
                    startStratum = route.startStratum,
                    proposalSeed = route.proposalSeed,
                    captureStatus = "started"
                };
                r5Report.routes.Add(result);
                yield return CaptureR5Route(route, result);
                if (result.observations.Count == 0)
                {
                    result.minimumScore = 0f;
                    result.maximumScore = 0f;
                }
                if (result.captureStatus == "complete") r5Report.completedRoutes++;
                else r5Report.failedRoutes++;
                SaveR5Report(r5PartialRoot);
                if (result.safetyFailures > 0)
                {
                    r5Report.infrastructureFailure = "Safety failure in " + route.routeId;
                    yield break;
                }
            }
        }

        private IEnumerator CaptureR5Route(R5CaptureRoute route, R5RouteResult result)
        {
            ResetActive2DRunCounters();
            bool placed = false;
            for (int attempt = 0; attempt < route.maxStartAttempts; attempt++)
            {
                if (r5Report.attemptedStartProposals >= r5Manifest.maxStartProposals)
                    throw new InvalidOperationException("Runtime start proposal budget exhausted");
                R5StartProposalAudit proposal = TryPlaceR5MarkerDerivedStart(route, attempt);
                result.startProposals.Add(proposal);
                r5Report.allStartProposals.Add(proposal);
                r5Report.attemptedStartProposals++;
                if (proposal.acceptedAsStart)
                {
                    result.acceptedStartProposalId = R5ProposalId(proposal);
                    r5Report.acceptedStarts++;
                    placed = true;
                    break;
                }
                yield return null;
            }
            if (!placed)
            {
                result.captureStatus = "rejected_start";
                result.failureReason = "No safe marker/skin-derived start was accepted";
                yield break;
            }

            string routeDirectory = Path.Combine(r5PartialRoot, route.arm, route.routeId);
            Directory.CreateDirectory(routeDirectory);
            Active2DStreamManifest stream = new Active2DStreamManifest
            {
                datasetRunId = r5Manifest.datasetRunId,
                episodeId = route.routeId,
                split = "exploratory_development",
                family = route.familyId,
                seed = route.proposalSeed,
                captureStatus = "partial",
                imageSource = "primary-ultrasound-camera-1024",
                policyInputs = "clean ultrasound history plus actually executed action feedback"
            };
            Active2DPrivilegedAnnotations privileged = new Active2DPrivilegedAnnotations
            {
                episodeId = route.routeId,
                workspaceOrigin = active2DWorkspaceOrigin,
                actuatorCranialAxis = -active2DCaudalAxis,
                actuatorLateralAxis = active2DLateralAxis,
                t7TargetPosition = t7RightSeed != null ? t7RightSeed.position : Vector3.zero,
                t7PreciseRadiusMm = Active2DT7PreciseRadiusMm(),
                stepMm = active2DStepMm,
                longitudinalHalfExtentMm = active2DLongitudinalHalfExtentMm,
                lateralHalfExtentMm = active2DLateralHalfExtentMm
            };

            int outputIndex = 0;
            int sourceIndex = 0;
            yield return StoreR5Observation(
                routeDirectory, stream, privileged, result, outputIndex++, sourceIndex,
                0, "RESET", true, true, true);
            if (route.repeatRenderAtObservationIndices.Contains(sourceIndex))
                yield return StoreR5Observation(
                    routeDirectory, stream, privileged, result, outputIndex++, sourceIndex,
                    1, "OBSERVE", true, true, false);

            int actionIndex = 0;
            foreach (string actionName in route.actions)
            {
                sourceIndex++;
                yield return ExecuteR5Action(actionName, actionIndex++, result);
                R5ActionAudit execution = result.actions[result.actions.Count - 1];
                yield return StoreR5Observation(
                    routeDirectory, stream, privileged, result, outputIndex++, sourceIndex,
                    0, execution.action, execution.accepted, execution.completed, false);
                if (route.repeatRenderAtObservationIndices.Contains(sourceIndex))
                    yield return StoreR5Observation(
                        routeDirectory, stream, privileged, result, outputIndex++, sourceIndex,
                        1, "OBSERVE", true, true, false);
                if (execution.safetyFailure)
                {
                    result.failureReason = "Trajectory safety failure: " + execution.rejectionReason;
                    break;
                }
            }

            stream.captureStatus = result.safetyFailures == 0 ? "complete" : "failed";
            result.captureStatus = stream.captureStatus;
            WriteR5RouteFiles(routeDirectory, stream, privileged, result);
        }

        private R5StartProposalAudit TryPlaceR5MarkerDerivedStart(R5CaptureRoute route, int attempt)
        {
            R5StartProposalAudit audit = new R5StartProposalAudit
            {
                proposalIndex = r5Report.attemptedStartProposals,
                routeId = route.routeId,
                attemptWithinRoute = attempt,
                proposalSeed = route.proposalSeed,
                startStratum = route.startStratum,
                resultingProbePosition = probeRoot != null ? probeRoot.position : Vector3.zero,
                resultingProbeRotation = probeRoot != null ? probeRoot.rotation : Quaternion.identity
            };
            if (probeRoot == null || faceCenter == null || skinCollider == null
                || t6RightMarker == null || t7RightSeed == null || t8RightMarker == null)
            {
                audit.rejectionReason = "Required probe, skin, or T6/T7/T8 marker reference is missing";
                return audit;
            }

            Vector3 originalPosition = probeRoot.position;
            Quaternion originalRotation = probeRoot.rotation;
            Vector3 annotationAxis = (t8RightMarker.position - t6RightMarker.position).normalized;
            if (annotationAxis.sqrMagnitude < 0.5f)
            {
                audit.rejectionReason = "T6/T8 annotation axis is degenerate";
                return audit;
            }
            float markerSpacing = Mathf.Min(
                Vector3.Distance(t6RightMarker.position, t7RightSeed.position),
                Vector3.Distance(t7RightSeed.position, t8RightMarker.position));
            ThoracicR5CaptureProtocol.StartOffsets(
                route.startStratum, route.proposalSeed, attempt, markerSpacing,
                out audit.longitudinalOffsetMm, out audit.lateralOffsetMm, out audit.yawDeg);
            Vector3 provisionalNormal = (t7RightSeed.position - skinCollider.bounds.center).normalized;
            if (provisionalNormal.sqrMagnitude < 0.5f) provisionalNormal = Vector3.back;
            Vector3 annotationLateral = Vector3.Cross(provisionalNormal, annotationAxis).normalized;
            if (annotationLateral.sqrMagnitude < 0.5f)
            {
                audit.rejectionReason = "Marker-derived lateral axis is degenerate";
                return audit;
            }
            audit.proposedWorldPoint = t7RightSeed.position
                + annotationAxis * audit.longitudinalOffsetMm
                + annotationLateral * audit.lateralOffsetMm;
            audit.resultingProbePosition = audit.proposedWorldPoint;
            if (!FindSurfaceAt(audit.proposedWorldPoint, Vector3.zero, out RaycastHit hit))
            {
                audit.rejectionReason = "Skin surface raycast failed";
                return audit;
            }
            audit.surfaceRaycastFound = true;
            audit.surfacePoint = hit.point;
            audit.surfaceNormal = hit.normal;

            seedProbeRotation = active2DCanonicalProbeRotation == default
                ? probeRoot.rotation : active2DCanonicalProbeRotation;
            active2DCaudalAxis = Vector3.ProjectOnPlane(annotationAxis, hit.normal).normalized;
            if (active2DCaudalAxis.sqrMagnitude < 0.5f)
                active2DCaudalAxis = Vector3.ProjectOnPlane(Vector3.down, hit.normal).normalized;
            if (Vector3.Dot(active2DCaudalAxis, annotationAxis) < 0f)
                active2DCaudalAxis = -active2DCaudalAxis;
            active2DLateralAxis = Vector3.Cross(hit.normal.normalized, active2DCaudalAxis).normalized;
            if (Vector3.Dot(active2DLateralAxis, annotationLateral) < 0f)
                active2DLateralAxis = -active2DLateralAxis;
            caudalTangent = active2DCaudalAxis;
            AlignProbeToSurface(hit, audit.yawDeg, 0f);
            audit.rigidContactApplied = EnforceRigidContact();
            Physics.SyncTransforms();
            active2DWorkspaceOrigin = faceCenter.position;
            Active2DPrivilegedFrame frame = BuildActive2DPrivilegedFrame(-1, null);
            audit.contactValid = frame.contactValid;
            audit.workspaceValid = frame.workspaceValid;
            audit.t7LevelAcceptable = frame.t7LevelAcceptable;
            audit.resultingProbePosition = probeRoot.position;
            audit.resultingProbeRotation = probeRoot.rotation;
            audit.resultingContactPosition = frame.contactPosition;
            audit.acceptedAsStart = audit.rigidContactApplied && audit.contactValid
                && audit.workspaceValid && audit.t7LevelAcceptable;
            if (!audit.acceptedAsStart)
            {
                audit.rejectionReason = "Start failed rigid-contact/workspace/T7 validity: rigid="
                    + audit.rigidContactApplied + " contact=" + audit.contactValid
                    + " workspace=" + audit.workspaceValid + " t7=" + audit.t7LevelAcceptable;
                probeRoot.SetPositionAndRotation(originalPosition, originalRotation);
                Physics.SyncTransforms();
                EnforceRigidContact();
            }
            return audit;
        }

        private IEnumerator ExecuteR5Action(string actionName, int sequenceIndex, R5RouteResult result)
        {
            if (!Enum.TryParse(actionName, true, out Active2DAction action))
                throw new InvalidOperationException("Unsupported R5 action " + actionName);
            Vector3 beforePosition = probeRoot.position;
            Quaternion beforeRotation = probeRoot.rotation;
            bool accepted = TryActive2DAction(action);
            yield return null;
            Active2DActionExecution execution = active2DLastActionExecution;
            bool completed = accepted && IsActive2DActionExecutionComplete(execution);
            R5ActionAudit audit = new R5ActionAudit
            {
                sequenceIndex = sequenceIndex,
                action = action.ToString().ToUpperInvariant(),
                accepted = accepted,
                completed = completed,
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
                rejectionReason = execution != null ? execution.rejectionReason : "missing execution record",
                beforePosition = beforePosition,
                beforeRotation = beforeRotation,
                afterPosition = probeRoot.position,
                afterRotation = probeRoot.rotation
            };
            result.actions.Add(audit);
            if (accepted) result.acceptedActions++; else result.rejectedActions++;
            if (accepted && !completed) result.incompleteActions++;
            if (audit.safetyFailure) result.safetyFailures++;
            r5Report.acceptedActions += accepted ? 1 : 0;
            r5Report.rejectedActions += accepted ? 0 : 1;
            r5Report.incompleteActions += accepted && !completed ? 1 : 0;
            r5Report.safetyFailures += audit.safetyFailure ? 1 : 0;
        }

        private IEnumerator StoreR5Observation(
            string directory,
            Active2DStreamManifest stream,
            Active2DPrivilegedAnnotations privileged,
            R5RouteResult result,
            int outputIndex,
            int sourceObservationIndex,
            int repeatIndex,
            string action,
            bool accepted,
            bool completed,
            bool reset)
        {
            Texture2D image = CaptureCleanImage();
            if (image == null) throw new InvalidOperationException("Clean renderer returned null for " + result.routeId);
            byte[] bytes = image.EncodeToPNG();
            string filename = "frame_" + outputIndex.ToString("D4", CultureInfo.InvariantCulture) + "_us.png";
            string absolute = Path.Combine(directory, filename);
            File.WriteAllBytes(absolute, bytes);
            string relative = ProjectRelativePath(absolute).Replace(".partial/", "/").Replace(".partial\\", "\\");
            ScoreBreakdown score = ScoreCandidate(image, SampleAnatomyLabels(labelGridSize));
            Active2DPrivilegedFrame label = BuildActive2DPrivilegedFrame(outputIndex, null);
            label.finalTargetScore = score.total;
            label.finalTargetAcceptable = score.total >= r5Manifest.acceptableScoreFloor;
            Destroy(image);
            string imageHash = Sha256Bytes(bytes);
            stream.frames.Add(new Active2DStreamFrame
            {
                index = outputIndex,
                image = relative,
                imageSha256 = imageHash,
                issuedAction = action,
                actionAccepted = accepted,
                actionCompleted = completed,
                episodeReset = reset
            });
            privileged.frames.Add(label);
            float margin = score.total - r5Manifest.acceptableScoreFloor;
            R5ObservationAudit observation = new R5ObservationAudit
            {
                index = outputIndex,
                sourceObservationIndex = sourceObservationIndex,
                repeatIndex = repeatIndex,
                image = relative,
                imageSha256 = imageHash,
                incomingAction = action,
                actionAccepted = accepted,
                actionCompleted = completed,
                episodeReset = reset,
                probePosition = label.probePosition,
                probeRotation = label.probeRotation,
                contactPosition = label.contactPosition,
                signedDepthMm = label.signedDepthMm,
                contactValid = label.contactValid,
                workspaceValid = label.workspaceValid,
                nearestLevel = label.nearestLevel,
                nearestLevelDistanceMm = label.nearestLevelDistanceMm,
                t7DistanceMm = label.t7DistanceMm,
                t7LevelAcceptable = label.t7LevelAcceptable,
                score = score,
                marginToAcceptableFloor = margin,
                finalTargetAcceptable = label.finalTargetAcceptable,
                evaluatorFloorAgreement = label.finalTargetAcceptable
                    == (score.total >= r5Manifest.acceptableScoreFloor)
            };
            result.observations.Add(observation);
            result.minimumScore = Mathf.Min(result.minimumScore, score.total);
            result.maximumScore = Mathf.Max(result.maximumScore, score.total);
            if (observation.finalTargetAcceptable)
            {
                result.positiveObservations++;
                r5Report.positiveObservations++;
            }
            if (margin >= 0f && margin <= 0.015f) result.nearBoundaryPositiveObservations++;
            if (margin < 0f && margin >= -0.015f) result.nearBoundaryNegativeObservations++;
            if (margin < 0f && margin >= -0.05f)
            {
                result.hardNegativeObservations++;
                r5Report.hardNegativeObservations++;
            }
            r5Report.totalObservations++;
            if (!observation.contactValid) r5Report.contactViolations++;
            if (!observation.workspaceValid) r5Report.workspaceViolations++;
            yield return null;
        }

        private static void WriteR5RouteFiles(
            string directory, Active2DStreamManifest stream,
            Active2DPrivilegedAnnotations privileged, R5RouteResult result)
        {
            File.WriteAllText(Path.Combine(directory, "stream_manifest.json"), JsonUtility.ToJson(stream, true));
            File.WriteAllText(Path.Combine(directory, "privileged_annotations.json"), JsonUtility.ToJson(privileged, true));
            File.WriteAllText(Path.Combine(directory, "route_audit.json"), JsonUtility.ToJson(result, true));
        }

        private void ValidateR5Manifest(R5CaptureManifest manifest)
        {
            ThoracicR5CaptureProtocol.ValidateStructure(manifest);
            if (manifest.scene != SceneManager.GetActiveScene().path
                || manifest.unityVersion != Application.unityVersion)
                throw new InvalidOperationException("R5 scene or Unity version mismatch");
            if (manifest.baseCommit != ThoracicR5CaptureProtocol.FrozenBaseCommit
                || manifest.captureBranch != "codex/r5-unity")
                throw new InvalidOperationException("R5 source ancestry or capture branch changed");
            if (manifest.sharedContractPath != "Tools/ThoracicUSCoach/experiments/g2_r5_evidence_schema_v1.json"
                || manifest.sharedContractSha256 != ThoracicR5CaptureProtocol.FrozenSchemaSha256
                || manifest.scientificCriteriaPath != "Tools/ThoracicUSCoach/experiments/g2_r5_protocol_v1.json"
                || manifest.scientificCriteriaSha256 != ThoracicR5CaptureProtocol.FrozenProtocolSha256
                || manifest.independentAuditPath != "Tools/ThoracicUSCoach/experiments/g2_r5_audit_protocol_v1.json"
                || manifest.independentAuditSha256 != ThoracicR5CaptureProtocol.FrozenAuditSha256)
                throw new InvalidOperationException("R5 frozen contract identity changed");
            if (HashR5CanonicalTextFile(manifest.scene) != ThoracicR5CaptureProtocol.FrozenSceneSha256
                || HashR5CanonicalTextFile("Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.cs")
                    != ThoracicR5CaptureProtocol.FrozenEvaluatorSha256)
                throw new InvalidOperationException("R5 scene or evaluator identity changed");
            string executionCommit = Environment.GetEnvironmentVariable("THORACIC_R5_EXECUTION_COMMIT") ?? string.Empty;
            if (string.IsNullOrEmpty(executionCommit) || executionCommit != manifest.sourceCommit)
                throw new InvalidOperationException("R5 execution/source commit mismatch");
            RequireR5TextHashes(manifest.sharedContractPath, manifest.sharedContractSha256,
                manifest.sharedContractMaterializedSha256, "shared contract");
            RequireR5TextHashes(manifest.scientificCriteriaPath, manifest.scientificCriteriaSha256,
                manifest.scientificCriteriaMaterializedSha256, "scientific criteria");
            RequireR5TextHashes(manifest.independentAuditPath, manifest.independentAuditSha256,
                manifest.independentAuditMaterializedSha256, "independent audit protocol");
            ResolveProjectPath(manifest.outputDirectory);
        }

        private void RequireR5TextHashes(
            string path, string expectedCanonical, string expectedMaterialized, string label)
        {
            string canonical = HashR5CanonicalTextFile(path);
            string materialized = HashR5ProjectFile(path);
            if (!canonical.Equals(expectedCanonical, StringComparison.OrdinalIgnoreCase)
                || !materialized.Equals(expectedMaterialized, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("R5 " + label + " hash mismatch: canonical expected/actual "
                    + expectedCanonical + "/" + canonical + ", materialized expected/actual "
                    + expectedMaterialized + "/" + materialized);
        }

        private string HashR5ProjectFile(string relativePath)
        {
            return Sha256Bytes(File.ReadAllBytes(ResolveProjectPath(relativePath)));
        }

        private string HashR5CanonicalTextFile(string relativePath)
        {
            string text = File.ReadAllText(ResolveProjectPath(relativePath))
                .Replace("\r\n", "\n").Replace("\r", "\n");
            return Sha256Bytes(new System.Text.UTF8Encoding(false).GetBytes(text));
        }

        private static string R5ProposalId(R5StartProposalAudit proposal)
        {
            return proposal.routeId + "_proposal_"
                + proposal.attemptWithinRoute.ToString("D2", CultureInfo.InvariantCulture);
        }

        private void SaveR5Report(string root)
        {
            if (r5Report == null || string.IsNullOrEmpty(root) || !Directory.Exists(root)) return;
            r5Report.generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            File.WriteAllText(Path.Combine(root, "r5_unity_report.json"), JsonUtility.ToJson(r5Report, true));
        }

        private void SealR5Snapshot()
        {
            CopyR5GeneratorSources();
            r5Report.immutableSnapshotSealed = true;
            SaveR5Report(r5PartialRoot);
            R5EvidenceIndex index = new R5EvidenceIndex
            {
                protocolId = r5Manifest.protocolId,
                generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                sourceCommit = r5Manifest.sourceCommit,
                sharedContractSha256 = r5Manifest.sharedContractSha256,
                scientificCriteriaSha256 = r5Manifest.scientificCriteriaSha256
            };
            foreach (string path in Directory.EnumerateFiles(r5PartialRoot, "*", SearchOption.AllDirectories)
                .Where(path => !Path.GetFileName(path).Equals("evidence_index.json", StringComparison.OrdinalIgnoreCase)
                    && !Path.GetFileName(path).Equals("r5_evidence.json", StringComparison.OrdinalIgnoreCase)
                    && !Path.GetFileName(path).Equals("snapshot_seal.json", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.Ordinal))
            {
                FileInfo info = new FileInfo(path);
                index.files.Add(new R5FileIntegrityRecord
                {
                    path = path.Substring(r5PartialRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                        .Replace('\\', '/'),
                    sha256 = Sha256Bytes(File.ReadAllBytes(path)),
                    bytes = info.Length
                });
            }
            string indexPath = Path.Combine(r5PartialRoot, "evidence_index.json");
            File.WriteAllText(indexPath, JsonUtility.ToJson(index, true));
            string indexHash = Sha256Bytes(File.ReadAllBytes(indexPath));
            R5EvidenceSnapshot evidence = BuildR5EvidenceSnapshot(indexHash);
            string evidencePath = Path.Combine(r5PartialRoot, "r5_evidence.json");
            File.WriteAllText(evidencePath, JsonUtility.ToJson(evidence, true));
            string reportPath = Path.Combine(r5PartialRoot, "r5_unity_report.json");
            R5SnapshotSeal seal = new R5SnapshotSeal
            {
                protocolId = r5Manifest.protocolId,
                generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                sourceCommit = r5Manifest.sourceCommit,
                evidenceIndexSha256 = indexHash,
                reportSha256 = Sha256Bytes(File.ReadAllBytes(reportPath)),
                evidenceManifestSha256 = Sha256Bytes(File.ReadAllBytes(evidencePath)),
                sharedContractSha256 = r5Manifest.sharedContractSha256,
                scientificCriteriaSha256 = r5Manifest.scientificCriteriaSha256,
                independentAuditSha256 = r5Manifest.independentAuditSha256,
                indexedFiles = index.files.Count
            };
            string sealPath = Path.Combine(r5PartialRoot, "snapshot_seal.json");
            File.WriteAllText(sealPath, JsonUtility.ToJson(seal, true));
            Directory.Move(r5PartialRoot, r5FinalRoot);
        }

        private R5EvidenceSnapshot BuildR5EvidenceSnapshot(string filesSha256)
        {
            R5EvidenceSnapshot snapshot = new R5EvidenceSnapshot
            {
                protocolId = r5Manifest.protocolId,
                protocolSha256 = r5Manifest.scientificCriteriaSha256,
                snapshotId = r5Manifest.datasetRunId,
                snapshotRole = r5Manifest.mode == ThoracicR5CaptureProtocol.ScoutMode
                    ? "r5_a_scout" : "r5_b_development",
                createdUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                source = new R5EvidenceSource
                {
                    baseCommit = r5Manifest.baseCommit,
                    captureCommit = r5Manifest.sourceCommit,
                    captureBranch = r5Manifest.captureBranch,
                    generatorFiles = new List<R5EvidenceGeneratorFile>
                    {
                        R5GeneratorFile("ThoracicR5CaptureTypes.cs"),
                        R5GeneratorFile("ThoracicUSCoachController.R5Capture.cs"),
                        R5GeneratorFile("ThoracicR5CaptureBatch.cs")
                    }
                },
                scene = new R5EvidenceScene
                {
                    path = r5Manifest.scene,
                    sha256CanonicalLf = r5Report.sceneSha256,
                    sha256Materialized = HashR5ProjectFile(r5Manifest.scene),
                    unityVersion = Application.unityVersion,
                    evaluatorSource = "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.cs",
                    evaluatorSha256CanonicalLf = r5Report.evaluatorSha256,
                    evaluatorSha256Materialized = HashR5ProjectFile(
                        "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.cs"),
                    acceptableScoreFloor = r5Manifest.acceptableScoreFloor
                },
                proposal = new R5EvidenceProposalSummary
                {
                    seed = r5Manifest.proposalSeed,
                    generatorVersion = r5Report.captureControllerSha256,
                    attemptedStarts = r5Report.attemptedStartProposals,
                    acceptedStarts = r5Report.acceptedStarts,
                    rejectedStarts = r5Report.attemptedStartProposals - r5Report.acceptedStarts
                },
                integrity = new R5EvidenceIntegrity
                {
                    manifestSha256 = r5Report.manifestSha256,
                    filesSha256 = filesSha256,
                    complete = true,
                    immutable = true
                }
            };
            foreach (R5StartProposalAudit proposal in r5Report.allStartProposals)
            {
                snapshot.startAttempts.Add(new R5EvidenceStartAttempt
                {
                    proposalId = R5ProposalId(proposal),
                    landmarkStratum = "T7_REGION",
                    landmarkObjects = new List<string>
                    {
                        "T6RightMarker", "T7RightSeed", "T8RightMarker", "SkinCollider"
                    },
                    generatedPosition = proposal.resultingProbePosition,
                    generatedRotation = proposal.resultingProbeRotation,
                    skinHitPoint = proposal.surfacePoint,
                    skinNormal = proposal.surfaceNormal,
                    accepted = proposal.acceptedAsStart,
                    rejectionReason = proposal.rejectionReason ?? string.Empty,
                    t7Valid = proposal.t7LevelAcceptable,
                    contactValid = proposal.contactValid,
                    workspaceValid = proposal.workspaceValid,
                    copiedPriorBaseTransform = false
                });
            }
            foreach (R5RouteResult route in r5Report.routes)
            {
                foreach (R5ObservationAudit observation in route.observations)
                {
                    R5ActionAudit action = observation.sourceObservationIndex > 0
                        && observation.sourceObservationIndex - 1 < route.actions.Count
                        ? route.actions[observation.sourceObservationIndex - 1] : null;
                    bool reset = observation.sourceObservationIndex == 0 && observation.repeatIndex == 0;
                    bool repeat = observation.repeatIndex > 0;
                    string recordId = route.routeId + "_obs_"
                        + observation.index.ToString("D3", CultureInfo.InvariantCulture);
                    R5EvidenceRecord record = new R5EvidenceRecord
                    {
                        recordId = recordId,
                        routeId = route.routeId,
                        startProposalId = route.acceptedStartProposalId,
                        observationIndex = observation.index,
                        sourceAncestry = new R5EvidenceSourceAncestry
                        {
                            origin = reset ? "marker_skin_derived" : "safe_action_descendant",
                            landmarkObjects = new List<string>
                            {
                                "T6RightMarker", "T7RightSeed", "T8RightMarker", "SkinCollider"
                            },
                            copiedPriorBaseTransform = false
                        },
                        plannedAction = reset ? "RESET" : repeat ? "OBSERVE" : action != null ? action.action : observation.incomingAction,
                        executedAction = reset ? "RESET" : repeat ? "OBSERVE"
                            : action != null ? (action.accepted ? action.action : "NONE") : observation.incomingAction,
                        actionAccepted = observation.actionAccepted,
                        rejectionReason = action != null ? action.rejectionReason : string.Empty,
                        poseTrace = new R5EvidencePoseTrace
                        {
                            beforePosition = action != null && !repeat ? action.beforePosition : observation.probePosition,
                            beforeRotation = action != null && !repeat ? action.beforeRotation : observation.probeRotation,
                            afterPosition = observation.probePosition,
                            afterRotation = observation.probeRotation
                        },
                        t7Valid = observation.t7LevelAcceptable,
                        contactValid = observation.contactValid,
                        workspaceValid = observation.workspaceValid,
                        image = new R5EvidenceImage
                        {
                            relativePath = R5SnapshotRelativeImagePath(observation.image),
                            sha256 = observation.imageSha256,
                            cleanUltrasoundOnly = true
                        },
                        offlineAnnotation = new R5EvidenceOfflineAnnotation
                        {
                            targetScore = observation.score.total,
                            acceptable = observation.finalTargetAcceptable,
                            scoreComponents = observation.score,
                            availableToActor = false
                        }
                    };
                    snapshot.records.Add(record);
                }
            }
            snapshot.integrity.recordCount = snapshot.records.Count;
            snapshot.integrity.imageCount = snapshot.records.Count;
            if (snapshot.records.Count == 0)
                throw new InvalidOperationException("Cannot seal a schema-valid R5 snapshot without observations");
            snapshot.developmentRoute = new R5EvidenceDevelopmentRoute
            {
                strategy = "Bounded marker/skin-derived T7 strata with safe longitudinal, lateral and yaw action descendants",
                expectedAdditionalIndependentPositiveComponents = 1,
                expectedAdditionalIndependentNegativeComponents = 1,
                supportingRecordIds = snapshot.records.Take(Mathf.Min(8, snapshot.records.Count))
                    .Select(item => item.recordId).ToList(),
                whyScoutSupportsIt = "If the cited physically executed marker/skin-derived records pass independent novelty checks, unused bounded proposal seeds and safe action descendants provide a predeclared route to add support to both positive and negative components; the auditor still decides whether that route is justified."
            };
            return snapshot;
        }

        private void CopyR5GeneratorSources()
        {
            string directory = Path.Combine(r5PartialRoot, "generator_sources");
            Directory.CreateDirectory(directory);
            Dictionary<string, string> sources = new Dictionary<string, string>
            {
                { "ThoracicR5CaptureTypes.cs", "Assets/ThoracicUSCoach/Runtime/ThoracicR5CaptureTypes.cs" },
                { "ThoracicUSCoachController.R5Capture.cs", "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.R5Capture.cs" },
                { "ThoracicR5CaptureBatch.cs", "Assets/ThoracicUSCoach/Editor/ThoracicR5CaptureBatch.cs" }
            };
            foreach (KeyValuePair<string, string> source in sources)
                File.Copy(ResolveProjectPath(source.Value), Path.Combine(directory, source.Key), false);
        }

        private R5EvidenceGeneratorFile R5GeneratorFile(string filename)
        {
            string relative = "generator_sources/" + filename;
            string absolute = Path.Combine(r5PartialRoot, "generator_sources", filename);
            return new R5EvidenceGeneratorFile
            {
                path = relative,
                sha256 = Sha256Bytes(File.ReadAllBytes(absolute))
            };
        }

        private string R5SnapshotRelativeImagePath(string projectRelativePath)
        {
            string marker = r5Manifest.outputDirectory.Replace('\\', '/').TrimEnd('/') + "/";
            string normalized = projectRelativePath.Replace('\\', '/');
            int index = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            return index >= 0 ? normalized.Substring(index + marker.Length) : normalized;
        }
    }
}
