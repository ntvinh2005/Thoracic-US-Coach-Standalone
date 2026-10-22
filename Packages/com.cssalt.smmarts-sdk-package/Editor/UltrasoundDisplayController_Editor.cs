using UnityEngine;
using UnityEditor;
using SMMARTS;

/// <summary>
/// Custom inspector for the UltrasoundDisplayController.
///
/// Sidedness and screen size are live runtime state, and they are deliberately NOT serialized on the controller.
/// A second serialized copy of an authored setting is exactly what used to drift out of sync: Start() applies
/// beginWithDotOnRight and beginInSmallScreen over the runtime values every play, so a serialized checkbox would
/// show a value that had already been overwritten. This inspector reads the live properties instead, so what you
/// see during play is always the truth, and it adds no serialized data of its own.
/// </summary>
[CustomEditor(typeof(UltrasoundDisplayController))]
public class UltrasoundDisplayController_Editor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        UltrasoundDisplayController controller = (UltrasoundDisplayController)target;

        GUILayout.Space(10);
        GUILayout.Label("Live State", EditorStyles.boldLabel);

        if (!Application.isPlaying)
        {
            EditorGUILayout.HelpBox(
                "Sidedness and screen size are runtime state, so there is nothing to show until you press Play.\n\n" +
                "To set what they start as, use Begin With Dot On Right and Begin In Small Screen above.",
                MessageType.Info);
            return;
        }

        EditorGUILayout.LabelField("Orientation dot",
            controller.DotOnRight ? "RIGHT   (reversed image - cardiology, thyroid bx)" : "LEFT   (default - anesthesiology, vascular access)");
        EditorGUILayout.LabelField("Screen size", controller.UltrasoundScreenSize.ToString());

        GUILayout.Space(6);
        GUILayout.Label("Runtime Controls", EditorStyles.boldLabel);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Dot Left")) controller.DotOnRight = false;
            if (GUILayout.Button("Dot Right")) controller.DotOnRight = true;
            if (GUILayout.Button("Toggle")) controller.ReverseImage();
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Small Screen")) controller.FullscreenOff();
            if (GUILayout.Button("Full Screen")) controller.FullscreenOn();
        }

        // These values change from gameplay, not from inspector events, so keep repainting while we are in play mode.
        Repaint();
    }
}
