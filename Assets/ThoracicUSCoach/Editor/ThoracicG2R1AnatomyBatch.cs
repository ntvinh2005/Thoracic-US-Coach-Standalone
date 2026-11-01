using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CSSALT.ThoracicUSCoach.Editor
{
    public static class ThoracicG2R1AnatomyBatch
    {
        private const string OutputRoot =
            "Artifacts/ThoracicUSCoach/sweep_count/G2_R1_ANATOMICAL_REFERENCE_RENDERER_RECOVERY_20261101";

        [Serializable]
        private sealed class MeshComponentRecord
        {
            public int componentId;
            public int vertexCount;
            public int triangleCount;
            public float surfaceAreaMm2;
            public Vector3 worldBoundsCenter;
            public Vector3 worldBoundsSize;
            public Vector3 worldCentroid;
            public float absoluteLateralCenterMm;
            public string geometryOnlyRole;
            public List<Vector3> sampledWorldVertices = new();
        }

        [Serializable]
        private sealed class ReferenceRecord
        {
            public string level;
            public Vector3 labelPosition;
            public string labelEvidenceCategory = "LABEL_DERIVED_APPROXIMATE_REFERENCE";
            public string ligamentObject;
            public Vector3 ligamentBoundsCenter;
            public Vector3 ligamentBoundsSize;
            public int nearestSkeletonComponentId;
            public float labelToNearestSkeletonVertexMm;
            public float ligamentCenterToNearestSkeletonVertexMm;
            public string anatomicalIdentity = "UNRESOLVED";
            public string associationEvidence =
                "Nearest-surface geometry is reported diagnostically; proximity alone does not establish vertebral identity.";
        }

        [Serializable]
        private sealed class DormantPartRecord
        {
            public string objectName;
            public bool activeSelf;
            public bool activeInHierarchy;
            public string meshName;
            public string meshAssetPath;
            public int vertexCount;
            public int triangleCount;
        }

        [Serializable]
        private sealed class TopologyAudit
        {
            public string contract = "thoracic_g2_r1_skeleton_topology_v1";
            public string generatedUtc;
            public string scenePath;
            public string sceneSha256;
            public string skeletonObjectPath;
            public string skeletonMeshName;
            public string skeletonMeshAssetPath;
            public string skeletonMeshAssetSha256;
            public int vertexCount;
            public int triangleCount;
            public int subMeshCount;
            public int connectedComponentCount;
            public bool meshReadable;
            public string actorUse = "PROHIBITED_EVALUATOR_ONLY";
            public string interpretation =
                "Connected components are geometric facts only and are not assigned to vertebral levels without independent anatomical evidence.";
            public List<MeshComponentRecord> components = new();
            public List<ReferenceRecord> references = new();
            public List<DormantPartRecord> dormantSkeletonParts = new();
        }

        private sealed class MutableComponent
        {
            public readonly HashSet<int> vertices = new();
            public readonly List<int> triangleOffsets = new();
            public Vector3 centroidSum;
            public float surfaceArea;
        }

        public static void RunR1ACli()
        {
            try
            {
                RunR1A();
                Debug.Log("[ThoracicUSCoach][G2R1] R1-A topology export complete");
                EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                Debug.LogError("[ThoracicUSCoach][G2R1] R1-A failed: " + exception);
                EditorApplication.Exit(2);
            }
        }

        internal static void RunR1A()
        {
            string scenePath = ThoracicUSCoachSetup.DevelopmentScene;
            EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            GameObject skeleton = Resources.FindObjectsOfTypeAll<GameObject>()
                .Where(item => item.scene.IsValid() && item.activeInHierarchy && item.name == "Skeleton")
                .FirstOrDefault(item => item.TryGetComponent(out MeshFilter filter)
                    && filter.sharedMesh != null && item.CompareTag("Bone"));
            if (skeleton == null)
                throw new InvalidOperationException("Active tagged Skeleton mesh was not found");
            MeshFilter meshFilter = skeleton.GetComponent<MeshFilter>();
            Mesh mesh = meshFilter.sharedMesh;
            if (!mesh.isReadable)
                throw new InvalidOperationException("Active Skeleton mesh is not readable");
            Vector3[] vertices = mesh.vertices;
            int[] triangles = mesh.triangles;
            int[] parent = Enumerable.Range(0, vertices.Length).ToArray();
            for (int offset = 0; offset < triangles.Length; offset += 3)
            {
                Union(parent, triangles[offset], triangles[offset + 1]);
                Union(parent, triangles[offset + 1], triangles[offset + 2]);
            }
            Dictionary<int, MutableComponent> mutable = new();
            for (int offset = 0; offset < triangles.Length; offset += 3)
            {
                int root = Find(parent, triangles[offset]);
                if (!mutable.TryGetValue(root, out MutableComponent component))
                {
                    component = new MutableComponent();
                    mutable.Add(root, component);
                }
                component.triangleOffsets.Add(offset);
                for (int corner = 0; corner < 3; corner++) component.vertices.Add(triangles[offset + corner]);
                Vector3 a = skeleton.transform.TransformPoint(vertices[triangles[offset]]);
                Vector3 b = skeleton.transform.TransformPoint(vertices[triangles[offset + 1]]);
                Vector3 c = skeleton.transform.TransformPoint(vertices[triangles[offset + 2]]);
                component.surfaceArea += 0.5f * Vector3.Cross(b - a, c - a).magnitude;
            }
            List<(MutableComponent component, Bounds bounds, Vector3 centroid)> ordered = new();
            foreach (MutableComponent component in mutable.Values)
            {
                bool initialized = false;
                Bounds bounds = default;
                Vector3 centroid = Vector3.zero;
                foreach (int vertexIndex in component.vertices)
                {
                    Vector3 point = skeleton.transform.TransformPoint(vertices[vertexIndex]);
                    centroid += point;
                    if (!initialized)
                    {
                        bounds = new Bounds(point, Vector3.zero);
                        initialized = true;
                    }
                    else bounds.Encapsulate(point);
                }
                centroid /= Mathf.Max(1, component.vertices.Count);
                ordered.Add((component, bounds, centroid));
            }
            ordered = ordered.OrderByDescending(item => item.centroid.z)
                .ThenBy(item => Mathf.Abs(item.centroid.x)).ToList();
            TopologyAudit audit = new TopologyAudit
            {
                generatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                scenePath = scenePath,
                sceneSha256 = Sha256File(scenePath),
                skeletonObjectPath = TransformPath(skeleton.transform),
                skeletonMeshName = mesh.name,
                skeletonMeshAssetPath = AssetDatabase.GetAssetPath(mesh),
                vertexCount = vertices.Length,
                triangleCount = triangles.Length / 3,
                subMeshCount = mesh.subMeshCount,
                connectedComponentCount = ordered.Count,
                meshReadable = mesh.isReadable
            };
            audit.skeletonMeshAssetSha256 = Sha256File(audit.skeletonMeshAssetPath);
            Dictionary<int, int> rootToComponentId = new();
            for (int componentIndex = 0; componentIndex < ordered.Count; componentIndex++)
            {
                (MutableComponent component, Bounds bounds, Vector3 centroid) = ordered[componentIndex];
                int componentId = componentIndex + 1;
                int root = Find(parent, component.vertices.First());
                rootToComponentId[root] = componentId;
                MeshComponentRecord record = new MeshComponentRecord
                {
                    componentId = componentId,
                    vertexCount = component.vertices.Count,
                    triangleCount = component.triangleOffsets.Count,
                    surfaceAreaMm2 = component.surfaceArea,
                    worldBoundsCenter = bounds.center,
                    worldBoundsSize = bounds.size,
                    worldCentroid = centroid,
                    absoluteLateralCenterMm = Mathf.Abs(centroid.x),
                    geometryOnlyRole = Mathf.Abs(centroid.x) <= 20f
                        ? "MIDLINE_OR_NEAR_MIDLINE_UNRESOLVED"
                        : "LATERAL_UNRESOLVED"
                };
                int stride = Mathf.Max(1, component.vertices.Count / 256);
                int sampled = 0;
                foreach (int vertexIndex in component.vertices.OrderBy(item => item))
                {
                    if (sampled++ % stride != 0) continue;
                    record.sampledWorldVertices.Add(skeleton.transform.TransformPoint(vertices[vertexIndex]));
                    if (record.sampledWorldVertices.Count >= 256) break;
                }
                audit.components.Add(record);
            }
            for (int level = 2; level <= 8; level++)
            {
                string levelName = "T" + level;
                GameObject label = FindSceneObject(levelName);
                GameObject ligament = FindSceneObject("LF " + levelName);
                if (label == null || ligament == null)
                    throw new InvalidOperationException("Missing reference pair for " + levelName);
                Renderer ligamentRenderer = ligament.GetComponent<Renderer>();
                NearestVertex(vertices, parent, rootToComponentId, skeleton.transform,
                    label.transform.position, out int componentId, out float labelDistance);
                NearestVertex(vertices, parent, rootToComponentId, skeleton.transform,
                    ligamentRenderer.bounds.center, out _, out float ligamentDistance);
                audit.references.Add(new ReferenceRecord
                {
                    level = levelName,
                    labelPosition = label.transform.position,
                    ligamentObject = ligament.name,
                    ligamentBoundsCenter = ligamentRenderer.bounds.center,
                    ligamentBoundsSize = ligamentRenderer.bounds.size,
                    nearestSkeletonComponentId = componentId,
                    labelToNearestSkeletonVertexMm = labelDistance,
                    ligamentCenterToNearestSkeletonVertexMm = ligamentDistance
                });
            }
            foreach (GameObject item in Resources.FindObjectsOfTypeAll<GameObject>()
                .Where(item => item.scene.IsValid() && item.name.StartsWith("Skeleton_MeshPart", StringComparison.Ordinal))
                .OrderBy(item => item.name))
            {
                MeshFilter filter = item.GetComponent<MeshFilter>();
                Mesh partMesh = filter != null ? filter.sharedMesh : null;
                audit.dormantSkeletonParts.Add(new DormantPartRecord
                {
                    objectName = item.name,
                    activeSelf = item.activeSelf,
                    activeInHierarchy = item.activeInHierarchy,
                    meshName = partMesh != null ? partMesh.name : string.Empty,
                    meshAssetPath = partMesh != null ? AssetDatabase.GetAssetPath(partMesh) : string.Empty,
                    vertexCount = partMesh != null ? partMesh.vertexCount : 0,
                    triangleCount = partMesh != null ? partMesh.triangles.Length / 3 : 0
                });
            }
            Directory.CreateDirectory(OutputRoot);
            File.WriteAllText(Path.Combine(OutputRoot, "skeleton_mesh_topology.json"),
                JsonUtility.ToJson(audit, true));
            WriteComponentCsv(Path.Combine(OutputRoot, "skeleton_components.csv"), audit.components);
            WriteVertexCsv(Path.Combine(OutputRoot, "skeleton_vertices.csv"), vertices,
                parent, rootToComponentId, skeleton.transform);
            WriteTriangleCsv(Path.Combine(OutputRoot, "skeleton_triangles.csv"), triangles);
        }

        private static void NearestVertex(Vector3[] vertices, int[] parent,
            Dictionary<int, int> rootToComponentId, Transform meshTransform, Vector3 query,
            out int componentId, out float distance)
        {
            int nearestIndex = -1;
            float squaredDistance = float.PositiveInfinity;
            for (int index = 0; index < vertices.Length; index++)
            {
                float candidate = (meshTransform.TransformPoint(vertices[index]) - query).sqrMagnitude;
                if (candidate >= squaredDistance) continue;
                squaredDistance = candidate;
                nearestIndex = index;
            }
            componentId = rootToComponentId[Find(parent, nearestIndex)];
            distance = Mathf.Sqrt(squaredDistance);
        }

        private static int Find(int[] parent, int value)
        {
            while (parent[value] != value)
            {
                parent[value] = parent[parent[value]];
                value = parent[value];
            }
            return value;
        }

        private static void Union(int[] parent, int a, int b)
        {
            int rootA = Find(parent, a);
            int rootB = Find(parent, b);
            if (rootA != rootB) parent[rootB] = rootA;
        }

        private static GameObject FindSceneObject(string objectName)
        {
            return Resources.FindObjectsOfTypeAll<GameObject>()
                .FirstOrDefault(item => item.scene.IsValid() && item.name == objectName);
        }

        private static string TransformPath(Transform transform)
        {
            List<string> names = new List<string>();
            for (Transform current = transform; current != null; current = current.parent) names.Add(current.name);
            names.Reverse();
            return string.Join("/", names);
        }

        private static string Sha256File(string path)
        {
            using SHA256 sha = SHA256.Create();
            using FileStream stream = File.OpenRead(path);
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static void WriteComponentCsv(string path, IEnumerable<MeshComponentRecord> components)
        {
            using StreamWriter writer = new StreamWriter(path, false);
            writer.WriteLine("component_id,vertex_count,triangle_count,surface_area_mm2,center_x,center_y,center_z,size_x,size_y,size_z,geometry_only_role");
            foreach (MeshComponentRecord item in components)
            {
                writer.WriteLine(string.Join(",", new[]
                {
                    item.componentId.ToString(CultureInfo.InvariantCulture),
                    item.vertexCount.ToString(CultureInfo.InvariantCulture),
                    item.triangleCount.ToString(CultureInfo.InvariantCulture),
                    item.surfaceAreaMm2.ToString("R", CultureInfo.InvariantCulture),
                    item.worldBoundsCenter.x.ToString("R", CultureInfo.InvariantCulture),
                    item.worldBoundsCenter.y.ToString("R", CultureInfo.InvariantCulture),
                    item.worldBoundsCenter.z.ToString("R", CultureInfo.InvariantCulture),
                    item.worldBoundsSize.x.ToString("R", CultureInfo.InvariantCulture),
                    item.worldBoundsSize.y.ToString("R", CultureInfo.InvariantCulture),
                    item.worldBoundsSize.z.ToString("R", CultureInfo.InvariantCulture),
                    item.geometryOnlyRole
                }));
            }
        }

        private static void WriteVertexCsv(string path, Vector3[] vertices, int[] parent,
            Dictionary<int, int> rootToComponentId, Transform meshTransform)
        {
            using StreamWriter writer = new StreamWriter(path, false);
            writer.WriteLine("vertex_index,component_id,world_x,world_y,world_z");
            for (int index = 0; index < vertices.Length; index++)
            {
                Vector3 point = meshTransform.TransformPoint(vertices[index]);
                writer.WriteLine(string.Join(",", new[]
                {
                    index.ToString(CultureInfo.InvariantCulture),
                    rootToComponentId[Find(parent, index)].ToString(CultureInfo.InvariantCulture),
                    point.x.ToString("R", CultureInfo.InvariantCulture),
                    point.y.ToString("R", CultureInfo.InvariantCulture),
                    point.z.ToString("R", CultureInfo.InvariantCulture)
                }));
            }
        }

        private static void WriteTriangleCsv(string path, int[] triangles)
        {
            using StreamWriter writer = new StreamWriter(path, false);
            writer.WriteLine("triangle_index,vertex_a,vertex_b,vertex_c");
            for (int offset = 0; offset < triangles.Length; offset += 3)
            {
                writer.WriteLine(string.Join(",", new[]
                {
                    (offset / 3).ToString(CultureInfo.InvariantCulture),
                    triangles[offset].ToString(CultureInfo.InvariantCulture),
                    triangles[offset + 1].ToString(CultureInfo.InvariantCulture),
                    triangles[offset + 2].ToString(CultureInfo.InvariantCulture)
                }));
            }
        }
    }
}
