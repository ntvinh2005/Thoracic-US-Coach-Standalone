using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using SMMARTS;
using UnityEngine;

namespace CSSALT.ThoracicUSCoach.Tests
{
    public sealed class AcquisitionGeometryRecoveryTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;

        private GameObject controllerObject;
        private GameObject probeObject;
        private GameObject skinObject;
        private ThoracicUSCoachController controller;
        private Transform face;

        [TearDown]
        public void TearDown()
        {
            if (controllerObject != null) UnityEngine.Object.DestroyImmediate(controllerObject);
            if (probeObject != null) UnityEngine.Object.DestroyImmediate(probeObject);
            if (skinObject != null) UnityEngine.Object.DestroyImmediate(skinObject);
        }

        [TestCase("TRANSVERSE_PARAMEDIAN_P6", 0f, 6f, "TRANSVERSE_PARAMEDIAN")]
        [TestCase("PARASAGITTAL_CENTER", 90f, 0f, "LONGITUDINAL_PARASAGITTAL")]
        public void ApprovedGeometryConfiguration_IsExplicit(
            string mode, float yaw, float lateral, string plane)
        {
            object config = Configuration(mode);
            Assert.That(Field<float>(config, "surfaceYawDeg"), Is.EqualTo(yaw));
            Assert.That(Field<float>(config, "lateralOffsetMm"), Is.EqualTo(lateral));
            Assert.That(Field<string>(config, "intendedScanPlane"), Is.EqualTo(plane));
        }

        [Test]
        public void UnsupportedGeometry_IsRejected()
        {
            TargetInvocationException error = Assert.Throws<TargetInvocationException>(
                () => Configuration("UNKNOWN"));
            Assert.That(error.InnerException, Is.TypeOf<ArgumentException>());
        }

        [TestCase(0f, 0f, 1f)]
        [TestCase(90f, 1f, 0f)]
        public void PhysicalLongitudinalMove_PreservesPlaneYawContactAndWorkspace(
            float yaw, float expectedCorridorAlignment, float expectedLateralAlignment)
        {
            CreateHarness();
            Assert.IsTrue(InvokePhysicalAction(Active2DAction.Caudal, yaw));
            Vector3 normal = Vector3.up;
            Vector3 scanWidth = Vector3.ProjectOnPlane(probeObject.transform.right, normal).normalized;
            Assert.That(Mathf.Abs(Vector3.Dot(scanWidth, Vector3.forward)),
                Is.EqualTo(expectedCorridorAlignment).Within(0.01f));
            Assert.That(Mathf.Abs(Vector3.Dot(scanWidth, Vector3.right)),
                Is.EqualTo(expectedLateralAlignment).Within(0.01f));
            Assert.That(MeasureSurfaceYaw(normal), Is.EqualTo(yaw).Within(0.01f));
            Assert.IsTrue(Execution<bool>("contactValidAfterExecution"));
            Assert.IsTrue(Execution<bool>("workspaceValidAfterExecution"));
            Assert.IsFalse(Execution<bool>("safetyFailure"));
        }

        [Test]
        public void ActorObservationSchema_ContainsNoGeometryOrAnatomicalPrivilege()
        {
            Type actorType = typeof(ThoracicUSCoachController).GetNestedType(
                "G2GeometryActorObservation", BindingFlags.NonPublic);
            Assert.That(actorType, Is.Not.Null);
            string[] names = actorType.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(item => item.Name).ToArray();
            foreach (string forbidden in new[]
            {
                "probePosition", "probeRotation", "diagnosticReferenceLevel",
                "evaluatorLongitudinalCoordinateMm", "surfaceYawDeg", "lateralOffsetMm",
                "provenance", "meshIdentity"
            })
                CollectionAssert.DoesNotContain(names, forbidden);
        }

        [Test]
        public void ActorTrajectorySchema_ContainsNoGeometryModeOrCommandedYaw()
        {
            Type actorType = typeof(ThoracicUSCoachController).GetNestedType(
                "G2GeometryActorTrajectory", BindingFlags.NonPublic);
            Assert.That(actorType, Is.Not.Null);
            string[] names = actorType.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(item => item.Name).ToArray();
            CollectionAssert.DoesNotContain(names, "mode");
            CollectionAssert.DoesNotContain(names, "surfaceYawDeg");
            CollectionAssert.DoesNotContain(names, "lateralOffsetMm");
        }

