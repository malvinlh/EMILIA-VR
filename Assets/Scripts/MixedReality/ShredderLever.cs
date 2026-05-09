using System.Collections;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>
/// Button-style pull-lever for the paper shredder. Attach to the handle mesh GameObject.
///
/// Lever stays uninteractable until <see cref="SetInteractable"/>(true) is called by
/// PaperShredder once the paper has snapped to the placeholder. A single select/poke
/// runs a canned animation: pull the handle from <see cref="restEulerX"/> to
/// <see cref="maxPullEulerX"/>, fire <see cref="OnPulled"/> at the bottom, then
/// spring back. The lever blocks itself from re-presses during the animation and
/// stays uninteractable afterward until re-enabled by the next session's snap.
///
/// LateUpdate locks the handle's localPosition, localScale, and Y/Z rotation so
/// nothing else can drift the pose; only the X rotation varies along the animation.
/// </summary>
[DefaultExecutionOrder(20000)]
[RequireComponent(typeof(XRSimpleInteractable))]
public class ShredderLever : MonoBehaviour
{
    [Header("References")]
    public XRSimpleInteractable simpleInteractable;

    [Header("Rotation animation")]
    [Tooltip("Euler X of the handle at rest (up) position.")]
    public float restEulerX = 55f;
    [Tooltip("Euler X of the handle at maximum pulled-down position.")]
    public float maxPullEulerX = -55f;
    [Tooltip("Duration of the pull-down lerp in seconds.")]
    [Range(0.1f, 1f)] public float pullDuration = 0.4f;
    [Tooltip("Duration of the spring-back lerp in seconds.")]
    [Range(0.1f, 1f)] public float returnDuration = 0.4f;

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
    private Coroutine _animCoroutine;

    public void SetInteractable(bool value)
    {
        if (simpleInteractable != null) simpleInteractable.enabled = value;
    }

    public void ResetToRest()
    {
        if (_animCoroutine != null) { StopCoroutine(_animCoroutine); _animCoroutine = null; }
        _currentX = restEulerX;
    }

    private void Awake()
    {
        _restLocalPosition    = transform.localPosition;
        _restLocalScale       = transform.localScale;
        _restLocalEulerAngles = transform.localEulerAngles;
        _restLocalEulerAngles.x = restEulerX;
        _currentX = restEulerX;
        if (simpleInteractable == null) simpleInteractable = GetComponent<XRSimpleInteractable>();
        SetInteractable(false);
        transform.localRotation = Quaternion.Euler(_restLocalEulerAngles);
    }

    private void OnEnable()
    {
        if (simpleInteractable != null)
            simpleInteractable.selectEntered.AddListener(HandleSelectEntered);
    }

    private void OnDisable()
    {
        if (simpleInteractable != null)
            simpleInteractable.selectEntered.RemoveListener(HandleSelectEntered);
    }

    private void HandleSelectEntered(SelectEnterEventArgs _)
    {
        if (_animCoroutine != null) return;
        SetInteractable(false);
        _animCoroutine = StartCoroutine(PullAndReturn());
    }

    private IEnumerator PullAndReturn()
    {
        yield return AnimateX(restEulerX, maxPullEulerX, pullDuration);

        if (audioSource != null && clickClip != null)
            audioSource.PlayOneShot(clickClip, clickVolume);

        OnPulled?.Invoke();

        yield return AnimateX(maxPullEulerX, restEulerX, returnDuration);

        _currentX = restEulerX;
        _animCoroutine = null;
    }

    private IEnumerator AnimateX(float fromX, float toX, float duration)
    {
        float elapsed = 0f;
        float dur = Mathf.Max(0.01f, duration);
        while (elapsed < dur)
        {
            elapsed += Time.deltaTime;
            float t     = Mathf.Clamp01(elapsed / dur);
            float eased = t * t * (3f - 2f * t);
            _currentX   = Mathf.Lerp(fromX, toX, eased);
            yield return null;
        }
        _currentX = toX;
    }

    private void LateUpdate()
    {
        transform.localPosition = _restLocalPosition;
        transform.localScale    = _restLocalScale;
        transform.localRotation = Quaternion.Euler(_currentX, _restLocalEulerAngles.y, _restLocalEulerAngles.z);
    }
}
