using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace CSSALT.ThoracicUSCoach.Tests
{
    public sealed class SweepCountMilestoneTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        private sealed class Harness
        {
            public GameObject controllerObject;
            public GameObject probeObject;
            public GameObject skinObject;
            public ThoracicUSCoachController controller;
            public Transform face;
        }

        private Harness harness;

        [TearDown]
        public void TearDown()
        {
            if (harness == null) return;
            if (harness.controllerObject != null) Object.DestroyImmediate(harness.controllerObject);
            if (harness.probeObject != null) Object.DestroyImmediate(harness.probeObject);
            if (harness.skinObject != null) Object.DestroyImmediate(harness.skinObject);
            harness = null;
        }

        [Test]
        public void SweepCountCaudalMoves_PreserveMeasuredSurfaceRelativeYaw()
        {
            harness = CreateHarness();

            Assert.IsTrue(InvokeSweepMove(3f));
            float firstYaw = MeasureSurfaceYaw(Vector3.up);
            Assert.IsTrue(InvokeSweepMove(3f));
            float secondYaw = MeasureSurfaceYaw(Vector3.up);

            Assert.That(firstYaw, Is.EqualTo(3f).Within(0.01f));
            Assert.That(secondYaw, Is.EqualTo(3f).Within(0.01f));
            Assert.That(harness.face.position.z, Is.EqualTo(6f).Within(0.001f));
        }

        [Test]
        public void ExistingActive2DCaudalMove_KeepsZeroYawBehavior()
        {
            harness = CreateHarness();
            harness.probeObject.transform.rotation = Quaternion.AngleAxis(3f, Vector3.up);

            Assert.IsTrue(InvokeActive2DAction(Active2DAction.Caudal));

            Assert.That(MeasureSurfaceYaw(Vector3.up), Is.EqualTo(0f).Within(0.01f));
        }

        [Test]
        public void SurfaceYawMeasurement_UsesAchievedProbeRotation()
        {
            harness = CreateHarness();
            harness.probeObject.transform.rotation = Quaternion.AngleAxis(-2.5f, Vector3.up);

            float measuredYaw = MeasureSurfaceYaw(Vector3.up);

            Assert.That(measuredYaw, Is.EqualTo(-2.5f).Within(0.01f));
        }

        private Harness CreateHarness()
        {
            Harness result = new Harness();
            result.skinObject = new GameObject("M15 surface skin");
            BoxCollider surface = result.skinObject.AddComponent<BoxCollider>();
            surface.center = new Vector3(0f, -0.5f, 0f);
            surface.size = new Vector3(40f, 1f, 40f);

            result.probeObject = new GameObject("M15 probe root");
            GameObject faceObject = new GameObject("Face center");
            faceObject.transform.SetParent(result.probeObject.transform, false);
            result.face = faceObject.transform;
            GameObject rearObject = new GameObject("Rear center");
            rearObject.transform.SetParent(result.probeObject.transform, false);
            rearObject.transform.localPosition = Vector3.up;

            result.controllerObject = new GameObject("M15 controller");
            SkinIntersectionCompression intersection =
                result.controllerObject.AddComponent<SkinIntersectionCompression>();
            intersection.CenterTransform = result.face;
            intersection.RearCenterTransform = rearObject.transform;
            intersection.SkinCollider = surface;
            intersection.GridCols = 1;
            intersection.GridRows = 1;
            intersection.GridWidth_mm = 0f;
            intersection.GridHeight_mm = 0f;
            intersection.RayStartBackoff_mm = 10f;
            intersection.ExtraRayLength_mm = 10f;

            result.controller = result.controllerObject.AddComponent<ThoracicUSCoachController>();
            result.controller.probeRoot = result.probeObject.transform;
            result.controller.faceCenter = result.face;
            result.controller.rearCenter = rearObject.transform;
            result.controller.skinCollider = surface;
            result.controller.skinIntersection = intersection;
            result.controller.maxPenetrationMm = 0.5f;
            result.controller.active2DStepMm = 3f;
            result.controller.active2DLongitudinalHalfExtentMm = 48f;
            result.controller.active2DLateralHalfExtentMm = 36f;

            harness = result;
            SetPrivate("active2DWorkspaceOrigin", Vector3.zero);
            SetPrivate("active2DCaudalAxis", Vector3.forward);
            SetPrivate("active2DLateralAxis", Vector3.right);
            SetPrivate("seedNormal", Vector3.up);
            SetPrivate("seedProbeRotation", Quaternion.identity);
            SetPrivate("active2DCanonicalProbeRotation", Quaternion.identity);
            Physics.SyncTransforms();
            intersection.Measure();
            Assert.IsTrue(intersection.AnyHit, "Synthetic fixture must begin in contact.");
            return result;
        }

        private bool InvokeSweepMove(float yawDeg)
        {
            Physics.SyncTransforms();
            return (bool)InvokePrivate("TrySweepCountCaudalAction", new object[] { yawDeg });
        }

        private bool InvokeActive2DAction(Active2DAction action)
        {
            Physics.SyncTransforms();
            return (bool)InvokePrivate("TryActive2DAction", new object[] { action });
        }

        private float MeasureSurfaceYaw(Vector3 normal)
        {
            return (float)InvokePrivate("MeasureSweepCountSurfaceYawDeg", new object[] { normal });
        }

        private object InvokePrivate(string name, object[] arguments)
        {
            MethodInfo method = typeof(ThoracicUSCoachController).GetMethod(name, PrivateInstance);
            Assert.That(method, Is.Not.Null, "Missing production method " + name);
            return method.Invoke(harness.controller, arguments);
        }

        private void SetPrivate<T>(string name, T value)
        {
            FieldInfo field = typeof(ThoracicUSCoachController).GetField(name, PrivateInstance);
            Assert.That(field, Is.Not.Null, "Missing production field " + name);
            field.SetValue(harness.controller, value);
        }
    }
}
