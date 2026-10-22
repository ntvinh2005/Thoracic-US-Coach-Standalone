using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using TMPro;
using SMMARTS;

/*
Overview: this guy controls the user interface. The buttons call functions in here -
also we update the button appearance to indicate the state of whatever the buttons control.
If you want to change something with code that you can do manually with a button, PLEASE, 
PLEASE call the same functions here that the buttons do so we can update the UI so the UI isnt stale.
If you are looking for needle interaction stuff (tactile thump, LOR, etc) go to the Simulation Manager script.
*/

public class UserInterfaceManager : MonoBehaviour
{
    public static UserInterfaceManager ME;

    // Fullscreen Ultrasound now controlled by Ultrasound's own UI.
    // It used to be here, but the SDK's interface is sophisticated enough we don't need to do it here.

    [Header("Visualization Mode")]
    public int VisualizationModeNumber = 1; // three options
    public bool HybridCrossSection = false;
    public TMP_Text VisualizationButtonText;

    [Header("Anatomic Difficulty")]
    public int AnatomicDifficultyNumber = 1; // two options
    public TMP_Text AnatomicDifficultyButtonText;
    public GameObject[] NormalAnatomicalObjects;
    public GameObject[] ChallengingAnatomicalObjects;

    [Header("Cognitive Aids")]
    public int CognitiveAidsNumber = 1; // three options
    public TMP_Text CognitiveAidsButtonText;
    public GameObject NeedleTrajectoryExtensionCognitiveAid;
    public GameObject NeedlePerpendicularityCognitiveAid;
    public GameObject ProbeClockfaceCognitiveAid;
    public GameObject NeedleAndProbeCognitiveAids;
    public GameObject ThoracicLevelLabels;

    [Header("Ultrasound Realism")]
    public bool UltrasoundRealism = false; // 
    public TMP_Text UltrasoundRealismButtonText;

    [Header("Preset Views Cameras and Lights")]
    // The first camera is the 3D perspective camera (usually Main Camera)
    // The other cameras are typically orthographic anatomical views for coronal, sagittal, or transferse planes
    // Camera[0] must be filled, the others are optional.
    // There must be a light assigned to each camera. Lights can be anywhere in the heirarchy.
    // The same light can be assigned to multiple cameras.
    public int PresetViewNumber = 1; // four options
    public TMP_Text PresetViewsButtonText;
    public int ActiveCameraAndLightIndex = 0; // only Camera[ActiveCameraAndLightIndex] will be enabled, the other cameras will not.
    public Camera[] Cameras;
    public Light[] Lights;
    public Camera Camera;

    [Header("Per-platform camera")]
    public int PlatformCameraSlot = 0;   // which preset slot swaps by build
    public Camera WebGLCamera;
    public Camera DesktopCamera;

    [Header("Hybrid Cross Section")]
    public CrossSectionManager CrossSectionManager;
    public GameObject CrossSectionPoint_Probe;
    public GameObject CrossSectionPoint_Needle;

    [Header("Fat Pad")]
    public bool FatPad;
    public GameObject SkinSurface;
    public GameObject NormalSkinPosition;
    public GameObject FatPadSkinPosition;
    public TMP_Text FatPadButtonText;

    // This class is a singleton, lets make sure there is only one and it persists across all scenes.
    private void Awake()
    {
        // singleton reference:
        if (ME != null) GameObject.Destroy(ME);
        else ME = this;
        DontDestroyOnLoad(this);
    }


    private void Start()
    {

        Cameras[PlatformCameraSlot] = Application.platform == RuntimePlatform.WebGLPlayer
        ? WebGLCamera : DesktopCamera;

        // apply the default view once so the right camera/light is enabled from frame 1
        PresetViewNumber = -1;     // CyclePresetViews does += 1 then lands on 0
        CyclePresetViews();

        Camera.main.nearClipPlane = 0.01f;
        HybridCrossSection = VisualizationModeNumber == 2;
    }