        private static object Configuration(string mode)
        {
            MethodInfo method = typeof(ThoracicUSCoachController).GetMethod(
                "G2GeometryConfiguration", PrivateStatic);
            Assert.That(method, Is.Not.Null);
            return method.Invoke(null, new object[] { mode });
        }

        private static T Field<T>(object value, string name)
        {
            FieldInfo field = value.GetType().GetField(
                name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            return (T)field.GetValue(value);
        }

        private void CreateHarness()
        {
            skinObject = new GameObject("G2 geometry surface skin");
            BoxCollider surface = skinObject.AddComponent<BoxCollider>();
            surface.center = new Vector3(0f, -0.5f, 0f);
            surface.size = new Vector3(80f, 1f, 80f);

            probeObject = new GameObject("G2 geometry probe root");
            GameObject faceObject = new GameObject("Face center");
            faceObject.transform.SetParent(probeObject.transform, false);
            face = faceObject.transform;
            GameObject rearObject = new GameObject("Rear center");
            rearObject.transform.SetParent(probeObject.transform, false);
            rearObject.transform.localPosition = Vector3.up;

            controllerObject = new GameObject("G2 geometry controller");
            SkinIntersectionCompression intersection =
                controllerObject.AddComponent<SkinIntersectionCompression>();
            intersection.CenterTransform = face;
            intersection.RearCenterTransform = rearObject.transform;
            intersection.SkinCollider = surface;
            intersection.GridCols = 1;
            intersection.GridRows = 1;
            intersection.GridWidth_mm = 0f;
            intersection.GridHeight_mm = 0f;
            intersection.RayStartBackoff_mm = 10f;
            intersection.ExtraRayLength_mm = 10f;

            controller = controllerObject.AddComponent<ThoracicUSCoachController>();
            controller.probeRoot = probeObject.transform;
            controller.faceCenter = face;
            controller.rearCenter = rearObject.transform;
            controller.skinCollider = surface;
            controller.skinIntersection = intersection;
            controller.maxPenetrationMm = 0.5f;
            controller.active2DStepMm = 3f;
            controller.active2DLongitudinalHalfExtentMm = 48f;
            controller.active2DLateralHalfExtentMm = 36f;
            SetPrivate("active2DWorkspaceOrigin", Vector3.zero);
            SetPrivate("active2DCaudalAxis", Vector3.forward);
            SetPrivate("active2DLateralAxis", Vector3.right);
            SetPrivate("seedNormal", Vector3.up);
            SetPrivate("seedProbeRotation", Quaternion.identity);
            SetPrivate("active2DCanonicalProbeRotation", Quaternion.identity);
            Physics.SyncTransforms();
            intersection.Measure();
            Assert.IsTrue(intersection.AnyHit);
        }

        private bool InvokePhysicalAction(Active2DAction action, float yaw)
        {
            Physics.SyncTransforms();
            MethodInfo method = typeof(ThoracicUSCoachController).GetMethod(
                "TryG2GeometryPhysicalAction", PrivateInstance);
            Assert.That(method, Is.Not.Null);
            return (bool)method.Invoke(controller, new object[] { action, yaw });
        }

        private float MeasureSurfaceYaw(Vector3 normal)
        {
            MethodInfo method = typeof(ThoracicUSCoachController).GetMethod(
                "MeasureSweepCountSurfaceYawDeg", PrivateInstance);
            Assert.That(method, Is.Not.Null);
            return (float)method.Invoke(controller, new object[] { normal });
        }

        private T Execution<T>(string fieldName)
        {
            FieldInfo executionField = typeof(ThoracicUSCoachController).GetField(
                "active2DLastActionExecution", PrivateInstance);
            object execution = executionField?.GetValue(controller);
            Assert.That(execution, Is.Not.Null);
            FieldInfo field = execution.GetType().GetField(
                fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            return (T)field.GetValue(execution);
        }

        private void SetPrivate<T>(string name, T value)
        {
            FieldInfo field = typeof(ThoracicUSCoachController).GetField(name, PrivateInstance);
            Assert.That(field, Is.Not.Null);
            field.SetValue(controller, value);
        }
    }
}
