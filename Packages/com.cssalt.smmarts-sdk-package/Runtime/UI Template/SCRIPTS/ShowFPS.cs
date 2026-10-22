using System.Collections;
using System.Collections.Generic;
using UnityEngine;

using TMPro;



public class ShowFPS : MonoBehaviour
{
    // apply this to a text object in the inspector.
    public TextMeshProUGUI FPStext;

    public int fps;
    // numberOfFrames is for averaging. 
    [SerializeField] int numberOfFrames = 25; // 25 is a good number here but can be adjusted in the inspector. 

   float LastCycleTimestamp;
   float averageframeduration;
   int framecount;

    // Start is called before the first frame update
    void Start()
    {

    }

    // Update is called once per frame
    void Update()
    {
        framecount++;
        if (framecount > numberOfFrames)
        {

            float deltaT = Time.time - LastCycleTimestamp; // seconds

            averageframeduration = deltaT / (float)framecount * 1000; // ms

            fps = (int)(1000 / averageframeduration);

            //FPStext.text = averageframeduration.ToString("00") + "ms"; // this shows ms per frame

            FPStext.SetText(fps.ToString() + " fps");
            
            // FPStext.ForceMeshUpdate();

            framecount = 0;
            LastCycleTimestamp = Time.time;


        }
    }
}

