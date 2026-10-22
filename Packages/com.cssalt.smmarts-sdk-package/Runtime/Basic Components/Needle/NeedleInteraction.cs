using UnityEngine;
using System.Collections.Generic;
using System.Linq;

namespace SMMARTS
{
    public class NeedleInteraction : MonoBehaviour
    {
        /*
         * Overview: This is a Needle-Anatomy Collision System that:
         * Handles virtual needle interactions with anatomy meshes.
         * Consolidates functionality into a single, friendly API.
         * Uses hub-to-tip and tip-to-hub raycasts to detect penetration, depth in mm, and backwall status.
         * Extended forward raycasting provides distance-to-surface data (in mm) for objects not yet reached.
         * Optimized for complex concave anatomy meshes with LayerMask filtering.
         * Returns comprehensive NeedleHitInfo array ordered by distance from needle hub.
         * Designed for 25fps SMMARTS simulation performance requirements.
         * Requires NeedleHubMarker and NeedleTipMarker child GameObjects on needle prefabs.
         * Refer to the git wiki page in SMMARTS repository for detailed description and use guide.
         */

        [Header("Needle Components")]
        private Transform needleHub;
        private Transform needleTip;

        [Header("Rays in Scene view for your development")]
        public bool showDebugRays = false;

        void Awake()
        {
            // Find and cache needle hub and tip references
            needleHub = transform.Find("NeedleHubMarker");
            needleTip = transform.Find("NeedleTipMarker");

            if (needleHub == null)
                Debug.LogError($"NeedleHub not found on {gameObject.name}. Make sure needle prefab has NeedleHub child object.");

            if (needleTip == null)
                Debug.LogError($"NeedleTip not found on {gameObject.name}. Make sure needle prefab has NeedleTip child object.");
        }

        public NeedleInteractionSummary[] TestPenetration(LayerMask mask)
        {
            if (needleHub == null || needleTip == null)
                return new NeedleInteractionSummary[0];

            Vector3 hubPos = needleHub.position;
            Vector3 tipPos = needleTip.position;
            Vector3 needleDirection = (tipPos - hubPos).normalized;
            float needleLength = Vector3.Distance(hubPos, tipPos); // mm

            // Hub-to-tip raycast for primary collision detection
            RaycastHit[] hubToTipHits = Physics.RaycastAll(hubPos, needleDirection, Mathf.Infinity, mask);

            // Tip-to-hub raycast for backwall detection
            RaycastHit[] tipToHubHits = Physics.RaycastAll(tipPos, -needleDirection, needleLength, mask);

            if (showDebugRays)
            {
                Debug.DrawRay(hubPos, needleDirection * needleLength, Color.green, 0.1f);
                Debug.DrawRay(tipPos, -needleDirection * needleLength, Color.red, 0.1f);
            }

            return ProcessHits(hubToTipHits, tipToHubHits, hubPos, tipPos);
        }

        public NeedleInteractionSummary[] TestPenetration(LayerMask mask, GameObject target)
        {
            // Same raycast logic but filter results to only include the target GameObject
            NeedleInteractionSummary[] allHits = TestPenetration(mask);
            return allHits.Where(hit => hit.gameObject == target).ToArray();
        }


        private NeedleInteractionSummary[] ProcessHits(RaycastHit[] hubToTipHits, RaycastHit[] tipToHubHits, Vector3 hubPos, Vector3 tipPos)
        {
            List<NeedleInteractionSummary> results = new List<NeedleInteractionSummary>();
            Vector3 needleDirection = (tipPos - hubPos).normalized;

            // Sort hub-to-tip hits by distance from hub
            var sortedHits = hubToTipHits.OrderBy(hit => hit.distance).ToArray();

            foreach (RaycastHit hit in sortedHits)
            {
                // Calculate distance from tip
                float distanceFromHub = hit.distance; // mm
                float needleLength = Vector3.Distance(hubPos, tipPos); // mm
                float distanceFromTip = needleLength - distanceFromHub; // mm

                // Check if we're penetrating (tip is past the hit point)
                bool isPenetrating = distanceFromTip >= 0; // Negative means tip is beyond hit point

                // If penetrating, distance is how deep we are
                // If not penetrating, distance is how far away you are.
                distanceFromTip = Mathf.Abs(distanceFromTip); // mm

                // Check for backwall by looking for same object in tip-to-hub hits
                bool isBackwalled = false;
                if (isPenetrating && tipToHubHits.Length > 0)
                {
                    isBackwalled = tipToHubHits.Any(backHit =>
                        backHit.collider.gameObject == hit.collider.gameObject);
                }

                results.Add(new NeedleInteractionSummary(hit, isPenetrating, distanceFromTip, isBackwalled));
            }

            return results.ToArray();
        }

        // future implementations will test for near miss distances of concave meshes.

    }



    [System.Serializable]
    public class NeedleInteractionSummary
    {
        [Header("Penetration Status")]
        public bool isPenetrating;      // True if needle tip is inside this mesh
        public float distanceFromTip;   // If penetrating: depth inside; if not: distance to surface along the axis of the needle. units are mm.
        public bool isBackwalled;       // True if needle penetrated through back wall

        [Header("Hit Object Data")]
        public GameObject gameObject;   // The anatomy object that was hit
        public int layer;              // Layer number of the hit object

        [Header("Raw Raycast Data")]
        public RaycastHit hit;         // Raw Unity raycast data for advanced use cases

        // Constructor for easy creation
        public NeedleInteractionSummary(RaycastHit raycastHit, bool penetrating, float tipDistance, bool backwall = false)
        {
            this.hit = raycastHit;
            this.gameObject = raycastHit.collider.gameObject;
            this.layer = raycastHit.collider.gameObject.layer;
            this.isPenetrating = penetrating;
            this.distanceFromTip = tipDistance; // mm
            this.isBackwalled = backwall;
        }

        // Default constructor for serialization
        public NeedleInteractionSummary() { }
    }
}
