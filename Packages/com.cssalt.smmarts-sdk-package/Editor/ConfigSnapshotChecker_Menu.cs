using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEditor;
using SMMARTS;

/// <summary>
/// Runs every ConfigSnapshotChecker in the open scenes in one go.
///
/// After pulling an SDK update this is the single action that answers "did that update break anything this simulator
/// configured?", instead of hunting through the hierarchy pressing Check Now on one object at a time. That matters
/// because the settings at risk are spread across many prefabs, and the ones most likely to be missed are the ones
/// nobody thought to go and look at.
/// </summary>
public static class ConfigSnapshotChecker_Menu
{
    [MenuItem("SMMARTS/Check All Config Snapshots", false, 2000)]
    public static void CheckAllConfigSnapshots()
    {
        ConfigSnapshotChecker[] checkers = Object.FindObjectsByType<ConfigSnapshotChecker>(
            FindObjectsInactive.Include, FindObjectsSortMode.None);

        if (checkers.Length == 0)
        {
            Debug.LogWarning("[ConfigSnapshotChecker] No config snapshot checkers in the open scenes. Click here for more info." +
                "\n    Add a Config Snapshot Checker component to a GameObject you have configured for this simulator," +
                "\n    then press Capture Snapshot. After an SDK update, this menu item reports what the update changed.");
            return;
        }

        List<string> withChanges = new List<string>();
        List<string> withoutSnapshot = new List<string>();
        int clean = 0;

        // Each checker logs its own entry, and those entries are clickable and select the object they belong to, so we
        // let them speak for themselves and only add a tally on top. Passing false keeps the clean ones quiet, which is
        // the whole point of running this across a scene full of objects.
        foreach (ConfigSnapshotChecker checker in checkers)
        {
            if (!File.Exists(checker.SnapshotPath))
            {
                checker.CheckAgainstSnapshot(false); // still reports: a checker with no snapshot protects nothing
                withoutSnapshot.Add(checker.gameObject.name);
                continue;
            }

            if (checker.CheckAgainstSnapshot(false) > 0) withChanges.Add(checker.gameObject.name);
            else clean++;
        }

        bool allWell = withChanges.Count == 0 && withoutSnapshot.Count == 0;

        if (allWell)
        {
            Debug.Log($"[ConfigSnapshotChecker] Checked {checkers.Length} object(s). All match their snapshots.");
            return;
        }

        List<string> counts = new List<string>();
        if (withChanges.Count > 0) counts.Add($"{withChanges.Count} with changes");
        if (clean > 0) counts.Add($"{clean} clean");
        if (withoutSnapshot.Count > 0) counts.Add($"{withoutSnapshot.Count} with no snapshot");

        string report =
            $"[ConfigSnapshotChecker] Checked {checkers.Length} object(s): {string.Join(", ", counts)}. Click here for more info.";

        // Name the objects here as well as in their own entries. In a busy console the individual warnings can be
        // scattered, and this line gives you the whole picture without scrolling for them.
        if (withChanges.Count > 0) report += "\n    changed: " + string.Join(", ", withChanges);
        if (withoutSnapshot.Count > 0) report += "\n    no snapshot: " + string.Join(", ", withoutSnapshot);
        report += "\n    Each object above has its own console entry with the detail. Nothing has been modified.";

        Debug.LogWarning(report);
    }
}
