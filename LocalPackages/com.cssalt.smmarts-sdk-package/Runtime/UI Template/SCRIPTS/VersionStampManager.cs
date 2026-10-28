using System.IO;
using UnityEngine;
using System;
using System.Text.RegularExpressions;

/// <summary>
/// VersionStampManager: Centralized version information provider for SMMARTS SDK
/// 
/// PURPOSE:
/// Provides consistent version information across the entire project by reading from:
/// 1. Unity package.json (when consumed as a package)
/// 2. Package manifest files (for git branch information)
/// 3. Automatic fallback for SDK development scenarios
/// 
/// DESIGN PHILOSOPHY:
/// - Single source of truth for all version displays
/// - Singleton pattern ensures consistency across scenes
/// - Editor generates fresh timestamps, builds use frozen data
/// - ScriptableObject persistence for reliable build-time freezing
/// 
/// USAGE:
/// Other scripts access version info via:
/// - VersionStampManager.Instance.SDK_Version
/// - VersionStampManager.Instance.Timestamp  
/// - VersionStampManager.Instance.VersionTimeStamp
/// 
/// TIMESTAMP FORMAT:
/// MMddyyHHmm (24-hour format)
/// Example: 0110252122 = January 10, 2025 at 9:22 PM
/// 
/// VERSION EXAMPLES:
/// - Package consumption: "VSDK2.1.0[Beta-2.0].0110252122"
/// - SDK development: "VSDK[DEV].0110252122"
/// - Build (frozen): "VSDK2.1.0[Beta-2.0].0110252122"
/// 
/// Dave L, Updated to centralized version management with build-time freezing
/// </summary>
namespace SMMARTS
{
    public class VersionStampManager : MonoBehaviour
    {
        #region Singleton Pattern
        /// <summary>
        /// Singleton instance for global access
        /// Ensures only one version manager exists across the entire project
        /// </summary>
        public static VersionStampManager ME { get; private set; }
        #endregion

        #region Configuration
        [Header("Package Configuration")]
        [Tooltip("Name of your SMMARTS SDK package - DO NOT CHANGE")]
        [SerializeField] private string packageName = "com.cssalt.smmarts-sdk-package";

        [Header("Version Data Asset")]
        [Tooltip("ScriptableObject that stores the persistent version information (auto-created if missing)")]
        public VersionData versionData;
        #endregion

        #region Public Properties
        /// <summary>
        /// SDK version with branch info (e.g., "SDK2.1.0[Beta-2.0]")
        /// Reads from persistent ScriptableObject data
        /// </summary>
        public string SDK_Version => versionData != null ? versionData.sdkVersion : "SDKX";

        /// <summary>
        /// Timestamp only in MMddyyHHmm format (e.g., "0110252122")
        /// Represents when the version was generated (frozen at build time)
        /// </summary>
        public string Timestamp => versionData != null ? versionData.timestamp : "UNKNOWN";

        /// <summary>
        /// Complete version string for display (e.g., "VSDK2.1.0[Beta-2.0].0110252122")
        /// This is the primary version string used throughout the application
        /// </summary>
        public string VersionTimeStamp => versionData != null ? versionData.versionTimeStamp : "VSDKX.UNKNOWN";
        #endregion

        #region Unity Lifecycle
        /// <summary>
        /// Initialize singleton instance and set up version system
        /// Ensures only one VersionStampManager exists and persists across scenes
        /// </summary>
        void Awake()
        {
            // Singleton pattern with DontDestroyOnLoad for cross-scene persistence
            if (ME == null)
            {
                ME = this;
                DontDestroyOnLoad(gameObject);
                InitializeVersionInfo();
            }
            else
            {
                // Destroy duplicate instances to maintain singleton
                Destroy(gameObject);
            }
        }

        /// <summary>
        /// Secondary initialization point for version generation
        /// Ensures version info is updated even if Awake timing is inconsistent
        /// </summary>
        void Start()
        {
            // Generate fresh version info in editor (covers various execution scenarios)
#if UNITY_EDITOR
            GenerateVersionInfo();
#endif
        }
        #endregion

        #region Version System Core
        /// <summary>
        /// Main version system initialization
        /// Behavior differs between Editor and Build:
        /// - Editor: Generate fresh version info with current timestamp
        /// - Build: Use frozen ScriptableObject data from build time
        /// </summary>
        private void InitializeVersionInfo()
        {
#if UNITY_EDITOR
            // EDITOR MODE: Generate fresh version info every time
            GenerateVersionInfo();
            Debug.Log($"VersionStampManager (Editor): {VersionTimeStamp} (from {GetVersionSource()})");
#else
            // BUILD MODE: Use ScriptableObject values (frozen at build time)
            if (versionData == null || string.IsNullOrEmpty(versionData.versionTimeStamp))
            {
                Debug.LogWarning("VersionStampManager (Build): No version data available, using fallback");
            }
            else
            {
                Debug.Log($"VersionStampManager (Build): {VersionTimeStamp} [Frozen at build time]");
            }
#endif
        }
        #endregion

