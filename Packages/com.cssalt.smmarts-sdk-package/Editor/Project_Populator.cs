using UnityEngine;
using UnityEditor;
using System;
using System.Linq;
using System.Collections.Generic;
using UnityEditor.SceneManagement;
using Object = UnityEngine.Object;

namespace SMMARTS
{
    /// <summary>
    /// Editor window for setting up new SMMARTS mixed reality projects.
    /// Handles instantiation of core components like tracking and microcontroller connection, 
    /// ultrasound probe (linear by default), needle (for interventional sims), and the camera controller (TUI)
    /// </summary>
    [System.Serializable]
    public class Project_Populator : EditorWindow
    {
        #region UI State Variables
        // Toggle states for UI checkboxes - these track what the user wants
        static bool newindexPlateStatus;
        static bool newCameraControllerStatus; // aka TUI for Tangible User Interface
        static bool newUltrasoundStatus;
        static bool newFluoroscopyStatus;
        static bool newReplayerStatus;
        static bool newKeywordRecognizerStatus;
        static bool newVideoReplayerStatus;
        static bool newVoiceAnnotationStatus;

        // for the autopopulator, which runs be default on new scenes
        private bool isAutoPopulating = false;
        private float autoPopulateStartTime = 0f;
        private int autoPopulateStep = 0;

        // Actual scene state - these track what currently exists in the scene
        static bool isIndexPlateActive, isCameraControllerActive, isUltrasoundActive, isNeedleActive, isFluoroscopyActive, isReplayerActive;
        static bool isKeywordRecognizerActive, isVideoReplayerActive, isVoiceAnnotationActive;
        static bool isBasicSceneSetUp; // True when WhiteBoxCommunicator exists

        #endregion

        #region Object References
        static GameObject communicator; // The main WhiteBoxCommunicator that manages tracking
                                        // Add these to the #region Object References section (around line 26):
        

        // References to instantiated prefabs - used for cleanup and ATC assignment
        static UnityEngine.Object indexPlate, cameraController, ultrasound, needle, fluoroscopy, replayer;
        static UnityEngine.Object keywordRecognizer, videoReplayer, voiceAnnotation;
        static UnityEngine.Object newIndexPlate, newCameraController, newUltrasound, newNeedle, newFluoroscopy, newReplayer; // For custom selection
        
        static GameObject atcTrackedObj; // Temporary reference for ATC assignment

        // NEW: Probe mode UI state
        static bool newLinearProbeStatus = true;        // Linear is the default
        static bool newCurvilinearProbeStatus;
        static bool isCurvilinearProbeActive;           // Linear is implicit: !isCurvilinearProbeActive
        #endregion

        #region Ultrasound Probe Presets
        // NEW: Preset values for the two ultrasound probe modes.
        // Stored as (insonationPlaneTopWidth_mm, scanDepth_mm, angle_degrees).
        // Tweak these here if the desired probe characteristics change.
        //
        // Linear probe: narrow, near-rectangular insonation plane.
        // Curvilinear probe: wider, more fan-shaped insonation plane with greater depth.
        private static readonly (int width, int depth, int angle) LinearPrimaryPreset = (45, 50, 3);
        private static readonly (int width, int depth, int angle) LinearSecondaryPreset = (6, 50, 20);
        private static readonly (int width, int depth, int angle) CurvilinearPrimaryPreset = (70, 130, 25);
        private static readonly (int width, int depth, int angle) CurvilinearSecondaryPreset = (16, 130, 20);

        private const string CurvilinearExtensionName = "Curvilinear Extension";
        #endregion

        #region Needle Configuration
        string[] needleOptions = new string[] { "None", "Basic Needle", "CVA Needle and Syringe", "RA Tuohy Needle" };
        static int needleIndex = 0;     // Current needle selection
        static int newNeedleIndex = 0;  // New needle selection from dropdown
        #endregion

        #region Utility Variables
        List<int> instanceIDs = new List<int>(); // Track instantiated objects for cleanup
        string warningMessage = ""; // Display validation messages to user
        #endregion

        #region Unity Editor Integration
        /// <summary>
        /// Creates the Project Populator window via SMMARTS menu
        /// and we move it to the very top of the SMMARTS menu
        /// </summary>
        [MenuItem("SMMARTS/SMMARTS Scene Configurator %P", false, 0)]
        static void Init()
        {
            Project_Populator window = (Project_Populator)GetWindow(typeof(Project_Populator));
            //window.minSize = new Vector2(400, 600); // Set minimum width and height
            ReadPrefsFromScene();
            window.Show();
        }

        /// <summary>
        /// Called when hierarchy changes - keeps UI in sync with scene state
        /// </summary>
        private void OnHierarchyChange()
        {
            ReadPrefsFromScene();
            Repaint(); // Update UI to reflect current state
        }

        /// <summary>
        /// Called when window opens - initialize state and check for updates
        /// </summary>
        private void Awake()
        {
          //  ReadPrefsFromScene();
          //  Repaint();
          //  UpdateChecker.CheckUpdates(); // Check for SDK updates
        }
        #endregion




