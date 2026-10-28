using UnityEngine;
using System;
#if !UNITY_WEBGL
using System.Runtime.InteropServices; // needed to get to the plugin (specifically, needed to reference the unmanaged dll we wrote that calls Ascension's API)
using System.Threading;
#endif

/// <summary>
/// The ATC is contained within the SMARTS_SDK namespace.
/// </summary>
namespace SMMARTS
{
    /// <summary>
    /// 
    /// Class Overview:
    /// The ATC Class handles communication with the 3D tracking system. It is the main conduit between the tracking system and the 
    /// software that is using it. The ATC establishes and maintains the connection with the white box's internal Ascension 3d 
    /// Guidance driveBAY. This script allows users to assign their tracked objects, toggle their tracking on/off, and replay the
    /// state of the ATC from a given recorded data line.
    /// 
    /// The ATC operates as a singleton. The single static instance of the ATC is initialized in Awake() and can be referenced by
    /// outside scripts using the ATC.ME. field. Scripts must be using the SMMARTS namespace to have access to this script.
    /// 
    /// Dependencies:
    /// ATC_Editor.
    /// 
    /// Developers:
    ///	before 2016, pre-dating SMMARTS SDK: Dave Lizdas.
    ///	2018 SMARTS-SDK: Andre Kazimierz Bigos. Useability enhancements.
    /// 2025 SMMARTS SDK MVP: Dave Lizdas. Exposed variables for recorders. Removed volume offset related to NDI firmware fix.
    /// 
    /// </summary>
    [Serializable]
    public class ATC : MonoBehaviour
    {
        [Header("-----SMMARTS SDK 2025-----", order = 0)]

        /// This static ATC is used to ensure a single instance of the ultrasound manager exists in the scene and that the single 
        /// instance is easily accessible to outside scripts. In awake we destroy all other possible instances.
        public static ATC ME;

        //[Header("----------- Base Object Locked to the Transmitter -----------------------------------------------------------")]

        /// This base object is the object moved to match the tracking system. Typically, this is the "Index Plate" object within the
        /// scene hierarchy. The base object is locked to the tracking transmitter, and therefore, anything that is a child of this
        /// GameObject is moved to the appropriate position within the world space.
        [SerializeField]
        private GameObject baseObject;
        //[SerializeField]
        public GameObject BaseObject { get { return baseObject; } set { baseObject = value; } }

        /// This toggle determines whether or not the base object is moved when the tracking is connected.
        /// Normal Operation Mode:
        /// When false, the baseObject's position is not updated based on the tracking system's information. 
        /// When true, the baseObject's position is updated if a connection to the white box has been established.
        /// Replay Operation Mode:
        /// In replay mode, this field is updated based on user inputted data and the baseObject is moved using the same principles
        /// as in normal operation mode, however, a connection to the white box is not required.
        /// This field cannot be updated remotely when the ATC is in replay mode. In replay mode this field is maintained through replay
        /// data input.
        //[SerializeField]
        private bool enableTrackingBaseObject = true;      // For replaying and resetting other transmitters and co-ordinate systems  ///YES
        public bool EnableTrackingBaseObject
        {
            get
            {
                return enableTrackingBaseObject;
            }
            set
            {
                if (currentInputMode == InputMode.ATC)
                    enableATCTrackingBaseObject = value;
                else if (currentInputMode == InputMode.Replay_Data)
                {
                    throw new UnauthorizedAccessException("When CurrentInputMode is set to \"Replay_Data\" the ATC does not allow user changes to the \"EnableTrackingBaseObject\" field.\n" +
                    "This is done to ensure replayed data is properly interpreted, and the data is accurately replayed.");
                }
                enableTrackingBaseObject = value;
            }
        }

        /// When the ATC is switched to replay mode, the normal enableTrackingBaseObject's state is maintained using this field.
        /// When the ATC is switched out of replay mode, the enableTrackingBaseObject is updated to reference this value. The whole
        /// idea behind the replay functionality is that the state of objects before replay mode is turned on is recorded, and then
        /// when replay mode is toggled off, the state of the object at the time of recording is replicated. The theory behind this
        /// is that when replaying data, all other scripts referencing the ATC will be able to use the same fields as if the ATC
        /// were actually connected.
        bool enableATCTrackingBaseObject = true;

        /// This is the position of the base object recorded to the EEPROM of the tracking system. When the tracking is turned on,
        /// the baseObject is moved to this position.
        Vector3 baseObjectPosition = Vector3.zero;

        /// This is the rotation of the base object recorded to the EEPROM of the tracking system. When the tracking is turned on,
        /// the baseObject is moved to this rotation.
        Quaternion baseObjectRotation = Quaternion.identity;

        //[Header("----------- Gameobject Tracked with Sensor Plug 1 ------------------------------------------------------------")]

        /// 
        [SerializeField]
        private GameObject trackedObject1;
        public GameObject TrackedObject1 { get { return trackedObject1; } set { trackedObject1 = value; } }
        [SerializeField]
        private bool enableTrackingObject1 = true; ///YES
		public bool EnableTrackingObject1
        {
            get
            {
                return enableTrackingObject1;
            }
            set
            {
                if (currentInputMode == InputMode.ATC)
                    enableATCTrackingObject1 = value;
                else if (currentInputMode == InputMode.Replay_Data)
                {
                    throw new UnauthorizedAccessException("When CurrentInputMode is set to \"Replay_Data\" the ATC does not allow user changes to the \"EnableTrackingObject1\" field.\n" +
                    "This is done to ensure replayed data is properly interpreted, and the data is accurately replayed.");
                }
                enableTrackingObject1 = value;
            }
        }
        bool enableATCTrackingObject1 = true;


        //[Header("----------- Gameobject Tracked with Sensor Plug 2 ------------------------------------------------------------")]
        [SerializeField]
        private GameObject trackedObject2;
        public GameObject TrackedObject2 { get { return trackedObject2; } set { trackedObject2 = value; } }
        [SerializeField]
        private bool enableTrackingObject2 = true; ///YES
		public bool EnableTrackingObject2
        {
            get
            {
                return enableTrackingObject2;
            }
            set
            {
                if (currentInputMode == InputMode.ATC)
                    enableATCTrackingObject2 = value;
                else if (currentInputMode == InputMode.Replay_Data)
                {
                    throw new UnauthorizedAccessException("When CurrentInputMode is set to \"Replay_Data\" the ATC does not allow user changes to the \"EnableTrackingObject2\" field.\n" +
                    "This is done to ensure replayed data is properly interpreted, and the data is accurately replayed.");
                }
                enableTrackingObject2 = value;
            }
        }
        bool enableATCTrackingObject2 = true;


        //[Header("----------- Gameobject Tracked with Sensor Plug 3 ------------------------------------------------------------")]
        [SerializeField]
        private GameObject trackedObject3;
        public GameObject TrackedObject3 { get { return trackedObject3; } set { trackedObject3 = value; } }
        [SerializeField]
        private bool enableTrackingObject3 = true; ///YES
		public bool EnableTrackingObject3
        {
            get
            {
                return enableTrackingObject3;
            }
            set
            {
                if (currentInputMode == InputMode.ATC)
                    enableATCTrackingObject3 = value;
                else if (currentInputMode == InputMode.Replay_Data)
                {
                    throw new UnauthorizedAccessException("When CurrentInputMode is set to \"Replay_Data\" the ATC does not allow user changes to the \"EnableTrackingObject3\" field.\n" +
                    "This is done to ensure replayed data is properly interpreted, and the data is accurately replayed.");
                }
                enableTrackingObject3 = value;
            }
        }
        bool enableATCTrackingObject3 = true;
        [SerializeField]
        private GameObject trackedObject4;
        public GameObject TrackedObject4 { get { return trackedObject4; } set { trackedObject4 = value; } }
        [SerializeField]
        private bool enableTrackingObject4; ///YES
		public bool EnableTrackingObject4
        {
            get
            {
                return enableTrackingObject4;
            }
            set
            {
                if (currentInputMode == InputMode.ATC)
                    enableATCTrackingObject4 = value;
                else if (currentInputMode == InputMode.Replay_Data)
                {
                    throw new UnauthorizedAccessException("When CurrentInputMode is set to \"Replay_Data\" the ATC does not allow user changes to the \"EnableTrackingObject4\" field.\n" +
                    "This is done to ensure replayed data is properly interpreted, and the data is accurately replayed.");
                }
                enableTrackingObject4 = value;
            }
        }
        bool enableATCTrackingObject4 = true;


        //[Header("----------- ATC Connection Options ------------------------------------------------------------------")]
        [SerializeField]
        bool disableATCConnectionRoutine = false;
        public bool DisableATCConnectionRoutine { get { return disableATCConnectionRoutine; } set { disableATCConnectionRoutine = value; } }

        [SerializeField]
        InputMode currentInputMode = InputMode.ATC;
        public InputMode CurrentInputMode
        {
            get
            {
                return currentInputMode;
            }
            set
            {
                //utilityDataAccessed = false;
                if (value == InputMode.ATC)
                    ResetToATCSettings();
                currentInputMode = value;
            }
        }
        public enum InputMode
        {
            ATC,
            Replay_Data,
            //Touch_Interface, //Going to be unused placeholder
            //Standby
        }

