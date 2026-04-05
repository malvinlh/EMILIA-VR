using System.Collections;
using System.IO;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Handles the Mic button on the journal whiteboard Footer.
/// Records audio via RecordAudio, transcribes via GeminiService, then injects
/// the transcribed text into ScribbleManager as typed words — same path as
/// handwriting recognition, just from voice.
///
/// Setup:
///   1. Add this component to any persistent GO in the scene (e.g., ScribbleManager GO).
///   2. Assign MicButton, RecordAudio, and optional MicLabel in the Inspector.
///      The MicButton is the "MicButton" child inside WhiteboardUI/Footer.
/// </summary>
[DefaultExecutionOrder(300)]
public class JournalMicController : MonoBehaviour
{
    [Header("Panel")]
    [Tooltip("Parent panel that contains the mic button (e.g. MicBtnPanel). " +
             "Starts hidden; shown only while SessionState == Journaling — mirrors DonePanel behaviour.")]
    [SerializeField] private GameObject micPanel;

    [Header("Footer References")]
    [SerializeField] private Button   micButton;
    [SerializeField] private TMP_Text micLabel;  // optional — TMP label on the mic button

    [Header("Audio Recording")]
    [SerializeField] private RecordAudio recorder;

    [Header("Input Guard")]
    [Tooltip("Minimum delay between accepted mic button clicks to prevent poke press+release double fire.")]
    [SerializeField] private float clickCooldownSec = 0.25f;

    [Header("Button Colors")]
    [SerializeField] private Color idleColor         = new Color(0.55f, 0.65f, 0.75f, 1f);
    [SerializeField] private Color recordingColor    = new Color(0.90f, 0.25f, 0.25f, 1f);
    [SerializeField] private Color transcribingColor = new Color(0.90f, 0.70f, 0.10f, 1f);

    [Header("Pulse (while recording)")]
    [SerializeField] private float pulseMinAlpha = 0.4f;
    [SerializeField] private float pulseMaxAlpha = 1.0f;
    [SerializeField] private float pulseSpeed    = 2.5f;

    public static JournalMicController Instance { get; private set; }

    /// <summary>Exposes the mic Button for external poke detection (JournalInlineCursor).</summary>
    public Button MicButton => micButton;

    private enum MicState { Idle, Recording, Transcribing }
    private MicState  _micState = MicState.Idle;
    private Coroutine _pulseCo;
    private Coroutine _transcribeCo;
    private float     _recordingStartTime;
    private float     _lastAcceptedClickTime = -999f;
    private int       _lastAcceptedClickFrame = -1;

    // Minimum seconds that must elapse before a Stop is accepted.
    // Guards against the poke gesture firing start+stop within the same gesture.
    private const float MIN_RECORDING_SEC = 1.0f;

    private const string TAG = "[JournalMicController]";

    // ==================================================================
    // LIFECYCLE
    // ==================================================================

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;

