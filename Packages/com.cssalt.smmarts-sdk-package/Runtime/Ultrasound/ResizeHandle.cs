using UnityEngine;
using UnityEngine.EventSystems;

public class ResizeHandle : MonoBehaviour,
    IBeginDragHandler, IDragHandler
{
    private DraggableResizable _parent;
    private RectTransform _parentRect;
    private RectTransform _handleRect;
    private Vector2 _lastPointer;

    void Awake()
    {
        _parent = GetComponentInParent<DraggableResizable>();
        _parentRect = _parent.GetComponent<RectTransform>();
        _handleRect = GetComponent<RectTransform>();
    }

    public void OnBeginDrag(PointerEventData eventData)
    {
        _lastPointer = eventData.position;
    }

    public void OnDrag(PointerEventData eventData)
    {
        Vector2 delta = eventData.position - _lastPointer;
        _lastPointer = eventData.position;
        _parent.OnResize(delta);

        // Snap to square: biggest dimension wins
        float biggest = Mathf.Max(_parentRect.sizeDelta.x, _parentRect.sizeDelta.y);
        _parentRect.sizeDelta = new Vector2(biggest, biggest);


    }

    public void LateUpdate()
    {
        // Pin handle to lower-right corner of parent
        // Parent pivot is top-left (0,1), so lower-right in local space
        // Note, the anchor (gameobject.this) handle is upper left corner, with pivot in the middle at (0.5, 05)
        _handleRect.anchoredPosition = new Vector2(
            _parentRect.sizeDelta.x,
            -_parentRect.sizeDelta.y
        );
    }



}