        //[Header("----------- Replay Options ------------------------------------------------------------------")]
        [SerializeField]
        string replayDataInput = "";
        public string ReplayDataInput { get { return replayDataInput; } set { replayDataInput = value; if (currentInputMode == InputMode.Replay_Data) ParseReplayData(); } }
        [SerializeField]
        string replayFormattedATCInputString = "ATC DATA|ATCDNC";
        public string ReplayFormattedATCInputString
        {
            get
            {
                //replayDataAccessedLastFrame = true;
                //if (!utilityDataAccessed)
                //{
                //    replayFormattedATCInputString += replayFormattedATCUtilities;
                //    utilityDataAccessed = true;
                //}
                return replayFormattedATCInputString;
            }
        }
        //bool utilityDataAccessed = false;
        //bool replayDataAccessed = false;
        //bool replayDataAccessedLastFrame = false;
        //string replayFormattedATCUtilities = "|ATCNCU";
        [SerializeField]
        string replayFormattedATCUtilities = "ATC DATA|ATCU";
        public string ReplayFormattedATCUtilities { get { return replayFormattedATCUtilities; } }
        //[SerializeField]
        string replayUtilityInput = "";
        public string ReplayUtilityInput { get { return replayUtilityInput; } }// set { replayUtilityInput = value; if (currentInputMode == InputMode.Replay_Data) ParseUtilityData(); } }
        //REPLAY FORMATTED UTILITY DATA (NO NEED TO RECREATE ATM) USEFUL TO HAVE READ ACCESS, NOT 100% USEFUL TO 
        //RECREATE DURING REPLAY.


        //[Header("----------- ATC Status and Error Reports ------------------------------------------------------------------")]
        [SerializeField]
        private bool connected = false; ///YES
		bool ATCConnected = false;
        public bool Connected { get { return connected; } set { connected = value; } }
        [SerializeField]
        Connection_Status connectionStatus = Connection_Status.Not_Connected; ///YES
		public Connection_Status ConnectionStatus { get { return connectionStatus; } }
        Connection_Status ATCConnectionStatus = Connection_Status.Not_Connected;
        public enum Connection_Status { Not_Connected, Establishing_Connection, Connected };
        private string errorReport;
        int AtcErrorCode { get; set; }
        public bool PowerFailure { get; private set; } = false;

        //[Header("----------- Transmitter Settings and Reference Frame ---------------------------------------------------------")]
        [SerializeField]
        Zone hemisphere = Zone.BOTTOM; // this is an ATC property of each sensor, refers to transmitter hemisphere, we assume one for all sensors.
        Zone Hemisphere { get { return hemisphere; } set { hemisphere = value; } }
        public enum Zone { FRONT = 1, BACK, TOP, BOTTOM, LEFT, RIGHT }; // handy enum for readability
        [SerializeField]
        private Vector3 transmitterRotation; // this preset based on typical configurations of the modlular stand is applied in Start()
                                             //public Vector3 TransmitterRotation { get { return transmitterRotation; } set { transmitterRotation = value; } }
        [SerializeField]
        private bool useModularStandPresetRotation = true; // use this for different transmitter rotations
                                                           //public bool UseModularStandPresetRotation { get { return useModularStandPresetRotation; } set { useModularStandPresetRotation = value; } }



        //[Header("----------- Filter Settings (Applied to all Sensors) ---------------------------------------------------------")]
        [SerializeField]
        private bool usePresetsForSRT = true; // This will set all filter settings and the Volume Correction Factor to Dave's best result from 4/24/2015
                                              //public bool UsePresetsForSRT { get { return usePresetsForSRT; } set { usePresetsForSRT = value; } }
        [SerializeField]
        //[Range(20, 255)]
        private int samplingFrequency = 200; // this is the internal hardware setting in the Ascension box - has to do with how well the filters work
                                             //public int SamplingFrequency { get { return samplingFrequency; } set { if (value > 255 || value < 20) throw new Exception("Sampling frequency must be in the range [20,255]."); else samplingFrequency = value; } }
        [SerializeField]
        private bool applyCustomFilters; // if this is true, we upload sensor filters, all sensors get the same filters for now.
                                         //public bool ApplyCustomFilters { get { return applyCustomFilters; } set { applyCustomFilters = value; } }
        [SerializeField]
        private bool ignoreLargeChanges = false;
        [SerializeField]
        private bool ac_WideNotchFilter = false;
        [SerializeField]
        private bool ac_NarrowNotchFilter = false;
        [SerializeField]
        private bool dc_AdaptiveFilter = false; // ATC sensor filter settings
                                                //public bool IgnoreLargeChanges { get { return ignoreLargeChanges; } set { ignoreLargeChanges = value; } }
                                                //public bool AC_WideNotchFilter { get { return ac_WideNotchFilter; } set { ac_WideNotchFilter = value; } }
                                                //public bool AC_NarrowNotchFilter { get { return ac_NarrowNotchFilter; } set { ac_NarrowNotchFilter = value; } }
                                                //public bool DC_AdaptiveFilter { get { return dc_AdaptiveFilter; } set { dc_AdaptiveFilter = value; } }
        [SerializeField]
        int[] _Vm = new int[7]; // DC Adaptive filter settings
                                //public int[] VM { get { return _Vm; } set { _Vm = value; } }

        [SerializeField]
        private int alphaMin, alphaMax; // DC Alpha filter settings
                                        //public int AlphaMin { get { return alphaMin; } set { alphaMin = value; } }
                                        //public int AlphaMax { get { return alphaMax; } set { alphaMax = value; } }



        // This was needed for some of the first SRTs we used. NDI firmware updates made it unnecessary.
        // However, if we ever need it again, its here.
        [Header("----------- Tracking Volume Scale Correction Factor ----------------------------------------------------------")]
        [SerializeField]
        private Vector3 volumeCorrectionFactor = new Vector3(1, 1, 1); // (1.008f, 1.01f, .9985f);



        //[Header("----------- Edit the Sensor Alignments -----------------------------------------------------------------------")]
        [SerializeField]
        AlignSensor editSensor = AlignSensor.NONE;
        public AlignSensor EditSensor { get { return editSensor; } set { editSensor = value; } }
        public enum AlignSensor
        {
            NONE = 0,
            Sensor1 = 1,
            Sensor2 = 2,
            Sensor3 = 3,
            Sensor4 = 4
        }
        private AlignSensor LastEditedSensor = AlignSensor.NONE;
        [SerializeField]
        private Vector3 offsetPosition;
        public Vector3 OffsetPosition { get { return offsetPosition; } set { offsetPosition = value; } }
        [SerializeField]
        private Vector3 offsetRotation;
        public Vector3 OffsetRotation { get { return offsetRotation; } set { offsetRotation = value; } }
        [SerializeField]
        private Vector4 utilities = Vector4.zero;
        public Vector4 Utilities { get { return utilities; } set { utilities = value; } }
        public Vector4 Sensor1Utilities { get; private set; } = Vector4.zero;
        public Vector4 Sensor2Utilities { get; private set; } = Vector4.zero;
        public Vector4 Sensor3Utilities { get; private set; } = Vector4.zero;
        public Vector4 Sensor4Utilities { get; private set; } = Vector4.zero;



        // These are filled by reading the EEPROMS at startup and are used for real-time tuning of the offsets
        private Vector3 sensor1OffsetPosition = Vector3.zero;
        private Vector3 sensor2OffsetPosition = Vector3.zero;
        private Vector3 sensor3OffsetPosition = Vector3.zero;
        private Vector3 sensor4OffsetPosition = Vector3.zero;
        private Vector3 sensor1OffsetRotation = Vector3.zero;
        private Vector3 sensor2OffsetRotation = Vector3.zero;
        private Vector3 sensor3OffsetRotation = Vector3.zero;
        private Vector3 sensor4OffsetRotation = Vector3.zero;

        // These are filled by reading the EEPROMS at startup and are used for utility functions

        // used for noticing changes for uploading to the ATC 
        private Vector3 lastTransmitterRotation;
        private Vector3 lastSensorOffsetPosition;
        private Vector3 lastSensorOffsetRotation;


        [Header("----------- What's Currently on the EEPROMs ------------------------------------------------------------------", order = 1)]
        public string MemorySensorPlug1 { get; private set; } = ""; // Sensor memory - we write alignments on the plug ///YES
                                               //public string MemorySensorPlug1 { get { return memorySensorPlug1; } set { memorySensorPlug1 = value; } }
        public string MemorySensorPlug2 { get; private set; } = ""; // Sensor memory - we write alignments on the plug ///YES
                                               //public string MemorySensorPlug2 { get { return memorySensorPlug2; } set { memorySensorPlug2 = value; } }
        public string MemorySensorPlug3 { get; private set; } = ""; // Sensor memory - we write alignments on the plug ///YES
                                               //public string MemorySensorPlug3 { get { return memorySensorPlug3; } set { memorySensorPlug3 = value; } }
        public string MemorySensorPlug4 { get; private set; } = ""; // Sensor memory - we write alignments on the plug ///YES
                                               //public string MemorySensorPlug4 { get { return memorySensorPlug4; } set { memorySensorPlug4 = value; } }
        public string MemoryTransmitterPlug { get; private set; } = ""; // Transmitter memory - we write alignments on the plug ///YES
                                               //public string MemoryTransmitterPlug { get { return memoryTransmitterPlug; } set { memoryTransmitterPlug = value; } }
        public string MemoryTrackingUnit { get; private set; } = ""; // Board memory - we write alignments on a chip in the board ///YES
                                                //public string MemoryTrackingUnit { get { return memoryTrackingUnit; } set { memoryTrackingUnit = value; } }
                                                //private readonly bool ReadSensorEEPROMS = true; // Startup flag.  Reading EEPROMS takes extra time and must done every time the ascension tracking system is powered.  Do this every time; ATC cannot tell the difference between USB disconnected and power lost. 


