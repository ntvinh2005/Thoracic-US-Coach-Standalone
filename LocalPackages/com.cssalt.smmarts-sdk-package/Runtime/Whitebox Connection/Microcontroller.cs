/// Final sign off from:
/// Andre Kazimierz Bigos
///	2020.01.08
///	YYYY.MM.DD
///	14:04 EST (24h)
///	Goodbye CSSALT
using UnityEngine;
using System;
#if !UNITY_WEBGL
using System.IO.Ports;
using System.Threading;
#endif
#if UNITY_EDITOR
using UnityEditor;
#endif

/// <summary>
/// The Microcontroller is contained within the SMARTS_SDK namespace.
/// </summary>
namespace SMMARTS
{
    /// <summary>
    /// 
    /// Class Overview:
    /// The purpose of this script is to provide simple bi-directional communication between the physical microcontroller and the associated software trying
    /// to communicate with it. The Microcontroller class establishes a link between any available external microcontroller connected through USB to the
    /// machine operating this script. The connected microcontroller can either be found dynamically through an attempted connection to all open COM port, 
    /// or, if desired, to a predefined COM port. A handshake sequence assures the proper establishment of the connection to a CSSALT provided WhiteBox.
    /// New modifications enable simple transition between a normal operation mode, and a replay formatted mode. When in the replay mode, this script takes
    /// saved Microcontroller data it's given, converts it to usable data, and runs on that data as if it were operating in the exact same state as when
    /// that data was recorded. Even in replay mode, the connection (and if not connected, connection attempts) are maintained and run in the background on
    /// a separate thread.
    ///	
    /// </summary>
    public class Microcontroller : MonoBehaviour
    {
        [Header("----- SMMARTS SDK V03.9.15.25 -----", order = 0)]

        /// This static Microcontroller is used to ensure a single instance of the microcontroller exists in the scene and is allowed to communicate with 
        /// the physical microcontroller. In awake we destroy all other possible instances.
        public static Microcontroller ME;


        [Header("----- Microcontroller Status -----", order = 1)]

        /// This boolean indicates whether or not the microcontroller is currently connected, or, if in replay mode, whether or not the microcontroller was
        /// connected during that moment within the replay.
        [SerializeField]
        bool connected = false;
        public bool Connected { get { return connected; } }

        /// More specific than the "connected" boolean, this field defines if the system is not connected, connected, or more importantly, if it is busy
        /// establishing a connection. This field is also dependent on the state of the microcontroller. In replay mode, this field reflects the state of
        /// the microcontroller during the time of the recorded frame being replayed.
        [SerializeField]
        Connection_Status currentConnectionStatus = Connection_Status.Not_Connected;
        public Connection_Status CurrentConnectionStatus { get { return currentConnectionStatus; } }


        [Header("----- Microcontroller Communication Status -----", order = 2)]

        /// A boolean that indicates whether or not the physical microcontroller is connected. When in replay, the "connected" field is updated based on
        /// data input during a given replay. This value however always displays the active state of the microcontroller connection and can be true even
        /// when the "connected" field is false during a replay.
        [SerializeField]
        bool microcontrollerConnected = false;

        /// As with the microcontrollerConnected field, this connection status refers solely to the microcontroller connection, independent of any replay
        /// data.
        [SerializeField]
        Connection_Status currentMicrocontrollerConnectionStatus = Connection_Status.Not_Connected;


        [Header("----- Microcontroller COM Port -----", order = 3)]

        /// By default the Microcontroller will cycle through each COM port before it finds a compatible white box microcontroller to connect to. If the
        /// user would like to specify which COM port to attempt to connect to, they may do so using this field.
        [SerializeField]
        string specifyComPortNumber = "";
        public string SpecifyComPortNumber { get { return specifyComPortNumber; } set { specifyComPortNumber = value; } }

        /// This field indicates the current connected microcontroller COM port. For example, if connected to COM5, returns "COM5." When not connected to a
        /// COM port, return "N/A-No Connection."
        [SerializeField]
        string currentConnectedPort = "N/A-No Connection";
        public string CurrentConnectedPort { get { return currentConnectedPort; } }

#if !UNITY_WEBGL
        /// The microcontroller connection is maintained on a separate thread, so as to not slow down the system. This is the thread the connection is
        /// maintained on.
        Thread microcontrollerCommunicator;

        /// The microcontrollerPort is the current connected SerialPort i.e. COM5.
        SerialPort microcontrollerPort;
#endif


        [Header("----- Microcontroller Input/Output -----", order = 4)]

        /// The currentInputMode field can be changed to allow different functionality. When set to "Replay" mode, the user can input previously recorded
        /// raw microcontroller input, and have it be parsed to replicated the state of the microcontroller at the time the data was recorded. When in the
        /// "Microcontroller" mode, the microcontroller operates normally and displays the microcontroller's connection statuses and data.
        [SerializeField]
        InputMode currentInputMode = InputMode.Microcontroller;
        public InputMode CurrentInputMode { get { return currentInputMode; } set { currentInputMode = value; } }

        /// When in Replay mode, this data field can be used by the user to replay previously stored microcontroller inputs. Any data given through this
        /// field will be parsed an replayed during then next update. 
        [SerializeField]
        string replayDataInput = "";
        public string ReplayDataInput { get { return replayDataInput; } set { replayDataInput = value; } }

