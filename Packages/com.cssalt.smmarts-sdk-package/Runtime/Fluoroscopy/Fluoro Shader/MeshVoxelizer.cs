using System.Linq;
using UnityEngine;
using TMPro;

public class MeshVoxelizer : MonoBehaviour
{
    [Header("Manual Setup")]
    public Transform voxelBounds; // Drag a cube/empty GO here to define scan volume
    public int voxelResolution = 64;
    public ComputeShader voxelizerShader;

    public VoxelRenderer voxelRenderer;

    [SerializeField] private TMP_InputField VoxelResolutionInputField;


    [Header("Runtime Re-voxelization")]
    public bool enableRuntimeVoxelization = false;
    [Range(0f, 2f)]
    public float voxelizationInterval = 1f;

    public Material voxelMaterial; // The original material asset from Project
    public MeshRenderer displayMeshRenderer; // The renderer we want to apply it to

    public bool Static; // if this is true, you can't revoxelize.

    public bool VoxelsDone = false;

    [Header("Debug")]
    public bool showVoxelBounds = true;

    private RenderTexture voxelTexture;
    private float lastVoxelizationTime = 0f;



    void Start()
    {

        if (int.TryParse(VoxelResolutionInputField.text, out int value))
        {
            voxelResolution = value;
            Debug.Log($"Set value to: {value}");
        }
        else
        {
            Debug.LogWarning("Invalid number entered");
            // and voxelResolution is the default (64 or whatever you have in the inspector)
        }

        VoxelizeMesh();
    }

    void Update()
    {

        if (Input.GetKeyDown(KeyCode.Space))
        {
            if (Static == false)
            {
                //  Debug.Log("SPACEBAR RE-VOXELIZE TEST");
                //  ReVoxelize();
            }
        }

    }


    void VoxelizeMesh()
    {
        if (voxelizerShader == null || voxelBounds == null) return;

        VoxelsDone = false;

        // Clean up old texture
        if (voxelTexture != null)
        {
            voxelTexture.Release();
            voxelTexture = null;
        }

        // Create fresh 3D texture
        voxelTexture = new RenderTexture(voxelResolution, voxelResolution, 0, RenderTextureFormat.RFloat);
        voxelTexture.name = "VoxelTexture_" + System.DateTime.Now.Ticks;
        voxelTexture.dimension = UnityEngine.Rendering.TextureDimension.Tex3D;
        voxelTexture.volumeDepth = voxelResolution;
        voxelTexture.enableRandomWrite = true;
        voxelTexture.Create();

        // CLEAR the texture first
        Graphics.SetRenderTarget(voxelTexture);
        GL.Clear(true, true, Color.black);
        Graphics.SetRenderTarget(null);

        // ADD THIS TEST
        // System.Threading.Thread.Sleep(10); // Give GPU time to clear

        // ADD THESE LINES:
        GL.Flush();
        // GL.Finish(); // Force GPU to complete all operations

        Debug.Log($"Created fresh voxel texture: {voxelTexture.name}");

        // Use manual bounds
        Bounds bounds = GetManualBounds();
        Debug.Log($"Voxel bounds: center={bounds.center}, size={bounds.size}");

        // Get all meshes but process them SEPARATELY
        // we used to use a specific tag for this purpose
        // MeshFilter[] meshFilters = GameObject.FindGameObjectsWithTag("Fluoro Voxelize")
        //    .Where(go => go.activeInHierarchy && go.GetComponent<MeshFilter>() != null)
        //    .Select(go => go.GetComponent<MeshFilter>())
        //    .ToArray();

        MeshFilter[] meshFilters = FindObjectsByType<IdentifiableObject>(FindObjectsSortMode.None)
            .Where(obj => obj.isVoxelizableForFluoro && obj.gameObject.activeInHierarchy)
            .Select(obj => obj.GetComponent<MeshFilter>())
            .Where(meshFilter => meshFilter != null)
            .ToArray();

        Debug.Log($"Found {meshFilters.Length} meshes to voxelize separately");

        int kernelHandle = voxelizerShader.FindKernel("CSMain");

        // Process each mesh individually
        for (int meshIndex = 0; meshIndex < meshFilters.Length; meshIndex++)
        {
            MeshFilter meshFilter = meshFilters[meshIndex];
            Mesh mesh = meshFilter.sharedMesh;

            Debug.Log($"Voxelizing mesh {meshIndex}: {meshFilter.name} ({mesh.triangles.Length / 3} triangles)");

            // Create triangle buffer for THIS mesh only
            ComputeBuffer triangleBuffer = CreateSingleMeshTriangleBuffer(mesh, meshFilter.transform);




            // Set up compute shader for this mesh
            voxelizerShader.SetBuffer(kernelHandle, "triangles", triangleBuffer);
            voxelizerShader.SetTexture(kernelHandle, "voxelTexture", voxelTexture);
            voxelizerShader.SetInt("voxelResolution", voxelResolution);
            voxelizerShader.SetVector("boundsMin", bounds.min);
            voxelizerShader.SetVector("boundsSize", bounds.size);
            voxelizerShader.SetInt("triangleCount", mesh.triangles.Length / 3);
            voxelizerShader.SetFloat("densityValue", 1.0f); // Each mesh adds 1.0 density

            // Run compute shader for this mesh
            int threadGroups = Mathf.CeilToInt(voxelResolution / 4.0f);


            // Right before the Dispatch, add this comprehensive logging:
            Debug.Log($"=== COMPUTE SHADER PARAMETERS ===");
            Debug.Log($"Triangle count: {mesh.triangles.Length / 3}");
            Debug.Log($"Bounds min: {bounds.min}, size: {bounds.size}");
            Debug.Log($"Voxel resolution: {voxelResolution}");
            Debug.Log($"Density value: 1.0");
            Debug.Log($"Texture name: {voxelTexture.name}");
            Debug.Log($"Thread groups: {threadGroups}");


            voxelizerShader.Dispatch(kernelHandle, threadGroups, threadGroups, threadGroups);

            // ADD THIS:
            // voxelizerShader.SetTexture(kernelHandle, "voxelTexture", (RenderTexture)null);
            GL.Flush();

            // ADD THIS LINE:
            Debug.Log($"Dispatched compute shader: {threadGroups} thread groups, kernel: {kernelHandle}");

            // Clean up this mesh's buffer
            triangleBuffer.Release();

            Debug.Log($"Completed mesh {meshIndex}");

        }



        FindObjectOfType<VoxelRenderer>()?.RefreshVoxelTexture(); // LINE A
        displayMeshRenderer.material = voxelMaterial; // Force fresh material instance  LINE B

        // If Line A only, works at startup but not on refresh
        // If Line B only, works at startup but not on refresh.
        // if both Line A and B, works on startup and on refresh.
        // using a coroutine delay does not matter




        Debug.Log($"Separately voxelized {meshFilters.Length} meshes into {bounds.size} volume");


        VoxelsDone = true;
    }


