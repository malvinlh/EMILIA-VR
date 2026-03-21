using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Lightweight wrapper around the Google Gemini REST API.
/// Supports both text-only (LLM) and multimodal (VLM) requests.
///
/// Free tier: Gemini 2.0 Flash — 15 RPM, 1 M TPM.
/// No SDK dependency; uses UnityWebRequest directly.
/// </summary>
public class GeminiService : MonoBehaviour
{
    [Tooltip("Gemini API key. Get one free at https://aistudio.google.com/apikey")]
    public string apiKey = "";

    [Tooltip("Model ID to use for text refinement.")]
    public string textModel = "gemini-2.0-flash";

    [Tooltip("Model ID to use for vision/OCR requests.")]
    public string visionModel = "gemini-2.0-flash";

    [Tooltip("Request timeout in seconds.")]
    [Range(5, 30)]
    public int timeoutSeconds = 10;

    public bool IsConfigured => !string.IsNullOrEmpty(apiKey);

    // ── Singleton accessor ───────────────────────────────────────────
    public static GeminiService Instance { get; private set; }

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    // ==================================================================
    // TEXT (LLM) — Refine ML Kit candidates
    // ==================================================================

    /// <summary>
    /// Send the top-N ML Kit candidates and context to Gemini for refinement.
    /// <paramref name="callback"/> receives the corrected text, or null on failure.
    /// </summary>
    public Coroutine RefineText(string candidatesDescription, string preContext,
                                Action<string> callback)
    {
        if (!IsConfigured) { callback?.Invoke(null); return null; }

        string prompt =
            "You are a handwriting recognition post-processor. " +
            "A user wrote text by hand on a VR whiteboard and an on-device recognizer " +
            "produced these candidate readings:\n\n" +
            candidatesDescription + "\n\n" +
            (string.IsNullOrEmpty(preContext)
                ? ""
                : "Previously written text for context: \"" + preContext + "\"\n\n") +
            "Instructions:\n" +
            "- Pick the most likely correct reading, or provide a corrected version.\n" +
            "- Consider common English words, the preceding context, and typical " +
            "handwriting recognition errors (rn<->m, cl<->d, l<->1<->I, O<->0, u<->v, n<->h).\n" +
            "- Respond with ONLY the corrected text. No explanation, no quotes.";

        return StartCoroutine(PostText(textModel, prompt, callback));
    }

    // ==================================================================
    // AUDIO (STT) — Speech-to-text transcription via Gemini
    // ==================================================================

    /// <summary>
    /// Send a WAV recording to Gemini for speech-to-text transcription.
    /// <paramref name="callback"/> receives the transcribed text, or null on failure.
    /// </summary>
    public Coroutine TranscribeAudio(byte[] wavBytes, Action<string> callback)
    {
        if (!IsConfigured || wavBytes == null || wavBytes.Length == 0)
        {
            callback?.Invoke(null);
            return null;
        }

        return StartCoroutine(PostAudio(textModel, wavBytes, callback));
    }

    private IEnumerator PostAudio(string model, byte[] wavBytes, Action<string> callback)
    {
        string url    = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";
        string base64 = Convert.ToBase64String(wavBytes);

        string body =
            "{\"contents\":[{\"parts\":[" +
            "{\"text\":\"Transcribe the speech in this audio exactly. Return only the spoken words, no extra commentary.\"}," +
            "{\"inline_data\":{\"mime_type\":\"audio/wav\",\"data\":\"" + base64 + "\"}}" +
            "]}]}";

        yield return SendRequest(url, body, callback);
    }

    // ==================================================================
    // VISION (VLM) — OCR on whiteboard image
    // ==================================================================

    /// <summary>
    /// Send a PNG image of the whiteboard to Gemini Vision for OCR.
    /// <paramref name="callback"/> receives the recognised text, or null on failure.
    /// </summary>
    public Coroutine RecognizeImage(byte[] pngBytes, Action<string> callback)
    {
        if (!IsConfigured || pngBytes == null || pngBytes.Length == 0)
        {
            callback?.Invoke(null);
            return null;
        }

        string prompt =
            "Read the handwritten text in this image. The image shows dark ink on a light surface. " +
            "Return ONLY the text content, nothing else. If the text is unclear, give your best guess.";

        return StartCoroutine(PostVision(visionModel, prompt, pngBytes, callback));
    }

    // ==================================================================
    // INTERNAL — HTTP helpers
    // ==================================================================

    private IEnumerator PostText(string model, string prompt, Action<string> callback)
    {
        string url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";

        string body = JsonUtility.ToJson(new GeminiRequest
        {
            contents = new[]
            {
                new GeminiContent
                {
                    parts = new[] { new GeminiPart { text = prompt } }
                }
            }
        });

        yield return SendRequest(url, body, callback);
    }

    private IEnumerator PostVision(string model, string prompt, byte[] pngBytes,
                                   Action<string> callback)
    {
        string url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";

        string base64 = Convert.ToBase64String(pngBytes);

        // Build JSON manually because JsonUtility can't serialise the mixed-type parts array
        string body =
            "{\"contents\":[{\"parts\":[" +
            "{\"text\":" + EscapeJsonString(prompt) + "}," +
            "{\"inline_data\":{\"mime_type\":\"image/png\",\"data\":\"" + base64 + "\"}}" +
            "]}]}";

        yield return SendRequest(url, body, callback);
    }

    private IEnumerator SendRequest(string url, string jsonBody, Action<string> callback)
    {
        using var request = new UnityWebRequest(url, "POST");
        byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonBody);
        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");
        request.timeout = timeoutSeconds;

        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogWarning($"[GeminiService] Request failed: {request.error}");
            callback?.Invoke(null);
            yield break;
        }

        string responseText = ExtractText(request.downloadHandler.text);
        callback?.Invoke(responseText);
    }

    /// <summary>
    /// Extract the generated text from Gemini's JSON response.
    /// Avoids a full JSON deserialiser — the response structure is simple and stable.
    /// </summary>
    private static string ExtractText(string json)
    {
        // Response shape:
        // { "candidates": [{ "content": { "parts": [{ "text": "..." }] } }] }
        try
        {
            const string marker = "\"text\"";
            int idx = json.IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) return null;

            // Find the colon after "text"
            int colon = json.IndexOf(':', idx + marker.Length);
            if (colon < 0) return null;

            // Find opening quote of the value
            int start = json.IndexOf('"', colon + 1);
            if (start < 0) return null;
            start++; // skip the quote

            // Find closing quote (handling escaped quotes)
            var sb = new StringBuilder();
            for (int i = start; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '\\' && i + 1 < json.Length)
                {
                    char next = json[i + 1];
                    if (next == '"') { sb.Append('"'); i++; }
                    else if (next == 'n') { sb.Append('\n'); i++; }
                    else if (next == '\\') { sb.Append('\\'); i++; }
                    else { sb.Append(c); }
                }
                else if (c == '"')
                {
                    break;
                }
                else
                {
                    sb.Append(c);
                }
            }

            return sb.ToString().Trim();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[GeminiService] Failed to parse response: {e.Message}");
            return null;
        }
    }

    private static string EscapeJsonString(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                default:   sb.Append(c);      break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    // ==================================================================
    // SERIALISATION HELPERS (for JsonUtility — text-only requests)
    // ==================================================================

    [Serializable] private class GeminiRequest  { public GeminiContent[] contents; }
    [Serializable] private class GeminiContent  { public GeminiPart[] parts; }
    [Serializable] private class GeminiPart     { public string text; }
}
