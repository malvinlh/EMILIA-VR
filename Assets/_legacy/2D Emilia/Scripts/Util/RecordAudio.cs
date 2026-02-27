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
    private AudioClip _recordedClip;
    private float _startTime;
    private Coroutine _blinkCo;

    #endregion

    #region Unity Lifecycle

    private void Awake()
    {
        // Ensure recording folder exists
        string targetFolder = @"D:\Emilia\AI\Recordings"; // Hardcoded path
        if (!Directory.Exists(targetFolder))
            Directory.CreateDirectory(targetFolder);

        _directoryPath = targetFolder;
        _filePath = Path.Combine(_directoryPath, outputFileName);

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

        if (Microphone.devices.Length == 0)
        {
            Debug.LogError("[RecordAudio] No microphone devices found.");
            return;
        }

        string device = string.IsNullOrEmpty(micDevice) ? Microphone.devices[0] : micDevice;
        _recordedClip = Microphone.Start(device, loop: false, lengthSec: maxLengthSec, frequency: sampleRate);
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

        try { Microphone.End(null); }
        catch { /* ignored */ }

        IsRecording = false;

        float recordedSeconds = Mathf.Max(0f, Time.realtimeSinceStartup - _startTime);

        if (_recordedClip != null && recordedSeconds > 0.01f)
        {
            var trimmed = TrimClip(_recordedClip, recordedSeconds);
            WavUtility.Save(_filePath, trimmed);
            Debug.Log($"[RecordAudio] Recording saved to: {_filePath}");
        }
        else
        {
            Debug.LogWarning("[RecordAudio] No valid audio recorded, skipping save.");
        }

        _recordedClip = null;

        OnMicStateChanged?.Invoke(false);

        ApplyBlink(false);
        SetCanvasAlpha(1f);

        // Notify listeners (e.g., ChatManager) that a recording is ready
        OnSaved?.Invoke(_filePath);
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