using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SMMARTS;
using UnityEngine;

namespace CSSALT.ThoracicUSCoach
{
    public sealed partial class ThoracicUSCoachController
    {
        private const string BoundaryFeasibilityRoot =
            "Artifacts/ThoracicUSCoach/boundary_t7/M0_BOUNDARY_FEASIBILITY_20261030";
        private const float BoundaryPreflightStepMm = 3f;
        private const float BoundaryRequiredOutsideMarginMm = 15f;
        private const int BoundaryMaximumPreflightStepsPerDirection = 240;

        [Serializable]
        private sealed class BoundaryRenderableBounds
        {
            public string objectName = string.Empty;
            public string renderType = string.Empty;
            public bool showOnUltrasound;
            public bool boneLike;
            public Vector3 boundsCenter;
            public Vector3 boundsSize;
            public float minimumLongitudinalMm;
            public float maximumLongitudinalMm;
        }

        [Serializable]
        private sealed class BoundarySurfaceSample
        {
            public int step;
            public float signedLongitudinalMm;
            public float accumulatedSurfaceTravelMm;
            public Vector3 contactPoint;
            public Vector3 surfaceNormal;
            public float stepDistanceMm;
            public float normalChangeDeg;
        }

        [Serializable]
        private sealed class BoundaryGeometryPreflight
        {
            public string contract = "thoracic_boundary_feasibility_m0_geometry_v1";
            public string generatedUtc = string.Empty;
            public string status = "BLOCKED";
            public string scene = string.Empty;
            public string warning = "Evaluator-only geometry; never an actor input.";
            public float stepMm;
            public float requiredOutsideMarginMm;
            public int maximumStepsPerDirection;
            public Vector3 corridorOrigin;
            public Vector3 corridorAxis;
            public Vector3 skinBoundsCenter;
            public Vector3 skinBoundsSize;
            public float skinBoundsMinimumLongitudinalMm;
            public float skinBoundsMaximumLongitudinalMm;
            public float reachableCranialLongitudinalMm;
            public float reachableCaudalLongitudinalMm;
            public float boneMinimumLongitudinalMm;
            public float boneMaximumLongitudinalMm;
            public float cranialOutsideMarginMm;
            public float caudalOutsideMarginMm;
            public bool cranialOutsideReachable;
            public bool caudalOutsideReachable;
            public bool bothEndsReachable;
            public float configuredActive2DLongitudinalHalfExtentMm;
            public bool configuredWorkspaceWouldRestrictAudit;
            public string rendererExpectation = string.Empty;
            public string decision = string.Empty;
            public List<string> limitations = new List<string>();
            public List<BoundaryRenderableBounds> renderables = new List<BoundaryRenderableBounds>();
            public List<BoundarySurfaceSample> cranialSurface = new List<BoundarySurfaceSample>();
            public List<BoundarySurfaceSample> caudalSurface = new List<BoundarySurfaceSample>();
        }