        /// When in normal operation mode, the raw input from the white box microcontroller is accessible as a correctly formatted string. This string,
        /// when returned to the microcontroller in replay mode, will be used to replicate the microcontroller's state at the time of the recording. The
        /// data string is much easier and faster to store than taking individual parsed inputs and storing them, and ensures easy usability. This data
        /// string is inaccessible when in replay mode as it would not make sense to use.
        [SerializeField]
        string replayFormattedMicrocontrollerInputString = "MICROCONTROLLER DATA|MCNC";
        public string ReplayFormattedMicrocontrollerInputString
        {
            get
            {
                if (currentInputMode != InputMode.Microcontroller)
                    throw new UnauthorizedAccessException("Microcontroller data can only be accessed when the CurrentInputMode is set to \"Microcontroller\".");
                return replayFormattedMicrocontrollerInputString;
            }
        }

        /// This string array represents the microcontroller output. It is split using commas and displayed to the user as an array of data. The user
        /// should know what each data string is based on their microcontroller output.
        [SerializeField]
        string[] microcontrollerData = { };
        public string[] MicrocontrollerData { get { return microcontrollerData; } }


        [Header("----- Device Configuration -----", order = 5)]
        [SerializeField]
        DeviceConfiguration currentDeviceConfig;
        public DeviceConfiguration CurrentDeviceConfig { get { return currentDeviceConfig; } }

        [SerializeField]
        string detectedDeviceType = "Standard (SMMARTSMC)";
        public string DetectedDeviceType { get { return detectedDeviceType; } }


        [Header("----- Debugging Options -----", order = 6)]

        /// Because Debug.Log() messages can be annoying to deal with, and disruptive when developing other parts of the software, but very useful when
        /// having issues with the microcontroller, this debugging toggle dictates to the software when to give the user input, and when to abstain.
        /// This toggle enables and disables messages about connection events (which port it's connected to, when it's trying to reconnect, etc.). It, 
        /// however, does not enable messages about critical errors and caught exceptions. That's the job of the errorDebuggingActive toggle.
        [SerializeField]
        bool debuggingActive = true;
        public bool DebuggingActive { get { return debuggingActive; } set { debuggingActive = value; } }

        /// The errorDebuggingActive toggle displays critical errors. Instead of using Debug.Log(), when active, this toggle enables the program to use
        /// Debug.LogError() messages displaying information about connection failures, parsed data failures, etc. Because this methodology uses
        /// Debug.LogError()s, if a project is built with this toggle active, as a Development Build, these errors will be written to the console and to
        /// the error log, and is very useful when debugging executables.
        [SerializeField]
        bool errorDebuggingActive = false;
        public bool ErrorDebuggingActive { get { return errorDebuggingActive; } set { errorDebuggingActive = value; } }

        // ------------------------------------------------------------------
        // Persistent‑log helpers (no change to the public API)
        // ------------------------------------------------------------------
        /// True the first time a connection is established during this run.
        /// If it stays false the whole auto‑detect routine failed.
        bool detectionSucceeded = false;               // becomes true on first successful handshake
        /// In‑memory buffer that gathers every diagnostic line while the
        /// connection routine is running. It is flushed to disk only on failure.
        readonly System.Text.StringBuilder logBuilder = new System.Text.StringBuilder();
        /// Full path of the log file. Application.persistentDataPath works on
        /// every Unity platform (Windows → %APPDATA%\LocalLow\<Company>\<Product>).
        string logFilePath;

        /// The rawMicrocontrollerDataInput string is used internally to build the input string from the microcontroller byte by byte. It seems to cause
        /// less timeout exceptions when the microcontroller data is read in byte by byte and not using "ReadLine()."
        string rawMicrocontrollerDataInput = "";

        /// The stored commands long int is used to store user commands using bitwise logic and operations.
        Int64 storedCommands = 0;

        /// Because it is incredibly important to send and receive microcontroller input and output separately, and non-simultaneously, we ensure the
        /// segregation of these signals by closely monitoring when we send and receive signals using this field. The current cycle is modified only
        /// upon completion of send and receive cycles making it impossible to send data before data is fully received and vise versa. An issue with
        /// data loss between the microcontroller and the computer had been documented and known for some time. It was not until 2017 during the course
        /// of developing the Wet Tap that the problem's source was identified (Andre Kazimierz Bigos). Since then the problem has been remedied.
        /// Changes were made to both the arduino code and the microcontroller code to prevent the problem from re-surfacing. This communication
        /// segregation is extremely important to maintain.
        Cycle currentCycle = Cycle.Not_Set;

        /// The first frame in unity tends to hang for some time (generally upwards of 500ms), presumably because of all the work done initializing 
        /// in the Awake() and Start() methods. This causes issues when initially connecting to the microcontroller as the microcontroller will assume
        /// disconnection has occurred (it has an internal 500ms timeout). This firstFrame boolean avoids the double connection attempt from occurring
        /// by not attempting the first connection until after an arbitrary 2000ms wait from the first update being called.
        bool firstFrame = true;

        /// The input mode enum is a public enum that user's can use to change the input mode. Currently, there are only two input modes: Microcontroller
        /// and Replay_Data. In the future, as touch interface usability increases, a third Touch_Interface input mode might possibly be added.
        public enum InputMode
        {
            Microcontroller,
            Replay_Data,
            //Touch_Interface, //Going to be unused placeholder
            //Standby
        }

        /// The cycle enum is internally used to differentiate between sending and receiving data to and from the microcontroller and is 
        /// crucial to ensuring the signals do not overlap.
        enum Cycle
        {
            Not_Set,
            Send,
            Sent,
            Receive,
            Received
        }