        //[Header("----------- Edit the EEPROMs ---------------------------------------------------------------------------------")]
        [SerializeField]
        EEPROM uploadTarget = EEPROM.NONE;
        public EEPROM UploadTarget { get { return uploadTarget; } set { uploadTarget = value; } }
        public enum EEPROM
        {
            NONE = 0,
            Sensor1 = 1,
            Sensor2 = 2,
            Sensor3 = 3,
            Sensor4 = 4,
            Transmitter = 5,
            Board = 6
        };
        // the plugin expects this order
        [SerializeField]
        private string uploadText;
        public string UploadText { get { return uploadText; } set { uploadText = value; } }

        //[Header("----- Debugging Options -----")]
        [SerializeField]
        bool debuggingActive = true;
        public bool DebuggingActive { get { return debuggingActive; } set { debuggingActive = value; } }
        [SerializeField]
        bool errorDebuggingActive = false;
        public bool ErrorDebuggingActive { get { return errorDebuggingActive; } set { errorDebuggingActive = value; } }
#if !UNITY_WEBGL
        private Thread ATCCommunicator;
#endif
        void Awake()
        {

            if (ME != null)
                Destroy(ME);
            ME = this;

            DontDestroyOnLoad(this);

        }
        void Start()
        {
            // Debug.Log("START");
#if !UNITY_WEBGL
            if (!disableATCConnectionRoutine)
            {
                ATCCommunicator = new Thread(ATCConnectionRoutine) { Name = "ATC Thread" };
                ATCCommunicator.Start();
            }
#else
            // No ATC64 plugin and no background threads on WebGL: report the same
            // "not connected" state a desktop build reaches with no tracker attached.
            AbortConnection();
#endif
            if (useModularStandPresetRotation)
            {
                if (hemisphere == Zone.FRONT) transmitterRotation = new Vector3(0, 180F, 0);
                if (hemisphere == Zone.BOTTOM) transmitterRotation = new Vector3(0, 180F, 180F);
                if (hemisphere == Zone.BACK) transmitterRotation = new Vector3(0, 0, 0);
                if (hemisphere == Zone.TOP) transmitterRotation = new Vector3(0, 180F, 0);
            }
            enableTrackingObject1 = enableTrackingObject1 && (trackedObject1 != null);
            enableTrackingObject2 = enableTrackingObject2 && (trackedObject2 != null);
            enableTrackingObject3 = enableTrackingObject3 && (trackedObject3 != null);
            enableTrackingObject4 = enableTrackingObject4 && (trackedObject4 != null);
            // This will set all filter settings and the Volume Correction Factor to Dave's best result from 4/24/2015
            if (usePresetsForSRT)
            {
                samplingFrequency = 255;
                applyCustomFilters = true;
                ignoreLargeChanges = false;
                ac_WideNotchFilter = true;
                ac_NarrowNotchFilter = false;
                dc_AdaptiveFilter = true;
                _Vm[0] = 1;
                _Vm[1] = 1;
                _Vm[2] = 1;
                _Vm[3] = 1;
                _Vm[4] = 1;
                _Vm[5] = 10;
                _Vm[6] = 200;
                alphaMin = 50;
                alphaMax = 15000;
            }
        }
#if !UNITY_WEBGL
        void ATCConnectionRoutine()
        {
            ATCConnected = false;
            ATCConnectionStatus = Connection_Status.Establishing_Connection;
            if (currentInputMode == InputMode.ATC) { connected = ATCConnected; connectionStatus = ATCConnectionStatus; replayFormattedATCInputString = "ATC DATA|ATCDEC"; }
            AtcErrorCode = HelloATC(true);
            if (AtcErrorCode == 0)
            {
                AtcErrorCode = SetMeasurementRate(samplingFrequency);
                if (AtcErrorCode == 0) AtcErrorCode = SetHemisphere(1, (int)hemisphere);
                if (AtcErrorCode == 0) AtcErrorCode = SetHemisphere(2, (int)hemisphere);
                if (AtcErrorCode == 0) AtcErrorCode = SetHemisphere(3, (int)hemisphere);
                if (AtcErrorCode == 0) AtcErrorCode = SetHemisphere(4, (int)hemisphere);
                if (AtcErrorCode == 0) AtcErrorCode = RotateTransmitterReferenceFrame(transmitterRotation.x, transmitterRotation.y, transmitterRotation.z);
                else AbortConnection();
                if (applyCustomFilters)
                {

                    AtcErrorCode = SetSensorFilterParameters(1, ignoreLargeChanges, ac_WideNotchFilter, ac_NarrowNotchFilter, dc_AdaptiveFilter, _Vm[0], _Vm[1], _Vm[2], _Vm[3], _Vm[4], _Vm[5], _Vm[6], alphaMin, alphaMax);
                    if (AtcErrorCode == 0)
                        AtcErrorCode = SetSensorFilterParameters(2, ignoreLargeChanges, ac_WideNotchFilter, ac_NarrowNotchFilter, dc_AdaptiveFilter, _Vm[0], _Vm[1], _Vm[2], _Vm[3], _Vm[4], _Vm[5], _Vm[6], alphaMin, alphaMax);
                    if (AtcErrorCode == 0)
                        AtcErrorCode = SetSensorFilterParameters(3, ignoreLargeChanges, ac_WideNotchFilter, ac_NarrowNotchFilter, dc_AdaptiveFilter, _Vm[0], _Vm[1], _Vm[2], _Vm[3], _Vm[4], _Vm[5], _Vm[6], alphaMin, alphaMax);
                    if (AtcErrorCode == 0)
                        AtcErrorCode = SetSensorFilterParameters(4, ignoreLargeChanges, ac_WideNotchFilter, ac_NarrowNotchFilter, dc_AdaptiveFilter, _Vm[0], _Vm[1], _Vm[2], _Vm[3], _Vm[4], _Vm[5], _Vm[6], alphaMin, alphaMax);
                    else AbortConnection();
                }
                ATCConnected = true;
                ATCConnectionStatus = Connection_Status.Connected;
                if (currentInputMode == InputMode.ATC) { connected = ATCConnected; connectionStatus = ATCConnectionStatus; }
                PowerFailure = false;
                if (currentInputMode == InputMode.ATC) ReadSensorEEPROMs();
            }
            else
            {
                AbortConnection();
            }

        }
#endif
        void AbortConnection()
        {
            replayFormattedATCUtilities = "ATC DATA|ATCU";
            //replayFormattedATCUtilities = "|ATCNCU";
            replayFormattedATCInputString = "ATC DATA|ATCDNC";
            ATCConnected = false;
            PowerFailure = false;
            ATCConnectionStatus = Connection_Status.Not_Connected;
            if (currentInputMode == InputMode.ATC) { connected = ATCConnected; connectionStatus = ATCConnectionStatus; }
            LookupErrorDescription(AtcErrorCode);
            if (AtcErrorCode == -1610612685)
            {
                PowerFailure = true;
            }
#if !UNITY_WEBGL
            try { ATCCommunicator.Abort(); } catch (Exception) { }
#endif
        }
#if !UNITY_WEBGL
        void ReadSensorEEPROMs()
        {
            IntPtr echoedStringPtr1 = ReadSensor1EEPROM();
            MemorySensorPlug1 = Marshal.PtrToStringAnsi(echoedStringPtr1);
            IntPtr echoedStringPtr2 = ReadSensor2EEPROM();
            MemorySensorPlug2 = Marshal.PtrToStringAnsi(echoedStringPtr2);
            IntPtr echoedStringPtr3 = ReadSensor3EEPROM();
            MemorySensorPlug3 = Marshal.PtrToStringAnsi(echoedStringPtr3);
            IntPtr echoedStringPtr4 = ReadSensor4EEPROM();
            MemorySensorPlug4 = Marshal.PtrToStringAnsi(echoedStringPtr4);
            IntPtr echoedStringPtr5 = ReadTransmitterEEPROM();
            MemoryTransmitterPlug = Marshal.PtrToStringAnsi(echoedStringPtr5);
            IntPtr echoedStringPtr6 = ReadBoardEEPROM();
            //Debug.Log(echoedStringPtr6.ToString());
            MemoryTrackingUnit = Marshal.PtrToStringAuto(echoedStringPtr6);
            //replayFormattedATCUtilities = "|ATCCU|" + memoryTransmitterPlug + "|" + memorySensorPlug1 + "|" + memorySensorPlug2 + "|" + memorySensorPlug3 + "|" + memorySensorPlug4 + "|" + memoryTrackingUnit;
            replayFormattedATCUtilities = "ATC DATA|ATCU|" + MemoryTransmitterPlug + "|" + MemorySensorPlug1 + "|"
                + MemorySensorPlug2 + "|" + MemorySensorPlug3 + "|" + MemorySensorPlug4 + "|" + MemoryTrackingUnit;
            ParseDataFromEEPROMs();
        }
#endif
        void ParseDataFromEEPROMs()
        {
            string[] data;
            if (!string.IsNullOrEmpty(MemoryTransmitterPlug))
            {
                data = MemoryTransmitterPlug.Split(' ');
                if (data.Length > 8)
                {
                    Vector3 pos = Vector3.zero;
                    pos.x = (float)Convert.ToDouble(data[3].Trim());
                    pos.y = (float)Convert.ToDouble(data[4].Trim());
                    pos.z = (float)Convert.ToDouble(data[5].Trim());

                    Quaternion rot = Quaternion.identity;
                    rot.w = (float)Convert.ToDouble(data[6].Trim());
                    rot.x = (float)Convert.ToDouble(data[7].Trim());
                    rot.y = (float)Convert.ToDouble(data[8].Trim());
                    rot.z = (float)Convert.ToDouble(data[9].Trim());

                    baseObjectPosition = pos;
                    baseObjectRotation = rot;
                    //baseObject.transform.position = baseObjectPosition;
                    //baseObject.transform.rotation = baseObjectRotation;
                }
            }
            if (!string.IsNullOrEmpty(MemorySensorPlug1))
            {
                data = MemorySensorPlug1.Split(' ');
                if (data.Length > 8)
                {
                    sensor1OffsetPosition.x = (float)Convert.ToDouble(data[3].Trim());
                    sensor1OffsetPosition.y = (float)Convert.ToDouble(data[4].Trim());
                    sensor1OffsetPosition.z = (float)Convert.ToDouble(data[5].Trim());
                    sensor1OffsetRotation.x = (float)Convert.ToDouble(data[6].Trim());
                    sensor1OffsetRotation.y = (float)Convert.ToDouble(data[7].Trim());
                    sensor1OffsetRotation.z = (float)Convert.ToDouble(data[8].Trim());
#if !UNITY_WEBGL
                    SetSensorPositionOffset(1, sensor1OffsetPosition.x, sensor1OffsetPosition.y, sensor1OffsetPosition.z);
                    SetSensorRotationOffset(1, sensor1OffsetRotation.x, sensor1OffsetRotation.y, sensor1OffsetRotation.z);
#endif
                }
                if (data.Length > 12)
                {
                    Sensor1Utilities = new Vector4((float)Convert.ToDouble(data[9].Trim()), (float)Convert.ToDouble(data[10].Trim()),
                    (float)Convert.ToDouble(data[11].Trim()), (float)Convert.ToDouble(data[12].Trim()));
                }
                else
                    Sensor1Utilities = Vector4.zero;
            }
            if (!string.IsNullOrEmpty(MemorySensorPlug2))
            {
                data = MemorySensorPlug2.Split(' ');
                if (data.Length > 8)
                {
                    sensor2OffsetPosition.x = (float)Convert.ToDouble(data[3].Trim());
                    sensor2OffsetPosition.y = (float)Convert.ToDouble(data[4].Trim());
                    sensor2OffsetPosition.z = (float)Convert.ToDouble(data[5].Trim());
                    sensor2OffsetRotation.x = (float)Convert.ToDouble(data[6].Trim());
                    sensor2OffsetRotation.y = (float)Convert.ToDouble(data[7].Trim());
                    sensor2OffsetRotation.z = (float)Convert.ToDouble(data[8].Trim());
#if !UNITY_WEBGL
                    SetSensorPositionOffset(2, sensor2OffsetPosition.x, sensor2OffsetPosition.y, sensor2OffsetPosition.z);
                    SetSensorRotationOffset(2, sensor2OffsetRotation.x, sensor2OffsetRotation.y, sensor2OffsetRotation.z);
#endif
                }
                if (data.Length > 12)
                {
                    Sensor2Utilities = new Vector4((float)Convert.ToDouble(data[9].Trim()), (float)Convert.ToDouble(data[10].Trim()),
                    (float)Convert.ToDouble(data[11].Trim()), (float)Convert.ToDouble(data[12].Trim()));
                }
                else
                    Sensor2Utilities = Vector4.zero;
            }
            if (!string.IsNullOrEmpty(MemorySensorPlug3))
            {
                data = MemorySensorPlug3.Split(' ');
                if (data.Length > 8)
                {
                    sensor3OffsetPosition.x = (float)Convert.ToDouble(data[3].Trim());
                    sensor3OffsetPosition.y = (float)Convert.ToDouble(data[4].Trim());
                    sensor3OffsetPosition.z = (float)Convert.ToDouble(data[5].Trim());
                    sensor3OffsetRotation.x = (float)Convert.ToDouble(data[6].Trim());
                    sensor3OffsetRotation.y = (float)Convert.ToDouble(data[7].Trim());
                    sensor3OffsetRotation.z = (float)Convert.ToDouble(data[8].Trim());
#if !UNITY_WEBGL
                    SetSensorPositionOffset(3, sensor3OffsetPosition.x, sensor3OffsetPosition.y, sensor3OffsetPosition.z);
                    SetSensorRotationOffset(3, sensor3OffsetRotation.x, sensor3OffsetRotation.y, sensor3OffsetRotation.z);
#endif
                }
                if (data.Length > 12)
                {
                    Sensor3Utilities = new Vector4((float)Convert.ToDouble(data[9].Trim()), (float)Convert.ToDouble(data[10].Trim()),
                    (float)Convert.ToDouble(data[11].Trim()), (float)Convert.ToDouble(data[12].Trim()));
                }
                else
                    Sensor3Utilities = Vector4.zero;
            }
            if (!string.IsNullOrEmpty(MemorySensorPlug4))
            {
                data = MemorySensorPlug4.Split(' ');
                if (data.Length > 8)
                {
                    sensor4OffsetPosition.x = (float)Convert.ToDouble(data[3].Trim());
                    sensor4OffsetPosition.y = (float)Convert.ToDouble(data[4].Trim());
                    sensor4OffsetPosition.z = (float)Convert.ToDouble(data[5].Trim());
                    sensor4OffsetRotation.x = (float)Convert.ToDouble(data[6].Trim());
                    sensor4OffsetRotation.y = (float)Convert.ToDouble(data[7].Trim());
                    sensor4OffsetRotation.z = (float)Convert.ToDouble(data[8].Trim());
#if !UNITY_WEBGL
                    SetSensorPositionOffset(4, sensor4OffsetPosition.x, sensor4OffsetPosition.y, sensor4OffsetPosition.z);
                    SetSensorRotationOffset(4, sensor4OffsetRotation.x, sensor4OffsetRotation.y, sensor4OffsetRotation.z);
#endif
                }
                if (data.Length > 12)
                {
                    Sensor4Utilities = new Vector4((float)Convert.ToDouble(data[9].Trim()), (float)Convert.ToDouble(data[10].Trim()),
                    (float)Convert.ToDouble(data[11].Trim()), (float)Convert.ToDouble(data[12].Trim()));
                }
                else
                    Sensor4Utilities = Vector4.zero;
            }
            //utilityDataAccessed = false;
        }
        void Update()
        {
            /*if (ATC_Field_Logger.ME.LOGATC)
            {
                ATC_Field_Logger.ME.LOGATC = false;
                Debug.Log("BASEOBJECT: " + BaseObject + "\n" +
                    "ENABLE TRACKING BASE OBJECT: " + EnableTrackingBaseObject + "\n" +
                    "TRACKED OBJECT 1: " + TrackedObject1 + "\n" +
                    "ENABLE TRACKING OBJECT 1: " + EnableTrackingObject1 + "\n" +
                    "TRACKED OBJECT 2: " + TrackedObject2 + "\n" +
                    "ENABLE TRACKING OBJECT 2: " + EnableTrackingObject2 + "\n" +
                    "TRACKED OBJECT 3: " + TrackedObject3 + "\n" +
                    "ENABLE TRACKING OBJECT 3: " + EnableTrackingObject3 + "\n" +
                    "TRACKED OBJECT 4: " + TrackedObject4 + "\n" +
                    "ENABLE TRACKING OBJECT 4: " + EnableTrackingObject4 + "\n" +
                    "DISABLE ATC CONNECTION: " + DisableATCConnectionRoutine + "\n" +
                    "CURRENT INPUT MODE: " + CurrentInputMode + "\n" +
                    "REPLAY DATA INPUT: " + ReplayDataInput + "\n" +
                    "REPLAY FORMATTED ATC DATA: " + ReplayFormattedATCInputString + "\n" +
                    "CONNECTED: " + Connected + "\n" +
                    "CONNECTION STATUS: " + ConnectionStatus + "\n" +
                    "HEMISPHERE: " + Hemisphere + "\n" +
                    "TRANSMITTER ROTATION: " + transmitterRotation + "\n" +
                    "USE MODULAR STAND PRESET ROATAION: " + useModularStandPresetRotation + "\n" +
                    "USE PRESETS FOR SRT: " + usePresetsForSRT + "\n" +
                    "SAMPLING FREQUENCY: " + samplingFrequency + "\n" +
                    "APPLY CUSTOM FILTERS: " + applyCustomFilters + "\n" +
                    "IGNORE LARGE CHANGES: " + ignoreLargeChanges + "\n" +
                    "AC WIDE NOTCH FILTER: " + ac_WideNotchFilter + "\n" +
                    "AC NARROW NOTCH FILTER: " + ac_NarrowNotchFilter + "\n" +
                    "DC ADAPTIVE FILTER: " + dc_AdaptiveFilter + "\n" +
                    "VM: " + _Vm[0] + ", " + _Vm[1] + ", " + _Vm[2] + ", " + _Vm[3] + ", " + _Vm[4] + ", " + _Vm[5] + ", " + _Vm[6] + "\n" +
                    "ALPHA MIN: " + alphaMin + "\n" +
                    "ALPHA MAX: " + alphaMax + "\n" +
                    "EDIT SENSOR: " + editSensor + "\n" +
                    "OFFSET POSITION: " + offsetPosition + "\n" +
                    "OFFSET ROTATION: " + offsetRotation + "\n" +
                    "UTILITIES: " + utilities.ToString() + "\n" +
                    "MEMORY SENSOR PLUG 1: " + memorySensorPlug1 + "\n" +
                    "MEMORY SENSOR PLUG 2: " + memorySensorPlug2 + "\n" +
                    "MEMORY SENSOR PLUG 3: " + memorySensorPlug3 + "\n" +
                    "MEMORY SENSOR PLUG 4: " + memorySensorPlug4 + "\n" +
                    "MEMORY TRACKING UNIT: " + memoryTrackingUnit + "\n" +
                    "UPLOAD TARGET: " + uploadTarget + "\n" +
                    "UPLOAD TEXT: " + uploadText + "\n" +
                    "DEBUGGING ACTIVE: " + DebuggingActive + "\n" +
                    "ERROR DEBUGGING ACTIVE: " + ErrorDebuggingActive);
            }*/
            //if (readyTOParseEEPROMData){ ParseDataFromEEPROMs(); readyTOParseEEPROMData = false; }
            //if (!replayDataAccessedLastFrame)
            //    utilityDataAccessed = false;
            //replayDataAccessedLastFrame = false;
#if !UNITY_WEBGL
            if (ATCConnected && disableATCConnectionRoutine)
            {
                AtcErrorCode = GoodbyeATC();
                AbortConnection();
            }
            else if (!ATCConnected && ATCConnectionStatus != Connection_Status.Establishing_Connection && !disableATCConnectionRoutine)
            {
                try { ATCCommunicator.Abort(); } catch (Exception) { }
                ATCCommunicator = new Thread(ATCConnectionRoutine) { Name = "ATC Thread" };
                ATCCommunicator.Start();
            }
            else if (ATCConnected && currentInputMode == InputMode.ATC)
            {

                MoveTrackedObjects();
            }
#endif
            //if (currentInputMode == InputMode.Replay_Data)
            //{
            ///	ParseReplayData();
            //}
        }
        void ParseReplayData()
        {
            //Debug.Log("TRIED " + replayDataInput);
            try
            {
                if (replayDataInput.Length < 13)
                {
                    if (errorDebuggingActive)
                    {
                        Debug.LogError("IMPROPER REPLAY DATA STRING DETECTED: " + replayDataInput);
                    }
                    else if (debuggingActive)
                    {
                        Debug.Log("IMPROPER REPLAY DATA STRING DETECTED: " + replayDataInput);
                    }
                    connected = false;
                    connectionStatus = Connection_Status.Not_Connected;
                    ZeroATCData();
                    return;
                }
                if (!replayDataInput.Substring(0, 13).Equals("ATC DATA|ATCD"))
                {
                    if (errorDebuggingActive)
                    {
                        Debug.LogError("IMPROPER REPLAY DATA STRING DETECTED: " + replayDataInput);
                    }
                    else if (debuggingActive)
                    {
                        Debug.Log("IMPROPER REPLAY DATA STRING DETECTED: " + replayDataInput);
                    }
                    connected = false;
                    connectionStatus = Connection_Status.Not_Connected;
                    ZeroATCData();
                    return;
                }
                string[] splitReplayData = replayDataInput.Split('|');
                if (splitReplayData.Length <= 3)
                {
                    connected = false;
                    if (splitReplayData[1].Equals("ATCEC"))
                        connectionStatus = Connection_Status.Establishing_Connection;
                    else
                        connectionStatus = Connection_Status.Not_Connected;
                    ZeroATCData();
                }
                else
                {
                    connected = true;
                    connectionStatus = Connection_Status.Connected;
                    enableTrackingBaseObject = splitReplayData[2].Equals("True");
                    if (enableTrackingBaseObject)
                    {
                        baseObject.transform.position = Vector3StringToVector3(splitReplayData[3]);
                        baseObject.transform.rotation = QuaternionStringToQuaternion(splitReplayData[4]);
                    }
                    //Debug.Log(splitReplayData[5] + " " + "True");
                    enableTrackingObject1 = splitReplayData[5].Equals("True");
                    if (enableTrackingObject1)
                    {
                        trackedObject1.transform.position = Vector3StringToVector3(splitReplayData[6]);
                        trackedObject1.transform.rotation = QuaternionStringToQuaternion(splitReplayData[7]);
                    }

                    enableTrackingObject2 = splitReplayData[8].Equals("True");
                    if (enableTrackingObject2)
                    {
                        //Debug.Log("object 2");
                        trackedObject2.transform.position = Vector3StringToVector3(splitReplayData[9]);
                        trackedObject2.transform.rotation = QuaternionStringToQuaternion(splitReplayData[10]);
                    }
                    enableTrackingObject3 = splitReplayData[11].Equals("True");
                    if (enableTrackingObject3)
                    {
                        trackedObject3.transform.position = Vector3StringToVector3(splitReplayData[12]);
                        trackedObject3.transform.rotation = QuaternionStringToQuaternion(splitReplayData[13]);
                    }
                    enableTrackingObject4 = splitReplayData[14].Equals("True");
                    if (enableTrackingObject4)
                    {
                        trackedObject4.transform.position = Vector3StringToVector3(splitReplayData[15]);
                        trackedObject4.transform.rotation = QuaternionStringToQuaternion(splitReplayData[16]);
                    }
                }
            }
            catch (Exception e)
            {
                if (errorDebuggingActive)
                {
                    Debug.LogError("REPLAY DATA PARSING ERROR OCCURED: ATC001\n" + e);
                }
                connectionStatus = Connection_Status.Not_Connected;
                connected = false;
            }
        }
        void ParseUtilityData()
        {
            try
            {
                if (replayUtilityInput.Length < 13)
                {
                    if (errorDebuggingActive)
                    {
                        Debug.LogError("IMPROPER REPLAY DATA STRING DETECTED: " + replayUtilityInput);
                    }
                    else if (debuggingActive)
                    {
                        Debug.Log("IMPROPER REPLAY DATA STRING DETECTED: " + replayUtilityInput);
                    }
                    connected = false;
                    connectionStatus = Connection_Status.Not_Connected;
                    ZeroATCData();
                    return;
                }
                if (!replayUtilityInput.Substring(0, 13).Equals("ATC DATA|ATCU"))
                {
                    if (errorDebuggingActive)
                    {
                        Debug.LogError("IMPROPER REPLAY DATA STRING DETECTED: " + replayUtilityInput);
                    }
                    else if (debuggingActive)
                    {
                        Debug.Log("IMPROPER REPLAY DATA STRING DETECTED: " + replayUtilityInput);
                    }
                    connected = false;
                    connectionStatus = Connection_Status.Not_Connected;
                    ZeroATCData();
                    return;
                }
                string[] splitUtilityData = replayUtilityInput.Split('|');
                if (splitUtilityData.Length == 8)
                {
                    MemoryTransmitterPlug = splitUtilityData[2];
                    MemorySensorPlug1 = splitUtilityData[3];
                    MemorySensorPlug2 = splitUtilityData[4];
                    MemorySensorPlug3 = splitUtilityData[5];
                    MemorySensorPlug4 = splitUtilityData[6];
                    MemoryTrackingUnit = splitUtilityData[7];
                }
            }
            catch (Exception e)
            {
                if (errorDebuggingActive)
                {
                    Debug.LogError("UTILITY DATA PARSING ERROR OCCURED: ATC002\n" + e);
                }
            }
        }
        Vector3 Vector3StringToVector3(string vec3)
        {
            try
            {
                vec3.Trim();
                vec3 = vec3.Substring(1, vec3.Length - 2);
                string[] splitVector = vec3.Split(',');
                return new Vector3(float.Parse(splitVector[0]), float.Parse(splitVector[1]), float.Parse(splitVector[2]));
            }
            catch
            {
                return Vector3.zero;
            }
        }
        Quaternion QuaternionStringToQuaternion(string quat)
        {
            try
            {
                quat.Trim();
                quat = quat.Substring(1, quat.Length - 2);
                string[] splitQuaternion = quat.Split(',');
                return new Quaternion(float.Parse(splitQuaternion[0]), float.Parse(splitQuaternion[1]), float.Parse(splitQuaternion[2]), float.Parse(splitQuaternion[3]));
            }
            catch
            {
                return Quaternion.identity;
            }
        }
        void ZeroATCData()
        {
            if (baseObject != null)
            {
                baseObject.transform.position = Vector3.zero;
                baseObject.transform.rotation = Quaternion.identity;
            }
            if (trackedObject1 != null)
            {
                trackedObject1.transform.position = Vector3.zero;
                trackedObject1.transform.rotation = Quaternion.identity;
            }
            if (trackedObject2 != null)
            {
                trackedObject2.transform.position = Vector3.zero;
                trackedObject2.transform.rotation = Quaternion.identity;
            }
            if (trackedObject3 != null)
            {
                trackedObject3.transform.position = Vector3.zero;
                trackedObject3.transform.rotation = Quaternion.identity;
            }
            if (trackedObject4 != null)
            {
                trackedObject4.transform.position = Vector3.zero;
                trackedObject4.transform.rotation = Quaternion.identity;
            }
            Sensor1Utilities = Vector4.zero;
            Sensor2Utilities = Vector4.zero;
            Sensor3Utilities = Vector4.zero;
            Sensor4Utilities = Vector4.zero;
        }
#if !UNITY_WEBGL
        void MoveTrackedObjects()
        {
            AtcErrorCode = MakeRecordsATC();
            if (AtcErrorCode != 0)
            {
                AbortConnection();
                return;
            }

            // Base Object
            // Check if Base Object has an active replayer
            bool baseObjectHasActiveReplayer = (baseObject != null &&
                baseObject.GetComponent<TrackedObjectReplayer>()?.IsReplaying == true);

            if (enableTrackingBaseObject && !baseObjectHasActiveReplayer)
            {
                baseObject.transform.position = baseObjectPosition;
                baseObject.transform.rotation = baseObjectRotation;
            }


            // Tracked Object 1
            // Check if TrackedObject1 has an active replayer
            bool object1HasActiveReplayer = (trackedObject1 != null &&
                trackedObject1.GetComponent<TrackedObjectReplayer>()?.IsReplaying == true);

            if (enableTrackingObject1 && !object1HasActiveReplayer)
            {
                trackedObject1.transform.position = new Vector3(GetRecordATC_1x() * volumeCorrectionFactor.x, GetRecordATC_1y() * volumeCorrectionFactor.y, GetRecordATC_1z() * volumeCorrectionFactor.z);
                trackedObject1.transform.rotation = new Quaternion(GetRecordATC_1q0(), GetRecordATC_1q1(), GetRecordATC_1q2(), GetRecordATC_1q3());
                if (trackedObject1.transform.position == new Vector3(0, 0, 0))
                {
                    if (SensorMissing(0))
                        enableTrackingObject1 = false;
                    else
                    {
                        AbortConnection();
                        PowerFailure = true;
                        return;
                    }
                }
            }

            // Tracked Object 2
            // Check if TrackedObject2 has an active replayer
            bool object2HasActiveReplayer = (trackedObject2 != null &&
                trackedObject2.GetComponent<TrackedObjectReplayer>()?.IsReplaying == true);

            if (enableTrackingObject2 && !object2HasActiveReplayer)
            {
                trackedObject2.transform.position = new Vector3(GetRecordATC_2x() * volumeCorrectionFactor.x, GetRecordATC_2y() * volumeCorrectionFactor.y, GetRecordATC_2z() * volumeCorrectionFactor.z);
                trackedObject2.transform.rotation = new Quaternion(GetRecordATC_2q0(), GetRecordATC_2q1(), GetRecordATC_2q2(), GetRecordATC_2q3());
                if (trackedObject2.transform.position == new Vector3(0, 0, 0))
                {
                    if (SensorMissing(1))
                        enableTrackingObject2 = false;
                    else
                    {
                        AbortConnection();
                        PowerFailure = true;
                        return;
                    }
                }
            }


            // Tracked Object 3
            // Check if TrackedObject3 has an active replayer
            bool object3HasActiveReplayer = (trackedObject3 != null &&
                trackedObject3.GetComponent<TrackedObjectReplayer>()?.IsReplaying == true);

            if (enableTrackingObject3 && !object3HasActiveReplayer)
            {
                trackedObject3.transform.position = new Vector3(GetRecordATC_3x() * volumeCorrectionFactor.x, GetRecordATC_3y() * volumeCorrectionFactor.z, GetRecordATC_3z() * volumeCorrectionFactor.z);
                trackedObject3.transform.rotation = new Quaternion(GetRecordATC_3q0(), GetRecordATC_3q1(), GetRecordATC_3q2(), GetRecordATC_3q3());
                if (trackedObject3.transform.position == new Vector3(0, 0, 0))
                {
                    if (SensorMissing(2))
                        enableTrackingObject3 = false;
                    else
                    {
                        AbortConnection();
                        PowerFailure = true;
                        return;
                    }
                }
            }

            // Tracked Object 4
            // Check if TrackedObject4 has an active replayer
            bool object4HasActiveReplayer = (trackedObject4 != null &&
                trackedObject4.GetComponent<TrackedObjectReplayer>()?.IsReplaying == true);

            if (enableTrackingObject4 && !object4HasActiveReplayer)
            {
                trackedObject4.transform.position = new Vector3(GetRecordATC_4x() * volumeCorrectionFactor.x, GetRecordATC_4y() * volumeCorrectionFactor.y, GetRecordATC_4z() * volumeCorrectionFactor.z);
                trackedObject4.transform.rotation = new Quaternion(GetRecordATC_4q0(), GetRecordATC_4q1(), GetRecordATC_4q2(), GetRecordATC_4q3());
                if (trackedObject4.transform.position == new Vector3(0, 0, 0))
                {
                    if (SensorMissing(3))
                        enableTrackingObject4 = false;
                    else
                    {
                        AbortConnection();
                        PowerFailure = true;
                        return;
                    }
                }
            }
            string newReplayFormattedATCInputString = "ATC DATA|ATCDC|";
            GameObject GO;
            newReplayFormattedATCInputString += enableTrackingBaseObject + "|";
            if (enableTrackingBaseObject)
            {
                GO = baseObject;
                newReplayFormattedATCInputString += GO.transform.position.ToString("0.0000") + "|" + GO.transform.rotation.ToString("0.0000");
            }
            else
                newReplayFormattedATCInputString += Vector3.zero.ToString("0.0") + "|" + Quaternion.identity.ToString("0.0");
            newReplayFormattedATCInputString += "|" + enableTrackingObject1;
            if (enableTrackingObject1)
            {
                GO = trackedObject1;
                newReplayFormattedATCInputString += "|" + GO.transform.position.ToString("0.0000") + "|" + GO.transform.rotation.ToString("0.0000");
            }
            else
                newReplayFormattedATCInputString += "|" + Vector3.zero.ToString("0.0") + "|" + Quaternion.identity.ToString("0.0");
            newReplayFormattedATCInputString += "|" + enableTrackingObject2;
            if (enableTrackingObject2)
            {
                GO = trackedObject2;
                newReplayFormattedATCInputString += "|" + GO.transform.position.ToString("0.0000") + "|" + GO.transform.rotation.ToString("0.0000");
            }
            else
                newReplayFormattedATCInputString += "|" + Vector3.zero.ToString("0.0") + "|" + Quaternion.identity.ToString("0.0");
            newReplayFormattedATCInputString += "|" + enableTrackingObject3;
            if (enableTrackingObject3)
            {
                GO = trackedObject3;
                newReplayFormattedATCInputString += "|" + GO.transform.position.ToString("0.0000") + "|" + GO.transform.rotation.ToString("0.0000");
            }
            else
                newReplayFormattedATCInputString += "|" + Vector3.zero.ToString("0.0") + "|" + Quaternion.identity.ToString("0.0");
            newReplayFormattedATCInputString += "|" + enableTrackingObject4;
            if (enableTrackingObject4)
            {
                GO = trackedObject4;
                newReplayFormattedATCInputString += "|" + GO.transform.position.ToString("0.0000") + "|" + GO.transform.rotation.ToString("0.0000");
            }
            else
                newReplayFormattedATCInputString += "|" + Vector3.zero.ToString("0.0") + "|" + Quaternion.identity.ToString("0.0");
            replayFormattedATCInputString = newReplayFormattedATCInputString;
        }
#endif
        void OnApplicationQuit()
        {
#if !UNITY_WEBGL
            AtcErrorCode = GoodbyeATC();
#endif
            AbortConnection();
        }
        private string noError = "No Error Codes to lookup.";
        void LookupErrorDescription(int errorCode)
        {
            if (errorCode != 0)
            {
#if !UNITY_WEBGL
                IntPtr echoedStringErr = GetErrorsReport(errorCode);
                errorReport = Marshal.PtrToStringAuto(echoedStringErr);
                //if (debuggingActive)
                //	Debug.Log("ATC Error Report: " + errorReport);
#else
                // The error string table lives in the native plugin, which WebGL does not have.
                errorReport = "ATC error code " + errorCode + " (no description available on WebGL).";
#endif
            }
            else
            {
                errorReport = noError;
            }
        }
        public Vector4 LookUpTrackedObjectUtilities(string trackedObjectName)
        {

            Vector4 results = Vector4.zero;
            if (trackedObject1 != null && trackedObjectName == trackedObject1.name)
                results = Sensor1Utilities;
            if (trackedObject2 != null && trackedObjectName == trackedObject2.name)
                results = Sensor2Utilities;
            if (trackedObject3 != null && trackedObjectName == trackedObject3.name)
                results = Sensor3Utilities;
            if (trackedObject4 != null && trackedObjectName == trackedObject4.name)
                results = Sensor4Utilities;

            return results;
        }
        void ResetToATCSettings()
        {
            enableTrackingBaseObject = enableATCTrackingBaseObject;
            enableTrackingObject1 = enableATCTrackingObject1;
            enableTrackingObject2 = enableATCTrackingObject2;
            enableTrackingObject3 = enableATCTrackingObject3;
            enableTrackingObject4 = enableATCTrackingObject4;
        }

