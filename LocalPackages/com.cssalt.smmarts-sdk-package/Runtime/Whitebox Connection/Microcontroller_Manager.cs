using UnityEngine;
using System.Collections.Generic;

namespace SMMARTS
{
    public class Microcontroller_Manager : MonoBehaviour
    {
        public static Microcontroller_Manager ME;

        private bool useReplayData = false;


        private void Awake()
        {
            if (ME != null)
                Destroy(ME);
            ME = this;
        }

        [Header("------Device Information (Display Only)------")]
        [SerializeField]
        string currentDeviceType = "Not Connected";


        void Start() 
        {
            // one null check for optional replayer.
            useReplayData = (MicrocontrollerReplayer.ME != null);
        }



        public string CurrentDeviceType
        {
            get
            {
                if (Microcontroller.ME.Connected && Microcontroller.ME.CurrentDeviceConfig != null)
                    return Microcontroller.ME.CurrentDeviceConfig.deviceName;
                return "Not Connected";
            }
        }

        [Header("------Sensor Values (Display Only)------")]
        [SerializeField]
        Dictionary<string, float> sensorValues = new Dictionary<string, float>();

        // Legacy properties for backward compatibility
        [SerializeField] bool tuiPressed = false;
        [SerializeField] float probeFaceFSR_RidgeSide = 0;
        [SerializeField] float probeFaceFSR_FlatSide = 0;
        [SerializeField] float syringePressure = 0;

        // Legacy public properties (keep existing projects working)
        // Legacy public properties (updated for fallback compatibility)
        public bool TUIPressed
        {
            get
            {
                // Check replayer first if available
                if (useReplayData && MicrocontrollerReplayer.ME.IsReplaying)
                {
                    if (MicrocontrollerReplayer.ME.HasSensorData("TUIButton"))
                    {
                        return (MicrocontrollerReplayer.ME.GetReplayValue("TUIButton") == 1);
                    }
                    else
                    {
                        Debug.Log("No replay data for TUIButton - using live data");
                    }
                }

                // Try dynamic config first, fall back to legacy array access
                if (Microcontroller.ME.CurrentDeviceConfig != null)
                    return GetSensorValue("TUIButton") > 0;

                // Legacy fallback - direct array access
                if (Microcontroller.ME.Connected && Microcontroller.ME.MicrocontrollerData.Length > 0)
                    return int.Parse(Microcontroller.ME.MicrocontrollerData[0]) > 0;

                return false;
            }
        }

        public float ProbeFaceFSR_RidgeSide
        {
            get
            {
                // Check replayer first if available
                if (useReplayData && MicrocontrollerReplayer.ME.IsReplaying)
                {
                    if (MicrocontrollerReplayer.ME.HasSensorData("ProbeRidgeSide"))
                    {
                        return MicrocontrollerReplayer.ME.GetReplayValue("ProbeRidgeSide");
                    }
                    else
                    {
                        Debug.Log("No replay data for ProbeRidgeSide - using live data");
                    }
                }

                if (Microcontroller.ME.CurrentDeviceConfig != null)
                    return GetSensorValue("ProbeRidgeSide");

                // Legacy fallback
                if (Microcontroller.ME.Connected && Microcontroller.ME.MicrocontrollerData.Length > 1)
                    return float.Parse(Microcontroller.ME.MicrocontrollerData[1]);

                return 0;
            }
        }

        public float ProbeFaceFSR_FlatSide
        {
            get
            {
                // Check replayer first if available
                if (useReplayData && MicrocontrollerReplayer.ME.IsReplaying)
                {
                    if (MicrocontrollerReplayer.ME.HasSensorData("ProbeFlatSide"))
                    {
                        return MicrocontrollerReplayer.ME.GetReplayValue("ProbeFlatSide");
                    }
                    else
                    {
                        Debug.Log("No replay data for ProbeFlatSide - using live data");
                    }
                }

                if (Microcontroller.ME.CurrentDeviceConfig != null)
                    return GetSensorValue("ProbeFlatSide");

                // Legacy fallback
                if (Microcontroller.ME.Connected && Microcontroller.ME.MicrocontrollerData.Length > 2)
                    return float.Parse(Microcontroller.ME.MicrocontrollerData[2]);

                return 0;
            }
        }

        public float SyringePressure
        {
            get
            {
                // Check replayer first if available
                if (useReplayData && MicrocontrollerReplayer.ME.IsReplaying)
                {
                    if (MicrocontrollerReplayer.ME.HasSensorData("SyringePressure"))
                    {
                        return MicrocontrollerReplayer.ME.GetReplayValue("SyringePressure");
                    }
                    else
                    {
                        Debug.Log("No replay data for SyringePressure - using live data");
                    }
                }

                if (Microcontroller.ME.CurrentDeviceConfig != null)
                    return GetSensorValue("SyringePressure");

                // Legacy fallback
                if (Microcontroller.ME.Connected && Microcontroller.ME.MicrocontrollerData.Length > 3)
                    return float.Parse(Microcontroller.ME.MicrocontrollerData[3]);

                return 0;
            }
        }
        /// --------------- END OF LEGACY CALLS


