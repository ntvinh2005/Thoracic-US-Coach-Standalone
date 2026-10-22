using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using SMMARTS;

/// <summary>
/// IdentifiableObject: Makes things clickable and tells you what they are
/// 
/// PURPOSE:
/// Turn any object into an educational "What's that thingy?" experience
/// Works for anatomical structures, medical devices, mystery blobs, and 
/// those weird shapes that make students go "...what IS that?"
/// 
/// MAGIC FEATURES:
/// - Touch it in 3D → it glows AND highlights in ultrasound  
/// - Touch it in ultrasound → it glows in 3D AND highlights in ultrasound
/// - Bidirectional identification magic that makes students say "Ohhhhh!"
/// 
/// INHERITANCE:
/// Extends UltrasoundVisibility to add 3D touch identification while maintaining
/// all existing ultrasound rendering capabilities. Backwards compatible with
/// existing code that expects UltrasoundVisibility components.
/// 
/// UNIFIED HIGHLIGHT SYSTEM:
/// Single timer manages both 3D glow material AND ultrasound highlight color
/// No double timers, no desync issues, just pure highlight harmony
/// </summary>
public class IdentifiableObject : UltrasoundVisibility
{
    [Header("3D Highlight Settings")]
    [Tooltip("Material used for 3D highlighting (added to renderer materials) - if unassigned, we'll use the one given to us from TouchIdentification")]
    [SerializeField] private Material highlightMaterial;

    [Tooltip("Color used for ultrasound highlighting")]
    [SerializeField] private Color ultrasoundHighlightColor = Color.magenta;

    // 3D highlight management
    private Renderer objectRenderer;
    private Material[] originalMaterials;
    private Coroutine highlightCoroutine;
    public bool isHighlighted = false;

    // Ultrasound highlight management
    private Color originalUltrasoundColor;
    private UltrasoundVisibility.RenderType originalRenderType;

    [Header("Fluoroscopy Settings")]
    [Tooltip("The C-Arm Fluoroscope image is created by voxelizing static meshes (typically bones). Should this object be one of them? ")]
    public bool isVoxelizableForFluoro = false;


    /// <summary>
    /// Initialize the IdentifiableObject
    /// Handles both UltrasoundVisibility setup AND 3D touch capabilities
    /// </summary>
    public void Start()
    {
        // Handle UltrasoundVisibility initialization (same as parent Start())
        if (TouchDisplayMessage == "") TouchDisplayMessage = transform.name;

        // Initialize 3D highlight system
        objectRenderer = GetComponent<Renderer>();
        if (objectRenderer != null)
        {
            originalMaterials = objectRenderer.materials;
        }

        // Store original ultrasound appearance
        originalUltrasoundColor = Color;
        originalRenderType = Type;
    }

    /// <summary>
    /// Start unified highlight (called by TouchIdentification or ultrasound touch)
    /// Highlights both 3D object AND ultrasound appearance simultaneously
    /// 
    /// SYNCHRONIZED MAGIC:
    /// - Adds glow material to 3D renderer
    /// - Changes ultrasound color to highlight color
    /// - Single timer manages both highlight states
    /// - Prevents duplicate highlights with restart capability
    /// </summary>
    public virtual void StartHighlight(Material glowMaterial, float duration)
    {
        // Stop existing highlight if running (restart capability)
        if (highlightCoroutine != null)
        {
            StopCoroutine(highlightCoroutine);
        }

        // Use provided material or fallback to assigned material
        Material materialToUse = glowMaterial != null ? glowMaterial : highlightMaterial;

        // Start 3D highlight
        if (materialToUse != null)
        {
            Add3DHighlight(materialToUse);
        }
        else
        {
            Debug.LogWarning($"IdentifiableObject on {gameObject.name}: No highlight material available for 3D glow");
        }

        // Start ultrasound highlight
        StartUltrasoundHighlight();

        // Mark as highlighted
        isHighlighted = true;

        // Start unified timer
        highlightCoroutine = StartCoroutine(UnifiedHighlightTimeout(duration));

        Debug.Log($"IdentifiableObject: Started unified highlight on {gameObject.name} for {duration} seconds");
    }

    /// <summary>
    /// Add 3D glow material to renderer (prevents duplicates)
    /// </summary>
    public void Add3DHighlight(Material glowMaterial)
    {
        if (objectRenderer == null) return;

        // Check if highlight material is already added (prevent duplicates)
        if (objectRenderer.materials.Contains(glowMaterial))
        {
            return; // Already highlighted
        }

        // Add highlight material to array
        List<Material> materials = new List<Material>(objectRenderer.materials);
        materials.Add(glowMaterial);
        objectRenderer.materials = materials.ToArray();

        Debug.Log($"IdentifiableObject: Added 3D highlight to {gameObject.name}");
    }

