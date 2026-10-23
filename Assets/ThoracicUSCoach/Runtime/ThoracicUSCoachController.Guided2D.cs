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
        [Header("Guided / oracle-assisted 2D demo")]
        [Range(0.5f, 4f)] public float guided2DStepMm = 2f;
        [Range(0.5f, 5f)] public float guided2DT7ToleranceMm = 2.5f;
        [Range(0.5f, 5f)] public float guided2DLateralToleranceMm = 2.5f;
        [Range(0.5f, 5f)] public float guided2DFinalToleranceMm = 2.5f;
        [Range(0.15f, 4f)] public float guided2DPlaybackSpeed = 1f;

        [SerializeField] private Guided2DPhase guided2DPhase = Guided2DPhase.Idle;
        [SerializeField] private Guided2DStart guided2DStart = Guided2DStart.NearT6;
        [SerializeField] private int guided2DSeed = 20261022;
        [SerializeField] private bool guided2DPaused;
        [SerializeField] private bool guided2DShowGuidance = true;
        [SerializeField] private string guided2DDirection = "STOP";
        [SerializeField] private string guided2DFailureReason = string.Empty;

        private readonly List<Guided2DTracePoint> guided2DTrace = new List<Guided2DTracePoint>();
        private Coroutine guided2DRoutine;
        private Coroutine guided2DManualRoutine;
        private Guided2DRunResult guided2DResult;
        private CandidateRecord guided2DFinalCandidate;
        private Vector3 guided2DT7Contact;
        private Vector3 guided2DFinalContact;
        private Vector3 guided2DReferenceCaudal;
        private Vector3 guided2DReferenceLateral;
        private Vector3 guided2DLastContact;
        private string guided2DLastTransition = "none";
        private string guided2DLastBridgeRequestId;
        private string guided2DResultPath = string.Empty;
        private string guided2DSeedText = "20261022";
        private int guided2DTotalSteps;
        private int guided2DRejectedMovements;
        private int guided2DContactViolations;
        private int guided2DWorkspaceViolations;
        private float guided2DLongitudinalTravel;
        private float guided2DLateralTravel;
        private float guided2DDistanceToGoal;
        private float guided2DMaxStepDistance;
        private bool guided2DLongitudinalPass;
        private bool guided2DLateralPass;
        private bool guided2DFinePass;
        private bool guided2DFinalStop;
        private bool guided2DSingleStepRequested;
        private bool guided2DManualBusy;
        private float guided2DNextBridgePoll;
        private Vector2 guided2DScroll;
        private Camera guided2DOverviewCamera;
        private RenderTexture guided2DOverviewTexture;

        public Guided2DPhase GuidedPhase => guided2DPhase;
        public bool Guided2DRunning => guided2DRoutine != null;
        public Guided2DRunResult Guided2DResult => guided2DResult;

        private void InitializeGuided2D()
        {
            // The isolated PoC intentionally omits some unrelated cognitive-aid
            // prefab objects. Disable only the legacy UI manager when that
            // optional reference is missing; otherwise it throws every frame
            // and obscures the demo. The coach UI and ultrasound renderer are
            // independent of that manager.
            UserInterfaceManager legacyUi = FindFirstObjectByType<UserInterfaceManager>(FindObjectsInactive.Include);
            if (legacyUi != null && legacyUi.NeedleTrajectoryExtensionCognitiveAid == null)
                legacyUi.enabled = false;
            EnsureGuided2DOverviewCamera();
            guided2DSeedText = guided2DSeed.ToString(CultureInfo.InvariantCulture);
            string requestPath = Path.Combine(ArtifactRoot(), "bridge", "guided2d_request.json");
            if (!File.Exists(requestPath)) return;
            try
            {
                Guided2DBridgeRequest stale = JsonUtility.FromJson<Guided2DBridgeRequest>(File.ReadAllText(requestPath));
                if (stale != null) guided2DLastBridgeRequestId = stale.requestId;
            }
            catch (IOException) { }
            catch (ArgumentException) { }
        }

        private void UpdateGuided2D()
        {
            UpdateGuided2DOverviewCamera();
            PollGuided2DBridge();
            if (Input.GetKeyDown(KeyCode.F2)) StartGuided2DDemo();
            if (Input.GetKeyDown(KeyCode.F3)) ResetGuided2DDemo();
            if (Input.GetKeyDown(KeyCode.F4)) ToggleGuided2DPause();
        }

        public void StartGuided2DDemo()
        {
            if (!Application.isPlaying || guided2DRoutine != null || guided2DManualBusy) return;
            if (!int.TryParse(guided2DSeedText, NumberStyles.Integer, CultureInfo.InvariantCulture, out guided2DSeed))
                guided2DSeed = 20261022;
            guided2DSeedText = guided2DSeed.ToString(CultureInfo.InvariantCulture);
            guided2DRoutine = StartCoroutine(RunGuided2DDemo());
        }

        public void PauseGuided2DDemo()
        {
            if (guided2DRoutine != null) guided2DPaused = true;
        }

        public void ResumeGuided2DDemo()
        {
            if (guided2DRoutine != null) guided2DPaused = false;
        }

        public void ToggleGuided2DPause()
        {
            if (guided2DRoutine == null) return;
            guided2DPaused = !guided2DPaused;
        }

        public void SingleStepGuided2DDemo()
        {
            if (guided2DRoutine == null) return;
            guided2DPaused = true;
            guided2DSingleStepRequested = true;
        }

        public void ResetGuided2DDemo()
        {
            if (guided2DRoutine != null) StopCoroutine(guided2DRoutine);
            if (guided2DManualRoutine != null) StopCoroutine(guided2DManualRoutine);
            guided2DRoutine = null;
            guided2DManualRoutine = null;
            guided2DManualBusy = false;
            guided2DPaused = false;
            guided2DSingleStepRequested = false;
            guided2DTrace.Clear();
            guided2DResult = null;
            guided2DResultPath = string.Empty;
            guided2DTotalSteps = 0;
            guided2DRejectedMovements = 0;
            guided2DContactViolations = 0;
            guided2DWorkspaceViolations = 0;
            guided2DLongitudinalTravel = 0f;
            guided2DLateralTravel = 0f;
            guided2DMaxStepDistance = 0f;
            guided2DLongitudinalPass = false;
            guided2DLateralPass = false;
            guided2DFinePass = false;
            guided2DFinalStop = false;
            guided2DFailureReason = string.Empty;
            guided2DDirection = "STOP";
            guided2DLastTransition = "reset -> IDLE";
            guided2DPhase = Guided2DPhase.Idle;
            status = "2D demo reset; ready";
        }

        public void RunGuided2DAgain()
        {
            ResetGuided2DDemo();
            StartGuided2DDemo();
        }

        public void ManualGuided2DMove(string direction)
        {
            if (!Application.isPlaying || guided2DRoutine != null || guided2DManualBusy) return;
            guided2DManualRoutine = StartCoroutine(ManualGuided2DMoveRoutine(direction));
        }

        private IEnumerator ManualGuided2DMoveRoutine(string direction)
        {
            guided2DManualBusy = true;
            AutoWire();
            if (!TryPrepareGuidedGeometry(false, out string error))
            {
                guided2DFailureReason = error;
                guided2DManualBusy = false;
                guided2DManualRoutine = null;
                yield break;
            }
            Vector3 normal = CurrentSurfaceNormal();
            Vector3 caudal = DetermineCaudalTangent(normal);
            Vector3 lateral = Vector3.Cross(normal, caudal).normalized;
            if (Vector3.Dot(lateral, guided2DReferenceLateral) < 0f) lateral = -lateral;
            Vector3 axis;
            switch ((direction ?? string.Empty).ToLowerInvariant())
            {
                case "cranial": axis = -caudal; break;
                case "caudal": axis = caudal; break;
                case "medial": axis = -lateral; break;
                case "lateral": axis = lateral; break;
                default: axis = Vector3.zero; break;
            }
            if (axis.sqrMagnitude > 0.5f)
            {
                guided2DDirection = direction.ToUpperInvariant();
                TryGuided2DMove(axis, guided2DStepMm, "manual");
                yield return null;
                CaptureCleanImage();
            }
            guided2DManualBusy = false;
            guided2DManualRoutine = null;
        }

        private IEnumerator RunGuided2DDemo()
        {
            ResetGuidedRunCounters();
            SetGuided2DPhase(Guided2DPhase.Initializing, "run started");
            status = "GUIDED / ORACLE-ASSISTED 2D NAVIGATION — initializing";
            AutoWire();
            if (!TryPrepareGuidedGeometry(true, out string error))
            {
                FailGuided2D(error);
                yield break;
            }

            Guided2DStart resolvedStart = guided2DStart;
            System.Random random = new System.Random(guided2DSeed);
            if (resolvedStart == Guided2DStart.RandomT6T8)
                resolvedStart = random.Next(0, 2) == 0 ? Guided2DStart.NearT6 : Guided2DStart.NearT8;
            Transform startMarker = resolvedStart == Guided2DStart.NearT6 ? t6RightMarker : t8RightMarker;
            float lateralPerturbation = Mathf.Lerp(-5f, 5f, (float)random.NextDouble());
            if (Mathf.Abs(lateralPerturbation) < 1.5f)
                lateralPerturbation = lateralPerturbation < 0f ? -1.5f : 1.5f;
            Vector3 initialProposal = startMarker.position + guided2DReferenceLateral * lateralPerturbation;
            if (!FindSurfaceAt(initialProposal, seedNormal, out RaycastHit initialHit))
            {
                FailGuided2D("No valid skin projection near selected start marker");
                yield break;
            }

            AlignProbeToSurface(initialHit, 0f, 0f);
            yield return null;
            if (!EnforceRigidContact())
            {
                guided2DContactViolations++;
                FailGuided2D("Initial placement could not establish rigid skin contact");
                yield break;
            }
            guided2DLastContact = faceCenter.position;
            if (!IsGuided2DWorkspaceValid(guided2DLastContact))
            {
                guided2DWorkspaceViolations++;
                FailGuided2D("Initial placement is outside the declared 2D workspace");
                yield break;
            }
            if (Vector3.Distance(guided2DLastContact, guided2DFinalContact) <= guided2DFinalToleranceMm)
            {
                FailGuided2D("Selected start unexpectedly lies inside the final target tolerance");
                yield break;
            }
            RecordGuided2DTrace(true, 0f, "initial placement: " + resolvedStart);
            yield return CaptureGuided2DScreenshot("initial");

            SetGuided2DPhase(Guided2DPhase.LongitudinalSearch, "initial placement -> longitudinal search");
            status = "PHASE A — SEARCHING FOR T7";
            for (int guard = 0; guard < 160; guard++)
            {
                float longitudinalError = Thoracic2DNavigation.SignedCoordinate(
                    guided2DT7Contact, faceCenter.position, guided2DReferenceCaudal);
                guided2DDistanceToGoal = Mathf.Abs(longitudinalError);
                if (guided2DDistanceToGoal <= guided2DT7ToleranceMm) break;
                yield return WaitForGuided2DPermission();
                if (guided2DRoutine == null) yield break;
                Vector3 localCaudal = DetermineCaudalTangent(CurrentSurfaceNormal());
                Vector3 direction = longitudinalError > 0f ? -localCaudal : localCaudal;
                guided2DDirection = Vector3.Dot(direction, guided2DReferenceCaudal) >= 0f ? "CAUDAL" : "CRANIAL";
                if (!TryGuided2DMove(direction, Mathf.Min(guided2DStepMm, guided2DDistanceToGoal), "phase A"))
                {
                    FailGuided2D("Longitudinal movement rejected: " + guided2DFailureReason);
                    yield break;
                }
                yield return Guided2DPlaybackDelay();
            }
            guided2DDistanceToGoal = Mathf.Abs(Thoracic2DNavigation.SignedCoordinate(
                guided2DT7Contact, faceCenter.position, guided2DReferenceCaudal));
            guided2DLongitudinalPass = guided2DDistanceToGoal <= guided2DT7ToleranceMm;
            if (!guided2DLongitudinalPass)
            {
                FailGuided2D("T7 level tolerance was not reached within the movement budget");
                yield break;
            }
            SetGuided2DPhase(Guided2DPhase.T7Acquired, "T7 level geometrically validated");
            status = "T7 LEVEL ACQUIRED";
            yield return CaptureGuided2DScreenshot("phase_a_t7_acquired");
            yield return Guided2DPlaybackDelay(2f);

            SetGuided2DPhase(Guided2DPhase.LateralSearch, "actual phase A endpoint -> lateral search");
            status = "PHASE B — LATERAL SEARCH";
            float targetLateral = Thoracic2DNavigation.SignedCoordinate(
                guided2DT7Contact, guided2DFinalContact, guided2DReferenceLateral);
            for (int guard = 0; guard < 120; guard++)
            {
                float currentLateral = Thoracic2DNavigation.SignedCoordinate(
                    guided2DT7Contact, faceCenter.position, guided2DReferenceLateral);
                float lateralError = targetLateral - currentLateral;
                guided2DDistanceToGoal = Mathf.Abs(lateralError);
                if (guided2DDistanceToGoal <= guided2DLateralToleranceMm) break;
                yield return WaitForGuided2DPermission();
                Vector3 normal = CurrentSurfaceNormal();
                Vector3 localCaudal = DetermineCaudalTangent(normal);
                Vector3 localLateral = Vector3.Cross(normal, localCaudal).normalized;
                if (Vector3.Dot(localLateral, guided2DReferenceLateral) < 0f) localLateral = -localLateral;
                Vector3 direction = lateralError >= 0f ? localLateral : -localLateral;
                guided2DDirection = lateralError >= 0f ? "LATERAL" : "MEDIAL";
                if (!TryGuided2DMove(direction, Mathf.Min(guided2DStepMm, guided2DDistanceToGoal), "phase B"))
                {
                    FailGuided2D("Lateral movement rejected: " + guided2DFailureReason);
                    yield break;
                }
                yield return Guided2DPlaybackDelay();
            }
            float reachedLateral = Thoracic2DNavigation.SignedCoordinate(
                guided2DT7Contact, faceCenter.position, guided2DReferenceLateral);
            guided2DLateralPass = Mathf.Abs(targetLateral - reachedLateral) <= guided2DLateralToleranceMm;
            if (!guided2DLateralPass)
            {
                FailGuided2D("Target lateral region was not reached within the movement budget");
                yield break;
            }
            status = "TARGET REGION APPROACHED";
            yield return CaptureGuided2DScreenshot("phase_b_target_approached");
            yield return Guided2DPlaybackDelay(2f);

            SetGuided2DPhase(Guided2DPhase.FinePlacement, "actual phase B endpoint -> fine placement");
            status = "PHASE C — FINE PLACEMENT";
            for (int guard = 0; guard < 120; guard++)
            {
                Vector3 current = faceCenter.position;
                guided2DDistanceToGoal = Vector3.Distance(current, guided2DFinalContact);
                // Navigate to the actual validated candidate, not merely the
                // broader pass/fail tolerance boundary. This preserves the
                // candidate's image-score semantics across approach paths.
                if (guided2DDistanceToGoal <= Mathf.Min(guided2DFinalToleranceMm, 0.15f)) break;
                yield return WaitForGuided2DPermission();
                Vector3 direction = Thoracic2DNavigation.TangentToward(current, guided2DFinalContact, CurrentSurfaceNormal());
                if (direction.sqrMagnitude < 0.5f)
                {
                    FailGuided2D("Final target has no valid local skin-tangent direction");
                    yield break;
                }
                guided2DDirection = "FINE TARGET";
                if (!TryGuided2DMove(direction, Mathf.Min(guided2DStepMm * 0.75f, guided2DDistanceToGoal), "phase C"))
                {
                    FailGuided2D("Fine placement movement rejected: " + guided2DFailureReason);
                    yield break;
                }
                yield return Guided2DPlaybackDelay();
            }

            // Reach the discovered orientation physically as bounded angular
            // steps about the current contact point. Position and orientation
            // are both part of the validated candidate; this is not a hidden
            // SetPositionAndRotation correction.
            if (guided2DFinalCandidate != null && guided2DFinalCandidate.pose != null)
            {
                for (int guard = 0; guard < 120; guard++)
                {
                    float angularError = Quaternion.Angle(probeRoot.rotation, guided2DFinalCandidate.pose.rotation);
                    if (angularError <= 0.75f) break;
                    yield return WaitForGuided2DPermission();
                    guided2DDirection = "FINE ORIENTATION";
                    if (!TryGuided2DRotate(guided2DFinalCandidate.pose.rotation, Mathf.Min(3f, angularError)))
                    {
                        FailGuided2D("Fine orientation movement rejected: " + guided2DFailureReason);
                        yield break;
                    }
                    yield return Guided2DPlaybackDelay();
                }
            }

            guided2DDistanceToGoal = Vector3.Distance(faceCenter.position, guided2DFinalContact);
            bool contactValid = MeasureGuided2DContact();
            bool workspaceValid = IsGuided2DWorkspaceValid(faceCenter.position);
            yield return null;
            Texture2D finalImage = CaptureCleanImage();
            ScoreBreakdown finalScore = finalImage != null
                ? ScoreCandidate(finalImage, SampleAnatomyLabels(labelGridSize))
                : new ScoreBreakdown { total = 0f, notes = "No ultrasound image" };
            float requiredScore = guided2DFinalCandidate != null && guided2DFinalCandidate.score != null
                ? guided2DFinalCandidate.score.total * acceptableRelativeToBest
                : discoveryAcceptableScoreFloor;
            if (discoveryAcceptableScoreFloor > 0f) requiredScore = discoveryAcceptableScoreFloor;
            guided2DFinePass = guided2DDistanceToGoal <= guided2DFinalToleranceMm
                && contactValid && workspaceValid && finalImage != null && finalScore.total >= requiredScore;
            guided2DFinalStop = true;
            guided2DDirection = "STOP";
            bool twoAxis = Thoracic2DNavigation.HasTwoAxisMotion(guided2DLongitudinalTravel, guided2DLateralTravel);
            bool success = guided2DLongitudinalPass && guided2DLateralPass && guided2DFinePass
                && guided2DFinalStop && twoAxis && guided2DContactViolations == 0 && guided2DWorkspaceViolations == 0;
            if (!success)
            {
                guided2DFailureReason = BuildFinalFailureReason(contactValid, workspaceValid, twoAxis, finalScore.total, requiredScore);
                SetGuided2DPhase(Guided2DPhase.FinalFailure, "explicit STOP -> validation failed");
                status = "FINAL STOP — FAILED: " + guided2DFailureReason;
            }
            else
            {
                SetGuided2DPhase(Guided2DPhase.FinalSuccess, "explicit STOP -> all validation passed");
                status = "FINAL STOP — SUCCESS";
            }
            RecordGuided2DTrace(true, 0f, "explicit final STOP");
            SaveGuided2DResult(resolvedStart, finalScore.total, requiredScore, success);
            yield return CaptureGuided2DScreenshot(success ? "final_stop_success" : "final_stop_failure");
            Debug.Log("[ThoracicUSCoach][2D] " + status + " result=" + guided2DResultPath);
            guided2DRoutine = null;
        }

        private void ResetGuidedRunCounters()
        {
            guided2DTrace.Clear();
            guided2DResult = null;
            guided2DResultPath = string.Empty;
            guided2DPaused = false;
            guided2DSingleStepRequested = false;
            guided2DTotalSteps = 0;
            guided2DRejectedMovements = 0;
            guided2DContactViolations = 0;
            guided2DWorkspaceViolations = 0;
            guided2DLongitudinalTravel = 0f;
            guided2DLateralTravel = 0f;
            guided2DMaxStepDistance = 0f;
            guided2DLongitudinalPass = false;
            guided2DLateralPass = false;
            guided2DFinePass = false;
            guided2DFinalStop = false;
            guided2DFailureReason = string.Empty;
            guided2DDirection = "STOP";
        }

        private bool TryPrepareGuidedGeometry(bool requireFinalCandidate, out string error)
        {
            if (!ValidateReferences(out error)) return false;
            if (t6RightMarker == null || t8RightMarker == null)
            {
                error = "T6/T8 right start markers are missing";
                return false;
            }
            if (!FindSurfaceAt(t7RightSeed.position, Vector3.zero, out RaycastHit t7Hit))
            {
                error = "No skin surface found at the T7 reference";
                return false;
            }
            seedNormal = t7Hit.normal.normalized;
            guided2DT7Contact = t7Hit.point;
            guided2DReferenceCaudal = DetermineCaudalTangent(seedNormal);
            guided2DReferenceLateral = Vector3.Cross(seedNormal, guided2DReferenceCaudal).normalized;
            if (guided2DReferenceLateral.sqrMagnitude < 0.5f)
            {
                error = "Could not construct the skin tangent frame";
                return false;
            }
            caudalTangent = guided2DReferenceCaudal;
            lateralTangent = guided2DReferenceLateral;
            seedProbeRotation = probeRoot.rotation;
            if (!TryLoadGuided2DCandidate(out guided2DFinalCandidate, out float floor))
            {
                if (requireFinalCandidate)
                {
                    error = "No validated candidate manifest. Run Candidate Discovery once before the 2D demo.";
                    return false;
                }
                guided2DFinalContact = guided2DT7Contact;
            }
            else
            {
                guided2DFinalContact = guided2DFinalCandidate.pose.contactPoint;
                discoveryAcceptableScoreFloor = floor;
            }
            error = null;
            return true;
        }

        private bool TryLoadGuided2DCandidate(out CandidateRecord candidate, out float floor)
        {
            candidate = null;
            floor = 0f;
            string manifestPath = Path.Combine(ArtifactRoot(), "candidates", "candidate_manifest.json");
            if (!File.Exists(manifestPath)) return false;
            try
            {
                CandidateManifest manifest = JsonUtility.FromJson<CandidateManifest>(File.ReadAllText(manifestPath));
                if (manifest == null || manifest.candidates == null || manifest.candidates.Count == 0) return false;
                candidate = manifest.candidates[0];
                floor = manifest.acceptableScoreFloor;
                return candidate.pose != null && candidate.score != null && floor > 0f;
            }
            catch (IOException) { return false; }
            catch (ArgumentException) { return false; }
        }

        private bool TryGuided2DMove(Vector3 tangentDirection, float commandedStepMm, string note)
        {
            Vector3 oldPosition = probeRoot.position;
            Quaternion oldRotation = probeRoot.rotation;
            Vector3 oldContact = faceCenter.position;
            Vector3 expectedNormal = CurrentSurfaceNormal();
            Vector3 direction = Vector3.ProjectOnPlane(tangentDirection, expectedNormal).normalized;
            if (direction.sqrMagnitude < 0.5f)
                return RejectGuided2DMove(oldPosition, oldRotation, "degenerate tangent direction", false, false);
            Vector3 proposed = oldContact + direction * commandedStepMm;
            if (!FindSurfaceAt(proposed, expectedNormal, out RaycastHit hit))
                return RejectGuided2DMove(oldPosition, oldRotation, "skin projection failed", false, false);
            AlignProbeToSurface(hit, 0f, 0f);
            if (!EnforceRigidContact())
                return RejectGuided2DMove(oldPosition, oldRotation, "contact constraint failed", true, false);
            Vector3 newContact = faceCenter.position;
            float distance = Vector3.Distance(oldContact, newContact);
            if (Thoracic2DNavigation.IsTeleportLike(distance, commandedStepMm, 2.5f))
                return RejectGuided2DMove(oldPosition, oldRotation,
                    string.Format(CultureInfo.InvariantCulture, "step discontinuity {0:F2} mm", distance), false, false);
            if (!IsGuided2DWorkspaceValid(newContact))
                return RejectGuided2DMove(oldPosition, oldRotation, "workspace boundary", false, true);

            Vector3 delta = newContact - oldContact;
            guided2DLongitudinalTravel += Mathf.Abs(Vector3.Dot(delta, guided2DReferenceCaudal));
            guided2DLateralTravel += Mathf.Abs(Vector3.Dot(delta, guided2DReferenceLateral));
            guided2DMaxStepDistance = Mathf.Max(guided2DMaxStepDistance, distance);
            guided2DTotalSteps++;
            guided2DLastContact = newContact;
            CaptureCleanImage();
            RecordGuided2DTrace(true, distance, note);
            return true;
        }

        private bool RejectGuided2DMove(Vector3 oldPosition, Quaternion oldRotation, string reason, bool contact, bool workspace)
        {
            probeRoot.SetPositionAndRotation(oldPosition, oldRotation);
            EnforceRigidContact();
            guided2DRejectedMovements++;
            if (contact) guided2DContactViolations++;
            if (workspace) guided2DWorkspaceViolations++;
            guided2DFailureReason = reason;
            RecordGuided2DTrace(false, 0f, reason);
            return false;
        }

        private bool TryGuided2DRotate(Quaternion targetRotation, float maximumAngleDeg)
        {
            Vector3 oldPosition = probeRoot.position;
            Quaternion oldRotation = probeRoot.rotation;
            Vector3 oldContact = faceCenter.position;
            float fullAngle = Mathf.Max(0.001f, Quaternion.Angle(oldRotation, targetRotation));
            Quaternion next = Quaternion.Slerp(oldRotation, targetRotation,
                Mathf.Clamp01(maximumAngleDeg / fullAngle));
            Quaternion deltaRotation = next * Quaternion.Inverse(oldRotation);
            probeRoot.rotation = next;
            probeRoot.position = oldContact + deltaRotation * (oldPosition - oldContact);
            if (!EnforceRigidContact())
                return RejectGuided2DMove(oldPosition, oldRotation, "contact constraint failed during orientation", true, false);
            Vector3 newContact = faceCenter.position;
            float contactDistance = Vector3.Distance(oldContact, newContact);
            if (contactDistance > 2.5f)
                return RejectGuided2DMove(oldPosition, oldRotation, "orientation displaced contact too far", false, false);
            if (!IsGuided2DWorkspaceValid(newContact))
                return RejectGuided2DMove(oldPosition, oldRotation, "workspace boundary during orientation", false, true);
            Vector3 contactDelta = newContact - oldContact;
            guided2DLongitudinalTravel += Mathf.Abs(Vector3.Dot(contactDelta, guided2DReferenceCaudal));
            guided2DLateralTravel += Mathf.Abs(Vector3.Dot(contactDelta, guided2DReferenceLateral));
            guided2DMaxStepDistance = Mathf.Max(guided2DMaxStepDistance, contactDistance);
            guided2DTotalSteps++;
            guided2DLastContact = newContact;
            CaptureCleanImage();
            RecordGuided2DTrace(true, contactDistance, "phase C orientation");
            return true;
        }

        private bool MeasureGuided2DContact()
        {
            if (skinIntersection == null) return false;
            skinIntersection.Measure();
            return IsCurrentContactValid();
        }

        private Vector3 CurrentSurfaceNormal()
        {
            if (skinIntersection != null)
            {
                skinIntersection.Measure();
                if (skinIntersection.AnyHit && skinIntersection.CompressionNormal.sqrMagnitude > 0.5f)
                    return skinIntersection.CompressionNormal.normalized;
            }
            return seedNormal.sqrMagnitude > 0.5f ? seedNormal : Vector3.up;
        }

        private bool IsGuided2DWorkspaceValid(Vector3 contact)
        {
            float longitudinal = Thoracic2DNavigation.SignedCoordinate(guided2DT7Contact, contact, guided2DReferenceCaudal);
            float lateral = Thoracic2DNavigation.SignedCoordinate(guided2DT7Contact, contact, guided2DReferenceLateral);
            return Mathf.Abs(longitudinal) <= captureCaudalPatchHalfExtentMm + 2f
                && Mathf.Abs(lateral) <= captureLateralPatchHalfExtentMm + 2f;
        }

        private IEnumerator WaitForGuided2DPermission()
        {
            while (guided2DPaused && !guided2DSingleStepRequested) yield return null;
            if (guided2DSingleStepRequested) guided2DSingleStepRequested = false;
        }

        private object Guided2DPlaybackDelay(float multiplier = 1f)
        {
            return new WaitForSecondsRealtime(0.12f * multiplier / Mathf.Max(0.15f, guided2DPlaybackSpeed));
        }

        private void SetGuided2DPhase(Guided2DPhase phase, string transition)
        {
            guided2DPhase = phase;
            guided2DLastTransition = transition;
        }

        private void FailGuided2D(string reason)
        {
            guided2DFailureReason = reason;
            guided2DFinalStop = true;
            guided2DDirection = "STOP";
            SetGuided2DPhase(Guided2DPhase.FinalFailure, "explicit STOP -> " + reason);
            status = "FINAL STOP — FAILED: " + reason;
            SaveGuided2DResult(guided2DStart, 0f, discoveryAcceptableScoreFloor, false);
            Debug.LogError("[ThoracicUSCoach][2D] " + status);
            guided2DRoutine = null;
        }

        private string BuildFinalFailureReason(bool contact, bool workspace, bool twoAxis, float score, float requiredScore)
        {
            if (!guided2DLongitudinalPass) return "T7 longitudinal phase not validated";
            if (!guided2DLateralPass) return "lateral target phase not validated";
            if (!twoAxis) return "both physical tangent axes were not exercised";
            if (!contact) return "final contact invalid";
            if (!workspace) return "final pose outside workspace";
            if (guided2DDistanceToGoal > guided2DFinalToleranceMm) return "final pose outside target tolerance";
            if (score < requiredScore) return string.Format(CultureInfo.InvariantCulture,
                "final score {0:F3} below validated floor {1:F3}", score, requiredScore);
            if (guided2DContactViolations > 0) return "contact violation occurred during the run";
            if (guided2DWorkspaceViolations > 0) return "workspace violation occurred during the run";
            return "final validation failed";
        }

        private void RecordGuided2DTrace(bool accepted, float distance, string note)
        {
            Vector3 contact = faceCenter != null ? faceCenter.position : Vector3.zero;
            guided2DTrace.Add(new Guided2DTracePoint
            {
                step = guided2DTotalSteps,
                utc = DateTime.UtcNow.ToString("O"),
                phase = guided2DPhase.ToString(),
                direction = guided2DDirection,
                probePosition = probeRoot != null ? probeRoot.position : Vector3.zero,
                contactPosition = contact,
                longitudinalMm = guided2DReferenceCaudal.sqrMagnitude > 0.5f
                    ? Thoracic2DNavigation.SignedCoordinate(guided2DT7Contact, contact, guided2DReferenceCaudal) : 0f,
                lateralMm = guided2DReferenceLateral.sqrMagnitude > 0.5f
                    ? Thoracic2DNavigation.SignedCoordinate(guided2DT7Contact, contact, guided2DReferenceLateral) : 0f,
                distanceToPhaseTargetMm = guided2DDistanceToGoal,
                stepDistanceMm = distance,
                contactValid = skinIntersection != null && IsCurrentContactValid(),
                workspaceValid = guided2DReferenceCaudal.sqrMagnitude > 0.5f && IsGuided2DWorkspaceValid(contact),
                accepted = accepted,
                note = note
            });
        }

        private void SaveGuided2DResult(Guided2DStart resolvedStart, float finalScore, float requiredScore, bool success)
        {
            guided2DResult = new Guided2DRunResult
            {
                generatedUtc = DateTime.UtcNow.ToString("O"),
                unityVersion = Application.unityVersion,
                scene = gameObject.scene.path,
                start = resolvedStart.ToString(),
                seed = guided2DSeed,
                longitudinalPass = guided2DLongitudinalPass,
                lateralPass = guided2DLateralPass,
                finePlacementPass = guided2DFinePass,
                finalStopPass = guided2DFinalStop,
                overallPass = success,
                contactViolations = guided2DContactViolations,
                workspaceViolations = guided2DWorkspaceViolations,
                rejectedMovements = guided2DRejectedMovements,
                totalSteps = guided2DTotalSteps,
                longitudinalDisplacementMm = guided2DLongitudinalTravel,
                lateralDisplacementMm = guided2DLateralTravel,
                maximumStepDistanceMm = guided2DMaxStepDistance,
                finalReferenceScore = finalScore,
                requiredReferenceScore = requiredScore,
                failureReason = guided2DFailureReason,
                lastValidatedTransition = guided2DLastTransition,
                trace = new List<Guided2DTracePoint>(guided2DTrace)
            };
            string directory = Path.Combine(ArtifactRoot(), "guided2d", "runs");
            Directory.CreateDirectory(directory);
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
            guided2DResultPath = Path.Combine(directory,
                "guided2d_" + resolvedStart.ToString().ToLowerInvariant() + "_seed" + guided2DSeed + "_" + stamp + ".json");
            File.WriteAllText(guided2DResultPath, JsonUtility.ToJson(guided2DResult, true));
        }

        private IEnumerator CaptureGuided2DScreenshot(string label)
        {
            yield return new WaitForEndOfFrame();
            string directory = Path.Combine(ArtifactRoot(), "guided2d", "screenshots");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, label + "_seed" + guided2DSeed + ".png");
            ScreenCapture.CaptureScreenshot(path);
            yield return null;
        }

        private void PollGuided2DBridge()
        {
            if (!Application.isPlaying || Time.realtimeSinceStartup < guided2DNextBridgePoll) return;
            guided2DNextBridgePoll = Time.realtimeSinceStartup + 0.05f;
            string bridge = Path.Combine(ArtifactRoot(), "bridge");
            string requestPath = Path.Combine(bridge, "guided2d_request.json");
            if (!File.Exists(requestPath)) return;
            try
            {
                Guided2DBridgeRequest request = JsonUtility.FromJson<Guided2DBridgeRequest>(File.ReadAllText(requestPath));
                if (request == null || string.IsNullOrEmpty(request.requestId) || request.requestId == guided2DLastBridgeRequestId) return;
                guided2DLastBridgeRequestId = request.requestId;
                string error = string.Empty;
                switch ((request.command ?? string.Empty).ToLowerInvariant())
                {
                    case "start":
                        if (!Enum.TryParse(request.start, true, out guided2DStart)) guided2DStart = Guided2DStart.NearT6;
                        guided2DSeed = request.seed;
                        guided2DSeedText = guided2DSeed.ToString(CultureInfo.InvariantCulture);
                        guided2DPlaybackSpeed = Mathf.Clamp(request.speed, 0.15f, 4f);
                        StartGuided2DDemo();
                        break;
                    case "pause": PauseGuided2DDemo(); break;
                    case "resume": ResumeGuided2DDemo(); break;
                    case "single_step": SingleStepGuided2DDemo(); break;
                    case "reset": ResetGuided2DDemo(); break;
                    case "manual": ManualGuided2DMove(request.manualDirection); break;
                    case "status": break;
                    default: error = "command must be start, pause, resume, single_step, reset, manual, or status"; break;
                }
                Directory.CreateDirectory(bridge);
                Guided2DBridgeResponse response = new Guided2DBridgeResponse
                {
                    requestId = request.requestId,
                    phase = guided2DPhase.ToString(),
                    status = status,
                    running = guided2DRoutine != null,
                    paused = guided2DPaused,
                    done = guided2DPhase == Guided2DPhase.FinalSuccess || guided2DPhase == Guided2DPhase.FinalFailure,
                    success = guided2DPhase == Guided2DPhase.FinalSuccess,
                    resultPath = guided2DResultPath,
                    error = error
                };
                File.WriteAllText(Path.Combine(bridge, "guided2d_response.json"), JsonUtility.ToJson(response, true));
            }
            catch (IOException) { }
            catch (ArgumentException exception)
            {
                Debug.LogWarning("[ThoracicUSCoach][2D] Invalid bridge request: " + exception.Message);
            }
        }

        private void DrawGuided2DGUI()
        {
            DrawGuided2DPreviews();
            float width = 500f;
            float height = Mathf.Min(720f, Screen.height - 40f);
            float x = Mathf.Max(510f, Screen.width - width - 20f);
            GUILayout.BeginArea(new Rect(x, 20f, width, height), GUI.skin.box);
            guided2DScroll = GUILayout.BeginScrollView(guided2DScroll);
            GUILayout.Label("THORACIC US COACH — 2D DEMO");
            GUILayout.Label("GUIDED / ORACLE-ASSISTED");
            GUILayout.Label("Simulator-only engineering demo; not ultrasound-only or clinically validated.");
            GUILayout.Space(5f);

            GUILayout.Label("Starting position");
            guided2DStart = (Guided2DStart)GUILayout.SelectionGrid((int)guided2DStart,
                new[] { "Near T6", "Near T8", "Random T6/T8" }, 3);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Seed", GUILayout.Width(45f));
            guided2DSeedText = GUILayout.TextField(guided2DSeedText, GUILayout.Width(110f));
            GUILayout.Label(string.Format(CultureInfo.InvariantCulture, "Speed {0:F2}x", guided2DPlaybackSpeed));
            guided2DPlaybackSpeed = GUILayout.HorizontalSlider(guided2DPlaybackSpeed, 0.15f, 4f, GUILayout.Width(140f));
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUI.enabled = guided2DRoutine == null && !guided2DManualBusy;
            if (GUILayout.Button("Start 2D Demo")) StartGuided2DDemo();
            GUI.enabled = guided2DRoutine != null && !guided2DPaused;
            if (GUILayout.Button("Pause")) PauseGuided2DDemo();
            GUI.enabled = guided2DRoutine != null && guided2DPaused;
            if (GUILayout.Button("Resume")) ResumeGuided2DDemo();
            if (GUILayout.Button("Single Step")) SingleStepGuided2DDemo();
            GUI.enabled = true;
            GUILayout.EndHorizontal();
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Reset")) ResetGuided2DDemo();
            GUI.enabled = guided2DRoutine == null && (guided2DPhase == Guided2DPhase.FinalSuccess || guided2DPhase == Guided2DPhase.FinalFailure);
            if (GUILayout.Button("Run Again")) RunGuided2DAgain();
            GUI.enabled = true;
            guided2DShowGuidance = GUILayout.Toggle(guided2DShowGuidance, "Show Guidance / Ground Truth");
            GUILayout.EndHorizontal();

            GUILayout.Space(6f);
            GUILayout.Label("PHASE STATUS");
            GUILayout.Label("Phase: " + guided2DPhase.ToString().ToUpperInvariant());
            GUILayout.Label("Direction: " + guided2DDirection + "   Step: " + guided2DTotalSteps);
            GUILayout.Label("Stage: " + status);
            GUILayout.Label("Contact: " + (skinIntersection != null && IsCurrentContactValid() ? "VALID" : "INVALID")
                + "   Workspace: " + (guided2DReferenceCaudal.sqrMagnitude > 0.5f && faceCenter != null && IsGuided2DWorkspaceValid(faceCenter.position) ? "VALID" : "INVALID"));
            GUILayout.Label("Final STOP: " + (guided2DFinalStop ? "EXECUTED" : "PENDING"));
            GUILayout.Label("Last transition: " + guided2DLastTransition);
            if (!string.IsNullOrEmpty(guided2DFailureReason)) GUILayout.Label("Failure: " + guided2DFailureReason);

            GUILayout.Space(6f);
            GUILayout.Label("MANUAL SURFACE MOVEMENT (same validated movement path)");
            GUI.enabled = guided2DRoutine == null && !guided2DManualBusy;
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Cranial")) ManualGuided2DMove("cranial");
            if (GUILayout.Button("Caudal")) ManualGuided2DMove("caudal");
            if (GUILayout.Button("Medial")) ManualGuided2DMove("medial");
            if (GUILayout.Button("Lateral")) ManualGuided2DMove("lateral");
            GUILayout.EndHorizontal();
            GUI.enabled = true;

            GUILayout.Space(6f);
            GUILayout.Label("DEBUG — PRIVILEGED SIMULATOR DIAGNOSTICS");
            GUILayout.Label(string.Format(CultureInfo.InvariantCulture,
                "Travel: longitudinal {0:F1} mm | lateral {1:F1} mm", guided2DLongitudinalTravel, guided2DLateralTravel));
            GUILayout.Label(string.Format(CultureInfo.InvariantCulture,
                "Goal distance {0:F2} mm | rejected {1} | max step {2:F2} mm",
                guided2DDistanceToGoal, guided2DRejectedMovements, guided2DMaxStepDistance));
            if (probeRoot != null) GUILayout.Label("Probe pose: " + probeRoot.position.ToString("F2") + " / " + probeRoot.eulerAngles.ToString("F1"));
            GUILayout.Label("Privileged geometry guides motion; it is not a policy input or learned result.");

            if (guided2DResult != null)
            {
                GUILayout.Space(8f);
                GUILayout.Label("2D GUIDED DEMO RESULT");
                GUILayout.Label("Start: " + guided2DResult.start + "   Seed: " + guided2DResult.seed);
                GUILayout.Label("Longitudinal: " + PassFail(guided2DResult.longitudinalPass)
                    + " | Lateral: " + PassFail(guided2DResult.lateralPass));
                GUILayout.Label("Fine placement: " + PassFail(guided2DResult.finePlacementPass)
                    + " | Final STOP: " + PassFail(guided2DResult.finalStopPass));
                GUILayout.Label("Contact violations: " + guided2DResult.contactViolations
                    + " | Workspace violations: " + guided2DResult.workspaceViolations
                    + " | Steps: " + guided2DResult.totalSteps);
                GUILayout.Label("OVERALL: " + PassFail(guided2DResult.overallPass));
                if (!string.IsNullOrEmpty(guided2DResult.failureReason))
                    GUILayout.Label("Reason: " + guided2DResult.failureReason);
            }
            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private static string PassFail(bool value)
        {
            return value ? "PASS" : "FAIL";
        }

        private void EnsureGuided2DOverviewCamera()
        {
            if (guided2DOverviewCamera != null) return;
            GameObject cameraObject = new GameObject("Thoracic 2D Demo Overview Camera");
            cameraObject.transform.SetParent(transform, false);
            guided2DOverviewCamera = cameraObject.AddComponent<Camera>();
            guided2DOverviewCamera.clearFlags = CameraClearFlags.SolidColor;
            guided2DOverviewCamera.backgroundColor = new Color(0.025f, 0.04f, 0.065f, 1f);
            guided2DOverviewCamera.cullingMask = ~(1 << 5); // exclude screen-space UI
            guided2DOverviewCamera.fieldOfView = 42f;
            guided2DOverviewCamera.nearClipPlane = 0.1f;
            guided2DOverviewCamera.farClipPlane = 1000f;
            guided2DOverviewCamera.depth = -20f;
            guided2DOverviewTexture = new RenderTexture(960, 600, 24, RenderTextureFormat.ARGB32)
            {
                name = "Thoracic 2D Demo Overview"
            };
            guided2DOverviewTexture.Create();
            guided2DOverviewCamera.targetTexture = guided2DOverviewTexture;
            UpdateGuided2DOverviewCamera();
        }

        private void UpdateGuided2DOverviewCamera()
        {
            if (guided2DOverviewCamera == null || probeRoot == null) return;
            Vector3 focus = faceCenter != null ? faceCenter.position : probeRoot.position;
            Vector3 normal = seedNormal.sqrMagnitude > 0.5f ? seedNormal : Vector3.up;
            Vector3 longitudinal = guided2DReferenceCaudal.sqrMagnitude > 0.5f
                ? guided2DReferenceCaudal : Vector3.forward;
            Vector3 lateral = guided2DReferenceLateral.sqrMagnitude > 0.5f
                ? guided2DReferenceLateral : Vector3.right;
            guided2DOverviewCamera.transform.position = focus + normal * 125f - longitudinal * 95f + lateral * 85f;
            guided2DOverviewCamera.transform.LookAt(focus + normal * 12f, normal);
        }

        private void DrawGuided2DPreviews()
        {
            if (guided2DOverviewTexture != null)
            {
                Rect overview = new Rect(20f, 20f, Mathf.Min(900f, Screen.width * 0.50f), Mathf.Min(560f, Screen.height * 0.54f));
                GUI.Box(new Rect(overview.x - 4f, overview.y - 4f, overview.width + 8f, overview.height + 28f), string.Empty);
                GUI.DrawTexture(overview, guided2DOverviewTexture, ScaleMode.ScaleToFit, false);
                GUI.Label(new Rect(overview.x + 8f, overview.y + 5f, 360f, 24f), "LIVE 3D PROBE / SKIN NAVIGATION");
            }
            Texture ultrasound = ultrasoundImageRenderer != null ? ultrasoundImageRenderer.material.mainTexture : null;
            if (ultrasound != null)
            {
                float size = Mathf.Min(360f, Screen.height * 0.34f);
                float x = Mathf.Min(Screen.width - 520f - size - 35f, Screen.width * 0.52f);
                Rect preview = new Rect(Mathf.Max(510f, x), Screen.height - size - 35f, size, size);
                GUI.Box(new Rect(preview.x - 4f, preview.y - 28f, preview.width + 8f, preview.height + 32f), string.Empty);
                GUI.Label(new Rect(preview.x + 4f, preview.y - 25f, 320f, 24f), "LIVE ULTRASOUND — ACTUAL RENDERER");
                GUI.DrawTexture(preview, ultrasound, ScaleMode.ScaleToFit, false);
            }
        }

        private void DestroyGuided2DResources()
        {
            if (guided2DOverviewCamera != null) Destroy(guided2DOverviewCamera.gameObject);
            if (guided2DOverviewTexture != null)
            {
                guided2DOverviewTexture.Release();
                Destroy(guided2DOverviewTexture);
            }
        }

        private void OnDrawGizmos()
        {
            if (!guided2DShowGuidance || guided2DTrace == null || guided2DTrace.Count == 0) return;
            Gizmos.color = Color.cyan;
            for (int index = 1; index < guided2DTrace.Count; index++)
                Gizmos.DrawLine(guided2DTrace[index - 1].contactPosition, guided2DTrace[index].contactPosition);
            Gizmos.color = Color.yellow;
            Gizmos.DrawSphere(guided2DT7Contact, 1.3f);
            Gizmos.color = Color.green;
            Gizmos.DrawSphere(guided2DFinalContact, 1.5f);
            if (faceCenter != null)
            {
                Gizmos.color = Color.magenta;
                Gizmos.DrawLine(faceCenter.position, faceCenter.position + guided2DReferenceCaudal * 8f);
                Gizmos.DrawLine(faceCenter.position, faceCenter.position + guided2DReferenceLateral * 8f);
            }
        }
    }
}
