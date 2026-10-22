using SMMARTS;
using UnityEngine;

/// <summary>
/// Controls the biplane ultrasound mode, which runs a secondary ultrasound manager
/// in parallel with the primary. Settings can be mirrored from the primary at startup
/// and/or enforced continuously at runtime.
/// </summary>
public class UltrasoundBiplaneController : MonoBehaviour
{
    public static UltrasoundBiplaneController ME;

    [Header("Enable or disable biplane mode.")]
    public bool BiplaneActive = false;

    [Header("Copy primary ultrasound settings to secondary on Start.")]
    public bool StartWithSameSettingsAsPrimary = true;

    [Header("Continuously sync secondary settings to match primary every frame?")]
    public bool ConstantlyEnforceSameSettingsAsPrimary = true;

    [Header("Selected sync secondary settings to match primary every frame.")]
    public bool Sync_Depth_cm = true;
    public bool Sync_Anisotropy = true;
    public bool Sync_BeamThickness_mm = false; // biplane is usually thicker
    public bool Sync_Blur = true;
    public bool Sync_DepthAttenuation = true;
    public bool Sync_DepthAttenuationFraction = true;
    public bool Sync_DrawAirGaps = true;
    public bool Sync_MinimumAirGap_mm = true;
    public bool Sync_UseBackgroundTexture = true;
    public bool Sync_ImageableLayers = true;
    public bool Sync_VisibilityOfCenterline = true;
    public bool Sync_ScreenSize = true;
    // We are assuming settings for Top Width and Angle are never the same.


    [Header("GameObjects to activate/deactivate with biplane mode.")]
    public GameObject[] BiplaneGameobjects;

    private UltrasoundManager PrimaryUltrasoundManager;
    private UltrasoundManager SecondaryUltrasoundManager;
    private UltrasoundDisplayController PrimaryUltrasoundDisplayController;
    private UltrasoundDisplayController SecondaryUltrasoundDisplayController;

    [SerializeField] GameObject PrimaryCenterline;
    [SerializeField] GameObject SecondaryCenterline;


    // Singleton reference 
    void Awake()
    {
        if (ME != null) GameObject.Destroy(ME);
        else ME = this;
    }

    void Start()
    {
        PrimaryUltrasoundManager = UltrasoundManager.ME;
        SecondaryUltrasoundManager = UltrasoundManager.ME2;
        PrimaryUltrasoundDisplayController = UltrasoundDisplayController.ME;
        SecondaryUltrasoundDisplayController = UltrasoundDisplayController.ME2;

        if (StartWithSameSettingsAsPrimary) SyncSettings();
    }

    void Update()
    {
        // If BiplaneActive was toggled in the inspector, apply the change.
        if (SecondaryUltrasoundManager.enabled != BiplaneActive)
            UpdateBiplane();

        // Poll primary settings and mirror to secondary each frame.
        // Property comparisons are lightweight, so per-frame polling is intentional here.
        if (ConstantlyEnforceSameSettingsAsPrimary)
        {
            SyncSettings();
        }
    }

    /// <summary>
    /// Copies selected mirrored settings from the primary ultrasound manager to the secondary.
    /// </summary>
    void SyncSettings()
    {

        if (Sync_Depth_cm && SecondaryUltrasoundManager.Depth_cm != PrimaryUltrasoundManager.Depth_cm) 
            SecondaryUltrasoundManager.Depth_cm = PrimaryUltrasoundManager.Depth_cm;
        
        if (Sync_Anisotropy && SecondaryUltrasoundManager.Anisotropy != PrimaryUltrasoundManager.Anisotropy) 
            SecondaryUltrasoundManager.Anisotropy = PrimaryUltrasoundManager.Anisotropy;
        
        if (Sync_BeamThickness_mm && SecondaryUltrasoundManager.BeamThickness_mm != PrimaryUltrasoundManager.BeamThickness_mm) 
            SecondaryUltrasoundManager.BeamThickness_mm = PrimaryUltrasoundManager.BeamThickness_mm;
        
        if (Sync_Blur && SecondaryUltrasoundManager.Blur != PrimaryUltrasoundManager.Blur) 
            SecondaryUltrasoundManager.Blur = PrimaryUltrasoundManager.Blur;
        
        if (Sync_DepthAttenuation && SecondaryUltrasoundManager.DepthAttenuation != PrimaryUltrasoundManager.DepthAttenuation) 
            SecondaryUltrasoundManager.DepthAttenuation = PrimaryUltrasoundManager.DepthAttenuation;
        
        if (Sync_DepthAttenuationFraction && SecondaryUltrasoundManager.DepthAttenuationFraction != PrimaryUltrasoundManager.DepthAttenuationFraction) 
            SecondaryUltrasoundManager.DepthAttenuationFraction = PrimaryUltrasoundManager.DepthAttenuationFraction;
        
        if (Sync_DrawAirGaps && SecondaryUltrasoundManager.DrawAirGaps != PrimaryUltrasoundManager.DrawAirGaps) 
            SecondaryUltrasoundManager.DrawAirGaps = PrimaryUltrasoundManager.DrawAirGaps;
        
        if (Sync_MinimumAirGap_mm && SecondaryUltrasoundManager.MinimumAirGap_mm != PrimaryUltrasoundManager.MinimumAirGap_mm) 
            SecondaryUltrasoundManager.MinimumAirGap_mm = PrimaryUltrasoundManager.MinimumAirGap_mm;
        
        if (Sync_UseBackgroundTexture && SecondaryUltrasoundManager.UseBackgroundTexture != PrimaryUltrasoundManager.UseBackgroundTexture) 
            SecondaryUltrasoundManager.UseBackgroundTexture = PrimaryUltrasoundManager.UseBackgroundTexture;
        
        if (Sync_ImageableLayers && SecondaryUltrasoundManager.ImageableLayers != PrimaryUltrasoundManager.ImageableLayers) 
            SecondaryUltrasoundManager.ImageableLayers = PrimaryUltrasoundManager.ImageableLayers;
        
        if (Sync_VisibilityOfCenterline && SecondaryCenterline.activeInHierarchy != PrimaryCenterline.activeInHierarchy)
            SecondaryCenterline.SetActive(PrimaryCenterline.activeInHierarchy);

        if (Sync_ScreenSize && UltrasoundDisplayController.ME2.UltrasoundScreenSize != UltrasoundDisplayController.ME.UltrasoundScreenSize)
                UltrasoundDisplayController.ME2.UltrasoundScreenSize = UltrasoundDisplayController.ME.UltrasoundScreenSize;

    }




    /// <summary> Enables biplane mode. </summary>
    public void BiplaneOn()
    {
        BiplaneActive = true;
        UpdateBiplane();
    }

    /// <summary> Disables biplane mode. </summary>
    public void BiplaneOff()
    {
        BiplaneActive = false;
        UpdateBiplane();
    }

    /// <summary> Toggles biplane mode on or off. </summary>
    public void ToggleBiplane()
    {
        BiplaneActive = !BiplaneActive;
        UpdateBiplane();
    }

    /// <summary>
    /// Applies the current BiplaneActive state to the secondary manager,
    /// display controller, and all associated GameObjects.
    /// </summary>
    void UpdateBiplane()
    {
        SecondaryUltrasoundManager.enabled = BiplaneActive;
        SecondaryUltrasoundDisplayController.enabled = BiplaneActive;
        foreach (var go in BiplaneGameobjects)
            go.SetActive(BiplaneActive);
    }
}