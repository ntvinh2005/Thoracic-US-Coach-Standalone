using UnityEngine;
using SMMARTS;
using System.Collections.Generic;

/// <summary>
/// Panel for Advanced Users with Shift+M.
/// Enhanced runtime debugging interface for monitoring and controlling microcontroller hardware.
/// Now supports dynamic device detection and configuration-driven sensor/command display.
/// Activated with Left Shift + M. Designed for developers who need to troubleshoot
/// SMMARTS hardware connectivity and functionality across multiple device types.
/// </summary>
public class MicrocontrollerPanel : MonoBehaviour
{
    public bool showSettings = false;
    public float GUI_ScaleFactorAdjust = 1f;
    public float scaleFactor;

    // UI state management
    private Vector2 sensorScrollPosition = Vector2.zero;
    private Vector2 commandScrollPosition = Vector2.zero;
    private bool showRawData = true;
    private bool showAllSensors = true;

    // custom microcontroller configurations get special attention, set by this internal flag
    private bool CustomLayout = false;

    /// <summary>
    /// Handles keyboard input for toggling the microcontroller debugging interface.
    /// Left Shift + M activates/deactivates the debugging panel.
    /// </summary>
    void Update()
    {
        if (Input.GetKey(KeyCode.LeftShift) && Input.GetKeyDown(KeyCode.M))
            showSettings = !showSettings;
    }

    /// <summary>
    /// Renders the enhanced microcontroller debugging GUI with dynamic device support.
    /// Automatically adapts to show sensors and commands available for the currently connected device.
    /// </summary>
    void OnGUI()
    {
        if (!showSettings) return;

        // if no device config file is found, microcontroller.cs generates its own, named "Standard (SMMARTSMC)"
        CustomLayout = (Microcontroller.ME.DetectedDeviceType != "Standard (SMMARTSMC)");

        // DPI Scaling System
        scaleFactor = Screen.dpi / 96f * GUI_ScaleFactorAdjust;
        GUI.matrix = Matrix4x4.Scale(new Vector3(scaleFactor, scaleFactor, 1));

        // Panel sizing - adaptive based on device complexity
        float panelWidth = (Screen.width * 0.8f) / scaleFactor;
        float panelHeight = (Screen.height * 0.95f) / scaleFactor;

        // Center the panel
        float x = (Screen.width - panelWidth * scaleFactor) / 2 / scaleFactor;
        float y = (Screen.height - panelHeight * scaleFactor) / 2 / scaleFactor;

        // Create styled background
        GUIStyle boxStyle = new GUIStyle(GUI.skin.box);
        boxStyle.normal.background = MakeTex(2, 2, new Color(0f, 0f, 0f, 0.90f));

        GUI.Box(new Rect(x, y, panelWidth, panelHeight), "", boxStyle);
        GUILayout.BeginArea(new Rect(x + 10, y + 10, panelWidth - 20, panelHeight - 20));

        // Enhanced header with device information
        GUILayout.Label($"MICROCONTROLLER PANEL - {GetDeviceDisplayName()}",
                       new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold });

        // Three-column layout for better organization
        GUILayout.BeginHorizontal();

        // Left Column - Connection and Device Info
        DrawConnectionColumn(panelWidth);

        // Middle Column - Dynamic Sensor Data
        DrawSensorColumn(panelWidth);

        // Right Column - Dynamic Command Controls
        DrawCommandColumn(panelWidth);

