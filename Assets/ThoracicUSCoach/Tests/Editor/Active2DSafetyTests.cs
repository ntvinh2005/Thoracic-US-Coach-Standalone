using System;
using System.IO;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace CSSALT.ThoracicUSCoach.Tests
{
    public sealed class Active2DSafetyTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        private sealed class Harness
        {
            public GameObject controllerObject;
            public GameObject probeObject;
            public GameObject skinObject;
            public GameObject contactSkinObject;
            public ThoracicUSCoachController controller;
            public Transform face;
            public SkinIntersectionCompression intersection;
            public BoxCollider surfaceCollider;
            public BoxCollider contactCollider;
        }

        private Harness harness;

        [TearDown]
        public void TearDown()
        {
            if (harness == null) return;
            if (harness.controllerObject != null) UnityEngine.Object.DestroyImmediate(harness.controllerObject);
            if (harness.probeObject != null) UnityEngine.Object.DestroyImmediate(harness.probeObject);
            if (harness.contactSkinObject != null && harness.contactSkinObject != harness.skinObject)
                UnityEngine.Object.DestroyImmediate(harness.contactSkinObject);
            if (harness.skinObject != null) UnityEngine.Object.DestroyImmediate(harness.skinObject);
            harness = null;
        }

        [Test]
        public void ValidSkinContactMovement_IsAcceptedAndTelemetryMatchesRequestedVsAchieved()
        {
            harness = CreateHarness(40f, 40f);

            bool accepted = InvokeAction(Active2DAction.Lateral);

            Assert.IsTrue(accepted);
            Assert.That(harness.face.position.x, Is.EqualTo(3f).Within(0.001f));
            Assert.IsTrue(Execution<bool>("accepted"));
            Assert.That(Execution<float>("requestedDisplacementMm"), Is.EqualTo(3f));
            Assert.That(Execution<float>("attemptedDisplacementMm"), Is.EqualTo(3f).Within(0.001f));
            Assert.That(Execution<float>("achievedDisplacementMm"), Is.EqualTo(3f).Within(0.001f));
            Assert.IsTrue(Execution<bool>("contactValidAfterExecution"));
            Assert.IsTrue(Execution<bool>("workspaceValidAfterExecution"));
            Assert.IsFalse(Execution<bool>("rollbackAttempted"));
            Assert.IsFalse(Execution<bool>("safetyFailure"));
            Assert.That(Execution<string>("rejectionReason"), Is.Empty);
        }

        [Test]
        public void FailedSurfaceRaycast_RejectsWithoutPoseMutation_AndRecordsReason()
        {
            harness = CreateHarness(1f, 1f);
            Vector3 originalPosition = harness.probeObject.transform.position;

            bool accepted = InvokeAction(Active2DAction.Lateral);

            Assert.IsFalse(accepted);
            Assert.That(Vector3.Distance(originalPosition, harness.probeObject.transform.position), Is.LessThan(0.0001f));
            Assert.IsFalse(Execution<bool>("accepted"));
            Assert.That(Execution<float>("requestedDisplacementMm"), Is.EqualTo(3f));
            Assert.That(Execution<float>("achievedDisplacementMm"), Is.EqualTo(0f));
            Assert.IsFalse(Execution<bool>("rollbackAttempted"));
            Assert.IsFalse(Execution<bool>("safetyFailure"));
            StringAssert.Contains("surface raycast failed", Execution<string>("rejectionReason"));
        }

        [Test]
        public void ContactEnforcementFailure_RejectsAndRestoresSafePreviousPose()
        {
            harness = CreateHarness(40f, 1f);
            Vector3 originalPosition = harness.probeObject.transform.position;

            bool accepted = InvokeAction(Active2DAction.Lateral);

            Assert.IsFalse(accepted);
            Assert.That(Vector3.Distance(originalPosition, harness.probeObject.transform.position), Is.LessThan(0.0001f));
            Assert.IsTrue(Execution<bool>("rollbackAttempted"));
            Assert.IsTrue(Execution<bool>("rollbackContactRestored"));
            Assert.IsFalse(Execution<bool>("safetyFailure"));
            Assert.IsTrue(Execution<bool>("contactValidAfterExecution"));
            StringAssert.Contains("contact enforcement failed", Execution<string>("rejectionReason"));
        }

        [Test]
        public void WorkspaceBoundaryMove_RollsBackAndRecordsWorkspaceReason()
        {
            harness = CreateHarness(40f, 40f);
            harness.controller.active2DLateralHalfExtentMm = 2.5f;
            Vector3 originalPosition = harness.probeObject.transform.position;

            bool accepted = InvokeAction(Active2DAction.Lateral);

            Assert.IsFalse(accepted);
            Assert.That(Vector3.Distance(originalPosition, harness.probeObject.transform.position), Is.LessThan(0.0001f));
            Assert.IsTrue(Execution<bool>("rollbackAttempted"));
            Assert.IsTrue(Execution<bool>("rollbackContactRestored"));
            Assert.IsTrue(Execution<bool>("workspaceValidAfterExecution"));
            Assert.IsFalse(Execution<bool>("safetyFailure"));
            StringAssert.Contains("outside the active workspace", Execution<string>("rejectionReason"));
        }

        [Test]
        public void TranslationRollbackFailure_FailsSafeAndPreservesExplicitRestorationReason()
        {
            harness = CreateHarness(40f, 40f);
            harness.intersection.SkinCollider = null;
            harness.intersection.SkinLayer = 0;
            harness.intersection.Measure();

            bool accepted = InvokeAction(Active2DAction.Lateral);

            Assert.IsFalse(accepted);
            Assert.IsTrue(Execution<bool>("rollbackAttempted"));
            Assert.IsFalse(Execution<bool>("rollbackContactRestored"));
            Assert.IsTrue(Execution<bool>("safetyFailure"));
            Assert.IsFalse(Execution<bool>("contactValidAfterExecution"));
            StringAssert.Contains("rollback safety restoration failed", Execution<string>("rejectionReason"));
        }

        [Test]
        public void RotationContactFailureAndWorkspaceRejection_AreRejectedAndReasonCoded()
        {
            harness = CreateHarness(40f, 40f);
            harness.intersection.SkinCollider = null;
            harness.intersection.SkinLayer = 0;
            harness.intersection.Measure();

            bool contactAccepted = InvokeAction(Active2DAction.RotateClockwise);

            Assert.IsFalse(contactAccepted);
            Assert.IsTrue(Execution<bool>("rollbackAttempted"));
            Assert.IsTrue(Execution<bool>("safetyFailure"));
            StringAssert.Contains("rotation contact enforcement failed", Execution<string>("rejectionReason"));

            harness.intersection.SkinCollider = harness.contactCollider;
            SetPrivate("active2DWorkspaceOrigin", new Vector3(-3f, 0f, 0f));
            harness.controller.active2DLateralHalfExtentMm = 2.5f;
            harness.intersection.Measure();

            bool workspaceAccepted = InvokeAction(Active2DAction.RotateCounterClockwise);

            Assert.IsFalse(workspaceAccepted);
            Assert.IsTrue(Execution<bool>("rollbackAttempted"));
            Assert.IsFalse(Execution<bool>("workspaceValidAfterExecution"));
            StringAssert.Contains("rotation candidate failed safety validation", Execution<string>("rejectionReason"));
            StringAssert.Contains("outside the active workspace", Execution<string>("rejectionReason"));
        }

        [Test]
        public void InvalidContactPhaseHandoff_IsRejectedWithoutPhaseAdvanceOrOriginReset()
        {
            harness = CreateHarness(40f, 40f);
            harness.intersection.SkinCollider = null;
            harness.intersection.SkinLayer = 0;
            SetPrivate("active2DPhase", Active2DPhase.LongitudinalLocalization);
            Vector3 originalOrigin = new Vector3(1f, 0f, 1f);
            SetPrivate("active2DWorkspaceOrigin", originalOrigin);

            bool accepted = InvokeTerminalSafety(Active2DAction.PhaseHandoff, out string reason);

            Assert.IsFalse(accepted);
            Assert.AreEqual(Active2DPhase.LongitudinalLocalization, harness.controller.ActivePhase);
            Assert.AreEqual(originalOrigin, GetPrivate<Vector3>("active2DWorkspaceOrigin"));
            Assert.IsTrue(Execution<bool>("safetyFailure"));
            Assert.IsFalse(Execution<bool>("contactValidAfterExecution"));
            StringAssert.Contains("no hit", reason);
        }

        [Test]
        public void InvalidContactOrWorkspaceStop_CannotSetOverallPass()
        {
            harness = CreateHarness(40f, 40f);
            harness.intersection.SkinCollider = null;
            harness.intersection.SkinLayer = 0;

            bool contactAccepted = InvokeTerminalSafety(Active2DAction.Stop, out string contactReason);

            Assert.IsFalse(contactAccepted);
            Assert.IsFalse(Thoracic2DNavigation.IsTerminalAcceptanceValid(true, false, true));
            StringAssert.Contains("no hit", contactReason);

            harness.intersection.SkinCollider = harness.contactCollider;
            SetPrivate("active2DWorkspaceOrigin", new Vector3(-60f, 0f, 0f));
            harness.intersection.Measure();

            bool workspaceAccepted = InvokeTerminalSafety(Active2DAction.Stop, out string workspaceReason);

            Assert.IsFalse(workspaceAccepted);
            Assert.IsFalse(Thoracic2DNavigation.IsTerminalAcceptanceValid(true, true, false));
            Assert.IsFalse(Thoracic2DNavigation.IsTerminalAcceptanceValid(false, true, true));
            StringAssert.Contains("outside the active workspace", workspaceReason);
        }

        [Test]
        public void ValidPhaseHandoffAndStop_PreserveTerminalSuccessRegression()
        {
            harness = CreateHarness(40f, 40f);

            Assert.IsTrue(InvokeTerminalSafety(Active2DAction.PhaseHandoff, out string handoffReason));
            Assert.That(handoffReason, Is.Empty);
            Assert.IsTrue(Execution<bool>("accepted"));
            Assert.IsTrue(Execution<bool>("contactValidAfterExecution"));
            Assert.IsTrue(Execution<bool>("workspaceValidAfterExecution"));

            Assert.IsTrue(InvokeTerminalSafety(Active2DAction.Stop, out string stopReason));
            Assert.That(stopReason, Is.Empty);
            Assert.IsTrue(Thoracic2DNavigation.IsTerminalAcceptanceValid(true, true, true));
        }

        [Test]
        public void MovementTelemetry_DistinguishesRejectedMovementFromAcceptedZeroDisplacementTerminal()
        {
            Assert.That(Thoracic2DNavigation.MinimumMeaningfulProgressMm(3f), Is.EqualTo(0.3f).Within(0.0001f));
            Assert.IsFalse(Thoracic2DNavigation.HasMeaningfulProgress(3f, 0f));
            Assert.IsTrue(Thoracic2DNavigation.HasMeaningfulProgress(0f, 0f));

            Active2DRunTrace rejectedMove = new Active2DRunTrace
            {
                action = "LATERAL",
                accepted = false,
                requestedDisplacementMm = 3f,
                attemptedDisplacementMm = 3f,
                achievedDisplacementMm = 0f,
                contactValidAfterExecution = true,
                workspaceValidAfterExecution = true,
                rollbackAttempted = true,
                rollbackContactRestored = true,
                rejectionReason = "workspace boundary"
            };
            Active2DRunTrace acceptedStop = new Active2DRunTrace
            {
                action = "STOP",
                accepted = true,
                requestedDisplacementMm = 0f,
                achievedDisplacementMm = 0f,
                contactValidAfterExecution = true,
                workspaceValidAfterExecution = true,
                rejectionReason = string.Empty
            };

            Active2DRunTrace restoredMove = JsonUtility.FromJson<Active2DRunTrace>(JsonUtility.ToJson(rejectedMove));
            Active2DRunTrace restoredStop = JsonUtility.FromJson<Active2DRunTrace>(JsonUtility.ToJson(acceptedStop));

            Assert.IsFalse(restoredMove.accepted);
            Assert.IsTrue(restoredMove.rollbackAttempted);
            Assert.IsTrue(restoredMove.rollbackContactRestored);
            Assert.That(restoredMove.rejectionReason, Is.EqualTo("workspace boundary"));
            Assert.IsTrue(restoredStop.accepted);
            Assert.That(restoredStop.requestedDisplacementMm, Is.EqualTo(0f));
            Assert.That(restoredStop.achievedDisplacementMm, Is.EqualTo(0f));
            Assert.That(restoredStop.rejectionReason, Is.Empty);
        }

        [Test]
        public void ControllerSource_ValidatesTerminalBeforeResetAndWiresRotationWorkspaceSafety()
        {
            const string sourcePath = "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.Active2D.cs";
            string source = File.ReadAllText(sourcePath);
            int handoff = source.IndexOf("if (action == Active2DAction.PhaseHandoff)", StringComparison.Ordinal);
            int handoffValidation = source.IndexOf("ValidateActive2DTerminalSafety(action", handoff, StringComparison.Ordinal);
            int phaseTwoOriginReset = source.IndexOf("active2DWorkspaceOrigin = faceCenter.position", handoff, StringComparison.Ordinal);
            int stop = source.IndexOf("else if (action == Active2DAction.Stop)", handoff, StringComparison.Ordinal);
            int stopValidation = source.IndexOf("ValidateActive2DTerminalSafety(action", stop, StringComparison.Ordinal);
            int stopOverallPass = source.IndexOf("active2DResult.overallPass", stop, StringComparison.Ordinal);
            int rotation = source.IndexOf("private bool TryActive2DRotate", StringComparison.Ordinal);
            int rotationSafety = source.IndexOf("RefreshActive2DSafetyState", rotation, StringComparison.Ordinal);
            int rotationEnd = source.IndexOf("private bool IsActive2DWorkspaceValid", rotation, StringComparison.Ordinal);

            Assert.That(handoff, Is.GreaterThanOrEqualTo(0));
            Assert.That(handoffValidation, Is.GreaterThan(handoff));
            Assert.That(phaseTwoOriginReset, Is.GreaterThan(handoffValidation),
                "Phase 1 workspace must be checked before establishing the Phase 2 origin.");
            Assert.That(stopValidation, Is.GreaterThan(stop));
            Assert.That(stopOverallPass, Is.GreaterThan(stopValidation));
            Assert.That(rotationSafety, Is.GreaterThan(rotation));
            Assert.That(rotationSafety, Is.LessThan(rotationEnd));
        }

        [Test]
        public void ContactPredicate_RequiresHitAndConfiguredAbsoluteDepthTolerance()
        {
            Assert.IsTrue(Thoracic2DNavigation.IsContactValid(true, 0.5f, 0.5f));
            Assert.IsTrue(Thoracic2DNavigation.IsContactValid(true, -0.5f, 0.5f));
            Assert.IsFalse(Thoracic2DNavigation.IsContactValid(false, 0f, 0.5f));
            Assert.IsFalse(Thoracic2DNavigation.IsContactValid(true, 0.5001f, 0.5f));
            Assert.IsFalse(Thoracic2DNavigation.IsContactValid(true, -0.5001f, 0.5f));
        }

        private Harness CreateHarness(float surfaceWidth, float contactWidth)
        {
            Harness result = new Harness();
            result.skinObject = new GameObject("M01 surface skin");
            result.surfaceCollider = result.skinObject.AddComponent<BoxCollider>();
            result.surfaceCollider.center = new Vector3(0f, -0.5f, 0f);
            result.surfaceCollider.size = new Vector3(surfaceWidth, 1f, surfaceWidth);

            if (Mathf.Approximately(surfaceWidth, contactWidth))
            {
                result.contactSkinObject = result.skinObject;
                result.contactCollider = result.surfaceCollider;
            }
            else
            {
                result.contactSkinObject = new GameObject("M01 contact skin");
                result.contactCollider = result.contactSkinObject.AddComponent<BoxCollider>();
                result.contactCollider.center = new Vector3(0f, -0.5f, 0f);
                result.contactCollider.size = new Vector3(contactWidth, 1f, contactWidth);
            }

            result.probeObject = new GameObject("M01 probe root");
            GameObject faceObject = new GameObject("Face center");
            faceObject.transform.SetParent(result.probeObject.transform, false);
            result.face = faceObject.transform;
            GameObject rearObject = new GameObject("Rear center");
            rearObject.transform.SetParent(result.probeObject.transform, false);
            rearObject.transform.localPosition = Vector3.up;

            result.controllerObject = new GameObject("M01 controller");
            result.intersection = result.controllerObject.AddComponent<SkinIntersectionCompression>();
            result.intersection.CenterTransform = result.face;
            result.intersection.RearCenterTransform = rearObject.transform;
            result.intersection.SkinCollider = result.contactCollider;
            result.intersection.GridCols = 1;
            result.intersection.GridRows = 1;
            result.intersection.GridWidth_mm = 0f;
            result.intersection.GridHeight_mm = 0f;
            result.intersection.RayStartBackoff_mm = 10f;
            result.intersection.ExtraRayLength_mm = 10f;

            result.controller = result.controllerObject.AddComponent<ThoracicUSCoachController>();
            result.controller.probeRoot = result.probeObject.transform;
            result.controller.faceCenter = result.face;
            result.controller.rearCenter = rearObject.transform;
            result.controller.skinCollider = result.surfaceCollider;
            result.controller.skinIntersection = result.intersection;
            result.controller.maxPenetrationMm = 0.5f;
            result.controller.active2DStepMm = 3f;
            result.controller.active2DLongitudinalHalfExtentMm = 48f;
            result.controller.active2DLateralHalfExtentMm = 36f;

            harness = result;
            SetPrivate("active2DWorkspaceOrigin", Vector3.zero);
            SetPrivate("active2DCaudalAxis", Vector3.forward);
            SetPrivate("active2DLateralAxis", Vector3.right);
            SetPrivate("seedNormal", Vector3.up);
            Physics.SyncTransforms();
            result.intersection.Measure();
            Assert.IsTrue(result.intersection.AnyHit, "Synthetic fixture must begin in contact.");
            Assert.That(Mathf.Abs(result.intersection.SignedDepth), Is.LessThanOrEqualTo(0.0001f));
            return result;
        }

        private bool InvokeAction(Active2DAction action)
        {
            Physics.SyncTransforms();
            return (bool)InvokePrivate("TryActive2DAction", new object[] { action });
        }

        private bool InvokeTerminalSafety(Active2DAction action, out string reason)
        {
            Physics.SyncTransforms();
            object[] arguments = { action, null };
            bool result = (bool)InvokePrivate("ValidateActive2DTerminalSafety", arguments);
            reason = (string)arguments[1];
            return result;
        }

        private object InvokePrivate(string name, object[] arguments)
        {
            MethodInfo method = typeof(ThoracicUSCoachController).GetMethod(name, PrivateInstance);
            Assert.That(method, Is.Not.Null, "Missing production method " + name);
            return method.Invoke(harness.controller, arguments);
        }

        private T Execution<T>(string fieldName)
        {
            object execution = GetPrivate<object>("active2DLastActionExecution");
            FieldInfo field = execution.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public);
            Assert.That(field, Is.Not.Null, "Missing execution telemetry field " + fieldName);
            return (T)field.GetValue(execution);
        }

        private T GetPrivate<T>(string name)
        {
            FieldInfo field = typeof(ThoracicUSCoachController).GetField(name, PrivateInstance);
            Assert.That(field, Is.Not.Null, "Missing production field " + name);
            return (T)field.GetValue(harness.controller);
        }

        private void SetPrivate<T>(string name, T value)
        {
            FieldInfo field = typeof(ThoracicUSCoachController).GetField(name, PrivateInstance);
            Assert.That(field, Is.Not.Null, "Missing production field " + name);
            field.SetValue(harness.controller, value);
        }
    }
}
