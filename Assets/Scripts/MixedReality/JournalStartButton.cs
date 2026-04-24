using System;
using TMPro;
using UnityEngine;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>
/// A world-space button for VR that works with BOTH hand tracking and controllers.
///
/// Hand tracking: detects right hand index fingertip entering the BoxCollider bounds.
/// Controllers:   uses XRSimpleInteractable's selectEntered event (ray or direct select).
///
/// Attach to a GameObject with a BoxCollider. Wire up the inspector or let the
/// component add XRSimpleInteractable automatically at runtime.
///
/// Pattern for fingertip detection mirrors WhiteboardPen — no extra XRI setup needed
/// beyond what's already in the scene (XR Ray Interactors on the controllers).
/// </summary>
[RequireComponent(typeof(BoxCollider))]
[RequireComponent(typeof(XRSimpleInteractable))]
public class JournalStartButton : MonoBehaviour
{
    [Header("Hand Tracking — Fingertip Poke")]
    [Tooltip("Distance (metres) at which the hover glow activates when using hands.")]
    [Range(0.02f, 0.15f)]
    public float hoverDistance = 0.06f;

    [Header("Interaction Cooldown")]
    [Tooltip("Seconds before the button can be triggered again.")]
    [Range(0.5f, 5f)]
    public float cooldown = 2f;

    [Header("Visual Feedback")]
    [Tooltip("Renderer to tint on hover/press. Uses this GameObject's Renderer if null.")]
    public Renderer buttonRenderer;

    public Color idleColor    = new Color(0.85f, 0.80f, 0.65f, 1f); // warm parchment
    public Color hoverColor   = new Color(1.00f, 0.92f, 0.60f, 1f); // warm amber
    public Color pressedColor = new Color(0.70f, 0.95f, 0.70f, 1f); // sage green

    [Tooltip("How much the button sinks in (metres) when pressed.")]
    [Range(0f, 0.02f)]
    public float pressDepth = 0.005f;

    [Header("Tooltip")]
    [Tooltip("Text shown above the button when the controller ray hovers over it.")]
    public string tooltipMessage = "Start Journaling?";
    [Tooltip("World-space offset from the button's position where the tooltip floats.")]
    public Vector3 tooltipWorldOffset = new Vector3(0f, 0.12f, 0f);
    [Tooltip("Font size of the tooltip (world units).")]
    public float tooltipFontSize = 1f;

    // ── Events ──────────────────────────────────────────────────────────
    public event Action OnButtonPressed;

    // ── State ───────────────────────────────────────────────────────────
    private XRHandSubsystem handSubsystem;
    private XRSimpleInteractable xrInteractable;
    private BoxCollider triggerCollider;
    private Material buttonMaterial;
    private Vector3 idleLocalPos;
    private Vector3 pressLocalAxis;
    private float cooldownTimer;
    private bool isOnCooldown;
    private bool wasHovering;
    private TextMeshPro _tooltipTmp;
    private bool _xrHovering;

    private void Awake()
    {
        triggerCollider = GetComponent<BoxCollider>();
        // Non-trigger so the NearFarInteractor's physics raycast (QueryTriggerInteraction.Ignore)
        // can detect this collider. Hand poke detection uses bounds.Contains / ClosestPoint which
        // work identically on non-trigger colliders.
        triggerCollider.isTrigger = false;

        xrInteractable = GetComponent<XRSimpleInteractable>();

        if (buttonRenderer == null)
            buttonRenderer = GetComponent<Renderer>();

        if (buttonRenderer != null)
        {
            buttonMaterial = buttonRenderer.material;
            buttonMaterial.color = idleColor;
        }

        idleLocalPos = transform.localPosition;

        // Stable local-axis press direction (avoids world/local mixing artefacts
        // when parent objects are rotated or scaled).
        pressLocalAxis = transform.InverseTransformDirection(transform.forward);
        if (pressLocalAxis.sqrMagnitude < 0.0001f)
            pressLocalAxis = Vector3.forward;
        else
            pressLocalAxis.Normalize();

        CreateTooltip();
    }

    private void OnEnable()
    {
        // Controller path: XRI select (ray interactor click or direct select)
        if (xrInteractable != null)
        {
            xrInteractable.selectEntered.AddListener(OnControllerSelect);
            xrInteractable.hoverEntered.AddListener(OnControllerHoverEnter);
            xrInteractable.hoverExited.AddListener(OnControllerHoverExit);
        }

        // Always restore to idle appearance when the button is shown.
        // This prevents the pressed/green tint persisting after a session ends.
        isOnCooldown = false;
        cooldownTimer = 0f;
        wasHovering = false;
        _xrHovering = false;
        SetColor(idleColor);
        if (idleLocalPos != Vector3.zero)   // Awake sets this; guard against OnEnable before Awake
            transform.localPosition = idleLocalPos;
        SetTooltipVisible(false);
    }

    private void OnDisable()
    {
        if (xrInteractable != null)
        {
            xrInteractable.selectEntered.RemoveListener(OnControllerSelect);
            xrInteractable.hoverEntered.RemoveListener(OnControllerHoverEnter);
            xrInteractable.hoverExited.RemoveListener(OnControllerHoverExit);
        }

        // Reset the whiteboard material so it does not stay green when this GO is
        // deactivated at the start of a journaling session (cooldown timer stops here).
        SetColor(idleColor);
        _xrHovering = false;
        SetTooltipVisible(false);
    }

    // ================================================================
    // CONTROLLER INPUT (XRI)
    // ================================================================