        /// This enum of microcontroller commands is specially formatted to enable the simple use of bitwise operations. The SendUserCommands() method
        /// highlights the usefulness of this type of enum. A simple | function can be used to queue user commands and an & can be used to read whether
        /// the command was sent by the user. This is extremely efficient and very powerful.
        public enum MicrocontrollerCommand : Int64
        {
            Microcontroller_Command_A = 1,
            Microcontroller_Command_B = 2,
            Microcontroller_Command_C = 4,
            Microcontroller_Command_D = 8,
            Microcontroller_Command_E = 16,
            Microcontroller_Command_F = 32,
            Microcontroller_Command_G = 64,
            Microcontroller_Command_H = 128,
            Microcontroller_Command_I = 256,
            Microcontroller_Command_J = 512,
            Microcontroller_Command_K = 1024,
            Microcontroller_Command_L = 2048,
            Microcontroller_Command_M = 4096,
            Microcontroller_Command_N = 8192,
            Microcontroller_Command_O = 16384,
            Microcontroller_Command_P = 32768,
            Microcontroller_Command_Q = 65536,
            Microcontroller_Command_R = 131072,
            Microcontroller_Command_S = 262144,
            Microcontroller_Command_T = 524288,
            Microcontroller_Command_U = 1048576,
            Microcontroller_Command_V = 2097152,
            Microcontroller_Command_W = 4194304,
            Microcontroller_Command_X = 8388608,
            Microcontroller_Command_Y = 16777216,
            Microcontroller_Command_Z = 33554432,
            Microcontroller_Command_0 = 67108864,
            Microcontroller_Command_1 = 134217728,
            Microcontroller_Command_2 = 268435456,
            Microcontroller_Command_3 = 536870912,
            Microcontroller_Command_4 = 1073741824,
            Microcontroller_Command_5 = 2147483648,
            Microcontroller_Command_6 = 4294967296,
            Microcontroller_Command_7 = 8589934592,
            Microcontroller_Command_8 = 17179869184,
            Microcontroller_Command_9 = 34359738368
        }

        /// The connection status is the enum used to display the current state of the microcontroller connection routine. A status of "Connected" implies
        /// that a white box microcontroller is currently connected and functioning properly. A status of "Establishing_Connection" implies that the program
        /// is either performing the initial connection, or any subsequent reconnection attempt. A status of "Not_Connected" implies the program is not
        /// currently connected. Either by choice or because of an error in connecting.
        public enum Connection_Status
        {
            Connected,
            Establishing_Connection,
            Not_Connected
        }

        /// <summary>
        /// Here we create the static Microcontroller connection. All other instances are destroyed, and only one instance will exist in the scene. 
        /// We load the device configuration from Resources, falling back to standard configuration if none is found. Then we begin the microcontroller 
        /// connection routine.
        /// </summary>
        private void Awake()
        {
            if (ME != null)
                Destroy(ME);
            ME = this;

            // Load device configuration
            currentDeviceConfig = Resources.Load<DeviceConfiguration>("DeviceConfiguration");

            if (currentDeviceConfig != null)
            {
                if (debuggingActive)
                    Debug.Log("Loaded custom device configuration: " + currentDeviceConfig.deviceName);
                detectedDeviceType = currentDeviceConfig.deviceName;
            }
            else
            {
                // Use built-in standard config
                currentDeviceConfig = CreateStandardDeviceConfig();
                detectedDeviceType = "Standard (SMMARTSMC)";
                //if (debuggingActive) Debug.Log("Using default device configuration");
            }

            logFilePath = System.IO.Path.Combine(
                Application.persistentDataPath, "Microcontroller.log");
            ConnectToMicrocontroller();
        }

        /// <summary>
        /// Here we close any open ports, and begin the microcontroller connection routine. It runs on a new thread.
        ///
        /// On WebGL there are no COM ports and no background threads, so this reports a permanent
        /// "not connected" state instead. Everything downstream already handles that state, which is
        /// the same one a desktop build reaches when no white box is plugged in.
        /// </summary>
        void ConnectToMicrocontroller()
        {
#if !UNITY_WEBGL
            try { microcontrollerPort.Close(); } catch (Exception) { }
            try { microcontrollerPort.Dispose(); } catch (Exception) { }
            microcontrollerCommunicator = new Thread(MicrocontrollerCommunicator);
            microcontrollerCommunicator.Start();
#else
            microcontrollerConnected = false;
            currentMicrocontrollerConnectionStatus = Connection_Status.Not_Connected;
            replayFormattedMicrocontrollerInputString = "MICROCONTROLLER DATA|MCNC";
            if (currentInputMode == InputMode.Microcontroller)
            {
                connected = microcontrollerConnected;
                currentConnectionStatus = currentMicrocontrollerConnectionStatus;
                microcontrollerData = new string[0];
            }
            currentConnectedPort = "N/A-No Connection";
            currentCycle = Cycle.Not_Set;
#endif
        }

#if !UNITY_WEBGL

