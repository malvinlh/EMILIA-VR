using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;
using TMPro;

[DisallowMultipleComponent]
public class LoginManager : MonoBehaviour
{
    [Header("Input Fields")]
    [SerializeField] private TMP_InputField nicknameInput;
    [SerializeField] private TMP_InputField fullNameInput;

    [Header("Continue Button")]
    [SerializeField] private Button continueButton;

    [Header("Error Text")]
    [SerializeField] private TMP_Text errorTextNickname;
    [SerializeField] private TMP_Text errorTextFullName;

    [Header("Portal Glass GameObjects")]
    [SerializeField] private GameObject beachPortalGlass;
    [SerializeField] private GameObject bedroomPortalGlass;

    [Header("On Success")]
    [SerializeField] private UnityEvent onLoginSuccess;

    private const string PrefKeyNickname = "Nickname";
    private const string PrefKeyFullName  = "PlayerFullName";

    // Static fields survive scene loads but reset on app restart.
    private static bool   _sessionLoggedIn;
    private static string _sessionNickname;
    private static string _sessionFullName;

    private void Start()
    {
        SetErrorVisible(errorTextNickname, false);
        SetErrorVisible(errorTextFullName, false);
        beachPortalGlass?.SetActive(false);
        bedroomPortalGlass?.SetActive(false);

        if (_sessionLoggedIn)
        {
            if (nicknameInput != null) { nicknameInput.text = _sessionNickname; nicknameInput.interactable = false; }
            if (fullNameInput  != null) { fullNameInput.text  = _sessionFullName;  fullNameInput.interactable  = false; }
            if (continueButton != null)   continueButton.interactable = false;
            beachPortalGlass?.SetActive(true);
            bedroomPortalGlass?.SetActive(true);
        }
    }

    public void OnContinueClicked()
    {
        string nickname = nicknameInput != null ? nicknameInput.text.Trim() : string.Empty;
        string fullName = fullNameInput != null ? fullNameInput.text.Trim() : string.Empty;

        bool nickEmpty = string.IsNullOrEmpty(nickname);
        bool nameEmpty = string.IsNullOrEmpty(fullName);

        SetErrorVisible(errorTextNickname, nickEmpty);
        SetErrorVisible(errorTextFullName, nameEmpty);

        if (nickEmpty || nameEmpty) return;

        PlayerPrefs.SetString(PrefKeyNickname, nickname);
        PlayerPrefs.SetString(PrefKeyFullName,  fullName);
        PlayerPrefs.Save();

        _sessionLoggedIn = true;
        _sessionNickname = nickname;
        _sessionFullName = fullName;

        beachPortalGlass?.SetActive(true);
        bedroomPortalGlass?.SetActive(true);

        onLoginSuccess?.Invoke();
    }

    private static void SetErrorVisible(TMP_Text label, bool visible)
    {
        label?.gameObject.SetActive(visible);
    }
}
