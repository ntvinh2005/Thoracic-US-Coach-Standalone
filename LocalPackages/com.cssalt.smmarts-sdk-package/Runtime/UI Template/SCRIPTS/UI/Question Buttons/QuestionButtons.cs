using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;//*
using UnityEngine.UI;

public class QuestionButtons : MonoBehaviour,
IPointerClickHandler
{
    Image currentImage;
    public Sprite standardState;
    public Sprite selectedState;
    public Sprite correctState;
    public Sprite incorrectState;
    public bool correct;
    
    // can be controlled by reveal script, boolean for wether button submits immediatley or you have to manuall submit
    public bool showImmediately; 
    
    public bool exculsiveSelect;
    public bool exculsiveSubmit;
    [Header("------------")]
    public bool selected=false;
    public bool submitted=false;
   
    void OnEnable(){
       currentImage = this.GetComponent<Image>();
    }
    // Start is called before the first frame update

    public void Reset(){
        selected=false;
        submitted=false;
        currentImage.sprite = standardState;
    }
    public void Select(){
            if(selected && !submitted){
                selected = false;
                currentImage.sprite = standardState;
            }else{
                selected = true;
                currentImage.sprite = selectedState;
            }
    }
    public void DeSelect(){
        if(selected && !submitted){
            selected = false;
            currentImage.sprite = standardState;
        }
    }
    public void Submit(){
        if(correct){
                currentImage.sprite = correctState;
                submitted=true; 
                selected = false;
        }else{
            currentImage.sprite = incorrectState;
            submitted=true; 
            selected = false;
        }
        //if correct display next 
    }
    
    public void ExclusiveSelect(){
        Select();
        QuestionButtons[] questionButtons =  FindObjectsOfType<QuestionButtons>();
        foreach( var button in questionButtons){
            if(button != this){
                button.DeSelect();
            }
         }
        
    }
    public void ExclusiveSubmit(){ // shows answer to all if you click won
        QuestionButtons[] questionButtons =  FindObjectsOfType<QuestionButtons>();
         foreach( var button in questionButtons){
            button.Submit();
        }
        // display on all clicks
        
    }
    //display next if correct
    
    public void OnPointerClick(PointerEventData pointerEventData)
    {
        if(showImmediately){
            Submit();
            if(exculsiveSubmit){
                 ExclusiveSubmit();
            }
            /*
            if(correct){
                currentImage.sprite = correctState;
                submitted=true; 
            }else{
                currentImage.sprite = incorrectState;
                submitted=true; 
            }
            */
        }else{
            if(exculsiveSelect){
                ExclusiveSelect();
            }else{
                Select();
            }
            
            /*
            if(selected && !submitted){
                selected = false;
                currentImage.sprite = standardState;
            }else{
                selected = true;
                currentImage.sprite = selectedState;
            }
             */
        
        }
    }
}
