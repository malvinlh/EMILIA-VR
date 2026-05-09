using UnityEngine;

public class WineRackGuidanceIndicator : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private JournalReviewController _reviewController;
    [SerializeField] private GameObject _groundVisual;
    [SerializeField] private Transform _cameraTransform;
    [Tooltip("When true, activates on DISCARD path (IsWaitingForShredder). When false (default), activates on KEEP path (IsWaitingForRack).")]
    [SerializeField] private bool _isShredderMode;

    private bool _isVisible;

    private void Start()
    {
        if (_cameraTransform == null)
        {
            var cam = Camera.main;
            if (cam != null) _cameraTransform = cam.transform;
        }

        _groundVisual?.SetActive(false);
    }

    private void Update()
    {
        bool shouldShow = _reviewController != null &&
            (_isShredderMode ? _reviewController.IsWaitingForShredder : _reviewController.IsWaitingForRack);

        if (shouldShow != _isVisible)
        {
            _isVisible = shouldShow;
            _groundVisual?.SetActive(shouldShow);
        }
    }
}