        /// <summary>
        /// The MicrocontrollerCommunicator() is the heart of the microcontroller class. This is where the connection between the physical microcontroller
        /// and this software is established and maintained.
        /// </summary>
        void MicrocontrollerCommunicator()
        {
            // if (debuggingActive) Debug.Log("ATTEMPTING MICROCONTROLLER CONNECTION ROUTINE");
            microcontrollerConnected = false;
            currentMicrocontrollerConnectionStatus = Connection_Status.Establishing_Connection;
            replayFormattedMicrocontrollerInputString = "MICROCONTROLLER DATA|MCEC";
            if (currentInputMode == InputMode.Microcontroller) { connected = microcontrollerConnected; currentConnectionStatus = currentMicrocontrollerConnectionStatus; microcontrollerData = new string[0]; }
            if (firstFrame)
                Thread.Sleep(2000);

            /// This is where the connection is established through a handshake.
            try
            {
                /// If the user has not specified a COM port number (i.e. 3,5,6,8,etc.), we try to establish a connection to every available port until
                /// a port with a microcontroller is found and a connection can be established.
                if (String.IsNullOrEmpty(specifyComPortNumber))
                {
                    string[] ports = SerialPort.GetPortNames();
                    foreach (string portName in ports)
                    {
                        if (debuggingActive)
                            Debug.Log("ATTEMPTING TO CONNECT TO PORT: " + portName);
                        AppendLog("ATTEMPTING TO CONNECT TO PORT: " + portName);
                        AttemptPortOpening(portName);
                        if (microcontrollerPort != null)
                            break;
                    }
                    if (microcontrollerPort != null && microcontrollerPort.IsOpen)
                    {
                        if (debuggingActive)
                            Debug.Log("CONNECTED TO PORT: " + microcontrollerPort.PortName);
                        AppendLog("CONNECTED TO PORT: " + microcontrollerPort.PortName);
                        microcontrollerConnected = true;
                        detectionSucceeded = true;
                    }
                }
                /// If the user has specified a COM port to connect to, we attempt a connection to that COM port, and that COM port alone.
                else
                {
                    string portName = "COM" + specifyComPortNumber;
                    if (debuggingActive)
                        Debug.Log("ATTEMPTING TO CONNECT TO PORT: " + portName);
                    AppendLog("ATTEMPTING TO CONNECT TO PORT: " + portName);
                    AttemptPortOpening(portName);
                    if (microcontrollerPort != null && microcontrollerPort.IsOpen)
                    {
                        microcontrollerConnected = true;
                        if (debuggingActive)
                            Debug.Log("CONNECTED TO PORT: " + microcontrollerPort.PortName);
                        AppendLog("CONNECTED TO PORT: " + microcontrollerPort.PortName);
                        detectionSucceeded = true;
                    }
                }
            }
            catch (Exception e)
            {
                if (errorDebuggingActive)
                {
                    Debug.LogError("SMARTS-SDK ERROR CODE: MC001\n" + e);
                }
                AppendLog($"SMARTS‑SDK ERROR CODE: MC001 – {e.GetType().Name}: {e.Message}", isError: true);
                Abort();
            }

            /// If a microcontroller connection is established, we mark the connection as successful and switch the microcontrollerConnected,
            /// and optionally, the connected field, to true.
            if (microcontrollerConnected)
            {
                currentMicrocontrollerConnectionStatus = Connection_Status.Connected;
                replayFormattedMicrocontrollerInputString = "MICROCONTROLLER DATA|MCC";
                if (currentInputMode == InputMode.Microcontroller) { connected = microcontrollerConnected; currentConnectionStatus = currentMicrocontrollerConnectionStatus; }
            }
            try
            {
                if (microcontrollerConnected && microcontrollerPort.IsOpen)
                    currentConnectedPort = microcontrollerPort.PortName;
            }
            catch (Exception e)
            {
                if (errorDebuggingActive)
                {
                    Debug.LogError("SMARTS-SDK ERROR CODE: MC002\n" + e);
                }
                AppendLog($"SMARTS‑SDK ERROR CODE: MC002 – {e.GetType().Name}: {e.Message}", isError: true);
                Abort();
            }
            currentCycle = Cycle.Received;

            /// After a connection has been established, this loop maintains the connection. Here we determine which cycle we're in.
            /// During a send cycle, we send user commands. During a receive cycle, we read in the microcontroller input.
            while (true)
            {
                try
                {
                    if (microcontrollerPort == null || !microcontrollerPort.IsOpen)
                        break;
                    if (currentCycle == Cycle.Send)
                    {
                        if (!SendUserCommands())
                        {
                            break;
                        }
                    }
                    else if (currentCycle == Cycle.Receive)
                    {
                        if (!ReceiveMicrocontrollerInput())
                        {
                            break;
                        }
                    }
                }
                catch (Exception e)
                {
                    if (errorDebuggingActive)
                    {
                        Debug.LogError("SMARTS-SDK ERROR CODE: MC003\n" + e);
                    }
                    AppendLog($"SMARTS‑SDK ERROR CODE: MC003 – {e.GetType().Name}: {e.Message}", isError: true);
                    break;
                }
            }
            Abort();
        }
#endif

        /// <summary>
        /// The Abort() method terminates the current microcontroller connection routine. This entails closing and disposing all open ports, marking
        /// the connection status as "Not_Connected," and ensuring all other needed data is reset. This method can be called from within the connection
        /// routine (for example if no microcontroller connection is ever established) or as a result of the user manually aborting the connection 
        /// (by disabling the component).
        /// </summary>
        /// </summary>
        void Abort()
        {
            // if (debuggingActive) Debug.Log("MICROCONTROLLER CONNECTION ABORTED");
            AppendLog("MICROCONTROLLER CONNECTION ABORTED", isError: true);
            microcontrollerConnected = false;
            currentMicrocontrollerConnectionStatus = Connection_Status.Not_Connected;
            replayFormattedMicrocontrollerInputString = "MICROCONTROLLER DATA|MCNC";
            if (currentInputMode == InputMode.Microcontroller) { connected = microcontrollerConnected; currentConnectionStatus = currentMicrocontrollerConnectionStatus; microcontrollerData = new string[0]; }
            currentConnectedPort = "N/A-No Connection";
            currentCycle = Cycle.Not_Set;
#if !UNITY_WEBGL
            try { microcontrollerPort.Close(); } catch (Exception) { }
            try { microcontrollerPort.Dispose(); } catch (Exception) { }
#endif
            // --------------------------------------------------------------
            //  Write the log to disk **only** if the auto‑detect never succeeded.
            // --------------------------------------------------------------
            if (!detectionSucceeded && logBuilder.Length > 0)
            {
                try
                {
                    // Insert a clear separator so successive runs are easy to spot
                    logBuilder.Insert(0, "\n----- NEW RUN -----\n");
                    System.IO.File.AppendAllText(logFilePath, logBuilder.ToString());

                    // Let the developer know where the file landed
                    AppendLog($"WROTE MICROCONTROLLER DIAGNOSTIC LOG TO {logFilePath}", isError: true);
                }
                catch (Exception e)
                {
                    // Even the file write failed – at least we can see it in the console.
                    if (errorDebuggingActive)
                        Debug.LogError($"Failed to write microcontroller log: {e}");
                }
            }

            // Reset for a possible later reconnect attempt in the same session
            detectionSucceeded = false;
            logBuilder.Clear();
        }

