using SMMARTS;
using System;
using UnityEngine;
using UnityEngine.UI;

public class FluoroUpdateControl : MonoBehaviour
{

    #region Singleton
    public static FluoroUpdateControl ME { get; private set; }
    void Awake()
    {
        if (ME != null && ME != this)
        {
            Destroy(this);
            return;
        }
        ME = this;
        DontDestroyOnLoad(gameObject);
    }
    #endregion

    private bool fluoroLiveVideo = false; // flag for taking "live" fluoro images, like holding down the pedal or saying "go live"
    private bool fluoroThisFrame = false; // flag for taking a single image this frame only

    public bool UseSecondDisplay = true;
    private bool isSecondDisplayActive = false; // flag for something changed with the displays.

    public GameObject FluoroIndicatorDot1;
    public Text currentTimeString1;

    public AudioSource FluoroAudioSource;
    float currentTime;

    public Camera FluoroImagingCamera;

    public bool LowDose;
    public GameObject LowDoseNoiseOverlay;
    FluoroGrainOverlay fluoroGrainOverlayNoiseGenerator;

    public Canvas FluoroCanvas;
    public Camera FluoroTechCamera;
    public Vector3 InitialCameraPosition;
    public Quaternion InitialCameraRotation;

    public MeshVoxelizer MeshVoxelizer_Anatomy;
    public GameObject WhiteboxCommunicator;



    // these are for experiments with real-time revoxelization
    /*
    public bool AutoRefresh;
    private bool UpdateFlag = false; 
    private bool everyotherframe = false;
    public MeshVoxelizer MeshVoxelizer_Anatomy;
    public MeshVoxelizer MeshVoxelizer_Needle;
    public float RevoxelizeInterval = 1; 
    public GameObject AnatomyObject;
    public GameObject NeedleObject;
    */


    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        // this is for runtime revoxelization of the needle anatomy.
        // if we are using a needle overlay it is not necessary.
        // Invoke("Revoxelize", RevoxelizeInterval); 

        // this could be a separate camera on Display 2 for the technician
        if (FluoroTechCamera == null) FluoroTechCamera = Camera.main;
        InitialCameraPosition = FluoroTechCamera.transform.position;
        InitialCameraRotation = FluoroTechCamera.transform.rotation;

        if (WhiteboxCommunicator == null) WhiteboxCommunicator = GameObject.Find("WhiteboxCommunicator");

        // The default display will be display 1. The second display is display 1.
        if (UseSecondDisplay)
        {
            FluoroTechCamera.targetDisplay = 1;
            FluoroCanvas.targetDisplay = 1;
        } else
        {
            FluoroTechCamera.targetDisplay = 0;
            FluoroCanvas.targetDisplay = 0;
        }

        fluoroGrainOverlayNoiseGenerator = LowDoseNoiseOverlay.GetComponent<FluoroGrainOverlay>();

