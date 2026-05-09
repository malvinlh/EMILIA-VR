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

    private Vector3   _restLocalPosition;
    private Vector3   _restLocalScale;
    private Vector3   _restLocalEulerAngles;
    private float     _currentX;
    private bool      _isHeld;
    private bool      _pulledThisHold;
    private Coroutine _returnCoroutine;
    private Coroutine _autoPullCoroutine;
    private Transform _interactorTransform;
    private float     _grabStartControllerY;

    private void Awake()
    {
        _restLocalPosition    = transform.localPosition;
        _restLocalScale       = transform.localScale;
        _restLocalEulerAngles = transform.localEulerAngles;
        _restLocalEulerAngles.x = restEulerX;
        _currentX = restEulerX;
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

    // LateUpdate is the single writer of the handle's transform. It runs every
    // frame (held or not) so XRI cannot drift the position, scale, or Y/Z rotation.
    private void LateUpdate()
    {
        transform.localPosition = _restLocalPosition;
        transform.localScale    = _restLocalScale;
        transform.localRotation = Quaternion.Euler(_currentX, _restLocalEulerAngles.y, _restLocalEulerAngles.z);

        if (!_isHeld) return;

        // Wait for any slight downward movement to trigger the auto-pull animation.
        if (!_pulledThisHold && _autoPullCoroutine == null && _interactorTransform != null)
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
        float startX = _restLocalEulerAngles.x;

        float elapsed = 0f;
        while (elapsed < pullDuration)
        {
            elapsed += Time.deltaTime;
            float t     = Mathf.Clamp01(elapsed / pullDuration);
            float eased = t * t * (3f - 2f * t); // smoothstep
            _currentX   = Mathf.Lerp(startX, maxPullEulerX, eased);
            yield return null;
        }
        _currentX = maxPullEulerX;

        if (audioSource != null && clickClip != null)
            audioSource.PlayOneShot(clickClip, clickVolume);

        _autoPullCoroutine = null;
        OnPulled?.Invoke();
    }

    private IEnumerator ReturnToRest()
    {
        float startX = _currentX;
        float elapsed = 0f;
        float duration = Mathf.Max(0.01f, returnDuration);

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t     = Mathf.Clamp01(elapsed / duration);
            float eased = t * t * (3f - 2f * t);
            _currentX   = Mathf.Lerp(startX, _restLocalEulerAngles.x, eased);
            yield return null;
        }
        _currentX = _restLocalEulerAngles.x;
        _returnCoroutine = null;
    }
}
