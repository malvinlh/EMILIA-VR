using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>
/// Blinks an item back to its authored scene position when it has drifted too far away
/// and is no longer being held by the player.
///
/// Attach to both CorkPost and BottlePost in the scene.
///
/// Cork-specific: CorkSnapZone disables the XRGrabInteractable after sealing.
/// When the interactable is disabled we treat the item as "locked" and skip the reset
/// so the sealed cork follows the bottle without interference.
/// </summary>
public class ItemAutoReset : MonoBehaviour
{
    [Tooltip("Distance from the item's origin (metres) that triggers the blink-back timer.")]
    public float resetRadius = 3f;

    [Tooltip("Seconds the item must remain outside the reset radius (and un-grabbed) " +
             "before it blinks back.")]
    public float outsideDelay = 1.5f;

    // ── Cached state ───────────────────────────────────────────────────
    private Transform  _originParent;
    private Vector3    _originLocalPos;
    private Quaternion _originLocalRot;
    private XRBaseInteractable _interactable;
    private float _outsideTimer;
    private bool  _blinking;

    // ================================================================
    // UNITY
    // ================================================================

    private void Awake()
    {
        _originParent   = transform.parent;
        _originLocalPos = transform.localPosition;
        _originLocalRot = transform.localRotation;

        _interactable = GetComponent<XRBaseInteractable>()
                     ?? GetComponentInChildren<XRBaseInteractable>(includeInactive: true);
    }

    private void Update()
    {
        // If the interactable is disabled the item is locked (e.g. cork sealed to bottle).
        if (_interactable != null && !_interactable.enabled)
        {
            _outsideTimer = 0f;
            return;
        }

        // Don't reset while the player is holding the item.
        if (_interactable != null && _interactable.isSelected)
        {
            _outsideTimer = 0f;
            return;
        }

        Vector3 worldOrigin = _originParent != null
            ? _originParent.TransformPoint(_originLocalPos)
            : _originLocalPos;

        if (Vector3.Distance(transform.position, worldOrigin) > resetRadius)
        {
            _outsideTimer += Time.deltaTime;
            if (_outsideTimer >= outsideDelay && !_blinking)
                StartCoroutine(BlinkBack());
        }
        else
        {
            _outsideTimer = 0f;
        }
    }

    // ================================================================
    // BLINK BACK
    // ================================================================

    private IEnumerator BlinkBack()
    {
        _blinking     = true;
        _outsideTimer = 0f;

        // Snapshot ONLY the renderers that are currently visible.
        // Using includeInactive:true + filtering to enabled means we don't accidentally
        // re-enable hidden children (e.g. a cork placeholder mesh inside the bottle socket
        // that should stay invisible until the cork is physically sealed by the player).
        var allRenderers = GetComponentsInChildren<Renderer>(includeInactive: true);
        var visibleRenderers = new List<Renderer>(allRenderers.Length);
        foreach (var r in allRenderers)
            if (r != null && r.enabled) visibleRenderers.Add(r);

        // Hide via renderers — NOT SetActive(false). Calling SetActive(false) kills this
        // coroutine immediately, so the restore code below it would never run.
        foreach (var r in visibleRenderers) r.enabled = false;

        // Freeze physics for the restore so the engine picks up the new position cleanly.
        var rb = GetComponent<Rigidbody>();
        bool wasKinematic = rb != null && rb.isKinematic;
        if (rb != null)
        {
            rb.isKinematic    = true;
            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        // Restore parent, position, and rotation.
        transform.SetParent(_originParent, worldPositionStays: false);
        transform.localPosition = _originLocalPos;
        transform.localRotation = _originLocalRot;

        yield return new WaitForSeconds(0.15f);

        // Restore physics and re-enable only the renderers that were visible before.
        if (rb != null) rb.isKinematic = wasKinematic;
        foreach (var r in visibleRenderers) if (r != null) r.enabled = true;
        _blinking = false;
    }

    // ================================================================
    // PUBLIC — refresh origin after programmatic repositioning
    // ================================================================

    /// <summary>
    /// Re-captures the current local transform as the new origin.
    /// Call after programmatically repositioning the item (e.g. from BeginCorkPhase).
    /// </summary>
    public void RefreshOrigin()
    {
        _originParent   = transform.parent;
        _originLocalPos = transform.localPosition;
        _originLocalRot = transform.localRotation;
        _outsideTimer   = 0f;
    }
}
