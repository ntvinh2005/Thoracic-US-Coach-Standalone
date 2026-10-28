using UnityEngine;
using SMMARTS;
using System.Collections;

/// <summary>
/// Panel for Advanced Users with Shift+U.
/// Runtime developer or advanced user interface for adjusting ultrasound simulation parameters via GUI.
/// Activated with Left Shift + U. Designed for end users and instructors who need
/// parameter control without Unity editor access.
/// </summary>
public class UltrasoundManagerPanel : MonoBehaviour
{
    public bool showSettings = false;
    public float GUI_ScaleFactorAdjust = 1f;
    public float scaleFactor;

    // Store default values - Primary
    private bool defaultRendering;
    private bool defaultDotOnRight;
    private int defaultDepth_cm;
    private int defaultWidth;
    private int defaultAngle;
    private float defaultBeamThickness_mm;
    private bool defaultBlur;
    private bool defaultDepthAttenuation;
    private float defaultDepthAttenuationFraction;
    private bool defaultAnisotropy;
    private bool defaultDrawAirGaps;
    private float defaultMinimumAirGap_mm;
    private bool defaultInvertAxisViewCogAid;
    private bool defaultInvertInPlaneCogAid;
    private bool defaultInvertOutOfPlaneCogAid;

    // Store default values - Biplane
    private bool defaultBiplaneActive;
    private int defaultWidth_ME2;
    private int defaultAngle_ME2;
    private float defaultBeamThickness_mm_ME2;

    void Start()
    {
        StartCoroutine(CaptureDefaultsAfterDelay());
    }

    /// <summary>
    /// Captures ultrasound settings as defaults after a delay to ensure UltrasoundManager
    /// has fully initialized. The 3-second delay prevents race conditions where this
    /// script might run before the main ultrasound system is ready.
    /// </summary>
    private IEnumerator CaptureDefaultsAfterDelay()
    {
        yield return new WaitForSeconds(3f);

        // Capture initial values as defaults - Primary
        defaultRendering = UltrasoundManager.ME.Rendering;
        defaultDotOnRight = UltrasoundDisplayController.ME.DotOnRight;
        defaultDepth_cm = UltrasoundManager.ME.Depth_cm;
        defaultWidth = UltrasoundManager.ME.Width;
        defaultAngle = UltrasoundManager.ME.Angle;
        defaultBeamThickness_mm = UltrasoundManager.ME.BeamThickness_mm;
        defaultBlur = UltrasoundManager.ME.Blur;
        defaultDepthAttenuation = UltrasoundManager.ME.DepthAttenuation;
        defaultDepthAttenuationFraction = UltrasoundManager.ME.DepthAttenuationFraction;
        defaultAnisotropy = UltrasoundManager.ME.Anisotropy;
        defaultDrawAirGaps = UltrasoundManager.ME.DrawAirGaps;
        defaultMinimumAirGap_mm = UltrasoundManager.ME.MinimumAirGap_mm;
        defaultInvertAxisViewCogAid = UltrasoundDisplayController.ME.InvertAxisViewCogAid;
        defaultInvertInPlaneCogAid = UltrasoundDisplayController.ME.InvertInPlaneCogAid;
        defaultInvertOutOfPlaneCogAid = UltrasoundDisplayController.ME.InvertOutOfPlaneCogAid;

        // Capture initial values as defaults - Biplane
        defaultBiplaneActive = UltrasoundBiplaneController.ME.BiplaneActive;
        defaultWidth_ME2 = UltrasoundManager.ME2.Width;
        defaultAngle_ME2 = UltrasoundManager.ME2.Angle;
        defaultBeamThickness_mm_ME2 = UltrasoundManager.ME2.BeamThickness_mm;
    }

    /// <summary>
    /// Handles keyboard input for toggling the backdoor interface.
    /// Left Shift + U activates/deactivates the settings panel.
    /// </summary>
    void Update()
    {
        if (Input.GetKey(KeyCode.LeftShift) && Input.GetKeyDown(KeyCode.U))
            showSettings = !showSettings;
    }