        /// <summary>
        /// This method is used by the connection routine to attempt COM port opening. It attempts to open the given port, if successful, it
        /// uses the handshake command from the loaded device configuration. If the port is connected to the microcontroller, then the expected 
        /// response character should be returned. If this character is then read, the connection is determined to have been successful. 
        /// The input parameter is the port name to attempt connection with (i.e. COM5).
        /// </summary>
        /// <param name="portName">Name of the port to which a connection should be attempted.</param>
#if !UNITY_WEBGL
        void AttemptPortOpening(string portName)
        {
            try
            {
                microcontrollerPort = null;
                microcontrollerPort = new SerialPort(portName, 19200, Parity.None, 8, StopBits.One);
                microcontrollerPort.Open();
                microcontrollerPort.ReadTimeout = 150;

                // Use the handshake from the loaded configuration
                if (TryHandshake(currentDeviceConfig.handshakeCommand))
                {
                    if (microcontrollerPort.BytesToRead > 0)
                        microcontrollerPort.ReadExisting();
                }
                else
                {
                    microcontrollerPort.Close();
                    microcontrollerPort.Dispose();
                    microcontrollerPort = null;
                }
            }
            catch (Exception e) {
                AppendLog($"AttemptPortOpening failed on {portName}: {e.GetType().Name} – {e.Message}", isError: true);
            }
        }

        /// <summary>
        /// Attempts a handshake with the microcontroller using the specified command and expected response from the device configuration.
        /// </summary>
        /// <param name="handshakeCommand">The handshake command to send</param>
        /// <returns>True if handshake was successful, false otherwise</returns>
        bool TryHandshake(string handshakeCommand)
        {
            try
            {
                microcontrollerPort.Write(handshakeCommand);
                float timeout = 0;
                while (microcontrollerPort.IsOpen && timeout < 150)
                {
                    if (microcontrollerPort.BytesToRead > 0)
                    {
                        byte response = (byte)microcontrollerPort.ReadByte();
                        // Use expected response from configuration, defaulting to '>' if not set
                        char expectedResponse = string.IsNullOrEmpty(currentDeviceConfig.expectedResponse) ? '>' : currentDeviceConfig.expectedResponse[0];
                        if (response == expectedResponse)
                            return true;
                    }
                    Thread.Sleep(1); // Add this line
                    timeout++;
                }
            }
            catch (Exception e) {
                AppendLog($"Handshake exception on {microcontrollerPort?.PortName}: {e.GetType().Name} – {e.Message}", isError: true);
            }
            return false;
        }
#endif

        /// <summary>
        /// Built-in configuration for existing SMMARTSMC devices to maintain backward compatibility.
        /// </summary>
        /// <returns>A DeviceConfiguration with standard SMMARTSMC settings</returns>
        DeviceConfiguration CreateStandardDeviceConfig()
        {
            DeviceConfiguration config = ScriptableObject.CreateInstance<DeviceConfiguration>();
            config.deviceName = "Standard (SMMARTSMC)";
            config.handshakeCommand = "-";
            config.expectedResponse = ">";

            // Define the standard 4-sensor configuration
            config.sensors = new DeviceConfiguration.SensorDefinition[]
            {
                new DeviceConfiguration.SensorDefinition
                {
                    name = "TUIButton",
                    displayName = "TUI Button",
                    dataIndex = 0,
                },
                new DeviceConfiguration.SensorDefinition
                {
                    name = "ProbeRidgeSide",
                    displayName = "Probe FSR (Ridge Side)",
                    dataIndex = 1,
                },
                new DeviceConfiguration.SensorDefinition
                {
                    name = "ProbeFlatSide",
                    displayName = "Probe FSR (Flat Side)",
                    dataIndex = 2,
                },
                new DeviceConfiguration.SensorDefinition
                {
                    name = "SyringePressure",
                    displayName = "Syringe Pressure",
                    dataIndex = 3,
                }
            };

            return config;
        }

        /// <summary>
        /// This method is used by users to send commands to the microcontroller. Instead of queuing things in a list<>, or having an array of
        /// booleans to determine whether a command has been received, the technique uses bitwise operations to modify an initially 0 value
        /// int64 (00000000000000000000000000000000) with each enum sent flipping the corresponding 0 to a 1 using the | operator. As an example,
        /// the value of Microcontroller_Command_J is 512 or 00000000000000000000001000000000.
        /// 
        /// 00000000000000000000000000 | 00000000000000000001000000000 = 00000000000000000001000000000
        /// 
        /// Therefore, each command the user sends gets added (but not removed from) the storedCommands int64. This storedCommands int64 is then later
        /// used in conjunction with the & operator to determine which commands to send to the physical microcontroller.
        /// 
        /// </summary>
        /// <param name="command">The command the user wants to send to the microcontroller.</param>
        public void SendCommand(MicrocontrollerCommand command)
        {
            storedCommands |= (Int64)command;
        }

