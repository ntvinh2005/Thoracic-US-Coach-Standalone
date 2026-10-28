using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;
using System.Collections.Generic;
using System.Collections;
namespace SMMARTS
{
    public class UltrasoundImageToWorldSpace : MonoBehaviour, IPointerClickHandler
    {
        // Here is how this code works:
        // First major task: convert a touch point on US image (its a touch screen) to 3D world space
        // click on the US image, and you get a point in 3D world space on that location in the insonating plane.
        // if you like, you can drop a little 3mm diameter test sphere in that space in the 3D world and you can see it in the ultrasound image.
        // Second major task: identify the object you clicked on in the US image.
        // We're going to use raycasts. keep in mind, raycast hits work from the outside in, not from the inside out (raycasts wont work on backface normals)
        // raycast from the probe to the touch point and build a sorted list of all the objects that raycast encountered. 
        // then raycast in the other direction, from the touch point back up to the probe, and build another sorted list of all the objects that that backwards raycast encountered.
        // now you have two sorted lists: a forward hit list and a backwards hit list.
        // now remove objects from the sorted forward hit lists that appear in the backwards hit list, searching from the last to the first entry in the forward hit list.
        // the last object in the sorted list of forward hits is the object you clicked on!
        // LIMITATIONS:
        // does not yet take into account a frozen ultrasound image!
        // requires Physics.queriesHitBackfaces set to false (its default value). If set to true, you might break this system especially with mesh colliders. 
        // This script goes on the Ultrasound Screen gameobject under the Ultrasound canvas.

        [Header("-----Enable Message Pop-up -----")]
        // Send a toast from here, each time you click and something is recognized?
        public bool MessageAppears = true;

        [Header("-----Highlight Settings -----")]
        [Tooltip("Material used for highlighting touched objects (auto-found by name if not assigned)")]
        public Material highlightMaterial;

        [Tooltip("Duration to highlight touched objects (seconds)")]
        public int highlightDuration = 2;

        [Header("-----Last object touched in the US display-----")]
        // Reference to the gamobject that your touch is inside - and can be null.
        public GameObject ObjectInUltrasound;

        [Header("-----Object References-----")]
        // It assumes a rect transform is present (which Ultrasound Screen gameobject under the Ultrasound canvas does.)
        public RectTransform rectTransform;
        // Reference to the GameObject that appears in 3D space. By default, the mesh renderer is off so its not visible.
        // You can use this GO for verification by adding a collider with an UltrasoundVisibility component to it.
        // Its public so someone else can reference it.
        public GameObject UltrasoundTouchscreenToWorldSpaceMarker;
        // Reference to the space in the world corresponding to your touch on the US image.
        // Its public so someone else can reference it.
        public Vector3 WorldSpaceTouchLocation;

        // this is for the hand pointer 
        [Header("-----Display the pointer?----")]
        public bool DisplayWorldSpaceMarker;
        private Coroutine markerVisibilityCoroutine;

        void Start()
        {
            // Auto-find highlight material if not assigned
            if (highlightMaterial == null)
            {
                highlightMaterial = Resources.Load<Material>("Touch Hilight Material");
                if (highlightMaterial == null)
                {
                    Debug.LogWarning($"UltrasoundImageToWorldSpace on {gameObject.name}: Could not find 'Touch Hilight Material' in Resources folder");
                }
            }

            // Log warning if Physics.queriesHitBackfaces set to true. Its false by default.
            // If Physics.queriesHitBackfaces is set to true, objects like mesh colliders may not respond to clicks.
            if (Physics.queriesHitBackfaces == true) Debug.LogWarning("Ultrasound touch identification system requires Physics.queriesHitBackfaces set to false (the default).");

        }

