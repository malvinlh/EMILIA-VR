using UnityEngine;

/// <summary>
/// Controls fade in/out of a world-space dialogue panel via <see cref="CanvasGroup.alpha"/>.
/// Provides an auto-hide timer that fades the panel after a configurable delay once content
/// has finished displaying (e.g., typewriter complete on final page).
/// </summary>
[RequireComponent(typeof(CanvasGroup))]
public class VRDialogueFader : MonoBehaviour
{
    [Header("Fade Timing")]
    [SerializeField] private float _fadeInDuration  = 0.3f;
    [SerializeField] private float _fadeOutDuration = 0.4f;

    [Header("Auto-Hide")]
    [Tooltip("Seconds after content finishes before auto-fading out. Set 0 to disable.")]
    [SerializeField] private float _autoHideDelay = 8f;

    private CanvasGroup _canvasGroup;
    private float _targetAlpha;
    private float _fadeSpeed;
    private float _autoHideTimer;
    private bool  _autoHideActive;

    /// <summary>True when the panel is fully visible (alpha == 1).</summary>
    public bool IsVisible => _canvasGroup != null && _canvasGroup.alpha > 0.99f;

    /// <summary>True when the panel is fully hidden (alpha == 0).</summary>
    public bool IsHidden => _canvasGroup != null && _canvasGroup.alpha < 0.01f;

    private void Awake()
    {
        _canvasGroup = GetComponent<CanvasGroup>();
        _canvasGroup.alpha = 0f;
        _targetAlpha = 0f;
    }

    private void Update()
    {
        // Lerp alpha toward target
        if (!Mathf.Approximately(_canvasGroup.alpha, _targetAlpha))
        {
            _canvasGroup.alpha = Mathf.MoveTowards(_canvasGroup.alpha, _targetAlpha, _fadeSpeed * Time.deltaTime);

            // Disable interaction while fading or hidden
            bool interactable = _canvasGroup.alpha > 0.95f;
            _canvasGroup.interactable   = interactable;
            _canvasGroup.blocksRaycasts = interactable;
        }

        // Auto-hide countdown
        if (_autoHideActive)
        {
            _autoHideTimer -= Time.deltaTime;
            if (_autoHideTimer <= 0f)
            {
                _autoHideActive = false;
                FadeOut();
            }
        }
    }

    /// <summary>Fade the panel in. Resets auto-hide timer.</summary>
    public void FadeIn()
    {
        _targetAlpha = 1f;
        _fadeSpeed = _fadeInDuration > 0f ? 1f / _fadeInDuration : 100f;
        _autoHideActive = false;
    }

    /// <summary>Fade the panel out.</summary>
    public void FadeOut()
    {
        _targetAlpha = 0f;
        _fadeSpeed = _fadeOutDuration > 0f ? 1f / _fadeOutDuration : 100f;
        _autoHideActive = false;
    }

    /// <summary>
    /// Starts the auto-hide countdown. Call this when the typewriter finishes
    /// on the last page (or when there is only one page).
    /// </summary>
    public void StartAutoHideTimer()
    {
        if (_autoHideDelay <= 0f) return;
        _autoHideTimer  = _autoHideDelay;
        _autoHideActive = true;
    }

    /// <summary>Cancels any pending auto-hide (e.g., new text arrives).</summary>
    public void CancelAutoHide()
    {
        _autoHideActive = false;
    }

    /// <summary>Immediately sets alpha to 0 without animation.</summary>
    public void HideImmediate()
    {
        // Guard: if called before our own Awake (e.g. from another script's Awake due to
        // execution-order uncertainty), initialise _canvasGroup on the spot.
        if (_canvasGroup == null) _canvasGroup = GetComponent<CanvasGroup>();
        if (_canvasGroup == null) return;

        _canvasGroup.alpha          = 0f;
        _targetAlpha                = 0f;
        _autoHideActive             = false;
        _canvasGroup.interactable   = false;
        _canvasGroup.blocksRaycasts = false;
    }
}
