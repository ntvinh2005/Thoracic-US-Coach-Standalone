using System;
using System.Collections;
using System.Linq;
using SMMARTS;
using UnityEngine;

namespace CSSALT.ThoracicUSCoach
{
    public sealed partial class ThoracicUSCoachController
    {
        private const int DirectFreshMaximumFrameWait = 8;
        private const int DirectFreshMaximumPublicationAttempts = 3;
        private const float DirectFreshMaximumPoseErrorMm = 0.25f;
        private const float DirectFreshMaximumPoseErrorDeg = 0.25f;
        private long directFreshPoseEpoch;

        private sealed class DirectFreshPoseToken
        {
            public long poseEpoch;
            public int poseAppliedUnityFrame;
            public double poseAppliedRealtime;
            public Vector3 position;
            public Quaternion rotation;
            public long minimumProducerScanSerial;
            public int lastAcceptedCaptureFrame = -1;
            public string posePath;
        }

        private sealed class DirectFreshCaptureResult
        {
            public Texture2D image;
            public byte[] pngBytes;
            public string purpose;
            public long poseEpoch;
            public int poseAppliedUnityFrame;
            public double poseAppliedRealtime;
            public long producerPoseEpoch;
            public int producerUnityFrame;
            public long producerScanSerial;
            public int producerSamplesPublished;
            public int captureUnityFrame;
            public double captureRealtime;
            public string capturePath;
            public string capturePathMode;
            public int activeAggregatorCount;
            public int waitedFrames;
            public int publicationAttempts;
            public Vector3 capturedPosition;
            public Quaternion capturedRotation;
        }

