using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Serialisable candidate returned by ML Kit Digital Ink Recognition.
/// Score is -1 when the model does not provide confidence values.
/// </summary>
[Serializable]
public class InkCandidate
{
    public string text;
    public float score;
}

/// <summary>
/// C# bridge that communicates with the Android ML Kit Digital Ink
/// Recognition plugin via JNI (AndroidJavaObject).
///
/// Attach to a GameObject in the scene (e.g. "DigitalInkManager").
/// WhiteboardPen feeds stroke data; call Recognize() to trigger
/// recognition.  Results arrive asynchronously via OnTextRecognized
/// and OnCandidatesReady.
/// </summary>
public class DigitalInkBridge : MonoBehaviour
{
    // ── Configuration ────────────────────────────────────────────────
    [Tooltip("BCP-47 language tag for the recognition model.")]
    public string languageTag = "en-US";

    [Tooltip("Seconds of inactivity (no new strokes) before auto-recognize fires.")]
    [Range(0.5f, 10f)]
    public float autoRecognizeDelay = 2.0f;

    // ── Events ───────────────────────────────────────────────────────
    /// <summary>Fired when recognition returns a result string (top-1 candidate).</summary>
    public event Action<string> OnTextRecognized;

    /// <summary>Fired with the full list of candidates (text + score) for pipeline processing.</summary>
    public event Action<List<InkCandidate>> OnCandidatesReady;

    /// <summary>Fired when the ML Kit model is downloaded and ready.</summary>
    public event Action OnModelReady;

    // ── Runtime state ────────────────────────────────────────────────
    public bool IsModelReady { get; private set; }

    private AndroidJavaObject plugin;
    private bool waitingForResult;
    private float lastStrokeEndTime;
    private int   pendingStrokeCount;
    private bool  autoRecognizePending;

    // Pre-context: accumulated recognised text fed back to ML Kit for
    // improved language-model predictions.
    private string preContext = "";

    // ── Singleton-like accessor (optional convenience) ───────────────
    public static DigitalInkBridge Instance { get; private set; }

    // ==================================================================
    // LIFECYCLE
    // ==================================================================

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void Start()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        plugin = new AndroidJavaObject("com.vrhandwriting.digitalink.DigitalInkPlugin");
        plugin.Call("downloadModel", languageTag);
        StartCoroutine(WaitForModel());
#else
        Debug.LogWarning("[DigitalInkBridge] ML Kit only works on Android builds. " +
                         "Recognition will be unavailable in the Editor.");
#endif
    }

    private IEnumerator WaitForModel()
    {
        while (plugin != null && !plugin.Call<bool>("isModelReady"))
        {
            string err = plugin.Call<string>("getLastError");
            if (!string.IsNullOrEmpty(err))
            {
                Debug.LogError($"[DigitalInkBridge] {err}");
            }
            yield return new WaitForSeconds(0.5f);
        }

        IsModelReady = true;
        Debug.Log("[DigitalInkBridge] Model downloaded and ready.");
        OnModelReady?.Invoke();
    }

    private void Update()
    {
        // ── Poll for recognition result ──────────────────────────
        if (waitingForResult && plugin != null)
        {
            if (!plugin.Call<bool>("isRecognizing"))
            {
                waitingForResult = false;

                string result = plugin.Call<string>("getLastResult");
                List<InkCandidate> candidates = ParseCandidates(
                    plugin.Call<string>("getLastCandidatesJson"));

                if (!string.IsNullOrEmpty(result))
                {
                    // Update pre-context for next recognition cycle
                    if (preContext.Length > 0) preContext += " ";
                    preContext += result;
                    // Keep pre-context to a reasonable length (last ~200 chars)
                    if (preContext.Length > 200)
                        preContext = preContext.Substring(preContext.Length - 200);
                    plugin.Call("setPreContext", preContext);

                    Debug.Log($"[DigitalInkBridge] Recognized: {result} ({candidates.Count} candidates)");
                    OnTextRecognized?.Invoke(result);
                }

                if (candidates.Count > 0)
                {
                    OnCandidatesReady?.Invoke(candidates);
                }
            }
        }

        // ── Auto-recognize after idle delay ──────────────────────
        if (autoRecognizePending && pendingStrokeCount > 0)
        {
            if (Time.time - lastStrokeEndTime >= autoRecognizeDelay)
            {
                Recognize();
                autoRecognizePending = false;
            }
        }
    }

    private void OnDestroy()
    {
        plugin?.Dispose();
        if (Instance == this) Instance = null;
    }

    // ==================================================================
    // STROKE API  (called by WhiteboardPen)
    // ==================================================================

    /// <summary>Start a new stroke (finger touches the board).</summary>
    public void BeginStroke(float x, float y)
    {
        if (plugin == null || !IsModelReady) return;
        long ts = CurrentMillis();
        plugin.Call("beginStroke", x, y, ts);
    }

    /// <summary>Append a point to the current stroke (each frame while touching).</summary>
    public void AddPoint(float x, float y)
    {
        if (plugin == null || !IsModelReady) return;
        long ts = CurrentMillis();
        plugin.Call("addPoint", x, y, ts);
    }

    /// <summary>Finish the current stroke (finger leaves the board).</summary>
    public void EndStroke()
    {
        if (plugin == null || !IsModelReady) return;
        plugin.Call("endStroke");

        pendingStrokeCount++;
        lastStrokeEndTime    = Time.time;
        autoRecognizePending = true;
    }

    /// <summary>
    /// Set the writing surface dimensions so ML Kit can calibrate stroke scale.
    /// Call whenever the active whiteboard changes or is resized.
    /// </summary>
    public void SetWritingArea(float width, float height)
    {
        if (plugin == null) return;
        plugin.Call("setWritingArea", width, height);
    }

    // ==================================================================
    // RECOGNITION API
    // ==================================================================

    /// <summary>
    /// Trigger recognition on all accumulated strokes.
    /// The result is delivered asynchronously via <see cref="OnTextRecognized"/>
    /// and <see cref="OnCandidatesReady"/>.
    /// </summary>
    public void Recognize()
    {
        if (plugin == null || !IsModelReady) return;
        if (waitingForResult) return; // already running

        plugin.Call("recognize");
        waitingForResult   = true;
        pendingStrokeCount = 0;
    }

    /// <summary>Discard all accumulated strokes without recognising.</summary>
    public void ClearInk()
    {
        if (plugin != null) plugin.Call("clearInk");
        pendingStrokeCount   = 0;
        autoRecognizePending = false;
    }

    /// <summary>Reset the pre-context (e.g. when the board is cleared).</summary>
    public void ClearPreContext()
    {
        preContext = "";
        if (plugin != null) plugin.Call("setPreContext", "");
    }

    // ==================================================================
    // HELPERS
    // ==================================================================

    private static long CurrentMillis()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    // Wrapper for JsonUtility (which cannot deserialise bare arrays).
    [Serializable]
    private class CandidateArray
    {
        public InkCandidate[] items;
    }

    private static List<InkCandidate> ParseCandidates(string json)
    {
        var list = new List<InkCandidate>();
        if (string.IsNullOrEmpty(json) || json == "[]") return list;

        try
        {
            var arr = JsonUtility.FromJson<CandidateArray>(json);
            if (arr?.items != null)
            {
                list.AddRange(arr.items);
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[DigitalInkBridge] Failed to parse candidates JSON: {e.Message}");
        }
        return list;
    }
}
