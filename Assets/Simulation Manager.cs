using UnityEngine;
using SMMARTS;
using System.Linq;
using UnityEngine.Windows;

/*
Overview: this guy orchestrates needle interactions like LOR, tactile thump, hydrodissection.
It has needle raycasts and proximity detectors.
Event flags are set for other scripts to read, like is the needle in the skin? This script knows.
If you want to change something with code that you can do manually with a button, PLEASE, 
PLEASE call the same functions in UserInterfaceManager that the buttons normally do to prevent the UI from becomming stale.
*/

public class SimulationManager : MonoBehaviour
{

    public static SimulationManager ME;

    [Header("The Tuohy needle and Interaction with Anatomy")]
    public GameObject TuohyNeedle;
    public NeedleInteraction needleInteraction;
    public NeedleInteractionSummary[] Hits;
    //public NeedleInteractionSummary[] NearNeedleTipNotInLine; // yet to be implemented

    [Header("Event Flags")]
    public bool needleInSkin = false; // useful so you dont do stuff if no needle
    public bool needleInLung = false; // useful for Pneumothorax! alert
    public bool needleInLigament = false; // useful for tactile thumper
    public bool needleInDura = false; // useful for Wet Tap! alert
    public bool needleInSpinalCord = false; // useful for Spinal Injury! alert
    public bool needleBackwalledLigament = false; // useful for LOR valve
    public bool valveOpen = true; // LOR valve

    // these are internal flags for when something changed 
    // useful for not spamming the toast notification system
    private bool pneumothoraxJustHappened = false;
    private bool wetTapJustHappened = false;
    private bool spinalInjuryJustHappened = false;
    private bool lastSkinState = false;
    private bool lastValveState = false;
    private bool lastNeedleInLigament = false;


    [Header("Layer Masks for the Anatomic Layers")]
    // this is like peeling off the layers of an onion as you go down
    public LayerMask SkinLayer;
    public LayerMask MuscleLayer, LigamentLayer, NerveLayer, ArteryLayer, VeinLayer;
    public LayerMask LungLayer, BoneLayer, DuraLayer, SpinalCordLayer, NeuraxialLayer;
    public LayerMask NeedleLayer;

    [Header("Primary Layer Mask for Testing Needle Interaction")]
    public LayerMask NeedleInteractionLayerMask;

    [Header("Raycast Test Results - For Devs to Read Only")]
    // these are testers that can be read in the inspector. Just look at the inspector for this class to verify.
    // these are all from the list of needleInteraction hits for verification in the inspector.
    // dont use these to code.
    [SerializeField] bool[] isPenetratings = new bool[0];
    [SerializeField] float[] distanceFromTips = new float[0];
    [SerializeField] bool[] isBackwalleds = new bool[0];
    [SerializeField] GameObject[] gameObjectsHit = new GameObject[0]; // The anatomy object that was hit
    [SerializeField] int[] layersHit = new int[0];             // Layer number of the hit object
    [SerializeField] float nearMissDistance = -1f;  // Near miss result
    [SerializeField] int hitCount = 0;  // How many hits we got

    // This class is a singleton, lets make sure there is only one and it persists across all scenes.
    private void Awake()
    {
        // singleton reference:
        if (ME != null) GameObject.Destroy(ME);
        else ME = this;
        DontDestroyOnLoad(this);
    }

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        needleInteraction = TuohyNeedle.GetComponent<NeedleInteraction>();

