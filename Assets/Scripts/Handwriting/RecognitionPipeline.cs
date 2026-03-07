using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
// using OpenCVForUnity.CoreModule;
// using OpenCVForUnity.ImgprocModule;
// using OpenCVForUnity.ImgcodecsModule;
// using OpenCVForUnity.UnityIntegration;

/// <summary>
/// Three-layer handwriting recognition pipeline:
///   Layer 1 — Google ML Kit Digital Ink (on-device, always runs)
///   Layer 2 — Gemini LLM refinement  (cloud, optional)
///   Layer 3 — Gemini VLM + OpenCV    (cloud + image, optional fallback)
///
/// Attach to a GameObject alongside or near DigitalInkBridge.
/// If no Gemini API key is set, the pipeline gracefully degrades to Layer 1 only.
/// </summary>
public class RecognitionPipeline : MonoBehaviour
{
    // ── Configuration ────────────────────────────────────────────────

    [Header("Layer 2 — LLM Refinement")]
    [Tooltip("Always send ML Kit results to Gemini LLM for refinement. " +
             "If false, LLM is only invoked when ML Kit confidence is low.")]
    public bool alwaysRefineLLM = true;

    [Tooltip("Confidence threshold below which LLM refinement is triggered " +
             "(only used when alwaysRefineLLM is false). -1 scores are treated as low confidence.")]
    [Range(0f, 1f)]
    public float llmConfidenceThreshold = 0.6f;

    [Header("Layer 3 — VLM Fallback")]
    [Tooltip("Enable VLM image-based fallback when ML Kit returns no candidates " +
             "or all candidates have very low confidence.")]
    public bool enableVLMFallback = true;

    [Tooltip("Confidence threshold below which VLM fallback is triggered.")]
    [Range(0f, 1f)]
    public float vlmConfidenceThreshold = 0.2f;

    [Tooltip("Reference to the active whiteboard (for texture capture). " +
             "If null, will attempt to find one automatically.")]
    public Whiteboard activeWhiteboard;

    // ── Events ───────────────────────────────────────────────────────

    /// <summary>Fired with the final refined text after all layers have processed.</summary>
    public event Action<string> OnFinalTextRecognized;

    // ── Singleton accessor ───────────────────────────────────────────
    public static RecognitionPipeline Instance { get; private set; }

    // ── Runtime ──────────────────────────────────────────────────────
    private DigitalInkBridge inkBridge;
    private GeminiService gemini;
    private string preContext = "";
    private bool processing;
    private readonly Queue<List<InkCandidate>> pendingCandidates = new Queue<List<InkCandidate>>();

    // ==================================================================
    // LIFECYCLE
    // ==================================================================

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private IEnumerator Start()
    {
        // Wait for dependencies
        while (DigitalInkBridge.Instance == null)
            yield return null;

        inkBridge = DigitalInkBridge.Instance;
        inkBridge.OnCandidatesReady += OnCandidatesReady;

        gemini = GeminiService.Instance;

        Debug.Log("[RecognitionPipeline] Initialised. " +
                  $"LLM={gemini != null && gemini.IsConfigured}, " +
                  $"VLM={enableVLMFallback && gemini != null && gemini.IsConfigured}");
    }

    private void OnDestroy()
    {
        if (inkBridge != null)
            inkBridge.OnCandidatesReady -= OnCandidatesReady;
        if (Instance == this) Instance = null;
    }

    // ==================================================================
    // PIPELINE ENTRY
    // ==================================================================

    private void OnCandidatesReady(List<InkCandidate> candidates)
    {
        if (processing)
        {
            // Queue candidates so they aren't lost during fast writing
            pendingCandidates.Enqueue(candidates);
            return;
        }
        StartCoroutine(RunPipeline(candidates));
    }