        /// <summary>
        /// Once the microcontroller connection routine hits the "Send" cycle, this method is called and sends all the data to the microcontroller.
        /// It cycles through each microcontroller command and sends any command that had been flagged in the SendCommand method by the user since the
        /// last send. It also writes the "*" character to the port to signal the microcontroller that data can be sent back from the white box
        /// microcontroller. The cycle is then set to "Sent" to mark the completion of the data send.
        /// The method returns "true" if all the writing was completed successfully and no exceptions were thrown in the process. Should any errors have
        /// occurred during this data send, the return value will be "false" notifying the connection routine that there is a problem with the port 
        /// (likely a disconnection). The connection routine will then know to re-attempt the connection routine.
        /// </summary>
        /// <returns>True if successfully executed, false otherwise.</returns>
#if !UNITY_WEBGL
        bool SendUserCommands()
        {
            try
            {
                if ((storedCommands & (Int64)MicrocontrollerCommand.Microcontroller_Command_A) == (Int64)MicrocontrollerCommand.Microcontroller_Command_A) microcontrollerPort.Write("A");
                if ((storedCommands & (Int64)MicrocontrollerCommand.Microcontroller_Command_B) == (Int64)MicrocontrollerCommand.Microcontroller_Command_B) microcontrollerPort.Write("B");
                if ((storedCommands & (Int64)MicrocontrollerCommand.Microcontroller_Command_C) == (Int64)MicrocontrollerCommand.Microcontroller_Command_C) microcontrollerPort.Write("C");
                if ((storedCommands & (Int64)MicrocontrollerCommand.Microcontroller_Command_D) == (Int64)MicrocontrollerCommand.Microcontroller_Command_D) microcontrollerPort.Write("D");
                if ((storedCommands & (Int64)MicrocontrollerCommand.Microcontroller_Command_E) == (Int64)MicrocontrollerCommand.Microcontroller_Command_E) microcontrollerPort.Write("E");
                if ((storedCommands & (Int64)MicrocontrollerCommand.Microcontroller_Command_F) == (Int64)MicrocontrollerCommand.Microcontroller_Command_F) microcontrollerPort.Write("F");
                if ((storedCommands & (Int64)MicrocontrollerCommand.Microcontroller_Command_G) == (Int64)MicrocontrollerCommand.Microcontroller_Command_G) microcontrollerPort.Write("G");
                if ((storedCommands & (Int64)MicrocontrollerCommand.Microcontroller_Command_H) == (Int64)MicrocontrollerCommand.Microcontroller_Command_H) microcontrollerPort.Write("H");
                if ((storedCommands & (Int64)MicrocontrollerCommand.Microcontroller_Command_I) == (Int64)MicrocontrollerCommand.Microcontroller_Command_I) microcontrollerPort.Write("I");
                if ((storedCommands & (Int64)MicrocontrollerCommand.Microcontroller_Command_J) == (Int64)MicrocontrollerCommand.Microcontroller_Command_J) microcontrollerPort.Write("J");
                if ((storedCommands & (Int64)MicrocontrollerCommand.Microcontroller_Command_K) == (Int64)MicrocontrollerCommand.Microcontroller_Command_K) microcontrollerPort.Write("K");
                if ((storedCommands & (Int64)MicrocontrollerCommand.Microcontroller_Command_L) == (Int64)MicrocontrollerCommand.Microcontroller_Command_L) microcontrollerPort.Write("L");
                if ((storedCommands & (Int64)MicrocontrollerCommand.Microcontroller_Command_M) == (Int64)MicrocontrollerCommand.Microcontroller_Command_M) microcontrollerPort.Write("M");
                if ((storedCommands & (Int64)MicrocontrollerCommand.Microcontroller_Command_N) == (Int64)MicrocontrollerCommand.Microcontroller_Command_N) microcontrollerPort.Write("N");
                if ((storedCommands & (Int64)MicrocontrollerCommand.Microcontroller_Command_O) == (Int64)MicrocontrollerCommand.Microcontroller_Command_O) microcontrollerPort.Write("O");
                if ((storedCommands & (Int64)MicrocontrollerCommand.Microcontroller_Command_P) == (Int64)MicrocontrollerCommand.Microcontroller_Command_P) microcontrollerPort.Write("P");
                if ((storedCommands & (Int64)MicrocontrollerCommand.Microcontroller_Command_Q) == (Int64)MicrocontrollerCommand.Microcontroller_Command_Q) microcontrollerPort.Write("Q");
                if ((storedCommands & (Int64)MicrocontrollerCommand.Microcontroller_Command_R) == (Int64)MicrocontrollerCommand.Microcontroller_Command_R) microcontrollerPort.Write("R");
                if ((storedCommands & (Int64)MicrocontrollerCommand.Microcontroller_Command_S) == (Int64)MicrocontrollerCommand.Microcontroller_Command_S) microcontrollerPort.Write("S");
                if ((storedCommands & (Int64)MicrocontrollerCommand.Microcontroller_Command_T) == (Int64)MicrocontrollerCommand.Microcontroller_Command_T) microcontrollerPort.Write("T");
                if ((storedCommands & (Int64)MicrocontrollerCommand.Microcontroller_Command_U) == (Int64)MicrocontrollerCommand.Microcontroller_Command_U) microcontrollerPort.Write("U");
                if ((storedCommands & (Int64)MicrocontrollerCommand.Microcontroller_Command_V) == (Int64)MicrocontrollerCommand.Microcontroller_Command_V) microcontrollerPort.Write("V");
                if ((storedCommands & (Int64)MicrocontrollerCommand.Microcontroller_Command_W) == (Int64)MicrocontrollerCommand.Microcontroller_Command_W) microcontrollerPort.Write("W");
                if ((storedCommands & (Int64)MicrocontrollerCommand.Microcontroller_Command_X) == (Int64)MicrocontrollerCommand.Microcontroller_Command_X) microcontrollerPort.Write("X");
                if ((storedCommands & (Int64)MicrocontrollerCommand.Microcontroller_Command_Y) == (Int64)MicrocontrollerCommand.Microcontroller_Command_Y) microcontrollerPort.Write("Y");
                if ((storedCommands & (Int64)MicrocontrollerCommand.Microcontroller_Command_Z) == (Int64)MicrocontrollerCommand.Microcontroller_Command_Z) microcontrollerPort.Write("Z");
                if ((storedCommands & (Int64)MicrocontrollerCommand.Microcontroller_Command_0) == (Int64)MicrocontrollerCommand.Microcontroller_Command_0) microcontrollerPort.Write("0");
                if ((storedCommands & (Int64)MicrocontrollerCommand.Microcontroller_Command_1) == (Int64)MicrocontrollerCommand.Microcontroller_Command_1) microcontrollerPort.Write("1");
                if ((storedCommands & (Int64)MicrocontrollerCommand.Microcontroller_Command_2) == (Int64)MicrocontrollerCommand.Microcontroller_Command_2) microcontrollerPort.Write("2");
                if ((storedCommands & (Int64)MicrocontrollerCommand.Microcontroller_Command_3) == (Int64)MicrocontrollerCommand.Microcontroller_Command_3) microcontrollerPort.Write("3");
                if ((storedCommands & (Int64)MicrocontrollerCommand.Microcontroller_Command_4) == (Int64)MicrocontrollerCommand.Microcontroller_Command_4) microcontrollerPort.Write("4");
                if ((storedCommands & (Int64)MicrocontrollerCommand.Microcontroller_Command_5) == (Int64)MicrocontrollerCommand.Microcontroller_Command_5) microcontrollerPort.Write("5");
                if ((storedCommands & (Int64)MicrocontrollerCommand.Microcontroller_Command_6) == (Int64)MicrocontrollerCommand.Microcontroller_Command_6) microcontrollerPort.Write("6");
                if ((storedCommands & (Int64)MicrocontrollerCommand.Microcontroller_Command_7) == (Int64)MicrocontrollerCommand.Microcontroller_Command_7) microcontrollerPort.Write("7");
                if ((storedCommands & (Int64)MicrocontrollerCommand.Microcontroller_Command_8) == (Int64)MicrocontrollerCommand.Microcontroller_Command_8) microcontrollerPort.Write("8");
                if ((storedCommands & (Int64)MicrocontrollerCommand.Microcontroller_Command_9) == (Int64)MicrocontrollerCommand.Microcontroller_Command_9) microcontrollerPort.Write("9");
                microcontrollerPort.Write("*");
            }
            catch (Exception e)
            {
                if (errorDebuggingActive)
                {
                    Debug.LogError("SMARTS-SDK ERROR CODE: MC004\n" + e);
                }
                AppendLog($"SMARTS‑SDK ERROR CODE: MC004 – {e.GetType().Name}: {e.Message}", isError: true);
                return false;
            }
            currentCycle = Cycle.Sent;
            storedCommands = 0;
            return true;
        }