    // called by a UI button, probably labeled "Visualization Mode"
    // three options:
    // 1 = 3D visualization (normal view you see by default)
    // 2 = 3D visualization with the hybrid cross section
    // 3 = fullscreen ultrasound, no 3D visualization.
    public void CycleVisualizationMode()
    {
        // set flag
        VisualizationModeNumber += 1;
        if (VisualizationModeNumber > 3) VisualizationModeNumber = 1;
        HybridCrossSection = VisualizationModeNumber == 2; // the hybrid cross section flag is the second vis mode. 

        // turn on and off the hybrid cross sections. if the manager isn't active on the scene, hybrid cross section is off.
        // NOTE, this is now controlled in Update() as it relates to how the needle and probe are being used, if at all.
        // CrossSectionManager.enabled = HybridCrossSection;

        // set UI text to reflect flag
        if (VisualizationModeNumber == 1) VisualizationButtonText.text = "Visualization\nMode\n<line-height=105%><b><size=160%><color=orange>• </color><color=grey>• •";
        if (VisualizationModeNumber == 2) VisualizationButtonText.text = "Visualization\nMode\n<line-height=105%><b><size=160%><color=grey>• <color=orange>• </color><color=grey>•";
        if (VisualizationModeNumber == 3) VisualizationButtonText.text = "Visualization\nMode\n<line-height=105%><b><size=160%><color=grey>• • <color=orange>•</color>";

        // functionality
        if (VisualizationModeNumber == 1) SMMARTS.UltrasoundDisplayController.ME.FullscreenOff();
        if (VisualizationModeNumber == 2) SMMARTS.UltrasoundDisplayController.ME.FullscreenOff();
        if (VisualizationModeNumber == 3) SMMARTS.UltrasoundDisplayController.ME.FullscreenOn();

        // in fullscreen US, we'll turn off the 3D visualization by just making the clip plane very close for all the cameras (orthographic, main, etc).
        // that way we don't have to turn cameras on or off and manage possible conflicts with Preset Views, which also turns cameras on and off.
        foreach (Camera camera in Cameras)
        {
            camera.farClipPlane = 1000;
            if (VisualizationModeNumber == 3) camera.farClipPlane = 0.4f;
        }

    }

    // called by a UI button, probably labeled "Anatomic Difficulty"
    // two options: normal and challenging
    public void CycleAnatomicDifficulty()
    {
        // set flag
        AnatomicDifficultyNumber += 1;
        if (AnatomicDifficultyNumber > 2) AnatomicDifficultyNumber = 1;

        // set UI text to reflect flag
        if (AnatomicDifficultyNumber == 1) AnatomicDifficultyButtonText.text = "Anatomic\nDifficulty\n<line-height=105%><b><color=orange><size=160%>• <color=grey>•";
        if (AnatomicDifficultyNumber == 2) AnatomicDifficultyButtonText.text = "Anatomic\nDifficulty\n<line-height=105%><b><color=grey><size=160%>• <color=orange>•";

        // functionality
        foreach (GameObject g in NormalAnatomicalObjects) g.SetActive(AnatomicDifficultyNumber == 1);
        foreach (GameObject g in ChallengingAnatomicalObjects) g.SetActive(AnatomicDifficultyNumber == 2);

    }


    // called by a UI button, probabnly labeled "Cognitive Aids"
    // three options: 1=none, 2=standard, 3=standard plus labels on the vertebrae (T4, T5, etc).
    public void CycleCognitiveAids()
    {
        // set flag
        CognitiveAidsNumber += 1;
        if (CognitiveAidsNumber > 3) CognitiveAidsNumber = 1;

        // set UI text to reflect flag
        if (CognitiveAidsNumber == 1) CognitiveAidsButtonText.text = "Cognitive\nAids\n<line-height=105%><b><size=160%><color=orange>• <color=grey>• •";
        if (CognitiveAidsNumber == 2) CognitiveAidsButtonText.text = "Cognitive\nAids\n<line-height=105%><b><size=160%><color=grey>• <color=orange>• <color=grey>•";
        if (CognitiveAidsNumber == 3) CognitiveAidsButtonText.text = "Cognitive\nAids\n<line-height=105%><b><size=160%><color=grey>• • <color=orange>•";

        // functionality 
        // these are controlled in Update() because it depends on what you are doing

    }

