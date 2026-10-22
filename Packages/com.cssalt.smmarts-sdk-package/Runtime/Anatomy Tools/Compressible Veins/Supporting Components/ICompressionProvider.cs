using UnityEngine;

public interface ICompressionProvider
{
	Vector3 CompressionCenter { get; }
	Vector3 CompressionNormal { get; }
	float CompressionDepth { get; } // in millimeters
	float ContactPercent { get; }

    // NEW: Actual probe contact points for shape-aware compression
    Vector3[] ContactPoints { get; }
}

