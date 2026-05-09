using System.Collections;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>
/// Pull-lever for the paper shredder. Attach to the handle mesh GameObject.
/// Rotation is driven entirely from the controller's downward Y-displacement —
/// no RotationAxisLockGrabTransformer required.
///
/// Usage: player grabs the handle and pulls the controller downward.
/// At pullThresholdDeg from rest, OnPulled fires (→ PaperShredder.Pull()).
/// Releasing springs the handle back to its rest rotation.
/// </summary>
[RequireComponent(typeof(XRGrabInteractable))]
public class ShredderLever : MonoBehaviour
{
    [Header("References")]
    public XRGrabInteractable grabInteractable;

    [Header("Rotation")]
    [Tooltip("Local axis the lever pivots around.")]
    public Vector3 rotationAxis = Vector3.right;
    [Tooltip("Degrees of rotation per metre of downward hand travel. 400 = ~9 cm needed for a 35° pull.")]
    [Range(50f, 800f)] public float rotationSensitivity = 400f;
    [Tooltip("Check if pulling downward rotates the lever the wrong way.")]
    public bool invertPullDirection = false;
    [Tooltip("Absolute angle (degrees) from rest at which the pull is committed.")]
    [Range(5f, 120f)] public float pullThresholdDeg = 35f;
    [Tooltip("Hard clamp on how far the lever can rotate from rest.")]
    [Range(10f, 180f)] public float maxPullDeg = 110f;

    [Header("Spring-back")]
    [Tooltip("Seconds to return to rest pose after release.")]
    [Range(0.05f, 2f)] public float returnDuration = 0.4f;

    [Header("Audio")]
    public AudioSource audioSource;
    public AudioClip clickClip;
    [Range(0f, 1f)] public float clickVolume = 0.8f;

    [Header("Events")]
    public UnityEvent OnPulled;

    private Quaternion _restLocalRotation;
    private Vector3    _restLocalPosition;
    private bool       _isHeld;
    private bool       _pulledThisHold;
    private Coroutine  _returnCoroutine;
    private Transform  _interactorTransform;
    private float      _grabStartControllerY;

    private void Awake()
    {
        _restLocalRotation = transform.localRotation;
        _restLocalPosition = transform.localPosition;
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
        if (_returnCoroutine != null) { StopCoroutine(_returnCoroutine); _returnCoroutine = null; }
    }

    private void HandleSelectExited(SelectExitEventArgs _)
    {
        _isHeld = false;
        _interactorTransform = null;
        if (_returnCoroutine != null) StopCoroutine(_returnCoroutine);
        _returnCoroutine = StartCoroutine(ReturnToRest());
    }

    // Runs after XRI has applied its own transform changes for the frame.
    private void LateUpdate()
    {
        if (!_isHeld || _interactorTransform == null) return;

        // Positive yDelta = controller moved downward.
        float yDelta = _grabStartControllerY - _interactorTransform.position.y;
        float sign   = invertPullDirection ? 1f : -1f;

        // Map to a rotation angle clamped to [−maxPullDeg, 0].
        // Negative angle rotates the handle from rest (+55°) toward the pulled position (−55°).
        float angle = Mathf.Clamp(yDelta * rotationSensitivity * sign, -maxPullDeg, 0f);

        // Override whatever XRI set this frame — keeps the handle fixed in space while rotating.
        transform.localPosition = _restLocalPosition;
        ApplyAngleFromRest(angle);

        if (!_pulledThisHold && Mathf.Abs(angle) >= pullThresholdDeg)
        {
            _pulledThisHold = true;
            if (audioSource != null && clickClip != null)
                audioSource.PlayOneShot(clickClip, clickVolume);
            OnPulled?.Invoke();
        }
    }

    private void ApplyAngleFromRest(float signedAngle)
    {
        Vector3 axisN = rotationAxis.sqrMagnitude < 1e-6f ? Vector3.right : rotationAxis.normalized;
        transform.localRotation = _restLocalRotation * Quaternion.AngleAxis(signedAngle, axisN);
    }

    private IEnumerator ReturnToRest()
    {
        Quaternion fromRot = transform.localRotation;
        float elapsed = 0f;
        float duration = Mathf.Max(0.01f, returnDuration);

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            float eased = t * t * (3f - 2f * t);
            transform.localRotation = Quaternion.Slerp(fromRot, _restLocalRotation, eased);
            yield return null;
        }

        transform.localRotation = _restLocalRotation;
        _returnCoroutine = null;
    }
}