    // called by a UI button, probabnly labeled "Ultrasound Realism"
    // two options: normal and "training wheels" - just call the ultrasound manager and its boolean flag
    public void ToggleUltrasoundRealism()
    {
        // set flag
        UltrasoundRealism = !UltrasoundRealism;

        // set UI text to reflect flag
        if (UltrasoundRealism) UltrasoundRealismButtonText.text = "Ultrasound\nRealism\n<line-height=105%><b><size=160%><color=orange>•";
        if (!UltrasoundRealism) UltrasoundRealismButtonText.text = "Ultrasound\nRealism\n<line-height=105%><b><size=160%><color=grey>•";

        // functionality
        if (UltrasoundRealism)  UltrasoundDisplayController.ME.RealismOn();
        if (!UltrasoundRealism) UltrasoundDisplayController.ME.RealismOff();
    }


    // called by a UI button, labeled "Adipose Layer" (Fat Pad)
    // two options: normal and fat pad - this moves the skin to reflect the fat pad surface and toggles some ultrasound depth settings.
    public void ToggleFatPad()
    {
        // set flag
        FatPad = !FatPad;

        // set UI text to reflect flag
        if (FatPad) FatPadButtonText.text = "Adipose Layer\n<line-height=105%><b><size=160%><color=orange>•";
        if (!FatPad) FatPadButtonText.text = "Adipose Layer\n<line-height=105%><b><size=160%><color=grey>•";

        // functionality
        if (!FatPad)
        {
            SkinSurface.transform.position = NormalSkinPosition.transform.position;
            UltrasoundManager.ME.Depth_cm = 5;
            UltrasoundManager.ME.Angle = 5;
        }

        if (FatPad)
        {
            SkinSurface.transform.position = FatPadSkinPosition.transform.position;
            UltrasoundManager.ME.Depth_cm = 9;
            UltrasoundManager.ME.Angle = 10;
        }

    }



    // called by a UI button, probabnly labeled "Preset Views"
    // four options:
    // 0 = standard main perspective camera controlled with camera view controller (TUI)
    // 1 = top-down orthographic
    // 2 = front orthographic
    // 3 = side orthographic - Left
    // 4 = side orthographic - right

    public void CyclePresetViews()
    {

        // if fullscreen US is on, just turn it off, which will restore the last display layer, and leave. 
        if (VisualizationModeNumber == 3) // VisualizationModeNumber 3 is fullscreen US view in this simulation
        {
            CycleVisualizationMode();
            return;
        }

        // increment through the sequence of camera view options - each one has its own flag
        PresetViewNumber += 1;
        if (PresetViewNumber > 4) PresetViewNumber = 0;

        // set UI text to reflect flag
        if (PresetViewNumber == 0) PresetViewsButtonText.text = "Preset\nViews\n<line-height=105%><b><size=160%><color=grey>• • • •";
        if (PresetViewNumber == 1) PresetViewsButtonText.text = "Preset\nViews\n<line-height=105%><b><size=160%><color=orange>• <color=grey>• • •";
        if (PresetViewNumber == 2) PresetViewsButtonText.text = "Preset\nViews\n<line-height=105%><b><size=160%><color=grey>• <color=orange>• <color=grey>• •";
        if (PresetViewNumber == 3) PresetViewsButtonText.text = "Preset\nViews\n<line-height=105%><b><size=160%><color=grey>• • <color=orange>• <color=grey>•";
        if (PresetViewNumber == 4) PresetViewsButtonText.text = "Preset\nViews\n<line-height=105%><b><size=160%><color=grey>• • • <color=orange>•";

        // functionality
        // disable all the cameras, then enable the one we want. same for the lights. 
        ActiveCameraAndLightIndex = PresetViewNumber;

        foreach (Camera camera in Cameras) camera.enabled = false;
        foreach (Light light in Lights) light.enabled = false;

        Cameras[ActiveCameraAndLightIndex].enabled = true;
        Lights[ActiveCameraAndLightIndex].enabled = true;

        // reference to the camera we are using since there are many
        Camera = Cameras[ActiveCameraAndLightIndex];

        CrossSectionManager.targetCamera = Cameras[ActiveCameraAndLightIndex];

        // the near and far clip planes are set by visualization mode

    }


