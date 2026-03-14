using System;
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

    // ── Events ──────────────────────────────────────────────────────────
    public event Action OnButtonPressed;

    // ── State ───────────────────────────────────────────────────────────
    private XRHandSubsystem handSubsystem;
    private XRSimpleInteractable xrInteractable;
    private BoxCollider triggerCollider;
    private Material buttonMaterial;
    private Vector3 idleLocalPos;
    private float cooldownTimer;
    private bool isOnCooldown;
    private bool wasHovering;

    private void Awake()
    {
        triggerCollider = GetComponent<BoxCollider>();
        triggerCollider.isTrigger = true;

        xrInteractable = GetComponent<XRSimpleInteractable>();

        if (buttonRenderer == null)
            buttonRenderer = GetComponent<Renderer>();

        if (buttonRenderer != null)
        {
            buttonMaterial = buttonRenderer.material;
            buttonMaterial.color = idleColor;
        }

        idleLocalPos = transform.localPosition;
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
    }

    private void OnDisable()
    {
        if (xrInteractable != null)
        {
            xrInteractable.selectEntered.RemoveListener(OnControllerSelect);
            xrInteractable.hoverEntered.RemoveListener(OnControllerHoverEnter);
            xrInteractable.hoverExited.RemoveListener(OnControllerHoverExit);
        }
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
            SetColor(hoverColor);
    }

    private void OnControllerHoverExit(HoverExitEventArgs args)
    {
        if (!isOnCooldown)
        {
            SetColor(idleColor);
            transform.localPosition = idleLocalPos;
        }
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

        // Only run fingertip detection when hands are active
        if (handSubsystem == null || !handSubsystem.running)
        {
            handSubsystem = WhiteboardPen.GetHandSubsystem();
            if (handSubsystem == null) return;
        }

        XRHand rightHand = handSubsystem.rightHand;
        if (!rightHand.isTracked) return;

        XRHandJoint indexTip = rightHand.GetJoint(XRHandJointID.IndexTip);
        if (!indexTip.TryGetPose(out Pose tipPose)) return;

        Vector3 tipPos = tipPose.position;

        bool isInside   = triggerCollider.bounds.Contains(tipPos);
        float dist      = Vector3.Distance(tipPos, triggerCollider.ClosestPoint(tipPos));
        bool isHovering = dist <= hoverDistance;

        if (isInside)
        {
            TriggerPress();
        }
        else if (isHovering && !wasHovering)
        {
            SetColor(hoverColor);
        }
        else if (!isHovering && wasHovering)
        {
            SetColor(idleColor);
            transform.localPosition = idleLocalPos;
        }

        wasHovering = isHovering;
    }

    // ================================================================
    // SHARED PRESS LOGIC
    // ================================================================

    private void TriggerPress()
    {
        if (isOnCooldown) return;

        SetColor(pressedColor);
        transform.localPosition = idleLocalPos - transform.forward * pressDepth;

        isOnCooldown = true;
        cooldownTimer = 0f;

        OnButtonPressed?.Invoke();
    }

    private void SetColor(Color color)
    {
        if (buttonMaterial != null)
            buttonMaterial.color = color;
    }

    private void OnDestroy()
    {
        if (buttonMaterial != null)
            Destroy(buttonMaterial);
    }
}
