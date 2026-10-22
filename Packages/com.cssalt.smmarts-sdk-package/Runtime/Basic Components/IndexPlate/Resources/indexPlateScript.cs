using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace SMMARTS
{
    public class indexPlateScript : MonoBehaviour, IBasePlate
    {
        // this script does a couple of things. 
        // 1) it tags the index plate for the CSSALT editor tools
        // The next items are here because they are core elements of SMMARTS.
        // Since the index plate is perhaps the MOST core element of a SMMARTS simulator, its a good place to put them:
        // no matter what you have in your SMMARTS simulator, it likely has an index plate. SO, 
        // 2) it has a CSSALT Logo. We humbly request you not forget to add our logo. We've included one here for you.
        // 3) it has that handy SHIFT>ESC keyboard code that we use to close all our simulators. You need not forget to add it to your simulation scripts.

        GameObject backmark; // Thats our CSSALT Logo. 
        GameObject canvas;   // thats the canvas for the logo

        // Use this for initialization
        void Start()
        {
            backmark = GameObject.Find("IndexPlate/Canvas/backmark"); // thats our CSSALT logo. 
            canvas = GameObject.Find("IndexPlate/Canvas");            // a canvas for our logo.
            if (backmark == null)
            {
                throw new Exception();
            }
            if (canvas == null)
            {
                throw new Exception();
            }
        }


        void Update()
        {
            // look for that close key combo - it listens for SHIFT-ESC keys being held down for .2 seconds
            CheckForExitCombo();


            // Honesly, this is here just to spams errors if someone accidently deleted our logo :)
            // Thanks for your understanding and support!
            if (backmark == null || !backmark.activeInHierarchy)
            {
                throw new Exception();
            }
            if (canvas == null || !canvas.activeInHierarchy)
            {
                throw new Exception();
            }



        }


        // this next part is for closing the sim if you press and hold SHIFT-ESC
        private float escapeHoldTime = 0f;
        private const float requiredHoldTime = 0.2f; // How long they need to hold the keys

        private void CheckForExitCombo()
        {
            // Check if SHIFT+ESC is being pressed
            if (Input.GetKey(KeyCode.LeftShift) && Input.GetKey(KeyCode.Escape))
            {
                escapeHoldTime += Time.deltaTime;

                // Optional: Show some feedback like "Hold to exit... (progress: X%)"

                // Check if held long enough
                if (escapeHoldTime >= requiredHoldTime)
                {
                    // Quit the application
#if UNITY_EDITOR
                    Debug.Log("Shift-ESC would close the application now.");
                    UnityEditor.EditorApplication.isPlaying = false;
#else
                    Application.Quit();
#endif
                }
            }
            else
            {
                // Reset the timer if keys are released
                escapeHoldTime = 0f;
            }
        }
    }
}
