using System.Collections;
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

    [Header("Audio")]
    [SerializeField] private AudioSource clickAudioSource;
    [SerializeField] private AudioClip   clickSfx;

    [Header("Portal VFX")]
    [SerializeField] private GameObject beachPortalVfx;
    [SerializeField] private GameObject bedroomPortalVfx;

    [Header("Portal VFX Fade")]
    [SerializeField] [Range(0f, 5f)] private float portalFadeInDuration = 1.5f;

    [Header("On Success")]
    [SerializeField] private UnityEvent onLoginSuccess;
    [Header("Login UI")]
    [Tooltip("Root Canvas or parent GameObject for the login UI to hide on successful login.")]
    [SerializeField] private GameObject loginCanvas;

    private const string PrefKeyNickname = "Nickname";
    private const string PrefKeyFullName  = "PlayerFullName";
    private const string PrefKeyIsLoggedIn = "IsLoggedIn";

    // Static fields survive scene loads but reset on app restart.
    private static bool   _sessionLoggedIn;
    private static string _sessionNickname;
    private static string _sessionFullName;

    private void Start()
    {
        SetErrorVisible(errorTextNickname, false);
        SetErrorVisible(errorTextFullName, false);
        beachPortalVfx?.SetActive(false);
        bedroomPortalVfx?.SetActive(false);

        if (IsLoggedIn())
        {
            RestoreLoggedInState();
            return;
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

        if (clickAudioSource != null && clickSfx != null)
            clickAudioSource.PlayOneShot(clickSfx);

        PlayerPrefs.SetString(PrefKeyNickname, nickname);
        PlayerPrefs.SetString(PrefKeyFullName,  fullName);
        PlayerPrefs.SetInt(PrefKeyIsLoggedIn, 1);
        PlayerPrefs.Save();

        _sessionLoggedIn = true;
        _sessionNickname = nickname;
        _sessionFullName = fullName;

        StartCoroutine(LoginVfxSequence());
    }

    private IEnumerator LoginVfxSequence()
    {
        StartCoroutine(FadeInPortalVfx(beachPortalVfx,   portalFadeInDuration));
        StartCoroutine(FadeInPortalVfx(bedroomPortalVfx, portalFadeInDuration));

        yield return new WaitForSeconds(portalFadeInDuration);

        // Hide the login UI (canvas) now that the VFX finished and login succeeded.
        if (loginCanvas != null)
            loginCanvas.SetActive(false);

        onLoginSuccess?.Invoke();
    }

    private bool IsLoggedIn()
    {
        if (_sessionLoggedIn)
            return true;

        return PlayerPrefs.GetInt(PrefKeyIsLoggedIn, 0) == 1;
    }

    private void RestoreLoggedInState()
    {
        _sessionLoggedIn = true;

        if (string.IsNullOrEmpty(_sessionNickname))
            _sessionNickname = PlayerPrefs.GetString(PrefKeyNickname, string.Empty);

        if (string.IsNullOrEmpty(_sessionFullName))
            _sessionFullName = PlayerPrefs.GetString(PrefKeyFullName, string.Empty);

        if (nicknameInput != null)
        {
            nicknameInput.text = _sessionNickname;
            nicknameInput.interactable = false;
        }

        if (fullNameInput != null)
        {
            fullNameInput.text = _sessionFullName;
            fullNameInput.interactable = false;
        }

        if (continueButton != null)
            continueButton.interactable = false;

        if (loginCanvas != null)
            loginCanvas.SetActive(false);
    }

    private IEnumerator FadeInPortalVfx(GameObject vfx, float duration)
    {
        if (vfx == null) yield break;
        vfx.SetActive(true);

        var systems = vfx.GetComponentsInChildren<ParticleSystem>(true);
        var origColors = new Color[systems.Length];
        for (int i = 0; i < systems.Length; i++)
            origColors[i] = systems[i].main.startColor.color;

        float elapsed = 0f;
        while (elapsed < duration)
        {
            elapsed += Time.deltaTime;
            float t = Mathf.Clamp01(elapsed / duration);
            for (int i = 0; i < systems.Length; i++)
            {
                var main = systems[i].main;
                Color c = origColors[i];
                c.a = t;
                main.startColor = new ParticleSystem.MinMaxGradient(c);
            }
            yield return null;
        }

        for (int i = 0; i < systems.Length; i++)
        {
            var main = systems[i].main;
            main.startColor = new ParticleSystem.MinMaxGradient(origColors[i]);
        }
    }

    private static void SetErrorVisible(TMP_Text label, bool visible)
    {
        label?.gameObject.SetActive(visible);
    }
}