        // --------- Special Alignment Stuff - EDITOR ONLY ----------------------//
        void LateUpdate()
        {
            //Debug.Log("LATE UPDATE");

            // if we have been connected since last frame, then proceed
            if (ConnectionStatus == Connection_Status.Connected)
            {

                // Want to play with the Transmitter Reference Frame Rotation in real time?  Set UsePresetTransmitterRotation to true.
                if (!useModularStandPresetRotation)
                {
                    if (transmitterRotation != lastTransmitterRotation)
                    {
#if !UNITY_WEBGL
                        AtcErrorCode = RotateTransmitterReferenceFrame(transmitterRotation.x, transmitterRotation.y, transmitterRotation.z);
#endif
                        lastTransmitterRotation = transmitterRotation;
                        LookupErrorDescription(AtcErrorCode);
                    }
                }

                // Adjust the Sensor Position and Rotation Offsets in realtime
                if (editSensor != AlignSensor.NONE)
                {
                    // if a new sensor was just selected, then load the OffsetPosition and Rotation from what was previously read from EEPROM.
                    if (LastEditedSensor != editSensor)
                    {
                        if (editSensor == AlignSensor.Sensor1) offsetPosition = sensor1OffsetPosition;
                        if (editSensor == AlignSensor.Sensor1) offsetRotation = sensor1OffsetRotation;
                        if (editSensor == AlignSensor.Sensor1) utilities = Sensor1Utilities;
                        if (editSensor == AlignSensor.Sensor2) offsetPosition = sensor2OffsetPosition;
                        if (editSensor == AlignSensor.Sensor2) offsetRotation = sensor2OffsetRotation;
                        if (editSensor == AlignSensor.Sensor2) utilities = Sensor2Utilities;
                        if (editSensor == AlignSensor.Sensor3) offsetPosition = sensor3OffsetPosition;
                        if (editSensor == AlignSensor.Sensor3) offsetRotation = sensor3OffsetRotation;
                        if (editSensor == AlignSensor.Sensor3) utilities = Sensor3Utilities;
                        if (editSensor == AlignSensor.Sensor4) offsetPosition = sensor4OffsetPosition;
                        if (editSensor == AlignSensor.Sensor4) offsetRotation = sensor4OffsetRotation;
                        if (editSensor == AlignSensor.Sensor4) utilities = Sensor4Utilities;
                    }
                    LastEditedSensor = editSensor;

                    // Upload any changes to the Sensor Position.  These are temporary and will be overwritten unless saved to EEPROM.
                    if (lastSensorOffsetPosition != offsetPosition)
                    {
#if !UNITY_WEBGL
                        SetSensorPositionOffset((int)editSensor, offsetPosition.x, offsetPosition.y, offsetPosition.z);
#endif
                        lastSensorOffsetPosition = offsetPosition;
                    }

                    // Upload any changes to the Sensor Rotation.  These are temporary and will be overwritten unless saved to EEPROM.
                    if (lastSensorOffsetRotation != offsetRotation)
                    {
#if !UNITY_WEBGL
                        SetSensorRotationOffset((int)editSensor, offsetRotation.x, offsetRotation.y, offsetRotation.z);
#endif
                        lastSensorOffsetRotation = offsetRotation;
                    }


                }
                else
                {
                    // Don't leave confuse the user interface by leaving expired information laying around
                    offsetPosition = Vector3.zero;
                    offsetRotation = Vector3.zero;
                    utilities = Vector4.zero;
                }


            }
        }
        // --------- Special Alignment Stuff - EDITOR ONLY ----------------------//

