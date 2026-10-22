using UnityEngine;
using TMPro;
using System;

// This class is specifically designed to hide the button columns on the right and left side of many SMMARTS simulators.

public class UI_Column_Expander : MonoBehaviour
{

    // Is this button on the right or the left column?
    public bool RightSide;
    public bool LeftSide;

    // Expanded is the flag for opening the column
    public bool Expand = true;

    // these are two speeds for opening and closing the animation
    [SerializeField] float ExpandingSpeed = 1400f;
    [SerializeField] float CollapsingSpeed = 400f;

    // flag for if the column is done moving
    bool FinishedAnimating;

    // internal reference to this RectTransform.
    RectTransform RectTransform = new RectTransform();
    // Internal reference to the <<< carets under the Tab Expander - gameobject names are hard coded.
    RectTransform Carets;

    // these are for minimizing garbage collection
    Vector2 anchoredPosition = Vector2.zero;
    float dx;
    Vector3 caretsPointLeft = new Vector3(1, 1, 1);
    Vector3 caretsPointRight = new Vector3(-1, 1, 1);



    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        // we expect this script to be on the entire button column! Otherwise it won't work. 
        RectTransform = GetComponent<RectTransform>();

        // We also expect to find a child GO named "Tab Expander" with a child GO named "Open & Close Carets"
        Carets = (RectTransform)RectTransform.Find("Tab Expander").Find("Open & Close Carets");

        // we have button columns on the right and left sides, so this code can control both of them.
        // you have to tell us which side we are working on via the inspector. if you dont, we'll assume right side. 
        if (!RightSide & !LeftSide) 
        { 
            RightSide = true;
            // Let the programmer know they should check the right or left side box in the inspector:
            Debug.Log("I'm setting RightSide to TRUE in UI Column Expander on " + gameObject.name + "... Hope thats OK. Please check."); 
        }
        LeftSide = !RightSide;

    }

    private void Update()
    {
        if (RightSide) UpdateRight();
        if (LeftSide) UpdateLeft();
    }
    // Update is called once per frame
    void UpdateRight()
    {
        if (Expand) {
            if (RectTransform.anchoredPosition.x > 0)
            {
                dx = ExpandingSpeed * Time.deltaTime;
                anchoredPosition.x = RectTransform.anchoredPosition.x - dx;
                if (anchoredPosition.x < 0) anchoredPosition.x = 0;
            } else
            {
                anchoredPosition.x = 0;
            }
            FinishedAnimating = anchoredPosition.x == 0;
            Carets.localScale = caretsPointRight;
        }
        if (!Expand)
        {
            if (RectTransform.sizeDelta.x > RectTransform.anchoredPosition.x)
            {
                dx = CollapsingSpeed * Time.deltaTime;
                anchoredPosition.x = RectTransform.anchoredPosition.x + dx;
                if (anchoredPosition.x > RectTransform.sizeDelta.x) anchoredPosition.x = RectTransform.sizeDelta.x;
            }
            else
            {
                anchoredPosition.x = RectTransform.sizeDelta.x;
            }
            FinishedAnimating = anchoredPosition.x == RectTransform.sizeDelta.x;
            Carets.localScale = caretsPointLeft;
        }

        RectTransform.anchoredPosition = anchoredPosition;

    }

    void UpdateLeft()
    {
        if (!Expand)
        {
            if (RectTransform.anchoredPosition.x > 0)
            {
                dx = CollapsingSpeed * Time.deltaTime;
                anchoredPosition.x = RectTransform.anchoredPosition.x - dx;
                if (anchoredPosition.x < 0) anchoredPosition.x = 0;
            }
            else
            {
                anchoredPosition.x = 0;
            }
            FinishedAnimating = anchoredPosition.x == 0;
            Carets.localScale = caretsPointRight;
        }
        if (Expand)
        {
            if (RectTransform.sizeDelta.x > RectTransform.anchoredPosition.x)
            {
                dx = ExpandingSpeed * Time.deltaTime;
                anchoredPosition.x = RectTransform.anchoredPosition.x + dx;
                if (anchoredPosition.x > RectTransform.sizeDelta.x) anchoredPosition.x = RectTransform.sizeDelta.x;
            }
            else
            {
                anchoredPosition.x = RectTransform.sizeDelta.x;
            }
            FinishedAnimating = anchoredPosition.x == RectTransform.sizeDelta.x;
            Carets.localScale = caretsPointLeft;
        }

        RectTransform.anchoredPosition = anchoredPosition;

    }

    public void ExpandCollapseToggle() // called by the expander button OnClick.
    {
        Expand = !Expand;
    }

    public void OnMouseEnter() // called by the expander button's Event Trigger component
    {
        if (FinishedAnimating & !Expand) {
            ExpandCollapseToggle();
        }
    }

    public void OnMouseLeave () // called by the expander button's Event Trigger component
    {
        if (FinishedAnimating & Expand)
        {
            ExpandCollapseToggle();
        }
    }


}