        Microcontroller_Manager.ME.SendCommand(Microcontroller_Manager.COMMAND.LORO);
        valveOpen = true;
        lastValveState = true;
    }

    // Update is called once per frame
    void Update()
    {

        // everything here is based on needle interaction.
        // lets first default our needle interaction flags to false.
        needleInSkin = false;
        needleInLung = false;
        needleInLigament = false;
        needleInDura = false;
        needleInSpinalCord = false;

        // are we in the skin? ligament? what's going on with the needle right now?
        // were going to get a big ordered list of what the needle is penetrating, by how much,
        // followed by what is in front of it and by how far.
        // this is IN LINE with the needle axis and it returns alot of information all at once. 
        // we can grab all we need with just two raycasts with this function. the old RA needed dozens.
        NeedleInteractionSummary[] Hits = needleInteraction.TestPenetration(NeedleInteractionLayerMask);

        // HUMAN-READABLE HIT SUMMARY //////////////////////////////////////////////////////////////////////
        // don't use this for coding if you can avoid it - this is for the developer to see WTF is going on.
        // lets build up some human-readable summary of hits for the inspector.
        if (Hits.Length == 0)
        {
            isPenetratings = new bool[0];
            distanceFromTips = new float[0];
            isBackwalleds = new bool[0];
            gameObjectsHit = new GameObject[0];
            layersHit = new int[0];
            nearMissDistance = -1f;
            hitCount = 0;
        }
        else
        {
            // Populate arrays with hit data for inspector viewing
            isPenetratings = new bool[Hits.Length];
            distanceFromTips = new float[Hits.Length];
            isBackwalleds = new bool[Hits.Length];
            gameObjectsHit = new GameObject[Hits.Length];
            layersHit = new int[Hits.Length];
            hitCount = Hits.Length;
            for (int i = 0; i < Hits.Length; i++)
            {
                isPenetratings[i] = Hits[i].isPenetrating;
                distanceFromTips[i] = Hits[i].distanceFromTip;
                isBackwalleds[i] = Hits[i].isBackwalled;
                gameObjectsHit[i] = Hits[i].gameObject;
                layersHit[i] = Hits[i].layer;
            }
        }
        // END HUMAN-READABLE HIT SUMMARY ///////////////////////////////////////////////////////////////////


        // are we in the skin? the skin should be the very first thing in the list, if not - 
        // something isnt right, maybe your anatomical block isnt on the index plate.


        // this is how you test for needle penetrating in a layer at a specific hit order (in this case, the first one)
        needleInSkin = Hits.Length > 0 && ContainsLayer(SkinLayer, Hits[0].layer) && Hits[0].isPenetrating;
        needleInLung = Hits.Any(hit => hit.isPenetrating && ContainsLayer(LungLayer, hit.layer));
        needleInLigament = Hits.Any(hit => hit.isPenetrating && ContainsLayer(LigamentLayer, hit.layer));
        needleBackwalledLigament = Hits.Any(hit => hit.isBackwalled && ContainsLayer(LigamentLayer, hit.layer));
        needleInDura       = Hits.Any(hit => hit.isPenetrating && hit.distanceFromTip > 2.2f && ContainsLayer(DuraLayer, hit.layer)); // allow some penetration before Wet Tap alert
        needleInSpinalCord = Hits.Any(hit => hit.isPenetrating && hit.distanceFromTip > 2.2f && ContainsLayer(SpinalCordLayer, hit.layer)); // allow some penetration before Spinal Injury alert

        // Loss of Resistance (LOR) valve 
        // did we just poke all the way through the back of the ligament? we get a LOR. also in air and (hopefully not) the lung
        valveOpen = !needleInSkin || needleInLung || needleBackwalledLigament;

        if (valveOpen != lastValveState)
        {
            // Microcontroller calls go here
            if (valveOpen == true) { Microcontroller_Manager.ME.SendCommand(Microcontroller_Manager.COMMAND.LORO); Debug.Log("LORO!"); }
            if (valveOpen == false) { Microcontroller_Manager.ME.SendCommand(Microcontroller_Manager.COMMAND.LORC); Debug.Log("LORC!"); }

            lastValveState = valveOpen;
        }

        // Tactile Thumper
        // did we just poke the needle into the ligament? we get a tactile thump.
        if (needleInSkin && needleInLigament && !lastNeedleInLigament)
        {
            // Tactile feedback trigger goes here - only fires on entry
            Microcontroller_Manager.ME.SendCommand(Microcontroller_Manager.COMMAND.LPop); Debug.Log("LPop!");
        }
        lastNeedleInLigament = needleInLigament;


        // Hydrodissection with the Space key
        // Hydrodissection is only possible if the needle is poking all the way through the ligament and not in the lung.
        if (needleInSkin && needleBackwalledLigament && !needleInLung)
        {
            HydrodissectionManager.ME.PressingOnThePlunger = UnityEngine.Input.GetKey("space");
        }

        // Reset stuff when pulling the needle out of the skin
        // did the needle just enter or exit the skin?
        if (needleInSkin != lastSkinState)
        {
            // on needle exit,
            if (needleInSkin == false)
            {
                HydrodissectionManager.ME.Reset(); // do this once because resetting meshes and colliders is expensive.
            }

            lastSkinState = needleInSkin;
        }

        // WARNINGS AND ALERT TOASTS //////////////////////////////////////////////////////////////////
        // dont show warnings that may be caused by needle hemisphere tracking issues 
        if (needleInSkin)
        {

            // did we just enter the lung? scream PNEUMOTHORAX! without spamming the toast.
            if (this.needleInLung && pneumothoraxJustHappened == false)
            {
                Toast.Show(this, "PNEUMOTHORAX", 2, Toast.Type.WARNING, Toast.Gravity.BOTTOM);
                pneumothoraxJustHappened = true;
            }
            if (!this.needleInLung) pneumothoraxJustHappened = false;

            // did we just enter the dura? scream WET TAP! without spamming the toast.
            if (this.needleInDura && wetTapJustHappened == false)
            {
                Toast.Dismiss(); // nevermind anything else in the queue, you just caused a massive headache.
                Toast.Show(this, "WET TAP!", 4, Toast.Type.WARNING, Toast.Gravity.BOTTOM);
                wetTapJustHappened = true;
            }
            if (!this.needleInDura) wetTapJustHappened = false;

            // did we just enter the spinal cord? scream SPINAL INJURY! without spamming the toast.
            if (this.needleInSpinalCord && spinalInjuryJustHappened == false)
            {
                Toast.Dismiss(); // nevermind anything else in the queue, you just paralyzed someone.
                Toast.Show(this, "SPINAL INJURY!", 4, Toast.Type.ERROR, Toast.Gravity.CENTER);
                spinalInjuryJustHappened = true;
            }
            if (!this.needleInSpinalCord) spinalInjuryJustHappened = false;

        }












}


    // HELPER FUNCTIONS
    public static bool ContainsLayer(LayerMask mask, int layer)
    {
        return (mask & (1 << layer)) != 0;
    }

}
