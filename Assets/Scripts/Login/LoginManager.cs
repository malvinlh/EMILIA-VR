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
    [SerializeField] [Range(0f, 2f)] private float canvasFadeDuration = 0.5f;

    private const string NicknameRequiredMessage = "Nama panggilan diperlukan.";
    private const string LoginServiceUnavailableMessage = "Layanan login tidak tersedia.";

    // Static fields survive scene loads but reset on app restart.
    private static bool   _sessionLoggedIn;
    private static string _sessionNickname;

    private void Start()
    {
        ClearAllErrors();
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

        bool nickEmpty = string.IsNullOrWhiteSpace(nickname);

        ClearAllErrors();

        if (nickEmpty)
        {
            ShowNicknameError(NicknameRequiredMessage);
            return;
        }

        if (ServiceManager.Instance == null || ServiceManager.Instance.UserService == null)
        {
            ShowNicknameError(LoginServiceUnavailableMessage);
            return;
        }

        if (clickAudioSource != null && clickSfx != null)
            clickAudioSource.PlayOneShot(clickSfx);

        StartCoroutine(UpsertAndContinue(nickname, fullName));
    }

    private IEnumerator UpsertAndContinue(string nickname, string fullName)
    {
        bool isSuccess = false;
        string serviceError = null;

        yield return StartCoroutine(ServiceManager.Instance.UserService.UpsertUser(
            nickname,
            fullName,
            onSuccess: () => isSuccess = true,
            onError: message => serviceError = message
        ));

        if (!isSuccess)
        {
            HandleServiceError(serviceError);
            yield break;
        }

        _sessionLoggedIn = true;
        _sessionNickname = nickname;

        DisableInputUI();

        StartCoroutine(LoginVfxSequence());
    }

    private IEnumerator LoginVfxSequence()
    {
        StartCoroutine(FadeInPortalVfx(beachPortalVfx,   portalFadeInDuration));
        StartCoroutine(FadeInPortalVfx(bedroomPortalVfx, portalFadeInDuration));

        yield return new WaitForSeconds(portalFadeInDuration);

        yield return StartCoroutine(FadeOutCanvas());

        onLoginSuccess?.Invoke();
    }

    private bool IsLoggedIn() => _sessionLoggedIn;

    private void RestoreLoggedInState()
    {
        _sessionLoggedIn = true;

        if (nicknameInput != null)
        {
            nicknameInput.text = _sessionNickname;
        }

        if (fullNameInput != null)
            fullNameInput.text = string.Empty;

        DisableInputUI();

        if (loginCanvas != null)
            loginCanvas.SetActive(false);

        onLoginSuccess?.Invoke();
        StartCoroutine(FadeInPortalVfx(beachPortalVfx,   portalFadeInDuration));
        StartCoroutine(FadeInPortalVfx(bedroomPortalVfx, portalFadeInDuration));
    }

    private IEnumerator FadeOutCanvas()
    {
        if (loginCanvas == null) yield break;

        var cg = loginCanvas.GetComponent<CanvasGroup>();
        if (cg == null) cg = loginCanvas.AddComponent<CanvasGroup>();

        cg.alpha = 1f;
        float elapsed = 0f;
        while (elapsed < canvasFadeDuration)
        {
            elapsed += Time.deltaTime;
            cg.alpha = 1f - Mathf.Clamp01(elapsed / canvasFadeDuration);
            yield return null;
        }
        cg.alpha = 0f;
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

    private void HandleServiceError(string message)
    {
        if (!string.IsNullOrEmpty(message) &&
            (message.ToLower().Contains("full name") || message.ToLower().Contains("nama lengkap")))
        {
            ShowFullNameError(message);
        }
        else
        {
            ShowNicknameError(string.IsNullOrEmpty(message) ? LoginServiceUnavailableMessage : message);
        }
    }

    private void ShowNicknameError(string message)
    {
        SetErrorText(errorTextNickname, message);
    }

    private void ShowFullNameError(string message)
    {
        SetErrorText(errorTextFullName, message);
    }

    private void ClearAllErrors()
    {
        SetErrorText(errorTextNickname, string.Empty);
        SetErrorText(errorTextFullName, string.Empty);
    }

    private static void SetErrorText(TMP_Text label, string message)
    {
        if (label == null) return;

        label.text = message ?? string.Empty;
        label.gameObject.SetActive(!string.IsNullOrEmpty(label.text));
    }

    private void DisableInputUI()
    {
        if (nicknameInput != null)
        {
            nicknameInput.interactable = false;
            nicknameInput.DeactivateInputField();
        }

        if (fullNameInput != null)
        {
            fullNameInput.interactable = false;
            fullNameInput.DeactivateInputField();
        }

        if (continueButton != null)
            continueButton.interactable = false;
    }
}
