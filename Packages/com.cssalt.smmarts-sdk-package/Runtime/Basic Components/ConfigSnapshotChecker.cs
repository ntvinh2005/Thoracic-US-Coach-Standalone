using System.Collections.Generic;
using System.IO;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace SMMARTS
{
    /// <summary>
    /// Detects when an SDK package update has quietly changed a simulator's case-specific setup.
    ///
    /// THE PROBLEM THIS SOLVES:
    /// The SMMARTS SDK ships as a read-only package, so a simulator configures SDK prefabs by dropping them in a
    /// scene and overriding values on the instance. Unity stores those overrides by property NAME. If a later SDK
    /// version renames a serialized field, moves a GameObject, or replaces a component, the override no longer
    /// matches anything, and Unity drops it silently. The value snaps back to the SDK default and nothing is logged.
    /// The SDK author cannot fix this from their side, because the simulator's scenes are not in the SDK repo.
    ///
    /// WHAT THIS DOES:
    /// Add this component to a GameObject you have configured, then press Capture Snapshot. It writes a JSON file
    /// recording that object's configuration, along with the SDK version it was captured under. After you pull an
    /// SDK update, press Check Now (or just press Play) and it reports every setting that was lost or changed.
    ///
    /// WHAT THIS DELIBERATELY DOES NOT DO:
    /// It never writes to the hierarchy. It only ever reads and reports. Automatic repair would happily overwrite
    /// legitimate SDK improvements alongside the regressions, and telling those apart is a human judgement call.
    ///
    /// The snapshot JSON belongs to the simulator, not to the SDK. It is written under the project's own Assets
    /// folder so that a package update can never touch it. That is the same reasoning behind DeviceConfiguration.
    ///
    /// This is an editor-time development tool. In a player build it does nothing.
    /// </summary>
    public class ConfigSnapshotChecker : MonoBehaviour
    {
        [Header("----- Snapshot File -----")]

        /// Folder for the snapshot, relative to the project root. Keep this inside the simulator's own Assets
        /// folder and NOT inside the SDK package, or a package update will wipe the very thing meant to survive it.
        public string snapshotFolder = "Assets/SMMARTS Config Snapshots";

        /// File name without extension. Leave empty to use this GameObject's name.
        public string snapshotFileName = "";

        [Header("----- When To Check -----")]

        /// Run the check automatically on Start. This is the canary that catches drift nobody went looking for.
        /// It stays silent when everything matches, so it costs you nothing in an already busy console. Press
        /// Check Now in the inspector when you want confirmation that a check actually ran and passed.
        public bool checkOnPlay = true;

        /// Floats rarely compare exactly after a reserialization, so allow a little slack before calling it a change.
        public float floatTolerance = 0.0001f;

        [Header("----- Ignore List -----")]

        /// Property paths containing any of these strings are skipped. Transforms in particular get nudged constantly
        /// during normal scene work and would otherwise bury the alerts that matter.
        public string[] ignorePropertiesContaining = new string[]
        {
            "m_LocalPosition",
            "m_LocalRotation",
            "m_LocalScale",
            "m_RootOrder",
        };

        public string SnapshotPath
        {
            get
            {
                string name = string.IsNullOrEmpty(snapshotFileName) ? gameObject.name : snapshotFileName;
                return Path.Combine(snapshotFolder, name + ".json").Replace("\\", "/");
            }
        }

        void Start()
        {
#if UNITY_EDITOR
            // Automatic check, so report only if something is actually wrong. Simulator consoles are busy enough
            // without a passing check announcing itself on every single Play.
            if (checkOnPlay) CheckAgainstSnapshot(false);
#endif
        }

#if UNITY_EDITOR

        // ------------------------------------------------------------------------------------------------
        // Capture
        // ------------------------------------------------------------------------------------------------

        /// <summary>
        /// Records the current configuration of this GameObject to JSON. Safe to re-run: it overwrites the file.
        /// Re-capture deliberately, after you have reviewed an SDK update and accepted the new state as correct.
        /// </summary>
        public void CaptureSnapshot()
        {
            // Capturing during Play produces a snapshot that is wrong in two ways at once, so refuse rather than
            // quietly write a bad baseline. Prefab links do not exist at runtime, so even a prefab instance falls
            // back to a full dump of every serialized value instead of just this simulator's own settings. And the
            // values themselves are whatever runtime code has done to them, not what is authored in the scene.
            if (Application.isPlaying)
            {
                Debug.LogWarning($"[ConfigSnapshotChecker] Did not capture '{gameObject.name}': you are in Play mode. Click here for more info." +
                                 "\n    Prefab links do not exist at runtime, so a capture taken now records a full dump of every serialized" +
                                 "\n    value rather than just the settings this simulator configured, and it records runtime state instead" +
                                 "\n    of what is authored in the scene." +
                                 "\n    Exit Play mode and capture again.", this);
                return;
            }

            ConfigSnapshot snapshot = BuildSnapshot();

            Directory.CreateDirectory(snapshotFolder);
            File.WriteAllText(SnapshotPath, JsonUtility.ToJson(snapshot, true));

            // Import just this one file rather than calling AssetDatabase.Refresh(). A full refresh on a project
            // this size is slow and interrupts whatever the user was doing, for no benefit here.
            AssetDatabase.ImportAsset(SnapshotPath, ImportAssetOptions.ForceSynchronousImport);

            Debug.Log($"[ConfigSnapshotChecker] Captured {snapshot.entries.Length} setting(s) for '{gameObject.name}' " +
                      $"under SDK {snapshot.sdkPackageVersion} using mode '{snapshot.captureMode}'.\n{SnapshotPath}", this);
        }

        ConfigSnapshot BuildSnapshot()
        {
            ConfigSnapshot snapshot = new ConfigSnapshot();
            snapshot.capturedUtc = System.DateTime.UtcNow.ToString("u");
            snapshot.sdkPackageVersion = GetSdkVersion();
            snapshot.unityVersion = Application.unityVersion;
            snapshot.rootObjectName = gameObject.name;

            GameObject prefabInstanceRoot = PrefabUtility.GetNearestPrefabInstanceRoot(gameObject);
            if (prefabInstanceRoot != null)
            {
                // The prefab overrides ARE the case-specific configuration, by definition, and they are exactly
                // what an SDK rename orphans. Capturing them keeps the snapshot small and the alerts meaningful.
                snapshot.captureMode = "PrefabOverrides";
                snapshot.sourcePrefabPath = AssetDatabase.GetAssetPath(
                    PrefabUtility.GetCorrespondingObjectFromSource(prefabInstanceRoot));
                snapshot.entries = CapturePrefabOverrides(prefabInstanceRoot).ToArray();
            }
            else
            {
                // Not a prefab instance, so there are no overrides to read. Fall back to recording every serialized
                // value on this object and its children. This is noisier, and that is the tradeoff for covering
                // objects the simulator built by hand.
                snapshot.captureMode = "FullSnapshot";
                snapshot.sourcePrefabPath = "";
                snapshot.entries = CaptureFullHierarchy().ToArray();
            }

            return snapshot;
        }

        List<ConfigEntry> CapturePrefabOverrides(GameObject prefabInstanceRoot)
        {
            List<ConfigEntry> entries = new List<ConfigEntry>();

            PropertyModification[] modifications = PrefabUtility.GetPropertyModifications(prefabInstanceRoot);
            if (modifications == null) return entries;

            foreach (PropertyModification mod in modifications)
            {
                if (mod == null || mod.target == null) continue;
                if (ShouldIgnore(mod.propertyPath)) continue;

                ConfigEntry entry = new ConfigEntry();
                DescribeTarget(mod.target, out entry.targetPath, out entry.component, out entry.componentIndex);
                entry.property = mod.propertyPath;
                entry.value = mod.value;
                entry.reference = DescribeReference(mod.objectReference);
                entries.Add(entry);
            }

            entries.Sort(CompareEntries);
            return entries;
        }

        List<ConfigEntry> CaptureFullHierarchy()
        {
            List<ConfigEntry> entries = new List<ConfigEntry>();

            foreach (Component component in GetComponentsInChildren<Component>(true))
            {
                // A missing script leaves a null component behind. Nothing to record, but worth knowing about.
                if (component == null) continue;
                if (component is ConfigSnapshotChecker) continue; // don't record our own settings

                string targetPath = GetRelativePath(component.transform, transform);
                string componentName = component.GetType().Name;
                int componentIndex = IndexOfComponent(component);

                SerializedObject serialized = new SerializedObject(component);
                SerializedProperty property = serialized.GetIterator();

                bool enterChildren = true;
                while (property.NextVisible(enterChildren))
                {
                    enterChildren = false;
                    if (ShouldIgnore(property.propertyPath)) continue;

                    ConfigEntry entry = new ConfigEntry();
                    entry.targetPath = targetPath;
                    entry.component = componentName;
                    entry.componentIndex = componentIndex;
                    entry.property = property.propertyPath;
                    entry.value = DescribeProperty(property, out entry.reference);
                    entries.Add(entry);
                }
            }

            entries.Sort(CompareEntries);
            return entries;
        }

        // ------------------------------------------------------------------------------------------------
        // Check
        // ------------------------------------------------------------------------------------------------

        /// <summary>
        /// Compares the live hierarchy against the stored snapshot and writes any differences to the console.
        /// Returns the number of settings that were lost or changed, so callers can branch on it. Never modifies
        /// anything in the scene.
        ///
        /// Set reportWhenClean false for automatic checks: a problem is always reported, but a clean result stays
        /// quiet. Pressing the inspector button passes true, because there you asked a question and deserve an
        /// answer either way. A missing snapshot is always reported regardless, since a checker with nothing to
        /// check against is not protecting anything.
        /// </summary>
        public int CheckAgainstSnapshot(bool reportWhenClean = true)
        {
            if (!File.Exists(SnapshotPath))
            {
                // Always reported, including on Play, and deliberately not subject to reportWhenClean. A checker with
                // no snapshot is protecting nothing while looking like coverage, and that false confidence is worse
                // than the noise of saying so.
                Debug.LogWarning($"[ConfigSnapshotChecker] No snapshot yet for '{gameObject.name}'. Click here for more info." +
                                 "\n    Press Capture Snapshot once this object is configured the way this simulator needs it." +
                                 $"\n    Expected file: {SnapshotPath}", this);
                return 0;
            }

            ConfigSnapshot stored = JsonUtility.FromJson<ConfigSnapshot>(File.ReadAllText(SnapshotPath));
            if (stored == null || stored.entries == null)
            {
                Debug.LogError($"[ConfigSnapshotChecker] Could not read the snapshot at {SnapshotPath}.", this);
                return 0;
            }

            // Resolve every stored setting directly against the live object, rather than re-capturing and diffing two
            // snapshots. Re-capturing was mode-dependent and that was a bug: prefab links do not exist in Play mode,
            // so capture fell back to FullSnapshot there and every ordinary serialized value read as a difference.
            // Walking the snapshot makes the check behave identically in edit mode and Play mode, and it means we only
            // ever report on settings this simulator actually chose to configure.
            List<string> lost = new List<string>();
            List<string> changed = new List<string>();

            foreach (ConfigEntry entry in stored.entries)
            {
                string expected = ExpectedValue(entry);
                string liveValue, reason;
                SerializedPropertyType propertyType;

                if (!TryResolveLiveValue(entry, out liveValue, out reason, out propertyType))
                {
                    // This is the failure the whole tool exists for: the setting was configured, and the thing it was
                    // attached to is not there any more.
                    lost.Add($"    LOST     {Describe(entry)}\n             was: '{expected}'\n             why: {reason}");
                    continue;
                }

                if (!ValuesMatch(expected, liveValue))
                    changed.Add($"    CHANGED  {Describe(entry)}" +
                                $"\n             was: '{FormatForDisplay(expected, propertyType)}'" +
                                $"\n             now: '{FormatForDisplay(liveValue, propertyType)}'");
            }

            // Walking the snapshot alone cannot see a setting that is overridden NOW but was not overridden when the
            // snapshot was taken. Disabling a component, for instance, creates a brand new m_Enabled override, and
            // nothing in the stored snapshot refers to it, so the comparison above has nothing to catch it with.
            // That is a real difference from the recorded baseline, so it is reported.
            //
            // Only for PrefabOverrides snapshots, and only outside Play: the override set is small and meaningful,
            // whereas a FullSnapshot already records everything, and at runtime there is no override set to read.
            List<string> added = new List<string>();

            if (!Application.isPlaying && stored.captureMode == "PrefabOverrides")
            {
                GameObject prefabInstanceRoot = PrefabUtility.GetNearestPrefabInstanceRoot(gameObject);
                if (prefabInstanceRoot != null)
                {
                    HashSet<string> storedKeys = new HashSet<string>();
                    foreach (ConfigEntry stale in stored.entries) storedKeys.Add(KeyOf(stale));

                    foreach (ConfigEntry live in CapturePrefabOverrides(prefabInstanceRoot))
                    {
                        if (storedKeys.Contains(KeyOf(live))) continue;

                        // Resolve it as well, purely so the value prints the way the inspector shows it.
                        string liveValue, reason;
                        SerializedPropertyType propertyType;
                        if (!TryResolveLiveValue(live, out liveValue, out reason, out propertyType))
                            liveValue = ExpectedValue(live);

                        added.Add($"    ADDED    {Describe(live)}" +
                                  $"\n             now: '{FormatForDisplay(liveValue, propertyType)}'" +
                                  "\n             why: this is overridden now but was not when the snapshot was taken");
                    }
                }
            }

            // The Unity console only shows the FIRST LINE of a log entry in the collapsed list, so that line has to
            // carry the verdict. Everything else is detail you only see once you click the entry, so the summary is
            // built after the comparison rather than before it.
            bool isRegression = lost.Count > 0 || changed.Count > 0 || added.Count > 0;

            string summary = isRegression
                ? $"[ConfigSnapshotChecker] Something changed in '{gameObject.name}'! " +
                  $"{DriftSummary(lost.Count, changed.Count, added.Count)} Click here for more info."
                : $"[ConfigSnapshotChecker] '{gameObject.name}' matches its snapshot.";

            string report = summary +
                $"\n    snapshot captured under SDK {stored.sdkPackageVersion} (Unity {stored.unityVersion}) on {stored.capturedUtc}" +
                $"\n    now running SDK {GetSdkVersion()} (Unity {Application.unityVersion})";

            // A version 1 snapshot has no componentIndex, so every setting on a second same-typed component resolves to
            // the first one and reports a difference that re-capturing cannot clear. Say so rather than let someone
            // chase a phantom.
            if (stored.snapshotFormatVersion < 2)
                report += "\n    NOTE: this snapshot is in the older version 1 format, which could not tell apart two " +
                          "components of the same type on one object. Re-capture it to clear any false differences.";

            // Losing the prefab link is a real failure worth naming, but only edit mode can tell us: at runtime there
            // is no prefab connection to find, so checking for one during Play would cry wolf every single time.
            if (!Application.isPlaying
                && stored.captureMode == "PrefabOverrides"
                && PrefabUtility.GetNearestPrefabInstanceRoot(gameObject) == null)
            {
                report += "\n    NOTE: this was a prefab instance when the snapshot was taken and is not one now. " +
                          "Its prefab link may have been lost entirely.";
            }

            if (!isRegression)
            {
                if (reportWhenClean) Debug.Log(report, this);
                return 0;
            }

            if (lost.Count > 0) report += $"\n\n  {lost.Count} SETTING(S) LOST:\n" + string.Join("\n", lost);
            if (changed.Count > 0) report += $"\n\n  {changed.Count} SETTING(S) CHANGED:\n" + string.Join("\n", changed);
            if (added.Count > 0) report += $"\n\n  {added.Count} SETTING(S) ADDED:\n" + string.Join("\n", added);

            report += "\n\n  Nothing has been modified. Review these, fix what regressed, then re-capture the snapshot.";

            Debug.LogWarning(report, this);
            return lost.Count + changed.Count + added.Count;
        }

        // ------------------------------------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------------------------------------

        /// <summary>
        /// Looks up what a recorded setting is worth on the live object right now.
        /// Returns false when the setting cannot be found at all, with a reason saying which link in the chain broke:
        /// the GameObject, the component, or the property. That last case is the SDK rename this tool was built for.
        /// </summary>
        bool TryResolveLiveValue(ConfigEntry entry, out string liveValue, out string reason, out SerializedPropertyType propertyType)
        {
            liveValue = "";
            reason = "";
            propertyType = SerializedPropertyType.Generic;

            Transform target = FindByRelativePath(entry.targetPath);
            if (target == null)
            {
                reason = $"no GameObject at '{entry.targetPath}' any more";
                return false;
            }

            Object owner = target.gameObject;
            if (!string.IsNullOrEmpty(entry.component))
            {
                owner = FindComponentByTypeName(target.gameObject, entry.component, entry.componentIndex);
                if (owner == null)
                {
                    reason = entry.componentIndex > 0
                        ? $"there is no {entry.component} #{entry.componentIndex + 1} on that object any more, nor anything extending it"
                        : $"the {entry.component} component is no longer on that object, nor is anything extending it";
                    return false;
                }
            }

            SerializedProperty property = new SerializedObject(owner).FindProperty(entry.property);
            if (property == null)
            {
                reason = $"'{entry.property}' no longer exists, so the SDK probably renamed or removed it";
                return false;
            }

            propertyType = property.propertyType;

            string reference;
            string described = DescribeProperty(property, out reference);
            liveValue = string.IsNullOrEmpty(reference) ? described : reference;
            return true;
        }

        /// <summary>
        /// A prefab override stores a bool as "0" or "1", so a recorded value would print as '0' next to a live value
        /// of 'True' and leave the reader to translate. Show it the way the inspector does instead. Only applied to
        /// actual booleans, because an int of 0 has to stay 0.
        /// </summary>
        static string FormatForDisplay(string raw, SerializedPropertyType type)
        {
            if (type != SerializedPropertyType.Boolean) return raw;
            return Normalize(raw) == "true" ? "True" : "False";
        }

        Transform FindByRelativePath(string path)
        {
            if (string.IsNullOrEmpty(path) || path == "(root)") return transform;
            return transform.Find(path); // Transform.Find walks a slash-separated path and does find inactive children
        }

        /// <summary>
        /// Which of several same-typed components this one is. A type name alone does not identify a component: the
        /// SDK puts two UltrasoundManagers and two UltrasoundDisplayControllers on single GameObjects for the primary
        /// and biplane planes, so without this every setting on the second one resolved to the first and reported a
        /// difference that no amount of re-capturing could clear.
        /// </summary>
        static int IndexOfComponent(Component component)
        {
            string typeName = component.GetType().Name;
            int index = 0;

            foreach (Component candidate in component.gameObject.GetComponents<Component>())
            {
                if (candidate == null) continue;
                if (candidate == component) return index;
                if (candidate.GetType().Name == typeName) index++;
            }
            return 0;
        }

        /// <summary>
        /// Finds the component a recorded setting belongs to, counting the same way IndexOfComponent does so the two
        /// stay in step.
        ///
        /// Falls back to matching a subclass when the exact type is absent. A simulator can replace an SDK script with
        /// one that extends it, and because a snapshot records the type from the PREFAB, it always names the base
        /// class no matter how often you re-capture. The setting is inherited and genuinely present on the subclass
        /// instance, so an exact-name comparison would report a permanent phantom loss.
        ///
        /// Exact matches win, so precision is only relaxed when it has to be. The subclass pass is a superset of the
        /// exact pass, which makes the fallback safe to run second.
        /// </summary>
        static Component FindComponentByTypeName(GameObject go, string typeName, int index)
        {
            Component exact = MatchComponent(go, typeName, index, false);
            return exact != null ? exact : MatchComponent(go, typeName, index, true);
        }

        static Component MatchComponent(GameObject go, string typeName, int index, bool allowSubclass)
        {
            int seen = 0;

            foreach (Component component in go.GetComponents<Component>())
            {
                if (component == null) continue;

                bool matches = allowSubclass
                    ? DerivesFromTypeNamed(component, typeName)
                    : component.GetType().Name == typeName;

                if (!matches) continue;
                if (seen == index) return component;
                seen++;
            }
            return null;
        }

        static bool DerivesFromTypeNamed(Component component, string typeName)
        {
            for (System.Type type = component.GetType(); type != null; type = type.BaseType)
                if (type.Name == typeName) return true;
            return false;
        }

        /// The value a snapshot entry expects. Object references are recorded in their own field, so prefer that.
        static string ExpectedValue(ConfigEntry e) => string.IsNullOrEmpty(e.reference) ? e.value : e.reference;

        /// Short, countable phrase for the first console line, so a glance tells you the scale and kind of the problem.
        static string DriftSummary(int lost, int changed, int added)
        {
            List<string> parts = new List<string>();
            if (lost > 0) parts.Add(lost == 1 ? "1 setting lost" : $"{lost} settings lost");
            if (changed > 0) parts.Add(changed == 1 ? "1 setting changed" : $"{changed} settings changed");
            if (added > 0) parts.Add(added == 1 ? "1 setting added" : $"{added} settings added");
            return string.Join(", ", parts) + ".";
        }

        static string KeyOf(ConfigEntry e) => $"{e.targetPath}|{e.component}#{e.componentIndex}|{e.property}";

        /// Names the component number only when there is more than one, so the common case stays uncluttered but two
        /// UltrasoundManagers on one object are told apart.
        static string Describe(ConfigEntry e) => e.componentIndex > 0
            ? $"{e.targetPath} [{e.component} #{e.componentIndex + 1}] {e.property}"
            : $"{e.targetPath} [{e.component}] {e.property}";
        /// <summary>
        /// Compares a recorded value against the live one. This has to tolerate two different encodings: a prefab
        /// override records a bool as "1" or "0", while reading the live property back gives "True" or "False".
        /// Comparing those literally would report every boolean setting in the snapshot as changed.
        /// </summary>
        bool ValuesMatch(string expected, string live)
        {
            if (Normalize(expected) == Normalize(live)) return true;

            // Compare numerically where we can, so formatting differences do not read as real changes.
            float a, b;
            if (float.TryParse(expected, out a) && float.TryParse(live, out b))
                return Mathf.Abs(a - b) <= floatTolerance;

            return false;
        }

        static string Normalize(string value)
        {
            if (string.IsNullOrEmpty(value)) return "";
            if (value == "1" || value.Equals("true", System.StringComparison.OrdinalIgnoreCase)) return "true";
            if (value == "0" || value.Equals("false", System.StringComparison.OrdinalIgnoreCase)) return "false";
            return value;
        }

        bool ShouldIgnore(string propertyPath)
        {
            if (string.IsNullOrEmpty(propertyPath)) return true;
            if (ignorePropertiesContaining == null) return false;

            foreach (string fragment in ignorePropertiesContaining)
                if (!string.IsNullOrEmpty(fragment) && propertyPath.Contains(fragment)) return true;

            return false;
        }

        static int CompareEntries(ConfigEntry a, ConfigEntry b) => string.CompareOrdinal(KeyOf(a), KeyOf(b));

        /// Turns an override target into a readable hierarchy path plus component name. We use the path rather than
        /// a fileID because the whole point is that a human reads this file and diffs it in git.
        static void DescribeTarget(Object target, out string targetPath, out string componentName, out int componentIndex)
        {
            componentName = "";
            componentIndex = 0;
            targetPath = "";
            if (target == null) return;

            GameObject go = target as GameObject;
            Component component = target as Component;

            if (component != null)
            {
                componentName = component.GetType().Name;
                componentIndex = IndexOfComponent(component);
                go = component.gameObject;
            }

            if (go == null)
            {
                targetPath = target.name;
                return;
            }

            Transform root = go.transform;
            while (root.parent != null) root = root.parent;

            targetPath = GetRelativePath(go.transform, root);
            if (string.IsNullOrEmpty(targetPath)) targetPath = "(root)";
        }

        static string GetRelativePath(Transform target, Transform root)
        {
            if (target == root) return "(root)";

            string path = target.name;
            Transform parent = target.parent;
            while (parent != null && parent != root)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }

        static string DescribeReference(Object reference)
        {
            if (reference == null) return "";
            return $"{reference.name} ({reference.GetType().Name})";
        }

        static string DescribeProperty(SerializedProperty property, out string reference)
        {
            reference = "";

            switch (property.propertyType)
            {
                case SerializedPropertyType.ObjectReference:
                    reference = DescribeReference(property.objectReferenceValue);
                    return reference;
                case SerializedPropertyType.Integer: return property.intValue.ToString();
                case SerializedPropertyType.Boolean: return property.boolValue.ToString();
                case SerializedPropertyType.Float: return property.floatValue.ToString("R");
                case SerializedPropertyType.String: return property.stringValue;
                case SerializedPropertyType.Color: return property.colorValue.ToString();
                // The underlying int, not enumValueIndex: a prefab override records the real value, and an enum like
                // SCREEN_DIMENSIONS { LARGE = 1, SMALL = 2 } has an index that does not equal its value.
                case SerializedPropertyType.Enum: return property.intValue.ToString();
                case SerializedPropertyType.Vector2: return property.vector2Value.ToString("R");
                case SerializedPropertyType.Vector3: return property.vector3Value.ToString("R");
                case SerializedPropertyType.Vector4: return property.vector4Value.ToString("R");
                case SerializedPropertyType.Quaternion: return property.quaternionValue.ToString("R");
                case SerializedPropertyType.Bounds: return property.boundsValue.ToString();
                case SerializedPropertyType.Rect: return property.rectValue.ToString();
                case SerializedPropertyType.ArraySize: return property.intValue.ToString();
                case SerializedPropertyType.LayerMask: return property.intValue.ToString();
                default: return property.propertyType.ToString();
            }
        }

        /// Reports the SDK version the snapshot was taken under, so a mismatch alert can name the update that caused it.
        static string GetSdkVersion()
        {
            UnityEditor.PackageManager.PackageInfo info =
                UnityEditor.PackageManager.PackageInfo.FindForAssembly(typeof(ConfigSnapshotChecker).Assembly);
            if (info != null && !string.IsNullOrEmpty(info.version)) return info.version;

            // In the SDK's own development project the package lives under Assets, so there is no PackageInfo.
            const string inProjectManifest = "Assets/SMMARTS-SDK-Package/package.json";
            if (File.Exists(inProjectManifest))
            {
                PackageManifest manifest = JsonUtility.FromJson<PackageManifest>(File.ReadAllText(inProjectManifest));
                if (manifest != null && !string.IsNullOrEmpty(manifest.version)) return manifest.version + " (in-project)";
            }

            return "unknown";
        }

        [System.Serializable]
        class PackageManifest
        {
            public string version;
        }

#endif // UNITY_EDITOR
    }

    /// <summary>
    /// One recorded setting. Kept flat and string-based on purpose: the file is meant to be read by a human and
    /// diffed in git, so that comparing two SDK versions is just a diff of two snapshots.
    /// </summary>
    [System.Serializable]
    public class ConfigEntry
    {
        public string targetPath;   // hierarchy path of the object the setting lives on
        public string component;    // component type name, empty when the target is the GameObject itself
        public int componentIndex;  // which one, when a GameObject carries several components of the same type
        public string property;     // Unity's serialized property path, e.g. InvertInPlaneCogAid
        public string value;        // the value as text
        public string reference;    // set instead of value when the setting points at another object or asset
    }

    /// <summary>
    /// The snapshot file itself. snapshotFormatVersion exists so a future version of this tool can recognise and
    /// upgrade older files rather than silently misreading them, which is the exact failure it is built to detect.
    /// </summary>
    [System.Serializable]
    public class ConfigSnapshot
    {
        // 2 added componentIndex. Version 1 snapshots could not tell apart two components of the same type on one
        // GameObject, which the SDK does routinely for the primary and biplane ultrasound planes.
        public int snapshotFormatVersion = 2;
        public string capturedUtc;
        public string sdkPackageVersion;
        public string unityVersion;
        public string rootObjectName;
        public string captureMode;      // PrefabOverrides or FullSnapshot
        public string sourcePrefabPath;
        public ConfigEntry[] entries = new ConfigEntry[0];
    }
}
