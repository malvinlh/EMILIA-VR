using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Controls the login UI flow:
/// - Handles nickname/full name input
/// - Validates input locally
/// - Calls the <see cref="UserService"/> to upsert (insert/update) the user
/// - Routes errors to the appropriate error label
/// - Saves the nickname in PlayerPrefs on success
/// - Loads the target scene via <see cref="SceneButtonHandler"/>
/// </summary>
public class LoginUIController : MonoBehaviour
{
    #region Inspector Fields

    [Header("UI References")]
    [Tooltip("Input field for the user's full name.")]
    [SerializeField] private TMP_InputField fullNameInput;

    [Tooltip("Input field for the user's nickname (required).")]
    [SerializeField] private TMP_InputField nicknameInput;

    [Tooltip("Button to continue login once inputs are validated.")]
    [SerializeField] private Button continueButton;

    [Header("Error Labels (Separated)")]
    [Tooltip("Error label for nickname-related errors.")]
    [SerializeField] private TextMeshProUGUI nicknameErrorText;

    [Tooltip("Error label for full-name-related errors.")]
    [SerializeField] private TextMeshProUGUI fullNameErrorText;

    [Header("Scene Navigation")]
    [Tooltip("Handler responsible for loading the next scene after login.")]
    [SerializeField] private SceneButtonHandler sceneButtonHandler;

    #endregion

    #region Constants

    private const string NicknameRequiredMessage = "Nickname is required.";

    #endregion

    #region Unity Callbacks

    private void Start()
    {
        // Ensure error labels are clear on startup
        ClearAllErrors();

        // Hook up button and input field events
        continueButton.onClick.AddListener(OnContinueClicked);

        // Clear specific errors when user starts typing
        nicknameInput.onValueChanged.AddListener(_ => ClearNicknameError());
        fullNameInput.onValueChanged.AddListener(_ => ClearFullNameError());
    }

    #endregion

    #region Event Handlers

    /// <summary>
    /// Handles the Continue button click:
    /// - Validates local input
    /// - Sends request to UserService
    /// </summary>
    private void OnContinueClicked()
    {
        string nickname = nicknameInput.text.Trim();
        string fullName = fullNameInput.text.Trim();

        ClearAllErrors();

        // Local validation: nickname is mandatory
        if (string.IsNullOrWhiteSpace(nickname))
        {
            ShowNicknameError(NicknameRequiredMessage);
            return;
        }

        // Call backend via UserService
        StartCoroutine(ServiceManager.Instance.UserService.UpsertUser(
            nickname,
            fullName,
            onSuccess: () => ProcessLoginSuccess(nickname),
            onError: HandleServiceError
        ));
    }

    /// <summary>
    /// Processes a successful login:
    /// - Saves nickname in PlayerPrefs
    /// - Loads the target scene
    /// </summary>
    private void ProcessLoginSuccess(string nickname)
    {
        PlayerPrefs.SetString("Nickname", nickname);
        PlayerPrefs.Save();

        Debug.Log($"[Login] Saved Nickname: {nickname}");
        sceneButtonHandler.LoadTargetScene();
    }

    #endregion

    #region Error Handling

    /// <summary>
    /// Routes error messages to the appropriate UI label.
    /// If the message contains "full name", it is shown under full name errors,
    /// otherwise under nickname errors.
    /// </summary>
    private void HandleServiceError(string message)
    {
        if (!string.IsNullOrEmpty(message) &&
            message.ToLower().Contains("full name"))
        {
            ShowFullNameError(message);
        }
        else
        {
            ShowNicknameError(message);
        }

        Debug.LogWarning($"[Login] Service error: {message}");
    }

    #endregion

    #region Helper Methods - Error UI

    private void ShowNicknameError(string message)
    {
        if (nicknameErrorText != null) nicknameErrorText.text = message;
        else Debug.LogWarning(message);
    }

    private void ShowFullNameError(string message)
    {
        if (fullNameErrorText != null) fullNameErrorText.text = message;
        else Debug.LogWarning(message);
    }

    private void ClearNicknameError()
    {
        if (nicknameErrorText != null) nicknameErrorText.text = string.Empty;
    }

    private void ClearFullNameError()
    {
        if (fullNameErrorText != null) fullNameErrorText.text = string.Empty;
    }

    private void ClearAllErrors()
    {
        ClearNicknameError();
        ClearFullNameError();
    }

    #endregion
}