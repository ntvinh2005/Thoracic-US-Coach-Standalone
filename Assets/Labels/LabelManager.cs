using UnityEngine;
using System.Collections;
using System;

// SINGLETON
// 


public class LabelManager : MonoBehaviour {

    public static LabelManager ME;

    public GameObject[] labels;

    // public for user settings
    public float LabelSize;
    public float HideDistance;

    // public for other scripts to test
    public bool Show = true;

    // public for property inspector reference
    public GameObject labelPrefab;


    void Awake()
    {
        if (ME != null) GameObject.Destroy(ME);
        else ME = this;
        DontDestroyOnLoad(this);
    }

    // Use this for initialization
    void Start () {

        labelPrefab.GetComponent<LabelController>().LabelSize = LabelSize;
        labelPrefab.GetComponent<LabelController>().HideDistance = HideDistance;

        // Find all initial spheres that serve as placeholders
        labels = GameObject.FindGameObjectsWithTag("Label");

        // For each placeholder, clone a new prefab and set its name + parent
        // The controller script automatically renames the label using its name
        foreach (GameObject go in labels)
        {
            GameObject newLabel = Instantiate(labelPrefab, go.transform.position, Quaternion.identity) as GameObject;
            newLabel.name = go.name;
            newLabel.transform.position = go.transform.position;
            newLabel.transform.parent = go.transform;
            newLabel.tag = "Label";
        }

        // Refresh the labels list including the newly created objects
        labels = GameObject.FindGameObjectsWithTag("Label");
    }
	
    /* // Dev button 
    void OnGUI()
    {
        if (GUI.Button(new Rect(10, 10, 100, 30), "All Labels")){
            if (Show)
                HideLabels();
            else
                ShowLabels();
        }
    }
    */

    public void ShowLabels()
    {
        // Recursively enable all renderers
        foreach (GameObject go in labels)
            go.SetActive(true);
        Show = true;
    }

    public void HideLabels()
    {

        // Recursively disable all renderers
        foreach (GameObject go in labels)
            go.SetActive(false);
        Show = false;
    }
}
