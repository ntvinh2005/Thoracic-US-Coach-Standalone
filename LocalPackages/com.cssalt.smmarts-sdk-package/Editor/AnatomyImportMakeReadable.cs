using UnityEngine;
using UnityEditor;

namespace SMMARTS
{
    /// <summary>
    /// Automatically enables Read/Write on imported FBX models
    /// </summary>
    public class AnatomyImportMakeReadable : AssetPostprocessor
    {
        void OnPreprocessModel()
        {
            ModelImporter modelImporter = assetImporter as ModelImporter;

            if (modelImporter != null && !modelImporter.isReadable)
            {
                // Enable Read/Write
                modelImporter.isReadable = true;

                Debug.LogWarning($"[SMMARTS] Auto-enabled Read/Write for model: {assetPath}\n" +
                                "If this is not desired, uncheck Read/Write in the import settings and reimport.");
            }
        }
    }
}