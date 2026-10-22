using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

public class GhostCArmManager : MonoBehaviour
{
    public static GhostCArmManager ME;

    [Header("Ghost State")]
    public bool GhostState;
    public bool Collision;
    public bool HumanInLoopMode;

    [Header("Materials")]
    [SerializeField] private Material ghostGreen;
    [SerializeField] private Material ghostRed;

    [Header("Swappers")]
    [SerializeField] private List<GhostCArmMaterialSwapper> swappers = new();

    [Header("Skip List")]
    [SerializeField] private List<GameObject> skipGOs = new();

    [Header("Cameras")]
    [SerializeField] private Camera techViewpointCamera;
    [SerializeField] private Camera collisionRedPredictionCamera;
    [SerializeField] private Camera noCollisionGreenPredictionCamera;

    [Header("Render Textures")]
    [SerializeField] private RenderTexture greenRT;
    [SerializeField] private RenderTexture redRT;

    [Header("Prediction Hide Panel")]
    [SerializeField] private GameObject predictionHidePanel;
    [SerializeField] private RawImage predictionHideImage;

    [Header("Collision Warning Panel")]
    [SerializeField] private GameObject collisionWarningPanel;
    [SerializeField] private RawImage greenResultImage;
    [SerializeField] private RawImage redResultImage;

    [Header("HL Confirmation Panel")]
    [SerializeField] private GameObject hlConfirmationPanel;
    [SerializeField] private RawImage hlGreenResultImage;

    [Header("Events")]
    [SerializeField] private UnityEvent onCollisionDetected;
    [SerializeField] private UnityEvent onUndoAndCorrect;
    [SerializeField] private UnityEvent onPerformAlternative;
    [SerializeField] private UnityEvent onHLConfirmation;
    [SerializeField] private UnityEvent onPerformRequestedAction;

    private const int SafeBufferSize = 10;
    private SMMARTS.C_ArmPositioner.PositionSnapshot[] safeSnapshotBuffer =
        new SMMARTS.C_ArmPositioner.PositionSnapshot[SafeBufferSize];
    private int bufferIndex = 0;


    private bool lastGhostState;
    private bool lastCollision;
    private RenderTexture freezeRT;
    private int _techCameraOriginalCullingMask;
    private SMMARTS.C_ArmPositioner.PositionSnapshot lastSafeSnapshot;
    private SMMARTS.C_ArmPositioner.PositionSnapshot requestedEndpointSnapshot;

    public bool PredictionRunning { get; private set; }
    public bool CollisionFoundDuringPrediction { get; private set; }

    public Material GhostGreen => ghostGreen;
    public Material GhostRed => ghostRed;

    void Awake()
    {
        ME = this;
    }

    void Start()
    {
        foreach (MeshRenderer mr in GetComponentsInChildren<MeshRenderer>(true))
        {
            if (skipGOs.Contains(mr.gameObject)) continue;
            GhostCArmMaterialSwapper swapper = mr.gameObject.GetComponent<GhostCArmMaterialSwapper>();
            if (swapper == null)
                swapper = mr.gameObject.AddComponent<GhostCArmMaterialSwapper>();
            swappers.Add(swapper);
        }

        freezeRT = new RenderTexture(Screen.width, Screen.height, 0);
        if (predictionHideImage != null)
            predictionHideImage.texture = freezeRT;

        if (collisionRedPredictionCamera != null)
            collisionRedPredictionCamera.enabled = false;
        if (noCollisionGreenPredictionCamera != null)
            noCollisionGreenPredictionCamera.enabled = false;
        if (predictionHidePanel != null)
            predictionHidePanel.SetActive(false);
        if (collisionWarningPanel != null)
            collisionWarningPanel.SetActive(false);
    }

    void Update()
    {
        if (PredictionRunning) return;

        GhostState = SMMARTS.C_ArmPositioner.ME.IsMoving;
        Collision = CArmCollisionDetector.Instance.InterferenceDetected;

        if (lastGhostState != GhostState)
        {
            lastGhostState = GhostState;
            OnGhostStateChanged();
        }

        if (lastCollision != Collision)
        {
            lastCollision = Collision;
            OnCollisionChanged();
        }
    }

