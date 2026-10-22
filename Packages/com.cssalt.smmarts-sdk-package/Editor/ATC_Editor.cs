using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

namespace SMMARTS
{
    [CustomEditor(typeof(ATC))]
    class ATC_Editor : Editor
    {
        // IF we are using the TRANSMITTER for alignment, Write the system/antomical registration to the TRANSMITTER'S plug.
        // Unity reads this on startup, parses it, and applies it to GameObject BaseObject's transform.  
        // The anatomy should be a child of GameObject BaseObject.
        private string generateTransmitterLabel = '\n' + "BASE OBJECT: Generate a Gameobject Transform String" + '\n' + "in a Format Suitable for the Transmitter Plug EEPROM" + '\n';

        // Write the registration offsets of the needles/TUI/ultrasound probe to the sensor plugs.  
        // Ascention trakSTAR and driveBAY applies this before it gets to Unity.
        private string generateSensorLabel = '\n' + "SENSOR OBJECT: Generate an Alignment String" + '\n' + "in a Format Suitable for a Sensor Plug EEPROM" + '\n';

        private string uploadLabel = '\n' + "Upload Generated String to EEPROM" + '\n';

        /*SerializedProperty baseObject;
        SerializedProperty enableTrackingBaseObject;
        SerializedProperty trackedObject1;
        SerializedProperty enableTrackingObject1;
        SerializedProperty trackedObject2;
        SerializedProperty enableTrackingObject2;
        SerializedProperty trackedObject3;
        SerializedProperty enableTrackingObject3;
        SerializedProperty trackedObject4;
        SerializedProperty enableTrackingObject4;
        SerializedProperty disableATCConnectionRoutine;
        SerializedProperty currentInputMode;
        SerializedProperty replayDataInput;
        SerializedProperty replayFormattedATCInput;
        SerializedProperty connected;
        SerializedProperty connectionStatus;
        SerializedProperty errorReport;
        */
        SerializedProperty hemisphere;
        SerializedProperty transmitterRotation;
        SerializedProperty useModularStandPresetRotation;
        SerializedProperty usePresetsForSRT;
        SerializedProperty samplingFrequency;
        SerializedProperty applyCustomFilters;
        SerializedProperty ignoreLargeChanges;
        SerializedProperty AC_WideNotchFilter;
        SerializedProperty AC_NarrowNotchFilter;
        SerializedProperty DC_AdaptiveFilter;
        SerializedProperty VM;
        SerializedProperty alphaMin;
        SerializedProperty alphaMax;
        SerializedProperty editSensor;
        SerializedProperty offsetPosition;
        SerializedProperty offsetRotation;
        SerializedProperty utilities;
        SerializedProperty MemorySensorPlug1;
        SerializedProperty MemorySensorPlug2;
        SerializedProperty MemorySensorPlug3;
        SerializedProperty MemorySensorPlug4;
        SerializedProperty MemoryTransmitterPlug;
        SerializedProperty MemoryTrackingUnit;
        //SerializedProperty uploadTarget;
        SerializedProperty uploadText;
        //SerializedProperty debuggingActive;
        //SerializedProperty errorDebuggingActive;
        //SerializedProperty connected;
        //ATC atc;
        private void OnEnable()
        {
            hemisphere = serializedObject.FindProperty("hemisphere");
            transmitterRotation = serializedObject.FindProperty("transmitterRotation");
            useModularStandPresetRotation = serializedObject.FindProperty("useModularStandPresetRotation");
            usePresetsForSRT = serializedObject.FindProperty("usePresetsForSRT");
            samplingFrequency = serializedObject.FindProperty("samplingFrequency");
            applyCustomFilters = serializedObject.FindProperty("applyCustomFilters");
            ignoreLargeChanges = serializedObject.FindProperty("ignoreLargeChanges");
            AC_WideNotchFilter = serializedObject.FindProperty("ac_WideNotchFilter");
            AC_NarrowNotchFilter = serializedObject.FindProperty("ac_NarrowNotchFilter");
            DC_AdaptiveFilter = serializedObject.FindProperty("dc_AdaptiveFilter");
            VM = serializedObject.FindProperty("_Vm");
            alphaMin = serializedObject.FindProperty("alphaMin");
            alphaMax = serializedObject.FindProperty("alphaMax");
            editSensor = serializedObject.FindProperty("editSensor");
            offsetPosition = serializedObject.FindProperty("offsetPosition");
            offsetRotation = serializedObject.FindProperty("offsetRotation");
            utilities = serializedObject.FindProperty("utilities");
            MemorySensorPlug1 = serializedObject.FindProperty("MemorySensorPlug1");
            MemorySensorPlug2 = serializedObject.FindProperty("MemorySensorPlug2");
            MemorySensorPlug3 = serializedObject.FindProperty("MemorySensorPlug3");
            MemorySensorPlug4 = serializedObject.FindProperty("MemorySensorPlug4");
            MemoryTransmitterPlug = serializedObject.FindProperty("MemoryTransmitterPlug");
            MemoryTrackingUnit = serializedObject.FindProperty("MemoryTrackingUnit");
            //uploadTarget = serializedObject.FindProperty("uploadTarget");
            uploadText = serializedObject.FindProperty("uploadText");
            //debuggingActive = serializedObject.FindProperty("debuggingActive");
            //errorDebuggingActive = serializedObject.FindProperty("errorDebuggingActive");
        }
        public override void OnInspectorGUI()
        {

            serializedObject.Update();

            ATC atc = target as ATC;
            //atc.BaseObject = EditorGUILayout.ObjectField("Base Object",atc.BaseObject as UnityEngine.Object,atc.BaseObject.GetType()) as GameObject;
            EditorGUILayout.LabelField("---------- Base Object Locked to Transmitter ----------", EditorStyles.boldLabel);
            atc.BaseObject = EditorGUILayout.ObjectField("Base Object", atc.BaseObject as UnityEngine.Object, typeof(UnityEngine.Object), true) as GameObject;
            if (atc.CurrentInputMode != ATC.InputMode.Replay_Data)
                atc.EnableTrackingBaseObject = EditorGUILayout.Toggle("Enable Tracking Base Object", atc.EnableTrackingBaseObject);
            else
                EditorGUILayout.Toggle("Enable Tracking Base Object", atc.EnableTrackingBaseObject);


            EditorGUILayout.LabelField("---------- GameObject Tracked with Sensor Plug 1 ----------", EditorStyles.boldLabel);
            atc.TrackedObject1 = EditorGUILayout.ObjectField("Tracked Object 1", atc.TrackedObject1 as UnityEngine.Object, typeof(UnityEngine.Object), true) as GameObject;
            if (atc.CurrentInputMode != ATC.InputMode.Replay_Data)
                atc.EnableTrackingObject1 = EditorGUILayout.Toggle("Enable Tracking Object 1", atc.EnableTrackingObject1);
            else
                EditorGUILayout.Toggle("Enable Tracking Object 1", atc.EnableTrackingObject1);


            EditorGUILayout.LabelField("---------- GameObject Tracked with Sensor Plug 2 ----------", EditorStyles.boldLabel);
            atc.TrackedObject2 = EditorGUILayout.ObjectField("Tracked Object 2", atc.TrackedObject2 as UnityEngine.Object, typeof(UnityEngine.Object), true) as GameObject;
            if (atc.CurrentInputMode != ATC.InputMode.Replay_Data)
                atc.EnableTrackingObject2 = EditorGUILayout.Toggle("Enable Tracking Object 2", atc.EnableTrackingObject2);
            else
                EditorGUILayout.Toggle("Enable Tracking Object 2", atc.EnableTrackingObject2);


            EditorGUILayout.LabelField("---------- GameObject Tracked with Sensor Plug 3 ----------", EditorStyles.boldLabel);
            atc.TrackedObject3 = EditorGUILayout.ObjectField("Tracked Object 3", atc.TrackedObject3 as UnityEngine.Object, typeof(UnityEngine.Object), true) as GameObject;
            if (atc.CurrentInputMode != ATC.InputMode.Replay_Data)
                atc.EnableTrackingObject3 = EditorGUILayout.Toggle("Enable Tracking Object 3", atc.EnableTrackingObject3);
            else
                EditorGUILayout.Toggle("Enable Tracking Object 3", atc.EnableTrackingObject3);


            EditorGUILayout.LabelField("---------- GameObject Tracked with Sensor Plug 4 ----------", EditorStyles.boldLabel);
            atc.TrackedObject4 = EditorGUILayout.ObjectField("Tracked Object 4", atc.TrackedObject4 as UnityEngine.Object, typeof(UnityEngine.Object), true) as GameObject;
            if (atc.CurrentInputMode != ATC.InputMode.Replay_Data)
                atc.EnableTrackingObject4 = EditorGUILayout.Toggle("Enable Tracking Object 4", atc.EnableTrackingObject4);
            else
                EditorGUILayout.Toggle("Enable Tracking Object 4", atc.EnableTrackingObject4);


            EditorGUILayout.LabelField("---------- ATC Connection Options ----------", EditorStyles.boldLabel);
            atc.DisableATCConnectionRoutine = EditorGUILayout.Toggle("Disable ATC Connection Routine", atc.DisableATCConnectionRoutine);
            ATC.InputMode inputMode = (ATC.InputMode)EditorGUILayout.EnumPopup("Current Input Mode", atc.CurrentInputMode);
            if (atc.CurrentInputMode != inputMode)
                atc.CurrentInputMode = inputMode;


            EditorGUILayout.LabelField("---------- Replay Options ----------", EditorStyles.boldLabel);
            if (atc.CurrentInputMode == ATC.InputMode.Replay_Data)
            {
                string tempString = EditorGUILayout.TextField("Replay Data Input", atc.ReplayDataInput);
                if (tempString.Length != 0)
                {
                    atc.ReplayDataInput = tempString;
                }
            }
            else
            {
                EditorGUILayout.TextField("Replay Data Input", atc.ReplayDataInput);
            }
            EditorGUILayout.TextField("Replay Formatted ATC String", atc.ReplayFormattedATCInputString);

            if (atc.CurrentInputMode == ATC.InputMode.Replay_Data)
            {
                string tempString = EditorGUILayout.TextField("Utility Data Input", atc.ReplayUtilityInput);
                //if (tempString.Length != 0)
                //{
                //atc.ReplayUtilityInput = tempString;
                //}///FUNCTIONALITY NO LONGER SUPPORTED (NO NEED TO REPLAY)
            }
            else
            {
                EditorGUILayout.TextField("Replay Utility Input", atc.ReplayUtilityInput);
            }
            EditorGUILayout.TextField("Replay Formatted Utility String", atc.ReplayFormattedATCUtilities);


            EditorGUILayout.LabelField("---------- ATC Status ----------", EditorStyles.boldLabel);
            EditorGUILayout.Toggle("Connected", atc.Connected);
            EditorGUILayout.EnumPopup("Connection Status", atc.ConnectionStatus);


            EditorGUILayout.LabelField("---------- Transmitter Settings and Reference Frame ----------", EditorStyles.boldLabel);
            //atc.Hemisphere = (ATC.Zone)EditorGUILayout.EnumPopup("Hemisphere", atc.Hemisphere);
            EditorGUILayout.PropertyField(hemisphere, true);
            transmitterRotation.vector3Value = EditorGUILayout.Vector3Field("Transmitter Rotation", transmitterRotation.vector3Value);
            useModularStandPresetRotation.boolValue = EditorGUILayout.Toggle("Use Modular Stand Preset Rotation", useModularStandPresetRotation.boolValue);


            EditorGUILayout.LabelField("---------- Filter Settings (Applied to all Sensors) ----------", EditorStyles.boldLabel);
            usePresetsForSRT.boolValue = EditorGUILayout.Toggle("Use Presets for SRT", usePresetsForSRT.boolValue);
            samplingFrequency.intValue = EditorGUILayout.IntField("Sampling Frequency", samplingFrequency.intValue);
            applyCustomFilters.boolValue = EditorGUILayout.Toggle("Apply Custom Filters", applyCustomFilters.boolValue);
            ignoreLargeChanges.boolValue = EditorGUILayout.Toggle("Ignore Large Changes", ignoreLargeChanges.boolValue);
            AC_WideNotchFilter.boolValue = EditorGUILayout.Toggle("AC Wide Notch Filter", AC_WideNotchFilter.boolValue);
            AC_NarrowNotchFilter.boolValue = EditorGUILayout.Toggle("AC Narrow Notch Filter", AC_NarrowNotchFilter.boolValue);
            DC_AdaptiveFilter.boolValue = EditorGUILayout.Toggle("DC Adaptive Filter", DC_AdaptiveFilter.boolValue);
            EditorGUILayout.PropertyField(VM, true);
            VM.arraySize = 7;
            alphaMin.intValue = EditorGUILayout.IntField("Alpha Min", alphaMin.intValue);
            alphaMax.intValue = EditorGUILayout.IntField("Alpha Max", alphaMax.intValue);


            EditorGUILayout.LabelField("---------- Sensor Alignment Editing ----------", EditorStyles.boldLabel);
            //atc.EditSensor = (ATC.AlignSensor)EditorGUILayout.EnumPopup("Edit Sensor", atc.EditSensor);
            EditorGUILayout.PropertyField(editSensor, true);
            offsetPosition.vector3Value = EditorGUILayout.Vector3Field("Offset Position", offsetPosition.vector3Value);
            offsetRotation.vector3Value = EditorGUILayout.Vector3Field("Offset Rotation", offsetRotation.vector3Value);
            utilities.vector4Value = EditorGUILayout.Vector4Field("Utilities", utilities.vector4Value);


            EditorGUILayout.LabelField("---------- Currently Written on EEPROMs ----------", EditorStyles.boldLabel);
            EditorGUILayout.TextField("Memory Sensor Plug 1", atc.MemorySensorPlug1);
            EditorGUILayout.TextField("Memory Sensor Plug 2", atc.MemorySensorPlug2);
            EditorGUILayout.TextField("Memory Sensor Plug 3", atc.MemorySensorPlug3);
            EditorGUILayout.TextField("Memory Sensor Plug 4", atc.MemorySensorPlug4);
            EditorGUILayout.TextField("Memory Transmitter Plug", atc.MemoryTransmitterPlug);
            EditorGUILayout.TextField("Memory Tracking Unit", atc.MemoryTrackingUnit);


            EditorGUILayout.LabelField("---------- Edit EEPROMs ----------", EditorStyles.boldLabel);
            atc.UploadTarget = (ATC.EEPROM)EditorGUILayout.EnumPopup("Upload Target", atc.UploadTarget);
            uploadText.stringValue = EditorGUILayout.TextField("Upload Text", uploadText.stringValue);

            EditorGUILayout.LabelField("---------- Debugging Options ----------", EditorStyles.boldLabel);
            atc.DebuggingActive = EditorGUILayout.Toggle("Debugging Active", atc.DebuggingActive);
            atc.ErrorDebuggingActive = EditorGUILayout.Toggle("Error Debugging Active", atc.ErrorDebuggingActive);

            // OnInspectorGUI() overrides the default inspector panel; we want that, so explicity request it. 
            //base.OnInspectorGUI();
            GUILayout.Label(""); // blank space

            // Make an Alignment String for the Transmitter using the Base Object.
            if (GUILayout.Button(generateTransmitterLabel)) ATC.ME.GenerateTransmitterEEPROMAlignmentString();

            GUILayout.Label(""); // blank space

            // Make an Alignment String for a Sensor
            if (GUILayout.Button(generateSensorLabel)) ATC.ME.GenerateSensorEEPROMAlignmentString();

            GUILayout.Label(""); // blank space

            // Trigger an EEPROM upload
            if (GUILayout.Button(uploadLabel))
            {
                // If a target exists, ask to double check it through a dialog window.
                if (atc.UploadTarget != ATC.EEPROM.NONE)
                {
                    string alertBoxTitle = "Confirm EEPROM Upload to " + ATC.ME.UploadTarget + " ?";
                    //string uploadText = ATC.ME.UploadText;

                    if (uploadText.stringValue.Length >= 112)
                    {
                        int overLength = uploadText.stringValue.Length - 112;
                        alertBoxTitle = "Too Many Characters To Upload ";
                        uploadText.stringValue = "Delete " + overLength + " characters from " + uploadText;
                        if (EditorUtility.DisplayDialog(alertBoxTitle, uploadText.stringValue, "           OK           "))
                        {

                        }
                    }
                    else
                    {
                        if (EditorUtility.DisplayDialog(alertBoxTitle, uploadText.stringValue, "Make it so.", "      Cancel      "))
                        {
                            atc.UploadToEEPROM();
                        }
                    }


                }
            }
            GUILayout.Label(""); // blank space

            serializedObject.ApplyModifiedProperties();
        }
    }
}