        #region Main UI
        /// <summary>
        /// Main UI drawing method - creates the project setup interface
        /// </summary>
        void OnGUI()
        {
            // Don't allow project setup while playing
            if (Application.isPlaying)
                return;

            GUILayout.BeginVertical();

            // Step 1: Set up core communicator - this enables all other options
            GUI.enabled = !isBasicSceneSetUp;

            // if you press the "Set Up White Box Communicator" button while a whitebox is already on the scene your project gets messed up.
            // so, if we find a WhiteboxCommunicator, lets assume the basic configuration has been setup.
            // at this point, we have already set isBasicSceneSetUp flag based on if GO "WhiteboxCommunicator" was found.

            // this block should only run if there is not GO named "WhiteboxCommunicator"
            if (!isBasicSceneSetUp)
            {
                GUILayout.Label("------ Automated Setup", EditorStyles.miniBoldLabel);

                if (GUILayout.Button("\n" + "Pre-Populate Scene" + "\n"))
                {
                    // Instantiate the main tracking communicator
                    communicator = Instantiate(Resources.Load<GameObject>("WhiteBoxCommunicator")) as GameObject;
                    communicator.name = communicator.name.Replace("(Clone)", "");
                    isBasicSceneSetUp = true;

                    // Set up standard layer names before instantiating any prefabs
                    // This ensures prefabs show proper layer names in inspector
                    SetupProjectLayers();

                    // Assign the default layers to the main camera.
                    SetupMainCameraCullingMask();

                    // move the camera so it points to the index plate, if it is still at the origin ( 0,0,0).
                    SetupMainCameraPosition();

                    // Start the auto-populate sequence
                    isAutoPopulating = true;
                    autoPopulateStartTime = (float)EditorApplication.timeSinceStartup;
                    autoPopulateStep = 0;
                    EditorApplication.update += AutoPopulateUpdate;

                    // Set up standard tags as well. this is a placeholder in case you need to add standardized tags.
                    // SetupProjectTags();
                }

                GUILayout.Label("------ Manual Setup (Advanced)", EditorStyles.miniBoldLabel);

                if (GUILayout.Button("Set Up Whitebox Communicator"))
                {
                    // Instantiate the main tracking communicator
                    communicator = Instantiate(Resources.Load<GameObject>("WhiteBoxCommunicator")) as GameObject;
                    communicator.name = communicator.name.Replace("(Clone)", "");
                    isBasicSceneSetUp = true;

                    // Set up standard layer names before instantiating any prefabs
                    // This ensures prefabs show proper layer names in inspector
                    SetupProjectLayers();

                    // Assign the default layers to the main camera.
                    SetupMainCameraCullingMask();

                    // move the camera so it points to the index plate, if it is still at the origin (0,0,0).
                    SetupMainCameraPosition();

                    // Set up standard tags as well. this is a placeholder in case you need to add standardized tags.
                    // SetupProjectTags();
                }
            }

            // Step 2: Component selection - only available after communicator setup
            if (isBasicSceneSetUp)
            {
                GUI.enabled = true;
                GUILayout.Label("------Select SMMARTS Components------", EditorStyles.miniBoldLabel);

                // INDEX PLATE - Standard tracking base object
                newindexPlateStatus = EditorGUILayout.Toggle("Standard Index Plate", newindexPlateStatus);
                isIndexPlateActive = GameObject.Find("IndexPlate");

                if (isIndexPlateActive && !newindexPlateStatus)
                {
                    // User unchecked - remove existing index plate
                    // nope, you are not allowed to delete the index plate. 
                    // DestroyImmediate(GameObject.Find("IndexPlate"));
                }
                else if (!isIndexPlateActive && newindexPlateStatus)
                {
                    // User checked - create index plate and assign to ATC
                    indexPlate = PrefabUtility.InstantiatePrefab(Resources.Load<GameObject>("IndexPlate"));
                    instanceIDs.Add(indexPlate.GetInstanceID());
                    communicator.GetComponent<ATC>().BaseObject = (GameObject)indexPlate;

                    // Ensure EventSystem exists for UI interaction
                    EnsureEventSystem();
                }


                // Camera Controller aka TUI PROBE for Tangible User Interface
                newCameraControllerStatus = EditorGUILayout.Toggle("Camera Controller (TUI)", isCameraControllerActive);
                if (newCameraControllerStatus != isCameraControllerActive)
                {
                    if (newCameraControllerStatus)
                    {
                        // Create cameara controller and assign to ATC TrackedObject1
                        cameraController = PrefabUtility.InstantiatePrefab(Resources.Load<GameObject>("TUI Probe"));
                        instanceIDs.Add(cameraController.GetInstanceID());
                        communicator.GetComponent<ATC>().TrackedObject1 = (GameObject)cameraController;
                    }
                    else
                    {
                        // Remove camera controller - rare, almost all simulators have one.
                        DestroyImmediate(GameObject.Find("TUI Probe"));
                    }
                    isCameraControllerActive = newCameraControllerStatus;
                    EditorSceneManager.MarkAllScenesDirty();
                }


                // NEEDLE SELECTION - Dropdown for different needle types
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Needle");
                newNeedleIndex = EditorGUILayout.Popup(needleIndex, needleOptions, GUILayout.ExpandWidth(true));
                EditorGUILayout.EndHorizontal();

                // Handle needle selection changes (excluding custom option)
                if (newNeedleIndex != needleIndex && newNeedleIndex != 4)
                {
                    HandleNeedleSelection(newNeedleIndex);
                    needleIndex = newNeedleIndex;
                }

                // Custom needle selection (option 4 - currently disabled in needleOptions)
                if (newNeedleIndex == 4)
                {
                    HandleCustomSelection(typeof(INeedle), ref needle, ref newNeedle, "Needle", 3);
                    needleIndex = newNeedleIndex;
                }



                // ULTRASOUND PROBE 
                GUILayout.Label("------Ultrasound------", EditorStyles.miniBoldLabel);

                // Note: This assumes only one ultrasound probe and one ultrasound image.
                // If you need a second image (like a biplane probe) with both images running simultaneously, 
                // refer to the documentation; there are manual steps required.
                newUltrasoundStatus = EditorGUILayout.Toggle("Ultrasound Probe", newUltrasoundStatus);
                isUltrasoundActive = GameObject.Find("Ultrasound and Probe");


                if (newUltrasoundStatus != isUltrasoundActive)
                {
                    if (newUltrasoundStatus)
                    {
                        // Create ultrasound
                        // User checked - create ultrasound and assign to ATC TrackedObject2
                        // Note: We assign the child object (the actual probe) not the parent container
                        ultrasound = PrefabUtility.InstantiatePrefab(Resources.Load<GameObject>("Ultrasound and Probe"));
                        instanceIDs.Add(ultrasound.GetInstanceID());
                        communicator.GetComponent<ATC>().TrackedObject2 = ((GameObject)ultrasound).transform.GetChild(0).gameObject;
                        if (isReplayerActive) ConfigureReplayerComponents();
                    }
                    else
                    {
                        DestroyImmediate(GameObject.Find("Ultrasound and Probe"));
                    }
                }

                // NEW: Linear / Curvilinear sub-toggles. Greyed out when no ultrasound is in the scene.
                // These behave like radio buttons: exactly one must be checked at all times.
                // Indented to match the Video Replayer / Voice Annotation pattern.
                GUI.enabled = isUltrasoundActive;

                // Determine current mode from scene state (Linear is the default when no extension is active)
                newLinearProbeStatus = !isCurvilinearProbeActive;
                newCurvilinearProbeStatus = isCurvilinearProbeActive;

                // LINEAR PROBE sub-toggle
                GUILayout.BeginHorizontal();
                GUILayout.Space(20);
                bool linearToggleResult = EditorGUILayout.Toggle("Linear Probe", newLinearProbeStatus);
                GUILayout.EndHorizontal();
                // If user just turned Linear ON (and it wasn't already), switch to Linear mode
                if (linearToggleResult && !newLinearProbeStatus)
                {
                    SetCurvilinearMode(false);
                    EditorSceneManager.MarkAllScenesDirty();
                }
                // If user tried to turn Linear OFF while it was the active mode, ignore (one must always be checked)

                // CURVILINEAR PROBE sub-toggle
                GUILayout.BeginHorizontal();
                GUILayout.Space(20);
                bool curvilinearToggleResult = EditorGUILayout.Toggle("Curvilinear Probe", newCurvilinearProbeStatus);
                GUILayout.EndHorizontal();
                // If user just turned Curvilinear ON (and it wasn't already), switch to Curvilinear mode
                if (curvilinearToggleResult && !newCurvilinearProbeStatus)
                {
                    SetCurvilinearMode(true);
                    EditorSceneManager.MarkAllScenesDirty();
                }
                // If user tried to turn Curvilinear OFF while it was the active mode, ignore

                GUI.enabled = true;

                EditorSceneManager.MarkAllScenesDirty();


                // FLUOROSCOPY - C-Arm system (not tracked)
                GUILayout.Label("------Fluoroscopy on Second Display------", EditorStyles.miniBoldLabel);

                newFluoroscopyStatus = EditorGUILayout.Toggle("Fluoroscopy C-Arm", isFluoroscopyActive);
                if (newFluoroscopyStatus != isFluoroscopyActive)
                {
                    if (newFluoroscopyStatus)
                    {
                        // Create fluoroscopy - NO ATC connection since it's not tracked
                        fluoroscopy = PrefabUtility.InstantiatePrefab(Resources.Load<GameObject>("Fluoroscopy and C-Arm"));
                        instanceIDs.Add(fluoroscopy.GetInstanceID());
                        // No ATC assignment - fluoroscopy is not tracked

                        // Show display information to user
                        // EditorUtility.DisplayDialog("Fluoroscopy Display Info",
                        //    "The C-Arm UX is on Display 2. \n\nTo use Display 1, uncheck the UseSecondDisplay option in the 'Fluoroscopy and C-Arm' gameobject.",
                        //    "OK");

                        Debug.Log("The C-Arm UX is on Display 2. To use Display 1, uncheck the UseSecondDisplay option in the 'Fluoroscopy and C-Arm' gameobject.");

                    }
                    else
                    {
                        // Remove fluoroscopy
                        DestroyImmediate(GameObject.Find("Fluoroscopy and C-Arm"));
                    }
                    isFluoroscopyActive = newFluoroscopyStatus;
                    EditorSceneManager.MarkAllScenesDirty();
                }

                // ADDITIONAL FEATURES
                GUILayout.Label("------Speech Input------", EditorStyles.miniBoldLabel);

                // KEYWORD RECOGNIZER - Independent feature
                newKeywordRecognizerStatus = EditorGUILayout.Toggle("Keyword Recognizer", isKeywordRecognizerActive);
                if (newKeywordRecognizerStatus != isKeywordRecognizerActive)
                {
                    if (newKeywordRecognizerStatus)
                    {
                        keywordRecognizer = PrefabUtility.InstantiatePrefab(Resources.Load<GameObject>("Keyword Recognizer"));
                        instanceIDs.Add(keywordRecognizer.GetInstanceID());
                    }
                    else
                    {
                        DestroyAllOfType(typeof(IKeywordRecognizer));
                    }
                    isKeywordRecognizerActive = newKeywordRecognizerStatus;
                    EditorSceneManager.MarkAllScenesDirty();
                }

                // REPLAYER
                GUILayout.Label("------Recording and Replaying Sessions------", EditorStyles.miniBoldLabel);
                newReplayerStatus = EditorGUILayout.Toggle("Replayer & Recorder", isReplayerActive);
                if (newReplayerStatus != isReplayerActive)
                {
                    if (newReplayerStatus)
                    {
                        // Create replayer and session recorder GO
                        replayer = PrefabUtility.InstantiatePrefab(Resources.Load<GameObject>("Replayer and Session Saver"));
                        instanceIDs.Add(replayer.GetInstanceID());
                        // Configure components for replay
                        ConfigureReplayerComponents();
                    }
                    else
                    {
                        // Remove dependent features first
                        if (isVideoReplayerActive)
                        {
                            DestroyAllOfType(typeof(IVideoReplayer));
                            isVideoReplayerActive = false;
                        }
                        if (isVoiceAnnotationActive)
                        {
                            DestroyAllOfType(typeof(IVoiceAnnotation));
                            isVoiceAnnotationActive = false;
                        }

                        // Remove replayer components from other objects
                        RemoveReplayerComponents();
                        // Remove replayer
                        DestroyImmediate(GameObject.Find("Replayer and Session Saver"));
                    }
                    isReplayerActive = newReplayerStatus;
                    EditorSceneManager.MarkAllScenesDirty();
                }

                // VIDEO REPLAYER - Requires Replayer and Session Saver (indented under Replayer)
                GUI.enabled = isReplayerActive;
                GUILayout.BeginHorizontal();
                GUILayout.Space(20); // Indent 20 pixels
                newVideoReplayerStatus = EditorGUILayout.Toggle("Video Replayer - Webcam or Camera", isVideoReplayerActive);
                GUILayout.EndHorizontal();
                if (newVideoReplayerStatus != isVideoReplayerActive)
                {
                    if (newVideoReplayerStatus)
                    {
                        videoReplayer = PrefabUtility.InstantiatePrefab(Resources.Load<GameObject>("Video Replayer - Webcam or Camera"));
                        instanceIDs.Add(videoReplayer.GetInstanceID());
                    }
                    else
                    {
                        DestroyAllOfType(typeof(IVideoReplayer));
                    }
                    isVideoReplayerActive = newVideoReplayerStatus;
                    EditorSceneManager.MarkAllScenesDirty();
                }

                // VOICE ANNOTATION - Requires both Keyword Recognizer AND Replayer
                GUI.enabled = isReplayerActive && isKeywordRecognizerActive;
                GUILayout.BeginHorizontal();
                GUILayout.Space(20); // Indent 20 pixels
                newVoiceAnnotationStatus = EditorGUILayout.Toggle("Voice Annotation", isVoiceAnnotationActive);
                GUILayout.EndHorizontal();
                if (newVoiceAnnotationStatus != isVoiceAnnotationActive)
                {
                    if (newVoiceAnnotationStatus)
                    {
                        voiceAnnotation = PrefabUtility.InstantiatePrefab(Resources.Load<GameObject>("Voice Annotation"));
                        instanceIDs.Add(voiceAnnotation.GetInstanceID());
                    }
                    else
                    {
                        DestroyAllOfType(typeof(IVoiceAnnotation));
                    }
                    isVoiceAnnotationActive = newVoiceAnnotationStatus;
                    EditorSceneManager.MarkAllScenesDirty();
                }



                GUI.enabled = true;


            }

            GUILayout.EndVertical();
        }
        #endregion