    /// <summary>
    /// Renders the backdoor GUI interface using Unity's immediate mode GUI system.
    /// Implements DPI scaling for consistent appearance across different resolutions.
    /// Provides real-time controls for all major ultrasound simulation parameters.
    /// </summary>
    void OnGUI()
    {
        if (!showSettings) return;

        // DPI Scaling System - maintains consistent GUI size across different display densities
        scaleFactor = Screen.dpi / 96f * GUI_ScaleFactorAdjust; // 96 is standard DPI
        GUI.matrix = Matrix4x4.Scale(new Vector3(scaleFactor, scaleFactor, 1));

        // Panel sizing and positioning - responsive to screen size
        float panelWidth = (Screen.width * 0.4f) / scaleFactor;
        float panelHeight = (Screen.height * 0.90f) / scaleFactor;

        // Center the panel
        float x = (Screen.width - panelWidth * scaleFactor) / 2 / scaleFactor;
        float y = (Screen.height - panelHeight * scaleFactor) / 2 / scaleFactor;

        // Create a darker, more opaque background style
        GUIStyle boxStyle = new GUIStyle(GUI.skin.box);
        boxStyle.normal.background = MakeTex(2, 2, new Color(0f, 0f, 0f, 0.85f));

        GUI.Box(new Rect(x, y, panelWidth, panelHeight), "", boxStyle);

        GUILayout.BeginArea(new Rect(x + 10, y + 10, panelWidth - 20, panelHeight - 20));

        GUILayout.Label("ULTRASOUND PANEL");

        // Master rendering toggle
        UltrasoundManager.ME.Rendering = GUILayout.Toggle(UltrasoundManager.ME.Rendering, "Rendering On/Off");

        // Image and Cognitive Aid Orientation Controls
        GUILayout.Label("--- Image and Cognitive Aid Orientation ---");

        string orientationButtonText = UltrasoundDisplayController.ME.DotOnRight ? "Orientation: Right" : "Orientation: Left";
        if (GUILayout.Button(orientationButtonText, GUILayout.Width(200)))
            UltrasoundDisplayController.ME.ReverseImage();

        bool newInvertAxisView = GUILayout.Toggle(UltrasoundDisplayController.ME.InvertAxisViewCogAid, "Invert Axis View Cog Aid");
        if (newInvertAxisView != UltrasoundDisplayController.ME.InvertAxisViewCogAid)
        {
            UltrasoundDisplayController.ME.InvertAxisViewCogAid = newInvertAxisView;
            UltrasoundDisplayController.ME.SetCogAidSidedness();
        }

        bool newInvertInPlane = GUILayout.Toggle(UltrasoundDisplayController.ME.InvertInPlaneCogAid, "Invert In-Plane Cog Aid");
        if (newInvertInPlane != UltrasoundDisplayController.ME.InvertInPlaneCogAid)
        {
            UltrasoundDisplayController.ME.InvertInPlaneCogAid = newInvertInPlane;
            UltrasoundDisplayController.ME.SetCogAidSidedness();
        }

        bool newInvertOutOfPlane = GUILayout.Toggle(UltrasoundDisplayController.ME.InvertOutOfPlaneCogAid, "Invert Out-Of-Plane Cog Aid");
        if (newInvertOutOfPlane != UltrasoundDisplayController.ME.InvertOutOfPlaneCogAid)
        {
            UltrasoundDisplayController.ME.InvertOutOfPlaneCogAid = newInvertOutOfPlane;
            UltrasoundDisplayController.ME.SetCogAidSidedness();
        }

        // Insonation Plane Geometry Controls
        GUILayout.Label("--- Insonation Plane Geometry ---");

        GUILayout.Label($"Depth (cm): {UltrasoundManager.ME.Depth_cm}");
        UltrasoundManager.ME.Depth_cm = Mathf.RoundToInt(GUILayout.HorizontalSlider(UltrasoundManager.ME.Depth_cm, 1, 15));

        GUILayout.Label($"Width at Probe Face (mm): {UltrasoundManager.ME.Width}");
        UltrasoundManager.ME.Width = Mathf.RoundToInt(GUILayout.HorizontalSlider(UltrasoundManager.ME.Width, 1, 100));

        GUILayout.Label($"Angle: {UltrasoundManager.ME.Angle}°   (Spread Angle: {UltrasoundManager.ME.Angle * 2}°)");
        UltrasoundManager.ME.Angle = Mathf.RoundToInt(GUILayout.HorizontalSlider(UltrasoundManager.ME.Angle, 2, 60));

        GUILayout.Label($"Beam Thickness (mm): {UltrasoundManager.ME.BeamThickness_mm:F1}");
        UltrasoundManager.ME.BeamThickness_mm = GUILayout.HorizontalSlider(UltrasoundManager.ME.BeamThickness_mm, 0, 4);

        // Realism Effects
        GUILayout.Label("--- Realism Effects ---");

        UltrasoundManager.ME.Blur = GUILayout.Toggle(UltrasoundManager.ME.Blur, "Blur_Primary");
        UltrasoundManager.ME.DepthAttenuation = GUILayout.Toggle(UltrasoundManager.ME.DepthAttenuation, "Depth Attenuation");

        float attenuationPercent = UltrasoundManager.ME.DepthAttenuationFraction * 100f;
        GUILayout.Label($"Max Depth Attenuation (%): {attenuationPercent:F0}");
        attenuationPercent = GUILayout.HorizontalSlider(attenuationPercent, 0, 100);
        UltrasoundManager.ME.DepthAttenuationFraction = attenuationPercent / 100f;

        UltrasoundManager.ME.Anisotropy = GUILayout.Toggle(UltrasoundManager.ME.Anisotropy, "Anisotropy_Primary");

        // Air Gap Controls
        GUILayout.Label("--- Acceptable Air Gap ---");

        UltrasoundManager.ME.DrawAirGaps = GUILayout.Toggle(UltrasoundManager.ME.DrawAirGaps, "Air Gaps Hide Image");

        GUILayout.Label($"Minimum Tolerable Air Gap (mm): {UltrasoundManager.ME.MinimumAirGap_mm:F1}");
        UltrasoundManager.ME.MinimumAirGap_mm = GUILayout.HorizontalSlider(UltrasoundManager.ME.MinimumAirGap_mm, 0, 10);

        // Biplane Controls
        GUILayout.Label("--- Biplane ---");

        bool newBiplaneActive = GUILayout.Toggle(UltrasoundBiplaneController.ME.BiplaneActive, "Biplane Active");
        if (newBiplaneActive != UltrasoundBiplaneController.ME.BiplaneActive)
        {
            if (newBiplaneActive) UltrasoundBiplaneController.ME.BiplaneOn();
            else UltrasoundBiplaneController.ME.BiplaneOff();
        }

        GUILayout.Label($"Width at Probe Face (mm): {UltrasoundManager.ME2.Width}");
        UltrasoundManager.ME2.Width = Mathf.RoundToInt(GUILayout.HorizontalSlider(UltrasoundManager.ME2.Width, 1, 100));

        GUILayout.Label($"Angle: {UltrasoundManager.ME2.Angle}°   (Spread Angle: {UltrasoundManager.ME2.Angle * 2}°)");
        UltrasoundManager.ME2.Angle = Mathf.RoundToInt(GUILayout.HorizontalSlider(UltrasoundManager.ME2.Angle, 2, 60));

        GUILayout.Label($"Beam Thickness (mm): {UltrasoundManager.ME2.BeamThickness_mm:F1}");
        UltrasoundManager.ME2.BeamThickness_mm = GUILayout.HorizontalSlider(UltrasoundManager.ME2.BeamThickness_mm, 0, 4);

        // Reset
        if (GUILayout.Button("Reset to Defaults"))
        {
            UltrasoundManager.ME.Rendering = defaultRendering;
            UltrasoundDisplayController.ME.DotOnRight = defaultDotOnRight;
            UltrasoundManager.ME.Depth_cm = defaultDepth_cm;
            UltrasoundManager.ME.Width = defaultWidth;
            UltrasoundManager.ME.Angle = defaultAngle;
            UltrasoundManager.ME.BeamThickness_mm = defaultBeamThickness_mm;
            UltrasoundManager.ME.Blur = defaultBlur;
            UltrasoundManager.ME.DepthAttenuation = defaultDepthAttenuation;
            UltrasoundManager.ME.DepthAttenuationFraction = defaultDepthAttenuationFraction;
            UltrasoundManager.ME.Anisotropy = defaultAnisotropy;
            UltrasoundManager.ME.DrawAirGaps = defaultDrawAirGaps;
            UltrasoundManager.ME.MinimumAirGap_mm = defaultMinimumAirGap_mm;
            UltrasoundDisplayController.ME.InvertAxisViewCogAid = defaultInvertAxisViewCogAid;
            UltrasoundDisplayController.ME.InvertInPlaneCogAid = defaultInvertInPlaneCogAid;
            UltrasoundDisplayController.ME.InvertOutOfPlaneCogAid = defaultInvertOutOfPlaneCogAid;
            UltrasoundDisplayController.ME.SetCogAidSidedness();

            if (UltrasoundBiplaneController.ME.BiplaneActive != defaultBiplaneActive)
            {
                if (defaultBiplaneActive) UltrasoundBiplaneController.ME.BiplaneOn();
                else UltrasoundBiplaneController.ME.BiplaneOff();
            }
            UltrasoundManager.ME2.Width = defaultWidth_ME2;
            UltrasoundManager.ME2.Angle = defaultAngle_ME2;
            UltrasoundManager.ME2.BeamThickness_mm = defaultBeamThickness_mm_ME2;

            Debug.Log("Ultrasound settings reset to defaults");
        }

        GUILayout.EndArea();
    }

    /// <summary>
    /// Creates a solid color texture for GUI styling purposes.
    /// Used to generate the semi-transparent dark background for the settings panel.
    /// </summary>
    private Texture2D MakeTex(int width, int height, Color col)
    {
        Color[] pix = new Color[width * height];
        for (int i = 0; i < pix.Length; i++)
            pix[i] = col;
        Texture2D result = new Texture2D(width, height);
        result.SetPixels(pix);
        result.Apply();
        return result;
    }
}