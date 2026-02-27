using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Controls scene navigation flow (next/previous/custom scene) with fade transitions.
/// Implements Singleton pattern so only one instance exists across scenes.
/// 
/// Usage:
/// - Configure a list of <see cref="SceneInfo"/> in the Inspector (ordered).
/// - Use scene keys (string identifiers) to trigger scene loads.
/// - Optionally call LoadNextScene / LoadPreviousScene based on list order.
/// - Supports fade-in/fade-out via <see cref="FadeManager"/>.
/// </summary>
public class SceneFlowManager : MonoBehaviour
{
    #region Singleton

    /// <summary>
    /// Global singleton instance of the <see cref="SceneFlowManager"/>.
    /// </summary>
    public static SceneFlowManager Instance { get; private set; }

    private void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(gameObject); // enforce single instance
        }
    }

    #endregion

    #region Inspector Fields

    [Header("Manual Scene List")]
    [Tooltip("Ordered list of scenes used for flow navigation. Each entry requires a unique key.")]
    [SerializeField] private List<SceneInfo> _scenes = new List<SceneInfo>();

    [HideInInspector]
    [SerializeField] private string _currentSceneKey = string.Empty;

    /// <summary>
    /// Key of the currently active scene. Updated whenever a new scene is loaded.
    /// </summary>
    public string CurrentSceneKey => _currentSceneKey;

    #endregion

    #region Unity Callbacks

    private void Start()
    {
        UpdateCurrentSceneKey();
    }

    private void OnEnable()  => SceneManager.sceneLoaded += OnSceneLoaded;
    private void OnDisable() => SceneManager.sceneLoaded -= OnSceneLoaded;

    #endregion

    #region Public API

    /// <summary>
    /// Loads a scene by its key with an optional fade duration override.
    /// </summary>
    /// <param name="key">Scene key (defined in <see cref="_scenes"/>).</param>
    /// <param name="fadeDurationOverride">
    /// Custom fade duration. If negative, the default fade duration is used.
    /// </param>
    public void LoadSceneByKey(string key, float fadeDurationOverride)
    {
        var target = _scenes.Find(s => s.sceneKey == key);
        if (target != null)
        {
            StartCoroutine(TransitionScene(target, fadeDurationOverride));
        }
        else
        {
            Debug.LogError($"[SceneFlowManager] Scene key not found: {key}");
        }
    }

    /// <summary>
    /// Loads a scene by its key using the default fade duration.
    /// </summary>
    /// <param name="key">Scene key to load.</param>
    public void LoadSceneByKey(string key) => LoadSceneByKey(key, -1f);

    /// <summary>
    /// Loads the next scene in the configured list, if available.
    /// </summary>
    public void LoadNextScene()
    {
        int idx = _scenes.FindIndex(s => s.sceneKey == _currentSceneKey);
        if (idx >= 0 && idx + 1 < _scenes.Count)
        {
            LoadSceneByKey(_scenes[idx + 1].sceneKey);
        }
        else
        {
            Debug.LogWarning("[SceneFlowManager] No next scene found.");
        }
    }

    /// <summary>
    /// Loads the previous scene in the configured list, if available.
    /// </summary>
    public void LoadPreviousScene()
    {
        int idx = _scenes.FindIndex(s => s.sceneKey == _currentSceneKey);
        if (idx > 0)
        {
            LoadSceneByKey(_scenes[idx - 1].sceneKey);
        }
        else
        {
            Debug.LogWarning("[SceneFlowManager] No previous scene found.");
        }
    }

    /// <summary>
    /// Convenience wrapper for UI buttons: loads a scene by key using default fade duration.
    /// </summary>
    public void LoadSceneByKeyFromButton(string key) => LoadSceneByKey(key);

    #endregion

    #region Private Helpers

    /// <summary>
    /// SceneManager callback when a scene is loaded. Updates current scene key.
    /// </summary>
    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        UpdateCurrentSceneKey();
    }

    /// <summary>
    /// Updates the current scene key based on the active Unity scene.
    /// </summary>
    private void UpdateCurrentSceneKey()
    {
        string activeName = SceneManager.GetActiveScene().name;
        foreach (var info in _scenes)
        {
            if (info.sceneName == activeName)
            {
                _currentSceneKey = info.sceneKey;
                return;
            }
        }
    }

    /// <summary>
    /// Handles scene transition with fade out, async load, and fade in.
    /// </summary>
    /// <param name="target">Target scene information.</param>
    /// <param name="customFadeDuration">Fade duration override (negative = default).</param>
    private IEnumerator TransitionScene(SceneInfo target, float customFadeDuration)
    {
        if (FadeManager.Instance != null)
        {
            yield return FadeManager.Instance.FadeOutCoroutine(customFadeDuration);
        }

        yield return SceneManager.LoadSceneAsync(target.sceneName);

        if (FadeManager.Instance != null)
        {
            yield return FadeManager.Instance.FadeInCoroutine();
        }
    }

    #endregion
}