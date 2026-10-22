using UnityEngine;

public class MoveWithButtonColumn : MonoBehaviour
{

    [SerializeField] RectTransform RightOrLeftButtonColumn;

    // internal reference to this RectTransform.
    RectTransform RectTransform = new RectTransform();

    // these are for minimizing garbage collection
    Vector2 anchoredPosition = Vector2.zero;
    float initialAnchoredPosition_x;
    float anchoredPositionOffset_x;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        // the script is on by default and we expect you to use it when your UI is developed.
        // your initial test scene may not have a button column yet, and that's OK.
        if (RightOrLeftButtonColumn == null) return;

        // we expect this script to be on the entire button column! Otherwise it won't work. 
        RectTransform = GetComponent<RectTransform>();

        initialAnchoredPosition_x = RectTransform.anchoredPosition.x;

        anchoredPositionOffset_x = initialAnchoredPosition_x - RightOrLeftButtonColumn.anchoredPosition.x;

        anchoredPosition = RectTransform.anchoredPosition;

    }

    // Update is called once per frame
    void Update()
    {
        if (RightOrLeftButtonColumn == null) return;

        anchoredPosition.x = anchoredPositionOffset_x + RightOrLeftButtonColumn.anchoredPosition.x;
        RectTransform.anchoredPosition = anchoredPosition;
    }
}