        // Update will check if the second display is active and change to display 2 if needed. 

    }



    public void StartVoxels(string resolution)
    {
        Debug.Log("StartVoxels called with " + resolution);
        MeshVoxelizer_Anatomy.voxelResolution = int.Parse(resolution);
        MeshVoxelizer_Anatomy.gameObject.SetActive(true);

    }

    public void SetLowDose(bool value)
    {
        LowDose = value;
    }
    // for external scripts to call, including C_Arm Voice Control, etc.
    public void TakeSingleImage()
    {
        fluoroThisFrame = true;
    }

    public void TurnOnContinousFluoro()
    {
        fluoroLiveVideo = true;
    }

    public void TurnOffContinousFluoro()
    {
        fluoroLiveVideo = false;
    }


    void Update()
    {
        // the startup delay for ATC seems to hold back the voxelization, the GPU times out, and Unity crashes.
        // turn whitebox communicator on AFTER voxelization is complete.
        // this should be the other way around, whitebox moves anatomy a bit to index plate of that stand,
        // then voxelize.  for demo, this is simple.
        if (WhiteboxCommunicator != null)
        {
            if (MeshVoxelizer_Anatomy.VoxelsDone == true) WhiteboxCommunicator.SetActive(true);
        }

        bool footpedal = Input.GetKey(KeyCode.Space);
        bool fluoroIsOn = footpedal | fluoroLiveVideo | fluoroThisFrame;

        // quick way to turn off the fluoro video if the speech-controlled rad tech can't hear "stop"
        if (Input.GetKeyUp(KeyCode.Space) & fluoroLiveVideo) fluoroLiveVideo = false;

        if (fluoroIsOn)
        {

            currentTime = currentTime + Time.deltaTime;
            TimeSpan time = TimeSpan.FromSeconds(currentTime);
            currentTimeString1.text = time.ToString(@"mm\:ss");

            if (FluoroAudioSource.isPlaying == false) { FluoroAudioSource.Play(); }

            // turn the low dose noise overlay on if the LowDose mode is being used.
            LowDoseNoiseOverlay.gameObject.SetActive(LowDose);
            fluoroGrainOverlayNoiseGenerator.GenerateNoiseEveryFrame = LowDose;
        }
        else
        {
            if (FluoroAudioSource.isPlaying == true) { FluoroAudioSource.Stop(); }
            fluoroGrainOverlayNoiseGenerator.GenerateNoiseEveryFrame = false; // the grain needs to freeze when the pedal is released.
        }

        if (FluoroIndicatorDot1 != null) FluoroIndicatorDot1.SetActive(fluoroIsOn); // | AutoRefresh);

        // this is how we turn on and off the actual rendering. 
        FluoroImagingCamera.enabled = fluoroIsOn;

        // reset flag for one-frame only fluoro
        fluoroThisFrame = false;

        // this is for the second display, if you are running the sim and then plug the display into it while running.
        // its a hot-plug test.
        // you won't have to restart if you forgot to plug in Display 2 at a workshop or demo - and vsoxelization can take minutes.



        /*
        Debug.Log("this code is getting called and displays.length = " + Display.displays.Length);
        if (UseSecondDisplay) // && !isSecondDisplayActive)
        {
            if (Display.displays.Length > 1)
            {
                FluoroTechCamera.targetDisplay = 1;
                FluoroCanvas.targetDisplay = 1;
                Display.displays[1].Activate();
                isSecondDisplayActive = true;
            }
        }
        else if (!UseSecondDisplay) // && isSecondDisplayActive)
        {
            // Switch back to primary display. this works. 
            FluoroTechCamera.targetDisplay = 0;
            FluoroCanvas.targetDisplay = 0;
            Display.displays[0].Activate();
            isSecondDisplayActive = false;
        }
        */

#if UNITY_EDITOR
        // In editor, simulate second display behavior for testing
        // we have to simulate it because in the editor, Display.displays.Length is always 1 and Activate() does nothing.
        if (UseSecondDisplay && !isSecondDisplayActive)
        {
            // second display is 1.
            FluoroTechCamera.targetDisplay = 1;
            FluoroCanvas.targetDisplay = 1;
            isSecondDisplayActive = true;
        }
        else if (!UseSecondDisplay && isSecondDisplayActive)
        {
            // Switch back to primary display, display 0. 
            FluoroTechCamera.targetDisplay = 0;
            FluoroCanvas.targetDisplay = 0;
            isSecondDisplayActive = false;
        }

#else
    // Runtime code for the builds.
        if (UseSecondDisplay && !isSecondDisplayActive)
        {
            if (Display.displays.Length > 1)
            {
                FluoroTechCamera.targetDisplay = 1;
                FluoroCanvas.targetDisplay = 1;
                Display.displays[1].Activate();
                isSecondDisplayActive = true;
            }
        }
        else if (!UseSecondDisplay && isSecondDisplayActive)
        {
            FluoroTechCamera.targetDisplay = 0;
            FluoroCanvas.targetDisplay = 0;
            isSecondDisplayActive = false;
        }
#endif


    }



    // if you move your TUI/Camera View Controller at the anatomy, you loose the user interface for the C-Arm.
    public void ResetView()
    {
        FluoroTechCamera.transform.position = InitialCameraPosition;
        FluoroTechCamera.transform.rotation = InitialCameraRotation;
    }


}