    // for Turning Human in the Loop mode on and off from buttons
    public void TurnHumanInLoopModeOn(){ HumanInLoopMode = true;}
    public void TurnHumanInLoopModeOff() { HumanInLoopMode = false; }

    void OnGhostStateChanged()
    {
        if (GhostState)
            ApplyCollisionColor();
        else
            SetDefault();
    }

    void OnCollisionChanged()
    {
        if (!GhostState) return;
        ApplyCollisionColor();
        SMMARTS.C_ArmPositioner.ME.MovementBlocked = Collision;
        if (Collision)
        {
            collisionWarningPanel.SetActive(true);
            onCollisionDetected.Invoke();
        }
    }

    void ApplyCollisionColor()
    {
        if (Collision)
            SetRed();
        else
            SetDefault();
    }

    public void SetGreen()
    {
        foreach (var s in swappers) s.SetGreen();
    }

    public void SetRed()
    {
        foreach (var s in swappers) s.SetRed();
    }

    public void SetDefault()
    {
        foreach (var s in swappers) s.SetDefault();
    }

    // -----------------------------------------------------------------------
    // Prediction pipeline
    // -----------------------------------------------------------------------

    /// <summary>
    /// Called by LLMCommandDispatcher instead of StreamMove directly.
    /// speed and minDuration come from LLMVoiceControl's inspector values,
    /// passed in by the dispatcher since we can't reference LLMVoice assembly.
    /// </summary>
    public IEnumerator PredictThenStream(string axis, float totalDelta,
                                          IEnumerator streamMove,
                                          float speed, float minDuration)
    {
        PredictionRunning = true;
        CollisionFoundDuringPrediction = false;

        // 1. freeze main display
        CaptureFreeze();
        yield return null;
        predictionHidePanel.SetActive(true);

        // 2. snapshot current position
        SMMARTS.C_ArmPositioner.ME.TakeSnapshot();
        lastSafeSnapshot = SMMARTS.C_ArmPositioner.ME.GetCurrentPositionAsSnapshot();

        // 3. green camera on, CA goes green
        noCollisionGreenPredictionCamera.enabled = true;
        SetGreen();

        // 4. animate, monitoring collision each frame
        float magnitude = Mathf.Abs(totalDelta);
        float sign = Mathf.Sign(totalDelta);
        float spd = Mathf.Max(0.01f, speed);
        float duration = Mathf.Max(minDuration, magnitude / spd);
        float effSpeed = magnitude / duration;
        float remaining = magnitude;

        // reset buffer for this prediction run
        bufferIndex = 0;

        while (remaining > 0.0001f)
        {
            float step = Mathf.Min(remaining, effSpeed * Time.deltaTime);
            remaining -= step;
            SMMARTS.C_ArmPositioner.ME.UpdatePosition(axis, step * sign);
            yield return null;

            bool hit = CArmCollisionDetector.Instance.InterferenceDetected;

            if (!hit)
            {
                // write current position into ring buffer
                safeSnapshotBuffer[bufferIndex % SafeBufferSize] =
                    SMMARTS.C_ArmPositioner.ME.GetCurrentPositionAsSnapshot();
                bufferIndex++;
            }
            else if (!CollisionFoundDuringPrediction)
            {
                CollisionFoundDuringPrediction = true;
                // read oldest entry in ring buffer — SafeBufferSize frames behind collision
                lastSafeSnapshot = safeSnapshotBuffer[bufferIndex % SafeBufferSize];
                noCollisionGreenPredictionCamera.enabled = false;
                collisionRedPredictionCamera.enabled = true;
                SetRed();
            }
        }

        // capture full requested endpoint before rewind
        requestedEndpointSnapshot = SMMARTS.C_ArmPositioner.ME.GetCurrentPositionAsSnapshot();

        // 5. turn off prediction cameras
        noCollisionGreenPredictionCamera.enabled = false;
        collisionRedPredictionCamera.enabled = false;

        // 6. rewind CA to pre-command position
        SMMARTS.C_ArmPositioner.ME.RestoreSnapshot();
        SetDefault();

        // 7. hide freeze panel
        techViewpointCamera.cullingMask = _techCameraOriginalCullingMask;
        predictionHidePanel.SetActive(false);

        PredictionRunning = false;

        // 8. show result
        if (CollisionFoundDuringPrediction)
        {
            ShowCollisionWarning();
        }
        else if (HumanInLoopMode)
        {
            ShowHLConfirmation();
        }
        else
        {
            // clean run, no HL mode — execute normally
            yield return streamMove;
        }
    }

