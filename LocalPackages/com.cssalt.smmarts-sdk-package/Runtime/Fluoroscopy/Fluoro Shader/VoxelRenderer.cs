using UnityEngine;

[RequireComponent(typeof(MeshRenderer))]
public class VoxelRenderer : MonoBehaviour
{
    [Header("Voxel Source")]
    public MeshVoxelizer voxelizer; // Drag the voxelized object here
    
    [Header("Material")]
    public Material voxelMaterial; // Drag the VoxelVisualization material here


    [Header("Settings")]
    [Range(0.1f, 10f)]
    public float density = 1.0f;
    
    [Range(0.001f, 0.1f)]
    public float stepSize = 0.01f;
    
    [Range(0.1f, 1f)]
    public float contrast = 1f;

    [Header("Camera")]
    public Camera camera;

    public bool InvertColors; // black vs white

    private MeshRenderer meshRenderer;
    
    void Start()
    {
        meshRenderer = GetComponent<MeshRenderer>();
        
        if (voxelMaterial != null)
        {
            meshRenderer.material = voxelMaterial;
        }
        
        UpdateVoxelTexture();
    }
    
    void Update()
    {
        // Update material properties in real-time
        if (meshRenderer.material != null)
        {
            meshRenderer.material.SetFloat("_Density", density);
            meshRenderer.material.SetFloat("_StepSize", stepSize);
            meshRenderer.material.SetFloat("_Contrast", contrast);
            if (InvertColors) {
                meshRenderer.material.SetFloat("_InvertColors", 1);
                camera.backgroundColor = Color.black;
            } else { 
                meshRenderer.material.SetFloat("_InvertColors", 0);
                camera.backgroundColor = Color.white;
            }
        }
    }
    
void UpdateVoxelTexture()
{
    if (voxelizer == null || voxelMaterial == null) return;
    
    RenderTexture voxelTexture = voxelizer.GetVoxelTexture();
    
    Debug.Log($"UpdateVoxelTexture called:");
    Debug.Log($"  Got texture: {voxelTexture?.name} (null? {voxelTexture == null})");
    Debug.Log($"  Current material texture: {voxelMaterial.GetTexture("_VoxelTexture")?.name}");
    
    if (voxelTexture != null)
    {
        voxelMaterial.SetTexture("_VoxelTexture", voxelTexture);
        Debug.Log($"  Set material texture to: {voxelTexture.name}");
        Debug.Log($"  Verify material now has: {voxelMaterial.GetTexture("_VoxelTexture")?.name}");
    }
    else
    {
        Debug.LogWarning("No voxel texture found - make sure voxelizer has run");
    }
}

    public void RefreshVoxelTexture()
    {
        Debug.Log("=== RefreshVoxelTexture called ===");
        UpdateVoxelTexture();
    }

}