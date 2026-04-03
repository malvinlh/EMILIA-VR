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
    [Header("Footer References")]
    [SerializeField] private Button   micButton;
    [SerializeField] private TMP_Text micLabel;  // optional — TMP label on the mic button

    [Header("Audio Recording")]
    [SerializeField] private RecordAudio recorder;

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

    private const string TAG = "[JournalMicController]";

    // ==================================================================
    // LIFECYCLE
    // ==================================================================

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
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
        Debug.Log($"{TAG} OnMicClicked state={_micState}");
        if (recorder == null) return;
        if (_micState == MicState.Transcribing) return; // busy

        if (_micState == MicState.Recording)
        {
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

        StartCoroutine(CoTranscribe(filePath));
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
                interactable = false;
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
