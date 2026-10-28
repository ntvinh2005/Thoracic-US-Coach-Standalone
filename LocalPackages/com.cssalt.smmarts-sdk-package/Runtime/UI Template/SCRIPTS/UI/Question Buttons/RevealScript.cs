using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class RevealScript : MonoBehaviour
{
    // imediatemode 
    // delayed
    public bool syncButtonMode = false;
    public bool showImmediatly = true;
    public List<GameObject> RevealWhenSelected = new List<GameObject>();   
    public List<GameObject> RevealWhenSubmitted = new List<GameObject>();  
    QuestionButtons[] questionButtons;
    
    // Start is called before the first frame update
    void OnEnable()
    {
        //RevealObjectsInSelectedList();
        questionButtons =  FindObjectsOfType<QuestionButtons>();
        if(syncButtonMode){
            foreach(var button in questionButtons ){
                    //enable button
                    //Buttons[0].SetActive(true);
                    button.showImmediately=showImmediatly;
                    
            }
        }
        StartCoroutine(coroutine());
    }
    void RevealObjectsInSelectedList(){
        foreach(var objectToReveal in RevealWhenSelected ){
            objectToReveal.SetActive(true);
        }
    }
    void RevealObjectsInSubmittedList(){
        foreach(var objectToReveal in RevealWhenSubmitted ){
            objectToReveal.SetActive(true);
        }
    }
    void HideObjectsInSelectedList(){
        foreach(var objectToReveal in RevealWhenSelected ){
            objectToReveal.SetActive(false);
        }
    }
    void HideObjectsInSubmittedList(){
        foreach(var objectToReveal in RevealWhenSubmitted ){
            objectToReveal.SetActive(false);
        }
    }
    
    // Update is called once per frame
    IEnumerator coroutine(){
        while(this.gameObject.activeInHierarchy == true){
            questionButtons =  FindObjectsOfType<QuestionButtons>();
            bool OneIsSelected = false;
            bool OneIsSubmitted = false;
            if(questionButtons.Length>0){
                
                foreach(var button in questionButtons ){
                    if(button.selected){
                        //Debug.Log("TTTTTRT");
                        OneIsSelected = true;
                        
                    }
                    if(button.submitted){
                        OneIsSubmitted = true;
                        
                    }
                    //enable button
                    
                    
                    
                }
                if(OneIsSelected==false){
                    HideObjectsInSelectedList();
                }else{
                    RevealObjectsInSelectedList();
                }
                if(OneIsSubmitted==false){
                
                    HideObjectsInSubmittedList();
                }else{
                    RevealObjectsInSubmittedList();
                }
            // disable button
            }
            yield return null;
        }

    }
    
        
    
    
}