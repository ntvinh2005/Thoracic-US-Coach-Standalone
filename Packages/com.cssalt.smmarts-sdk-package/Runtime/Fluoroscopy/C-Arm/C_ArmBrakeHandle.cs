using SMMARTS;
using UnityEngine;

public class C_ArmBrakeHandle : MonoBehaviour
{
    public string brakeType; // "InOut", "WigWag", "Tilt", "Orbit"

    public void OnMouseDown()
    {
        C_ArmPositioner.ME.ToggleBrake(brakeType);
    }
}