        // --------- Upload to EEPROM - EDITOR ONLY -----------------------------//
        public void UploadToEEPROM()
        {
            int identifier = (int)uploadTarget;
            if (identifier != 0)
            {

#if !UNITY_WEBGL
                WriteToEEPROM(identifier, uploadText);
#endif
                uploadText = "";
                uploadTarget = EEPROM.NONE;
                editSensor = AlignSensor.NONE;
                offsetPosition = Vector3.zero;
                offsetRotation = Vector3.zero;
                utilities = Vector4.zero;

                // Flag for the restart sequence - also read the EEPROMS
                //ReadSensorEEPROMS = true;

                // update will read this status and try to reconnect.
                //connectionStatus = Connection_Status.WaitingToReconnect;
                AbortConnection();

                // We will wait only one second on startup to let the screen refresh.
                //restartWaitTimer = restartWaitSeconds - 1;
            }
            else
            {
                Debug.Log("Can't upload. Select an upload target. Be sure to first review your edited/generated string.");
            }
        }
        // --------- Upload to EEPROM - EDITOR ONLY -----------------------------//

        // --------- Make a Formatted Alignment STRING - EDITOR ONLY ------------//
        public void GenerateSensorEEPROMAlignmentString()
        {
            if (editSensor != AlignSensor.NONE)
            {
                string makerID = "UFCSSALT";
                string date = System.DateTime.Today.Date.ToShortDateString();

                string objectName = "";
                if (editSensor == AlignSensor.Sensor1) objectName = trackedObject1.name;
                if (editSensor == AlignSensor.Sensor2) objectName = trackedObject2.name;
                if (editSensor == AlignSensor.Sensor3) objectName = trackedObject3.name;
                if (editSensor == AlignSensor.Sensor4) objectName = trackedObject4.name;

                string px = offsetPosition.x.ToString("0.000");
                string py = offsetPosition.y.ToString("0.000");
                string pz = offsetPosition.z.ToString("0.000");
                string rx = offsetRotation.x.ToString("0.000");
                string ry = offsetRotation.y.ToString("0.000");
                string rz = offsetRotation.z.ToString("0.000");
                string ux = utilities.x.ToString("0.000");
                string uy = utilities.y.ToString("0.000");
                string uz = utilities.z.ToString("0.000");
                string uw = utilities.w.ToString("0.000");

                if (editSensor == AlignSensor.Sensor1) uploadTarget = EEPROM.Sensor1;
                if (editSensor == AlignSensor.Sensor2) uploadTarget = EEPROM.Sensor2;
                if (editSensor == AlignSensor.Sensor3) uploadTarget = EEPROM.Sensor3;
                if (editSensor == AlignSensor.Sensor4) uploadTarget = EEPROM.Sensor4;

                makerID = makerID.Replace(" ", "");
                objectName = objectName.Replace(" ", "");
                date = date.Replace(" ", "");
                uploadText = objectName + " " + makerID + " " + date + " " + px + " " + py + " " + pz + " " + rx + " " + ry + " " + rz + " " + ux + " " + uy + " " + uz + " " + uw;

                print("Review the string before uploading to EEPROM:");
                print(uploadText);
            }
            else
            {
                Debug.Log("Select a sensor to edit from the dropdown list.");
            }

        }
        // --------- Make a Formatted Alignment STRING - EDITOR ONLY ------------//

