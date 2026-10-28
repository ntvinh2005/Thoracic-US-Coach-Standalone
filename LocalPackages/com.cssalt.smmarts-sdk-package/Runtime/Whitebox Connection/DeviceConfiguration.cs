using UnityEngine;

[CreateAssetMenu(fileName = "New Device Config", menuName = "SMMARTS/Device Configuration")]
public class DeviceConfiguration : ScriptableObject
{
    [Header("Device Identification")]
    public string deviceName;
    public string handshakeCommand;  // "-" for standard, "=" for IV trainer, etc.
    public string expectedResponse = ">";

    [Header("Data Structure")]
    public SensorDefinition[] sensors;
    public CommandDefinition[] commands;

    [System.Serializable]
    public class SensorDefinition
    {
        public string name;
        public string displayName;
        public int dataIndex;
    }

    [System.Serializable]
    public class CommandDefinition
    {
        public string name;
        public string displayName;
        public string[] arduinoCommands;  // Can send multiple commands
    }

}