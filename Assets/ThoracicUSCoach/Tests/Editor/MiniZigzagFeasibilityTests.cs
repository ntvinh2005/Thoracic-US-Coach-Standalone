using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using SMMARTS;
using UnityEngine;

namespace CSSALT.ThoracicUSCoach.Tests
{
    public sealed class MiniZigzagFeasibilityTests
    {
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
        private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;

        private GameObject controllerObject;
        private GameObject probeObject;
        private GameObject skinObject;
        private GameObject managerObject;
        private ThoracicUSCoachController controller;
        private Transform face;

        [TearDown]
        public void TearDown()
        {
            if (controllerObject != null) UnityEngine.Object.DestroyImmediate(controllerObject);
            if (probeObject != null) UnityEngine.Object.DestroyImmediate(probeObject);
            if (skinObject != null) UnityEngine.Object.DestroyImmediate(skinObject);
            if (managerObject != null) UnityEngine.Object.DestroyImmediate(managerObject);
        }

        [Test]
        public void FixedProfiles_VisitFiveUniqueOffsetsAndAlternateFirstDirection()
        {
            float[] even = ProfileOffsets(0);
            float[] odd = ProfileOffsets(1);

            CollectionAssert.AreEqual(new[] { 0f, -3f, -6f, -3f, 0f, 3f, 6f, 3f, 0f }, even);
            CollectionAssert.AreEqual(new[] { 0f, 3f, 6f, 3f, 0f, -3f, -6f, -3f, 0f }, odd);
            CollectionAssert.AreEquivalent(new[] { -6f, -3f, 0f, 3f, 6f }, even.Distinct().ToArray());
            Assert.That(even.Last(), Is.EqualTo(0f));
            Assert.That(odd.Last(), Is.EqualTo(0f));
        }

        [TestCase(-3f, "ZM3")]
        [TestCase(0f, "Z0")]
        [TestCase(3f, "ZP3")]
        public void YawTag_IsUnambiguousForApprovedEpisodes(float yawDeg, string expected)
        {
            MethodInfo method = typeof(ThoracicUSCoachController).GetMethod(
                "MiniZigzagYawTag", PrivateStatic);
            Assert.That(method, Is.Not.Null);
            Assert.That(method.Invoke(null, new object[] { yawDeg }), Is.EqualTo(expected));
        }

        [TestCase(-3f)]
        [TestCase(3f)]
        public void PhysicalLateralProfile_PreservesYawContactWorkspaceAndCenterReturn(float yawDeg)
        {
            CreateHarness();
            float[] offsets = ProfileOffsets(0);
            for (int index = 1; index < offsets.Length; index++)
            {
                Active2DAction action = offsets[index] > offsets[index - 1]
                    ? Active2DAction.Lateral : Active2DAction.Medial;
                Assert.IsTrue(InvokePhysicalAction(action, yawDeg), "move " + index);
                Assert.That(face.position.x, Is.EqualTo(offsets[index]).Within(0.001f));
                Assert.That(MeasureSurfaceYaw(Vector3.up), Is.EqualTo(yawDeg).Within(0.01f));
                Assert.IsTrue(Execution<bool>("contactValidAfterExecution"));
                Assert.IsTrue(Execution<bool>("workspaceValidAfterExecution"));
                Assert.IsFalse(Execution<bool>("safetyFailure"));
            }
            Assert.That(Vector3.Distance(face.position, Vector3.zero), Is.LessThan(0.001f));
        }

        [Test]
        public void ActorObservationSchema_DoesNotContainWorldPoseLevelOrProvenance()
        {
            Type actorType = typeof(ThoracicUSCoachController).GetNestedType(
                "MiniZigzagActorObservation", BindingFlags.NonPublic);
            Assert.That(actorType, Is.Not.Null);
            string[] fieldNames = actorType.GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(item => item.Name).ToArray();

            CollectionAssert.DoesNotContain(fieldNames, "probePosition");
            CollectionAssert.DoesNotContain(fieldNames, "probeRotation");
            CollectionAssert.DoesNotContain(fieldNames, "diagnosticReferenceLevel");
            CollectionAssert.DoesNotContain(fieldNames, "corridorCoordinateMm");
            CollectionAssert.DoesNotContain(fieldNames, "provenance");
        }

        [Test]
        public void ProvenanceLookup_RequiresExactPoseEpochAndScanSerial()
        {
            managerObject = new GameObject("Mini zigzag provenance manager");
            UltrasoundManager manager = managerObject.AddComponent<UltrasoundManager>();
            UltrasoundManager.PublishedScanProvenanceSnapshot snapshot =
                new UltrasoundManager.PublishedScanProvenanceSnapshot
                {
                    poseEpoch = 41,
                    producerUnityFrame = 12,
                    producerScanSerial = 73,
                    beamCount = 256
                };
            typeof(UltrasoundManager).GetProperty(
                    "LastPublishedScanProvenance", BindingFlags.Instance | BindingFlags.Public)
                ?.GetSetMethod(true)?.Invoke(manager, new object[] { snapshot });

            Assert.IsTrue(manager.TryGetPublishedScanProvenance(41, 73, out var exact));
            Assert.AreSame(snapshot, exact);
            Assert.IsFalse(manager.TryGetPublishedScanProvenance(40, 73, out _));
            Assert.IsFalse(manager.TryGetPublishedScanProvenance(41, 72, out _));
        }

        [Test]
        public void FreshCaptureProvenance_IsOptInAndDefaultsOffForExistingModes()
        {
            MethodInfo method = typeof(ThoracicUSCoachController).GetMethod(
                "CaptureDirectProducerFreshFrame", PrivateInstance);
            Assert.That(method, Is.Not.Null);
            ParameterInfo provenance = method.GetParameters().Last();
            Assert.That(provenance.Name, Is.EqualTo("capturePrimaryProvenance"));
            Assert.IsTrue(provenance.HasDefaultValue);
            Assert.That(provenance.DefaultValue, Is.EqualTo(false));
        }

        private void CreateHarness()
        {
            skinObject = new GameObject("Mini zigzag surface skin");
            BoxCollider surface = skinObject.AddComponent<BoxCollider>();
            surface.center = new Vector3(0f, -0.5f, 0f);
            surface.size = new Vector3(40f, 1f, 40f);

            probeObject = new GameObject("Mini zigzag probe root");
            GameObject faceObject = new GameObject("Face center");
            faceObject.transform.SetParent(probeObject.transform, false);
            face = faceObject.transform;
            GameObject rearObject = new GameObject("Rear center");
            rearObject.transform.SetParent(probeObject.transform, false);
            rearObject.transform.localPosition = Vector3.up;

            controllerObject = new GameObject("Mini zigzag controller");
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
            controller.active2DLateralHalfExtentMm = 9f;
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

        private static float[] ProfileOffsets(int index)
        {
            MethodInfo method = typeof(ThoracicUSCoachController).GetMethod(
                "MiniZigzagProfileOffsets", PrivateStatic);
            Assert.That(method, Is.Not.Null);
            return (float[])method.Invoke(null, new object[] { index });
        }

        private bool InvokePhysicalAction(Active2DAction action, float yawDeg)
        {
            Physics.SyncTransforms();
            MethodInfo method = typeof(ThoracicUSCoachController).GetMethod(
                "TryMiniZigzagPhysicalAction", PrivateInstance);
            Assert.That(method, Is.Not.Null);
            return (bool)method.Invoke(controller, new object[] { action, yawDeg });
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
