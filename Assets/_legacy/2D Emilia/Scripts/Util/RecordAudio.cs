using System;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

[RequireComponent(typeof(AudioSource))]
/// <summary>
/// Provides microphone recording functionality with a single toggle button UI.
/// 
/// Features:
/// - Records microphone input and saves as WAV files.
/// - Exposes events for when a recording is saved or mic state changes.
/// - Handles mic button UI states (visibility, interactability, blinking).
/// - Optional integration with an input field to show "Listening..." placeholder.
/// </summary>
public class RecordAudio : MonoBehaviour
{
    #region Inspector Fields

    [Header("Recording Settings")]
    [Tooltip("Output file name for the recording.")]
    [SerializeField] private string outputFileName = "recording.wav";

    [Tooltip("Name of the folder where recordings are stored.")]
    [SerializeField] private string folderName = "Recordings";

    [Tooltip("Target microphone device (leave empty for default).")]
    [SerializeField] private string micDevice = "";

    [Tooltip("Sample rate of the recording (Hz).")]
    [SerializeField] private int sampleRate = 44100;

    [Tooltip("Maximum recording length in seconds (default: 3599).")]
    [SerializeField] private int maxLengthSec = 3599;

    [Header("Mic UI (Single Button)")]
    [Tooltip("Single toggle button to start/stop recording.")]
    [SerializeField] private Button micButton;

    [Tooltip("CanvasGroup on the mic button for controlling alpha and raycasts.")]
    [SerializeField] private CanvasGroup micCanvasGroup;

    [Tooltip("Minimum alpha during blinking effect.")]
    [SerializeField] private float blinkMinAlpha = 0.45f;

    [Tooltip("Maximum alpha during blinking effect.")]
    [SerializeField] private float blinkMaxAlpha = 1.0f;

    [Tooltip("Blinking speed multiplier (higher = faster).")]
    [SerializeField] private float blinkSpeed = 2.0f;

    [Header("Optional Input Field")]
    [Tooltip("Optional reference to input field to disable while recording.")]
    [SerializeField] private TMP_InputField _inputField;

    #endregion

    #region Events & State

    /// <summary>
    /// Event invoked after a recording is saved. Passes the file path.
    /// </summary>
    public event Action<string> OnSaved;

    /// <summary>
    /// Event invoked whenever the mic state changes (true = recording).
    /// </summary>
    public event Action<bool> OnMicStateChanged;

    /// <summary>
    /// Indicates whether the microphone is currently recording.
    /// </summary>
    public bool IsRecording { get; private set; }

    private string _directoryPath;
    private string _filePath;
    private string _activeMicDevice;
    private AudioClip _recordedClip;
    private float _startTime;
    private Coroutine _blinkCo;

    #endregion

    #region Unity Lifecycle

    private void Awake()
    {
        EnsureOutputPathInitialized();

        if (micButton != null) 
            micButton.onClick.AddListener(OnMicClicked);

        // Ensure CanvasGroup is available for alpha & raycast control
        if (micCanvasGroup == null && micButton != null)
        {
            micCanvasGroup = micButton.GetComponent<CanvasGroup>() 
                             ?? micButton.gameObject.AddComponent<CanvasGroup>();
        }

        // Default UI state: visible, interactable, no blinking
        ApplyMicVisibility(true);
        ApplyMicInteractable(true);
        ApplyBlink(false);
    }

    private void OnDestroy()
    {
        if (micButton != null)
            micButton.onClick.RemoveListener(OnMicClicked);
    }

    #endregion

    #region Public API

    /// <summary>
    /// Synchronizes mic UI state with current logic.
    /// </summary>
    /// <param name="isOn">True if currently recording (controls blinking).</param>
    /// <param name="uiEnabled">If true, button is interactable.</param>
    /// <param name="forceHide">If true, hides the mic button completely.</param>
    /// <param name="waitingForAI">If true, disables interaction (global lock).</param>
    public void SetUIState(bool isOn, bool uiEnabled, bool forceHide, bool waitingForAI = false)
    {
        if (forceHide)
        {
            ApplyBlink(false);
            ApplyMicVisibility(false);
            return;
        }

        ApplyMicVisibility(true);

        if (waitingForAI)
        {
            ApplyMicInteractable(false);
            ApplyBlink(false);
            return;
        }

        ApplyMicInteractable(uiEnabled);
        ApplyBlink(isOn);
    }