        // My gameobject is an image on a canvas. Specifically, the US image.
        // this code runs when you touch or click on my image (same code as a button press).
        // You need an Event System on the canvas for this to work (you should already have it setup in SMMARTS SDK Ultrasound)
        public void OnPointerClick(PointerEventData eventData)
        {
            // First major task: convert the touch point on my US image into to point in 3D world space
            WorldSpaceTouchLocation = ConvertTouch_to_WorldSpace(eventData);
            // Second major task: report what object is inside your click on the US
            ObjectInUltrasound = IdentifyObjectAtPoint(WorldSpaceTouchLocation);

            // Post the message here, if you like.
            // Each UltrasoundVisibility has a message string specifically for this purpose.
            if (ObjectInUltrasound != null & MessageAppears)
            {
                // Try to get IdentifiableObject first (preferred), fall back to UltrasoundVisibility
                IdentifiableObject identifiable = ObjectInUltrasound.GetComponent<IdentifiableObject>();
                UltrasoundVisibility ultrasoundVis = ObjectInUltrasound.GetComponent<UltrasoundVisibility>();

                if (identifiable != null && identifiable.EnableTouchDisplayMessage)
                {
                    // BIDIRECTIONAL MAGIC: Touch ultrasound → highlight both 3D and ultrasound!
                    string messageToDisplay = identifiable.TouchDisplayMessage;

                    // Show toast message
                    Toast.Dismiss();
                    Toast.Show(this, messageToDisplay, highlightDuration, Toast.Type.MESSAGE, 15, Toast.Gravity.TOP);

                    // Start unified highlight (3D glow + ultrasound highlight)
                    identifiable.StartHighlight(highlightMaterial, highlightDuration);

                    // Show the 3D world space marker at the touch location
                    if (DisplayWorldSpaceMarker) ShowWorldSpaceMarker();

                    Debug.Log($"UltrasoundImageToWorldSpace: Identified and highlighted '{messageToDisplay}' on {ObjectInUltrasound.name}");
                }
                else if (ultrasoundVis != null && ultrasoundVis.EnableTouchDisplayMessage)
                {
                    // Fallback for legacy UltrasoundVisibility components (no 3D highlight)
                    string messageToDisplay = ultrasoundVis.TouchDisplayMessage;
                    Toast.Dismiss();
                    Toast.Show(this, messageToDisplay, highlightDuration, Toast.Type.MESSAGE, 15, Toast.Gravity.TOP);

                    Debug.Log($"UltrasoundImageToWorldSpace: Identified '{messageToDisplay}' on {ObjectInUltrasound.name} (legacy UltrasoundVisibility, no 3D highlight)");
                }
            }
            // Checks:
            // if (ObjectInUltrasound == null)  Debug.Log("I think you touched inside empty space.");
            // else Debug.Log("I think you touched inside " + ObjectInUltrasound);
        }

        // First major task: touch point on US image to 3D world space
        // click on the US image, and you get a point in 3D world space on that location in the insonating plane.
        // if you like, you can drop a little 3mm diameter test sphere in that space in the 3D world and you can see it in the ultrasound image.
        private Vector3 ConvertTouch_to_WorldSpace(PointerEventData eventData)
        {
            // First major task: touch point on US image to 3D world space
            // Convert click position on my rectTransform to a local position within my RawImage
            Vector2 localPoint;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                rectTransform,
                eventData.position,
                eventData.pressEventCamera,
                out localPoint);
            //  Debug.Log("localPoint = " + localPoint);
            // at this point the coordinate system has origin at the center of the texture.
            // clicking on the lower right corner of the texture will return two different numbers for small screen US or large screen US,
            // and are independant of aspect ratio or screen resolution - which is great!
            // Convert from UI local coordinates to normalized coordinates (0-1 range)
            // Rect rect = ultrasoundDisplay.rectTransform.rect;
            Rect rect = rectTransform.rect;
            Vector2 normalizedPoint = new Vector2(
                (localPoint.x - rect.x) / rect.width,
                (localPoint.y - rect.y) / rect.height);
            if (UltrasoundDisplayController.ME.DotOnRight == false) normalizedPoint.x = 1 - normalizedPoint.x;
            // Convert from normalized coordinates into ultrasound Texture Space in pixels
            Vector2 pixelPoint = new Vector2(
                normalizedPoint.x * UltrasoundManager.ME.Resolution,
                normalizedPoint.y * UltrasoundManager.ME.Resolution);
            // Debug.Log("pixelPoint = " + pixelPoint);
            // now the click point is just like the Texure Space and Scanning Plane Space Quick Reference at
            // https://github.com/CSSALT/SMMARTS-SDK-Internal/wiki/UltrasoundManager.cs
            // Convert from Texture Space in pixels to Scanning Plane Space
            Vector3 worldSpacePoint = UltrasoundManager.ME.ScreenPointToWorldCoordinate(pixelPoint);
            // now, drop a little 3mm diameter test sphere in that space in the 3D world and you can see it in the ultrasound image.
            // in the SDK, that little test sphere is on this transform with the mesh renderer turned off.
            UltrasoundTouchscreenToWorldSpaceMarker.transform.position = worldSpacePoint;
            return worldSpacePoint;
        }

