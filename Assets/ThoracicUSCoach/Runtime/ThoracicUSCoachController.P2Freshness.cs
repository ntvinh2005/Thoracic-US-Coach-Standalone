using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SMMARTS;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CSSALT.ThoracicUSCoach
{
    public sealed partial class ThoracicUSCoachController
    {
        private P2FreshnessManifest p2FreshnessManifest;
        private P2FreshnessReport p2FreshnessReport;
        private string p2FreshnessFinalRoot;
        private string p2FreshnessPartialRoot;
        private bool p2FreshnessComplete;
        private long p2PoseEpoch;
        private int p2AcquisitionOrder;

        public bool P2FreshnessComplete => p2FreshnessComplete;
        public string P2FreshnessFailure => p2FreshnessReport != null
            ? p2FreshnessReport.infrastructureFailure : string.Empty;

        public bool StartP2Freshness(string manifestProjectRelativePath)
        {
            if (!Application.isPlaying || active2DRoutine != null || active2DCaptureRoutine != null) return false;
            try
            {
                AutoWire();
                string path = ResolveProjectPath(manifestProjectRelativePath);
                byte[] manifestBytes = File.ReadAllBytes(path);
                p2FreshnessManifest = JsonUtility.FromJson<P2FreshnessManifest>(
                    System.Text.Encoding.UTF8.GetString(manifestBytes));
                ThoracicP2FreshnessProtocol.Validate(p2FreshnessManifest);
                ValidateP2FreshnessIdentity(path, manifestBytes);
                p2FreshnessFinalRoot = ResolveProjectPath(p2FreshnessManifest.outputDirectory);
                p2FreshnessPartialRoot = p2FreshnessFinalRoot + ".partial";
                if (Directory.Exists(p2FreshnessFinalRoot) || Directory.Exists(p2FreshnessPartialRoot))
                    throw new InvalidOperationException("P2 freshness refuses to overwrite output");
                Directory.CreateDirectory(p2FreshnessPartialRoot);
                Directory.CreateDirectory(Path.Combine(p2FreshnessPartialRoot, "images"));
                Directory.CreateDirectory(Path.Combine(p2FreshnessPartialRoot, "discarded"));
                File.WriteAllBytes(Path.Combine(p2FreshnessPartialRoot, "capture_input_manifest.json"), manifestBytes);
                p2FreshnessReport = new P2FreshnessReport
                {
                    protocolId = p2FreshnessManifest.protocolId,
                    mode = p2FreshnessManifest.mode,
                    sourceCommit = p2FreshnessManifest.sourceCommit,
                    executionCommit = Environment.GetEnvironmentVariable("THORACIC_P2_EXECUTION_COMMIT") ?? string.Empty,
                    manifestSha256 = Sha256Bytes(manifestBytes),
                    sceneCanonicalSha256 = HashR6CanonicalText(p2FreshnessManifest.scene),
                    evaluatorCanonicalSha256 = HashR6CanonicalText(
                        "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.cs")
                };
                p2PoseEpoch = 0;
                p2AcquisitionOrder = 0;
                p2FreshnessComplete = false;
                active2DRoutine = StartCoroutine(RunP2Freshness());
                return true;
            }
            catch (Exception exception)
            {
                p2FreshnessReport = p2FreshnessReport ?? new P2FreshnessReport();
                p2FreshnessReport.infrastructureFailure = exception.ToString();
                p2FreshnessComplete = true;
                Debug.LogError("[ThoracicUSCoach][P2Freshness] " + exception);
                return false;
            }
        }

        private IEnumerator RunP2Freshness()
        {
            Stack<IEnumerator> work = new Stack<IEnumerator>();
            work.Push(RunP2FreshnessCore());
            try
            {
                while (work.Count > 0)
                {
                    object current;
                    try
                    {
                        IEnumerator active = work.Peek();
                        if (!active.MoveNext())
                        {
                            work.Pop();
                            continue;
                        }
                        current = active.Current;
                    }
                    catch (Exception exception)
                    {
                        p2FreshnessReport.infrastructureFailure = exception.ToString();
                        break;
                    }
                    if (current is IEnumerator nested)
                    {
                        work.Push(nested);
                        continue;
                    }
                    yield return current;
                }
                p2FreshnessReport.complete = string.IsNullOrEmpty(p2FreshnessReport.infrastructureFailure);
            }
            finally
            {
                try
                {
                    if (p2FreshnessReport.complete) SealP2FreshnessSnapshot();
                    else SaveP2FreshnessReport();
                }
                catch (Exception exception)
                {
                    p2FreshnessReport.complete = false;
                    p2FreshnessReport.immutableSnapshotSealed = false;
                    p2FreshnessReport.infrastructureFailure = exception.ToString();
                    SaveP2FreshnessReport();
                }
                p2FreshnessComplete = true;
                active2DRoutine = null;
            }
        }

        private IEnumerator RunP2FreshnessCore()
        {
                Dictionary<string, R6PositiveControl> controls = p2FreshnessManifest.positiveControls
                    .ToDictionary(item => item.controlId, StringComparer.Ordinal);
                int transitionIndex = 0;
                foreach (string controlId in p2FreshnessManifest.freshnessControlSequence)
                {
                    R6PositiveControl control = controls[controlId];
                    string transitionId = "control_transition_" + transitionIndex.ToString("D2", CultureInfo.InvariantCulture);
                    R6AttemptRecord restore = RestoreR6Control(control, transitionId);
                    p2PoseEpoch++;
                    P2FreshnessTransitionAttempt attempt = BeginP2Attempt(
                        transitionId, "freshness_control", controlId, restore.accepted);
                    attempt.resultingPosition = restore.resultingPosition;
                    attempt.resultingRotation = restore.resultingRotation;
                    p2FreshnessReport.attempts.Add(attempt);
                    p2FreshnessReport.controlTransitionsAttempted++;
                    if (!restore.accepted)
                        throw new InvalidOperationException("P2 control restore failed: " + controlId);
                    yield return CaptureP2FreshSet(attempt, controlId, null);
                    transitionIndex++;
                }

                R6PositiveControl actionAnchor = controls[p2FreshnessManifest.freshnessControlSequence[0]];
                foreach (string actionName in p2FreshnessManifest.movementFreshnessActions)
                {
                    R6AttemptRecord restore = RestoreR6Control(actionAnchor, "movement_restore:" + actionName);
                    if (!restore.accepted) throw new InvalidOperationException("P2 movement anchor restore failed");
                    if (!Enum.TryParse(actionName, true, out Active2DAction action))
                        throw new InvalidOperationException("Unknown P2 action " + actionName);
                    R6ActionRecord execution = ExecuteR6TargetIndependentAction(action.ToString(), p2FreshnessReport.actions.Count);
                    p2FreshnessReport.actions.Add(execution);
                    p2PoseEpoch++;
                    string transitionId = "movement_" + actionName.ToLowerInvariant();
                    P2FreshnessTransitionAttempt attempt = BeginP2Attempt(
                        transitionId, "freshness_movement", actionName, true);
                    p2FreshnessReport.attempts.Add(attempt);
                    p2FreshnessReport.movementScenariosAttempted++;
                    yield return CaptureP2FreshSet(
                        attempt, actionName, P2ActionCategory(action), execution);
                }

                R6AttemptRecord rejectionRestore = RestoreR6Control(actionAnchor, "rejection_probe_restore");
                if (!rejectionRestore.accepted) throw new InvalidOperationException("P2 rejection anchor restore failed");
                bool observedRejected = false;
                bool observedIncomplete = false;
                bool observedRollback = false;
                for (int index = 0; index < p2FreshnessManifest.maximumRejectionProbeActions; index++)
                {
                    R6ActionRecord execution = ExecuteR6TargetIndependentAction(
                        p2FreshnessManifest.rejectionProbeAction, p2FreshnessReport.actions.Count);
                    p2FreshnessReport.actions.Add(execution);
                    p2PoseEpoch++;
                    string transitionId = "rejection_probe_" + index.ToString("D2", CultureInfo.InvariantCulture);
                    P2FreshnessTransitionAttempt attempt = BeginP2Attempt(
                        transitionId, "freshness_movement",
                        p2FreshnessManifest.rejectionProbeAction, true);
                    p2FreshnessReport.attempts.Add(attempt);
                    p2FreshnessReport.movementScenariosAttempted++;
                    if (!Enum.TryParse(execution.plannedAction, true, out Active2DAction executedAction))
                        throw new InvalidOperationException("Unknown executed P2 rejection-probe action");
                    yield return CaptureP2FreshSet(
                        attempt, transitionId, P2ActionCategory(executedAction), execution);
                    observedRejected |= !execution.accepted;
                    observedIncomplete |= execution.accepted && !execution.completed;
                    observedRollback |= execution.rollbackAttempted;
                    if (observedRejected && observedIncomplete && observedRollback) break;
                }
                if (!observedRejected)
                    throw new InvalidOperationException("P2 did not observe a rejected action within frozen budget");
                if (!observedIncomplete)
                    throw new InvalidOperationException("P2 did not observe an accepted-but-incomplete action within frozen budget");
                if (!observedRollback)
                    throw new InvalidOperationException("P2 did not observe a rollback within frozen budget");
                p2FreshnessReport.rejectedActions = p2FreshnessReport.actions.Count(item => !item.accepted);
                p2FreshnessReport.rollbackActions = p2FreshnessReport.actions.Count(item => item.rollbackAttempted);
                p2FreshnessReport.safetyFailures = p2FreshnessReport.actions.Count(item => item.safetyFailure);
        }

        private P2FreshnessTransitionAttempt BeginP2Attempt(
            string transitionId, string sourceKind, string sourceId, bool accepted)
        {
            Physics.SyncTransforms();
            return new P2FreshnessTransitionAttempt
            {
                transitionId = transitionId,
                sourceKind = sourceKind,
                sourceId = sourceId,
                poseEpoch = p2PoseEpoch,
                poseAppliedFrame = Time.frameCount,
                poseAppliedRealtime = Time.realtimeSinceStartupAsDouble,
                poseAccepted = accepted,
                resultingPosition = probeRoot.position,
                resultingRotation = probeRoot.rotation
            };
        }

        private IEnumerator CaptureP2FreshSet(
            P2FreshnessTransitionAttempt attempt, string sourceId, R6ActionRecord action)
        {
            string category = attempt.sourceKind == "freshness_control"
                ? string.Empty : action != null && Enum.TryParse(action.plannedAction, true, out Active2DAction parsed)
                    ? P2ActionCategory(parsed) : string.Empty;
            yield return CaptureP2FreshSet(attempt, sourceId, category, action);
        }

        private IEnumerator CaptureP2FreshSet(
            P2FreshnessTransitionAttempt attempt, string sourceId, string actionCategory, R6ActionRecord action)
        {
            R6PositiveControl control = attempt.sourceKind == "freshness_control"
                ? p2FreshnessManifest.positiveControls.First(item => item.controlId == sourceId) : null;
            Vector3 requestedPosition = control != null ? control.probePosition : probeRoot.position;
            Quaternion requestedRotation = control != null ? control.probeRotation : probeRoot.rotation;
            byte[] immediateBytes = CaptureP2Bytes();
            string immediateRelative = SaveP2Discard(
                attempt.transitionId + "_immediate", immediateBytes);
            RecordP2Discard(attempt, sourceId, actionCategory, action, immediateRelative,
                immediateBytes, requestedPosition, requestedRotation, "immediate_post_pose");
            p2FreshnessReport.discardedWarmups++;

            // The scene's temporal aggregator is intentionally inactive. Cross
            // one exact player-loop boundary and explicitly rebuild the direct
            // producer texture once for this completed physical pose.
            yield return null;
            Physics.SyncTransforms();
            UltrasoundManager[] managers = FindObjectsByType<UltrasoundManager>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            P2Publication publication = PublishP2CurrentPoseSamples(attempt.poseEpoch, managers);
            UltrasoundManager producer = publication.producer;
            ValidateP2ProducerHandshake(attempt, producer);

            byte[] publishedWarmupBytes = CaptureP2Bytes();
            string publishedWarmupRelative = SaveP2Discard(
                attempt.transitionId + "_published_warmup", publishedWarmupBytes);
            RecordP2Discard(attempt, sourceId, actionCategory, action, publishedWarmupRelative,
                publishedWarmupBytes, requestedPosition, requestedRotation, "published_warmup");
            p2FreshnessReport.discardedWarmups++;
            attempt.producerPublished = true;

            int lastAcceptedCaptureFrame = -1;
            for (int slot = 0; slot < p2FreshnessManifest.acceptedFreshCapturesPerTransition; slot++)
            {
                // Every accepted observation has its own later player-loop frame
                // and one explicit direct-producer current-pose publication.
                yield return null;
                Physics.SyncTransforms();
                publication = PublishP2CurrentPoseSamples(attempt.poseEpoch, managers);
                producer = publication.producer;
                ValidateP2ProducerHandshake(attempt, producer);
                if (Time.frameCount <= lastAcceptedCaptureFrame)
                    throw new InvalidOperationException("P2 accepted captures must use distinct later Unity frames");
                byte[] bytes = CaptureP2Bytes();
                string relative = "images/" + attempt.transitionId + "_accepted_"
                    + slot.ToString("D2", CultureInfo.InvariantCulture) + ".png";
                File.WriteAllBytes(Path.Combine(p2FreshnessPartialRoot, relative.Replace('/', Path.DirectorySeparatorChar)), bytes);
                Texture2D scoringImage = new Texture2D(2, 2, TextureFormat.RGBA32, false, true);
                if (!scoringImage.LoadImage(bytes, false))
                    throw new InvalidOperationException("P2 accepted PNG decode failed");
                ScoreBreakdown score = ScoreCandidate(scoringImage, SampleAnatomyLabels(labelGridSize));
                Destroy(scoringImage);
                Active2DPrivilegedFrame label = BuildActive2DPrivilegedFrame(p2AcquisitionOrder, null);
                P2FreshnessCaptureRecord record = new P2FreshnessCaptureRecord
                {
                    recordId = "p2_fresh_" + p2AcquisitionOrder.ToString("D4", CultureInfo.InvariantCulture),
                    transitionId = attempt.transitionId,
                    visitId = attempt.transitionId,
                    sourceKind = attempt.sourceKind,
                    sourceId = sourceId,
                    controlId = control != null ? control.controlId : string.Empty,
                    actionCategory = actionCategory,
                    captureDisposition = "accepted",
                    poseEpoch = attempt.poseEpoch,
                    poseAppliedFrame = attempt.poseAppliedFrame,
                    poseAppliedUnityFrame = attempt.poseAppliedFrame,
                    poseAppliedRealtime = attempt.poseAppliedRealtime,
                    producerPoseEpoch = producer.PublishedPoseEpoch,
                    producerUnityFrame = producer.PublishedUnityFrame,
                    producerScanSerial = producer.PublishedScanSerial,
                    producerSamplesPublished = publication.producerSamplesPublished,
                    capturePath = publication.capturePath,
                    capturePathMode = publication.capturePathMode,
                    activeAggregatorCount = publication.activeAggregatorCount,
                    aggregatorCount = publication.activeAggregatorCount,
                    discardedWarmupCount = 2,
                    immediateDiscardImage = immediateRelative,
                    immediateDiscardSha256 = Sha256Bytes(immediateBytes),
                    publishedWarmupImage = publishedWarmupRelative,
                    publishedWarmupSha256 = Sha256Bytes(publishedWarmupBytes),
                    acquisitionOrder = p2AcquisitionOrder,
                    acceptedSlot = slot,
                    captureUnityFrame = Time.frameCount,
                    unityFrameCount = Time.frameCount,
                    captureRealtime = Time.realtimeSinceStartupAsDouble,
                    image = relative,
                    imageSha256 = Sha256Bytes(bytes),
                    probePosition = label.probePosition,
                    probeRotation = label.probeRotation,
                    requestedProbePosition = requestedPosition,
                    requestedProbeRotation = requestedRotation,
                    beforeProbePosition = action != null ? action.beforePosition : attempt.resultingPosition,
                    beforeProbeRotation = action != null ? action.beforeRotation : attempt.resultingRotation,
                    actualResultingPoseRecorded = true,
                    contactValid = label.contactValid,
                    workspaceValid = label.workspaceValid,
                    t7Valid = label.t7LevelAcceptable,
                    score = score,
                    acceptable = score.total >= p2FreshnessManifest.acceptableScoreFloor,
                    finalTargetAcceptable = score.total >= p2FreshnessManifest.acceptableScoreFloor,
                    plannedAction = action != null ? action.plannedAction : "RESTORE",
                    executedAction = action != null ? action.executedAction : "RESTORE",
                    actionAccepted = action == null || action.accepted,
                    actionCompleted = action == null || action.completed,
                    rollbackAttempted = action != null && action.rollbackAttempted,
                    rollbackContactRestored = action != null && action.rollbackContactRestored,
                    safetyFailure = action != null && action.safetyFailure,
                    actionRejectionReason = action != null ? action.rejectionReason : string.Empty
                };
                if (record.producerPoseEpoch != record.poseEpoch
                    || record.producerUnityFrame < record.poseAppliedFrame + 1)
                    throw new InvalidOperationException("Accepted image failed producer pose epoch contract");
                p2FreshnessReport.records.Add(record);
                p2FreshnessReport.acceptedFreshCaptures++;
                p2AcquisitionOrder++;
                lastAcceptedCaptureFrame = record.captureUnityFrame;
            }
            p2FreshnessReport.poseEpochs++;
        }

        private sealed class P2Publication
        {
            public UltrasoundManager producer;
            public int producerSamplesPublished;
            public string capturePath;
            public string capturePathMode;
            public int activeAggregatorCount;
        }

        private P2Publication PublishP2CurrentPoseSamples(
            long poseEpoch, UltrasoundManager[] managers)
        {
            if (managers == null || managers.Length == 0)
                throw new InvalidOperationException("No active ultrasound producer exists");
            UltrasoundManager[] published = managers
                .Where(item => item.PublishCurrentPose(poseEpoch))
                .ToArray();
            if (published.Length != 1)
                throw new InvalidOperationException(
                    "P2 requires exactly one direct ultrasound producer; found " + published.Length);

            int activeAggregatorCount = P2RequireNoActiveAggregator();
            return new P2Publication
            {
                producer = published[0],
                producerSamplesPublished = 1,
                capturePath = "direct_producer_texture",
                capturePathMode = "DIRECT_PRODUCER_CAMERA",
                activeAggregatorCount = activeAggregatorCount
            };
        }

        private static int P2RequireNoActiveAggregator()
        {
            int count = FindObjectsByType<USImageAggregator>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .Count(item => item != null && item.isActiveAndEnabled);
            if (count != 0)
                throw new InvalidOperationException(
                    "P2 direct-producer path requires zero active aggregators; found " + count);
            return count;
        }

        private static void ValidateP2ProducerHandshake(
            P2FreshnessTransitionAttempt attempt, UltrasoundManager producer)
        {
            if (producer.PublishedPoseEpoch != attempt.poseEpoch
                || producer.PublishedUnityFrame <= attempt.poseAppliedFrame)
                throw new InvalidOperationException("Ultrasound producer pose/frame handshake failed");
        }

        private void RecordP2Discard(
            P2FreshnessTransitionAttempt attempt, string sourceId, string actionCategory,
            R6ActionRecord action, string relative, byte[] bytes,
            Vector3 requestedPosition, Quaternion requestedRotation, string reason)
        {
            P2FreshnessCaptureRecord record = new P2FreshnessCaptureRecord
            {
                recordId = "p2_discard_" + p2AcquisitionOrder.ToString("D4", CultureInfo.InvariantCulture),
                transitionId = attempt.transitionId,
                visitId = attempt.transitionId,
                sourceKind = attempt.sourceKind,
                sourceId = sourceId,
                controlId = attempt.sourceKind == "freshness_control" ? sourceId : string.Empty,
                actionCategory = actionCategory,
                captureDisposition = "discarded",
                capturePath = "direct_producer_texture",
                capturePathMode = "DIRECT_PRODUCER_CAMERA",
                activeAggregatorCount = P2RequireNoActiveAggregator(),
                poseEpoch = attempt.poseEpoch,
                poseAppliedFrame = attempt.poseAppliedFrame,
                poseAppliedUnityFrame = attempt.poseAppliedFrame,
                poseAppliedRealtime = attempt.poseAppliedRealtime,
                acquisitionOrder = p2AcquisitionOrder,
                captureUnityFrame = Time.frameCount,
                unityFrameCount = Time.frameCount,
                captureRealtime = Time.realtimeSinceStartupAsDouble,
                image = relative,
                imageSha256 = Sha256Bytes(bytes),
                probePosition = probeRoot.position,
                probeRotation = probeRoot.rotation,
                requestedProbePosition = requestedPosition,
                requestedProbeRotation = requestedRotation,
                beforeProbePosition = action != null ? action.beforePosition : attempt.resultingPosition,
                beforeProbeRotation = action != null ? action.beforeRotation : attempt.resultingRotation,
                actualResultingPoseRecorded = true,
                plannedAction = action != null ? action.plannedAction : "RESTORE",
                executedAction = action != null ? action.executedAction : "RESTORE",
                actionAccepted = action == null || action.accepted,
                actionCompleted = action == null || action.completed,
                rollbackAttempted = action != null && action.rollbackAttempted,
                rollbackContactRestored = action != null && action.rollbackContactRestored,
                safetyFailure = action != null && action.safetyFailure,
                actionRejectionReason = reason + (action != null && !string.IsNullOrEmpty(action.rejectionReason)
                    ? "; " + action.rejectionReason : string.Empty)
            };
            p2FreshnessReport.records.Add(record);
            p2AcquisitionOrder++;
        }

        private static string P2ActionCategory(Active2DAction action)
        {
            switch (action)
            {
                case Active2DAction.Lateral:
                case Active2DAction.Medial: return "lateral";
                case Active2DAction.Cranial:
                case Active2DAction.Caudal: return "longitudinal";
                case Active2DAction.RotateClockwise:
                case Active2DAction.RotateCounterClockwise: return "rotation";
                case Active2DAction.Observe: return "observe";
                default: return action.ToString().ToLowerInvariant();
            }
        }

        private byte[] CaptureP2Bytes()
        {
            Texture2D image = CaptureCleanImage();
            if (image == null) throw new InvalidOperationException("P2 clean image capture returned null");
            return image.EncodeToPNG();
        }

        private string SaveP2Discard(string id, byte[] bytes)
        {
            string relative = "discarded/" + id + ".png";
            File.WriteAllBytes(Path.Combine(p2FreshnessPartialRoot, relative.Replace('/', Path.DirectorySeparatorChar)), bytes);
            return relative;
        }

        private void ValidateP2FreshnessIdentity(string manifestPath, byte[] manifestBytes)
        {
            if (p2FreshnessManifest.scene != SceneManager.GetActiveScene().path
                || p2FreshnessManifest.unityVersion != Application.unityVersion)
                throw new InvalidOperationException("P2 scene/Unity mismatch");
            if (HashR6CanonicalText(p2FreshnessManifest.scene) != ThoracicP2FreshnessProtocol.FrozenSceneSha256
                || HashR6CanonicalText("Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.cs")
                    != ThoracicP2FreshnessProtocol.FrozenEvaluatorSha256)
                throw new InvalidOperationException("P2 frozen scene/evaluator identity mismatch");
            string execution = Environment.GetEnvironmentVariable("THORACIC_P2_EXECUTION_COMMIT") ?? string.Empty;
            if (execution != p2FreshnessManifest.sourceCommit)
                throw new InvalidOperationException("P2 execution commit mismatch");
            string expectedManifest = Environment.GetEnvironmentVariable("THORACIC_P2_MANIFEST_SHA256") ?? string.Empty;
            string actualManifest = Sha256Bytes(manifestBytes);
            if (string.IsNullOrEmpty(expectedManifest)
                || !expectedManifest.Equals(actualManifest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("P2 pre-frozen manifest hash mismatch");
            if (!HashR6CanonicalText(p2FreshnessManifest.captureProtocolPath).Equals(
                    p2FreshnessManifest.captureProtocolSha256, StringComparison.OrdinalIgnoreCase)
                || !HashR6CanonicalText(p2FreshnessManifest.auditProtocolPath).Equals(
                    p2FreshnessManifest.auditProtocolSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("P2 frozen protocol hash mismatch");
        }

        private void SaveP2FreshnessReport()
        {
            if (p2FreshnessReport == null || string.IsNullOrEmpty(p2FreshnessPartialRoot)
                || !Directory.Exists(p2FreshnessPartialRoot)) return;
            p2FreshnessReport.generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            File.WriteAllText(Path.Combine(p2FreshnessPartialRoot, "p2_freshness_report.json"),
                JsonUtility.ToJson(p2FreshnessReport, true));
        }

        private void SealP2FreshnessSnapshot()
        {
            p2FreshnessReport.immutableSnapshotSealed = true;
            SaveP2FreshnessReport();
            File.Copy(ResolveProjectPath(p2FreshnessManifest.captureProtocolPath),
                Path.Combine(p2FreshnessPartialRoot, Path.GetFileName(p2FreshnessManifest.captureProtocolPath)), false);
            File.Copy(ResolveProjectPath(p2FreshnessManifest.auditProtocolPath),
                Path.Combine(p2FreshnessPartialRoot, Path.GetFileName(p2FreshnessManifest.auditProtocolPath)), false);
            P2FreshnessEvidenceIndex index = new P2FreshnessEvidenceIndex
            {
                protocolId = p2FreshnessManifest.protocolId,
                generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                sourceCommit = p2FreshnessManifest.sourceCommit,
                captureProtocolSha256 = p2FreshnessManifest.captureProtocolSha256,
                auditProtocolSha256 = p2FreshnessManifest.auditProtocolSha256
            };
            foreach (string path in Directory.EnumerateFiles(p2FreshnessPartialRoot, "*", SearchOption.AllDirectories)
                .Where(path => !Path.GetFileName(path).Equals("evidence_index.json", StringComparison.OrdinalIgnoreCase)
                    && !Path.GetFileName(path).Equals("snapshot_seal.json", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.Ordinal))
            {
                FileInfo info = new FileInfo(path);
                index.files.Add(new R6FileIntegrityRecord
                {
                    path = path.Substring(p2FreshnessPartialRoot.Length)
                        .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/'),
                    sha256 = HashR6Raw(path),
                    bytes = info.Length
                });
            }
            string indexPath = Path.Combine(p2FreshnessPartialRoot, "evidence_index.json");
            File.WriteAllText(indexPath, JsonUtility.ToJson(index, true));
            string reportPath = Path.Combine(p2FreshnessPartialRoot, "p2_freshness_report.json");
            string manifestPath = Path.Combine(p2FreshnessPartialRoot, "capture_input_manifest.json");
            P2FreshnessSnapshotSeal seal = new P2FreshnessSnapshotSeal
            {
                protocolId = p2FreshnessManifest.protocolId,
                generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                sourceCommit = p2FreshnessManifest.sourceCommit,
                evidenceIndexSha256 = HashR6Raw(indexPath),
                reportSha256 = HashR6Raw(reportPath),
                manifestSha256 = HashR6Raw(manifestPath),
                captureProtocolCanonicalSha256 = p2FreshnessManifest.captureProtocolSha256,
                captureProtocolMaterializedSha256 = HashR6Raw(ResolveProjectPath(p2FreshnessManifest.captureProtocolPath)),
                auditProtocolCanonicalSha256 = p2FreshnessManifest.auditProtocolSha256,
                auditProtocolMaterializedSha256 = HashR6Raw(ResolveProjectPath(p2FreshnessManifest.auditProtocolPath)),
                sceneCanonicalSha256 = ThoracicP2FreshnessProtocol.FrozenSceneSha256,
                sceneMaterializedSha256 = HashR6Raw(ResolveProjectPath(p2FreshnessManifest.scene)),
                evaluatorCanonicalSha256 = ThoracicP2FreshnessProtocol.FrozenEvaluatorSha256,
                evaluatorMaterializedSha256 = HashR6Raw(ResolveProjectPath(
                    "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.cs")),
                indexedFiles = index.files.Count,
                recordCount = p2FreshnessReport.records.Count,
                acceptedImageCount = p2FreshnessReport.records.Count(item => item.captureDisposition == "accepted"),
                discardedImageCount = p2FreshnessReport.discardedWarmups,
                actionCount = p2FreshnessReport.actions.Count,
                complete = true,
                immutable = true
            };
            File.WriteAllText(Path.Combine(p2FreshnessPartialRoot, "snapshot_seal.json"),
                JsonUtility.ToJson(seal, true));
            Directory.Move(p2FreshnessPartialRoot, p2FreshnessFinalRoot);
        }
    }
}
