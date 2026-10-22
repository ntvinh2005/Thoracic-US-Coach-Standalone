using UnityEngine;

// This class is simply to display a gameobject for a user-adjustable time upon startup, and turn it off.

public class ShowOnStartForSeconds : MonoBehaviour
{

    [SerializeField] float SecondsUntilHide;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        if (SecondsUntilHide > 0) Invoke("TimeToHide", SecondsUntilHide);
    }


    void TimeToHide()
    {
        gameObject.SetActive(false);
    }
}