        // To report what object you clicked on in the US image,
        // We're going to use raycasts. Keep in mind raycast hits work from the outside in, not from the inside out (raycasts wont work on backface normals)
        // so we will have to do two raycasts, one from outside in and another from inside out.
        // So, we will raycast from the probe to the touch point and build a sorted list of all the objects that raycast encountered.
        // then raycast in the other direction, from the touch point back up to the probe, and build another sorted list of all the objects that that backwards raycast encountered.
        // We will then have two sorted lists: a forward hit list and a backwards hit list.
        // Next we remove objects from the sorted forward hit lists that appear in the backwards hit list, searching from the last to the first entry in the forward hit list.
        // the last object in the sorted list of forward hits is the object you clicked on!
        // Note - if you set Physics.queriesHitBackfaces to true, you might break this system especially with mesh colliders. 
        /// Enable these arrays for debugging:
        /// public  GameObject[] forwardGameobjects;
        /// public  GameObject[] backwardGameobjects;
        private GameObject IdentifyObjectAtPoint(Vector3 touchPointInWorldSpace)
        {
            // We will start raycasting from the rear of the probe, well outside the skin.
            Vector3 startUpTheProbe = UltrasoundManager.ME.CenterOfProbeRear.transform.position;
            Vector3 globalEndPointU = touchPointInWorldSpace;
            // verification of the rays (only forwards)
            // Debug.DrawRay(startUpTheProbe, globalEndPointU - startUpTheProbe, Color.red, 0.1f);
            // were going to consider air gaps an edge case and not worry about them. Air gaps are relavent to individual scan lines on the ultrasound,
            // and this code renders outside of the individual scan lines.
            // if the probe if far from the skin, you can't id what you can't see, so return null here as well.
            if (UltrasoundManager.ME.ProbeIsFarFromSkin)
            {
                Debug.Log("Bailing out early because far from skin.");
                return null;
            }
            // raycast for all imageable layers.
            LayerMask lm = UltrasoundManager.ME.ImageableLayers;
            // Raycast from far up inside the probe to the touch point, and don't overshoot the touch point.
            float maxDistance = Vector3.Distance(startUpTheProbe, globalEndPointU);
            RaycastHit[] forwardRaycastHits = Physics.RaycastAll(startUpTheProbe, globalEndPointU - startUpTheProbe, maxDistance, lm);
            // Draw a verification ray in the scene
            // Debug.DrawRay(startUpTheProbe, globalEndPointU - startUpTheProbe, Color.red, 4f, false);
            // if there are no hits at all, just return null now.
            if (forwardRaycastHits.Length == 0)
            {
                Debug.Log("Bailing out early because there are no forward hits.");
                return null;
            }
            // make an empty "DetectedInterface" list of things the raycasts hit.
            // we'll use this struct because it allows us sort the hits AND to look at the ultrasound material on the gameobjects hit. Not all hits should be added.
            List<UltrasoundManager.DetectedInterface> forwardObjects = new List<UltrasoundManager.DetectedInterface>();
            // add a DetectedInterface for each raycast hit, but only if its ultrasound material component (if present) is flagged for rendering in US.
            for (int hitIndex = 0; hitIndex < forwardRaycastHits.Length; hitIndex++)
            {
                RaycastHit hit = forwardRaycastHits[hitIndex];
                UltrasoundVisibility uM = hit.transform.GetComponent<UltrasoundVisibility>();
                // were only going to consider things you can see on the ultrasound image: UltrasoundMaterials,
                // and in addtion they need to be flagged as showing in ultrasound.
                // this way, other raycast hits from parts of the probe, etc. won't muck up our algorithm.
                if (uM != null && uM.ShowOnUltrasound)
                {
                    forwardObjects.Add(new UltrasoundManager.DetectedInterface(hit.distance, true, uM, hit.transform.GetInstanceID()));
                    // Debug.Log("adding forward hit " + hit.collider.gameObject.name + " " + hit.transform.GetInstanceID());
                }
            }
            // Now we'll sort all the the forwardObjects detected from the forward raycasts in order of distance.
            for (int y = 0; y < forwardObjects.Count - 1; y++)
            {
                UltrasoundManager.DetectedInterface sEY = forwardObjects[y];
                for (int z = y + 1; z < forwardObjects.Count; z++)
                {
                    UltrasoundManager.DetectedInterface sEZ = forwardObjects[z];
                    if (sEZ.depthFromProbe < sEY.depthFromProbe)
                    {
                        forwardObjects[y] = sEZ;
                        forwardObjects[z] = sEY;
                        sEY = sEZ;
                    }
                }
            }
            // if you like, make a list of gameobjects so its easier to see what is going on. comment back in the forwardGameobjects[] public declaration.
            // (DetectedInterfaces are not serializeable so its easier to make a list of GameObjects and serialize that instead)
            // forwardGameobjects = new GameObject[forwardObjects.Count];
            // for (int y = 0; y < forwardObjects.Count; y++) forwardGameobjects[y] = forwardObjects[y].uMaterial.gameObject;
            // now that we have the ordered list of objects detected from a forward raycast, we'll build up another list of objects from the backward raycast.
            // Raycast from far up inside the probe to the touch point, and don't overshoot the touch point. We already specified max distance.
            RaycastHit[] backwardRaycastHits = Physics.RaycastAll(globalEndPointU, startUpTheProbe - globalEndPointU, maxDistance, lm);
            // build an empty list of DetectedInterfaces from the backward raycast hits, just like we did for forward hits as described above.
            List<UltrasoundManager.DetectedInterface> backwardObjects = new List<UltrasoundManager.DetectedInterface>();
            for (int hitIndex = 0; hitIndex < backwardRaycastHits.Length; hitIndex++)
            {
                RaycastHit hit = backwardRaycastHits[hitIndex];
                UltrasoundVisibility uM = hit.transform.GetComponent<UltrasoundVisibility>();
                if (uM != null && uM.ShowOnUltrasound)
                {
                    backwardObjects.Add(new UltrasoundManager.DetectedInterface(hit.distance, true, uM, hit.transform.GetInstanceID()));
                    // Debug.Log("adding backward hit " + hit.collider.gameObject.name + " " + hit.transform.GetInstanceID());
                }
            }
            // Now we'll sort all the the ScannedElements detected from the backward raycasts in order of distance, same as we did for forward hits described above.
            for (int y = 0; y < backwardObjects.Count - 1; y++)
            {
                UltrasoundManager.DetectedInterface sEY = backwardObjects[y];
                for (int z = y + 1; z < backwardObjects.Count; z++)
                {
                    UltrasoundManager.DetectedInterface sEZ = backwardObjects[z];
                    if (sEZ.depthFromProbe < sEY.depthFromProbe)
                    {
                        backwardObjects[y] = sEZ;
                        backwardObjects[z] = sEY;
                        sEY = sEZ;
                    }
                }
            }
            // if you like, make a list of gameobjects so its easier to see what is going on. comment back in the backwardGameobjects[] public declaration.
            // (DetectedInterfaces are not serializeable so its easier to make a list of GameObjects and serialize that instead)
            // backwardGameobjects = new GameObject[backwardObjects.Count];
            // for (int y = 0; y < backwardObjects.Count; y++)  backwardGameobjects[y] = backwardObjects[y].uMaterial.gameObject;
            // At this point, we have a sorted list of objects detected from forward hits AND a sorted list of objects detected from backward hits.
            // to get what you clicked on, all you need to do is remove the backward hits from the forward hits and return the last forward hit in the list.
            // so: for each object in backward hits, search for the last instance of that object in the forward object list and delete it from the list.
            foreach (UltrasoundManager.DetectedInterface backObject in backwardObjects)
            {
                for (int i = forwardObjects.Count - 1; i >= 0; i--)
                {
                    if (forwardObjects[i].objectInstanceID == backObject.objectInstanceID)
                    {
                        forwardObjects.RemoveAt(i);
                    }
                }
            }
            // At this point, the LAST object in forwardObjects should be the guy you click on.
            // we return the reference to the gameobject because its more useful. Also, reverse lookup a gameobject from an instance ID presents problems.
            GameObject clickedObject = null; // default case - you clicked on empty space below an object. clickedObject will stay null.
            if (forwardObjects.Count > 0) clickedObject = forwardObjects[forwardObjects.Count - 1].uMaterial.gameObject;

            Debug.Log("forwardObjects.Count = " + forwardObjects.Count);
            Debug.Log("clickedObject = " + clickedObject);

            return clickedObject;
        }

