using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using CSSALT.ThoracicUSCoach;
using Newtonsoft.Json.Linq;
using SMMARTS;
using UnityEngine;

public static class WorkerBFrameAttribution
{
    private sealed class SampleSpec
    {
        public string Code;
        public int Prototype;
        public string Episode;
        public int Frame;
        public string Split;
        public string EvaluatorLevel;
        public string Role;
    }

    private sealed class HitAggregate
    {
        public string Path;
        public string Mesh;
        public string RenderType;
        public string Message;
        public int Count;
        public readonly Dictionary<int, int> Triangles = new Dictionary<int, int>();
        public Vector3 Min = new Vector3(float.PositiveInfinity, float.PositiveInfinity, float.PositiveInfinity);
        public Vector3 Max = new Vector3(float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity);

        public void Add(RaycastHit hit)
        {
            Count++;
            if (hit.triangleIndex >= 0)
            {
                int prior;
                Triangles.TryGetValue(hit.triangleIndex, out prior);
                Triangles[hit.triangleIndex] = prior + 1;
            }
            Min = Vector3.Min(Min, hit.point);
            Max = Vector3.Max(Max, hit.point);
        }
    }

    public static async Task<string> Analyze()
    {
        const string outputRelative = "Artifacts/ThoracicUSCoach/active2d/diagnostics/m01_worker_b/m01_worker_b_unity_attribution_v1.json";
        if (!Application.isPlaying) throw new InvalidOperationException("Worker B attribution requires Play Mode.");
        string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        ThoracicUSCoachController controller = UnityEngine.Object.FindFirstObjectByType<ThoracicUSCoachController>();
        if (controller == null || controller.probeRoot == null) throw new InvalidOperationException("ThoracicUSCoachController/probeRoot unavailable.");
        if (!UltrasoundManager.TryGet(UltrasoundManager.Plane.Primary, out UltrasoundManager manager))
            throw new InvalidOperationException("Primary UltrasoundManager unavailable.");

        Vector4[,] beams = (Vector4[,])ReadField(manager, "beamTrajectoryPoints");
        if (beams == null || beams.GetLength(1) == 0) throw new InvalidOperationException("beamTrajectoryPoints not initialized.");
        float raycastUp = (float)ReadField(manager, "raycastUptheProbe_mm");
        int scanDepth = (int)ReadField(manager, "scanDepth_mm");
        LayerMask skinLayer = (LayerMask)ReadField(manager, "SkinLayer");
        bool anisotropy = (bool)ReadField(manager, "anisotropy");
        Transform face = manager.CenterOfProbeFace.transform;
        Transform rear = manager.CenterOfProbeRear.transform;

        SampleSpec[] samples = new[]
        {
            S("P0-S1",0,"train","A2D_active2d_phase1_balanced_v1_20261023_0007_S20316456",29,"T7","closest"),
            S("P0-S2",0,"train","A2D_active2d_phase1_balanced_v1_20261023_0025_S20458998",22,"T7","closest"),
            S("P0-S3",0,"test","A2D_active2d_phase1_balanced_v1_20261023_0029_S20490674",48,"T7","closest"),
            S("P0-S5",0,"train","A2D_active2d_phase1_balanced_v1_20261023_0016_S20387727",21,"T6","near-radius"),
            S("P3-S1",3,"train","A2D_active2d_phase1_balanced_v1_20261023_0026_S20466917",38,"T7","closest"),
            S("P3-S2",3,"train","A2D_active2d_phase1_balanced_v1_20261023_0025_S20458998",15,"T7","closest"),
            S("P3-S3",3,"train","A2D_active2d_phase1_balanced_v1_20261023_0007_S20316456",39,"T7","closest"),
            S("P3-S5",3,"train","A2D_active2d_phase1_balanced_v1_20261023_0005_S20300618",19,"T7","near-radius"),
            S("P5-S1",5,"train","A2D_active2d_phase1_balanced_v1_20261023_0016_S20387727",46,"T6","closest"),
            S("P5-S2",5,"train","A2D_active2d_phase1_balanced_v1_20261023_0000_S20261023",41,"T6","closest"),
            S("P5-S3",5,"train","A2D_active2d_phase1_balanced_v1_20261023_0015_S20379808",23,"T6","closest"),
            S("P5-S5",5,"train","A2D_active2d_phase1_balanced_v1_20261023_0000_S20261023",14,"T6","near-radius"),
            S("P6-S1",6,"dev","A2D_active2d_phase1_balanced_v1_20261023_0008_S20324375",21,"T7","closest"),
            S("P6-S2",6,"train","A2D_active2d_phase1_balanced_v1_20261023_0022_S20435241",30,"T6","closest"),
            S("P6-S3",6,"train","A2D_active2d_phase1_balanced_v1_20261023_0001_S20268942",38,"T6","closest"),
            S("P6-S5",6,"dev","A2D_active2d_phase1_balanced_v1_20261023_0008_S20324375",43,"T7","near-radius"),
            S("T8-REF",-1,"dev","A2D_active2d_phase1_balanced_v1_20261023_0013_S20363970",14,"T8","level-reference"),
        };

        JObject report = new JObject();
        report["schemaVersion"] = 1;
        report["taskId"] = "M01-WORKER-B";
        report["purpose"] = "Offline Unity renderer/object/triangle attribution only; never an actor input.";
        report["scene"] = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path;
        report["rendererImplementation"] = "UltrasoundManager.Scan Physics.RaycastAll; first entering Shadow object emits bright edge and terminates the beam.";
        report["beamCount"] = beams.GetLength(1);
        report["scanDepthMm"] = scanDepth;
        report["raycastUpProbeMm"] = raycastUp;
        report["beamThicknessMm"] = manager.BeamThickness_mm;
        report["useSpherecast"] = manager.UseSpherecast;
        report["anisotropy"] = anisotropy;
        report["sampling"] = "Three deterministic ray slabs at local z = -0.5*thickness, 0, +0.5*thickness; original capture used stochastic per-beam z and cannot be replayed bit-exactly without saved RNG state.";
        report["combinedSkeletonLimitation"] = "All ribs and vertebrae are one GameObject/mesh named Skeleton; triangle identity proves bone-mesh contribution but does not provide a rib/lamina/transverse-process semantic label.";
        report["probeFacePath"] = PathOf(face);
        report["probeRearPath"] = PathOf(rear);

        JArray sampleResults = new JArray();
        Vector3 originalPosition = controller.probeRoot.position;
        Quaternion originalRotation = controller.probeRoot.rotation;
        try
        {
            foreach (SampleSpec sample in samples)
            {
                string episodeDir = Path.Combine(root, "Artifacts", "ThoracicUSCoach", "active2d_dataset", sample.Split, sample.Episode);
                JObject privileged = JObject.Parse(File.ReadAllText(Path.Combine(episodeDir, "privileged_annotations.json")));
                JToken frameToken = privileged["frames"].First(value => (int)value["index"] == sample.Frame);
                JObject stream = JObject.Parse(File.ReadAllText(Path.Combine(episodeDir, "stream_manifest.json")));
                JToken streamFrame = stream["frames"].First(value => (int)value["index"] == sample.Frame);
                Vector3 position = Vec(frameToken["probePosition"]);
                Quaternion rotation = Quat(frameToken["probeRotation"]);
                controller.probeRoot.SetPositionAndRotation(position, rotation);
                Physics.SyncTransforms();
                await Task.Delay(160);
                controller.probeRoot.SetPositionAndRotation(position, rotation);
                Physics.SyncTransforms();
                await Task.Delay(80);
                Vector3 achievedPosition = controller.probeRoot.position;
                Quaternion achievedRotation = controller.probeRoot.rotation;

                var firstVisible = new Dictionary<string, HitAggregate>();
                var firstShadow = new Dictionary<string, HitAggregate>();
                var allBeforeShadow = new Dictionary<string, HitAggregate>();
                int rays = 0;
                int airGapRays = 0;
                int noVisibleRays = 0;
                float[] offsets = manager.BeamThickness_mm <= 0f
                    ? new[] { 0f }
                    : new[] { -0.5f * manager.BeamThickness_mm, 0f, 0.5f * manager.BeamThickness_mm };

                for (int beamIndex = 0; beamIndex < beams.GetLength(1); beamIndex++)
                foreach (float offset in offsets)
                {
                    rays++;
                    Vector3 localStart = beams[2, beamIndex];
                    Vector3 localEnd = beams[3, beamIndex];
                    localStart.z = offset;
                    localEnd.z = offset;
                    Vector3 globalStart = face.TransformPoint(localStart);
                    Vector3 globalEnd = face.TransformPoint(localEnd);
                    Vector3 up = (globalStart - globalEnd).normalized * raycastUp;
                    Vector3 castStart = globalStart + up;
                    Vector3 direction = globalEnd - castStart;
                    float maxDistance = scanDepth + raycastUp;
                    if (manager.DrawAirGaps && !Physics.Raycast(castStart, direction, raycastUp + manager.MinimumAirGap_mm, skinLayer))
                    {
                        airGapRays++;
                        continue;
                    }
                    RaycastHit[] hits = Physics.RaycastAll(castStart, direction, maxDistance, manager.ImageableLayers)
                        .OrderBy(hit => hit.distance).ToArray();
                    bool foundVisible = false;
                    foreach (RaycastHit hit in hits)
                    {
                        UltrasoundVisibility visibility = hit.transform.GetComponent<UltrasoundVisibility>();
                        if (visibility == null || !visibility.ShowOnUltrasound) continue;
                        float incidence = Mathf.Abs(90f - Vector3.Angle(hit.normal, globalStart - globalEnd));
                        if (anisotropy && incidence < visibility.AnisoIndex) continue;
                        if (!foundVisible)
                        {
                            Add(firstVisible, hit, visibility);
                            foundVisible = true;
                        }
                        Add(allBeforeShadow, hit, visibility);
                        if (visibility.Type == UltrasoundVisibility.RenderType.Shadow)
                        {
                            Add(firstShadow, hit, visibility);
                            break;
                        }
                    }
                    if (!foundVisible) noVisibleRays++;
                }

                JObject row = new JObject();
                row["sampleCode"] = sample.Code;
                row["prototypeId"] = sample.Prototype;
                row["role"] = sample.Role;
                row["split"] = sample.Split;
                row["episodeId"] = sample.Episode;
                row["frameIndex"] = sample.Frame;
                row["evaluatorLevelOfflineOnly"] = sample.EvaluatorLevel;
                row["image"] = (string)streamFrame["image"];
                row["imageSha256"] = (string)streamFrame["imageSha256"];
                row["probePosition"] = JsonVec(position);
                row["probeRotation"] = JsonQuat(rotation);
                row["achievedProbePosition"] = JsonVec(achievedPosition);
                row["achievedProbeRotation"] = JsonQuat(achievedRotation);
                row["positionReplayErrorMm"] = Vector3.Distance(position, achievedPosition);
                row["rotationReplayErrorDeg"] = Quaternion.Angle(rotation, achievedRotation);
                row["raysSampled"] = rays;
                row["airGapRays"] = airGapRays;
                row["noVisibleRays"] = noVisibleRays;
                row["firstVisible"] = JsonAggregates(firstVisible, rays);
                row["firstShadow"] = JsonAggregates(firstShadow, rays);
                row["allEntriesBeforeShadow"] = JsonAggregates(allBeforeShadow, rays);
                sampleResults.Add(row);
            }
        }
        finally
        {
            controller.probeRoot.SetPositionAndRotation(originalPosition, originalRotation);
            Physics.SyncTransforms();
        }

        report["samples"] = sampleResults;
        JObject summary = new JObject();
        foreach (int prototype in new[] { 0, 3, 5, 6 })
        {
            var rows = sampleResults.Cast<JObject>().Where(row => (int)row["prototypeId"] == prototype).ToArray();
            var shadowTotals = new Dictionary<string, int>();
            foreach (JObject row in rows)
            foreach (JObject item in (JArray)row["firstShadow"])
            {
                string key = (string)item["path"];
                int count = (int)item["count"];
                int prior;
                shadowTotals.TryGetValue(key, out prior);
                shadowTotals[key] = prior + count;
            }
            summary["P" + prototype] = new JObject
            {
                ["sampleCount"] = rows.Length,
                ["firstShadowTotals"] = new JArray(shadowTotals.OrderByDescending(pair => pair.Value)
                    .Select(pair => new JObject { ["path"] = pair.Key, ["count"] = pair.Value })),
            };
        }
        report["prototypeSummary"] = summary;
        report["classificationRule"] = new JObject
        {
            ["VERIFIED_ANATOMICAL_LANDMARK"] = "Requires repeatable object/triangle attribution plus a simulator semantic object identity for the named landmark.",
            ["GENERIC_VISUAL_FEATURE"] = "Repeatable renderer feature attributable only to broad tissue/shadow/window behavior, not a unique named landmark.",
            ["AMBIGUOUS"] = "Multiple physical structures or level-dependent sources produce the prototype, or the combined mesh prevents semantic attribution.",
            ["UNSUPPORTED"] = "No sufficient Unity attribution evidence.",
        };

        string output = Path.GetFullPath(Path.Combine(root, outputRelative));
        Directory.CreateDirectory(Path.GetDirectoryName(output));
        File.WriteAllText(output, report.ToString());
        return output;
    }