        #region Replayer and Session Recorder
        private void ConfigureReplayerComponents()
        {
            ConfigureUltrasoundForReplay();
            ConfigureNeedleForReplay();
            ConfigureAnatomyForReplay();
            ConfigureMicrocontrollerForReplay();
            ConfigureFluoroscopyForReplay();
        }

        #region Auto-Configurator (Self-Clicking Checkboxes)
        // Auto-check methods that simulate the checkbox logic

        private void AutoPopulateUpdate()
        {
            if (!isAutoPopulating) return;

            float elapsed = (float)EditorApplication.timeSinceStartup - autoPopulateStartTime;

            if (elapsed > 0.5f && autoPopulateStep == 0)
            {
                AutoCheckIndexPlate();
                autoPopulateStep = 1;
                Repaint();
            }
            else if (elapsed > 1.0f && autoPopulateStep == 1)
            {
                AutoCheckCameraController();
                autoPopulateStep = 2;
                Repaint();
            }
            else if (elapsed > 1.5f && autoPopulateStep == 2)
            {
                AutoCheckBasicNeedle();
                autoPopulateStep = 3;
                Repaint();
            }
            else if (elapsed > 2.0f && autoPopulateStep == 3)
            {
                AutoCheckUltrasound();
                autoPopulateStep = 4;
                Repaint();
            }
            else if (elapsed > 2.5f && autoPopulateStep == 4)
            {
                AutoCheckFluoroscopy();
                autoPopulateStep = 5;
                Repaint();
            }
            else if (elapsed > 3.0f && autoPopulateStep == 5)
            {
                AutoCheckReplayer();
                autoPopulateStep = 6;
                Repaint();
            }

            if (autoPopulateStep >= 6)
            {
                isAutoPopulating = false;
                EditorApplication.update -= AutoPopulateUpdate;
                Debug.Log("Auto-populate sequence completed!");
            }

            Debug.Log($"Your Subscribed AutoPopulateUpdate is running!");
        }









