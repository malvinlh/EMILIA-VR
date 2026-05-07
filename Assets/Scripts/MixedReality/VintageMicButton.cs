using System;
using System.Collections;
using UnityEngine;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>
/// A 3D VR-friendly mic toggle button for the VintageMicrophone prop.
/// Supports both XRI controller ray/select AND hand fingertip poke.
///
/// Pattern mirrors JournalStartButton — attach to the 'button' child GO of
/// VintageMicrophone, add a BoxCollider and XRSimpleInteractable, then
/// assign buttonRenderer in the Inspector.
///
/// Visual state is driven by JournalMicController via SetVisualState(); the
/// material color pulses while recording (oscillates between the target color
/// and a dimmed version — works on opaque URP materials without needing alpha).
/// </summary>
[RequireComponent(typeof(BoxCollider))]
[RequireComponent(typeof(XRSimpleInteractable))]
public class VintageMicButton : MonoBehaviour
{
    [Header("Visual Feedback")]
    [Tooltip("Renderer to tint on hover/press. Falls back to this GO's own Renderer if null.")]
    public Renderer buttonRenderer;

    [Tooltip("Color when idle (not recording).")]
    public Color idleColor         = new Color(0.85f, 0.80f, 0.65f, 1f); // warm parchment

    [Tooltip("Color while recording.")]
    public Color recordingColor    = new Color(0.90f, 0.25f, 0.25f, 1f); // red

    [Tooltip("Color while transcribing.")]
    public Color transcribingColor = new Color(0.90f, 0.70f, 0.10f, 1f); // yellow

    [Tooltip("Color when hovering (controller ray or hand near).")]
    public Color hoverColor        = new Color(1.00f, 0.92f, 0.60f, 1f); // warm amber

    [Tooltip("How much the button sinks in (metres) on press.")]
    [Range(0f, 0.02f)]
    public float pressDepth = 0.004f;

    [Header("Pulse (while recording)")]
    [Tooltip("Fraction of the recording color used for the dim phase of the pulse (0 = black, 1 = full color).")]
    [Range(0.1f, 0.9f)]
    public float pulseDimFraction = 0.35f;

    [Tooltip("Pulse cycles per second.")]
    [Range(0.5f, 5f)]
    public float pulseSpeed = 2.5f;

    [Header("Hand Tracking")]
    [Tooltip("Distance (metres) at which the hover glow activates when a hand is near.")]
    [Range(0.02f, 0.15f)]
    public float hoverDistance = 0.06f;

    [Header("Cooldown")]
    [Tooltip("Minimum seconds between activations. Prevents double-fire from hand gestures.")]
    [Range(0.1f, 2f)]
    public float cooldown = 0.25f;

    // ── Events ──────────────────────────────────────────────────────────
    /// <summary>Fired when the button is activated (controller select or hand poke).</summary>
    public event Action OnActivated;

    // ── State ───────────────────────────────────────────────────────────
    private XRHandSubsystem      _handSubsystem;
    private XRSimpleInteractable _xrInteractable;
    private BoxCollider          _collider;
    private Material             _material;
    private Vector3              _idleLocalPos;
    private Vector3              _pressLocalAxis;
    private float                _cooldownTimer;
    private bool                 _isOnCooldown;
    private bool                 _wasHovering;
    private bool                 _xrHovering;
    private Coroutine            _pulseCo;
    private Color                _currentBaseColor;

    // ── Interactable toggle ──────────────────────────────────────────────
    /// <summary>
    /// Enables or disables the underlying XRSimpleInteractable. When false the
    /// button ignores controller ray events (hand poke is still guarded by
    /// JournalMicController's session check).
    /// </summary>
    public bool IsInteractable
    {
        get => _xrInteractable != null && _xrInteractable.enabled;
        set { if (_xrInteractable != null) _xrInteractable.enabled = value; }
    }

    // ================================================================
    // LIFECYCLE
    // ================================================================

    private void Awake()
    {
        _collider = GetComponent<BoxCollider>();
        _collider.isTrigger = false;   // must be non-trigger for NearFarInteractor raycasts

        _xrInteractable = GetComponent<XRSimpleInteractable>();

        if (buttonRenderer == null)
            buttonRenderer = GetComponent<Renderer>();

        if (buttonRenderer != null)
        {
            _material = buttonRenderer.material;   // clone so we don't modify the shared asset
            _material.color = idleColor;
        }

        _currentBaseColor = idleColor;
        _idleLocalPos     = transform.localPosition;

        _pressLocalAxis = transform.InverseTransformDirection(transform.forward);
        if (_pressLocalAxis.sqrMagnitude < 0.0001f) _pressLocalAxis = Vector3.forward;
        else _pressLocalAxis.Normalize();
    }

    private void OnEnable()
    {
        if (_xrInteractable != null)
        {
            _xrInteractable.selectEntered.AddListener(OnControllerSelect);
            _xrInteractable.hoverEntered.AddListener(OnControllerHoverEnter);
            _xrInteractable.hoverExited.AddListener(OnControllerHoverExit);
        }

        _isOnCooldown = false;
        _cooldownTimer = 0f;
        _wasHovering = false;
        _xrHovering  = false;
        StopPulse();
        SetMaterialColor(idleColor);
        if (_idleLocalPos != Vector3.zero)
            transform.localPosition = _idleLocalPos;
    }

