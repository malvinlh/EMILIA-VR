using System.Collections;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>
/// Pull-lever for the paper shredder. Attach to the handle mesh GameObject.
///
/// When the player grabs the handle and moves the controller down by at least
/// <see cref="triggerMovement"/> metres, a canned lerp animation automatically drives
/// the handle from <see cref="restEulerX"/> to <see cref="maxPullEulerX"/> over
/// <see cref="pullDuration"/> seconds, then fires <see cref="OnPulled"/>.
/// Releasing the lever at any point springs it back to rest.
/// </summary>
[RequireComponent(typeof(XRGrabInteractable))]
public class ShredderLever : MonoBehaviour
{
    [Header("References")]
    public XRGrabInteractable grabInteractable;

    [Header("Rotation")]
    [Tooltip("Euler X of the handle at rest (up) position.")]
    public float restEulerX = 55f;
    [Tooltip("Euler X of the handle at maximum pulled-down position.")]
    public float maxPullEulerX = -55f;
    [Tooltip("Minimum downward controller travel (metres) that triggers the auto-pull animation.")]
    [Range(0.005f, 0.1f)] public float triggerMovement = 0.02f;
    [Tooltip("Duration of the pull lerp animation in seconds.")]
    [Range(0.1f, 1f)] public float pullDuration = 0.4f;

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
    private bool      _isHeld;
    private bool      _pulledThisHold;
    private Coroutine _returnCoroutine;
    private Coroutine _autoPullCoroutine;
    private Transform _interactorTransform;
    private float     _grabStartControllerY;

    private void Awake()
    {
        _restLocalEulerAngles = transform.localEulerAngles;
        _restLocalEulerAngles.x = restEulerX;
        if (grabInteractable == null) grabInteractable = GetComponent<XRGrabInteractable>();
        // Prevent XRI from moving the handle — animation is driven exclusively by coroutines.
        grabInteractable.trackPosition = false;
        grabInteractable.trackRotation = false;
        grabInteractable.addDefaultGrabTransformers = false;
        transform.localRotation = Quaternion.Euler(_restLocalEulerAngles);
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
        if (_returnCoroutine != null) { StopCoroutine(_returnCoroutine); _returnCoroutine = null; }
    }

    private void HandleSelectExited(SelectExitEventArgs _)
    {
        _isHeld = false;
        _pulledThisHold = false;
        _interactorTransform = null;
        if (_autoPullCoroutine != null) { StopCoroutine(_autoPullCoroutine); _autoPullCoroutine = null; }
        if (_returnCoroutine != null) StopCoroutine(_returnCoroutine);
        _returnCoroutine = StartCoroutine(ReturnToRest());
    }

    private void LateUpdate()
    {
        if (!_isHeld) return;

        // Keep the handle constrained to the authored rest pose until the pull begins.
        if (!_pulledThisHold && _autoPullCoroutine == null)
            transform.localRotation = Quaternion.Euler(_restLocalEulerAngles);

        // Wait for any slight downward movement to trigger the auto-pull animation.
        if (!_pulledThisHold && _interactorTransform != null)
        {
            float yDelta = _grabStartControllerY - _interactorTransform.position.y;
            if (yDelta >= triggerMovement)
            {
                _pulledThisHold = true;
                if (_returnCoroutine != null) { StopCoroutine(_returnCoroutine); _returnCoroutine = null; }
                _autoPullCoroutine = StartCoroutine(AutoPull());
            }
        }
    }

    private IEnumerator AutoPull()
    {
        // Always start from the declared rest angle — never read the live transform,
        // which may have been corrupted by XRI grab transformers.
        float startX = _restLocalEulerAngles.x;

        float elapsed = 0f;
        while (elapsed < pullDuration)
        {
            elapsed += Time.deltaTime;
            float t     = Mathf.Clamp01(elapsed / pullDuration);
            float eased = t * t * (3f - 2f * t); // smoothstep
            float eulerX = Mathf.Lerp(startX, maxPullEulerX, eased);
            transform.localRotation = Quaternion.Euler(eulerX, _restLocalEulerAngles.y, _restLocalEulerAngles.z);
            yield return null;
        }

        transform.localRotation = Quaternion.Euler(maxPullEulerX, _restLocalEulerAngles.y, _restLocalEulerAngles.z);

        if (audioSource != null && clickClip != null)
            audioSource.PlayOneShot(clickClip, clickVolume);

        _autoPullCoroutine = null;
        // Fires PaperShredder.Pull() → disables grabInteractable → HandleSelectExited → ReturnToRest.
        OnPulled?.Invoke();
    }

    private IEnumerator ReturnToRest()
    {
        Quaternion fromRot = transform.localRotation;
        Quaternion restRot = Quaternion.Euler(_restLocalEulerAngles);
        float elapsed = 0f;
        float duration = Mathf.Max(0.01f, returnDuration);

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t     = Mathf.Clamp01(elapsed / duration);
            float eased = t * t * (3f - 2f * t);
            transform.localRotation = Quaternion.Slerp(fromRot, restRot, eased);
            yield return null;
        }

        transform.localRotation = restRot;
        _returnCoroutine = null;
    }
}
