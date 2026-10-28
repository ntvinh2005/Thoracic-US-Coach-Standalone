using UnityEngine;
using UnityEditor;

namespace SMMARTS
{
    /// <summary>
    /// Automatically sets up anatomical models dropped under the Anatomy GameObject.
    /// Adds mesh colliders (if no collider exists) and Identifiable Object scripts to all meshes.
    /// </summary>
    public class AnatomyAutoSetup : EditorWindow
    {
        [MenuItem("SMMARTS/Anatomy Import 3: Apply Components to Anatomy")]
        static void Init()
        {
            // Find the Anatomy GameObject in the scene
            GameObject anatomy = GameObject.Find("Anatomy");
            if (anatomy == null)
            {
                EditorUtility.DisplayDialog("Error", "No Anatomy GameObject found in scene.", "OK");
                return;
            }

            ProcessAllChildren(anatomy);
        }

        /// <summary>
        /// Recursively processes all children, setting up mesh colliders and Identifiable Object scripts
        /// </summary>
        static void ProcessAllChildren(GameObject anatomy)
        {
            int meshCollidersAdded = 0;
            int identifiableObjectsAdded = 0;

            foreach (Transform child in anatomy.transform)
            {
                // Process everything but the Bounding Box, which is only there to aid in anatomy alignment.
                if (child.gameObject.name != "Bounding Box")
                {
                    ProcessGameObjectRecursively(child.gameObject, ref meshCollidersAdded, ref identifiableObjectsAdded);
                }
            }

            // Mark scene as modified
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(
                UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene());

            // Show summary popup
            string message = $"Automatic Index Plate Anatomy Processing complete!\n\n" +
                           $"Mesh Colliders added: {meshCollidersAdded}\n" +
                           $"Identifiable Objects added: {identifiableObjectsAdded}\n\n" +
                           $"Please check the layers, tags, and IdentifiableObject settings of the anatomy objects.";
            EditorUtility.DisplayDialog("Anatomy Auto Setup", message, "OK");
        }

        /// <summary>
        /// Recursively processes a GameObject and all its children
        /// </summary>
        static void ProcessGameObjectRecursively(GameObject obj, ref int meshCollidersAdded, ref int identifiableObjectsAdded)
        {
            // Process this object if it has a mesh
            if (obj.GetComponent<MeshRenderer>() != null || obj.GetComponent<MeshFilter>() != null)
            {
                // Register undo for this object
                Undo.RecordObject(obj, "Apply Anatomy Components");

                // Add mesh collider if no collider exists
                if (obj.GetComponent<Collider>() == null)
                {
                    Undo.AddComponent<MeshCollider>(obj);
                    meshCollidersAdded++;
                }

                // Add Identifiable Object script if missing
                if (obj.GetComponent<IdentifiableObject>() == null)
                {
                    Undo.AddComponent<IdentifiableObject>(obj);
                    identifiableObjectsAdded++;
                }
            }

            // Recursively process all children
            foreach (Transform child in obj.transform)
            {
                ProcessGameObjectRecursively(child.gameObject, ref meshCollidersAdded, ref identifiableObjectsAdded);
            }
        }
    }
}