    void CaptureFreeze()
    {
        var prev = techViewpointCamera.targetTexture;
        techViewpointCamera.targetTexture = freezeRT;
        techViewpointCamera.Render();
        techViewpointCamera.targetTexture = prev;

        // hide CA from tech camera during prediction
        _techCameraOriginalCullingMask = techViewpointCamera.cullingMask;
        techViewpointCamera.cullingMask &= ~LayerMask.GetMask("Fluoroscopy Display");
    }

    void ShowCollisionWarning()
    {
        if (greenResultImage != null) greenResultImage.texture = greenRT;
        if (redResultImage != null)
        {
            redResultImage.texture = redRT;
            redResultImage.gameObject.SetActive(true);
        }
        collisionWarningPanel.SetActive(true);
        onCollisionDetected.Invoke();
    }

    void ShowHLConfirmation()
    {
        if (hlGreenResultImage != null) hlGreenResultImage.texture = greenRT;
        hlConfirmationPanel.SetActive(true);
        onHLConfirmation.Invoke();
    }

    public void OnHLConfirmVoice()
    {
        bool wasCollision = collisionWarningPanel.activeSelf;
        collisionWarningPanel.SetActive(false);
        hlConfirmationPanel.SetActive(false);
        if (wasCollision)
            StartCoroutine(AnimateToLastSafe());
        else
            StartCoroutine(AnimateToRequestedEndpoint());
    }

    /// <summary>Called by Revert button on warning panel.</summary>
    public void OnRevert()
    {
        collisionWarningPanel.SetActive(false);
        hlConfirmationPanel.SetActive(false);
        if (redResultImage != null) redResultImage.gameObject.SetActive(true);
        onUndoAndCorrect.Invoke();
    }

    /// <summary>Called by Perform Alternative button on warning panel.</summary>
    public void OnPerformAlternative()
    {
        collisionWarningPanel.SetActive(false);
        if (redResultImage != null) redResultImage.gameObject.SetActive(true);
        StartCoroutine(AnimateToLastSafe());
    }

    public void OnPerformRequestedAction()
    {
        hlConfirmationPanel.SetActive(false);
        StartCoroutine(AnimateToRequestedEndpoint());
    }