        public string AuditBoundaryFeasibilityGeometry()
        {
            AutoWire();
            if (!ValidateReferences(out string referenceError))
                throw new InvalidOperationException(referenceError);
            if (skinCollider == null)
                throw new InvalidOperationException("Boundary preflight requires the exact skin collider");

            SweepCountReferenceSet references = BuildSweepCountReferences();
            if (references.references.Count != 7
                || references.references.Any(item => !item.surfaceProjectionValid))
                throw new InvalidOperationException("Boundary preflight requires valid evaluator-only T2-T8 projections");

            Vector3 t2 = references.references[0].contactPoint;
            Vector3 t8 = references.references[6].contactPoint;
            Vector3 axis = (t8 - t2).normalized;
            Vector3 origin = 0.5f * (t2 + t8);
            if (axis.sqrMagnitude < 0.5f || !FindSurfaceAt(origin, Vector3.zero, out RaycastHit originHit))
                throw new InvalidOperationException("Boundary preflight could not establish a central skin corridor origin");
            origin = originHit.point;

            BoundaryGeometryPreflight audit = new BoundaryGeometryPreflight
            {
                generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                scene = gameObject.scene.path,
                stepMm = BoundaryPreflightStepMm,
                requiredOutsideMarginMm = BoundaryRequiredOutsideMarginMm,
                maximumStepsPerDirection = BoundaryMaximumPreflightStepsPerDirection,
                corridorOrigin = origin,
                corridorAxis = axis,
                skinBoundsCenter = skinCollider.bounds.center,
                skinBoundsSize = skinCollider.bounds.size,
                configuredActive2DLongitudinalHalfExtentMm = active2DLongitudinalHalfExtentMm
            };

            ProjectBounds(skinCollider.bounds, origin, axis,
                out audit.skinBoundsMinimumLongitudinalMm,
                out audit.skinBoundsMaximumLongitudinalMm);
            audit.cranialSurface = TraceBoundarySurface(
                originHit, -axis, origin, axis, BoundaryMaximumPreflightStepsPerDirection);
            audit.caudalSurface = TraceBoundarySurface(
                originHit, axis, origin, axis, BoundaryMaximumPreflightStepsPerDirection);
            audit.reachableCranialLongitudinalMm = audit.cranialSurface.Min(
                item => item.signedLongitudinalMm);
            audit.reachableCaudalLongitudinalMm = audit.caudalSurface.Max(
                item => item.signedLongitudinalMm);

            UltrasoundVisibility[] visibleObjects = Resources.FindObjectsOfTypeAll<UltrasoundVisibility>()
                .Where(item => item != null && item.gameObject.scene == gameObject.scene
                    && item.ShowOnUltrasound)
                .OrderBy(item => item.gameObject.name, StringComparer.Ordinal)
                .ToArray();
            foreach (UltrasoundVisibility visibility in visibleObjects)
            {
                Renderer[] renderers = visibility.GetComponentsInChildren<Renderer>(true);
                if (renderers.Length == 0) continue;
                Bounds bounds = renderers[0].bounds;
                for (int index = 1; index < renderers.Length; index++)
                    bounds.Encapsulate(renderers[index].bounds);
                ProjectBounds(bounds, origin, axis, out float minimum, out float maximum);
                bool boneLike = visibility.Type == UltrasoundVisibility.RenderType.Shadow
                    || visibility.CompareTag("Bone") || visibility.CompareTag("Ligament")
                    || visibility.gameObject.name.StartsWith("LF T", StringComparison.Ordinal);
                audit.renderables.Add(new BoundaryRenderableBounds
                {
                    objectName = visibility.gameObject.name,
                    renderType = visibility.Type.ToString(),
                    showOnUltrasound = visibility.ShowOnUltrasound,
                    boneLike = boneLike,
                    boundsCenter = bounds.center,
                    boundsSize = bounds.size,
                    minimumLongitudinalMm = minimum,
                    maximumLongitudinalMm = maximum
                });
            }

            BoundaryRenderableBounds[] bone = audit.renderables.Where(item => item.boneLike).ToArray();
            if (bone.Length == 0)
                throw new InvalidOperationException("No ultrasound-visible bone-like renderable bounds were found");
            audit.boneMinimumLongitudinalMm = bone.Min(item => item.minimumLongitudinalMm);
            audit.boneMaximumLongitudinalMm = bone.Max(item => item.maximumLongitudinalMm);
            audit.cranialOutsideMarginMm = audit.boneMinimumLongitudinalMm
                - audit.reachableCranialLongitudinalMm;
            audit.caudalOutsideMarginMm = audit.reachableCaudalLongitudinalMm
                - audit.boneMaximumLongitudinalMm;
            audit.cranialOutsideReachable = audit.cranialOutsideMarginMm
                >= BoundaryRequiredOutsideMarginMm;
            audit.caudalOutsideReachable = audit.caudalOutsideMarginMm
                >= BoundaryRequiredOutsideMarginMm;
            audit.bothEndsReachable = audit.cranialOutsideReachable && audit.caudalOutsideReachable;
            float requiredWorkspace = Mathf.Max(
                Mathf.Abs(audit.reachableCranialLongitudinalMm),
                Mathf.Abs(audit.reachableCaudalLongitudinalMm));
            audit.configuredWorkspaceWouldRestrictAudit = active2DLongitudinalHalfExtentMm
                + 0.001f < requiredWorkspace;
            audit.rendererExpectation = audit.renderables.Any(item => !item.boneLike)
                ? "Low-content rather than zero-intensity frames are possible because non-bone ultrasound-visible renderables extend through the scene."
                : "Outside bone-like bounds may still contain renderer background and noise; zero intensity is not assumed.";
            audit.limitations.Add(
                "Renderable AABB projection is a conservative evaluator-only extent, not an image-derived boundary.");
            audit.limitations.Add(
                "The combined Skeleton does not provide rib-versus-lamina-versus-vertebra provenance.");
            if (audit.configuredWorkspaceWouldRestrictAudit)
                audit.limitations.Add(
                    "The serialized Active2D +/-48 mm workspace would stop this audit before the physical skin corridor ends; M0 must use an isolated temporary diagnostic extent and restore it afterward.");
            audit.status = audit.bothEndsReachable ? "PASS" : "BLOCKED";
            audit.decision = audit.bothEndsReachable
                ? "Both connected skin directions extend at least five 3 mm steps beyond projected ultrasound-visible bone bounds; an isolated physical M0 pilot is geometrically supportable."
                : "M0_PHYSICAL_REACHABILITY_BLOCKED: the connected skin corridor does not extend sufficiently beyond ultrasound-visible bone bounds at both ends.";

            string output = Path.Combine(BoundaryFeasibilityRoot, "preflight_geometry.json");
            WriteSweepCountJson(output, audit);
            return ProjectRelativePath(Path.GetFullPath(output));
        }

