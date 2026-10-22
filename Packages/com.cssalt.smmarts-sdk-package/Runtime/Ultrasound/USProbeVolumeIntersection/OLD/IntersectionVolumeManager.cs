using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class IntersectionVolumeManager : MonoBehaviour
{
    public static IntersectionVolumeManager ME;

    [Header("---- Skin Intersection Volumes ----")]
    public float IntersectionVolume_ml = 0; // ml
    public float IntersectionArea_mm2 = 0; // ml
    public float IntersectionVolume_cc = 0; // cubic centimeters
    public Vector3 Posoffset = new Vector3(0, 0, 0);

    [Header("---- Normalized Skin Intersection Volume ----")] // this might be more useful than the volume.
    public float MinIntersectionVolume_ml = 0; // ml
    public float MaxIntersectionVolume_ml = 1000; // ml
    [Range(0, 1f)]
    public float NormalizedIntersectionVolume = 0; // this is probably more useful for outside scripts.
    [Range(0, 1f)]
    public float NormalizedIntersectionArea = 0;
    private float volumeAccumulator = 0; // ml
    private float areaAccumulator = 0; // ml

    [Header("---- Estimated Pressure ----")] // this might be more useful than the volume.
    public float IntersectionVolumeToPressureConversionFactor = 1; // units of .... not sure yet. magnitude depends on gel hardness.
    public float ReactionForce = 0; // units of ... not sure yet. likely Newtons.

    [Header("---- Layermasks and Prototypes ----")]
    public LayerMask SkinLayer;
    public LayerMask ProbeInnerSurfaceLayer; 

    public GameObject ColumnPrototype; // this has the IntersectionVolumeElement.cs on it
    public GameObject ColumnHolder;    // this is a gameobject that holds all the columns

    [Header("---- Arrangement of Intersection Volume Elements ----")]
    // covers the area that we will fill with a 1x1 mm grid of IntersectionVolumnElements
    // note one Unity distance unit = 1 mm in our simulations
    public float Length; // 60 mm
    public float LengthOffset; // -30 mm
    public float Width;  // 20 mm
    public float WidthOffset;  // -10 mm
    // how many skin intersection columns are left? we cull the ones that are off the edge of the probe.
    public int ColumnCount; // we used 1121 columns in the verification test on 10/31/24

    [Header("---- Programmer Tools ----")]
    // useful for building and tuning in Unity editor
    public bool DrawDebugRays;

    // Singleton reference 
    void Awake()
    {
        if (ME != null) GameObject.Destroy(ME);
        else ME = this;

        BuildColumns();
    }

    // this is the initialization function. its called once during Awake(),
    // and can also be called from Update() using the public rebuild flag.
    // this fills the area defined by width and length with Columns spaced 1mm apart,
    // and each column has an IntersectionVolumeElement.
    void BuildColumns()
    {
        for (int x = 1; x < Length; x++)
        {
            for (int y = 1; y < Width; y++)
            {
                GameObject newColumn = Instantiate(ColumnPrototype, ColumnHolder.transform);
                Vector3 location = new Vector3(x + LengthOffset, y + WidthOffset, 0);
                newColumn.transform.localPosition = location;
                newColumn.SetActive(true);
                newColumn.GetComponent<IntersectionVolumeElement>().JustKillYourselfIfYourNotNeeded();
                 
            }
        }
        ColumnCount = ColumnHolder.transform.childCount;
    }

    // The individual intersection volume elements send their measurements to this function during Update()
    public void Add(float aVolume)
    {
        volumeAccumulator += aVolume;
    }

    public void AddArea(float aArea)
    {
        areaAccumulator += aArea;
    }

    public void PositionOffset(Vector3 offset)
    {
        Posoffset = offset;
    }

    // by LateUpdate, all the individual Intersection Volume elements are finished reporting in via Add().
    // its time to write down the result and reset the accumulator for the next frame.
    private void LateUpdate()
    {


        IntersectionVolume_ml = volumeAccumulator; // ml
        IntersectionVolume_cc = IntersectionVolume_ml / 1000f; // cc
        volumeAccumulator = 0;

        IntersectionArea_mm2 = areaAccumulator; // mm2
        areaAccumulator = 0;

        // % of contact area
        NormalizedIntersectionArea = IntersectionArea_mm2 / ColumnCount;

        // this is likely the most useful information needed for outside scripts.
        NormalizedIntersectionVolume = Mathf.InverseLerp(MinIntersectionVolume_ml, MaxIntersectionVolume_ml, IntersectionVolume_ml);

        // this is also likely useful information needed for outside scripts. this assumes the gel is deformable and has a hardness.
        ReactionForce = IntersectionVolume_ml * IntersectionVolumeToPressureConversionFactor; // units?

    }

    
}