        private void AutoCheckIndexPlate()
        {
            newindexPlateStatus = true;
            // Trigger the same logic as manual checkbox
            indexPlate = PrefabUtility.InstantiatePrefab(Resources.Load<GameObject>("IndexPlate"));
            instanceIDs.Add(indexPlate.GetInstanceID());
            communicator.GetComponent<ATC>().BaseObject = (GameObject)indexPlate;
            isIndexPlateActive = true; 
            ReadPrefsFromScene();

            // Ensure EventSystem exists for UI interaction
            EnsureEventSystem();

        }

        private void AutoCheckCameraController()
        {
            newCameraControllerStatus = true;
            cameraController = PrefabUtility.InstantiatePrefab(Resources.Load<GameObject>("TUI Probe"));
            instanceIDs.Add(cameraController.GetInstanceID());
            communicator.GetComponent<ATC>().TrackedObject1 = (GameObject)cameraController;
            isCameraControllerActive = true; 
            ReadPrefsFromScene(); 
        }

        private void AutoCheckBasicNeedle()
        {
            // Set needle to Basic Needle (index 1)
            needleIndex = 1;
            newNeedleIndex = 1;
            HandleNeedleSelection(1);
            isNeedleActive = true; 
            ReadPrefsFromScene(); 
        }

        private void AutoCheckUltrasound()
        {
            newUltrasoundStatus = true;
            ultrasound = PrefabUtility.InstantiatePrefab(Resources.Load<GameObject>("Ultrasound and Probe"));
            instanceIDs.Add(ultrasound.GetInstanceID());
            communicator.GetComponent<ATC>().TrackedObject2 = ((GameObject)ultrasound).transform.GetChild(0).gameObject;
            isUltrasoundActive = true;
            ReadPrefsFromScene(); 
        }

        private void AutoCheckFluoroscopy()
        {
            newFluoroscopyStatus = true;
            fluoroscopy = PrefabUtility.InstantiatePrefab(Resources.Load<GameObject>("Fluoroscopy and C-Arm"));
            instanceIDs.Add(fluoroscopy.GetInstanceID());
            isFluoroscopyActive = true; 
            Debug.Log("The C-Arm UX is on Display 2. To use Display 1, uncheck the UseSecondDisplay option in the 'Fluoroscopy and C-Arm' gameobject.");
            ReadPrefsFromScene(); 
        }

        private void AutoCheckReplayer()
        {
            newReplayerStatus = true;
            replayer = PrefabUtility.InstantiatePrefab(Resources.Load<GameObject>("Replayer and Session Saver"));
            instanceIDs.Add(replayer.GetInstanceID());
            ConfigureReplayerComponents();
            isReplayerActive = true; 
            ReadPrefsFromScene(); 
        }
        #endregion




        private void AddComponentSafely<T>(GameObject target, string componentName) where T : Component
        {
            if (target.GetComponent<T>() == null)
            {
                target.AddComponent<T>();
                Debug.Log($"Added {componentName} to {target.name}");
            }
            else
            {
                Debug.Log($"{componentName} already exists on {target.name}");
            }
        }

        private void ConfigureUltrasoundForReplay()
        {
            GameObject ultrasoundProbe = GameObject.Find("Tracked Ultrasound Probe");
            if (ultrasoundProbe != null)
            {
                AddComponentSafely<TrackedObjectRecorder>(ultrasoundProbe, "TrackedObjectRecorder");
                AddComponentSafely<TrackedObjectReplayer>(ultrasoundProbe, "TrackedObjectReplayer");
            }
            else
            {
                Debug.LogWarning("Tracked Ultrasound Probe not found - skipping ultrasound replay configuration");
            }
        }

