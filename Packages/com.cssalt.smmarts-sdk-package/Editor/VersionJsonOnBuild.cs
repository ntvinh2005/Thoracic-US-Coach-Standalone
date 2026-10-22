using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using System.IO;
using UnityEngine;
using SMMARTS;

// Simple serializable class for JSON export
[System.Serializable]
public class VersionJsonData
{
    public string version;
}

public class VersionJsonOnBuild : IPreprocessBuildWithReport
{
    public int callbackOrder => 0;

    public void OnPreprocessBuild(BuildReport report)
    {
        // Get timestamp from VersionStampManager
        string timestamp = System.DateTime.Now.ToString("yyMMddHHmm");

        // Create simple JSON data
        VersionJsonData jsonData = new VersionJsonData { version = timestamp };
        string json = JsonUtility.ToJson(jsonData, true);

        // Get the build output path
        string buildPath = Path.GetDirectoryName(report.summary.outputPath);
        string versionPath = Path.Combine(buildPath, "version.json");

        File.WriteAllText(versionPath, json);

        Debug.Log($"Version file created at: {versionPath} with version: {timestamp}");
    }
}