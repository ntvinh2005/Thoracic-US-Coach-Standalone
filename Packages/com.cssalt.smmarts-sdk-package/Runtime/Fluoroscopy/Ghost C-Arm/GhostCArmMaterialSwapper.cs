using UnityEngine;

[RequireComponent(typeof(MeshRenderer))]
public class GhostCArmMaterialSwapper : MonoBehaviour
{
    private Material originalMaterial;

    void Awake()
    {
        originalMaterial = GetComponent<MeshRenderer>().material;
    }

    public void SetGreen()
    {
        GetComponent<MeshRenderer>().material = GhostCArmManager.ME.GhostGreen;
    }
    public void SetRed()
    {
        GetComponent<MeshRenderer>().material = GhostCArmManager.ME.GhostRed;
    }

    public void SetDefault()
    {
        GetComponent<MeshRenderer>().material = originalMaterial;
    }

}