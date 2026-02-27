using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// Centralized audio manager for handling SFX (sound effects) and BGM (background music).
/// Uses a Singleton pattern to persist across scenes.
/// </summary>
public class AudioManager : MonoBehaviour
{
    #region Singleton

    /// <summary>
    /// Global singleton instance of the <see cref="AudioManager"/>.
    /// Ensures only one instance exists across all scenes.
    /// </summary>
    public static AudioManager Instance { get; private set; }

    #endregion

    #region Constants & Fields

    private const string PrefKeyMasterVolume = "MasterVolume"; ///< PlayerPrefs key for storing master volume
    private const string SfxSourceName        = "SFXSource";   ///< Child object name expected for SFX AudioSource
    private const string BgmSourceName        = "BGMSource";   ///< Child object name expected for BGM AudioSource

    /// <summary>
    /// Maps scene names to their corresponding audio root transform.
    /// </summary>
    private readonly Dictionary<string, Transform> _sceneAudioMap = new Dictionary<string, Transform>();

    private Coroutine     _sfxDelayCoroutine; ///< Coroutine reference for delaying BGM after SFX.
    private AudioSource[] _sources;           ///< Cached audio sources managed by this AudioManager.

    #endregion

    #region Unity Callbacks

    /// <summary>
    /// Unity lifecycle: Called when the script instance is loaded.
    /// Initializes singleton, caches audio sources, restores volume, and builds scene audio map.
    /// </summary>
    private void Awake()
    {
        if (!InitializeSingleton())
            return;

        CacheAudioSources();
        RestoreMasterVolume();
        BuildSceneAudioMap();
    }

    /// <summary>
    /// Subscribes to scene load event.
    /// </summary>
    private void OnEnable()
    {
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    /// <summary>
    /// Unsubscribes from scene load event.
    /// </summary>
    private void OnDisable()
    {
        SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    #endregion

    #region Public API

    /// <summary>
    /// Sets the volume for all managed AudioSources and saves the value in PlayerPrefs.
    /// </summary>
    /// <param name="volume">The master volume level (0.0f – 1.0f).</param>
    public void SetMasterVolume(float volume)
    {
        foreach (var src in _sources)
        {
            src.volume = volume;
        }

        PlayerPrefs.SetFloat(PrefKeyMasterVolume, volume);
        PlayerPrefs.Save();
    }

    /// <summary>
    /// Plays a one-shot sound effect at the given volume.
    /// Creates a temporary GameObject that is destroyed after the clip finishes.
    /// </summary>
    /// <param name="clip">The audio clip to play.</param>
    /// <param name="volume">Volume multiplier for this clip (default: 1.0f).</param>
    /// <returns>The created <see cref="AudioSource"/> instance, or null if clip is null.</returns>
    public AudioSource PlaySFX(AudioClip clip, float volume = 1f)
    {
        if (clip == null)
            return null;

        var go  = new GameObject($"SFX_{clip.name}");
        var sfx = go.AddComponent<AudioSource>();
        sfx.clip   = clip;
        sfx.volume = volume;
        sfx.Play();

        Destroy(go, clip.length);
        return sfx;
    }

    #endregion

    #region Initialization Helpers

    /// <summary>
    /// Ensures only one <see cref="AudioManager"/> exists. 
    /// If another instance is found, destroys the duplicate.
    /// </summary>
    private bool InitializeSingleton()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
            return true;
        }

        Destroy(gameObject);
        return false;
    }

    /// <summary>
    /// Caches all AudioSources that are children of this GameObject.
    /// </summary>
    private void CacheAudioSources()
    {
        _sources = GetComponentsInChildren<AudioSource>(true);
    }

    /// <summary>
    /// Restores the master volume from PlayerPrefs, defaulting to 1.0 if not found.
    /// </summary>
    private void RestoreMasterVolume()
    {
        var savedVol = PlayerPrefs.GetFloat(PrefKeyMasterVolume, 1f);
        SetMasterVolume(savedVol);
    }

    /// <summary>
    /// Builds a dictionary mapping scene names to child transforms for audio roots.
    /// </summary>
    private void BuildSceneAudioMap()
    {
        _sceneAudioMap.Clear();

        foreach (Transform child in transform)
        {
            _sceneAudioMap[child.name] = child;
        }
    }

    #endregion

    #region Scene BGM Logic

    /// <summary>
    /// Callback executed when a scene is loaded.
    /// Triggers playback of scene-specific audio setup.
    /// </summary>
    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        => PlaySceneBGM(scene.name);

    /// <summary>
    /// Plays scene-specific BGM and optionally SFX if available in the audio root.
    /// </summary>
    /// <param name="sceneName">The name of the scene that was loaded.</param>
    private void PlaySceneBGM(string sceneName)
    {
        if (!_sceneAudioMap.TryGetValue(sceneName, out var audioRoot))
        {
            Debug.LogWarning($"[AudioManager] No audio setup found for scene '{sceneName}'.");
            return;
        }

        StopAllSceneAudio();
        CancelPendingSfxDelay();

        var sfxTransform = audioRoot.Find(SfxSourceName);
        var bgmTransform = audioRoot.Find(BgmSourceName);

        AudioSource sfx = null;
        if (sfxTransform != null)
        {
            sfxTransform.TryGetComponent(out sfx);
        }

        AudioSource bgm = null;
        if (bgmTransform != null)
        {
            bgmTransform.TryGetComponent(out bgm);
        }

        if (sfx != null)
        {
            sfx.Play();
            if (bgm != null)
            {
                _sfxDelayCoroutine = StartCoroutine(PlayBGMAfterSFX(sfx.clip.length, bgm));
            }
        }
        else if (bgm != null)
        {
            bgm.Play();
        }
    }

    /// <summary>
    /// Stops all AudioSources in the current scene audio map.
    /// </summary>
    private void StopAllSceneAudio()
    {
        foreach (var kvp in _sceneAudioMap)
        {
            foreach (var src in kvp.Value.GetComponentsInChildren<AudioSource>())
            {
                src.Stop();
            }
        }
    }

    /// <summary>
    /// Cancels any pending coroutine that delays BGM playback after SFX.
    /// </summary>
    private void CancelPendingSfxDelay()
    {
        if (_sfxDelayCoroutine != null)
        {
            StopCoroutine(_sfxDelayCoroutine);
            _sfxDelayCoroutine = null;
        }
    }

    /// <summary>
    /// Coroutine that waits for the given delay before playing the BGM.
    /// </summary>
    /// <param name="delay">Delay in seconds before playing the BGM.</param>
    /// <param name="bgm">The BGM AudioSource to play.</param>
    private IEnumerator PlayBGMAfterSFX(float delay, AudioSource bgm)
    {
        yield return new WaitForSeconds(delay);
        bgm.Play();
    }

    #endregion
}