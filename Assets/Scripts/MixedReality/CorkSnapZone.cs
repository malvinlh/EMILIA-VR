using System;
using System.Collections;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;

/// <summary>
/// Attach to the PostJournal Bottle root — the SAME GameObject that has the XRSocketInteractor.
///
/// How it works:
///   The Bottle's XRSocketInteractor already handles attraction and snapping the Cork
///   into position. This script listens to that socket's selectEntered event and then
///   performs the stabilisation step: releases the Cork from XRI's physics control,
///   leaves its Rigidbody in a fully inert state (for XRGrabInteractable compatibility),
///   and parents it as a static child of the socket's attach point so it moves exactly
///   with the Bottle.
///
/// Inspector wiring:
///   None — auto-discovers XRSocketInteractor via GetComponent on Awake.
///   Wire the Bottle's CorkSnapZone to JournalReviewController.bottleNeckZone.
/// </summary>
public class CorkSnapZone : MonoBehaviour
{
    /// <summary>Fired once after the Cork is permanently fixed to the Bottle.</summary>
    public event Action OnCorkSealed;

    /// <summary>Debug-only access for state snapshots in JournalReviewController.</summary>
    public Transform DebugCorkTransform => _cork;

    [Header("Optional Seat Override")]
    [Tooltip("If assigned, the cork is sealed to this transform (e.g., the shadow placeholder) instead of using socket attach math.")]
    [SerializeField] private Transform seatOverrideTarget;

    [Header("Session Reset")]
    [Tooltip("The Cork GameObject. Assign in Inspector so the original pose can be stored " +
             "at scene start and restored when a new journaling session begins.")]
    [SerializeField] private Transform _cork;

    private XRSocketInteractor _socket;
    private bool _sealed;

    // Original cork state captured once at scene start (before any session runs).
    private Transform  _corkOriginalParent;
    private Vector3    _corkOriginalLocalPos;
    private Quaternion _corkOriginalLocalRot;
    private Vector3    _corkOriginalLocalScale;

    private void Awake()
    {
        _socket = GetComponent<XRSocketInteractor>();
        if (_socket == null)
            Debug.LogWarning("[CorkSnapZone] No XRSocketInteractor found on this GameObject.");

        if (_cork != null)
        {
            _corkOriginalParent   = _cork.parent;
            _corkOriginalLocalPos = _cork.localPosition;
            _corkOriginalLocalRot = _cork.localRotation;
            _corkOriginalLocalScale = _cork.localScale;
        }
        else
        {
            Debug.LogWarning("[CorkSnapZone] _cork not assigned — cork cannot be reset between sessions.");
        }
    }

    /// <summary>
    /// Restores the cork to its scene-start state so the next journaling session
    /// can seal it again. Call from JournalReviewController.BeginCorkPhase().
    /// </summary>
    public void ResetForNewSession()
    {
        if (_cork == null) return;

        // Restore Rigidbody to a grabbable, physics-active state.
        var rb = _cork.GetComponent<Rigidbody>();
        if (rb != null)
        {
            rb.isKinematic          = false;
            rb.useGravity           = true;
            rb.detectCollisions     = true;
            rb.interpolation        = RigidbodyInterpolation.None;
            rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
            rb.constraints          = RigidbodyConstraints.None;
            rb.linearVelocity       = Vector3.zero;
            rb.angularVelocity      = Vector3.zero;
        }

        // Re-enable grab so the player can pick the cork up again.
        var grab = _cork.GetComponent<XRGrabInteractable>();
        if (grab != null) grab.enabled = true;

        // Return the cork to its original parent and local pose.
        _cork.SetParent(_corkOriginalParent, worldPositionStays: false);
        _cork.localPosition = _corkOriginalLocalPos;
        _cork.localRotation = _corkOriginalLocalRot;
        _cork.localScale    = _corkOriginalLocalScale;

        // Defensive: if any script disabled the cork GameObject, bring it back.
        if (!_cork.gameObject.activeSelf)
            _cork.gameObject.SetActive(true);

        // Ensure the cork is visible and collidable again even if previous disposal
        // disabled child components while it was parented under the bottle.
        foreach (var r in _cork.GetComponentsInChildren<Renderer>(true))
            r.enabled = true;
        foreach (var c in _cork.GetComponentsInChildren<Collider>(true))
            c.enabled = true;

        _sealed = false;
        // OnEnable re-subscribes the socket listener automatically when the GO re-activates.
        // If the socket GO is already active, subscribe now.
        if (_socket != null && isActiveAndEnabled)
        {
            _socket.selectEntered.RemoveListener(OnSocketSelectEntered);
            _socket.selectEntered.AddListener(OnSocketSelectEntered);
        }

        Debug.Log("[CorkSnapZone] Reset for new session — cork restored, _sealed = false.");
    }

