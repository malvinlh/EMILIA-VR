using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
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

    [Tooltip("Sample rate of the recording (Hz). 16 kHz matches what speech-to-text models consume natively " +
             "and keeps WAV files ~2.75x smaller than 44.1 kHz, dramatically shrinking the on-Stop file write.")]
    [SerializeField] private int sampleRate = 16000;

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
    /// Event invoked after a recording is saved to disk. Passes the file path.
    /// </summary>
    public event Action<string> OnSaved;

    /// <summary>
    /// Event invoked the moment a recording has been encoded to a WAV byte buffer,
    /// before the (off-thread) disk write completes. Subscribers that only need the
    /// audio payload (e.g. transcription) can start consuming immediately and skip
    /// the disk round-trip.
    /// </summary>
    public event Action<byte[], string> OnEncoded;

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
    private bool _pendingStart;
    // Reused across recordings to avoid allocating a multi-MB float[] on every Stop.
    private float[] _trimBuffer;
    // True while the pre-warm coroutine is briefly holding the mic device. Real
    // user-initiated StartRecording calls wait for pre-warm to release before
    // claiming the device themselves.
    private bool _preWarming;

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

    private void Start()
    {
        // Pre-warm the audio capture path so the user's first real click does
        // not pay the 10–100 ms cost of the OS audio HW initializing for the
        // first time. We briefly Start/End the microphone, which forces the
        // platform to allocate buffers and bring up the device. Subsequent
        // Microphone.Start calls are then near-instant.
        StartCoroutine(PreWarmMicrophone());
    }

    private IEnumerator PreWarmMicrophone()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        const string MIC_PERMISSION = "android.permission.RECORD_AUDIO";
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(MIC_PERMISSION))
        {
            UnityEngine.Android.Permission.RequestUserPermission(MIC_PERMISSION);
            // Poll until permission resolves (or 10 s timeout).
            float pt = 0f;
            while (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(MIC_PERMISSION) && pt < 10f)
            {
                pt += Time.deltaTime;
                yield return null;
            }
        }