        // --------- Make a Formatted Alignment STRING - EDITOR ONLY ------------//
        public void GenerateTransmitterEEPROMAlignmentString()
        {
            if (baseObject != null)
            {

                uploadTarget = EEPROM.Transmitter;

                Debug.Log("Generating a Gameobject alignment string for the transmitter...");
                Debug.Log("Be sure the anatomy or alignment objects are children of the Base Object.");
                Debug.Log("This allows this transmitter to be registered to an anatomical base.");

                string makerID = "UFCSSALT";
                string date = System.DateTime.Today.Date.ToShortDateString();

                string objectName = "SRT-MRT";

                string px = baseObject.transform.position.x.ToString("0.000");
                string py = baseObject.transform.position.y.ToString("0.000");
                string pz = baseObject.transform.position.z.ToString("0.000");
                string rw = baseObject.transform.rotation.w.ToString("0.000");
                string rx = baseObject.transform.rotation.x.ToString("0.000");
                string ry = baseObject.transform.rotation.y.ToString("0.000");
                string rz = baseObject.transform.rotation.z.ToString("0.000");
                string ux = utilities.x.ToString("0.000");
                string uy = utilities.y.ToString("0.000");
                string uz = utilities.z.ToString("0.000");
                string uw = utilities.w.ToString("0.000");

                // remove unnecessary spaces
                makerID = makerID.Replace(" ", "");
                objectName = objectName.Replace(" ", "");
                date = date.Replace(" ", "");
                uploadText = objectName + " " + makerID + " " + date + " " + px + " " + py + " " + pz + " " + rw + " " + rx + " " + ry + " " + rz + " " + ux + " " + uy + " " + uz + " " + uw;

                print("Review the string before uploading to EEPROM:");
                print(uploadText);
            }
            else
            {
                Debug.Log("Select a base object first.");
            }

        }