        private List<BoundarySurfaceSample> TraceBoundarySurface(
            RaycastHit initial,
            Vector3 referenceDirection,
            Vector3 origin,
            Vector3 corridorAxis,
            int maximumSteps)
        {
            List<BoundarySurfaceSample> samples = new List<BoundarySurfaceSample>();
            RaycastHit current = initial;
            float travel = 0f;
            samples.Add(new BoundarySurfaceSample
            {
                step = 0,
                signedLongitudinalMm = Vector3.Dot(current.point - origin, corridorAxis),
                accumulatedSurfaceTravelMm = 0f,
                contactPoint = current.point,
                surfaceNormal = current.normal
            });
            for (int step = 1; step <= maximumSteps; step++)
            {
                Vector3 tangent = Vector3.ProjectOnPlane(referenceDirection, current.normal).normalized;
                if (tangent.sqrMagnitude < 0.5f) break;
                Vector3 proposed = current.point + tangent * BoundaryPreflightStepMm;
                if (!FindSurfaceAt(proposed, current.normal, out RaycastHit next)) break;
                float distance = Vector3.Distance(current.point, next.point);
                float normalChange = Vector3.Angle(current.normal, next.normal);
                if (Thoracic2DNavigation.IsTeleportLike(
                        distance, BoundaryPreflightStepMm, 2.5f)
                    || !Thoracic2DNavigation.HasMeaningfulProgress(
                        BoundaryPreflightStepMm, distance)
                    || normalChange > 35f)
                    break;
                travel += distance;
                samples.Add(new BoundarySurfaceSample
                {
                    step = step,
                    signedLongitudinalMm = Vector3.Dot(next.point - origin, corridorAxis),
                    accumulatedSurfaceTravelMm = travel,
                    contactPoint = next.point,
                    surfaceNormal = next.normal,
                    stepDistanceMm = distance,
                    normalChangeDeg = normalChange
                });
                current = next;
            }
            return samples;
        }

        private static void ProjectBounds(
            Bounds bounds,
            Vector3 origin,
            Vector3 axis,
            out float minimum,
            out float maximum)
        {
            minimum = float.PositiveInfinity;
            maximum = float.NegativeInfinity;
            Vector3 extent = bounds.extents;
            for (int x = -1; x <= 1; x += 2)
            for (int y = -1; y <= 1; y += 2)
            for (int z = -1; z <= 1; z += 2)
            {
                Vector3 corner = bounds.center + Vector3.Scale(extent, new Vector3(x, y, z));
                float coordinate = Vector3.Dot(corner - origin, axis);
                minimum = Mathf.Min(minimum, coordinate);
                maximum = Mathf.Max(maximum, coordinate);
            }
        }
    }
}
