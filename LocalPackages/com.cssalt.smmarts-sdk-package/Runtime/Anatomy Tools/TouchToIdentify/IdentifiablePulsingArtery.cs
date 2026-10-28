using UnityEngine;
using System.Collections;
using SMMARTS;

/// <summary>
/// IdentifiablePulsingArtery: Special version of IdentifiableObject for pulsing arteries
/// 
/// Extends IdentifiableObject to work with ProceduralPulsingArtery parent.
/// Delegates highlight management to parent coordinator while maintaining
/// all individual touch detection and ultrasound rendering capabilities.
/// </summary>
public class IdentifiablePulsingArtery : IdentifiableObject
{
    private ProceduralPulsingArtery parentCoordinator;

    /// <summary>
    /// Initialize and register with parent coordinator
    /// </summary>
    void Start()
    {
        // Call parent Start() for normal IdentifiableObject initialization
        base.Start();

        // Find and register with parent coordinator
        parentCoordinator = GetComponentInParent<ProceduralPulsingArtery>();
        if (parentCoordinator != null)
        {
            parentCoordinator.RegisterChildArtery(this);
        }
        else
        {
            Debug.LogWarning($"IdentifiableArtery: {gameObject.name} couldn't find ProceduralPulsingArtery parent!");
        }
    }

    /// <summary>
    /// Override highlight to delegate to parent coordinator
    /// </summary>
    public override void StartHighlight(Material glowMaterial, float duration)
    {
        if (parentCoordinator != null)
        {
            // Let the parent handle the coordinated highlight
            parentCoordinator.StartCoordinatedHighlight(glowMaterial, duration);
        }
        else
        {
            // Fallback to normal behavior if no coordinator found
            base.StartHighlight(glowMaterial, duration);
        }
    }

    /// <summary>
    /// Public method for parent coordinator to apply highlight to this specific variant
    /// Bypasses the normal StartHighlight delegation
    /// </summary>
    public void ApplyDirectHighlight(Material glowMaterial)
    {
        // Use parent's highlight methods directly, skip the timeout coroutine
        if (glowMaterial != null)
        {
            Add3DHighlight(glowMaterial);
        }
        StartUltrasoundHighlight();
        isHighlighted = true;
    }

    /// <summary>
    /// Public method for parent coordinator to remove highlight from this specific variant
    /// </summary>
    public void RemoveDirectHighlight()
    {
        Remove3DHighlight();
        RemoveUltrasoundHighlight();
        isHighlighted = false;
    }
}