using System;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace CSSALT.ThoracicUSCoach.Tests
{
    public sealed class BoundaryFeasibilityTests
    {
        private const BindingFlags PrivateStatic = BindingFlags.Static | BindingFlags.NonPublic;

        [Test]
        public void DiagnosticWorkspace_ExactlyCoversAsymmetricValidatedCorridor()
        {
            object[] arguments = { -91.839f, 97.602f, 0f, 0f };
            Method("CalculateM0BDiagnosticWorkspace").Invoke(null, arguments);
            float center = (float)arguments[2];
            float halfExtent = (float)arguments[3];

            Assert.That(center - halfExtent, Is.EqualTo(-91.839f).Within(0.001f));
            Assert.That(center + halfExtent, Is.EqualTo(97.602f).Within(0.001f));
            Assert.That(halfExtent, Is.GreaterThan(48f));
        }

        [TestCase(0f, 10f)]
        [TestCase(-10f, 0f)]
        [TestCase(10f, -10f)]
        public void DiagnosticWorkspace_RejectsNonTwoSidedOrUnorderedLimits(
            float minimum, float maximum)
        {
            object[] arguments = { minimum, maximum, 0f, 0f };
            TargetInvocationException error = Assert.Throws<TargetInvocationException>(
                () => Method("CalculateM0BDiagnosticWorkspace").Invoke(null, arguments));
            Assert.That(error.InnerException, Is.TypeOf<ArgumentException>());
        }

        [Test]
        public void ValidDarkUltrasoundFrame_IsNotRejectedAsInvalid()
        {
            Texture2D image = new Texture2D(32, 32, TextureFormat.RGBA32, false, true);
            image.SetPixels(Enumerable.Repeat(Color.black, 32 * 32).ToArray());
            image.Apply(false, false);
            byte[] png = image.EncodeToPNG();
            try
            {
                bool valid = (bool)Method("M0BFrameIntegrityValid")
                    .Invoke(null, new object[] { image, png });
                Assert.IsTrue(valid, "Low variance is a visual state, not an integrity failure.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(image);
            }
        }

        [Test]
        public void MagentaRendererArtifact_IsRejected()
        {
            Texture2D image = new Texture2D(32, 32, TextureFormat.RGBA32, false, true);
            image.SetPixels(Enumerable.Repeat(Color.magenta, 32 * 32).ToArray());
            image.Apply(false, false);
            byte[] png = image.EncodeToPNG();
            try
            {
                bool valid = (bool)Method("M0BFrameIntegrityValid")
                    .Invoke(null, new object[] { image, png });
                Assert.IsFalse(valid);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(image);
            }
        }

        [Test]
        public void ActorTrajectorySchemas_ExcludePrivilegedGeometryAndAnatomy()
        {
            Type controller = typeof(ThoracicUSCoachController);
            foreach (string nestedName in new[] { "M0BActorObservation", "M0BActorTrajectory", "M0BFrameManifest" })
            {
                Type nested = controller.GetNestedType(nestedName, BindingFlags.NonPublic);
                Assert.That(nested, Is.Not.Null);
                string[] fields = nested.GetFields(
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Select(item => item.Name.ToLowerInvariant()).ToArray();
                foreach (string forbidden in new[]
                {
                    "probeposition", "proberotation", "contactpoint", "surfacenormal",
                    "corridorcoordinate", "evaluatorlongitudinal", "diagnosticreferencelevel",
                    "anatomy", "mesh", "t2", "t7", "t8", "provenance"
                })
                    Assert.IsFalse(fields.Any(item => item.Contains(forbidden)),
                        nestedName + " leaked " + forbidden);
            }
        }

        [Test]
        public void VisualPilotSource_RestoresWorkspaceAndDoesNotUseAabbAsStopRule()
        {
            string source = System.IO.File.ReadAllText(
                "Assets/ThoracicUSCoach/Runtime/ThoracicUSCoachController.VisualBoundaryFeasibility.cs");
            StringAssert.Contains("finally", source);
            StringAssert.Contains(
                "active2DLongitudinalHalfExtentMm = originalLongitudinalExtent", source);
            StringAssert.Contains("active2DWorkspaceOrigin = originalWorkspaceOrigin", source);
            StringAssert.Contains("skeletonAabbExcludedFromStoppingRule = true", source);
            StringAssert.DoesNotContain("boneMinimumLongitudinalMm", source);
            StringAssert.DoesNotContain("boneMaximumLongitudinalMm", source);
        }

        private static MethodInfo Method(string name)
        {
            MethodInfo method = typeof(ThoracicUSCoachController).GetMethod(name, PrivateStatic);
            Assert.That(method, Is.Not.Null, "Missing production method " + name);
            return method;
        }
    }
}