    IEnumerator AnimateToRequestedEndpoint()
    {
        yield return null;
        var target = requestedEndpointSnapshot;
        var pos = SMMARTS.C_ArmPositioner.ME;

        if (!Mathf.Approximately(target.baseInOut, pos.baseInOutPosition) ||
            !Mathf.Approximately(target.baseSlide, pos.baseSlidePosition))
        {
            pos.SetBrake("Base", true);
            yield return StartCoroutine(AnimateAxis("BaseInOut", target.baseInOut));
            yield return StartCoroutine(AnimateAxis("BaseSlide", target.baseSlide));
            pos.SetBrake("Base", false);
        }
        if (!Mathf.Approximately(target.upDown, pos.upDownPosition))
        {
            pos.SetBrake("UpDown", true);
            yield return StartCoroutine(AnimateAxis("UpDown", target.upDown));
            pos.SetBrake("UpDown", false);
        }
        if (!Mathf.Approximately(target.inOut, pos.inOutPosition))
        {
            pos.SetBrake("InOut", true);
            yield return StartCoroutine(AnimateAxis("InOut", target.inOut));
            pos.SetBrake("InOut", false);
        }
        if (!Mathf.Approximately(target.wigWag, pos.wigWagRotation))
        {
            pos.SetBrake("WigWag", true);
            yield return StartCoroutine(AnimateAxis("WigWag", target.wigWag));
            pos.SetBrake("WigWag", false);
        }
        if (!Mathf.Approximately(target.tilt, pos.tiltRotation))
        {
            pos.SetBrake("Tilt", true);
            yield return StartCoroutine(AnimateAxis("Tilt", target.tilt));
            pos.SetBrake("Tilt", false);
        }
        if (!Mathf.Approximately(target.orbit, pos.orbitRotation))
        {
            pos.SetBrake("Orbit", true);
            yield return StartCoroutine(AnimateAxis("Orbit", target.orbit));
            pos.SetBrake("Orbit", false);
        }

        onPerformRequestedAction.Invoke();
    }

    IEnumerator AnimateToLastSafe()
    {
        yield return null;
        var safe = lastSafeSnapshot;
        var pos = SMMARTS.C_ArmPositioner.ME;

        if (!Mathf.Approximately(safe.baseInOut, pos.baseInOutPosition) ||
            !Mathf.Approximately(safe.baseSlide, pos.baseSlidePosition))
        {
            pos.SetBrake("Base", true);
            yield return StartCoroutine(AnimateAxis("BaseInOut", safe.baseInOut));
            yield return StartCoroutine(AnimateAxis("BaseSlide", safe.baseSlide));
            pos.SetBrake("Base", false);
        }
        if (!Mathf.Approximately(safe.upDown, pos.upDownPosition))
        {
            pos.SetBrake("UpDown", true);
            yield return StartCoroutine(AnimateAxis("UpDown", safe.upDown));
            pos.SetBrake("UpDown", false);
        }
        if (!Mathf.Approximately(safe.inOut, pos.inOutPosition))
        {
            pos.SetBrake("InOut", true);
            yield return StartCoroutine(AnimateAxis("InOut", safe.inOut));
            pos.SetBrake("InOut", false);
        }
        if (!Mathf.Approximately(safe.wigWag, pos.wigWagRotation))
        {
            pos.SetBrake("WigWag", true);
            yield return StartCoroutine(AnimateAxis("WigWag", safe.wigWag));
            pos.SetBrake("WigWag", false);
        }
        if (!Mathf.Approximately(safe.tilt, pos.tiltRotation))
        {
            pos.SetBrake("Tilt", true);
            yield return StartCoroutine(AnimateAxis("Tilt", safe.tilt));
            pos.SetBrake("Tilt", false);
        }
        if (!Mathf.Approximately(safe.orbit, pos.orbitRotation))
        {
            pos.SetBrake("Orbit", true);
            yield return StartCoroutine(AnimateAxis("Orbit", safe.orbit));
            pos.SetBrake("Orbit", false);
        }

        onPerformAlternative.Invoke();
    }
    IEnumerator AnimateAxis(string axis, float target)
    {
        float current = SMMARTS.C_ArmPositioner.ME.ReadAxis(axis);
        float delta = target - current;
        if (Mathf.Approximately(delta, 0f)) yield break;

        // use a reasonable default speed since we can't reach LLMVoiceControl
        float spd = 150f;
        float magnitude = Mathf.Abs(delta);
        float duration = Mathf.Max(0.25f, magnitude / spd);
        float effSpeed = magnitude / duration;
        float remaining = magnitude;
        float sign = Mathf.Sign(delta);

        while (remaining > 0.0001f)
        {
            float step = Mathf.Min(remaining, effSpeed * Time.deltaTime);
            remaining -= step;
            SMMARTS.C_ArmPositioner.ME.UpdatePosition(axis, step * sign);
            yield return null;
        }
    }
}