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
        private const int Gate2SmokeSeed = 612345679;
        private const int Gate2SmokeBudget = 3;
        private const string Gate2SmokeFamily = "gate2_technical_smoke_20261023";
        private bool gate2EvaluationRunning;
        private bool gate2EvaluationComplete;
        private Gate2Phase1Manifest gate2Manifest;
        private Gate2Phase1Report gate2Report;
        private Gate2CaseOutcome gate2CurrentOutcome;
        private string gate2ManifestRelativePath;
        private string gate2OutputAbsolutePath;
        private string gate2RunNonce;

        public bool Gate2EvaluationRunning => gate2EvaluationRunning;
        public bool Gate2EvaluationComplete => gate2EvaluationComplete;
        public string Gate2InfrastructureFailure => gate2Report != null ? gate2Report.infrastructureFailure : string.Empty;

        public bool StartGate2Evaluation(string manifestProjectRelativePath)
        {
            if (!Application.isPlaying || active2DRoutine != null || active2DCaptureRoutine != null)
                return false;
            try
            {
                AutoWire();
                string absoluteManifest = ResolveProjectPath(manifestProjectRelativePath);
                Gate2Phase1Manifest parsed = JsonUtility.FromJson<Gate2Phase1Manifest>(File.ReadAllText(absoluteManifest));
                gate2RunNonce = Environment.GetEnvironmentVariable("THORACIC_GATE2_RUN_NONCE") ?? string.Empty;
                ValidateGate2Manifest(parsed);
                gate2Manifest = parsed;
                gate2ManifestRelativePath = ProjectRelativePath(absoluteManifest);
                gate2OutputAbsolutePath = ResolveProjectPath(parsed.output);
                gate2Report = new Gate2Phase1Report
                {
                    protocolId = parsed.protocolId,
                    generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    unityVersion = Application.unityVersion,
                    scene = SceneManager.GetActiveScene().path,
                    manifestSourceCommit = parsed.sourceCommit,
                    executionCommit = Environment.GetEnvironmentVariable("THORACIC_GATE2_EXECUTION_COMMIT") ?? string.Empty,
                    runNonce = gate2RunNonce,
                    manifest = gate2ManifestRelativePath,
                    manifestSha256 = Sha256Bytes(File.ReadAllBytes(absoluteManifest)),
                    checkpoint = parsed.checkpoint,
                    checkpointSha256 = Sha256Bytes(File.ReadAllBytes(ResolveProjectPath(parsed.checkpoint))),
                    policyServer = parsed.policyServer,
                    policyServerSha256 = Sha256Bytes(File.ReadAllBytes(ResolveProjectPath(parsed.policyServer))),
                    handoffThreshold = parsed.handoffThreshold,
                    phaseOneOnly = parsed.phaseOneOnly,
                    stepBudget = parsed.stepBudget,
                    stepMm = active2DStepMm,
                    maxPenetrationMm = maxPenetrationMm,
                    t7PreciseRadiusMm = Active2DT7PreciseRadiusMm(),
                    t7LevelAcceptableDefinition =
                        "nearest marker is T7 AND Euclidean contact-to-T7 distance <= max(4 mm, 0.42 * mean(T6-T7,T7-T8 spacing))"
                };
                gate2EvaluationComplete = false;
                gate2EvaluationRunning = true;
                active2DRoutine = StartCoroutine(RunGate2Evaluation());
                return true;
            }
            catch (Exception exception) when (exception is IOException || exception is ArgumentException || exception is InvalidOperationException)
            {
                gate2Report = gate2Report ?? new Gate2Phase1Report();
                gate2Report.infrastructureFailure = exception.Message;
                gate2EvaluationRunning = false;
                gate2EvaluationComplete = true;
                Debug.LogError("[ThoracicUSCoach][Gate2] " + exception);
                return false;
            }
        }

        private IEnumerator RunGate2Evaluation()
        {
            bool previousPhaseOneOnly = active2DPhaseOneOnly;
            int previousBudget = active2DLiveStepBudget;
            active2DPhaseOneOnly = true;
            active2DLiveStepBudget = gate2Manifest.stepBudget;
            try
            {
                yield return RunGate2Preflight();
                if (!string.IsNullOrEmpty(gate2Report.infrastructureFailure)) yield break;
                yield return RunGate2Smoke();
                if (!string.IsNullOrEmpty(gate2Report.infrastructureFailure)) yield break;

                foreach (Gate2Phase1Case evaluationCase in gate2Manifest.cases.OrderBy(item => item.order))
                {
                    gate2CurrentOutcome = NewGate2Outcome(evaluationCase);
                    yield return RunActive2DImageOnlyCore(evaluationCase);
                    FinalizeGate2Outcome(evaluationCase, gate2CurrentOutcome);
                    gate2Report.cases.Add(gate2CurrentOutcome);
                    UpdateGate2Summary();
                    SaveGate2Report(false);
                    gate2CurrentOutcome = null;
                    if (!string.IsNullOrEmpty(gate2Report.infrastructureFailure)) yield break;
                    yield return null;
                }
                gate2Report.complete = true;
                UpdateGate2Summary();
                SaveGate2Report(true);
            }
            finally
            {
                if (!gate2Report.complete && string.IsNullOrEmpty(gate2Report.infrastructureFailure))
                    gate2Report.infrastructureFailure = "Gate2 coroutine terminated before all 40 cases completed";
                if (!gate2Report.complete)
                {
                    UpdateGate2Summary();
                    SaveGate2Report(false);
                }
                active2DPhaseOneOnly = previousPhaseOneOnly;
                active2DLiveStepBudget = previousBudget;
                gate2EvaluationRunning = false;
                gate2EvaluationComplete = true;
                active2DRoutine = null;
            }
        }

        private IEnumerator RunGate2Smoke()
        {
            Gate2Phase1Case smokeCase = new Gate2Phase1Case
            {
                caseId = "SMOKE",
                order = 9999,
                family = Gate2SmokeFamily,
                seed = Gate2SmokeSeed,
                startStratum = 1,
                expectedStartLevel = "T7",
                plannedInterpolation = 0.5f,
                plannedLateralMm = 0f
            };
            int benchmarkBudget = active2DLiveStepBudget;
            active2DLiveStepBudget = Gate2SmokeBudget;
            gate2CurrentOutcome = NewGate2Outcome(smokeCase);
            yield return RunActive2DImageOnlyCore(smokeCase);
            FinalizeGate2Outcome(smokeCase, gate2CurrentOutcome);
            active2DLiveStepBudget = benchmarkBudget;
            Gate2CaseOutcome outcome = gate2CurrentOutcome;
            gate2CurrentOutcome = null;
            bool filesExist = File.Exists(ResolveProjectPath(outcome.rawResult))
                && File.Exists(ResolveProjectPath(outcome.privilegedTrace));
            int actorActions = outcome.actionCounts.Sum(item => item.count);
            bool pass = outcome.completed && filesExist && actorActions > 0
                && !outcome.timeout && outcome.safetyFailures == 0
                && outcome.unsafeAcceptedOrTerminalPoses == 0
                && outcome.actorPrivilegedLeakage == 0
                && !outcome.rawVerdictMismatch;
            gate2Report.smoke = new Gate2SmokeOutcome
            {
                family = Gate2SmokeFamily,
                seed = Gate2SmokeSeed,
                stepBudget = Gate2SmokeBudget,
                rawResult = outcome.rawResult,
                privilegedTrace = outcome.privilegedTrace,
                actorActions = actorActions,
                infrastructurePass = pass,
                failureReason = pass ? string.Empty : outcome.failureReason
            };
            if (!pass)
                gate2Report.infrastructureFailure = "Gate2 technical smoke failed before locked cases: "
                    + (string.IsNullOrEmpty(outcome.failureReason) ? "missing bridge/action/raw evidence" : outcome.failureReason);
            SaveGate2Report(false);
        }

        private IEnumerator RunGate2Preflight()
        {
            gate2Report.preflight.Clear();
            foreach (Gate2Phase1Case evaluationCase in gate2Manifest.cases.OrderBy(item => item.order))
            {
                ResetActive2DRunCounters();
                bool placed = TryPlaceActive2DStartExact(
                    evaluationCase.plannedInterpolation,
                    evaluationCase.plannedLateralMm,
                    "gate2-preflight",
                    evaluationCase.seed,
                    out string _);
                yield return null;
                Gate2PreflightOutcome outcome = new Gate2PreflightOutcome
                {
                    caseId = evaluationCase.caseId,
                    order = evaluationCase.order,
                    seed = evaluationCase.seed,
                    startStratum = evaluationCase.startStratum,
                    plannedInterpolation = evaluationCase.plannedInterpolation,
                    plannedLateralMm = evaluationCase.plannedLateralMm,
                    placementSucceeded = placed
                };
                if (placed)
                {
                    Active2DPrivilegedFrame startFrame = BuildActive2DPrivilegedFrame(-1, null);
                    outcome.start = BuildGate2StartAudit(evaluationCase, startFrame);
                    outcome.physicalReachabilityTested = true;
                    outcome.closestObservedT7DistanceMm = startFrame.t7DistanceMm;
                    outcome.physicalReachedPreciseRegion = startFrame.t7LevelAcceptable;
                    Active2DAction reachabilityAction = outcome.start.t7LongitudinalOffsetMm >= 0f
                        ? Active2DAction.Caudal : Active2DAction.Cranial;
                    for (int step = 0;
                        step < gate2Manifest.preflightMaxSteps && !outcome.physicalReachedPreciseRegion;
                        step++)
                    {
                        bool accepted = TryActive2DAction(reachabilityAction);
                        yield return null;
                        Active2DPrivilegedFrame physicalFrame = BuildActive2DPrivilegedFrame(step, null);
                        outcome.physicalTrace.Add(new Gate2PreflightStep
                        {
                            step = step,
                            action = reachabilityAction.ToString().ToUpperInvariant(),
                            accepted = accepted,
                            contactValid = physicalFrame.contactValid,
                            workspaceValid = physicalFrame.workspaceValid,
                            safetyFailure = active2DLastActionExecution.safetyFailure,
                            t7DistanceMm = physicalFrame.t7DistanceMm,
                            t7LevelAcceptable = physicalFrame.t7LevelAcceptable
                        });
                        outcome.physicalSteps++;
                        if (!accepted) outcome.physicalRejectedMoves++;
                        outcome.closestObservedT7DistanceMm = Mathf.Min(
                            outcome.closestObservedT7DistanceMm, physicalFrame.t7DistanceMm);
                        outcome.physicalUnsafe |= !physicalFrame.contactValid
                            || !physicalFrame.workspaceValid
                            || active2DLastActionExecution.safetyFailure;
                        outcome.physicalReachedPreciseRegion |= physicalFrame.t7LevelAcceptable;
                        if (!accepted || outcome.physicalUnsafe) break;
                    }
                    outcome.valid = IsGate2StartValid(outcome.start)
                        && outcome.physicalReachedPreciseRegion
                        && !outcome.physicalUnsafe;
                    if (!outcome.valid)
                    {
                        outcome.failureReason = Gate2StartFailureReason(outcome.start);
                        if (!outcome.physicalReachedPreciseRegion)
                            outcome.failureReason += "; physical longitudinal path did not reach T7 precise region";
                        if (outcome.physicalUnsafe)
                            outcome.failureReason += "; physical reachability path became unsafe";
                    }
                }
                else outcome.failureReason = "exact physical placement failed";
                gate2Report.preflight.Add(outcome);
            }

            int t6 = gate2Report.preflight.Count(item => item.start.actualLevel == "T6");
            int t7 = gate2Report.preflight.Count(item => item.start.actualLevel == "T7");
            int t8 = gate2Report.preflight.Count(item => item.start.actualLevel == "T8");
            int outside = gate2Report.preflight.Count(item => item.start.actualLevel == "OUTSIDE_T6_T8");
            bool allValid = gate2Report.preflight.Count == gate2Manifest.requiredCaseCount
                && gate2Report.preflight.All(item => item.placementSucceeded && item.valid)
                && t6 == gate2Manifest.requiredPerStratum
                && t7 == gate2Manifest.requiredPerStratum
                && t8 == gate2Manifest.requiredPerStratum
                && outside == gate2Manifest.requiredPerStratum;
            if (!allValid)
            {
                gate2Report.infrastructureFailure = string.Format(CultureInfo.InvariantCulture,
                    "Gate2 preflight failed before any actor request: valid={0}/{1}; actual labels T6/T7/T8/Outside={2}/{3}/{4}/{5}",
                    gate2Report.preflight.Count(item => item.valid), gate2Manifest.requiredCaseCount,
                    t6, t7, t8, outside);
                SaveGate2Report(false);
            }
        }

        private Gate2CaseOutcome NewGate2Outcome(Gate2Phase1Case evaluationCase)
        {
            return new Gate2CaseOutcome
            {
                caseId = evaluationCase.caseId,
                order = evaluationCase.order,
                family = evaluationCase.family,
                seed = evaluationCase.seed,
                startStratum = evaluationCase.startStratum,
                plannedInterpolation = evaluationCase.plannedInterpolation,
                plannedLateralMm = evaluationCase.plannedLateralMm
            };
        }

        private bool RecordAndValidateGate2Start(Gate2Phase1Case evaluationCase)
        {
            Active2DPrivilegedFrame frame = BuildActive2DPrivilegedFrame(-1, null);
            active2DLivePrivilegedAudit.frames.Add(frame);
            gate2CurrentOutcome.start = BuildGate2StartAudit(evaluationCase, frame);
            bool valid = IsGate2StartValid(gate2CurrentOutcome.start);
            if (!valid)
            {
                gate2Report.infrastructureFailure = "Gate2 benchmark re-placement diverged after passing preflight for "
                    + evaluationCase.caseId + ": " + Gate2StartFailureReason(gate2CurrentOutcome.start);
            }
            return valid;
        }

        private Gate2StartAudit BuildGate2StartAudit(
            Gate2Phase1Case evaluationCase,
            Active2DPrivilegedFrame frame)
        {
            Vector3 toT7 = t7RightSeed.position - faceCenter.position;
            float longitudinal = Vector3.Dot(toT7, active2DCaudalAxis);
            float lateral = Vector3.Dot(toT7, active2DLateralAxis);
            Vector3 residual = toT7 - active2DCaudalAxis * longitudinal;
            float preciseRadius = Active2DT7PreciseRadiusMm();
            int availableSteps = Mathf.Min(
                gate2Manifest.preflightMaxSteps,
                active2DLiveStepBudget,
                Mathf.FloorToInt(active2DLongitudinalHalfExtentMm / Mathf.Max(0.001f, active2DStepMm)));
            float closestDistance = float.PositiveInfinity;
            int minimumSteps = int.MaxValue;
            int minimumSignedStep = 0;
            for (int signedStep = -availableSteps; signedStep <= availableSteps; signedStep++)
            {
                float remainingLongitudinal = longitudinal - signedStep * active2DStepMm;
                float distance = Mathf.Sqrt(residual.sqrMagnitude + remainingLongitudinal * remainingLongitudinal);
                if (distance < closestDistance) closestDistance = distance;
                int stepCount = Mathf.Abs(signedStep);
                if (distance <= preciseRadius && stepCount < minimumSteps)
                {
                    minimumSteps = stepCount;
                    minimumSignedStep = signedStep;
                }
            }
            bool reachable = minimumSteps != int.MaxValue;
            Vector3 direction = toT7.sqrMagnitude > 0.000001f ? toT7.normalized : Vector3.zero;
            return new Gate2StartAudit
            {
                expectedLevel = evaluationCase.expectedStartLevel,
                actualLevel = frame.nearestLevel,
                expectedLabelMatches = string.Equals(
                    evaluationCase.expectedStartLevel, frame.nearestLevel, StringComparison.Ordinal),
                contactValid = frame.contactValid,
                workspaceValid = frame.workspaceValid,
                t7Reachable = reachable,
                t7DistanceMm = frame.t7DistanceMm,
                t7LongitudinalOffsetMm = longitudinal,
                t7LateralOffsetMm = lateral,
                t7PreciseRadiusMm = preciseRadius,
                closestAttainableT7DistanceMm = closestDistance,
                longitudinalTravelToPreciseRegionMm = reachable ? minimumSteps * active2DStepMm : -1f,
                minimumLongitudinalSteps = reachable ? minimumSteps : -1,
                t7Direction = !reachable ? "UNREACHABLE"
                    : minimumSignedStep > 0 ? "CAUDAL"
                    : minimumSignedStep < 0 ? "CRANIAL"
                    : "AT_T7",
                caudalAxisAlignment = Vector3.Dot(direction, active2DCaudalAxis),
                lateralAxisAlignment = Vector3.Dot(direction, active2DLateralAxis),
                contactPosition = faceCenter.position,
                t7TargetPosition = t7RightSeed.position,
                caudalAxis = active2DCaudalAxis,
                lateralAxis = active2DLateralAxis,
                probeRotation = frame.probeRotation,
                longitudinalHalfExtentMm = active2DLongitudinalHalfExtentMm,
                stepMm = active2DStepMm,
                preflightMaxSteps = gate2Manifest.preflightMaxSteps
            };
        }

        private static bool IsGate2StartValid(Gate2StartAudit audit)
        {
            return audit.expectedLabelMatches && audit.contactValid && audit.workspaceValid && audit.t7Reachable;
        }

        private static string Gate2StartFailureReason(Gate2StartAudit audit)
        {
            List<string> reasons = new List<string>();
            if (!audit.expectedLabelMatches)
                reasons.Add("actual label " + audit.actualLevel + " != expected " + audit.expectedLevel);
            if (!audit.contactValid) reasons.Add("contact invalid");
            if (!audit.workspaceValid) reasons.Add("workspace invalid");
            if (!audit.t7Reachable) reasons.Add("T7 precise sphere unreachable on bounded longitudinal action lattice");
            return string.Join("; ", reasons);
        }

        private void FinalizeGate2Outcome(Gate2Phase1Case evaluationCase, Gate2CaseOutcome outcome)
        {
            string episodeId = Gate2EpisodeId(evaluationCase);
            outcome.rawResult = ProjectRelativePath(Path.Combine(
                ArtifactRoot(), "active2d", "runs", episodeId + ".json"));
            outcome.privilegedTrace = ProjectRelativePath(Path.Combine(
                ArtifactRoot(), "active2d", "runs_privileged", episodeId + ".json"));
            if (active2DResult == null)
            {
                outcome.failureReason = "active2D result missing";
                outcome.failure = true;
                return;
            }
            outcome.completed = active2DResult.finalPhase == Active2DPhase.FinalSuccess.ToString()
                || active2DResult.finalPhase == Active2DPhase.FinalFailure.ToString();
            outcome.phaseOneHandoff = active2DResult.phaseOneHandoff;
            outcome.contactValidAtTerminal = active2DResult.finalContactValid;
            outcome.workspaceValidAtTerminal = active2DResult.finalWorkspaceValid;
            outcome.steps = active2DResult.steps;
            outcome.failureReason = active2DResult.failureReason;
            outcome.rejectedMovements = active2DResult.rejectedMovements;
            outcome.contactViolations = active2DResult.contactViolations;
            outcome.workspaceViolations = active2DResult.workspaceViolations;
            outcome.timeout = !string.IsNullOrEmpty(outcome.failureReason)
                && outcome.failureReason.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0;
            outcome.abstention = !outcome.phaseOneHandoff;
            outcome.failure = active2DResult.finalPhase == Active2DPhase.FinalFailure.ToString();
            if (active2DLivePrivilegedAudit != null)
            {
                Active2DPrivilegedFrame first = active2DLivePrivilegedAudit.frames
                    .Where(item => item.index >= 0 && item.t7LevelAcceptable)
                    .OrderBy(item => item.index)
                    .FirstOrDefault();
                outcome.preciseRegionEntered = first != null;
                outcome.firstPreciseEntryStep = first != null ? first.index : -1;
                outcome.privilegedUnsafeFrames = active2DLivePrivilegedAudit.frames.Count(
                    item => !item.contactValid || !item.workspaceValid);
            }
            Active2DRunTrace handoff = active2DResult.trace.FirstOrDefault(item => item.action == "PHASEHANDOFF");
            if (handoff != null)
            {
                outcome.handoffStep = handoff.step;
                outcome.handoffConfidence = handoff.confidence;
                Active2DPrivilegedFrame handoffFrame = active2DLivePrivilegedAudit != null
                    ? active2DLivePrivilegedAudit.frames.FirstOrDefault(item => item.index == handoff.step)
                    : null;
                if (handoffFrame != null)
                {
                    outcome.handoffT7DistanceMm = handoffFrame.t7DistanceMm;
                    outcome.handoffNearestLevel = handoffFrame.nearestLevel;
                    outcome.handoffNearestLevelCorrect = handoffFrame.nearestLevel == "T7";
                    outcome.handoffT7LevelAcceptable = handoffFrame.t7LevelAcceptable;
                }
            }
            outcome.correctPreciseHandoff = outcome.phaseOneHandoff
                && outcome.handoffT7LevelAcceptable
                && outcome.contactValidAtTerminal
                && outcome.workspaceValidAtTerminal;
            outcome.wrongLevelHandoff = outcome.phaseOneHandoff
                && !outcome.handoffNearestLevelCorrect;
            outcome.confidentWrongLevelHandoff = outcome.wrongLevelHandoff
                && outcome.handoffConfidence >= gate2Manifest.handoffThreshold;
            outcome.recognitionAfterPreciseEntry = outcome.correctPreciseHandoff
                && outcome.preciseRegionEntered
                && outcome.handoffStep >= outcome.firstPreciseEntryStep;
            outcome.safetyFailures = active2DResult.trace.Count(item => item.safetyFailure);
            outcome.unsafeAcceptedOrTerminalPoses = active2DResult.trace.Count(item =>
                (item.accepted || item.action == "PHASEHANDOFF" || item.action == "STOP")
                && (!item.contactValidAfterExecution || !item.workspaceValidAfterExecution));
            outcome.actionCounts = active2DResult.trace
                .GroupBy(item => item.action)
                .OrderBy(group => group.Key)
                .Select(group => new Gate2NamedCount { name = group.Key, count = group.Count() })
                .ToList();
            string opaqueEpisodeId = Gate2EpisodeId(evaluationCase);
            outcome.actorPrivilegedLeakage = active2DResult.episodeId == opaqueEpisodeId ? 0 : 1;
            outcome.actorPrivilegedLeakage += active2DResult.trace.Count(item =>
                string.IsNullOrEmpty(item.image)
                || item.image.IndexOf(opaqueEpisodeId, StringComparison.Ordinal) < 0
                || ActorVisibleValueContainsGate2Secret(item.image, evaluationCase));
            bool derivedAcceptedHandoff = handoff != null && handoff.accepted;
            outcome.rawVerdictMismatch = active2DResult.phaseOneHandoff != derivedAcceptedHandoff
                || active2DResult.correctLevelAtHandoff != outcome.handoffT7LevelAcceptable
                || active2DResult.overallPass != outcome.correctPreciseHandoff;
        }

        private static bool ActorVisibleValueContainsGate2Secret(
            string value,
            Gate2Phase1Case evaluationCase)
        {
            if (string.IsNullOrEmpty(value)) return true;
            return value.IndexOf(evaluationCase.caseId, StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf(evaluationCase.family, StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf(evaluationCase.expectedStartLevel, StringComparison.OrdinalIgnoreCase) >= 0
                || value.IndexOf(evaluationCase.seed.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) >= 0;
        }

        private void UpdateGate2Summary()
        {
            List<Gate2CaseOutcome> cases = gate2Report.cases;
            int entered = cases.Count(item => item.preciseRegionEntered);
            int recognized = cases.Count(item => item.recognitionAfterPreciseEntry);
            Gate2CompletionSummary summary = new Gate2CompletionSummary
            {
                requiredCases = gate2Manifest.requiredCaseCount,
                completedCases = cases.Count(item => item.completed),
                t6Starts = cases.Count(item => item.start.actualLevel == "T6"),
                t7Starts = cases.Count(item => item.start.actualLevel == "T7"),
                t8Starts = cases.Count(item => item.start.actualLevel == "T8"),
                outsideStarts = cases.Count(item => item.start.actualLevel == "OUTSIDE_T6_T8"),
                validStarts = cases.Count(item => item.start.expectedLabelMatches
                    && item.start.contactValid && item.start.workspaceValid && item.start.t7Reachable),
                correctPreciseHandoffs = cases.Count(item => item.correctPreciseHandoff),
                wrongLevelHandoffs = cases.Count(item => item.wrongLevelHandoff),
                confidentWrongLevelHandoffs = cases.Count(item => item.confidentWrongLevelHandoff),
                preciseRegionEntries = entered,
                recognitionsAfterPreciseEntry = recognized,
                recognitionAfterPreciseEntryRate = entered > 0 ? (float)recognized / entered : 0f,
                finalContactValidCount = cases.Count(item => item.contactValidAtTerminal),
                finalWorkspaceValidCount = cases.Count(item => item.workspaceValidAtTerminal),
                unsafeAcceptedOrTerminalPoses = cases.Sum(item => item.unsafeAcceptedOrTerminalPoses),
                privilegedUnsafeFrames = cases.Sum(item => item.privilegedUnsafeFrames),
                safetyFailures = cases.Sum(item => item.safetyFailures),
                rejectedMovements = cases.Sum(item => item.rejectedMovements),
                contactViolations = cases.Sum(item => item.contactViolations),
                workspaceViolations = cases.Sum(item => item.workspaceViolations),
                timeouts = cases.Count(item => item.timeout),
                abstentions = cases.Count(item => item.abstention),
                failures = cases.Count(item => item.failure),
                actorPrivilegedLeakage = cases.Sum(item => item.actorPrivilegedLeakage),
                rawVerdictMismatches = cases.Count(item => item.rawVerdictMismatch)
            };
            summary.actionCounts = cases.SelectMany(item => item.actionCounts)
                .GroupBy(item => item.name)
                .OrderBy(group => group.Key)
                .Select(group => new Gate2NamedCount { name = group.Key, count = group.Sum(item => item.count) })
                .ToList();
            summary.perStratum = gate2Manifest.cases.GroupBy(item => item.startStratum)
                .OrderBy(group => group.Key)
                .Select(group =>
                {
                    List<Gate2CaseOutcome> stratumCases = cases
                        .Where(item => item.startStratum == group.Key).ToList();
                    return new Gate2StratumSummary
                    {
                        startStratum = group.Key,
                        expectedLevel = group.First().expectedStartLevel,
                        cases = stratumCases.Count,
                        validStarts = stratumCases.Count(item => IsGate2StartValid(item.start)),
                        correctPreciseHandoffs = stratumCases.Count(item => item.correctPreciseHandoff),
                        wrongLevelHandoffs = stratumCases.Count(item => item.wrongLevelHandoff),
                        abstentions = stratumCases.Count(item => item.abstention)
                    };
                }).ToList();
            summary.protocolComplete = gate2Report.complete
                && string.IsNullOrEmpty(gate2Report.infrastructureFailure)
                && gate2Report.preflight.Count == gate2Manifest.requiredCaseCount
                && gate2Report.preflight.All(item => item.valid)
                && summary.completedCases == gate2Manifest.requiredCaseCount
                && summary.validStarts == gate2Manifest.requiredCaseCount
                && summary.t6Starts == gate2Manifest.requiredPerStratum
                && summary.t7Starts == gate2Manifest.requiredPerStratum
                && summary.t8Starts == gate2Manifest.requiredPerStratum
                && summary.outsideStarts == gate2Manifest.requiredPerStratum
                && summary.actorPrivilegedLeakage == 0
                && summary.rawVerdictMismatches == 0;
            summary.thresholdsPass = summary.protocolComplete
                && summary.correctPreciseHandoffs >= gate2Manifest.minimumCorrectPreciseHandoffs
                && summary.wrongLevelHandoffs <= gate2Manifest.maximumWrongLevelHandoffs
                && summary.confidentWrongLevelHandoffs <= gate2Manifest.maximumConfidentWrongLevelHandoffs
                && summary.recognitionAfterPreciseEntryRate >= gate2Manifest.minimumRecognitionAfterPreciseEntry
                && summary.finalContactValidCount == gate2Manifest.requiredCaseCount
                && summary.finalWorkspaceValidCount == gate2Manifest.requiredCaseCount
                && summary.unsafeAcceptedOrTerminalPoses == 0
                && summary.privilegedUnsafeFrames == 0
                && summary.safetyFailures == 0
                && summary.actorPrivilegedLeakage == 0
                && summary.rawVerdictMismatches == 0;
            gate2Report.summary = summary;
        }

        private void SaveGate2Report(bool complete)
        {
            gate2Report.complete = complete;
            string directory = Path.GetDirectoryName(gate2OutputAbsolutePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            string temporary = gate2OutputAbsolutePath + ".tmp";
            File.WriteAllText(temporary, JsonUtility.ToJson(gate2Report, true));
            if (File.Exists(gate2OutputAbsolutePath)) File.Replace(temporary, gate2OutputAbsolutePath, null, true);
            else File.Move(temporary, gate2OutputAbsolutePath);
        }

        private void ValidateGate2Manifest(Gate2Phase1Manifest manifest)
        {
            if (manifest == null) throw new InvalidOperationException("Gate2 manifest is empty");
            if (manifest.requiredCaseCount != 40 || manifest.requiredPerStratum != 10
                || manifest.cases == null || manifest.cases.Count != 40)
                throw new InvalidOperationException("Gate2 requires exactly 40 cases and 10 starts per stratum");
            if (!manifest.phaseOneOnly || manifest.stepBudget != 300
                || manifest.hybridEnabled || !string.IsNullOrEmpty(manifest.landmarkCheckpoint)
                || !string.IsNullOrEmpty(manifest.stopCheckpoint))
                throw new InvalidOperationException("Gate2 must remain direct-image Phase1-only with budget 300");
            if (Mathf.Abs(manifest.handoffThreshold - 0.85f) > 0.000001f)
                throw new InvalidOperationException("Gate2 handoff threshold must be exactly 0.85");
            if (!string.Equals(manifest.unityVersion, Application.unityVersion, StringComparison.Ordinal))
                throw new InvalidOperationException("Gate2 Unity version mismatch: actual " + Application.unityVersion);
            if (!string.Equals(manifest.scene, SceneManager.GetActiveScene().path, StringComparison.Ordinal))
                throw new InvalidOperationException("Gate2 scene mismatch: actual " + SceneManager.GetActiveScene().path);
            string executionCommit = Environment.GetEnvironmentVariable("THORACIC_GATE2_EXECUTION_COMMIT") ?? string.Empty;
            if (manifest.sourceCommit.Length != 40 || executionCommit.Length != 40
                || string.IsNullOrWhiteSpace(gate2RunNonce)
                || gate2RunNonce.Any(character => !char.IsLetterOrDigit(character)))
                throw new InvalidOperationException("Gate2 source/execution commit or opaque run nonce provenance is missing");
            if (manifest.cases.Select(item => item.seed).Distinct().Count() != 40
                || manifest.cases.Select(item => item.family).Distinct().Count() != 40
                || manifest.cases.Select(item => item.caseId).Distinct().Count() != 40)
                throw new InvalidOperationException("Gate2 case seeds, families, and IDs must be unique");
            for (int index = 0; index < manifest.cases.Count; index++)
            {
                Gate2Phase1Case item = manifest.cases[index];
                if (item.order != index || item.startStratum < 0 || item.startStratum > 3)
                    throw new InvalidOperationException("Gate2 case order/stratum is invalid at index " + index);
            }
            string checkpointHash = Sha256Bytes(File.ReadAllBytes(ResolveProjectPath(manifest.checkpoint)));
            string serverHash = Sha256Bytes(File.ReadAllBytes(ResolveProjectPath(manifest.policyServer)));
            if (!string.Equals(checkpointHash, manifest.checkpointSha256, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(serverHash, manifest.policyServerSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Gate2 checkpoint or policy-server hash does not match the frozen manifest");
            ResolveProjectPath(manifest.output);
        }

        private string ResolveProjectPath(string projectRelativePath)
        {
            if (string.IsNullOrWhiteSpace(projectRelativePath))
                throw new ArgumentException("Project-relative path is empty");
            string root = Directory.GetParent(Application.dataPath).FullName;
            string full = Path.GetFullPath(Path.Combine(root, projectRelativePath));
            string prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Path escapes project root: " + projectRelativePath);
            return full;
        }

        private string Gate2EpisodeId(Gate2Phase1Case evaluationCase)
        {
            return "G2E_" + gate2RunNonce + "_" + evaluationCase.order.ToString("D4", CultureInfo.InvariantCulture);
        }
    }
}
