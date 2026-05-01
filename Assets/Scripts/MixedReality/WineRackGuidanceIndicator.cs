using UnityEngine;

/// <summary>
/// Guidance indicator for the Wine Rack KEEP path (ReviewState.WaitingForRack).
/// Controls two visuals:
///   _arrowVisual  — floats above the rack, bobs up/down, billboards toward the camera.
///   _groundVisual — flat ring on the floor beneath the rack, stationary.
/// Both are hidden by default and shown only while WaitingForRack.
///
/// Inspector wiring:
///   _reviewController — JournalReviewController in the scene
///   _arrowVisual      — child GO with the floating arrow Canvas
///   _groundVisual     — child GO with the ground ring Canvas (rotated X=-90)
/// Camera is auto-found via Camera.main if not assigned.
/// </summary>
public class WineRackGuidanceIndicator : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private JournalReviewController _reviewController;
    [SerializeField] private GameObject _arrowVisual;
    [SerializeField] private GameObject _groundVisual;
    [SerializeField] private Transform _cameraTransform;

    [Header("Arrow Animation")]
    [SerializeField] private float _bobAmplitude = 0.12f;
    [SerializeField] private float _bobSpeed = 1.5f;

    private Vector3 _arrowBaseLocalPos;
    private bool _isVisible;

    private void Start()
    {
        if (_cameraTransform == null)
        {
            var cam = Camera.main;
            if (cam != null) _cameraTransform = cam.transform;
        }

        if (_arrowVisual != null)
        {
            _arrowBaseLocalPos = _arrowVisual.transform.localPosition;
            _arrowVisual.SetActive(false);
        }

        _groundVisual?.SetActive(false);
    }

    private void Update()
    {
        bool shouldShow = _reviewController != null && _reviewController.IsWaitingForRack;

        if (shouldShow != _isVisible)
        {
            _isVisible = shouldShow;
            _arrowVisual?.SetActive(shouldShow);
            _groundVisual?.SetActive(shouldShow);
        }

        if (!_isVisible || _arrowVisual == null) return;

        // Bob
        float yOffset = Mathf.Sin(Time.time * _bobSpeed) * _bobAmplitude;
        _arrowVisual.transform.localPosition = _arrowBaseLocalPos + Vector3.up * yOffset;

        // Billboard (Y-axis only — keeps arrow pointing down)
        if (_cameraTransform != null)
        {
            Vector3 toCam = _cameraTransform.position - _arrowVisual.transform.position;
            toCam.y = 0f;
            if (toCam.sqrMagnitude > 0.001f)
                _arrowVisual.transform.rotation = Quaternion.LookRotation(toCam);
        }
    }
}
