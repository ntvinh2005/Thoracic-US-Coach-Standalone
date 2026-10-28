using UnityEngine;

public class SlavePlane : MonoBehaviour
{
    public DraggableResizable master;

    private RectTransform _rect;
    private RectTransform _masterRect;
    private float _masterOriginalWidth;

    void Start()
    {
        _rect = GetComponent<RectTransform>();
        _masterRect = master.GetComponent<RectTransform>();

        // Snapshot master's designed width as the scale baseline
        _masterOriginalWidth = _masterRect.sizeDelta.x;

    }


    void Update()
    {
        Vector2 masterSize = _masterRect.sizeDelta;

        // Scale uniformly relative to master's original size
        float scale = masterSize.x / _masterOriginalWidth;
        _rect.localScale = Vector3.one * scale;

        // Pin top-center of slave to bottom-center of master
        // Master pivot is top-left so bottom-center is (width/2, -height)
        // We divide by scale to account for the parent's scaled coordinate space
        _rect.anchoredPosition = new Vector2(
            masterSize.x / 2f,
            -masterSize.y
        );
    }
}