#endif
        // Wait for the audio subsystem to enumerate at least one device.
        float wait = 0f;
        while (Microphone.devices.Length == 0 && wait < 5f)
        {
            wait += Time.deltaTime;
            yield return null;
        }

        if (Microphone.devices.Length == 0)
        {
            Debug.LogWarning("[RecordAudio] PreWarm: no microphone devices found after 5 s.");
            yield break;
        }

        string device = ResolveMicDevice();
        _preWarming = true;
        AudioClip warmClip = null;
        try
        {
            warmClip = Microphone.Start(device, loop: false, lengthSec: 1, frequency: sampleRate);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[RecordAudio] PreWarm: Microphone.Start failed: {ex.Message}");
        }

        // Yield two frames so the OS audio thread has time to push the device
        // through its init path before we tear it down.
        yield return null;
        yield return null;

        try { Microphone.End(device); } catch { /* swallowed — pre-warm best-effort */ }

        if (warmClip != null) Destroy(warmClip);

        _preWarming = false;
        Debug.Log("[RecordAudio] PreWarm complete — first real Microphone.Start should now be cheap.");
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
    /// Starts microphone recording using the configured settings. The actual
    /// <see cref="Microphone.Start"/> call (which can block the main thread for
    /// 10–100 ms while initializing the audio device on Quest) is deferred by one
    /// frame so the click handler returns immediately and the mic-button visual
    /// can render before the HW init stalls the next render. Net result: the
    /// click frame stays smooth and only one render is touched by the stall.
    /// </summary>
    public void StartRecording()
    {
        if (IsRecording || _pendingStart) return;
        EnsureOutputPathInitialized();

        if (Microphone.devices.Length == 0)
        {
            Debug.LogError("[RecordAudio] No microphone devices found.");
            return;
        }

        _pendingStart = true;
        StartCoroutine(CoStartRecording());
    }

    private IEnumerator CoStartRecording()
    {
        // Yield once so the click event finishes propagating and the UI updates
        // (e.g. the mic icon flipping to "Recording") render this frame. The
        // blocking Microphone.Start runs next frame.
        yield return null;

        if (!_pendingStart) yield break;     // cancelled before start

        // If the pre-warm coroutine is currently holding the device, wait for it
        // to release before claiming the mic ourselves. Quick (≤ 2 frames in
        // practice) and only relevant if the user clicks within the brief window
        // immediately after scene load.
        while (_preWarming) yield return null;

        string device = ResolveMicDevice();
        _recordedClip = Microphone.Start(device, loop: false, lengthSec: maxLengthSec, frequency: sampleRate);
        _pendingStart = false;

        if (_recordedClip == null)
        {
            Debug.LogError($"[RecordAudio] Failed to start recording on device '{device}'.");
            yield break;
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
    /// Stops recording and saves the audio to disk if valid. Encoding to a WAV byte
    /// buffer happens on the main thread (it needs <see cref="AudioClip.GetData"/>),
    /// but the encoded bytes are then handed off to a background thread for the
    /// actual file write via <see cref="Task.Run"/>. <see cref="OnEncoded"/> fires
    /// immediately so transcription can begin in parallel; <see cref="OnSaved"/>
    /// fires later, after the disk write completes.
    /// </summary>
    public void StopRecording()
    {
        if (!IsRecording) return;

        EndMicrophoneCapture();

        IsRecording = false;

        float recordedSeconds = Mathf.Max(0f, Time.realtimeSinceStartup - _startTime);
        byte[] wavBytes       = null;
        string targetPath     = null;
        string fileName       = null;

        try
        {
            if (_recordedClip != null && recordedSeconds > 0.01f)
            {
                int floatsToEncode = ReadTrimmedSamplesIntoPool(_recordedClip, recordedSeconds);
                if (floatsToEncode > 0)
                {
                    targetPath = EnsureWavExtension(_filePath);
                    fileName   = Path.GetFileName(targetPath);
                    wavBytes   = WavUtility.EncodeToBytes(
                        _trimBuffer, floatsToEncode,
                        _recordedClip.channels, _recordedClip.frequency);
                    if (wavBytes == null)
                        Debug.LogError("[RecordAudio] WAV encode failed.");
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
            Debug.LogError($"[RecordAudio] Stop/encode failed: {ex.Message}");
            wavBytes = null;
        }
        finally
        {
            _recordedClip = null;
            _activeMicDevice = null;

            OnMicStateChanged?.Invoke(false);

            ApplyBlink(false);
            SetCanvasAlpha(1f);
        }

        // Fire OnEncoded immediately (off the main-thread blocking path) so listeners
        // such as transcription can start uploading while we write to disk in parallel.
        if (wavBytes != null && wavBytes.Length > 0)
        {
            OnEncoded?.Invoke(wavBytes, fileName);
            StartCoroutine(CoWriteWavAsync(targetPath, wavBytes));
        }
    }

    /// <summary>
    /// Writes the encoded WAV bytes to disk on a background thread and fires
    /// <see cref="OnSaved"/> when done. The main thread polls Task completion
    /// via WaitUntil so we never block any frame on disk I/O.
    /// </summary>
    private IEnumerator CoWriteWavAsync(string targetPath, byte[] wavBytes)
    {
        if (string.IsNullOrEmpty(targetPath) || wavBytes == null) yield break;

        string directory = Path.GetDirectoryName(targetPath);
        var task = Task.Run(() =>
        {
            try
            {
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);
                File.WriteAllBytes(targetPath, wavBytes);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[RecordAudio] Async WAV write failed: {ex.Message}");
                return false;
            }
        });

        yield return new WaitUntil(() => task.IsCompleted);

        if (task.Status == TaskStatus.RanToCompletion && task.Result)
        {
            Debug.Log($"[RecordAudio] Recording saved to: {targetPath}");
            OnSaved?.Invoke(targetPath);
        }
    }

    /// <summary>
    /// Reads the recorded portion of <paramref name="clip"/> into the pooled
    /// <see cref="_trimBuffer"/> and returns the number of valid floats
    /// (samples × channels) that should be encoded. Returns 0 when the clip is
    /// empty or invalid. Avoids the <see cref="AudioClip.Create"/> +
    /// <see cref="AudioClip.SetData"/> round-trip the previous TrimClip used to
    /// build a temporary clip, since the encoder consumes raw floats directly.
    /// </summary>
    private int ReadTrimmedSamplesIntoPool(AudioClip clip, float length)
    {
        if (clip == null) return 0;

        int samplesPerChannel = Mathf.Min((int)(length * clip.frequency), clip.samples);
        if (samplesPerChannel <= 0) return 0;

        int floatCount = samplesPerChannel * clip.channels;
        if (_trimBuffer == null || _trimBuffer.Length < floatCount)
            _trimBuffer = new float[floatCount];

        // GetData fills the first (clip.samples * channels) entries; the encoder only
        // reads `floatCount`, so capturing the leading recorded portion is sufficient.
        clip.GetData(_trimBuffer, 0);
        return floatCount;
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