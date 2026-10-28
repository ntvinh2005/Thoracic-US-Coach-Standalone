using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace SMMARTS
{
    public class ButtonTemplate_25 : MonoBehaviour
    {
        //public List<GameObject> objectsToToggle = new List<GameObject>();   
        //public GameObject objectToToggle;
        public GameObject[] ToggleList;
        public GameObject[] EnableList;
        public GameObject[] DisableList;

        public RenderTexture renderTexture;
        public ButtonColor buttonColor;
        [Header("ExclusiveToggle")]
        //public bool ignoreChildren=false;
        public List<ButtonTemplate_25> ignoreList = new List<ButtonTemplate_25>();
        public bool antiDisable = false;
        public bool buttonState = false;

        [Header("Self Click at Start")]
        public Button mybutton;
        public float AutoDelaySeconds;

        public void Start()
        {
            mybutton = GetComponent<Button>();

            if (AutoDelaySeconds > 0)
            {
                mybutton.onClick.Invoke();
                Invoke("DelayedClick", AutoDelaySeconds);
            }

            if (this.GetComponent<ButtonColor>() != null)
            {
                buttonColor = this.GetComponent<ButtonColor>();
            }
        }

        void DelayedClick()
        {
            if (buttonState)
            {
                mybutton.onClick.Invoke();
            }
        }

        // loop throught the toggleobjects list and toggle all of the objects
        public void ToggleObjects()
        {
            foreach (var objectToToggle in ToggleList)
            {
                Toggle(objectToToggle);
            }
            UpdateButtonState(!buttonState);
            UpdateColorState(buttonState);
        }
        // use this function if you want to untoggle all other buttons when you click this button
        public void ExclusiveToggleObjects()
        {
            DisableAllOtherButtons();
            foreach (var objectToToggle in ToggleList)
            {
                Toggle(objectToToggle);
            }
            UpdateButtonState(!buttonState);
            UpdateColorState(buttonState);





        }
        public void EnableObjectsInList()
        {
            foreach (var objectToEnable in EnableList)
            {
                Toggle(objectToEnable);
            }
            UpdateButtonState(true);
            UpdateColorState(true);

        }

        public void ExclusiveEnableObjects()
        {
            DisableAllOtherButtons();
            foreach (var objectToEnable in EnableList)
            {
                Toggle(objectToEnable);
            }
            UpdateButtonState(true);
            UpdateColorState(true);

        }
        public void DisableObjectsInList()
        {
            foreach (var objectToToggle in DisableList)
            {
                Disable(objectToToggle);
            }
            UpdateButtonState(false);
            UpdateColorState(false);
        }
        public void DisableAllObjects()
        {

            foreach (var objectToToggle in ToggleList)
            {
                Disable(objectToToggle);
            }
            foreach (var objectToToggle in EnableList)
            {
                Disable(objectToToggle);
            }
            foreach (var objectToToggle in DisableList)
            {
                Disable(objectToToggle);
            }
            UpdateButtonState(false);
            UpdateColorState(false);
        }

        private void Toggle(GameObject objectToToggle)
        {
            if (buttonState == true)
            {
                //objectToToggle.SetActive(false);
                Disable(objectToToggle);
                //UpdateColorState(false);
                //UpdateButtonState(false);
            }
            else
            {
                Enable(objectToToggle);
                //UpdateColorState(true);
                //UpdateButtonState(true);
            }

        }
        private void UpdateButtonState(bool buttonState)
        {
            this.buttonState = buttonState;

        }
        private void UpdateColorState(bool colorState)
        {
            if (buttonColor)
            {
                buttonColor.updateColorState(colorState);
            }
        }
        private void Enable(GameObject objectToEnable)
        {
            if (objectToEnable)
            {
                objectToEnable.SetActive(true);
            }

        }
        private void Disable(GameObject objectToDisable)
        {
            if (objectToDisable)
            {
                objectToDisable.SetActive(false);
                if (renderTexture)
                {
                    renderTexture.Release();
                }
            }
        }


        private void DisableAllOtherButtons()
        {
            ButtonTemplate_25[] buttons = FindObjectsOfType<ButtonTemplate_25>();

            for (int i = 0; i < buttons.Length; i++)
            {
                //check if its this object
                if (buttons[i] == this.gameObject.GetComponent<ButtonTemplate_25>())
                {
                    continue;
                }


                if (!ignoreList.Contains(buttons[i]))
                {

                    buttons[i].DisableAllObjects();

                }

            }
        }





        /*
        public void ToggleObject() {
            if(objectToToggle){
            if(objectToToggle.activeInHierarchy == true){
                objectToToggle.SetActive(false);
                if(renderTexture){
                    renderTexture.Release();
                }
                if(buttonColor){
                    buttonColor.updateColorState(false);
                }
            }
            else{
                objectToToggle.SetActive(true);
                if(buttonColor){
                    buttonColor.updateColorState(true);
                }
            }
            }
        }
        // USE IF YOU WANT ALL OTHER EXCLUSIVE TOGGLE OBJECTS TO BE DISABLED
        public void ExclusiveToggleObject() {
            if(objectToToggle){
                if(objectToToggle.activeInHierarchy == true){
                    objectToToggle.SetActive(false);
                    if(renderTexture){
                        renderTexture.Release();
                    }
                    if(buttonColor){
                        buttonColor.updateColorState(false);
                    }
                }
                else{
                // IF OBJECT NOT ENABLED 

                    objectToToggle.SetActive(true);
                    if(buttonColor){
                        buttonColor.updateColorState(true);
                    }
                    DisableAll();
                }
            }
        }
        public void Disable(){
            if(objectToToggle){// if object is set
                if(antiDisable == false){
                if(objectToToggle.activeInHierarchy == true){
                        objectToToggle.SetActive(false);
                        if(renderTexture){
                            renderTexture.Release();
                        }
                        if(buttonColor){
                            buttonColor.updateColorState(false);
                        }
                    }
                }
            }
        }

        public void DisableAll(){
            ButtonTemplate[] buttons =  FindObjectsOfType<ButtonTemplate>();

            for(int i = 0; i<buttons.Length;i++){
                //check if its this object
                if(buttons[i]==this.gameObject.GetComponent<ButtonTemplate>()){
                    continue;
                }


                if(!ignoreList.Contains(buttons[i])){
                    buttons[i].Disable();
                }

            }


        }
        */
    }
}