    private void OnControllerSelect(SelectEnterEventArgs args)
    {
        TriggerPress();
    }

    private void OnControllerHoverEnter(HoverEnterEventArgs args)
    {
        if (!isOnCooldown)
        {
            SetColor(hoverColor);
            _xrHovering = true;
            SetTooltipVisible(true);
        }
    }

    private void OnControllerHoverExit(HoverExitEventArgs args)
    {
        if (!isOnCooldown)
        {
            SetColor(idleColor);
            transform.localPosition = idleLocalPos;
        }
        _xrHovering = false;
        SetTooltipVisible(false);
    }

    // ================================================================
    // HAND TRACKING — FINGERTIP POKE
    // ================================================================

    private void Update()
    {
        // Cooldown timer (shared between hand and controller paths)
        if (isOnCooldown)
        {
            cooldownTimer += Time.deltaTime;
            if (cooldownTimer >= cooldown)
            {
                isOnCooldown = false;
                cooldownTimer = 0f;
                SetColor(idleColor);
                transform.localPosition = idleLocalPos;
            }
            return;
        }

        // Billboard tooltip toward the camera while it is visible
        if (_tooltipTmp != null && _tooltipTmp.gameObject.activeSelf && Camera.main != null)
        {
            _tooltipTmp.transform.position = transform.position + tooltipWorldOffset;
            Vector3 lookDir = _tooltipTmp.transform.position - Camera.main.transform.position;
            if (lookDir.sqrMagnitude > 0.0001f)
                _tooltipTmp.transform.rotation = Quaternion.LookRotation(lookDir);
        }

        // Only run fingertip detection when hands are active
        if (handSubsystem == null || !handSubsystem.running)
        {
            handSubsystem = WhiteboardPen.GetHandSubsystem();
            if (handSubsystem == null) return;
        }

        // Check both hands for fingertip poke
        bool poked = TryPokeWithHand(handSubsystem.rightHand)
                  || TryPokeWithHand(handSubsystem.leftHand);

        // Handle hover state from either hand
        bool isHovering = IsHandHovering(handSubsystem.rightHand)
                       || IsHandHovering(handSubsystem.leftHand);

        if (!poked)
        {
            if (isHovering && !wasHovering)
            {
                SetColor(hoverColor);
            }
            else if (!isHovering && wasHovering)
            {
                SetColor(idleColor);
                transform.localPosition = idleLocalPos;
            }
        }

        wasHovering = isHovering;
    }

    private bool TryPokeWithHand(XRHand hand)
    {
        if (!hand.isTracked) return false;

        XRHandJoint indexTip = hand.GetJoint(XRHandJointID.IndexTip);
        if (!indexTip.TryGetPose(out Pose tipPose)) return false;

        if (triggerCollider.bounds.Contains(tipPose.position))
        {
            TriggerPress();
            return true;
        }
        return false;
    }

    private bool IsHandHovering(XRHand hand)
    {
        if (!hand.isTracked) return false;

        XRHandJoint indexTip = hand.GetJoint(XRHandJointID.IndexTip);
        if (!indexTip.TryGetPose(out Pose tipPose)) return false;

        float dist = Vector3.Distance(tipPose.position, triggerCollider.ClosestPoint(tipPose.position));
        return dist <= hoverDistance;
    }

    // ================================================================
    // SHARED PRESS LOGIC
    // ================================================================

    private void TriggerPress()
    {
        if (isOnCooldown) return;

        // Block journal start while the chat bridge is awaiting an AI response or
        // the chat mic is actively recording. This prevents overlapping states.
        var chatBridge = VRChatBridge.Instance;
        if (chatBridge != null && !chatBridge.IsChatInputAllowed)
        {
            Debug.Log("[JournalStartButton] Press ignored — chat bridge is busy or mic is recording.");
            return;
        }

        Debug.Log("[JournalStartButton] Button triggered.");

        SetColor(pressedColor);
        transform.localPosition = idleLocalPos - pressLocalAxis * pressDepth;

        isOnCooldown = true;
        cooldownTimer = 0f;

        _xrHovering = false;
        SetTooltipVisible(false);

        OnButtonPressed?.Invoke();
    }

    private void SetColor(Color color)
    {
        if (buttonMaterial != null)
            buttonMaterial.color = color;
    }

    // ================================================================
    // TOOLTIP
    // ================================================================

    private void CreateTooltip()
    {
        // Root-level GO avoids inheriting JournalTable's non-uniform scale (160 × 16.65 × 280).
        var go = new GameObject("JournalStartButton_Tooltip");
        go.transform.SetParent(null);

        _tooltipTmp = go.AddComponent<TextMeshPro>();
        _tooltipTmp.text         = tooltipMessage;
        _tooltipTmp.fontSize     = tooltipFontSize;
        _tooltipTmp.alignment    = TextAlignmentOptions.Center;
        _tooltipTmp.color        = new Color(1f, 0.96f, 0.84f, 1f);        // warm cream
        _tooltipTmp.fontStyle    = FontStyles.Bold;
        _tooltipTmp.outlineWidth = 0.2f;
        _tooltipTmp.outlineColor = new Color32(51, 31, 13, 255);           // dark brown

        go.SetActive(false);
    }

    private void SetTooltipVisible(bool visible)
    {
        if (_tooltipTmp != null)
            _tooltipTmp.gameObject.SetActive(visible);
    }

    private void OnDestroy()
    {
        if (buttonMaterial != null)
            Destroy(buttonMaterial);
        if (_tooltipTmp != null)
            Destroy(_tooltipTmp.gameObject);
    }
}