    /*
    System.Collections.IEnumerator DelayedRefresh()
    {
        yield return null; // Wait one frame for GPU to complete
        FindObjectOfType<VoxelRenderer>()?.RefreshVoxelTexture();
        displayMeshRenderer.material = voxelMaterial; // Force fresh material instance

    }
    */



    ComputeBuffer CreateSingleMeshTriangleBuffer(Mesh mesh, Transform meshTransform)
    {
        Vector3[] vertices = mesh.vertices;
        int[] triangles = mesh.triangles;

        Triangle[] triangleData = new Triangle[triangles.Length / 3];

        for (int i = 0; i < triangles.Length; i += 3)
        {
            // Transform vertices to world space using the individual mesh's transform
            Vector3 v0 = meshTransform.TransformPoint(vertices[triangles[i]]);
            Vector3 v1 = meshTransform.TransformPoint(vertices[triangles[i + 1]]);
            Vector3 v2 = meshTransform.TransformPoint(vertices[triangles[i + 2]]);

            triangleData[i / 3] = new Triangle { v0 = v0, v1 = v1, v2 = v2 };
        }

        ComputeBuffer buffer = new ComputeBuffer(triangleData.Length, sizeof(float) * 9);
        buffer.SetData(triangleData);
        return buffer;
    }


    Bounds GetManualBounds()
    {
        // Just use the bounds object's world bounds - super simple!
        Renderer renderer = voxelBounds.GetComponent<Renderer>();
        if (renderer != null)
            return renderer.bounds; // Uses mesh bounds transformed to world space
        else
            return new Bounds(voxelBounds.position, voxelBounds.localScale); // For empty GameObjects
    }

    Mesh CombineMeshes(MeshFilter[] meshFilters)
    {
        CombineInstance[] combine = new CombineInstance[meshFilters.Length];

        for (int i = 0; i < meshFilters.Length; i++)
        {
            combine[i].mesh = meshFilters[i].sharedMesh;
            combine[i].transform = transform.worldToLocalMatrix * meshFilters[i].transform.localToWorldMatrix;
        }

        Mesh combinedMesh = new Mesh();
        combinedMesh.CombineMeshes(combine, true, true);
        return combinedMesh;
    }


    struct Triangle
    {
        public Vector3 v0, v1, v2;
    }

    void OnDrawGizmos()
    {
        if (showVoxelBounds && voxelBounds != null)
        {
            Gizmos.color = Color.green;
            Gizmos.matrix = voxelBounds.localToWorldMatrix;
            Gizmos.DrawWireCube(Vector3.zero, Vector3.one);
        }
    }

    public RenderTexture GetVoxelTexture() => voxelTexture;

    void OnDestroy()
    {
        if (voxelTexture != null) voxelTexture.Release();
    }

    public void ReVoxelize()
    {
        VoxelizeMesh(); // Just call the main method

        // Don't call VoxelizeMesh() - create completely separate method
        // CreateBrandNewVoxelTexture();
    }






    System.Collections.IEnumerator DebugVoxelContents()
    {
        yield return null; // Wait one frame for GPU to finish

        // Sample a few specific voxel positions to see what's in there
        int mid = voxelResolution / 2;

        // We can't easily read back 3D textures, so let's use a different approach
        // Check if the triangle data makes sense
        MeshFilter[] meshFilters = GetComponentsInChildren<MeshFilter>();
        Mesh combinedMesh = CombineMeshes(meshFilters);

        Debug.Log($"Combined mesh vertex count: {combinedMesh.vertexCount}");
        Debug.Log($"Combined mesh bounds: {combinedMesh.bounds}");
        Debug.Log($"First 3 vertices: {combinedMesh.vertices[0]}, {combinedMesh.vertices[1]}, {combinedMesh.vertices[2]}");

        // Compare this to what we expect - your cross should have vertices spread out,
        // not all clustered together
    }



}