        #region Editor-Only Version Generation
#if UNITY_EDITOR
        /// <summary>
        /// Generate fresh version information (Editor only)
        /// 
        /// PROCESS:
        /// 1. Auto-create VersionData ScriptableObject if missing
        /// 2. Read package version from package.json
        /// 3. Extract branch name from manifest files
        /// 4. Generate fresh timestamp (24-hour format)
        /// 5. Combine into final version strings
        /// 6. Update ScriptableObject (persists to builds)
        /// 
        /// VERSION FORMAT:
        /// SDK{version}[{branch}].{timestamp}
        /// Example: "VSDK2.1.0[Beta-2.0].0110252122"
        /// </summary>
        private void GenerateVersionInfo()
        {
            // AUTO-CREATE SCRIPTABLEOBJECT IF MISSING
            if (versionData == null)
            {
                // Try to find existing VersionData in Assets root
                string assetPath = "Assets/VersionData.asset";
                versionData = UnityEditor.AssetDatabase.LoadAssetAtPath<VersionData>(assetPath);

                if (versionData == null)
                {
                    // Create new VersionData asset in Assets root (rooty enough!)
                    versionData = ScriptableObject.CreateInstance<VersionData>();
                    UnityEditor.AssetDatabase.CreateAsset(versionData, assetPath);
                    UnityEditor.AssetDatabase.SaveAssets();
                    Debug.Log("Created new VersionData asset at Assets/VersionData.asset");

                    // Assign to this component so it shows up in inspector
                    UnityEditor.EditorUtility.SetDirty(this);
                }
            }

            // GATHER VERSION COMPONENTS
            string packageVersion = GetPackageVersion();  // From package.json or [DEV]
            string branchName = GetBranchName();          // From manifest files

            // GENERATE FRESH TIMESTAMP (24-hour format for maximum impressiveness)
            // Format: MMddyyHHmm (Month Day Year Hour24 Minute)
            // Example: 0110252122 = January 10, 2025 at 9:22 PM
            string timestamp = System.DateTime.Now.ToString("yyMMddHHmm");

            // BUILD SDK VERSION STRING
            // With non-main branch: "SDK2.1.0[Beta-2.0]"
            // Main branch or no branch: "SDK2.1.0" or "SDK[DEV]"
            string sdkVersion = (!string.IsNullOrEmpty(branchName) && branchName != "main") ?
                $"SDK{packageVersion}[{branchName}]" :
                $"SDK{packageVersion}";

            // BUILD COMPLETE VERSION STRING
            // Final format: "VSDK2.1.0[Beta-2.0].0110252122"
            string versionTimeStamp = $"V{sdkVersion}.{timestamp}";

            // UPDATE SCRIPTABLEOBJECT (this data persists to builds)
            versionData.SetVersionInfo(sdkVersion, timestamp, versionTimeStamp);
        }

        /// <summary>
        /// Get package version from package.json or fallback to [DEV]
        /// 
        /// LOGIC:
        /// 1. Try to read from Packages/{packageName}/package.json
        /// 2. If found, return version field (e.g., "2.1.0")
        /// 3. If not found, return "[DEV]" (SDK development scenario)
        /// 
        /// SCENARIOS:
        /// - Package consumption: Returns actual version like "2.1.0"
        /// - SDK development: Returns "[DEV]" (no package.json available)
        /// </summary>
        private string GetPackageVersion()
        {
            // This covers the edge case where an SDK developer like me is working 
            // in the project that creates the package.
            string version = GetVersionFromPackage();
            if (!string.IsNullOrEmpty(version))
            {
                return version;
            }

            // Version to use when developing inside the SDK itself (no package.json available) 
            // is simply [DEV]
            return "[DEV]";
        }

