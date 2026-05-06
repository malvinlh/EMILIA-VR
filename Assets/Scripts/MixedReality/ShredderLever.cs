using System.Collections;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>
/// Pull-lever for committal gestures (e.g. starting the paper shredder).
/// Attach to a pivot empty parent of the lever mesh. The pivot must also have:
///   - Rigidbody (kinematic, no gravity)
///   - Collider (sized to the grippable handle)
///   - XRGrabInteractable
///   - RotationAxisLockGrabTransformer (configured to free only the chosen pull axis)
///
/// While held, this script tracks rotation around <see cref="rotationAxis"/>
/// from the rest pose. Once the absolute angle exceeds
/// <see cref="pullThresholdDeg"/>, <see cref="OnPulled"/> fires once per hold.
/// Releasing the lever springs it back to rest.
/// </summary>
[RequireComponent(typeof(XRGrabInteractable))]
public class ShredderLever : MonoBehaviour
{
    [Header("References")]
    public XRGrabInteractable grabInteractable;

    [Header("Rotation")]
    [Tooltip("Local axis the lever pivots around. Must match the axis left free by RotationAxisLockGrabTransformer.")]
    public Vector3 rotationAxis = Vector3.right;
    [Tooltip("Absolute angle (degrees) from rest at which the pull is committed.")]
    [Range(5f, 120f)] public float pullThresholdDeg = 35f;
    [Tooltip("Hard clamp on how far the lever can rotate either side of rest.")]
    [Range(10f, 180f)] public float maxPullDeg = 60f;

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
    private bool _isHeld;
    private bool _pulledThisHold;
    private Coroutine _returnCoroutine;

    private void Awake()
    {
        _restLocalRotation = transform.localRotation;
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

    private void HandleSelectEntered(SelectEnterEventArgs _)
    {
        _isHeld = true;
        _pulledThisHold = false;
        if (_returnCoroutine != null) { StopCoroutine(_returnCoroutine); _returnCoroutine = null; }
    }

    private void HandleSelectExited(SelectExitEventArgs _)
    {
        _isHeld = false;
        if (_returnCoroutine != null) StopCoroutine(_returnCoroutine);
        _returnCoroutine = StartCoroutine(ReturnToRest());
    }

    // Run after the XRI grab pipeline has applied its rotation for the frame.
    private void LateUpdate()
    {
        if (!_isHeld) return;

        float signedAngle = SignedAngleFromRest();

        if (Mathf.Abs(signedAngle) > maxPullDeg)
        {
            float clamped = Mathf.Sign(signedAngle) * maxPullDeg;
            ApplyAngleFromRest(clamped);
            signedAngle = clamped;
        }

        if (!_pulledThisHold && Mathf.Abs(signedAngle) >= pullThresholdDeg)
        {
            _pulledThisHold = true;
            if (audioSource != null && clickClip != null)
                audioSource.PlayOneShot(clickClip, clickVolume);
            OnPulled?.Invoke();
        }
    }

    private float SignedAngleFromRest()
    {
        Quaternion delta = Quaternion.Inverse(_restLocalRotation) * transform.localRotation;
        delta.ToAngleAxis(out float angle, out Vector3 axis);

        if (angle > 180f) { angle = 360f - angle; axis = -axis; }
        if (rotationAxis.sqrMagnitude < 1e-6f) return angle;

        float dot = Vector3.Dot(axis, rotationAxis.normalized);
        return dot * angle;
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
