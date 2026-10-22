using System;
using UnityEngine;
using UnityEngine.EventSystems;

public class DraggableResizable : MonoBehaviour,
    IBeginDragHandler, IDragHandler
{
    public Vector2 minSize = new Vector2(100f, 100f);

    public event Action OnResized;

    private RectTransform _rect;
    private Vector2 _dragOffset;

    void Awake()
    {
        _rect = GetComponent<RectTransform>();

        // Shift pivot to top-left without moving the object visually
        Vector2 size = _rect.rect.size;
        Vector2 deltaPivot = new Vector2(0f, 1f) - _rect.pivot;
        _rect.pivot = new Vector2(0f, 1f);
        _rect.anchoredPosition += new Vector2(
            deltaPivot.x * size.x,
            deltaPivot.y * size.y
        );
    }

    // ── Drag ──────────────────────────────────────────────────

    public void OnBeginDrag(PointerEventData eventData)
    {
        _dragOffset = _rect.anchoredPosition - eventData.position;
    }

    public void OnDrag(PointerEventData eventData)
    {
        _rect.anchoredPosition = eventData.position + _dragOffset;
    }

    // ── Called by ResizeHandle ─────────────────────────────────

    public void OnResize(Vector2 delta)
    {
        Vector2 newSize = _rect.sizeDelta + new Vector2(delta.x, -delta.y);
        newSize.x = Mathf.Max(newSize.x, minSize.x);
        newSize.y = Mathf.Max(newSize.y, minSize.y);
        _rect.sizeDelta = newSize;

        OnResized?.Invoke();
    }
}