        /// <summary>
        /// Extract git branch name from Unity package manifest files
        /// 
        /// PROCESS:
        /// 1. Check Packages/manifest.json for git URL with branch
        /// 2. Check Packages/packages-lock.json as fallback
        /// 3. Use regex to extract branch name after '#' character
        /// 
        /// EXAMPLE:
        /// URL: "https://github.com/CSSALT/SMMARTS-SDK-Internal.git#Beta-2.0"
        /// Returns: "Beta-2.0"
        /// 
        /// RETURNS:
        /// - Branch name if found (e.g., "Beta-2.0")
        /// - null if no branch info found
        /// </summary>
        private string GetBranchName()
        {
            // Try both manifest files (Unity versions use different files)
            string branch = GetBranchFromManifest("Packages/manifest.json");
            if (string.IsNullOrEmpty(branch))
            {
                branch = GetBranchFromManifest("Packages/packages-lock.json");
            }

            return branch;
        }

        /// <summary>
        /// Extract branch name from a specific manifest file
        /// 
        /// REGEX PATTERN:
        /// Searches for: "com.cssalt.smmarts-sdk-package": "https://...#BranchName"
        /// Captures: Everything after the '#' character
        /// 
        /// ERROR HANDLING:
        /// - File not found: Returns null
        /// - JSON parse error: Logs warning, returns null
        /// - No match found: Returns null
        /// </summary>
        private string GetBranchFromManifest(string manifestPath)
        {
            try
            {
                if (!File.Exists(manifestPath))
                    return null;

                string jsonContent = File.ReadAllText(manifestPath);

                // Look for our package in the JSON with regex
                // Pattern matches: "packageName": "url#branch"
                // Captures: branch name after '#'
                string pattern = $"\"{packageName}\"\\s*:\\s*\"[^#]*#([^\"]+)\"";
                Match match = Regex.Match(jsonContent, pattern);

                if (match.Success)
                {
                    string branch = match.Groups[1].Value;
                    Debug.Log($"Found branch '{branch}' in {manifestPath}");
                    return branch;
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Error reading branch from {manifestPath}: {e.Message}");
            }

            return null;
        }

        /// <summary>
        /// Read version from Unity package.json file
        /// 
        /// PROCESS:
        /// 1. Look for package.json in Packages/{packageName}/
        /// 2. Parse JSON to extract version field
        /// 3. Return version string (e.g., "2.1.0")
        /// 
        /// ERROR HANDLING:
        /// - File not found: Returns null
        /// - JSON parse error: Logs warning, returns null
        /// - Empty version field: Returns null
        /// </summary>
        private string GetVersionFromPackage()
        {
            try
            {
                string packagePath = $"Packages/{packageName}/package.json";

                if (File.Exists(packagePath))
                {
                    string jsonContent = File.ReadAllText(packagePath);
                    PackageInfo packageInfo = JsonUtility.FromJson<PackageInfo>(jsonContent);

                    if (!string.IsNullOrEmpty(packageInfo.version))
                    {
                        Debug.Log($"Found SMMARTS SDK version {packageInfo.version} in {packagePath}");
                        return packageInfo.version;
                    }
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"Error reading package.json: {e.Message}");
            }

            return null;
        }

        /// <summary>
        /// Get human-readable description of version source (for debugging)
        /// 
        /// RETURNS:
        /// - "package.json + branch from manifest" (normal package consumption)
        /// - "package.json" (package without branch info)
        /// - "SDK development version" (SDK development scenario)
        /// </summary>
        private string GetVersionSource()
        {
            if (!string.IsNullOrEmpty(GetVersionFromPackage()))
                return "package.json" + (!string.IsNullOrEmpty(GetBranchName()) ? $" + branch from manifest" : "");
            else
                return "SDK development version";
        }

        /// <summary>
        /// Data structure for parsing Unity package.json files
        /// Only includes fields we actually need for version extraction
        /// </summary>
        [System.Serializable]
        public class PackageInfo
        {
            public string version;      // The version field we're interested in
            public string displayName;  // Human-readable name (unused)
            public string name;         // Package identifier (unused)
        }

        #region Editor Helper Methods
        /// <summary>
        /// Force refresh version information (Editor context menu)
        /// Useful for testing or when version info seems stale
        /// </summary>
        [ContextMenu("Force Refresh Version")]
        public void ForceRefreshVersion()
        {
            GenerateVersionInfo();
        }

        /// <summary>
        /// Display current version information in console (Editor context menu)
        /// Useful for debugging version detection issues
        /// </summary>
        [ContextMenu("Show Version Info")]
        public void ShowVersionInfo()
        {
            Debug.Log($"Package Name: {packageName}");
            Debug.Log($"SDK_Version: {SDK_Version}");
            Debug.Log($"Timestamp: {Timestamp}");
            Debug.Log($"VersionTimeStamp: {VersionTimeStamp}");
            Debug.Log($"Version Source: {GetVersionSource()}");
        }
        #endregion

#endif
        #endregion
    }
}