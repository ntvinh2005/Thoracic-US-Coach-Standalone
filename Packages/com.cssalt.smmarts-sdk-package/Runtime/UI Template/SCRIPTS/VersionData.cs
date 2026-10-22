using UnityEngine;

namespace SMMARTS
{
    [CreateAssetMenu(fileName = "VersionData", menuName = "SMMARTS/Version Data")]
    public class VersionData : ScriptableObject
    {
        [Header("Version Information (Generated in Editor, Frozen in Builds)")]
        public string sdkVersion = "";
        public string timestamp = "";
        public string versionTimeStamp = "";

        public void SetVersionInfo(string sdk, string time, string full)
        {
            sdkVersion = sdk;
            timestamp = time;
            versionTimeStamp = full;

#if UNITY_EDITOR
            UnityEditor.EditorUtility.SetDirty(this);
#endif
        }
    }
}