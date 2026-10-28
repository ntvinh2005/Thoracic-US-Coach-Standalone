using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class IntersectionVolumeElement : MonoBehaviour
{

    // we are placing these elements 25mm up the probe's surface arranged in a plane at the end of the probe.
    // this requires a normals-flipped mesh collider of the end of the probe so we can raycast to it from the inside.

    // Make public to see them in the inspector.
    float volume;
    float SkinDistance;
    float InsideProbeDistance;

    RaycastHit hit;

    // this took the place of Start(). We explicity call this once from IntersectionVolumeManager(), which instantiated this gameobject.
    // we made an array of elements bigger than the probe, so this enables the elements outside the range of the probe to be removed.
    // if your not cleared it initializes InsideProbeDistance.
    public void JustKillYourselfIfYourNotNeeded()
    {
        Vector3 forward = transform.TransformDirection(Vector3.back);

        // InsideProbeDepth will be the same every time. just measure it now.
        if (Physics.Raycast(transform.position, forward, out hit, 300, IntersectionVolumeManager.ME.ProbeInnerSurfaceLayer))
        {
            InsideProbeDistance = hit.distance;
        }
        else
        {
            // Debug.Log("We have a column here that is outside the boundry of the US probe. OK to cull it since its off the edge.");
            Destroy(gameObject);
        }

    }


    // Update is called once per frame
    // measure the skin intersetion volume and pass that volume up the chain of command.
    // Note, since these Volume Elements are arranged in a 1x1mm grid, the area of this element is 1mm^2.
    void Update()
    {

        // do some raycasts. first, re-define the forward direction of this column element.
        Vector3 forward = transform.TransformDirection(Vector3.back);

        // (this is helpful during programming in the scene view)
        if (IntersectionVolumeManager.ME.DrawDebugRays) Debug.DrawRay(transform.position, (forward * 300), Color.green);

        // find some volumes. 
        volume = 0;

        // in the simulation, 1 unity unit = 1 mm.
        if (Physics.Raycast(transform.position, forward, out hit, 300, IntersectionVolumeManager.ME.SkinLayer))
        {
            // imagine were inside the probe, looking out.
            // if we see the skin before the inside surface of the probe, then there is some volume of skin that is intersecting the probe.
            SkinDistance = hit.distance; // mm
            volume = InsideProbeDistance - SkinDistance; // times an area of 1 mm^2 for cubic mm, or ml. 
        }

        // now report up to the manager.
        // Negative volumes mean no intersection of the probe surface and the skin, and therefore no report necessary.
        if (volume > 0) IntersectionVolumeManager.ME.Add(volume);
        if (volume > 0) IntersectionVolumeManager.ME.AddArea(1);
    }
}
