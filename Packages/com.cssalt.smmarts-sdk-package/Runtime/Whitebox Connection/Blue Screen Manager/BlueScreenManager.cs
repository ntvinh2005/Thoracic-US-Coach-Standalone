using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using System;
using UnityEngine.EventSystems;

/// <summary>
// 
// The 'T' in 'SMMARTS' stands for 'Tracking', so if there is no Tracking we have a big problem. 
// This instantiates our little version of the "Blue Screen of Death" when the whitebox connection fails.
// Its from https://en.wikipedia.org/wiki/Blue_screen_of_death. Thankfully you don't see them as much these days.
// The cool thing about our BSOD is that you can almost always recover from it without restarting the sim!
// So our BSOD is more like a "Blue Screen of Delay" :)
// Thats because the whitebox failures are typically because there is some mundane USB or mains power plug issue, 
// and the tracking system and microcontroller codes simply poll to reconnect.
// Our BSOD asset is simply a message box that we turn on if we need to with some helpful message about how to fix the problem.
// Its not instantaneous because the DriveBAY takes a little less than 30 seconds to boot when you first try to connect to it,
// so we have to wait at least 30 seconds or we risk displaying a blue screen at startup when that delay is normal. 
// We make the "AllowableDisconnectionSeconds" variable because some projects write to EEPROM and that takes more time.
//
// We also instantiate an Event System if there isn't one already in the project somewhere.
//
/// </summary>
namespace SMMARTS
{
    public class BlueScreenManager : MonoBehaviour
    {
        [Header("-----Bluescreen Timeout-----", order = 0)]

        /// This static BlueScreenManager is used for other scripts to monitor or set expected delays from tracking system.
        /// It is easily accessible to outside scripts. In awake we destroy all other possible instances.
        public static BlueScreenManager ME;

        /// Rendering Ultrasound or not:
        /// This is how long its OK to wait for the system to boot. typically, its 30 seconds.
        /// if we don't hear from ATC or microcontroller after AllowableDisconnectionSeconds, something is wrong.
        [SerializeField]
        float AllowableDisconnectionSeconds = 30;

        GameObject blueScreen;
        GameObject titleGO, troubleshootGO, diagnosticGO, errorImageGO;
        GameObject continueButtonGO;


        public bool isSystemBooting = true;
        bool offlineMode = false;

        string titleText = "";
        string troubleshootText = "";
        string diagnosticText = "";
        Sprite errorImage;

        void Awake()
        {

            if (ME != null)
                Destroy(ME);
            ME = this;

            DontDestroyOnLoad(this);

        }

        // Use this for initialization
        void Start()
        {

            // Blue Screen Mgr will make an event system for your project if there isn't one already.
            // You need an one (and only one) EventSystem component somewhere in your project (preferably root)
            // in order for UI buttons to work. If there is one already present, we don't need to instantiate one.
            EventSystem eventSystem = FindAnyObjectByType<EventSystem>();
            if (eventSystem)
                Debug.Log("Blue Screen Mgr found an event system already in the project.");
            else
            {
                // Debug.Log("No Event System could be found, so Blue Screen Mgr is making one.");
                Instantiate(Resources.Load<GameObject>("EventSystem"));
            }

            // This instantiates our little version of the "Blue Screen of Death" when the whitebox connection fails.
            // https://en.wikipedia.org/wiki/Blue_screen_of_death
            // The T in SMMARTS stands for Tracking, so if there is no Tracking we have a big problem. BSOD.
            // The cool thing about our BSOD is that we can recover from it! Ours is more like a "Blue Screen of Delay" :)
            // We'll make the BSOD asset, immediately turn it off, and later we'll turn it on if we need to with some custom message..
            blueScreen = Instantiate(Resources.Load<GameObject>("BSOD"));
            titleGO = GameObject.Find("TitleTextBSOD");
            troubleshootGO = GameObject.Find("TroubleshootText");
            diagnosticGO = GameObject.Find("DiagnosticText");
            errorImageGO = GameObject.Find("ErrorImage");
            continueButtonGO = GameObject.Find("ContinueButtonBSOD");

            // now that we've made all our connections we can turn off the BSOD.
            blueScreen.SetActive(false);
            StartCoroutine(ExpireBootTimeout());




        }

