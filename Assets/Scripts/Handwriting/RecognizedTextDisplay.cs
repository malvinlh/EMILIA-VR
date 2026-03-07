using UnityEngine;
using TMPro;

/// <summary>
/// Displays recognised text using a TextMeshProUGUI component.
///
/// Prefers <see cref="RecognitionPipeline.OnFinalTextRecognized"/> when the
/// pipeline is available (LLM/VLM-refined results).  Falls back to
/// <see cref="DigitalInkBridge.OnTextRecognized"/> (raw ML Kit top-1) if the
/// pipeline is not present in the scene.
/// </summary>
public class RecognizedTextDisplay : MonoBehaviour
{
    [Tooltip("Reference to a TextMeshProUGUI component that will show the recognised text.")]
    public TextMeshProUGUI displayText;

    [Tooltip("Maximum characters shown. Older text scrolls off.")]
    public int maxChars = 200;

    private string fullText = "";
    private bool subscribedToPipeline;

    private void Start()
    {
        if (displayText == null)
        {
            displayText = GetComponentInChildren<TextMeshProUGUI>();
            if (displayText == null)
            {
                Debug.LogWarning($"[{nameof(RecognizedTextDisplay)}] No TextMeshProUGUI assigned or found in children. " +
                                 "Please assign one in the Inspector.");
            }
        }

        StartCoroutine(Subscribe());
    }

    private System.Collections.IEnumerator Subscribe()
    {
        // Give pipeline and bridge a frame to initialise
        yield return null;

        // Prefer the full pipeline (LLM/VLM-refined results)
        if (RecognitionPipeline.Instance != null)
        {
            RecognitionPipeline.Instance.OnFinalTextRecognized += OnRecognized;
            subscribedToPipeline = true;
            Debug.Log($"[{nameof(RecognizedTextDisplay)}] Subscribed to RecognitionPipeline.");
            yield break;
        }

        // Fallback: raw ML Kit results
        while (DigitalInkBridge.Instance == null)
            yield return null;

        DigitalInkBridge.Instance.OnTextRecognized += OnRecognized;
        Debug.Log($"[{nameof(RecognizedTextDisplay)}] Subscribed to DigitalInkBridge (no pipeline).");
    }

    private void OnRecognized(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        if (fullText.Length > 0)
            fullText += " ";
        fullText += text;

        // Trim if too long.
        if (fullText.Length > maxChars)
            fullText = fullText.Substring(fullText.Length - maxChars);

        if (displayText != null)
            displayText.text = fullText;
    }

    /// <summary>Clear the displayed recognised text.</summary>
    public void ClearText()
    {
        fullText = "";
        if (displayText != null) displayText.text = "";

        // Also clear pipeline context if available
        if (RecognitionPipeline.Instance != null)
            RecognitionPipeline.Instance.ClearContext();
    }

    private void OnDestroy()
    {
        if (subscribedToPipeline && RecognitionPipeline.Instance != null)
            RecognitionPipeline.Instance.OnFinalTextRecognized -= OnRecognized;
        else if (DigitalInkBridge.Instance != null)
            DigitalInkBridge.Instance.OnTextRecognized -= OnRecognized;
    }
}