        bool SensorMissing(ushort sensor)
        {
#if UNITY_WEBGL
            // No tracking hardware on WebGL, so every sensor is reported missing.
            return true;
#else
            uint status = GetSensorStatus(sensor);
            uint sensorMissingCode = 2;
            if(debuggingActive||errorDebuggingActive)
            {
                GameObject trackedObject = null;
                if (sensor == 0)
                    trackedObject = trackedObject1;
                if (sensor == 1)
                    trackedObject = trackedObject2;
                if (sensor == 2)
                    trackedObject = trackedObject3;
                if (sensor == 3)
                    trackedObject = trackedObject4;
                if (debuggingActive)
                    Debug.Log("Attempting to track: " + trackedObject.name
                        + " with sensor port: " + sensor + ".\nNo sensor connected," +
                        " disabling tracking of " + trackedObject1 + ".");
                else if (errorDebuggingActive)
                    Debug.LogError("Attempting to track: " + trackedObject.name
                    + " with sensor port: " + sensor + ".\nNo sensor connected," +
                    " disabling tracking of " + trackedObject1 + ".");
            }
            return (sensorMissingCode & status) == sensorMissingCode;
#endif
        }

        // --------- References to Plugin Functions -------------------//
        // ATC64 is a Windows x86_64 native plugin, so none of it exists on WebGL.
#if !UNITY_WEBGL
        [DllImport("ATC64")]
        private static extern int HelloATC(bool ReadSensorEEPROMS);
        [DllImport("ATC64")]
        private static extern IntPtr GetTransmitterType();
        [DllImport("ATC64")]
        private static extern int SetHemisphere(int sensorPlug, int zone);
        [DllImport("ATC64")]
        private static extern int SetSensorPositionOffset(int sensorPlug, float x, float y, float z);
        [DllImport("ATC64")]
        private static extern int SetSensorRotationOffset(int sensorPlug, float x, float y, float z);
        [DllImport("ATC64")]
        private static extern int RotateTransmitterReferenceFrame(float azimuth, float elevation, float roll);
        [DllImport("ATC64")]
        private static extern int SetMeasurementRate(int freq);
        [DllImport("ATC64")]
        private static extern int SetSensorFilterParameters(int sensorPlug, bool largeChange, bool ACWNF, bool ACNNF, bool DCAF, int Vm0, int Vm1, int Vm2, int Vm3, int Vm4, int Vm5, int Vm6, int alphaMin, int alphaMax);
        [DllImport("ATC64")]
        private static extern IntPtr ReadSensor1EEPROM();
        [DllImport("ATC64")]
        private static extern IntPtr ReadSensor2EEPROM();
        [DllImport("ATC64")]
        private static extern IntPtr ReadSensor3EEPROM();
        [DllImport("ATC64")]
        private static extern IntPtr ReadSensor4EEPROM();
        [DllImport("ATC64")]
        private static extern IntPtr ReadTransmitterEEPROM();
        [DllImport("ATC64")]
        private static extern IntPtr ReadBoardEEPROM();
        [DllImport("ATC64")]
        private static extern void WriteToEEPROM(int Identifier, string Information);
        [DllImport("ATC64")]
        private static extern int MakeRecordsATC();
        [DllImport("ATC64")]
        private static extern float GetRecordATC_1x();
        [DllImport("ATC64")]
        private static extern float GetRecordATC_1y();
        [DllImport("ATC64")]
        private static extern float GetRecordATC_1z();
        [DllImport("ATC64")]
        private static extern float GetRecordATC_1q0();
        [DllImport("ATC64")]
        private static extern float GetRecordATC_1q1();
        [DllImport("ATC64")]
        private static extern float GetRecordATC_1q2();
        [DllImport("ATC64")]
        private static extern float GetRecordATC_1q3();
        [DllImport("ATC64")]
        private static extern float GetRecordATC_2x();
        [DllImport("ATC64")]
        private static extern float GetRecordATC_2y();
        [DllImport("ATC64")]
        private static extern float GetRecordATC_2z();
        [DllImport("ATC64")]
        private static extern float GetRecordATC_2q0();
        [DllImport("ATC64")]
        private static extern float GetRecordATC_2q1();
        [DllImport("ATC64")]
        private static extern float GetRecordATC_2q2();
        [DllImport("ATC64")]
        private static extern float GetRecordATC_2q3();
        [DllImport("ATC64")]
        private static extern float GetRecordATC_3x();
        [DllImport("ATC64")]
        private static extern float GetRecordATC_3y();
        [DllImport("ATC64")]
        private static extern float GetRecordATC_3z();
        [DllImport("ATC64")]
        private static extern float GetRecordATC_3q0();
        [DllImport("ATC64")]
        private static extern float GetRecordATC_3q1();
        [DllImport("ATC64")]
        private static extern float GetRecordATC_3q2();
        [DllImport("ATC64")]
        private static extern float GetRecordATC_3q3();
        [DllImport("ATC64")]
        private static extern float GetRecordATC_4x();
        [DllImport("ATC64")]
        private static extern float GetRecordATC_4y();
        [DllImport("ATC64")]
        private static extern float GetRecordATC_4z();
        [DllImport("ATC64")]
        private static extern float GetRecordATC_4q0();
        [DllImport("ATC64")]
        private static extern float GetRecordATC_4q1();
        [DllImport("ATC64")]
        private static extern float GetRecordATC_4q2();
        [DllImport("ATC64")]
        private static extern float GetRecordATC_4q3();
        [DllImport("ATC64")]
        private static extern IntPtr GetErrorsReport(int errorCode);
        [DllImport("ATC64")]
        private static extern int LoadFromConfigFile(string filename);
        [DllImport("ATC64")]
        private static extern int SaveNewConfigFile(string filename);
        [DllImport("ATC64")]
        private static extern int GoodbyeATC();
        [DllImport("ATC3DG64")]
        private static extern uint GetSystemStatus();
        [DllImport("ATC3DG64")]
        private static extern uint GetSensorStatus(ushort sensorID);
#endif

