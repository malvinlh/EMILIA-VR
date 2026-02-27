using System.Collections;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Global fade manager that handles fade-in and fade-out transitions
/// using a <see cref="CanvasGroup"/> overlay.
/// 
/// Features:
/// - Singleton pattern (accessible via <see cref="Instance"/>).
/// - Fade in/out coroutines with configurable duration.
/// - Instantly force screen to black or clear.
/// - Blocks raycasts during fade-out (to prevent accidental clicks).
/// </summary>
public class FadeManager : MonoBehaviour
{
    #region Singleton

    /// <summary>
    /// Global singleton instance of the <see cref="FadeManager"/>.
    /// </summary>
    public static FadeManager Instance { get; private set; }

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(gameObject); // enforce singleton
        }
    }

    #endregion

    #region Inspector Fields

    [Header("Fade Settings")]
    [Tooltip("CanvasGroup overlay used for fading effect.")]
    [SerializeField] private CanvasGroup _fadeCanvas;

    [Tooltip("Default fade duration in seconds.")]
    [SerializeField] private float _fadeDuration = 1f;

    #endregion

    #region Unity Callbacks

    private void Start()
    {
        // Start fully black, then fade in to reveal scene
        _fadeCanvas.alpha = 1f;
        StartCoroutine(FadeInCoroutine());
    }

    #endregion

    #region Public API

    /// <summary>
    /// Fades the screen to black over the given duration (or the default).
    /// Raycasts are blocked while the screen is fading out.
    /// </summary>
    /// <param name="customDuration">Custom fade duration in seconds. If negative, uses default.</param>
    public IEnumerator FadeOutCoroutine(float customDuration = -1f)
    {
        float duration = GetDuration(customDuration);
        _fadeCanvas.blocksRaycasts = true;
        yield return FadeRoutine(0f, 1f, duration);
    }

    /// <summary>
    /// Fades the screen in from black to fully transparent using the default duration.
    /// Raycasts are unblocked after fade completes.
    /// </summary>
    public IEnumerator FadeInCoroutine()
    {
        yield return FadeRoutine(1f, 0f, _fadeDuration);
        _fadeCanvas.blocksRaycasts = false;
    }

    /// <summary>
    /// Instantly sets the overlay to fully black and blocks raycasts.
    /// Useful when transitioning immediately to a new scene.
    /// </summary>
    public void InstantBlack()
    {
        SetAlpha(1f, true);
    }

    /// <summary>
    /// Instantly clears the overlay (fully transparent) and unblocks raycasts.
    /// </summary>
    public void InstantClear()
    {
        SetAlpha(0f, false);
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Resolves duration: returns custom if positive, otherwise default.
    /// </summary>
    private float GetDuration(float customDuration)
    {
        return customDuration > 0f ? customDuration : _fadeDuration;
    }

    /// <summary>
    /// Coroutine that interpolates alpha of the <see cref="_fadeCanvas"/> 
    /// between two values over time.
    /// </summary>
    /// <param name="from">Starting alpha (0 = clear, 1 = black).</param>
    /// <param name="to">Target alpha.</param>
    /// <param name="duration">Fade duration in seconds.</param>
    private IEnumerator FadeRoutine(float from, float to, float duration)
    {
        float elapsed = 0f;

        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            _fadeCanvas.alpha = Mathf.Lerp(from, to, elapsed / duration);
            yield return null;
        }

        _fadeCanvas.alpha = to;
    }

    /// <summary>
    /// Instantly sets alpha and raycast blocking state.
    /// </summary>
    private void SetAlpha(float alpha, bool blockRaycasts)
    {
        _fadeCanvas.alpha = alpha;
        _fadeCanvas.blocksRaycasts = blockRaycasts;
    }

    #endregion
}