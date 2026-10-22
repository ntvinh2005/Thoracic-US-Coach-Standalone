using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace SMMARTS
{
    public class InsonationBeamThicknessSlider : MonoBehaviour
    {
        [Header("Slider Settings")]
        [SerializeField] private Slider beamWidthSlider;
        [SerializeField] private float Min_mm = 0f;
        [SerializeField] private float Max_mm = 4f;
        
        [Header("Text Displays")]
        [SerializeField] private TextMeshProUGUI minText;
        [SerializeField] private TextMeshProUGUI maxText;
        [SerializeField] private TextMeshProUGUI currentThicknessText;
        
        void Start()
        {
            // Stupidity check
            if (Min_mm < 0f)
            {
                Debug.LogWarning($"InsonationBeamThicknessSlider: Min_mm cannot be negative! Setting to 0. Was: {Min_mm}");
                Min_mm = 0f;
            }
            
            if (Max_mm <= Min_mm)
            {
                Debug.LogWarning($"InsonationBeamThicknessSlider: Max_mm must be greater than Min_mm! Setting Max to Min + 1. Min: {Min_mm}, Max was: {Max_mm}");
                Max_mm = Min_mm + 1f;
            }
            
            if (beamWidthSlider != null)
            {
                // Set slider range
                beamWidthSlider.minValue = Min_mm;
                beamWidthSlider.maxValue = Max_mm;

                // Set slider to current beam thickness
                // NOTE: Unity will clamp USMgr's BeamThickness_mm to this slider's min/max range - 
                // no matter where USMgr's BeamThickness_mm was set outside of this code.
                beamWidthSlider.value = UltrasoundManager.ME.BeamThickness_mm;
                
                // Set up listener
                beamWidthSlider.onValueChanged.AddListener(OnSliderValueChanged);

                // Initialize text displays
                if (minText != null) minText.text = Min_mm.ToString("F1"); // + " mm";
                if (maxText != null) maxText.text = Max_mm.ToString("F1"); // + " mm";
                UpdateCurrentThicknessDisplay();
            }
        }
        
        void Update()
        {
            // Poll for external changes to beam thickness
            if (beamWidthSlider != null && beamWidthSlider.value != UltrasoundManager.ME.BeamThickness_mm)
            {
                beamWidthSlider.value = UltrasoundManager.ME.BeamThickness_mm;
                UpdateCurrentThicknessDisplay();
            }
        }
        
        private void OnSliderValueChanged(float value)
        {
            UltrasoundManager.ME.BeamThickness_mm = value;
            UpdateCurrentThicknessDisplay();
        }
        
        private void UpdateCurrentThicknessDisplay()
        {
            if (currentThicknessText != null && beamWidthSlider != null)
            {
                currentThicknessText.text = beamWidthSlider.value.ToString("F1"); // + " mm";
            }
        }
    }
}