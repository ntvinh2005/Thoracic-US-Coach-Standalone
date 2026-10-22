
using UnityEngine;
using UnityEngine.EventSystems;
using TMPro;

public class OpenHyperlinks : MonoBehaviour, IPointerClickHandler {
    public TextMeshProUGUI text;
    TMP_LinkInfo linkInfo;
    int linkIndex;
    public void OnPointerClick(PointerEventData eventData) {
        //Debug.Log("test1");
        if(eventData.button==PointerEventData.InputButton.Left){
                linkIndex = TMP_TextUtilities.FindIntersectingLink(text, Input.mousePosition,null);
            //Debug.Log("test2");
            if( linkIndex != -1 ) { // was a link clicked?
                linkInfo = text.textInfo.linkInfo[linkIndex];
                //Debug.Log("test3");
                // open the link id as a url, which is the metadata we added in the text fieldf
                Application.OpenURL(linkInfo.GetLinkID());
            }
            
        }
    }
    
}