    private void OnEnable()
    {
        if (_socket != null)
            _socket.selectEntered.AddListener(OnSocketSelectEntered);
    }

    private void OnDisable()
    {
        if (_socket != null)
            _socket.selectEntered.RemoveListener(OnSocketSelectEntered);
    }

    private void OnSocketSelectEntered(SelectEnterEventArgs args)
    {
        if (_sealed) return;
        _sealed = true;

        // Stop listening — cork can only be sealed once.
        _socket.selectEntered.RemoveListener(OnSocketSelectEntered);

        StartCoroutine(StabiliseCork(args.interactableObject.transform));
    }

    private IEnumerator StabiliseCork(Transform corkTf)
    {
        // One render frame: let the socket move the Cork to its attach point.
        yield return null;

        var interactable = corkTf.GetComponent<XRGrabInteractable>();
        var rb           = corkTf.GetComponent<Rigidbody>();

        // Capture the snapped pose. If an override target is provided, seal exactly there.
        Vector3 sealedWorldPos = corkTf.position;
        Quaternion sealedWorldRot = corkTf.rotation;

        if (seatOverrideTarget != null)
        {
            sealedWorldPos = seatOverrideTarget.position;
            sealedWorldRot = seatOverrideTarget.rotation;
        }
        else if (interactable != null && _socket != null)
        {
            Transform socketAttachTf = _socket.GetAttachTransform(interactable);
            Transform interactableAttachTf = interactable.GetAttachTransform(_socket);

            if (socketAttachTf != null && interactableAttachTf != null)
            {
                sealedWorldRot = socketAttachTf.rotation * Quaternion.Inverse(interactableAttachTf.localRotation);
                sealedWorldPos = socketAttachTf.position - (sealedWorldRot * interactableAttachTf.localPosition);
            }
        }

        // Freeze physics immediately so the Cork doesn't fall when released from the socket.
        if (rb != null)
        {
            rb.isKinematic = true;
            rb.linearVelocity        = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        // Release from socket — stops the socket calling MovePosition on the Rigidbody
        // each FixedUpdate (which is the source of the jitter when the player moves).
        if (interactable != null && interactable.isSelected && _socket != null)
            _socket.interactionManager.SelectExit((IXRSelectInteractor)_socket, (IXRSelectInteractable)interactable);

        // Wait for XRI to process the exit event before final locking.
        yield return null;

        // Disable grab so the Cork can never be picked up again.
        if (interactable != null) interactable.enabled = false;

        // XRGrabInteractable expects a Rigidbody to exist, so keep it but make it inert.
        if (rb != null)
        {
            rb.isKinematic = true;
            rb.useGravity = false;
            rb.detectCollisions = false;
            rb.interpolation = RigidbodyInterpolation.None;
            rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
            rb.constraints = RigidbodyConstraints.FreezeAll;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
            rb.Sleep();
        }

        // Parent Cork to the socket's attach point so it moves exactly with the
        // Bottle with zero frame lag (no more physics-driven position updates).
        Transform anchor = (_socket != null && _socket.attachTransform != null)
            ? _socket.attachTransform
            : transform;
        corkTf.SetParent(anchor, worldPositionStays: true);
        corkTf.SetPositionAndRotation(sealedWorldPos, sealedWorldRot);

        OnCorkSealed?.Invoke();
    }
}
