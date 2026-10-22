using UnityEngine;

public class Replayer_UI_Toggle : MonoBehaviour
{
    public bool ShowReplayerUI = false;

    public Canvas ReplayerAndSessionSaverCanvas;


    /// <summary>
    /// Handles keyboard input for toggling the replayer interface.
    /// Left Shift + R activates/deactivates the secret panel.
    /// </summary>
    void Update()
    {
        if (Input.GetKey(KeyCode.LeftShift) && Input.GetKeyDown(KeyCode.R))
            ShowReplayerUI = !ShowReplayerUI;

        ReplayerAndSessionSaverCanvas.enabled = ShowReplayerUI;

    }

}