    private IEnumerator RunPipeline(List<InkCandidate> candidates)
    {
        processing = true;

        string bestText = candidates.Count > 0 ? candidates[0].text : "";
        float topScore = candidates.Count > 0 ? candidates[0].score : -1f;

        // ─────────────────────────────────────────────────────────────
        // Layer 2 — LLM Refinement
        // ─────────────────────────────────────────────────────────────
        bool shouldRefine = gemini != null && gemini.IsConfigured &&
                            (alwaysRefineLLM || topScore < llmConfidenceThreshold);

        if (shouldRefine && candidates.Count > 0)
        {
            string desc = FormatCandidates(candidates);
            string refined = null;
            bool done = false;

            gemini.RefineText(desc, preContext, result => { refined = result; done = true; });

            // Wait for the Gemini coroutine to complete
            float deadline = Time.time + (gemini.timeoutSeconds + 2);
            while (!done && Time.time < deadline)
                yield return null;

            if (!string.IsNullOrEmpty(refined))
            {
                Debug.Log($"[RecognitionPipeline] LLM refined: \"{bestText}\" → \"{refined}\"");
                bestText = refined;
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Layer 3 — VLM Fallback
        // ─────────────────────────────────────────────────────────────
        bool shouldVLM = enableVLMFallback &&
                         gemini != null && gemini.IsConfigured &&
                         (candidates.Count == 0 || (topScore >= 0f && topScore < vlmConfidenceThreshold));

        if (shouldVLM)
        {
            byte[] pngBytes = CaptureWhiteboardImage();
            if (pngBytes != null && pngBytes.Length > 0)
            {
                string vlmResult = null;
                bool vlmDone = false;

                gemini.RecognizeImage(pngBytes, result => { vlmResult = result; vlmDone = true; });

                float deadline = Time.time + (gemini.timeoutSeconds + 2);
                while (!vlmDone && Time.time < deadline)
                    yield return null;

                if (!string.IsNullOrEmpty(vlmResult))
                {
                    Debug.Log($"[RecognitionPipeline] VLM result: \"{vlmResult}\"");
                    bestText = vlmResult;
                }
            }
        }

        // ─────────────────────────────────────────────────────────────
        // Emit final result
        // ─────────────────────────────────────────────────────────────
        if (!string.IsNullOrEmpty(bestText))
        {
            // Update local pre-context
            if (preContext.Length > 0) preContext += " ";
            preContext += bestText;
            if (preContext.Length > 200)
                preContext = preContext.Substring(preContext.Length - 200);

            OnFinalTextRecognized?.Invoke(bestText);
        }

        processing = false;

        // Process any candidates that arrived while we were busy
        if (pendingCandidates.Count > 0)
        {
            var next = pendingCandidates.Dequeue();
            StartCoroutine(RunPipeline(next));
        }
    }

    /// <summary>Clear accumulated pre-context (e.g. when the board is cleared).</summary>
    public void ClearContext()
    {
        preContext = "";
        pendingCandidates.Clear();
    }

    // ==================================================================
    // HELPERS
    // ==================================================================

    private static string FormatCandidates(List<InkCandidate> candidates)
    {
        var sb = new StringBuilder();
        int count = Mathf.Min(candidates.Count, 5);
        for (int i = 0; i < count; i++)
        {
            var c = candidates[i];
            sb.Append($"{i + 1}. \"{c.text}\"");
            if (c.score >= 0f) sb.Append($" (confidence: {c.score:F2})");
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>
    /// Capture the whiteboard texture as a PNG byte array, using OpenCV to
    /// crop to the text region and enhance contrast for better VLM accuracy.
    /// </summary>
    private byte[] CaptureWhiteboardImage()
    {
        if (activeWhiteboard == null)
            activeWhiteboard = FindAnyObjectByType<Whiteboard>();

        if (activeWhiteboard == null || activeWhiteboard.GetTexture() == null)
        {
            Debug.LogWarning("[RecognitionPipeline] No whiteboard texture available for VLM.");
            return null;
        }

        Texture2D tex = activeWhiteboard.GetTexture();

        return tex.EncodeToPNG();
    }

    /// <summary>
    /// Use OpenCV to produce a clean, cropped, high-contrast image of the
    /// handwritten region for optimal VLM recognition.
    /// </summary>
    // private static byte[] PreprocessWithOpenCV(Texture2D tex)
    // {
    //     // 1. Convert Texture2D → Mat (RGBA, flipped to standard top-left origin)
    //     Mat rgba = new Mat(tex.height, tex.width, CvType.CV_8UC4);
    //     OpenCVMatUtils.Texture2DToMat(tex, rgba, true, 0);

    //     // 2. Convert to grayscale
    //     Mat gray = new Mat();
    //     Imgproc.cvtColor(rgba, gray, Imgproc.COLOR_RGBA2GRAY);
    //     rgba.Dispose();

    //     // 3. Invert so ink becomes white (for contour / bounding-box detection)
    //     Mat inverted = new Mat();
    //     Core.bitwise_not(gray, inverted);

    //     // 4. Threshold to separate ink from background
    //     Mat binary = new Mat();
    //     Imgproc.threshold(inverted, binary, 30, 255, Imgproc.THRESH_BINARY);
    //     inverted.Dispose();

    //     // 5. Find bounding rect of all ink pixels
    //     Mat nonZero = new Mat();
    //     Core.findNonZero(binary, nonZero);
    //     binary.Dispose();

    //     Mat output;
    //     if (nonZero.rows() > 0)
    //     {
    //         OpenCVForUnity.CoreModule.Rect bbox = Imgproc.boundingRect(nonZero);
    //         nonZero.Dispose();

    //         // Add padding around the text region
    //         int pad = 30;
    //         int x = Mathf.Max(0, bbox.x - pad);
    //         int y = Mathf.Max(0, bbox.y - pad);
    //         int w = Mathf.Min(gray.cols() - x, bbox.width + 2 * pad);
    //         int h = Mathf.Min(gray.rows() - y, bbox.height + 2 * pad);

    //         output = new Mat(gray, new OpenCVForUnity.CoreModule.Rect(x, y, w, h));
    //     }
    //     else
    //     {
    //         // No ink detected — send full image
    //         nonZero.Dispose();
    //         output = gray;
    //     }

    //     // 6. Encode to PNG
    //     MatOfByte buf = new MatOfByte();
    //     Imgcodecs.imencode(".png", output, buf);
    //     byte[] pngBytes = buf.toArray();

    //     // Cleanup
    //     buf.Dispose();
    //     if (output != gray) output.Dispose();
    //     gray.Dispose();

    //     return pngBytes;
    // }
}
