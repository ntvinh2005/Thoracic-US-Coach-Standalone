using UnityEngine;

namespace SMMARTS
{
    /// <summary>
    /// Controls collimation effect for fluoroscopy C-Arm by scaling mask UI elements.
    /// Simulates iris collimation using a circular mask and four edge masks.
    /// </summary>
    public class C_ArmCollimator : MonoBehaviour
    {
        [Header("Mask GameObjects")]
        [Tooltip("Circle mask with transparent center - scales down to reveal more image")]
        public GameObject fluoroCircleMask;

        [Tooltip("Edge masks that scale inward to maintain coverage as circle scales down")]
        public GameObject topEdgeMask;
        public GameObject bottomEdgeMask;
        public GameObject leftEdgeMask;
        public GameObject rightEdgeMask;

        [Header("Collimation Control")]
        [Range(0.37f, 1f)]
        [Tooltip("1.0 = full collimation (small opening), 0.0 = minimal collimation (large opening)")]
        public float CircularCollimationFraction = 1f;



        // CircularCollimation is analogous to iris collimation.
        // according to Philips BV Pulsera 2017 specifications document 452299119161.indd:
        // "Minimal iris diameter < 50 mm on II entrance"
        // Assuming this is a 50mm diameter circle, the edges of the collimation mask touch the edges of a 50mm cube at CircularCollimationFraction = 0.37.
        // Therefore, 0.37 is a reasonable limit for the smallest scale for the circle mask.

        [Header("Debug Info")]
        [SerializeField] private Vector3 currentCircleScale;

        [Header("Iris Control")]
        [Tooltip("Rate at which iris diameter changes per second")]
        public float irisAdjustmentRate = 0.5f;

        public void IncreaseIrisDiameter()
        {
            // Decrease collimation fraction = larger iris opening
            CircularCollimationFraction += irisAdjustmentRate * Time.deltaTime;
            CircularCollimationFraction = Mathf.Clamp(CircularCollimationFraction, 0.37f, 1f);
        }

        public void DecreaseIrisDiameter()
        {
            // Increase collimation fraction = smaller iris opening  
            CircularCollimationFraction -= irisAdjustmentRate * Time.deltaTime;
            CircularCollimationFraction = Mathf.Clamp(CircularCollimationFraction, 0.37f, 1f);
        }



        private bool isIncreaseIrisPressed = false;
        private bool isDecreaseIrisPressed = false;

        void Update()
        {
            UpdateCollimationMasks();

            // Handle continuous button press
            if (isIncreaseIrisPressed)
                IncreaseIrisDiameter();

            if (isDecreaseIrisPressed)
                DecreaseIrisDiameter();
        }

        // Call these from button EventTrigger OnPointerDown
        public void OnIncreaseIrisPressed()
        {
            isIncreaseIrisPressed = true;
        }

        public void OnDecreaseIrisPressed()
        {
            isDecreaseIrisPressed = true;
        }

        // Call these from button EventTrigger OnPointerUp  
        public void OnIncreaseIrisReleased()
        {
            isIncreaseIrisPressed = false;
        }

        public void OnDecreaseIrisReleased()
        {
            isDecreaseIrisPressed = false;
        }




        /// <summary>
        /// Updates all mask scales based on current collimation fraction
        /// </summary>
        private void UpdateCollimationMasks()
        {
            // Circle mask scales down as collimation decreases (more image visible)
            float circleScale = CircularCollimationFraction;
            currentCircleScale = Vector3.one * circleScale;

            // Edge masks scale from 0 to 0.5 based on collimation
            // 0 = no collimation (edge masks at minimum), 1 = full collimation (edge masks at 0.5)
            float edgeScaleValue = 0.5f - (CircularCollimationFraction * 0.5f);

            // Apply circle mask scaling
            if (fluoroCircleMask != null)
            {
                fluoroCircleMask.transform.localScale = currentCircleScale;
            }

            // Apply directional edge mask scaling
            if (topEdgeMask != null)
                topEdgeMask.transform.localScale = new Vector3(1f, edgeScaleValue, 1f);

            if (bottomEdgeMask != null)
                bottomEdgeMask.transform.localScale = new Vector3(1f, edgeScaleValue, 1f);

            if (leftEdgeMask != null)
                leftEdgeMask.transform.localScale = new Vector3(edgeScaleValue, 1f, 1f);

            if (rightEdgeMask != null)
                rightEdgeMask.transform.localScale = new Vector3(edgeScaleValue, 1f, 1f);

        }

        /// <summary>
        /// Set collimation to a specific value
        /// </summary>
        public void SetCollimation(float fraction)
        {
            CircularCollimationFraction = Mathf.Clamp01(fraction);
        }

        /// <summary>
        /// Get current collimation value
        /// </summary>
        public float GetCollimation()
        {
            return CircularCollimationFraction;
        }
    }
}