    #endregion

    #region UI Callbacks

    private void OnMicClicked()
    {
        if (micButton != null && !micButton.interactable) return;

        if (IsRecording) StopRecording();
        else             StartRecording();
    }

    #endregion

    #region Recording

    /// <summary>
    /// Starts microphone recording using the configured settings.
    /// </summary>
    public void StartRecording()
    {
        if (IsRecording) return;
        EnsureOutputPathInitialized();

        if (Microphone.devices.Length == 0)
        {
            Debug.LogError("[RecordAudio] No microphone devices found.");
            return;
        }

        string device = ResolveMicDevice();
        _recordedClip = Microphone.Start(device, loop: false, lengthSec: maxLengthSec, frequency: sampleRate);
        if (_recordedClip == null)
        {
            Debug.LogError($"[RecordAudio] Failed to start recording on device '{device}'.");
            return;
        }

        _activeMicDevice = device;
        _startTime = Time.realtimeSinceStartup;
        IsRecording = true;

        OnMicStateChanged?.Invoke(true);

        ApplyMicVisibility(true);
        ApplyMicInteractable(true);
        ApplyBlink(true);

        Debug.Log($"[RecordAudio] Recording started on device '{device}'");
    }

    /// <summary>
    /// Stops recording and saves the audio to disk if valid.
    /// </summary>
    public void StopRecording()
    {
        if (!IsRecording) return;

        EndMicrophoneCapture();

        IsRecording = false;

        float recordedSeconds = Mathf.Max(0f, Time.realtimeSinceStartup - _startTime);
        string savedPath = null;

        try
        {
            if (_recordedClip != null && recordedSeconds > 0.01f)
            {
                var trimmed = TrimClip(_recordedClip, recordedSeconds);
                if (trimmed != null && trimmed.samples > 0)
                {
                    string targetPath = EnsureWavExtension(_filePath);
                    if (WavUtility.Save(targetPath, trimmed))
                    {
                        savedPath = targetPath;
                        Debug.Log($"[RecordAudio] Recording saved to: {savedPath}");
                    }
                    else
                    {
                        Debug.LogError("[RecordAudio] WAV save failed.");
                    }
                }
                else
                {
                    Debug.LogWarning("[RecordAudio] Trimmed clip is empty, skipping save.");
                }
            }
            else
            {
                Debug.LogWarning("[RecordAudio] No valid audio recorded, skipping save.");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[RecordAudio] Stop/save failed: {ex.Message}");
        }
        finally
        {
            _recordedClip = null;
            _activeMicDevice = null;

            OnMicStateChanged?.Invoke(false);

            ApplyBlink(false);
            SetCanvasAlpha(1f);

            // Notify listeners only when a valid file was produced.
            if (!string.IsNullOrEmpty(savedPath))
                OnSaved?.Invoke(savedPath);
        }
    }

    /// <summary>
    /// Trims the AudioClip to the specified length (in seconds).
    /// </summary>
    private AudioClip TrimClip(AudioClip clip, float length)
    {
        if (clip == null) return null;

        int samples = Mathf.Min((int)(length * clip.frequency), clip.samples);
        samples = Mathf.Max(samples, 0);

        if (samples == 0) return clip;

        var data = new float[samples * clip.channels];
        clip.GetData(data, 0);

        var newClip = AudioClip.Create(clip.name, samples, clip.channels, clip.frequency, false);
        newClip.SetData(data, 0);
        return newClip;
    }

    private void EnsureOutputPathInitialized()
    {
        if (!string.IsNullOrWhiteSpace(_filePath) && !string.IsNullOrWhiteSpace(_directoryPath))
            return;

        string safeFolderName = string.IsNullOrWhiteSpace(folderName) ? "Recordings" : folderName.Trim();
        string persistentRoot = Application.persistentDataPath;
        string targetFolder = Path.Combine(persistentRoot, safeFolderName);

        try
        {
            Directory.CreateDirectory(targetFolder);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[RecordAudio] Cannot create folder '{targetFolder}': {ex.Message}. Falling back to '{persistentRoot}'.");
            targetFolder = persistentRoot;
            Directory.CreateDirectory(targetFolder);
        }

        string safeFileName = string.IsNullOrWhiteSpace(outputFileName) ? "recording.wav" : outputFileName.Trim();
        _directoryPath = targetFolder;
        _filePath = Path.Combine(_directoryPath, safeFileName);
    }

    private string ResolveMicDevice()
    {
        if (!string.IsNullOrWhiteSpace(micDevice))
        {
            for (int i = 0; i < Microphone.devices.Length; i++)
            {
                if (string.Equals(Microphone.devices[i], micDevice, StringComparison.Ordinal))
                    return Microphone.devices[i];
            }

            Debug.LogWarning($"[RecordAudio] Preferred mic '{micDevice}' not found. Using '{Microphone.devices[0]}'.");
        }

        return Microphone.devices[0];
    }

    private void EndMicrophoneCapture()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_activeMicDevice))
                Microphone.End(_activeMicDevice);
            else
                Microphone.End(null);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[RecordAudio] Microphone.End on active device failed: {ex.Message}. Trying fallback.");

            try { Microphone.End(null); }
            catch { /* ignored */ }
        }
    }

    private static string EnsureWavExtension(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        return path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
            ? path
            : path + ".wav";
    }

    #endregion

    #region Visual Helpers (Blinking without Animator)

    private void ApplyMicVisibility(bool visible)
    {
        if (micButton != null)
            micButton.gameObject.SetActive(visible);

        if (!visible)
        {
            ApplyBlink(false);
            SetCanvasAlpha(1f);
        }
    }

    private void ApplyMicInteractable(bool interactable)
    {
        if (micButton != null)
            micButton.interactable = interactable;

        if (micCanvasGroup != null)
        {
            micCanvasGroup.interactable = interactable;
            micCanvasGroup.blocksRaycasts = interactable;
        }
    }

    private void ApplyBlink(bool shouldBlink)
    {
        if (shouldBlink)
        {
            if (_inputField != null)
            {
                _inputField.interactable = false;
                if (_inputField.placeholder is TextMeshProUGUI placeholder)
                    placeholder.text = "Listening...";
            }

            StartBlink();
        }
        else
        {
            if (_inputField != null)
            {
                _inputField.interactable = true;
                if (_inputField.placeholder is TextMeshProUGUI placeholder)
                    placeholder.text = "Write your message";
            }

            StopBlink();
        }
    }

    private void StartBlink()
    {
        if (micCanvasGroup == null) return;
        if (_blinkCo != null) StopCoroutine(_blinkCo);
        _blinkCo = StartCoroutine(CoBlink());
    }

    private void StopBlink()
    {
        if (_blinkCo != null) StopCoroutine(_blinkCo);
        _blinkCo = null;
        SetCanvasAlpha(1f);
    }

    private System.Collections.IEnumerator CoBlink()
    {
        float t = 0f;
        while (true)
        {
            t += Time.unscaledDeltaTime * blinkSpeed;
            float s = 0.5f + 0.5f * Mathf.Sin(t);
            float a = Mathf.Lerp(blinkMinAlpha, blinkMaxAlpha, s);
            SetCanvasAlpha(a);
            yield return null;
        }
    }

    private void SetCanvasAlpha(float a)
    {
        if (micCanvasGroup != null)
        {
            micCanvasGroup.alpha = a;
        }
        else if (micButton != null)
        {
            var img = micButton.GetComponent<Image>();
            if (img != null)
            {
                var c = img.color;
                c.a = a;
                img.color = c;
            }
        }
    }

    #endregion
}