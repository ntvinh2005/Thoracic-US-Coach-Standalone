using System.IO;
using UnityEngine;
using UnityEditor;
using SMMARTS;

/// <summary>
/// Inspector for ConfigSnapshotChecker: capture a configuration snapshot, and check the live hierarchy against it
/// after an SDK update. Neither button ever modifies the scene.
/// </summary>
[CustomEditor(typeof(ConfigSnapshotChecker))]
public class ConfigSnapshotChecker_Editor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        ConfigSnapshotChecker checker = (ConfigSnapshotChecker)target;
        string path = checker.SnapshotPath;
        bool hasSnapshot = File.Exists(path);

        GUILayout.Space(10);
        GUILayout.Label("Snapshot", EditorStyles.boldLabel);

        if (hasSnapshot)
        {
            ConfigSnapshot stored = null;
            try { stored = JsonUtility.FromJson<ConfigSnapshot>(File.ReadAllText(path)); }
            catch { /* a malformed file is reported by the check itself, no need to throw from OnInspectorGUI */ }

            if (stored != null)
            {
                EditorGUILayout.LabelField("Captured under SDK", stored.sdkPackageVersion);
                EditorGUILayout.LabelField("Captured on", stored.capturedUtc);
                EditorGUILayout.LabelField("Mode", stored.captureMode);
                EditorGUILayout.LabelField("Settings recorded", stored.entries != null ? stored.entries.Length.ToString() : "0");
            }
            else
            {
                EditorGUILayout.HelpBox("A snapshot file exists but could not be read. Re-capture it.", MessageType.Warning);
            }
        }
        else
        {
            EditorGUILayout.HelpBox(
                "No snapshot yet.\n\n" +
                "Configure this object the way this simulator needs it, then press Capture Snapshot. " +
                "After you pull an SDK update, press Check Now to see what the update changed.",
                MessageType.Info);
        }

        EditorGUILayout.LabelField("File", path);

        GUILayout.Space(6);

        if (Application.isPlaying)
            EditorGUILayout.HelpBox(
                "Capture is disabled during Play. Prefab links do not exist at runtime, so a snapshot taken now " +
                "would record a full dump of runtime state instead of this simulator's authored settings.",
                MessageType.Warning);

        using (new EditorGUILayout.HorizontalScope())
        {
            using (new EditorGUI.DisabledScope(Application.isPlaying))
            if (GUILayout.Button(hasSnapshot ? "Re-Capture Snapshot" : "Capture Snapshot"))
            {
                // Re-capturing accepts the current state as correct, so make the user say so out loud.
                bool proceed = !hasSnapshot || EditorUtility.DisplayDialog(
                    "Re-capture snapshot?",
                    "This replaces the stored snapshot with the object's current configuration.\n\n" +
                    "Only do this after you have reviewed the differences and decided the current state is correct. " +
                    "Otherwise you will bake a regression in as the new baseline.",
                    "Re-capture", "Cancel");

                if (proceed) checker.CaptureSnapshot();
            }

            using (new EditorGUI.DisabledScope(!hasSnapshot))
            {
                if (GUILayout.Button("Check Now")) checker.CheckAgainstSnapshot();
            }
        }

        using (new EditorGUI.DisabledScope(!hasSnapshot))
        {
            if (GUILayout.Button("Reveal Snapshot File"))
                EditorUtility.RevealInFinder(Path.GetFullPath(path));
        }

        GUILayout.Space(4);
        EditorGUILayout.HelpBox(
            "This tool only reads and reports. It never writes to the hierarchy.",
            MessageType.None);
    }
}
