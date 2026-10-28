using UnityEngine;
using System.Collections;
using SMMARTS;
using UnityEngine.EventSystems;

/// <summary>
/// TouchIdentification: Interactive anatomy identification system
/// 
/// PURPOSE:
/// Allows learners to touch/click on 3D objects to get identification information
/// Each camera can have its own TouchIdentification component with custom layermasks
/// 
/// USAGE:
/// - Attach to camera GameObjects
/// - Configure layermask for what this camera can identify
/// - Objects need AnatomicalIdentifier component to be identifiable
/// 
/// INPUT SUPPORT:
/// - Mouse clicks (for development and remote teaching)
/// - Touch input (for touchscreen devices)
/// 
/// INTEGRATION:
/// - Uses existing SMMARTS Toast system
/// - Rate limiting prevents spam
/// - Cross-platform input handling
/// </summary>
public class TouchIdentification : MonoBehaviour
{
    [Header("Touch Identification Settings")]
    [Tooltip("Camera used for raycasting (auto-assigned if null)")]
    public Camera targetCamera;

    [Tooltip("Layer mask for objects that can be identified")]
    public LayerMask identifiableLayer = -1; // Default to everything

    [Tooltip("Maximum distance for raycast identification")]
    public float maxRaycastDistance = 1000f;

    [Header("Rate Limiting")]
    [Tooltip("Minimum time between identifications (seconds)")]
    public float rateLimitInterval = 1f;

    // Rate limiting control
    private bool identificationAvailable = true;
    [Header("Highlight Settings")]
    [Tooltip("Material used for highlighting touched objects (auto-found by name if not assigned)")]
    public Material highlightMaterial;

    [Tooltip("Duration to highlight touched objects (seconds)")]
    public int highlightDuration = 2;

    // If this is a render texture camera, we process clicks differently,
    // through a RenderTextureToTouchIdentification script component on the image that uses the render texture.
    // we'll set this flag automatically if the camera is set to render to a texture.
    private bool IsRenderTexture;


    void Start()
    {
        // Auto-assign camera if not set
        if (targetCamera == null)
        {
            targetCamera = GetComponent<Camera>();
            if (targetCamera == null)
            {
                Debug.LogError($"TouchIdentification on {gameObject.name} needs a Camera component or FluoroTechViewpointCamera assignment!");
                enabled = false;
                return;
            }
        }

        // Auto-find highlight material if not assigned
        if (highlightMaterial == null)
        {
            highlightMaterial = Resources.Load<Material>("Touch Hilight Material");
            if (highlightMaterial == null)
            {
                Debug.LogWarning($"TouchIdentification on {gameObject.name}: Could not find 'Touch Hilight Material' in Resources folder");
            }
        }

        // Auto-detect render texture mode
        IsRenderTexture = (targetCamera.targetTexture != null);

    }



    /// <summary>
    /// Perform raycast identification at the input position (direct camera input)
    /// </summary>
    private void ProcessIdentification(Vector3 screenPosition)
    {
        ProcessIdentificationAtScreenPoint(screenPosition);
    }