        /// <summary>
        /// Show the world space marker at the touch location for the highlight duration
        /// Restarts timer if already visible
        /// </summary>
        private void ShowWorldSpaceMarker()
        {
            if (UltrasoundTouchscreenToWorldSpaceMarker != null)
            {
                // Stop existing timer if running (restart capability)
                if (markerVisibilityCoroutine != null)
                {
                    StopCoroutine(markerVisibilityCoroutine);
                }

                // Make marker visible
                UltrasoundTouchscreenToWorldSpaceMarker.SetActive(true);

                // Make marker face the camera
                UltrasoundTouchscreenToWorldSpaceMarker.transform.LookAt(Camera.main.transform);

                // Start visibility timer
                markerVisibilityCoroutine = StartCoroutine(MarkerVisibilityTimeout());

                Debug.Log($"UltrasoundImageToWorldSpace: Showing world space marker at {WorldSpaceTouchLocation}");
            }
        }

        /// <summary>
        /// Hide the world space marker after highlight duration
        /// </summary>
        private IEnumerator MarkerVisibilityTimeout()
        {
            yield return new WaitForSeconds(highlightDuration);

            if (UltrasoundTouchscreenToWorldSpaceMarker != null)
            {
                UltrasoundTouchscreenToWorldSpaceMarker.SetActive(false);
                Debug.Log("UltrasoundImageToWorldSpace: Hiding world space marker");
            }

            markerVisibilityCoroutine = null;
        }
    }
}