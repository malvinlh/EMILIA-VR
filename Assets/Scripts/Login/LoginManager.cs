using TMPro;
using UnityEngine;
using UnityEngine.Events;

[DisallowMultipleComponent]
public class LoginManager : MonoBehaviour
{
    [Header("Input Fields")]
    [SerializeField] private TMP_InputField nicknameInput;
    [SerializeField] private TMP_InputField fullNameInput;

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

    private void Start()
    {
        SetErrorVisible(errorTextNickname, false);
        SetErrorVisible(errorTextFullName, false);
        if (beachPortalGlass   != null) beachPortalGlass.SetActive(false);
        if (bedroomPortalGlass != null) bedroomPortalGlass.SetActive(false);
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

        if (beachPortalGlass   != null) beachPortalGlass.SetActive(true);
        if (bedroomPortalGlass != null) bedroomPortalGlass.SetActive(true);

        onLoginSuccess?.Invoke();
    }

    private static void SetErrorVisible(TMP_Text label, bool visible)
    {
        if (label != null) label.gameObject.SetActive(visible);
    }
}