    private static SampleSpec S(string code, int prototype, string split, string episode, int frame, string level, string role)
    {
        return new SampleSpec { Code = code, Prototype = prototype, Split = split, Episode = episode, Frame = frame, EvaluatorLevel = level, Role = role };
    }

    private static object ReadField(object target, string name)
    {
        FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field == null) throw new MissingFieldException(target.GetType().FullName, name);
        return field.GetValue(target);
    }

    private static void Add(Dictionary<string, HitAggregate> values, RaycastHit hit, UltrasoundVisibility visibility)
    {
        string path = PathOf(hit.transform);
        HitAggregate aggregate;
        if (!values.TryGetValue(path, out aggregate))
        {
            MeshFilter filter = hit.transform.GetComponent<MeshFilter>();
            aggregate = new HitAggregate
            {
                Path = path,
                Mesh = filter != null && filter.sharedMesh != null ? filter.sharedMesh.name : "",
                RenderType = visibility.Type.ToString(),
                Message = visibility.TouchDisplayMessage,
            };
            values[path] = aggregate;
        }
        aggregate.Add(hit);
    }

    private static JArray JsonAggregates(Dictionary<string, HitAggregate> values, int rays)
    {
        return new JArray(values.Values.OrderByDescending(value => value.Count).Select(value =>
        {
            JObject result = new JObject();
            result["path"] = value.Path;
            result["mesh"] = value.Mesh;
            result["renderType"] = value.RenderType;
            result["touchMessage"] = value.Message;
            result["count"] = value.Count;
            result["rayFraction"] = rays > 0 ? value.Count / (double)rays : 0.0;
            result["distinctTriangleCount"] = value.Triangles.Count;
            result["triangleIndexMin"] = value.Triangles.Count > 0 ? value.Triangles.Keys.Min() : -1;
            result["triangleIndexMax"] = value.Triangles.Count > 0 ? value.Triangles.Keys.Max() : -1;
            result["topTriangles"] = new JArray(value.Triangles.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).Take(12)
                .Select(pair => new JObject { ["triangleIndex"] = pair.Key, ["count"] = pair.Value }));
            result["worldHitBoundsMin"] = float.IsInfinity(value.Min.x) ? null : JsonVec(value.Min);
            result["worldHitBoundsMax"] = float.IsInfinity(value.Max.x) ? null : JsonVec(value.Max);
            return result;
        }));
    }

    private static Vector3 Vec(JToken token) => new Vector3((float)token["x"], (float)token["y"], (float)token["z"]);
    private static Quaternion Quat(JToken token) => new Quaternion((float)token["x"], (float)token["y"], (float)token["z"], (float)token["w"]);
    private static JObject JsonVec(Vector3 value) => new JObject { ["x"] = value.x, ["y"] = value.y, ["z"] = value.z };
    private static JObject JsonQuat(Quaternion value) => new JObject { ["x"] = value.x, ["y"] = value.y, ["z"] = value.z, ["w"] = value.w };

    private static string PathOf(Transform transform)
    {
        var names = new List<string>();
        while (transform != null)
        {
            names.Add(transform.name);
            transform = transform.parent;
        }
        names.Reverse();
        return "/" + string.Join("/", names);
    }
}