    private void OnDisable()
    {
        if (_xrInteractable != null)
        {
            _xrInteractable.selectEntered.RemoveListener(OnControllerSelect);
            _xrInteractable.hoverEntered.RemoveListener(OnControllerHoverEnter);
            _xrInteractable.hoverExited.RemoveListener(OnControllerHoverExit);
        }

        _xrHovering = false;
        StopPulse();
        SetMaterialColor(idleColor);
    }

    private void OnDestroy()
    {
        if (_material != null) Destroy(_material);
    }

    // ================================================================
    // CONTROLLER INPUT (XRI)
    // ================================================================

    private void OnControllerSelect(SelectEnterEventArgs args) => TriggerActivation();

    private void OnControllerHoverEnter(HoverEnterEventArgs args)
    {
        if (!_isOnCooldown && _pulseCo == null)
        {
            SetMaterialColor(hoverColor);
            _xrHovering = true;
        }
    }

    private void OnControllerHoverExit(HoverExitEventArgs args)
    {
        _xrHovering = false;
        if (!_isOnCooldown && _pulseCo == null)
        {
            SetMaterialColor(_currentBaseColor);
            transform.localPosition = _idleLocalPos;
        }
    }

    // ================================================================
    // HAND TRACKING — FINGERTIP POKE
    // ================================================================

    private void Update()
    {
        // Cooldown tick
        if (_isOnCooldown)
        {
            _cooldownTimer += Time.deltaTime;
            if (_cooldownTimer >= cooldown)
            {
                _isOnCooldown  = false;
                _cooldownTimer = 0f;
                transform.localPosition = _idleLocalPos;
                if (_pulseCo == null)
                    SetMaterialColor(_currentBaseColor);
            }
            return;
        }

        // Lazy subsystem init
        if (_handSubsystem == null || !_handSubsystem.running)
        {
            _handSubsystem = WhiteboardPen.GetHandSubsystem();
            if (_handSubsystem == null) return;
        }

        bool poked = TryPokeWithHand(_handSubsystem.rightHand)
                  || TryPokeWithHand(_handSubsystem.leftHand);

        bool isHovering = IsHandHovering(_handSubsystem.rightHand)
                       || IsHandHovering(_handSubsystem.leftHand);

        if (!poked && _pulseCo == null)
        {
            if (isHovering && !_wasHovering)
                SetMaterialColor(hoverColor);
            else if (!isHovering && _wasHovering && !_xrHovering)
                SetMaterialColor(_currentBaseColor);
        }

        _wasHovering = isHovering;
    }

    private bool TryPokeWithHand(XRHand hand)
    {
        if (!hand.isTracked) return false;
        var joint = hand.GetJoint(XRHandJointID.IndexTip);
        if (!joint.TryGetPose(out Pose tip)) return false;
        if (!_collider.bounds.Contains(tip.position)) return false;
        TriggerActivation();
        return true;
    }

    private bool IsHandHovering(XRHand hand)
    {
        if (!hand.isTracked) return false;
        var joint = hand.GetJoint(XRHandJointID.IndexTip);
        if (!joint.TryGetPose(out Pose tip)) return false;
        return Vector3.Distance(tip.position, _collider.ClosestPoint(tip.position)) <= hoverDistance;
    }

    // ================================================================
    // ACTIVATION
    // ================================================================

    private void TriggerActivation()
    {
        if (_isOnCooldown) return;

        _isOnCooldown  = true;
        _cooldownTimer = 0f;
        _xrHovering    = false;

        // Physical press-depth feedback
        transform.localPosition = _idleLocalPos - _pressLocalAxis * pressDepth;

        OnActivated?.Invoke();
    }

    // ================================================================
    // VISUAL STATE (driven externally by JournalMicController)
    // ================================================================

    // ── Convenience state setters (called by JournalMicController) ──────
    /// <summary>Returns the button to its idle appearance.</summary>
    public void SetIdle()         => SetVisualState(idleColor,         pulse: false);
    /// <summary>Shows the recording state (red + pulsing).</summary>
    public void SetRecording()    => SetVisualState(recordingColor,    pulse: true);
    /// <summary>Shows the transcribing/waiting state (yellow, no pulse).</summary>
    public void SetTranscribing() => SetVisualState(transcribingColor, pulse: false);

    /// <summary>
    /// Low-level visual state setter. Prefer SetIdle/SetRecording/SetTranscribing
    /// when the state maps to one of the three named states.
    /// </summary>
    public void SetVisualState(Color color, bool pulse)
    {
        _currentBaseColor = color;
        StopPulse();
        if (pulse)
            _pulseCo = StartCoroutine(CoPulse(color));
        else
            SetMaterialColor(color);
    }

    private IEnumerator CoPulse(Color highColor)
    {
        Color lowColor = new Color(
            highColor.r * pulseDimFraction,
            highColor.g * pulseDimFraction,
            highColor.b * pulseDimFraction,
            highColor.a);

        float t = 0f;
        while (true)
        {
            t += Time.unscaledDeltaTime * pulseSpeed;
            float s = 0.5f + 0.5f * Mathf.Sin(t);
            SetMaterialColor(Color.Lerp(lowColor, highColor, s));
            yield return null;
        }
    }

    private void StopPulse()
    {
        if (_pulseCo == null) return;
        StopCoroutine(_pulseCo);
        _pulseCo = null;
    }

    private void SetMaterialColor(Color color)
    {
        if (_material != null) _material.color = color;
    }
}