        // Start hidden — same as DonePanel.
        ShowMicPanel(false);
    }

    private void Start()
    {
        if (micButton != null)
            micButton.onClick.AddListener(OnMicClicked);
        else
            Debug.LogWarning($"{TAG} MicButton not assigned.");

        if (recorder != null)
        {
            recorder.OnSaved           += OnAudioSaved;
            recorder.OnMicStateChanged += OnRecorderMicStateChanged;
        }
        else
        {
            Debug.LogWarning($"{TAG} RecordAudio not assigned — mic disabled.");
        }

        UpdateMicVisual();

        // On Android/Quest, Microphone.devices can return empty for the first few
        // seconds after a scene loads while the OS audio stack initialises.
        // Accessing it early (and requesting permission if needed) warms up the
        // subsystem so it is ready before the user presses the mic button.
        StartCoroutine(WarmUpMicrophone());
    }

    private void OnEnable()
    {
        // Sync immediately on re-enable rather than waiting for the next Update tick.
        var session = JournalSessionManager.Instance;
        bool journaling = session != null &&
                          session.CurrentState == JournalSessionManager.SessionState.Journaling;
        ShowMicPanel(journaling);
    }

    private void Update()
    {
        var session = JournalSessionManager.Instance;
        bool journaling = session != null &&
                          session.CurrentState == JournalSessionManager.SessionState.Journaling;

        ShowMicPanel(journaling);

        // Session ended while mic was active — reset to Idle so the button
        // is not stuck non-interactable on the next session.
        if (!journaling && _micState != MicState.Idle)
        {
            if (_micState == MicState.Recording)
                recorder?.StopRecording();

            if (_transcribeCo != null)
            {
                StopCoroutine(_transcribeCo);
                _transcribeCo = null;
            }
            _micState = MicState.Idle;
            UpdateMicVisual();
        }
    }

    private void ShowMicPanel(bool show)
    {
        if (micPanel != null && micPanel.activeSelf != show)
            micPanel.SetActive(show);
    }

    private IEnumerator WarmUpMicrophone()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        const string MIC_PERMISSION = "android.permission.RECORD_AUDIO";
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(MIC_PERMISSION))
        {
            UnityEngine.Android.Permission.RequestUserPermission(MIC_PERMISSION);
            // Give the permission dialog time to resolve before polling devices.
            yield return new UnityEngine.WaitForSeconds(1f);
        }
