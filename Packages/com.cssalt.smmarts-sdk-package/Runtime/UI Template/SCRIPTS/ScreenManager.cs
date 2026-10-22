using System.Collections;
using System.Collections.Generic;

using UnityEngine;

namespace SMMARTS
{
    public class ScreenManager : MonoBehaviour
    {

        public static ScreenManager ME;


        [Header("Ultrasound Gameobjects and flag")]
        // this flag can be read and set by any outside function.
        public bool FullscreenUltrasound;
        // these parts are under the SMMARTS Ultrasound UI gameobject.
        public RectTransform UltrasoundScreen;             // gameobject "Ultrasound Screen" 
        public RectTransform UltrasoundScreen_SmallLayout; // gameobject "Ultrasound Screen Small Dimensions"
        public RectTransform UltrasoundScreen_LargeLayout; // gameobject "Ultrasound Screen Large Dimensions"
        bool lastFullscreenUltrasound; // flag for detecting value change of FullScreenUltrasound


        [Header("3D View Cameras and Lights")]
        // The first camera is the 3D perspective camera (usually Main Camera)
        // The other cameras are typically orthographic anatomical views for coronal, sagittal, or transferse planes
        // Camera[0] must be filled, the others are optional.
        // There must be a light assigned to each camera. Lights can be anywhere in the heirarchy.
        // The same light can be assigned to multiple cameras.
        public int ActiveCameraAndLightIndex = 0; // only Camera[ActiveCameraAndLightIndex] will be enabled, the other cameras will not.
        public Camera[] Cameras;
        public Light[] Lights;


        [Header("Layer Masks for the Anatomic Layers")]
        // this is like peeling off the layers of an onion as you go down
        public LayerMask LayerSet_1; // Typically Skin
        public LayerMask LayerSet_2; // skin is off, exposing all bones, muscles, nerves, arteries and veins
        public LayerMask LayerSet_3; // everything is off except what you care most about, like a nerve.
        public LayerMask Nothing;    // everything is off. this is for fullscreen ultrasound.


        [Header("Layers in the 3D View")]
        public AnatomyLayer TheLayerDisplayedIn3DRendering;
        public enum AnatomyLayer { LayerSet_1, LayerSet_2, LayerSet_3, Nothing };
        private AnatomyLayer LastViewLayerBeforeFullscreenUltrasound;


        // This class is a singleton, lets make sure there is only one and it persists across all scenes.
        private void Awake()
        {
            // singleton reference:
            if (ME != null) GameObject.Destroy(ME);
            else ME = this;
            DontDestroyOnLoad(this);
        }

        // called directly by a UI button, probably named "Fullscreen Ultrasound"
        public void FullscreenUS_Toggle()
        {
            FullscreenUltrasound = !FullscreenUltrasound;
        }

        // called by FullscreenUS_Toggle(), can also be called directly by a UI button
        public void FullscreenUS_On()
        {
            FullscreenUltrasound = true;
        }

        // called by FullscreenUS_Toggle(), can also be called directly by a UI button
        public void FullscreenUS_Off()
        {
            FullscreenUltrasound = false;
        }


        // called by a UI button, probably labeled "Visible Layers"
        public void Cycle3DRenderingLayers()
        {
            // if fullscreen US is on, just turn it off and restore the last display layer 
            if (FullscreenUltrasound)
            {
                FullscreenUS_Off(); // also restores the last display layer 
                return;
            }

            // increment through the sequence of display options for visible anatomy
            TheLayerDisplayedIn3DRendering++;

            // the last layer, Nothing, is intended only for Fullscreen US.
            // we are not in Fullscreen US, so wrap it around to the first LayerSet.
            if (TheLayerDisplayedIn3DRendering >= AnatomyLayer.Nothing)
                TheLayerDisplayedIn3DRendering = AnatomyLayer.LayerSet_1;// skips the nothing layer, which is only for fullscreen US

        }


        // called by Update() when the camera view controller is turned on, can also be called directly by a UI button
        public void UseFirstCamera()
        {
            ActiveCameraAndLightIndex = 0;
        }

