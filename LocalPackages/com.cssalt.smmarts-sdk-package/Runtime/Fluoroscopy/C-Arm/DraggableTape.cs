//using System.Diagnostics;
using UnityEngine;
using System.Linq;

namespace SMMARTS
{
    /// <summary>
    /// Draggable tape piece for fluoroscopy machine.
    /// Click and drag to move tape to different positions on the C-arm.
    /// </summary>
    public class DraggableTape : MonoBehaviour
    {
        [Header("Tape Settings")]
        [Tooltip("Layer mask for surfaces the tape can stick to")]
        public LayerMask tapeableSurfaces = -1;

        [Header("Debug Info")]
        [SerializeField] private bool isDragging = false;


        public Collider tapeCollider;
        [Tooltip("Camera used for raycasting (usually the Tech's Viewpoint Camera)")]
        public Camera FluoroTechCamera;


        [Tooltip("If assigned, tape will always face towards this transform instead of using surface normal")]
        public Transform AlwaysFaceTowards;

        // this is a list of the acceptable gameobjects that the tape can stick to.
        public GameObject[] AcceptableObjects;

        public Transform StickOnObject;


        void Start()
        {
            // If not assigned in inspector, try to find it
            if (FluoroTechCamera == null)
            {
                GameObject techCameraGO = GameObject.Find("Tech's Viewpoint Camera");
                if (techCameraGO != null)
                {
                    FluoroTechCamera = techCameraGO.GetComponent<Camera>();
                }
                else
                {
                    Debug.Log("DraggableTape: Could not find Tech's Viewpoint Camera!");
                }
            }

            tapeCollider = GetComponent<Collider>();
        }

        void Update()
        {
            HandleMouseInput();

            if (isDragging)
            {
                DragTape();
            }
        }

        void HandleMouseInput()
        {
            if (Input.GetMouseButtonDown(0))
            {
                // Check if mouse is over THIS tape object
                Ray ray = FluoroTechCamera.ScreenPointToRay(Input.mousePosition);
                RaycastHit hit;

                if (Physics.Raycast(ray, out hit) && hit.collider == tapeCollider)
                {
                    isDragging = true;
                }
            }

            if (Input.GetMouseButtonUp(0))
            {
                isDragging = false;
            }
        }

        void DragTape()
        {
            // Raycast ignoring the tape itself
            Ray ray = FluoroTechCamera.ScreenPointToRay(Input.mousePosition);
            RaycastHit hit;

            // Temporarily disable tape collider so it doesn't block the raycast
            tapeCollider.enabled = false;

            if (Physics.Raycast(ray, out hit, Mathf.Infinity, tapeableSurfaces))
            {
                if (AcceptableObjects.Contains(hit.transform.gameObject))
                {
                    // Move tape to hit location
                    transform.position = hit.point;



                    // Choose orientation method
                    if (AlwaysFaceTowards != null)
                    {
                        // Face towards the specified transform
                        Vector3 faceDirection = (AlwaysFaceTowards.position - transform.position).normalized;
                        transform.up = faceDirection;
                    }
                    else
                    {
                        // Use surface normal (default behavior)
                        transform.up = hit.normal;
                    }

                    // parent to the object we are stuck to in case it moves later
                    // but don't stick tapes to one another or anything else we don't approve of.

                    // GameObject is in the AcceptableObjects array
                    StickOnObject = hit.transform;
                    transform.parent = StickOnObject;
                }


            }

            // Re-enable tape collider
            tapeCollider.enabled = true;
        }
    }
}