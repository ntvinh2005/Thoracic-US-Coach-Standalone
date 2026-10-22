using UnityEngine;
using System.Collections;



public class LabelController : MonoBehaviour {

    private Vector3 initialScale;
    private Renderer[] Renderers;

    public float HideDistance;
    public float LabelSize;


	// Use this for initialization
	void Start () {

        Camera Camera = UserInterfaceManager.ME.Camera;

        // Get the initial scale of the object (entire label system)
        initialScale = transform.localScale;

        // Initialize a few variables required later
        Renderers = gameObject.GetComponentsInChildren<Renderer>();

        // Set the label text to the object's name
        TextMesh textobj = gameObject.GetComponentInChildren<TextMesh>();
        textobj.text = gameObject.name;

        // Set the background plane width to slightly greater than text
        Renderer textRend = textobj.gameObject.GetComponent<Renderer>();
        // Bounds textbounds = textRend.bounds;
        // Debug.Log("Text Bounds: " + (textbounds) );
        Renderer planeRend = textobj.gameObject.transform.GetChild(0).GetComponent<Renderer>();
        while (planeRend.bounds.extents.x < textRend.bounds.extents.x)
        {
            planeRend.gameObject.transform.localScale = planeRend.transform.localScale + new Vector3(0.1f, 0, 0);
        }
        planeRend.gameObject.transform.localScale = planeRend.transform.localScale + new Vector3(0.3f, 0, 0);

        // Update the new camera distance
        float dist = Vector3.Distance(Camera.transform.position, transform.position);
        transform.localScale = initialScale * (dist / HideDistance) * LabelSize;

        // Show/Hide based on Distance
        if (dist > HideDistance)
        {
            foreach (Renderer r in Renderers)
                r.enabled = false;
        }         
        else
        {
            foreach (Renderer r in Renderers)
                r.enabled = true;
        }
    }
	
	// Update is called once per frame
	void Update () {

        Camera Camera = UserInterfaceManager.ME.Camera;

        // Rotate the label with the camera to keep it facing the camera
        gameObject.transform.rotation = Camera.transform.rotation;

        // Update the new camera distance
        float dist = Vector3.Distance(Camera.transform.position, transform.position);
        transform.localScale = initialScale * (dist / HideDistance) * LabelSize;

        // Show/Hide based on distance
        if (dist > HideDistance)
        {
            foreach (Renderer r in Renderers)
                r.enabled = false;
        }
        else
        {
            foreach (Renderer r in Renderers)
                r.enabled = true;
        }


    }

    
}