        private void ConfigureNeedleForReplay()
        {
            // Find any object with INeedle interface - robust approach
            foreach (Type t in InterfaceHelper.GetTypesInheritedFromInterface(typeof(INeedle)))
            {
                UnityEngine.Object needleComponent = FindFirstObjectByType(t);
                if (needleComponent != null)
                {
                    GameObject needle = ((MonoBehaviour)needleComponent).gameObject;
                    AddComponentSafely<TrackedObjectRecorder>(needle, "TrackedObjectRecorder");
                    AddComponentSafely<TrackedObjectReplayer>(needle, "TrackedObjectReplayer");
                    return; // Found and configured one needle, we're done
                }
            }

            Debug.LogWarning("No needle found - skipping needle replay configuration");
        }

        private void ConfigureAnatomyForReplay()
        {
            GameObject indexPlate = GameObject.Find("IndexPlate");
            if (indexPlate != null)
            {
                Transform anatomyTransform = indexPlate.transform.Find("Anatomy");
                if (anatomyTransform != null)
                {
                    GameObject anatomy = anatomyTransform.gameObject;
                    AddComponentSafely<TrackedObjectRecorder>(anatomy, "TrackedObjectRecorder");
                    AddComponentSafely<TrackedObjectReplayer>(anatomy, "TrackedObjectReplayer");

                    // Set RecordEveryFrame to false for anatomy
                    TrackedObjectRecorder recorder = anatomy.GetComponent<TrackedObjectRecorder>();
                    if (recorder != null)
                    {
                        recorder.RecordEveryFrame = false;
                        Debug.Log("Set Anatomy RecordEveryFrame = false");
                    }
                }
                else
                {
                    Debug.LogWarning("Anatomy not found under IndexPlate - skipping anatomy replay configuration");
                }
            }
            else
            {
                Debug.LogWarning("IndexPlate not found - skipping anatomy replay configuration");
            }
        }

        private void ConfigureMicrocontrollerForReplay()
        {
            GameObject whitebox = GameObject.Find("WhiteboxCommunicator");
            if (whitebox != null)
            {
                AddComponentSafely<Microcontroller_Manager>(whitebox, "Microcontroller_Manager");
                AddComponentSafely<MicrocontrollerRecorder>(whitebox, "MicrocontrollerRecorder");
                AddComponentSafely<MicrocontrollerReplayer>(whitebox, "MicrocontrollerReplayer");
            }
            else
            {
                Debug.LogWarning("WhiteboxCommunicator not found - skipping microcontroller replay configuration");
            }
        }

        private void ConfigureFluoroscopyForReplay()
        {
            string[] targetNames = {
        "Fluoroscopy C-Arm DOFs Base",
        "Fluoroscopy C-Arm DOF Wig Wag Axis",
        "Fluoroscopy C-Arm DOF Up-Down Column",
        "Fluoroscopy C-Arm DOF In-Out Slide",
        "Fluoroscopy C-Arm DOF Tilt Axis",
        "Fluoroscopy C-Arm DOF Oblique-Rotate Axis"
        };

            foreach (string targetName in targetNames)
            {
                GameObject target = GameObject.Find(targetName);
                if (target != null)
                {
                    AddComponentSafely<TrackedObjectRecorder>(target, "TrackedObjectRecorder");
                    AddComponentSafely<TrackedObjectReplayer>(target, "TrackedObjectReplayer");
                }
                else
                {
                    Debug.LogWarning($"Could not find '{targetName}' GameObject");
                }
            }
        }

        /// <summary>
        /// Recursively search for a child GameObject by name
        /// </summary>
        private Transform FindChildRecursive(Transform parent, string childName)
        {
            foreach (Transform child in parent)
            {
                if (child.name == childName)
                    return child;

                Transform found = FindChildRecursive(child, childName);
                if (found != null)
                    return found;
            }
            return null;
        }

        private void RemoveReplayerComponents()
        {
            RemoveReplayComponentsFromObject("Tracked Ultrasound Probe");
            RemoveReplayComponentsFromNeedle();
            RemoveReplayComponentsFromAnatomy();
            RemoveReplayComponentsFromMicrocontroller();
            RemoveReplayComponentsFromObject("Fluoroscopy C-Arm DOFs Base");
            RemoveReplayComponentsFromObject("Fluoroscopy C-Arm DOF Wig Wag Axis");
            RemoveReplayComponentsFromObject("Fluoroscopy C-Arm DOF Up-Down Column");
            RemoveReplayComponentsFromObject("Fluoroscopy C-Arm DOF In-Out Slide");
            RemoveReplayComponentsFromObject("Fluoroscopy C-Arm DOF Tilt Axis");
            RemoveReplayComponentsFromObject("Fluoroscopy C-Arm DOF Oblique-Rotate Axis");
        }

        private void RemoveComponentSafely<T>(GameObject target, string componentName) where T : Component
        {
            T component = target.GetComponent<T>();
            if (component != null)
            {
                DestroyImmediate(component);
                Debug.Log($"Removed {componentName} from {target.name}");
            }
        }

        private void RemoveReplayComponentsFromObject(string objectName)
        {
            GameObject obj = GameObject.Find(objectName);
            if (obj != null)
            {
                RemoveComponentSafely<TrackedObjectRecorder>(obj, "TrackedObjectRecorder");
                RemoveComponentSafely<TrackedObjectReplayer>(obj, "TrackedObjectReplayer");
            }
        }

        private void RemoveReplayComponentsFromNeedle()
        {
            foreach (Type t in InterfaceHelper.GetTypesInheritedFromInterface(typeof(INeedle)))
            {
                UnityEngine.Object needleComponent = FindFirstObjectByType(t);
                if (needleComponent != null)
                {
                    GameObject needle = ((MonoBehaviour)needleComponent).gameObject;
                    RemoveComponentSafely<TrackedObjectRecorder>(needle, "TrackedObjectRecorder");
                    RemoveComponentSafely<TrackedObjectReplayer>(needle, "TrackedObjectReplayer");
                    return;
                }
            }
        }

