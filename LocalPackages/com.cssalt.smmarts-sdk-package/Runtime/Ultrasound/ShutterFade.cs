using SMMARTS;
using UnityEngine;
using UnityEngine.UI;

public class ShutterFade : MonoBehaviour
{
    [SerializeField] private float minAlpha = 0.2f;
    [SerializeField] private float fadeSpeed = 0.5f;

    private Image _image;

    void Awake()
    {
        _image = GetComponent<Image>();
    }

    void Update()
    {
        bool cameraShutter = Microcontroller_Manager.ME.TUIPressed;

        float target = cameraShutter ? minAlpha : 1f;

        Color c = _image.color;
        c.a = Mathf.MoveTowards(c.a, target, fadeSpeed * Time.deltaTime);
        _image.color = c;
    }
}