        public static void SaveNewConfigurationFile(string filename)
        {
#if !UNITY_WEBGL
            Debug.Log(SaveNewConfigFile(filename));
#else
            Debug.LogWarning("SaveNewConfigurationFile is unavailable on WebGL: it is provided by the ATC64 native plugin.");
#endif
        }
        // --------- References to Plugin Functions -------------------//
    }



    /*
     * ------------------ API Literature search terms ---------------------------
     * This plugin uses an API called "ATC3DG.dll"
     * Its for NDI Ascension Technology Corporation's 3D Guidance (Rev D) tracking systems (driveBAY & trackSTAR)
     * 
     * Literature reference:
     * 3DGuidance_trakSTAR_Installation_and_Operation_Guide.pdf available at www.ascension-tech.com
     * 
     * HelloATC uses:
     * InitializeBIRDSystem, SetSystemParameter METRIC and SELECT_TRANSMITTER, SetSensorParameter DATA_FORMAT
     * 
     * SetMeasurementRate uses:
     * SetSystemParameter and MEASUREMENT_RATE
     * 
     * SetHemisphere uses:
     * SetSensorParameter and HEMISPHERE
     * 
     * LookupErrorDescription uses:
     * GetErrorText SIMPLE_MESSAGE
     * 
     * ReadSensor(x)EEPROM uses:
     * GetSensorParameter VITAL_PRODUCT_DATA_RX, GetTransmitterParameter VITAL_PRODUCT_DATA_TX, and GetBoardParameter VITAL_PRODUCT_DATA_PCB
     * 
     * WriteToEEPROM uses:
     * SetSensorParameter VITAL_PRODUCT_DATA_RX, SetTransmitterParameter VITAL_PRODUCT_DATA_TX, and SetBoardParameter VITAL_PRODUCT_DATA_PCB
     * 
     * MakeRecordsATC uses:
     * GetAsynchronousRecord and DOUBLE_POSITION_QUATERNION_RECORD
     * 
     * GoodByeATC uses:
     * CloseBIRDSystem
     * 
     */

    /*
    Change log:

        December 17 2015: never set the ReadSensorEEPROMS to false.
        Barys witnessed misaglinment that looked like needle offsets not being applied.
        Dave replicated error by unplugging ascension, plugging it back in, and letting ATC reconnect.
        Solution: set ReadSensorEEPROMS always true.
        We encode alignment offsets for the sensors in the EEPROMs.
        The Ascension hardware motherboard applies the aligment offsets, not Unity (which was the old way, used GameObject heirarchy).
        When Ascension hardware starts up, this script tells the Ascension hardware what those offsets are.
        Ascension hardware compensates for hemisphere, and our Unity scenes' Gameobject heirarchy doesn't need an awkward and unwieldy solution for offsets.
        If the power to the tracking box was lost, we will have to re-load those offsets to the Ascension motherboard.
        If connection to ATC was lost due to unplugging the USB cable, we don't have to do this again, but:
        we have NOT implemented a way to tell why the connection was lost.  Therefore, 
        Always upload the offsets to the Ascension motherboard.  it only takes a few more seconds to reconnect.

    */
}