        private void RemoveReplayComponentsFromAnatomy()
        {
            GameObject indexPlate = GameObject.Find("IndexPlate");
            if (indexPlate != null)
            {
                Transform anatomyTransform = indexPlate.transform.Find("Anatomy");
                if (anatomyTransform != null)
                {
                    GameObject anatomy = anatomyTransform.gameObject;
                    RemoveComponentSafely<TrackedObjectRecorder>(anatomy, "TrackedObjectRecorder");
                    RemoveComponentSafely<TrackedObjectReplayer>(anatomy, "TrackedObjectReplayer");
                }
            }
        }

        private void RemoveReplayComponentsFromMicrocontroller()
        {
            GameObject whitebox = GameObject.Find("WhiteboxCommunicator");
            if (whitebox != null)
            {
                RemoveComponentSafely<MicrocontrollerRecorder>(whitebox, "MicrocontrollerRecorder");
                RemoveComponentSafely<MicrocontrollerReplayer>(whitebox, "MicrocontrollerReplayer");
            }
        }
        #endregion

        #region Ultrasound Probe Mode (Linear / Curvilinear)
        /// <summary>
        /// NEW: Switches the ultrasound between Linear and Curvilinear probe modes.
        /// - Toggles the "Curvilinear Extension" GameObject's active state (the wider/curved model that fits over the linear probe tip).
        /// - Updates the three relevant fields (insonationPlaneTopWidth_mm, scanDepth_mm, angle) on BOTH UltrasoundManager instances
        ///   (primary and secondary planes for biplane), using preset values defined at the top of this class.
        /// - Uses SerializedObject so edits are properly serialized into the scene file and survive domain reloads / undo.
        /// </summary>
        /// <param name="curvilinear">true = Curvilinear mode, false = Linear mode</param>
        private void SetCurvilinearMode(bool curvilinear)
        {
            // Toggle the Curvilinear Extension GameObject under the ultrasound hierarchy
            GameObject ultrasoundRoot = GameObject.Find("Ultrasound and Probe");
            if (ultrasoundRoot != null)
            {
                Transform extensionTransform = FindChildRecursive(ultrasoundRoot.transform, CurvilinearExtensionName);
                if (extensionTransform != null)
                {
                    extensionTransform.gameObject.SetActive(curvilinear);
                }
                else
                {
                    Debug.LogWarning($"Could not find '{CurvilinearExtensionName}' under 'Ultrasound and Probe' - extension toggle skipped");
                }
            }
            else
            {
                Debug.LogWarning("'Ultrasound and Probe' not found - cannot switch probe mode");
                return;
            }

            // Update both UltrasoundManagers (primary and secondary planes for biplane)
            UltrasoundManager[] managers = FindObjectsByType<UltrasoundManager>(FindObjectsSortMode.None);
            foreach (UltrasoundManager manager in managers)
            {
                SerializedObject so = new SerializedObject(manager);
                SerializedProperty planeProp = so.FindProperty("plane");
                UltrasoundManager.Plane planeValue = (UltrasoundManager.Plane)planeProp.enumValueIndex;

                (int width, int depth, int angle) preset = GetUltrasoundPreset(curvilinear, planeValue);

                so.FindProperty("insonationPlaneTopWidth_mm").intValue = preset.width;
                so.FindProperty("scanDepth_mm").intValue = preset.depth;
                so.FindProperty("angle").intValue = preset.angle;

                // Mirror the runtime setter behavior: flag for rebuild so the scanning plane regenerates on next Play
                so.FindProperty("rebuild").boolValue = true;

                so.ApplyModifiedProperties();
            }

            // Update tracking flag so ReadPrefsFromScene and the UI stay in sync
            isCurvilinearProbeActive = curvilinear;

            Debug.Log($"Ultrasound probe mode set to {(curvilinear ? "Curvilinear" : "Linear")} on {managers.Length} UltrasoundManager(s)");
        }

        /// <summary>
        /// NEW: Returns the (width, depth, angle) preset for a given mode and plane.
        /// </summary>
        private static (int width, int depth, int angle) GetUltrasoundPreset(bool curvilinear, UltrasoundManager.Plane plane)
        {
            if (curvilinear)
                return plane == UltrasoundManager.Plane.Primary ? CurvilinearPrimaryPreset : CurvilinearSecondaryPreset;
            else
                return plane == UltrasoundManager.Plane.Primary ? LinearPrimaryPreset : LinearSecondaryPreset;
        }
        #endregion

        #region Object Management
        /// <summary>
        /// Destroys all GameObjects in the scene that implement the specified interface type
        /// Only destroys objects that were created by this tool (tracked in instanceIDs)
        /// </summary>
        private void DestroyAllOfType(Type type)
        {
            // Find all types that inherit from the specified interface
            foreach (Type t in InterfaceHelper.GetTypesInheritedFromInterface(type))
            {
                // Find all objects of each type in the scene
                foreach (UnityEngine.Object o in FindObjectsByType(t, FindObjectsSortMode.None))
                {
                    MonoBehaviour current = (MonoBehaviour)o;
                    // Only destroy objects we created (prevents destroying user's existing objects)
                    if (instanceIDs.Contains(current.gameObject.GetInstanceID()))
                    {
                        instanceIDs.Remove(current.gameObject.GetInstanceID());
                        DestroyImmediate(current.gameObject);
                    }
                }
            }
        }

        /// <summary>
        /// Checks if any GameObjects implementing the specified interface exist in the scene
        /// </summary>
        private static bool CheckIfAnyExists(Type type)
        {
            foreach (Type t in InterfaceHelper.GetTypesInheritedFromInterface(type))
            {
                if (FindFirstObjectByType(t) != null)
                {
                    return true;
                }
            }
            return false;
        }
        #endregion