    void Update()
    {
        // These elements related to UI need to be polled every frame.

        // CAMERA CONTROLLER'S EFFECT ON PRESET VIEWS /////////////////////////////////////////////////////////////////
        // if we pressed the camera view controller, this does two things in this order:
        // get out of fullscreen US mode. if VisualizationModeNumber is 3, call CycleVisualizationMode()
        // set PresetViewNumber to 0. if PresetViewNumber isn't 0, set it to 4 then call CyclePresetViews() so it wraps around to 0.
        if (Microcontroller_Manager.ME.TUIPressed)
        {
            if (VisualizationModeNumber == 3) CycleVisualizationMode();
            if (PresetViewNumber != 0) { PresetViewNumber = 4; CyclePresetViews(); }
        }

        // HYBRID CROSS SECTION and PROBE, NEEDLE, AND PRESET VIEW'S EFFECT //////////////////////////////////////////////
        // hybrid cross section isn't even relevant if no needle and probe are being used; what you see will be confusing.
        bool hybridCrossSection_isRelevant = SimulationManager.ME.needleInSkin || !UltrasoundManager.ME.ProbeIsFarFromSkin;
        CrossSectionManager.enabled = HybridCrossSection & hybridCrossSection_isRelevant;

        // hybrid cross section by default driven by the US probe; if not, then the needle.
        // this helps Barys teach ultrasound assisted techniques with the simulator.
        if (CrossSectionManager.enabled)
        {
            CrossSectionManager.targetCamera = Cameras[ActiveCameraAndLightIndex];

            if (!UltrasoundManager.ME.ProbeIsFarFromSkin)
            {
                CrossSectionManager.targetObject = CrossSectionPoint_Probe;
                CrossSectionManager.mode = CrossSectionMode.SmartFollow;
            }
            else
            {
                CrossSectionManager.targetObject = CrossSectionPoint_Needle;
                CrossSectionManager.mode = CrossSectionMode.FaceCamera;
            }
        }

        // COGNITIVE AIDS - SOME DEPEND ON WHAT TRACKED TOOL YOU ARE USING //////////////////////////////////////////////
        if (CognitiveAidsNumber == 1)
        {
            NeedleTrajectoryExtensionCognitiveAid.SetActive(false);
            NeedlePerpendicularityCognitiveAid.SetActive(false);
            ProbeClockfaceCognitiveAid.SetActive(false);
            NeedleAndProbeCognitiveAids.SetActive(false);
        } else
        {
            NeedleTrajectoryExtensionCognitiveAid.SetActive(true);

            bool usingNeedle = SimulationManager.ME.needleInSkin;
            bool usingProbe = !UltrasoundManager.ME.ProbeIsFarFromSkin;
            NeedlePerpendicularityCognitiveAid.SetActive(usingNeedle & !usingProbe);
            ProbeClockfaceCognitiveAid.SetActive(usingProbe & !usingNeedle);
            NeedleAndProbeCognitiveAids.SetActive(usingNeedle & usingProbe);
        }
        // Thoracic Level Labels
        ThoracicLevelLabels.SetActive(CognitiveAidsNumber == 3);

        // TOASTS AND ALERTS ////////////////////////////////////////////////////////////////////////////////////////////
        // these are handled in Simulation Manager, along with the raycast hits and what to do with them.


    }




}
