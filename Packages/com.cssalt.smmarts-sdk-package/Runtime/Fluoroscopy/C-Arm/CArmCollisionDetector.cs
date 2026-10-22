using UnityEngine;
using System.Collections.Generic;

public class CArmCollisionDetector : MonoBehaviour
{
    public static CArmCollisionDetector Instance { get; private set; }

    [SerializeField] private string cArmTag = "CArm";
    [SerializeField] private string tablePatientTag = "TablePatient";

    [SerializeField] private bool interferenceDetected = false;
    [SerializeField] private List<GameObject> cArmObjects = new List<GameObject>();
    [SerializeField] private List<GameObject> tablePatientObjects = new List<GameObject>();

    public bool InterferenceDetected => interferenceDetected;

    public GameObject CollisionWarning;

    void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
            RegisterColliders();
        }
        else
        {
            Destroy(gameObject);
        }
    }

    void RegisterColliders()
    {
        // Find and register all C-Arm objects
        GameObject[] cArmParts = GameObject.FindGameObjectsWithTag(cArmTag);

        foreach (GameObject part in cArmParts)
        {
            // Add CollisionReporter component if it doesn't exist
            if (part.GetComponent<CollisionReporter>() == null)
            {
                part.AddComponent<CollisionReporter>();
            }
            cArmObjects.Add(part);
        }

        // Find and register all Table and Patient objects
        GameObject[] tablePatientParts = GameObject.FindGameObjectsWithTag(tablePatientTag);

        foreach (GameObject part in tablePatientParts)
        {
            tablePatientObjects.Add(part);
        }

        Debug.Log($"Registered {cArmObjects.Count} C-Arm collision objects");
    }

    public void OnInterferenceDetected(Collider cArmCollider, Collider otherCollider)
    {
        if (otherCollider.CompareTag(tablePatientTag))
        {
            interferenceDetected = true;
            Debug.Log($"Interference: {cArmCollider.gameObject.name} hit {otherCollider.gameObject.name}");
        }
    }

    public void OnInterferenceCleared(Collider cArmCollider, Collider otherCollider)
    {
        if (otherCollider.CompareTag(tablePatientTag))
        {
            // Check if any other interferences still exist
            CheckForRemainingInterferences();
        }
    }

    public bool IsInterferenceDetected()
    {
        return interferenceDetected;
    }

    public void ClearInterferenceFlag()
    {
        interferenceDetected = false;
    }

    void CheckForRemainingInterferences()
    {
        // Simple approach: just clear the flag
        // More complex logic could track multiple simultaneous collisions
        interferenceDetected = false;
    }

    void Update()
    {
        CollisionWarning.SetActive(interferenceDetected);

    }
}

public class CollisionReporter : MonoBehaviour
{
    void OnTriggerEnter(Collider other)
    {
        if (CArmCollisionDetector.Instance != null)
        {
            CArmCollisionDetector.Instance.OnInterferenceDetected(GetComponent<Collider>(), other);
        }
    }

    void OnTriggerExit(Collider other)
    {
        if (CArmCollisionDetector.Instance != null)
        {
            CArmCollisionDetector.Instance.OnInterferenceCleared(GetComponent<Collider>(), other);
        }
    }
}