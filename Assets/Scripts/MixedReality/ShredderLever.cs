using System.Collections;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>
/// Pull-lever for the paper shredder. Attach to the handle mesh GameObject.
///
/// Rotation is driven by mapping the controller's downward Y-displacement directly
/// onto a clamped Euler X range (restEulerX → maxPullEulerX). This avoids quaternion
/// direction ambiguity and keeps the handle visually between the two authored extremes.
///
/// Usage: player grabs the handle and pulls the controller downward.
/// At pullThresholdDeg of travel from rest, OnPulled fires (→ PaperShredder.Pull()).
/// Releasing the lever springs the handle back to its rest rotation.
/// </summary>
[RequireComponent(typeof(XRGrabInteractable))]
public class ShredderLever : MonoBehaviour
{
    [Header("References")]
    public XRGrabInteractable grabInteractable;

    [Header("Rotation")]
    [Tooltip("Euler X of the handle at the rest (up) position. Must match the scene-authored value.")]
    public float restEulerX = 55f;
    [Tooltip("Euler X of the handle at the maximum pulled-down position.")]
    public float maxPullEulerX = -55f;
    [Tooltip("Controller must travel this many metres downward to reach maxPullEulerX.")]
    [Range(0.05f, 0.5f)] public float pullDistance = 0.25f;
    [Tooltip("Degrees of Euler X travel from rest at which the pull is committed.")]
    [Range(5f, 120f)] public float pullThresholdDeg = 35f;

    [Header("Spring-back")]
    [Tooltip("Seconds to return to rest pose after release.")]
    [Range(0.05f, 2f)] public float returnDuration = 0.4f;

    [Header("Audio")]
    public AudioSource audioSource;
    public AudioClip clickClip;
    [Range(0f, 1f)] public float clickVolume = 0.8f;

    [Header("Events")]
    public UnityEvent OnPulled;

    private Vector3   _restLocalEulerAngles;
    private Vector3   _restLocalPosition;
    private bool      _isHeld;
    private bool      _pulledThisHold;
    private Coroutine _returnCoroutine;
    private Transform _interactorTransform;
    private float     _grabStartControllerY;

    private void Awake()
    {
        _restLocalEulerAngles = transform.localEulerAngles;
        _restLocalPosition    = transform.localPosition;
        if (grabInteractable == null) grabInteractable = GetComponent<XRGrabInteractable>();
    }

    private void OnEnable()
    {
        if (grabInteractable == null) return;
        grabInteractable.selectEntered.AddListener(HandleSelectEntered);
        grabInteractable.selectExited.AddListener(HandleSelectExited);
    }

    private void OnDisable()
    {
        if (grabInteractable == null) return;
        grabInteractable.selectEntered.RemoveListener(HandleSelectEntered);
        grabInteractable.selectExited.RemoveListener(HandleSelectExited);
    }

    private void HandleSelectEntered(SelectEnterEventArgs args)
    {
        _isHeld = true;
        _pulledThisHold = false;
        _interactorTransform = (args.interactorObject as MonoBehaviour)?.transform;
        _grabStartControllerY = _interactorTransform != null ? _interactorTransform.position.y : 0f;
        // Prevent XRI from overriding the handle transform — our LateUpdate drives it exclusively.
        grabInteractable.trackPosition = false;
        grabInteractable.trackRotation = false;
        if (_returnCoroutine != null) { StopCoroutine(_returnCoroutine); _returnCoroutine = null; }
    }

    private void HandleSelectExited(SelectExitEventArgs _)
    {
        _isHeld = false;
        _interactorTransform = null;
        grabInteractable.trackPosition = true;
        grabInteractable.trackRotation = true;
        if (_returnCoroutine != null) StopCoroutine(_returnCoroutine);
        _returnCoroutine = StartCoroutine(ReturnToRest());
    }

    private void LateUpdate()
    {
        if (!_isHeld || _interactorTransform == null) return;

        // Map downward controller travel [0, pullDistance] → Euler X [restEulerX, maxPullEulerX].
        float t = Mathf.Clamp01((_grabStartControllerY - _interactorTransform.position.y) / pullDistance);
        float eulerX = Mathf.Lerp(restEulerX, maxPullEulerX, t);

        // Keep the handle fixed in space; only change its Euler X.
        transform.localPosition = _restLocalPosition;
        transform.localRotation = Quaternion.Euler(eulerX, _restLocalEulerAngles.y, _restLocalEulerAngles.z);

        float travelDeg = Mathf.Abs(eulerX - restEulerX);
        if (!_pulledThisHold && travelDeg >= pullThresholdDeg)
        {
            _pulledThisHold = true;
            if (audioSource != null && clickClip != null)
                audioSource.PlayOneShot(clickClip, clickVolume);
            OnPulled?.Invoke();
        }
    }

    private IEnumerator ReturnToRest()
    {
        Quaternion fromRot  = transform.localRotation;
        Quaternion restRot  = Quaternion.Euler(_restLocalEulerAngles);
        float elapsed = 0f;
        float duration = Mathf.Max(0.01f, returnDuration);

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            float eased = t * t * (3f - 2f * t);
            transform.localRotation = Quaternion.Slerp(fromRot, restRot, eased);
            yield return null;
        }

        transform.localRotation = restRot;
        _returnCoroutine = null;
    }
}
