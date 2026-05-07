using System.Collections;
using UnityEngine;

/// <summary>
/// Handles the mic toggle for the journal whiteboard.
/// Records audio via RecordAudio, transcribes via TranscribeApi, then injects
/// the transcribed text into ScribbleManager as typed words — same path as
/// handwriting recognition, just from voice.
///
/// Setup:
///   1. Add this component to any persistent GO in the scene.
///   2. Assign the 3D button (VintageMicButton on the 'button' child of VintageMicrophone)
///      and RecordAudio in the Inspector.
///   3. No 2D UI buttons are used — interaction is driven entirely by the 3D prop button.
/// </summary>
[DefaultExecutionOrder(300)]
public class JournalMicController : MonoBehaviour
{
    [Header("3D Mic Button")]
    [SerializeField] private VintageMicButton _micButton3D;

    [Header("Audio Recording")]
    [SerializeField] private RecordAudio recorder;

    [Header("Input Guard")]
    [Tooltip("Minimum delay between accepted activations to prevent double-fire.")]
    [SerializeField] private float clickCooldownSec = 0.25f;

    public static JournalMicController Instance { get; private set; }

    private enum MicState { Idle, Recording, Transcribing }
    private MicState  _micState = MicState.Idle;
    private Coroutine _transcribeCo;
    private float     _recordingStartTime;
    private float     _lastAcceptedClickTime = -999f;
    private int       _lastAcceptedClickFrame = -1;

    private const float MIN_RECORDING_SEC = 1.0f;
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
        if (_micButton3D != null)
            _micButton3D.OnActivated += OnMicClicked;
        else
            Debug.LogWarning($"{TAG} _micButton3D not assigned — mic disabled.");

        if (recorder != null)
        {
            recorder.OnEncoded         += OnAudioEncoded;
            recorder.OnMicStateChanged += OnRecorderMicStateChanged;
        }
        else
        {
            Debug.LogWarning($"{TAG} RecordAudio not assigned — mic disabled.");
        }

        UpdateMicVisual();
        StartCoroutine(WarmUpMicrophone());
    }

    private void Update()
    {
        var session = JournalSessionManager.Instance;
        bool journaling = session != null &&
                          session.CurrentState == JournalSessionManager.SessionState.Journaling;

        // Session ended while mic was active — reset to Idle.
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

    private IEnumerator WarmUpMicrophone()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        const string MIC_PERMISSION = "android.permission.RECORD_AUDIO";
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(MIC_PERMISSION))
        {
            UnityEngine.Android.Permission.RequestUserPermission(MIC_PERMISSION);
            yield return new UnityEngine.WaitForSeconds(1f);
        }
#endif
        int deviceCount = Microphone.devices.Length;
        Debug.Log($"{TAG} WarmUpMicrophone — devices found on start: {deviceCount}");
        yield break;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;

        if (_micButton3D != null)
            _micButton3D.OnActivated -= OnMicClicked;

        if (recorder != null)
        {
            recorder.OnEncoded         -= OnAudioEncoded;
            recorder.OnMicStateChanged -= OnRecorderMicStateChanged;
        }
    }

    // ==================================================================
    // BUTTON HANDLER
    // ==================================================================

    private void OnMicClicked()
    {
        // The 3D button is always visible regardless of session state. Guard here
        // so activations outside an active journaling session are silently ignored.
        var session = JournalSessionManager.Instance;
        if (session == null ||
            session.CurrentState != JournalSessionManager.SessionState.Journaling)
            return;

        float now = Time.unscaledTime;
        if (_lastAcceptedClickFrame == Time.frameCount)
        {
            Debug.Log($"{TAG} Duplicate activation ignored in frame {Time.frameCount}.");
            return;
        }
        if (clickCooldownSec > 0f && now - _lastAcceptedClickTime < clickCooldownSec)
        {
            Debug.Log($"{TAG} Activation ignored by cooldown ({now - _lastAcceptedClickTime:F3}s < {clickCooldownSec:F3}s).");
            return;
        }
        _lastAcceptedClickFrame = Time.frameCount;
        _lastAcceptedClickTime  = now;

        Debug.Log($"{TAG} OnMicClicked state={_micState}");
        if (recorder == null) return;

        if (_micState == MicState.Transcribing)
        {
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
                Debug.Log($"{TAG} Stop ignored — only {elapsed:F2}s recorded (min {MIN_RECORDING_SEC}s).");
                return;
            }
            recorder.StopRecording();
            return;
        }

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

    private void OnAudioEncoded(byte[] wavBytes, string fileName)
    {
        if (wavBytes == null || wavBytes.Length == 0)
        {
            _micState = MicState.Idle;
            UpdateMicVisual();
            return;
        }

        _transcribeCo = StartCoroutine(CoTranscribe(wavBytes, fileName));
    }

    // ==================================================================
    // TRANSCRIPTION
    // ==================================================================

    private IEnumerator CoTranscribe(byte[] wavBytes, string fileName)
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
        yield return api.Transcribe(
            wavBytes,
            string.IsNullOrEmpty(fileName) ? "audio.wav" : fileName,
            "audio/wav",
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
        if (_micButton3D == null) return;

        switch (_micState)
        {
            case MicState.Recording:    _micButton3D.SetRecording();    break;
            case MicState.Transcribing: _micButton3D.SetTranscribing(); break;
            default:                    _micButton3D.SetIdle();         break;
        }
    }
}