        #region Scene State Management
        /// <summary>
        /// Reads the current scene state to determine what objects exist
        /// Updates UI toggle states to match reality
        /// </summary>
        private static void ReadPrefsFromScene()
        {
            // Check if communicator exists and update setup state
            if (communicator == null)
            {
                if (GameObject.Find("WhiteboxCommunicator") != null)
                {
                    communicator = GameObject.Find("WhiteboxCommunicator");
                    isBasicSceneSetUp = true;
                }
                else
                    isBasicSceneSetUp = false;
            }

            // Update component existence flags based on scene contents
            isIndexPlateActive = CheckIfAnyExists(typeof(IBasePlate));
            isCameraControllerActive = CheckIfAnyExists(typeof(ITUIProbe));
            isFluoroscopyActive = CheckIfAnyExists(typeof(IFluoroscopy));
            isUltrasoundActive = CheckIfAnyExists(typeof(IUltrasound));
            isReplayerActive = CheckIfAnyExists(typeof(IReplayer));

            // Check for additional features
            isKeywordRecognizerActive = CheckIfAnyExists(typeof(IKeywordRecognizer));
            isVideoReplayerActive = CheckIfAnyExists(typeof(IVideoReplayer));
            isVoiceAnnotationActive = CheckIfAnyExists(typeof(IVoiceAnnotation));

            isNeedleActive = CheckIfAnyExists(typeof(INeedle));
            // Reset needle dropdown to "None" if no needle exists
            if (!isNeedleActive)
                needleIndex = 0;
            // Debug.LogWarning("Set needle to " + isNeedleActive);

            // NEW: Determine current ultrasound probe mode by checking if "Curvilinear Extension" is active.
            // Linear is the default state (extension inactive); Curvilinear means the extension is active.
            isCurvilinearProbeActive = false;
            GameObject ultrasoundRoot = GameObject.Find("Ultrasound and Probe");
            if (ultrasoundRoot != null)
            {
                Transform extensionTransform = FindChildRecursiveStatic(ultrasoundRoot.transform, CurvilinearExtensionName);
                if (extensionTransform != null)
                {
                    isCurvilinearProbeActive = extensionTransform.gameObject.activeSelf;
                }
            }
        }

        // NEW: Static version of FindChildRecursive for use in the static ReadPrefsFromScene().
        // The existing instance method FindChildRecursive can't be called from a static context.
        private static Transform FindChildRecursiveStatic(Transform parent, string childName)
        {
            foreach (Transform child in parent)
            {
                if (child.name == childName)
                    return child;

                Transform found = FindChildRecursiveStatic(child, childName);
                if (found != null)
                    return found;
            }
            return null;
        }
        #endregion

        #region Custom Object Selection
        /// <summary>
        /// Handles custom object selection for components (currently used for custom needles)
        /// Validates that selected objects implement required interfaces
        /// </summary>
        private void HandleCustomSelection(Type type, ref Object obj, ref Object newObj, String objTypeName, int atcTrackobjNum)
        {
            // Show object field for user selection
            newObj = EditorGUILayout.ObjectField(obj, typeof(GameObject), true);

            if (newObj != null)
            {
                // Handle replacement of existing objects
                if (CheckIfAnyExists(type))
                {
                    if (newObj != obj)
                    {
                        obj = newObj;
                        if (((GameObject)newObj).GetComponent(type) != null)
                        {
                            warningMessage = "Scene contains " + objTypeName + "!";
                            DestroyAllOfType(type); // Remove existing objects

                            try
                            {
                                // Try to instantiate as prefab
                                newObj = PrefabUtility.InstantiatePrefab(newObj);
                            }
                            catch (UnityException e)
                            {
                                Debug.Log(e);
                            }

                            // Set up tracking reference
                            if (newObj == null)
                            {
                                atcTrackedObj = (GameObject)obj;
                            }
                            else
                            {
                                instanceIDs.Add(newObj.GetInstanceID());
                                atcTrackedObj = (GameObject)newObj;
                            }
                        }
                        else
                        {
                            warningMessage = "Selected Object is not of type " + type.ToString() + "!";
                        }
                    }
                }
                // Handle creation of new objects
                else
                {
                    if (newObj != obj)
                    {
                        obj = newObj;
                        if (((GameObject)newObj).GetComponent(type) == null)
                        {
                            warningMessage = "Selected Object is not of type " + type.ToString() + "!";
                        }
                        else
                        {
                            // Valid object - instantiate and track
                            newObj = PrefabUtility.InstantiatePrefab(newObj);
                            instanceIDs.Add(newObj.GetInstanceID());
                            atcTrackedObj = (GameObject)newObj;
                        }
                    }
                }

                // Assign to appropriate ATC tracked object slot
                assignTrackObj(atcTrackobjNum);
            }

            // Show validation warnings to user
            if (!String.IsNullOrEmpty(warningMessage))
                EditorGUILayout.HelpBox(warningMessage, MessageType.Warning);
        }

        /// <summary>
        /// Assigns a GameObject to the specified ATC tracked object slot
        /// </summary>
        private void assignTrackObj(int num)
        {
            switch (num)
            {
                case 1:
                    communicator.GetComponent<ATC>().TrackedObject1 = atcTrackedObj;
                    break;
                case 2:
                    communicator.GetComponent<ATC>().TrackedObject2 = atcTrackedObj;
                    break;
                case 3:
                    communicator.GetComponent<ATC>().TrackedObject3 = atcTrackedObj;
                    break;
                case 4:
                    communicator.GetComponent<ATC>().TrackedObject4 = atcTrackedObj;
                    break;
                default:
                    break;
            }
        }
        #endregion

        #region Needle Handling
        /// <summary>
        /// Handles needle selection from the dropdown menu
        /// Each needle type has specific prefab and gets assigned to TrackedObject3
        /// </summary>
        private void HandleNeedleSelection(int index)
        {
            EditorSceneManager.MarkAllScenesDirty();

            switch (index)
            {
                case 0: // None
                    DestroyAllOfType(typeof(INeedle));
                    break;

                case 1: // Basic Needle
                    DestroyAllOfType(typeof(INeedle));
                    needle = PrefabUtility.InstantiatePrefab(Resources.Load<GameObject>("Basic Needle"));
                    instanceIDs.Add(needle.GetInstanceID());
                    communicator.GetComponent<ATC>().TrackedObject3 = (GameObject)needle;
                    if (isReplayerActive) ConfigureReplayerComponents();
                    break;

                case 2: // CVA Needle
                    DestroyAllOfType(typeof(INeedle));
                    needle = PrefabUtility.InstantiatePrefab(Resources.Load<GameObject>("CVA Needle and Syringe"));
                    instanceIDs.Add(needle.GetInstanceID());
                    communicator.GetComponent<ATC>().TrackedObject3 = (GameObject)needle;
                    if (isReplayerActive) ConfigureReplayerComponents();
                    break;

                case 3: // RA Needle
                    DestroyAllOfType(typeof(INeedle));
                    needle = PrefabUtility.InstantiatePrefab(Resources.Load<GameObject>("RA Tuohy Needle"));
                    instanceIDs.Add(needle.GetInstanceID());
                    communicator.GetComponent<ATC>().TrackedObject3 = (GameObject)needle;
                    if (isReplayerActive) ConfigureReplayerComponents();
                    break;
            }
        }
        #endregion

