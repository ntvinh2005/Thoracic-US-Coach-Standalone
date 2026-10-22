using UnityEngine;
using UnityEditor;
using SMMARTS;


    [CustomEditor(typeof(RecordingManager))]
    public class RecordingManager_Editor : Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            RecordingManager manager = (RecordingManager)target;

            GUILayout.Space(10);
            GUILayout.Label("Recording Controls", EditorStyles.boldLabel);

            if (!manager.IsRecording)
            {
                if (GUILayout.Button("Start Recording"))
                    manager.StartRecording();
            }
            else
            {
                if (!manager.IsPaused)
                {
                    if (GUILayout.Button("Pause Recording"))
                        manager.PauseRecording();
                }
                else
                {
                    if (GUILayout.Button("Resume Recording"))
                        manager.ResumeRecording();
                }

                if (GUILayout.Button("Stop Recording"))
                    manager.StopRecording();

                if (GUILayout.Button("Save Record"))
                    manager.SaveRecord();
            }
        }
    }
