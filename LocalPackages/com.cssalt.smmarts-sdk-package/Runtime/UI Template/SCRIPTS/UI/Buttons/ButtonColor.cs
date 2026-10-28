using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;


public class ButtonColor : MonoBehaviour
{
    public Button button;
    public Color hyperColor; // color to use when hovering over an already selected object
    public Color wantedColor;
    private Color originalColor;
    private ColorBlock cb;
    public bool colorState = false;
    //toggleOnIncorrect
    
    // Start is called before the first frame update
    void Start()
    {
        cb = button.colors;
        originalColor = cb.selectedColor;
    }
    // changes coloron hover
    public void changeWhenHover()
    {
        if(colorState){
            cb.normalColor = hyperColor;
            cb.selectedColor = hyperColor;
            button.colors = cb;
        }
        else{
            cb.selectedColor= wantedColor;
            button.colors = cb;
        }
    }
    // returns color to normal
    public void changeWhenLeaves()
    {
        if(colorState){
            cb.normalColor = wantedColor;
            cb.selectedColor = wantedColor;
            button.colors = cb;
        }else{
            cb.selectedColor = originalColor;
            cb.normalColor = originalColor;
            button.colors = cb;
        }
    }
    // this will change the color untill its changed back
    public void updateColorState(bool newColorState) 
    {
        colorState = newColorState;
        if(colorState){
            cb.normalColor = wantedColor;
            cb.selectedColor = wantedColor;
            button.colors = cb;
        }else{
            cb.selectedColor = originalColor;
            cb.normalColor = originalColor;
            button.colors = cb;
        }
    }
}