#endif
        // Touch Microphone.devices to trigger subsystem init (harmless if already ready).
        int deviceCount = Microphone.devices.Length;
        Debug.Log($"{TAG} WarmUpMicrophone — devices found on start: {deviceCount}");
        yield break;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;

        if (micButton != null)
            micButton.onClick.RemoveListener(OnMicClicked);

        if (recorder != null)
        {
            recorder.OnSaved           -= OnAudioSaved;
            recorder.OnMicStateChanged -= OnRecorderMicStateChanged;
        }
    }

    // ==================================================================
    // BUTTON HANDLER
    // ==================================================================

    private void OnMicClicked()
    {
        float now = Time.unscaledTime;
        if (_lastAcceptedClickFrame == Time.frameCount)
        {
            Debug.Log($"{TAG} Duplicate click ignored in frame {Time.frameCount}.");
            return;
        }
        if (clickCooldownSec > 0f && now - _lastAcceptedClickTime < clickCooldownSec)
        {
            Debug.Log($"{TAG} Click ignored by cooldown ({now - _lastAcceptedClickTime:F3}s < {clickCooldownSec:F3}s).");
            return;
        }
        _lastAcceptedClickFrame = Time.frameCount;
        _lastAcceptedClickTime = now;

        Debug.Log($"{TAG} OnMicClicked state={_micState}");
        if (recorder == null) return;
        if (_micState == MicState.Transcribing)
        {
            // Cancel the in-flight transcription so the user can record again.
            if (_transcribeCo != null) { StopCoroutine(_transcribeCo); _transcribeCo = null; }
            _micState = MicState.Idle;
            UpdateMicVisual();
            Debug.Log($"{TAG} Transcription cancelled by user.");
            return;
        }

        if (_micState == MicState.Recording)
        {
            float elapsed = Time.realtimeSinceStartup - _recordingStartTime;
            if (elapsed < MIN_RECORDING_SEC)
            {
                Debug.Log($"{TAG} Stop ignored — only {elapsed:F2}s recorded (min {MIN_RECORDING_SEC}s). Poke double-fire guard.");
                return;
            }
            recorder.StopRecording();
            return;
        }

        // On Android the audio stack may still be initialising at the time of the
        // first button press. Retry for up to 5 s instead of failing immediately.
        if (Microphone.devices.Length == 0)
        {
            StartCoroutine(RetryStartRecording());
            return;
        }

        recorder.StartRecording();
    }

    private IEnumerator RetryStartRecording()
    {
        Debug.Log($"{TAG} Microphone not ready — waiting for devices...");
        float elapsed = 0f;
        while (Microphone.devices.Length == 0 && elapsed < 5f)
        {
            elapsed += Time.deltaTime;
            yield return null;
        }

        if (Microphone.devices.Length == 0)
        {
            Debug.LogError($"{TAG} Microphone still unavailable after 5 s retry — aborting.");
            yield break;
        }

        Debug.Log($"{TAG} Microphone became available after {elapsed:F1} s — starting recording.");
        recorder.StartRecording();
    }

    // ==================================================================
    // RECORDAUDIO CALLBACKS
    // ==================================================================

    private void OnRecorderMicStateChanged(bool isRecording)
    {
        if (isRecording)
            _recordingStartTime = Time.realtimeSinceStartup;
        _micState = isRecording ? MicState.Recording : MicState.Idle;
        UpdateMicVisual();
    }

    private void OnAudioSaved(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            _micState = MicState.Idle;
            UpdateMicVisual();
            return;
        }

        _transcribeCo = StartCoroutine(CoTranscribe(filePath));
    }

    // ==================================================================
    // TRANSCRIPTION
    // ==================================================================

    private IEnumerator CoTranscribe(string filePath)
    {
        _micState = MicState.Transcribing;
        UpdateMicVisual();

        var api = ServiceManager.Instance?.TranscribeApi;
        if (api == null)
        {
            Debug.LogWarning($"{TAG} TranscribeApi not available — transcription skipped.");
            _micState = MicState.Idle;
            UpdateMicVisual();
            yield break;
        }

        string transcribed = null;
        yield return api.TranscribeFile(
            filePath,
            onSuccess: text => transcribed = text,
            onError:   err  => Debug.LogWarning($"{TAG} Transcription error: {err}"));

        _transcribeCo = null;
        _micState = MicState.Idle;
        UpdateMicVisual();

        if (!string.IsNullOrWhiteSpace(transcribed))
        {
            Debug.Log($"{TAG} Transcribed: \"{transcribed}\"");
            ScribbleManager.Instance?.AddVoiceText(transcribed);
        }
        else
        {
            Debug.LogWarning($"{TAG} Transcription empty — nothing added.");
        }
    }

    // ==================================================================
    // VISUAL STATE
    // ==================================================================

    private void UpdateMicVisual()
    {
        if (micButton == null) return;

        string label;
        Color  color;
        bool   interactable;
        bool   shouldPulse;

        switch (_micState)
        {
            case MicState.Recording:
                label        = "● Stop";
                color        = recordingColor;
                interactable = true;
                shouldPulse  = true;
                break;
            case MicState.Transcribing:
                label        = "⌛ ...";
                color        = transcribingColor;
                interactable = true;   // interactable so poke can cancel it
                shouldPulse  = false;
                break;
            default:
                label        = "Mic";
                color        = idleColor;
                interactable = true;
                shouldPulse  = false;
                break;
        }

        if (micLabel != null)
            micLabel.text = label;

        var img = micButton.targetGraphic as Image;
        if (img != null) img.color = color;

        micButton.interactable = interactable;

        // Start or stop the pulse animation.
        if (shouldPulse && _pulseCo == null)
            _pulseCo = StartCoroutine(CoPulse(img));
        else if (!shouldPulse && _pulseCo != null)
        {
            StopCoroutine(_pulseCo);
            _pulseCo = null;
            // Restore full opacity so the button doesn't freeze mid-fade.
            if (img != null)
            {
                var c = img.color;
                c.a = 1f;
                img.color = c;
            }
        }
    }

    private IEnumerator CoPulse(Image img)
    {
        float t = 0f;
        while (true)
        {
            t += Time.unscaledDeltaTime * pulseSpeed;
            float alpha = Mathf.Lerp(pulseMinAlpha, pulseMaxAlpha,
                                     0.5f + 0.5f * Mathf.Sin(t));
            if (img != null)
            {
                var c = img.color;
                c.a = alpha;
                img.color = c;
            }
            yield return null;
        }
    }
}