        // called by a UI button, probably labeled "Camera Views"
        public void CycleCameraViews()
        {
            // if fullscreen US is on, just turn it off and restore the last display layer 
            if (FullscreenUltrasound)
            {
                FullscreenUS_Off(); // also restores the last display layer 
                return;
            }

            // increment through the sequence of camera view options 
            ActiveCameraAndLightIndex++;
            if (ActiveCameraAndLightIndex >= Cameras.Length) ActiveCameraAndLightIndex = 0;
        }


        // Update is called once per frame
        // Update does four things:
        //      1) looks for a change to the FullscreenUltrasound flag and changes screen elements and layers accordingly
        //      2) sets the layermask of the cameras
        //      3) turns off all the cameras but the selected one
        //      4) turns off FullscreenUltrasound and selects the main 3D view camera when the camera controller's button is pressed
        void Update()
        {

            // set fullscreen mode by detecting a change to the flag.
            if (FullscreenUltrasound != lastFullscreenUltrasound)
            {
                if (FullscreenUltrasound)
                {
                    // Match the Ultrasound Screen position and scale to the large layout 
                    UltrasoundScreen.position = UltrasoundScreen_LargeLayout.position;
                    UltrasoundScreen.localScale = UltrasoundScreen_LargeLayout.localScale;

                    // Turn off the anatomy in the 3D perspective camera so no 3D anatomy is visible around the edge of the ultrasound display
                    // and remember the display layer so we can come back to it
                    LastViewLayerBeforeFullscreenUltrasound = TheLayerDisplayedIn3DRendering;
                    TheLayerDisplayedIn3DRendering = AnatomyLayer.Nothing;
                }
                else
                {
                    // Match the Ultrasound Screen position and scale to the large layout 
                    UltrasoundScreen.position = UltrasoundScreen_SmallLayout.position;
                    UltrasoundScreen.localScale = UltrasoundScreen_SmallLayout.localScale;

                    // Turn the anatomy in the 3D perspective camera back on to its setting before fullscreen US was turned on.
                    TheLayerDisplayedIn3DRendering = LastViewLayerBeforeFullscreenUltrasound;
                }
                lastFullscreenUltrasound = FullscreenUltrasound;
            }


            // choose the appropriate layermask of the camera based on the anatomic layer options
            switch (TheLayerDisplayedIn3DRendering)
            {
                case AnatomyLayer.LayerSet_1:
                    foreach (Camera camera in Cameras) camera.cullingMask = LayerSet_1;
                    break;
                case AnatomyLayer.LayerSet_2:
                    foreach (Camera camera in Cameras) camera.cullingMask = LayerSet_2;
                    break;
                case AnatomyLayer.LayerSet_3:
                    foreach (Camera camera in Cameras) camera.cullingMask = LayerSet_3;
                    break;
                case AnatomyLayer.Nothing:
                    foreach (Camera camera in Cameras) camera.cullingMask = Nothing;
                    break;
                default:
                    foreach (Camera camera in Cameras) camera.cullingMask = LayerSet_1;
                    break;
            }

            // disable all the cameras, then enable the one we want. same for the lights. 
            foreach (Camera camera in Cameras) camera.enabled = false; Cameras[ActiveCameraAndLightIndex].enabled = true;
            foreach (Light light in Lights) light.enabled = false; Lights[ActiveCameraAndLightIndex].enabled = true;


            // if the camera controller is pressed:
            // - use the Main Camera (the 3D perspective view). This should be the first camera in the list. turn off all the other cameras.
            // - the tracking system is already set up to control the position and rotation of the main camera via the TUI.
            // - jump out of fullscreen ultrasound, if present
            // of course, first make sure the microcontroller is present, connected, and running.
            if (Microcontroller.ME != null && Microcontroller.ME.Connected && Microcontroller.ME.MicrocontrollerData.Length > 0)
            {
                bool cameraShutter = int.Parse(Microcontroller.ME.MicrocontrollerData[0]) > 0;
                if (cameraShutter & FullscreenUltrasound)
                {
                    FullscreenUS_Off();
                    UseFirstCamera();
                }
            }





        }



    }
}