        // this is for outside functions to call when we change ATC settings that temporarily delay tracking, like EEPROM updates
        public void ExpectDisconnectionDelay(float expectDelaySeconds)
        {
            AllowableDisconnectionSeconds = expectDelaySeconds;
            blueScreen.SetActive(false);
            isSystemBooting = true;
        }

        private IEnumerator ExpireBootTimeout()
        {
            yield return new WaitForSeconds(AllowableDisconnectionSeconds); // default is 30 seconds
            isSystemBooting = false;
        }

        // Update is called once per frame
        void Update()
        {
            // blueScreen may have been destroyed by a scene reload. if thats the case, just re-create it.
            if (blueScreen == null)
            {
                Start();
                return;
            }

            if (!isSystemBooting && !offlineMode)
            {
                if ((ATC.ME.Connected && Microcontroller.ME.Connected) && !ATC.ME.PowerFailure)
                {
                    blueScreen.SetActive(false);
                    return;

                }

                if (ATC.ME.PowerFailure)
                {
                    // Debug.Log("Check Main Power");
                    SetupBlueScreen(1);
                }
                else if (!ATC.ME.Connected && Microcontroller.ME.Connected)
                {
                    // Debug.Log("ATC is not connected");
                    SetupBlueScreen(2);
                }
                else if (ATC.ME.Connected && !Microcontroller.ME.Connected)
                {
                    // Debug.Log("Microcontroller in not connected");
                    SetupBlueScreen(3);
                }
                else if (!ATC.ME.Connected && !Microcontroller.ME.Connected)
                {
                    // Debug.Log("Both ATC and MC are not connected");
                    SetupBlueScreen(4);
                }
            }
        }

        void SetupBlueScreen(int errorType)
        {
            // we are using one troubleshoot text for every error message because thats how we use it
            // and now its hard-coded into the text object so it doesnt need to be set here with code.
            /*
            troubleshootText =
            "1: Double check that the white box is plugged into a wall outlet with power\n" +
            "2: Be sure that both USB plugs are connected\n" +
            "3: Close other SMMARTS programs that may have been left running in the background (use Alt+Tab) \n\n\n" +
            "If this message keeps appearing:\n" +
            "Disconnect everything (including power plug to white box and USB plugs) and shut down.\n" +
            "Then reconnect everything and restart.";
            */

            switch (errorType)
            {
                case 1://Main Power down
                    {
                        titleText = "Check white box plugs and power";
                        diagnosticText = "Main Power Failure";
                        errorImage = Resources.Load<Sprite>("wb-diag");
                        break;
                    }
                case 2://ATC not connected
                    {
                        titleText = "Check white box plugs and power";
                        diagnosticText = "Can't find the tracking System";
                        errorImage = Resources.Load<Sprite>("wb-diag");
                        break;
                    }
                case 3://MC not connected
                    {
                        titleText = "Check white box plugs and power";
                        diagnosticText = "Can't find the microcontroller";
                        errorImage = Resources.Load<Sprite>("wb-diag");
                        break;
                    }
                case 4://ATC and MC not connected
                    {
                        titleText = "Check white box plugs and power";
                        diagnosticText = "Can't find the tracking system or microcontroller";
                        errorImage = Resources.Load<Sprite>("wb-diag");
                        break;
                    }
            }

            titleGO.GetComponent<Text>().text = titleText;
            diagnosticGO.GetComponent<Text>().text = diagnosticText;

            errorImageGO.GetComponent<Image>().sprite = errorImage;

            if (!blueScreen.activeInHierarchy)
            {
                blueScreen.SetActive(true);
                continueButtonGO.GetComponent<Button>().onClick.AddListener(ContinueWithoutWB);
            }
        }

        private void ContinueWithoutWB()
        {
            offlineMode = true;
            blueScreen.SetActive(false);
        }
    }
}