        /// <summary>
        /// This method is called by the microcontroller connection routine whenever it has cycled to the "Receive" cycle. This method attempts to read
        /// in microcontroller input. Issues seemed to arise with timeout exceptions using the ReadLine() method of the SerialPort. These issues seemed
        /// to have been minimized or completely removed by reading in any data byte by byte. The rationale for this is beyond me, and it could have
        /// been an external issue the fixing of which coincided with the change from using ReadLine() to ReadByte(). This issue should perhaps be
        /// explored further.
        /// 
        /// Should the reading be successful, the data is then split using the "," character into the string array microcontrollerData. The pre-slit 
        /// raw microcontroller data is re-formatted and exposed to the user through the replayFormattedMicrocontrollerInputString variable to be used
        /// in record storage.
        /// 
        /// Should the reading and splitting all be successful, the cycle will be set to "received" and the boolean value "true" will be returned to the
        /// connection routine to ensure it continues properly to the next send routine. Should the reading fail, the return value will be "false" and
        /// the connection routine will know to attempt reconnection.
        /// </summary>
        /// <returns>True if read succeeded, false otherwise.</returns>
        bool ReceiveMicrocontrollerInput()
        {
            try
            {
                rawMicrocontrollerDataInput = "";
                byte readByte;
                while (true)
                {
                    readByte = (byte)microcontrollerPort.ReadByte();
                    if (readByte == '$')

                    {
                        rawMicrocontrollerDataInput += (char)readByte;
                        while (true)
                        {
                            readByte = (byte)microcontrollerPort.ReadByte();
                            rawMicrocontrollerDataInput += (char)readByte;
                            if (readByte == '#')
                                break;
                        }
                        break;
                    }
                }
                rawMicrocontrollerDataInput = rawMicrocontrollerDataInput.Trim();
                if (!String.IsNullOrEmpty(rawMicrocontrollerDataInput) && (rawMicrocontrollerDataInput.Substring(0, 1)).CompareTo("$") == 0 &&
                (rawMicrocontrollerDataInput.Substring(rawMicrocontrollerDataInput.Length - 1)).CompareTo("#") == 0)
                {
                    replayFormattedMicrocontrollerInputString = "MICROCONTROLLER DATA|MCC|" + rawMicrocontrollerDataInput;
                    rawMicrocontrollerDataInput = rawMicrocontrollerDataInput.Substring(1, rawMicrocontrollerDataInput.Length - 2);
                    if (currentInputMode == InputMode.Microcontroller)
                    {
                        string[] SplitArray = rawMicrocontrollerDataInput.Split(',');
                        microcontrollerData = SplitArray;
                    }
                }
                else
                {
                    replayFormattedMicrocontrollerInputString = "MICROCONTROLLER DATA|MCNC";
                    if (debuggingActive)
                        Debug.Log("IMPROPER MICROCONTROLLER DATA DETECTED: " + rawMicrocontrollerDataInput + "\nMICRONCONTROLLER DATA SHOULD FOLLOW THE FORMAT: $xxxx,yyyy,zzzz,aaaa,bbbb#");
                    return false;
                }
            }
            catch (Exception e)
            {
                if (errorDebuggingActive)
                {
                    Debug.LogError("SMARTS-SDK ERROR CODE: MC005\n" + e);
                }
                AppendLog($"SMARTS‑SDK ERROR CODE: MC005 – {e.GetType().Name}: {e.Message}", isError: true);
                return false;
            }
            currentCycle = Cycle.Received;
            return true;
        }
#endif

