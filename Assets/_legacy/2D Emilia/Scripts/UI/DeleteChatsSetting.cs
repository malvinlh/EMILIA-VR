using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// UI controller for the "Delete All Chats" confirmation dialog.
/// 
/// - Fetches the current user ID from PlayerPrefs ("Nickname").
/// - Provides handlers for Yes/No buttons:
///   - Yes: deletes all chats for the current user via <see cref="ChatService"/>.
///   - No: simply closes the dialog.
/// - Hides the dialog automatically after either action.
/// </summary>
public class DeleteChatsSetting : MonoBehaviour
{
    [Header("UI References")]
    [Tooltip("Button that confirms deletion of all chats.")]
    public Button yesButton;

    [Tooltip("Button that cancels deletion and closes the dialog.")]
    public Button noButton;

    /// <summary>
    /// The current user ID retrieved from PlayerPrefs ("Nickname").
    /// </summary>
    private string _currentUserId;

    #region Unity Lifecycle

    /// <summary>
    /// Unity Start: caches user ID and binds button click events.
    /// </summary>
    private void Start()
    {
        _currentUserId = PlayerPrefs.GetString("Nickname", string.Empty);

        if (yesButton != null)
        {
            yesButton.onClick.AddListener(OnYesClicked);
        }

        if (noButton != null)
        {
            noButton.onClick.AddListener(OnNoClicked);
        }
    }

    #endregion

    #region Button Handlers

    /// <summary>
    /// Handler for the "Yes" button.
    /// - Calls <see cref="ChatService.DeleteAllChats"/> for the current user.
    /// - Hides the dialog after completion (success or error).
    /// </summary>
    public void OnYesClicked()
    {
        if (string.IsNullOrEmpty(_currentUserId))
        {
            Debug.LogError("[DeleteChatsSetting] No user ID found; aborting delete.");
            gameObject.SetActive(false);
            return;
        }

        StartCoroutine(
            ServiceManager.Instance.ChatService.DeleteAllChats(
                _currentUserId,
                onSuccess: () =>
                {
                    Debug.Log("[DeleteChatsSetting] All chats deleted successfully.");
                    gameObject.SetActive(false);
                },
                onError: err =>
                {
                    Debug.LogError("[DeleteChatsSetting] DeleteAllChats failed: " + err);
                    gameObject.SetActive(false);
                }
            )
        );
    }

    /// <summary>
    /// Handler for the "No" button.
    /// - Closes the dialog without performing any action.
    /// </summary>
    public void OnNoClicked()
    {
        gameObject.SetActive(false);
    }

    #endregion
}