    /// <summary>
    /// Process click through render texture UI element
    /// Converts UI canvas coordinates to camera screen coordinates, then performs standard raycast identification
    /// Called by RenderTextureLinkToTouchIdentification components on UI elements displaying this camera's render texture
    /// </summary>
    public void ProcessRenderTextureClick(PointerEventData eventData, RectTransform uiRectTransform)
    {
        if (!identificationAvailable)
        {
            Debug.Log("TouchIdentification: Rate limited, ignoring render texture click");
            return;
        }

        // Convert UI click position to normalized coordinates within the UI element (0-1 range)
        Vector2 localPoint;
        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
            uiRectTransform,
            eventData.position,
            eventData.pressEventCamera,
            out localPoint))
        {
            Debug.LogWarning("TouchIdentification: Failed to convert screen point to local UI coordinates");
            return;
        }

        // Convert local UI coordinates to normalized texture coordinates (0-1)
        Rect rect = uiRectTransform.rect;
        Vector2 normalizedPoint = new Vector2(
            (localPoint.x - rect.x) / rect.width,
            (localPoint.y - rect.y) / rect.height
        );

        // Clamp to ensure we're within bounds
        normalizedPoint.x = Mathf.Clamp01(normalizedPoint.x);
        normalizedPoint.y = Mathf.Clamp01(normalizedPoint.y);

        // Convert normalized coordinates to camera screen coordinates
        Vector3 cameraScreenPosition = new Vector3(
            normalizedPoint.x * targetCamera.pixelWidth,
            normalizedPoint.y * targetCamera.pixelHeight,
            0
        );

        Debug.Log($"TouchIdentification: Render texture click - UI local: {localPoint}, normalized: {normalizedPoint}, camera screen: {cameraScreenPosition}");

        // Use standard identification logic with converted coordinates
        ProcessIdentificationAtScreenPoint(cameraScreenPosition);
    }

    /// <summary>
    /// Core identification logic extracted for reuse by both direct input and render texture input
    /// </summary>
    private void ProcessIdentificationAtScreenPoint(Vector3 screenPosition)
    {
        // if we are a world camera, check if pointer is over a UI element and bail out if we are.
        // in this case, the render texture's TouchIdentification will handle the click/touch.
        // this prevents clicks/touches from on going through a UI element and into the 3D world behind it - for example,
        // if you click on a vein in the ultrasound image but the ultrasound image happens to be over bone in the 3D world, 
        // you dont want bone selected. you clicked on the US image, not what you can't see behind the US image.
        if (IsRenderTexture == false & EventSystem.current.IsPointerOverGameObject()) 
        {
            return; // Don't raycast if over UI if we are a world camera.
        }

        // Convert screen position to world ray
        Ray ray = targetCamera.ScreenPointToRay(screenPosition);

        // Get ALL hits along the ray with layer filtering
        RaycastHit[] allHits = Physics.RaycastAll(ray, maxRaycastDistance, identifiableLayer);

        // Sort by distance (closest to camera first)
        System.Array.Sort(allHits, (a, b) => a.distance.CompareTo(b.distance));

        // Find the first VISIBLE object (the one the user can actually see)
        foreach (RaycastHit hit in allHits)
        {
            // Check if this object has a visible renderer
            Renderer renderer = hit.collider.GetComponent<Renderer>();
            if (renderer != null && renderer.enabled)
            {
                // This is the visible object they meant to click!
                IdentifiableObject identifiable = hit.collider.GetComponent<IdentifiableObject>();

                if (identifiable != null && identifiable.EnableTouchDisplayMessage)
                {
                    // Display identification toast
                    ShowIdentificationToast(identifiable.TouchDisplayMessage);

                    // Start unified highlight (both 3D and ultrasound)
                    identifiable.StartHighlight(highlightMaterial, highlightDuration);

                    // Start rate limiting
                    StartCoroutine(RateLimitIdentification());

                    Debug.Log($"TouchIdentification: Identified visible object '{identifiable.TouchDisplayMessage}' on {hit.collider.name} via render texture");

                    // Exit immediately after processing the first visible hit
                    return;
                }
                else if (identifiable != null && !identifiable.EnableTouchDisplayMessage)
                {
                    // this means, basically, you clicked on something but we're not telling you what it is.
                    // this would be the case if we disabled an object for some quiz purpose or something.
                    // just exit immediately and be silent.
                    Debug.Log($"TouchIdentification: Hit visible object {hit.collider.name} but Identifiable is disabled");
                    return;
                }
                else 
                {
                    Debug.Log($"TouchIdentification: Hit visible object {hit.collider.name} but no IdentifiableObject or disabled");
                    // Continue to next hit in case there's another visible object behind this one
                }
            }
            else
            {
                Debug.Log($"TouchIdentification: Skipping invisible object {hit.collider.name}");
                // Continue to next hit
            }
        }

    }


    void Update()
    {
        if (!IsRenderTexture)
        {
            // Only handle direct input if NOT rendering to texture
            HandleInput();
        }
    }

    /// <summary>
    /// Handle both mouse and touch input for cross-platform support
    /// </summary>
    private void HandleInput()
    {
        Vector3 inputPosition = Vector3.zero;
        bool inputDetected = false;

        // Mouse input (development and remote teaching)
        if (Input.GetMouseButtonDown(0))
        {
            inputPosition = Input.mousePosition;
            inputDetected = true;
        }
        // Touch input (touchscreen devices)
        else if (Input.touchCount > 0)
        {
            Touch touch = Input.GetTouch(0);
            if (touch.phase == TouchPhase.Began)
            {
                inputPosition = touch.position;
                inputDetected = true;
            }
        }

        // Process input if detected and not rate limited
        if (inputDetected && identificationAvailable)
        {
            ProcessIdentification(inputPosition);
        }
    }


    /// <summary>
    /// Display identification toast using SMMARTS Toast system
    /// Same parameters as UltrasoundImageToWorldSpace
    /// </summary>
    private void ShowIdentificationToast(string displayName)
    {
        Toast.Show(this, displayName, highlightDuration, Toast.Type.MESSAGE, 15, Toast.Gravity.TOP);
    }

    /// <summary>
    /// Rate limiting coroutine to prevent spam
    /// Same pattern as UltrasoundImageToWorldSpace
    /// </summary>
    private IEnumerator RateLimitIdentification()
    {
        identificationAvailable = false;
        yield return new WaitForSeconds(rateLimitInterval);
        identificationAvailable = true;
    }

    /// <summary>
    /// Editor helper to test identification system
    /// </summary>
    [ContextMenu("Test Identification")]
    public void TestIdentification()
    {
        Debug.Log($"TouchIdentification test on {gameObject.name}:");
        Debug.Log($"- Camera: {(targetCamera ? targetCamera.name : "NULL")}");
        Debug.Log($"- Layer mask: {identifiableLayer}");
        Debug.Log($"- Max distance: {maxRaycastDistance}");
        Debug.Log($"- Rate limit: {rateLimitInterval}s");
    }
}