        /// <summary>
        /// When in replay mode, user inputted, previously stored microcontroller data gets parsed every update. This method analyzes the user's
        /// provided string, and uses it to reformat the output of this microcontroller to reflect the state the microcontroller was at during the
        /// time the data was recorded. This means splitting the stored microcontroller input to display the correct microcontroller data, but also 
        /// means replicating the connection state of the stored data. The fields "connected" and "currentConnectionStatus" both reflect their
        /// respective states at the time that data string was recorded.
        /// </summary>
        void ParseReplayData()
        {
            try
            {
                replayDataInput = replayDataInput.Trim();
                if (!replayDataInput.Substring(0, 23).Equals("MICROCONTROLLER DATA|MC"))
                {
                    if (errorDebuggingActive)
                    {
                        Debug.LogError("IMPROPER REPLAY DATA STRING DETECTED: " + replayDataInput);
                    }
                    else if (debuggingActive)
                    {
                        Debug.Log("IMPROPER REPLAY DATA STRING DETECTED: " + replayDataInput);
                    }
                    AppendLog($"IMPROPER REPLAY DATA STRING DETECTED: {replayDataInput}", isError: true);
                    connected = false;
                    currentConnectionStatus = Connection_Status.Not_Connected;
                    microcontrollerData = new string[0];
                    return;
                }
                string[] splitReplayData = replayDataInput.Split('|');
                if (splitReplayData.Length == 2)
                {
                    connected = false;
                    microcontrollerData = new string[0];
                    if (splitReplayData[1].Equals("MCEC"))
                        currentConnectionStatus = Connection_Status.Establishing_Connection;
                    else
                        currentConnectionStatus = Connection_Status.Not_Connected;
                }
                else
                {
                    connected = true;
                    currentConnectionStatus = Connection_Status.Connected;
                    string formattedReplayDataInput = splitReplayData[2];
                    if (!String.IsNullOrEmpty(formattedReplayDataInput) && (formattedReplayDataInput.Substring(0, 1)).CompareTo("$") == 0 &&
                    (formattedReplayDataInput.Substring(formattedReplayDataInput.Length - 1)).CompareTo("#") == 0)
                    {
                        formattedReplayDataInput = formattedReplayDataInput.Substring(1, formattedReplayDataInput.Length - 2);
                        string[] SplitArray = formattedReplayDataInput.Split(',');
                        microcontrollerData = SplitArray;
                    }
                }
            }
            catch (Exception e)
            {
                if (errorDebuggingActive)
                {
                    Debug.LogError("REPLAY DATA PARSING ERROR OCCURED: mc006\n" + e);
                }
                AppendLog($"REPLAY DATA PARSING ERROR OCCURED: mc006 – {e.GetType().Name}: {e.Message}", isError: true);
                currentConnectionStatus = Connection_Status.Not_Connected;
                connected = false;
                microcontrollerData = new string[0];
            }
        }

        /// <summary>
        /// Within update, if the microcontroller has become disconnected (and it shouldn't be) we restart the connection routine.
        /// Also, we mark send and receive cycles if data has been successfully sent or received.
        /// If in replay mode, we parse current user inputted replay data.
        /// </summary>
        private void Update()
        {
            firstFrame = false;
#if !UNITY_WEBGL
            if (!microcontrollerConnected && currentMicrocontrollerConnectionStatus != Connection_Status.Establishing_Connection)
            {
                ConnectToMicrocontroller();
            }
#endif
            if (microcontrollerConnected)
            {
                if (currentCycle == Cycle.Received)
                {
                    currentCycle = Cycle.Send;
                }
                if (currentCycle == Cycle.Sent)
                {
                    currentCycle = Cycle.Receive;
                }
            }
            if (currentInputMode == InputMode.Replay_Data)
            {
                ParseReplayData();
            }

        }

        /// <summary>
        /// In late update, to increase the speed at which cycles get toggled, we also set cycles after successful sending and receiving.
        /// </summary>
        private void LateUpdate()
        {
            if (microcontrollerConnected)
            {
                if (currentCycle == Cycle.Received)
                {
                    currentCycle = Cycle.Send;
                }
                if (currentCycle == Cycle.Sent)
                {
                    currentCycle = Cycle.Receive;
                }
            }
        }

        /// <summary>
        /// If the application is quit, we abort the connection routine. This ensures that the microcontroller thread is killed.
        /// </summary>
        private void OnApplicationQuit()
        {
            Abort();
#if !UNITY_WEBGL
            try { if (microcontrollerCommunicator.IsAlive) microcontrollerCommunicator.Abort(); } catch (Exception) { }
#endif
        }

        // ------------------------------------------------------------------
        //  Centralised logging used by the connection routine.
        // ------------------------------------------------------------------
        /// <summary>
        /// Writes a line to the Unity console (respecting the existing debug flags)
        /// **and** stores the line in an in‑memory StringBuilder so it can later be
        /// flushed to a persistent file.
        /// </summary>
        /// <param name="message">The text you want to record.</param>
        /// <param name="isError">Set true for red‑error style messages.</param>
        void AppendLog(string message, bool isError = false)
        {

            // Save the line for the persistent log file
            string prefix = isError ? "[ERR] " : "[INF] ";
            string line = $"{System.DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {prefix}{message}";
            logBuilder.AppendLine(line);
        }
    }

}