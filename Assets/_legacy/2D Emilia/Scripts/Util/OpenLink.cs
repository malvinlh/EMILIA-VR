using UnityEngine;

/// <summary>
/// Simple utility component that opens a given URL in the system's default browser.
/// 
/// Usage:
/// - Attach this script to a UI Button or other clickable GameObject.
/// - Assign the desired URL in the Inspector.
/// - Hook <see cref="OpenExternalLink"/> to the button's OnClick event.
/// </summary>
public class OpenLink : MonoBehaviour
{
    [Header("Link Settings")]
    [Tooltip("The URL that will be opened when the action is triggered.")]
    [SerializeField] private string url = "https://example.com";

    #region Public API

    /// <summary>
    /// Opens the configured URL in the default external web browser.
    /// </summary>
    public void OpenExternalLink()
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            Debug.LogWarning("[OpenLink] No URL specified. Aborting open request.");
            return;
        }

        Application.OpenURL(url);
    }

    #endregion
}