    /// <summary>
    /// Remove 3D glow material and restore original materials
    /// </summary>
    public void Remove3DHighlight()
    {
        if (objectRenderer != null && originalMaterials != null)
        {
            objectRenderer.materials = originalMaterials;
            Debug.Log($"IdentifiableObject: Removed 3D highlight from {gameObject.name}");
        }
    }

    /// <summary>
    /// Start ultrasound highlight (change color to highlight color)
    /// </summary>
    public void StartUltrasoundHighlight()
    {
        // Change ultrasound appearance to highlight color
        Color = ultrasoundHighlightColor;

        // You might want to change render type too for more dramatic effect
        Type = UltrasoundVisibility.RenderType.Normal; // Or whatever looks good

        Debug.Log($"IdentifiableObject: Started ultrasound highlight on {gameObject.name}");
    }

    /// <summary>
    /// Remove ultrasound highlight and restore original appearance
    /// </summary>
    public void RemoveUltrasoundHighlight()
    {
        // Restore original ultrasound appearance
        Color = originalUltrasoundColor;
        Type = originalRenderType;

        Debug.Log($"IdentifiableObject: Removed ultrasound highlight from {gameObject.name}");
    }

    /// <summary>
    /// Unified highlight timeout coroutine
    /// Manages both 3D and ultrasound highlight removal simultaneously
    /// </summary>
    private IEnumerator UnifiedHighlightTimeout(float duration)
    {
        yield return new WaitForSeconds(duration);

        // Remove both highlights simultaneously
        Remove3DHighlight();
        RemoveUltrasoundHighlight();

        // Clear highlight state
        isHighlighted = false;
        highlightCoroutine = null;

        Debug.Log($"IdentifiableObject: Unified highlight timeout completed on {gameObject.name}");
    }

    /// <summary>
    /// Public property to check if object is currently highlighted
    /// Useful for external systems that need to know highlight state
    /// </summary>
    public bool IsHighlighted => isHighlighted;

    /// <summary>
    /// Cleanup highlight if object is destroyed
    /// Note: UltrasoundVisibility doesn't have OnDestroy, so this is just our cleanup
    /// </summary>
    void OnDestroy()
    {
        if (highlightCoroutine != null)
        {
            StopCoroutine(highlightCoroutine);
        }
    }

    /// <summary>
    /// Editor helper to show current identification and highlight info
    /// </summary>
    [ContextMenu("Show Object Info")]
    public void ShowObjectInfo()
    {
        Debug.Log($"IdentifiableObject on {gameObject.name}:");
        Debug.Log($"- Display Name: '{TouchDisplayMessage}'");
        Debug.Log($"- Touch Enabled: {EnableTouchDisplayMessage}");
        Debug.Log($"- Layer: {LayerMask.LayerToName(gameObject.layer)}");
        Debug.Log($"- Has Renderer: {objectRenderer != null}");
        Debug.Log($"- Currently Highlighted: {isHighlighted}");
        Debug.Log($"- Ultrasound Color: {Color}");
        Debug.Log($"- Ultrasound Type: {Type}");
        Debug.Log($"- Highlight Material: {(highlightMaterial != null ? highlightMaterial.name : "None")}");
    }

    /// <summary>
    /// Editor helper to test highlight system
    /// </summary>
    [ContextMenu("Test Highlight")]
    public void TestHighlight()
    {
        if (Application.isPlaying)
        {
            StartHighlight(highlightMaterial, 5f);
        }
        else
        {
            Debug.Log("Test highlight only works in play mode");
        }
    }


    /// <summary>
    /// Called when values change in the inspector - handles automatic layer assignment
    /// </summary>
    
    void OnValidate()
    {
#if UNITY_EDITOR

        // lets assume bone is always on fluoro, and no other tissues are.
        if (AppearancePreset == UltrasoundPresets.Bone) isVoxelizableForFluoro = true;

        // Call parent's OnValidate first. AppearancePreset will return to none after all settings are applied. 
        base.OnValidate();

        // Check if isVoxelizableForFluoro was just enabled
        if (isVoxelizableForFluoro)
        {
            // Find the "US and Fluoroscopy Display" layer number
            int targetLayer = LayerMask.NameToLayer("US and Fluoroscopy Display");

            if (targetLayer != -1) // Layer exists
            {
                if (gameObject.layer != targetLayer)
                {
                    gameObject.layer = targetLayer;
                    UnityEditor.EditorUtility.SetDirty(gameObject);

                    Debug.Log($"This object has been moved to layer US and Fluoroscopy Display");

                    // Show popup alert
                    UnityEditor.EditorUtility.DisplayDialog("Layer Changed",
                        "This object has been moved to layer:\n\nUS and Fluoroscopy Display",
                        "OK");
                }
            }
            else
            {
                Debug.LogWarning("Layer 'US and Fluoroscopy Display' not found. Make sure layers are set up properly.");
            }
        }
#endif
    }
    
}