        // New dynamic sensor access
        public float GetSensorValue(string sensorName)
        {
            if (!Microcontroller.ME.Connected || Microcontroller.ME.CurrentDeviceConfig == null)
                return 0;

            var config = Microcontroller.ME.CurrentDeviceConfig;
            var sensor = System.Array.Find(config.sensors, s => s.name == sensorName);

            if (sensor != null && Microcontroller.ME.MicrocontrollerData.Length > sensor.dataIndex)
            {
                if (float.TryParse(Microcontroller.ME.MicrocontrollerData[sensor.dataIndex], out float value))
                {
                    return value;
                }
            }
            return 0;
        }

        // Get all available sensor names for current device
        public string[] GetAvailableSensors()
        {
            if (Microcontroller.ME.CurrentDeviceConfig == null)
                return new string[0];

            var config = Microcontroller.ME.CurrentDeviceConfig;
            string[] names = new string[config.sensors.Length];
            for (int i = 0; i < config.sensors.Length; i++)
                names[i] = config.sensors[i].name;
            return names;
        }

        private void Update()
        {
            // Update legacy display values for inspector
            tuiPressed = TUIPressed;
            probeFaceFSR_RidgeSide = ProbeFaceFSR_RidgeSide;
            probeFaceFSR_FlatSide = ProbeFaceFSR_FlatSide;
            syringePressure = SyringePressure;
            currentDeviceType = CurrentDeviceType;

            // Update dynamic sensor dictionary for debugging
            sensorValues.Clear();
            if (Microcontroller.ME.Connected && Microcontroller.ME.CurrentDeviceConfig != null)
            {
                foreach (var sensor in Microcontroller.ME.CurrentDeviceConfig.sensors)
                {
                    sensorValues[sensor.displayName] = GetSensorValue(sensor.name);
                }
            }
        }

        // Enhanced command system (backward compatible)
        public enum COMMAND
        {
            // Legacy commands (keep exactly the same)
            ZeroSyringePressure,
            ResetSyringe,
            LightsOff,
            BlueLight,
            RedLight,
            LORC,
            LORO,
            LPop,
            SPop
        }

        // Legacy SendCommand method (unchanged for compatibility)
        public void SendCommand(COMMAND command)
        {
            if (command == COMMAND.ZeroSyringePressure)
                Microcontroller.ME.SendCommand(Microcontroller.MicrocontrollerCommand.Microcontroller_Command_P);
            else if (command == COMMAND.ResetSyringe)
            {
                Microcontroller.ME.SendCommand(Microcontroller.MicrocontrollerCommand.Microcontroller_Command_9);
                Microcontroller.ME.SendCommand(Microcontroller.MicrocontrollerCommand.Microcontroller_Command_3);
            }
            else if (command == COMMAND.LightsOff)
                Microcontroller.ME.SendCommand(Microcontroller.MicrocontrollerCommand.Microcontroller_Command_9);
            else if (command == COMMAND.BlueLight)
                Microcontroller.ME.SendCommand(Microcontroller.MicrocontrollerCommand.Microcontroller_Command_7);
            else if (command == COMMAND.RedLight)
                Microcontroller.ME.SendCommand(Microcontroller.MicrocontrollerCommand.Microcontroller_Command_8);
            else if (command == COMMAND.LORC)
                Microcontroller.ME.SendCommand(Microcontroller.MicrocontrollerCommand.Microcontroller_Command_4);
            else if (command == COMMAND.LORO)
                Microcontroller.ME.SendCommand(Microcontroller.MicrocontrollerCommand.Microcontroller_Command_3);
            else if (command == COMMAND.SPop)
                Microcontroller.ME.SendCommand(Microcontroller.MicrocontrollerCommand.Microcontroller_Command_2);
            else if (command == COMMAND.LPop)
                Microcontroller.ME.SendCommand(Microcontroller.MicrocontrollerCommand.Microcontroller_Command_1);
        }

        // New dynamic command system
        public void SendDeviceCommand(string commandName)
        {
            if (Microcontroller.ME.CurrentDeviceConfig == null) return;

            var command = System.Array.Find(Microcontroller.ME.CurrentDeviceConfig.commands, c => c.name == commandName);
            if (command != null)
            {
                foreach (string arduinoCmd in command.arduinoCommands)
                {
                    // Convert string command to enum and send
                    if (System.Enum.TryParse($"Microcontroller_Command_{arduinoCmd}", out Microcontroller.MicrocontrollerCommand cmd))
                        Microcontroller.ME.SendCommand(cmd);
                }
            }
        }

        // Get available commands for current device
        public string[] GetAvailableCommands()
        {
            if (Microcontroller.ME.CurrentDeviceConfig == null)
                return new string[0];

            var config = Microcontroller.ME.CurrentDeviceConfig;
            string[] names = new string[config.commands.Length];
            for (int i = 0; i < config.commands.Length; i++)
                names[i] = config.commands[i].name;
            return names;
        }
    }
}