        GUILayout.EndHorizontal();
        GUILayout.EndArea();
    }

    private void DrawConnectionColumn(float panelWidth)
    {
        GUILayout.BeginVertical(GUILayout.Width((panelWidth - 40) * 0.33f));

        // Device Information
        GUILayout.Label("--- Device Information ---", GetHeaderStyle());
        GUILayout.Label($"Device Type: {GetDeviceDisplayName()}");
        GUILayout.Label($"Connected: {Microcontroller.ME.Connected}");
        GUILayout.Label($"Status: {Microcontroller.ME.CurrentConnectionStatus}");
        GUILayout.Label($"Port: {Microcontroller.ME.CurrentConnectedPort}");

        // Connection Control
        GUILayout.Space(10);
        GUILayout.Label("--- Connection Control ---", GetHeaderStyle());

        // Fix: Remove the non-existent DisableMicrocontrollerConnection toggle
        // OR add a local disable flag if needed

        GUILayout.Label("COM Port (empty = auto):");
        Microcontroller.ME.SpecifyComPortNumber =
            GUILayout.TextField(Microcontroller.ME.SpecifyComPortNumber);

        // Debug Controls
        GUILayout.Space(10);
        GUILayout.Label("--- Debug Controls ---", GetHeaderStyle());
        Microcontroller.ME.DebuggingActive =
            GUILayout.Toggle(Microcontroller.ME.DebuggingActive, "Debug Messages");
        Microcontroller.ME.ErrorDebuggingActive =
            GUILayout.Toggle(Microcontroller.ME.ErrorDebuggingActive, "Error Messages");

        // Display toggles
        GUILayout.Space(10);
        GUILayout.Label("--- Display Options ---", GetHeaderStyle());
        showRawData = GUILayout.Toggle(showRawData, "Show Raw Data");
        showAllSensors = GUILayout.Toggle(showAllSensors, "Show All Sensors");

        // Raw data section (if enabled)
        if (showRawData)
        {
            GUILayout.Space(10);
            GUILayout.Label("--- Raw Serial Data ---", GetHeaderStyle());
            GUILayout.TextArea(Microcontroller.ME.ReplayFormattedMicrocontrollerInputString,
                             GUILayout.Height(80));
        }

        if (CustomLayout) ShowDeviceSpecificInfo();

        GUILayout.EndVertical();
    }

    // Fix 2: Make sensor units dynamic based on device configuration
    private void DrawSensorColumn(float panelWidth)
    {
        GUILayout.BeginVertical(GUILayout.Width((panelWidth - 40) * 0.33f));
        GUILayout.Label("--- Sensor Data ---", GetHeaderStyle());

        // Always show legacy sensors for backward compatibility
      //  GUILayout.Label("Standard Sensors:", GetSubHeaderStyle());
      //  GUILayout.Label($"TUI Button: {Microcontroller_Manager.ME.TUIPressed}");
      //  GUILayout.Label($"Probe FSR (Ridge): {Microcontroller_Manager.ME.ProbeFaceFSR_RidgeSide:F2}");
      //  GUILayout.Label($"Probe FSR (Flat): {Microcontroller_Manager.ME.ProbeFaceFSR_FlatSide:F2}");
      //  GUILayout.Label($"Syringe Pressure: {Microcontroller_Manager.ME.SyringePressure:F2}");

        // Dynamic device-specific sensors with proper units
        if (showAllSensors && Microcontroller.ME.CurrentDeviceConfig != null)
        {
            GUILayout.Space(10);
            GUILayout.Label($"{GetDeviceDisplayName()} Sensors:", GetSubHeaderStyle());
            sensorScrollPosition = GUILayout.BeginScrollView(sensorScrollPosition, GUILayout.Height(200));

            // In the backdoor script DrawSensorColumn method:
            foreach (var sensor in Microcontroller.ME.CurrentDeviceConfig.sensors)
            {
                float value = Microcontroller_Manager.ME.GetSensorValue(sensor.name);
                string displayText = $"{sensor.displayName}: {value:F0}";  // Just raw values, no units

                GUILayout.Label(displayText);
            }

            GUILayout.EndScrollView();
        }
        GUILayout.EndVertical();
    }

    // Fix 3: Enable and improve the dynamic commands section
    private void DrawCommandColumn(float panelWidth)
    {
        GUILayout.BeginVertical(GUILayout.Width((panelWidth - 40) * 0.33f));
        GUILayout.Label("--- Hardware Control ---", GetHeaderStyle());

        if (CustomLayout == false)
        {
            // Legacy commands (always available for backward compatibility)
            GUILayout.Label("Standard Commands:", GetSubHeaderStyle());
            if (GUILayout.Button("Blue Light"))
                Microcontroller_Manager.ME.SendCommand(Microcontroller_Manager.COMMAND.BlueLight);
            if (GUILayout.Button("Red Light"))
                Microcontroller_Manager.ME.SendCommand(Microcontroller_Manager.COMMAND.RedLight);
            if (GUILayout.Button("Lights Off"))
                Microcontroller_Manager.ME.SendCommand(Microcontroller_Manager.COMMAND.LightsOff);

            GUILayout.Space(5);
            if (GUILayout.Button("Open Valve (LORO)"))
                Microcontroller_Manager.ME.SendCommand(Microcontroller_Manager.COMMAND.LORO);
            if (GUILayout.Button("Close Valve (LORC)"))
                Microcontroller_Manager.ME.SendCommand(Microcontroller_Manager.COMMAND.LORC);

            GUILayout.Space(5);
            if (GUILayout.Button("Large Pop"))
                Microcontroller_Manager.ME.SendCommand(Microcontroller_Manager.COMMAND.LPop);
            if (GUILayout.Button("Small Pop"))
                Microcontroller_Manager.ME.SendCommand(Microcontroller_Manager.COMMAND.SPop);

            GUILayout.Space(5);
            if (GUILayout.Button("Zero Pressure"))
                Microcontroller_Manager.ME.SendCommand(Microcontroller_Manager.COMMAND.ZeroSyringePressure);
            if (GUILayout.Button("Reset Syringe"))
                Microcontroller_Manager.ME.SendCommand(Microcontroller_Manager.COMMAND.ResetSyringe);
        }

        // FIXED: Enable dynamic device-specific commands
        if (CustomLayout)
        {
            if (Microcontroller.ME.CurrentDeviceConfig != null && Microcontroller.ME.CurrentDeviceConfig.commands.Length > 0)
            {
                GUILayout.Space(10);
                GUILayout.Label($"{GetDeviceDisplayName()} Commands:", GetSubHeaderStyle());
                commandScrollPosition = GUILayout.BeginScrollView(commandScrollPosition, GUILayout.Height(200));

                foreach (var command in Microcontroller.ME.CurrentDeviceConfig.commands)
                {
                    // Create button with proper styling based on command type
                    GUIStyle buttonStyle = GUI.skin.button;

                    if (GUILayout.Button(command.displayName, buttonStyle))
                    {
                        Microcontroller_Manager.ME.SendDeviceCommand(command.name);
                    }
                }
                GUILayout.EndScrollView();
            }
        }

        GUILayout.EndVertical();
    }

    private string GetDeviceDisplayName()
    {
        if (Microcontroller.ME.Connected && Microcontroller.ME.CurrentDeviceConfig != null)
            return Microcontroller.ME.CurrentDeviceConfig.deviceName;
        return "Not Connected";
    }

    // Fix 4: Add helper method for better device-specific features
    private void ShowDeviceSpecificInfo()
    {
        if (Microcontroller.ME.CurrentDeviceConfig == null) return;
        if (!CustomLayout) return;

        string deviceName = GetDeviceDisplayName();

        // Show device-specific information
        GUILayout.Space(10);
        GUILayout.Label("--- Device Details ---", GetHeaderStyle());
        GUILayout.Label($"Handshake: {Microcontroller.ME.CurrentDeviceConfig.handshakeCommand}");
        GUILayout.Label($"Expected Response: {Microcontroller.ME.CurrentDeviceConfig.expectedResponse}");
        GUILayout.Label($"Sensor Count: {Microcontroller.ME.CurrentDeviceConfig.sensors.Length}");
        GUILayout.Label($"Command Count: {Microcontroller.ME.CurrentDeviceConfig.commands.Length}");
    }


    private GUIStyle GetHeaderStyle()
    {
        return new GUIStyle(GUI.skin.label)
        {
            fontStyle = FontStyle.Bold,
            normal = { textColor = Color.yellow }
        };
    }

    private GUIStyle GetSubHeaderStyle()
    {
        return new GUIStyle(GUI.skin.label)
        {
            fontStyle = FontStyle.Bold,
            normal = { textColor = Color.cyan }
        };
    }

    /// <summary>
    /// Creates a solid color texture for GUI styling purposes.
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