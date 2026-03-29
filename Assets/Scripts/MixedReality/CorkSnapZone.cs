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

    [Header("Optional Seat Override")]
    [Tooltip("If assigned, the cork is sealed to this transform (e.g., the shadow placeholder) instead of using socket attach math.")]
    [SerializeField] private Transform seatOverrideTarget;

    private XRSocketInteractor _socket;
    private bool _sealed;

    private void Awake()
    {
        _socket = GetComponent<XRSocketInteractor>();
        if (_socket == null)
            Debug.LogWarning("[CorkSnapZone] No XRSocketInteractor found on this GameObject.");
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