        #region Project Configuration
        /// <summary>
        /// Sets up standard SMMARTS layer names in the project
        /// These layers are used by prefabs for rendering order and interaction logic
        /// Shows conflict dialog if layers are already in use
        /// </summary>
        private static void SetupProjectLayers()
        {
            SerializedObject tagManager = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")[0]);
            SerializedProperty layersProp = tagManager.FindProperty("layers");

            // Define the standard SMMARTS layer configuration
            var requiredLayers = new Dictionary<int, string>
            {
                {9, "Skin"},                // Anatomical skin meshes
                // Anatomical meshes for everything but skin is default unless otherwise stated.
                {19, "Needle"},             // Needle objects for collision detection  
                {20, "Ultrasound Image"},   // Ultrasound visualization textures
                {21, "Ultrasound Display"}, // UI elements for ultrasound display
                {22, "Fluoroscopy Image"},  // The voxelized volume created for fluoroscopy and the render texture
                {23, "Fluoroscopy Display"}, // UI elements for fluoroscopy display, including the C-Arm 3D model
                {24, "US and Fluoroscopy Display"} // objects you want visible in both US and fluoroscopy display - like bones
            };

            // Check for existing layer name conflicts
            List<string> conflicts = new List<string>();
            foreach (var kvp in requiredLayers)
            {
                SerializedProperty layerProp = layersProp.GetArrayElementAtIndex(kvp.Key);
                string currentLayerName = layerProp.stringValue;
                if (!string.IsNullOrEmpty(currentLayerName) && currentLayerName != kvp.Value)
                {
                    conflicts.Add($"Layer {kvp.Key}: '{currentLayerName}' -> '{kvp.Value}'");
                }
            }

            // Show warning dialog if conflicts exist, allow user to proceed or cancel
            if (conflicts.Count > 0)
            {
                string conflictMessage = "The following layers will be overwritten:\n" + string.Join("\n", conflicts);
                if (!EditorUtility.DisplayDialog("Layer Conflicts", conflictMessage, "Proceed", "Cancel"))
                {
                    return;
                }
            }

            // Apply the layer names
            foreach (var kvp in requiredLayers)
            {
                SerializedProperty layerProp = layersProp.GetArrayElementAtIndex(kvp.Key);
                layerProp.stringValue = kvp.Value;
            }

            tagManager.ApplyModifiedProperties();
            Debug.Log("SMMARTS layers configured successfully");
        }


        /// <summary>
        /// Sets up the main camera's culling mask to show appropriate layers for general viewing
        /// Includes: Default, US and Fluoroscopy Display, Needle
        /// Excludes: Skin, Ultrasound Image/Display, Fluoroscopy Image/Display
        /// </summary>
        private static void SetupMainCameraCullingMask()
        {
            Camera mainCamera = Camera.main;
            if (mainCamera == null)
            {
                // Try to find any camera tagged as "MainCamera"
                GameObject mainCameraGO = GameObject.FindWithTag("MainCamera");
                if (mainCameraGO != null)
                    mainCamera = mainCameraGO.GetComponent<Camera>();
            }

            if (mainCamera != null)
            {
                // Start with nothing
                int cullingMask = 0;

                // Include these layers
                cullingMask |= (1 << 0);  // Default layer
                cullingMask |= (1 << 19); // Needle
                cullingMask |= (1 << 24); // US and Fluoroscopy Display

                // Apply the culling mask
                mainCamera.cullingMask = cullingMask;

                Debug.Log("Main camera culling mask configured for general viewing");
            }
            else
            {
                Debug.LogWarning("Main camera not found - culling mask not set");
            }
        }

        /// <summary>
        /// Moves main camera to default SMMARTS position if it's still at origin
        /// Only moves camera if it hasn't been positioned yet (at 0,0,0)
        /// </summary>
        private static void SetupMainCameraPosition()
        {
            Camera mainCamera = Camera.main;
            if (mainCamera == null)
            {
                // Try to find any camera tagged as "MainCamera"
                GameObject mainCameraGO = GameObject.FindWithTag("MainCamera");
                if (mainCameraGO != null)
                    mainCamera = mainCameraGO.GetComponent<Camera>();
            }

            if (mainCamera != null)
            {
                // Only move if camera is still at Unity's default for a new scene (not origin, but hasn't been positioned yet)
                if (mainCamera.transform.position == new Vector3(0, 1, -10))
                {
                    mainCamera.transform.position = new Vector3(-178f, 225f, -128f);
                    mainCamera.transform.rotation = Quaternion.Euler(24f, 39f, 0f);

                    Debug.Log("Main camera positioned to default SMMARTS location");
                }
                else
                {
                    Debug.Log("Main camera already positioned - skipping default positioning");
                }
            }
            else
            {
                Debug.LogWarning("Main camera not found - position not set");
            }
        }


        // clean up editor update loops so they don't persist in the background after you close the window.
        void OnDestroy()
        {
            // Clean up the subscription when window is closed/destroyed
            if (isAutoPopulating)
            {
                isAutoPopulating = false;
                EditorApplication.update -= AutoPopulateUpdate;
                Debug.Log("Project Populator window closed - cancelled auto-populate sequence");
            }
        }


        /// <summary>
        /// Ensures an EventSystem exists in the scene - required for UI interaction
        /// Creates one if it doesn't exist
        /// </summary>
        private static void EnsureEventSystem()
        {
            // Check if EventSystem already exists
            if (GameObject.FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>() == null)
            {
                // Instantiate the SMMARTS EventSystem prefab.
                // This is identical to the default Unity event system.
                GameObject eventSystem = Instantiate(Resources.Load<GameObject>("SMMARTS EventSystem")) as GameObject;
                eventSystem.name = eventSystem.name.Replace("(Clone)", "");
                Debug.Log("Created SMMARTS EventSystem for UI interaction");
            }
            else
            {
                Debug.Log("EventSystem already exists in scene");
            }
        }




        #endregion
    }
}