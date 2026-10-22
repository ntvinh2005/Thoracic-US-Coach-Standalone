using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class IntersectionVector : MonoBehaviour
{

    // This calculates the position difference vector between probe center at the bottom and the skin surface of contact

    // Make public to see them in the inspector.

    
    Vector3 contactPoint;
    Vector3 positionDifference;
    float InsideProbeDistance;
    
    RaycastHit hit;

    public bool FlipTheNormal;

    // only one specialized column is needed for this function. So, let's use start() and not to destroy it
    public void Start()
    {
        Vector3 forward = transform.TransformDirection(Vector3.back);

        // InsideProbeDepth will be the same every time. just measure it now.
        if (Physics.Raycast(transform.position, forward, out hit, 300, IntersectionVolumeManager.ME.ProbeInnerSurfaceLayer))
        {
            InsideProbeDistance = hit.distance;
            InsideProbeDistance = Mathf.Infinity; // reduces the edge case of the center of the probe not hitting the skin when the side of the probe does, and there is a volume with an incorrect zero vector here.
        }
        else
        {
            Debug.Log("The center column is placed at a wrong location or orientation, please check");
        }

    }


    // Update is called once per frame
    // measure in (x,y,z)
    void Update()
    {

        // do some raycasts. first, re-define the forward direction of this column element.
        Vector3 forward = transform.TransformDirection(Vector3.back);

        // (this is helpful during programming in the scene view)
        if (IntersectionVolumeManager.ME.DrawDebugRays) Debug.DrawRay(transform.position, (forward * 300), Color.green);

        // in the simulation, 1 unity unit = 1 mm.
        // @Dave: I used InsideProbeDistance as the raycast distance, instead of fixed 300
        if (Physics.Raycast(transform.position, forward, out hit, InsideProbeDistance, IntersectionVolumeManager.ME.SkinLayer)) 
        {
            // imagine were inside the probe, looking out.
            // if we see the skin before the inside surface of the probe, then there is some volume of skin that is intersecting the probe.
            contactPoint = hit.point; 
            positionDifference = transform.position - contactPoint; // a vector with origin at contact point, pointing at probe center
            positionDifference = positionDifference.normalized;
            if (FlipTheNormal) positionDifference = -positionDifference;
        }
        else
        {
            positionDifference = new Vector3(0, 0, 0);
        }
        IntersectionVolumeManager.ME.PositionOffset(positionDifference);
    }
}
