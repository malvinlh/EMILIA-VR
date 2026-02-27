using UnityEngine;

/// <summary>
/// UI bridge for scene navigation and app exit actions.
/// Typically wired to Button OnClick events in the Inspector.
/// 
/// Responsibilities:
/// - Loads a target scene (by key) via <see cref="SceneFlowManager"/>.
/// - Optionally plays a button SFX before triggering the load, using the clip length
///   as the fade override so audio and transition feel synchronized.
/// - Exposes a simple Quit action for application exit.
/// </summary>
public class SceneButtonHandler : MonoBehaviour
{
    #region Inspector Fields

    [Header("Scene Settings")]
    [Tooltip("Scene key registered in SceneFlowManager's list.")]
    [SerializeField] private string _targetSceneKey = string.Empty;

    [Header("Optional Button SFX")]
    [Tooltip("Optional click sound. If assigned, its length is used as the fade duration.")]
    [SerializeField] private AudioClip _buttonSfx;

    #endregion

    #region Public API

    /// <summary>
    /// Called by a UI Button to trigger loading of the configured target scene.
    /// If an SFX is configured (and <see cref="AudioManager"/> exists), it plays first
    /// and its clip length is used as the fade duration.
    /// </summary>
    public void LoadTargetScene()
    {
        if (string.IsNullOrWhiteSpace(_targetSceneKey))
        {
            Debug.LogWarning("[SceneButtonHandler] Target scene key is empty.");
            return;
        }

        if (!HasSceneFlowManager())
        {
            return;
        }

        if (TryPlayButtonSfx(out float sfxDuration))
        {
            LoadSceneWithFade(sfxDuration);
        }
        else
        {
            LoadSceneWithFade(); // uses default fade duration
        }
    }

    /// <summary>
    /// Called by a UI Button to quit the application.
    /// No effect in the editor; works in builds.
    /// </summary>
    public void ExitApp()
    {
        Application.Quit();
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Ensures a SceneFlowManager instance exists before attempting to load.
    /// </summary>
    private bool HasSceneFlowManager()
    {
        if (SceneFlowManager.Instance == null)
        {
            Debug.LogWarning("[SceneButtonHandler] SceneFlowManager is not available.");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Plays the optional button SFX (if assigned) and returns its clip length
    /// so we can use it as the fade duration for the scene transition.
    /// </summary>
    /// <param name="duration">Outputs the clip length if played, otherwise 0.</param>
    /// <returns>True if the clip was played; otherwise false.</returns>
    private bool TryPlayButtonSfx(out float duration)
    {
        duration = 0f;

        if (_buttonSfx != null && AudioManager.Instance != null)
        {
            AudioManager.Instance.PlaySFX(_buttonSfx);
            duration = _buttonSfx.length;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Delegates the actual load to <see cref="SceneFlowManager"/>, using either
    /// an override fade duration (e.g., SFX length) or the manager's default.
    /// </summary>
    /// <param name="overrideFade">Fade duration override; pass a negative value to use default.</param>
    private void LoadSceneWithFade(float overrideFade = -1f)
    {
        if (overrideFade > 0f)
        {
            SceneFlowManager.Instance.LoadSceneByKey(_targetSceneKey, overrideFade);
        }
        else
        {
            SceneFlowManager.Instance.LoadSceneByKey(_targetSceneKey);
        }
    }

    #endregion
}