using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
public class TextSliderScript : MonoBehaviour
{
    public Slider slider;
    public string AfterNumber = " Minutes";
    private TextMeshProUGUI text;
    string oldtext ;
    void OnEnable()
    {
        text =this.GetComponent<TextMeshProUGUI>();
        oldtext = text.text ;
        slider.onValueChanged.AddListener((v)=>{
            
            text.text= oldtext+""+(v).ToString("0")+ AfterNumber;
            text.ForceMeshUpdate();
        });
        
    }
    
    // Update is called once per frame
    
}