        private DirectFreshPoseToken BeginDirectFreshCapturePose(
            string posePath, bool physicalPoseCompleted, long specifiedPoseEpoch = -1)
        {
            if (!physicalPoseCompleted)
                throw new InvalidOperationException(
                    "Fresh capture requires a completed physical pose or factual restored pose");
            if (probeRoot == null)
                throw new InvalidOperationException("Fresh capture probe root missing");

            Physics.SyncTransforms();
            UltrasoundManager[] managers = FindObjectsByType<UltrasoundManager>(
                FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            long minimumSerial = managers.Length == 0 ? -1 : managers.Max(item => item.PublishedScanSerial);
            long epoch = specifiedPoseEpoch >= 0 ? specifiedPoseEpoch : ++directFreshPoseEpoch;
            return new DirectFreshPoseToken
            {
                poseEpoch = epoch,
                poseAppliedUnityFrame = Time.frameCount,
                poseAppliedRealtime = Time.realtimeSinceStartupAsDouble,
                position = probeRoot.position,
                rotation = probeRoot.rotation,
                minimumProducerScanSerial = minimumSerial,
                posePath = posePath ?? string.Empty
            };
        }

        private DirectFreshCaptureResult CaptureDirectProducerImmediate(
            DirectFreshPoseToken token, string purpose)
        {
            ValidateDirectFreshPose(token);
            Texture2D image = CaptureCleanImage();
            if (image == null) throw new InvalidOperationException("Direct clean ultrasound capture returned null");
            return new DirectFreshCaptureResult
            {
                image = image,
                pngBytes = image.EncodeToPNG(),
                purpose = purpose,
                poseEpoch = token.poseEpoch,
                poseAppliedUnityFrame = token.poseAppliedUnityFrame,
                poseAppliedRealtime = token.poseAppliedRealtime,
                captureUnityFrame = Time.frameCount,
                captureRealtime = Time.realtimeSinceStartupAsDouble,
                capturePath = "direct_producer_texture",
                capturePathMode = "DIRECT_PRODUCER_CAMERA",
                activeAggregatorCount = RequireNoActiveUltrasoundAggregator(),
                capturedPosition = probeRoot.position,
                capturedRotation = probeRoot.rotation
            };
        }

        private IEnumerator CaptureDirectProducerFreshFrame(
            DirectFreshPoseToken token,
            string purpose,
            DirectFreshCaptureResult result,
            bool capturePrimaryProvenance = false)
        {
            if (token == null || result == null)
                throw new ArgumentNullException(token == null ? nameof(token) : nameof(result));
            ValidateDirectFreshPose(token);

            int minimumFrameExclusive = Mathf.Max(
                token.poseAppliedUnityFrame, token.lastAcceptedCaptureFrame);
            int waitedFrames = 0;
            while (Time.frameCount <= minimumFrameExclusive)
            {
                if (waitedFrames >= DirectFreshMaximumFrameWait)
                    throw new TimeoutException(
                        "Direct ultrasound freshness timed out waiting for a later Unity frame");
                waitedFrames++;
                yield return null;
            }

            UltrasoundManager producer = null;
            int publicationAttempts = 0;
            while (producer == null && publicationAttempts < DirectFreshMaximumPublicationAttempts)
            {
                publicationAttempts++;
                Physics.SyncTransforms();
                ValidateDirectFreshPose(token);
                RequireNoActiveUltrasoundAggregator();
                UltrasoundManager[] managers = FindObjectsByType<UltrasoundManager>(
                    FindObjectsInactive.Exclude, FindObjectsSortMode.None);
                if (managers.Length == 0)
                    throw new InvalidOperationException("No active ultrasound producer exists");
                if (capturePrimaryProvenance)
                {
                    if (!UltrasoundManager.TryGet(UltrasoundManager.Plane.Primary,
                            out UltrasoundManager primary)
                        || primary == null || !primary.isActiveAndEnabled)
                        throw new InvalidOperationException(
                            "Primary ultrasound provenance producer is unavailable");
                    primary.ArmNextPublishedScanProvenance();
                }
                UltrasoundManager[] published = managers
                    .Where(item => item.PublishCurrentPose(token.poseEpoch))
                    .ToArray();
                if (published.Length > 1)
                    throw new InvalidOperationException(
                        "Direct freshness requires exactly one publishing ultrasound producer; found "
                        + published.Length);
                if (published.Length == 1) producer = published[0];
                else if (publicationAttempts < DirectFreshMaximumPublicationAttempts)
                {
                    waitedFrames++;
                    if (waitedFrames >= DirectFreshMaximumFrameWait)
                        throw new TimeoutException(
                            "Direct ultrasound freshness timed out waiting for a producer scan");
                    yield return null;
                }
            }
            if (producer == null)
                throw new TimeoutException("Direct ultrasound producer did not publish within bounded attempts");
            if (producer.PublishedPoseEpoch != token.poseEpoch
                || producer.PublishedUnityFrame <= token.poseAppliedUnityFrame
                || producer.PublishedUnityFrame != Time.frameCount
                || producer.PublishedScanSerial <= token.minimumProducerScanSerial)
                throw new InvalidOperationException("Direct ultrasound producer pose/frame/serial handshake failed");

            Texture2D image = CaptureCleanImage();
            if (image == null) throw new InvalidOperationException("Direct clean ultrasound capture returned null");
            ValidateDirectFreshPose(token);
            result.image = image;
            result.pngBytes = image.EncodeToPNG();
            result.purpose = purpose;
            result.poseEpoch = token.poseEpoch;
            result.poseAppliedUnityFrame = token.poseAppliedUnityFrame;
            result.poseAppliedRealtime = token.poseAppliedRealtime;
            result.producerPoseEpoch = producer.PublishedPoseEpoch;
            result.producerUnityFrame = producer.PublishedUnityFrame;
            result.producerScanSerial = producer.PublishedScanSerial;
            result.producerSamplesPublished = 1;
            result.captureUnityFrame = Time.frameCount;
            result.captureRealtime = Time.realtimeSinceStartupAsDouble;
            result.capturePath = "direct_producer_texture";
            result.capturePathMode = "DIRECT_PRODUCER_CAMERA";
            result.activeAggregatorCount = 0;
            result.waitedFrames = waitedFrames;
            result.publicationAttempts = publicationAttempts;
            result.capturedPosition = probeRoot.position;
            result.capturedRotation = probeRoot.rotation;
            token.minimumProducerScanSerial = producer.PublishedScanSerial;
            token.lastAcceptedCaptureFrame = result.captureUnityFrame;
        }

        private void ValidateDirectFreshPose(DirectFreshPoseToken token)
        {
            if (token == null) throw new ArgumentNullException(nameof(token));
            if (Vector3.Distance(probeRoot.position, token.position) > DirectFreshMaximumPoseErrorMm
                || Quaternion.Angle(probeRoot.rotation, token.rotation) > DirectFreshMaximumPoseErrorDeg)
                throw new InvalidOperationException(
                    "Probe pose changed while waiting for direct ultrasound publication: " + token.posePath);
        }

        private static int RequireNoActiveUltrasoundAggregator()
        {
            int count = FindObjectsByType<USImageAggregator>(
                    FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .Count(item => item != null && item.isActiveAndEnabled);
            if (count != 0)
                throw new InvalidOperationException(
                    "Direct producer capture requires zero active aggregators; found " + count);
            return count;
        }

        private static string DirectFreshPurpose(
            string sourceKind, string action, bool accepted, bool completed,
            bool rollbackAttempted, int acceptedSlot)
        {
            if (acceptedSlot > 0) return "confirmation";
            if (string.Equals(action, "OBSERVE", StringComparison.OrdinalIgnoreCase)) return "observe";
            if (!accepted || rollbackAttempted) return "rejected_rollback";
            if (sourceKind != null && sourceKind.IndexOf("control", StringComparison.OrdinalIgnoreCase) >= 0)
                return "direct_pose_restoration";
            if (sourceKind != null && (sourceKind.IndexOf("candidate", StringComparison.OrdinalIgnoreCase) >= 0
                || sourceKind.IndexOf("marker", StringComparison.OrdinalIgnoreCase) >= 0))
                return "marker_skin_placement";
            if (completed) return "completed_movement";
            return "live_diagnostic";
        }
    }
}
