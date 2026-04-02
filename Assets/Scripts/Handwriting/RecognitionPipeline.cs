using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// ML Kit-only handwriting recognition pipeline.
/// Receives candidate lists from <see cref="DigitalInkBridge"/> and emits
/// the best candidate text to downstream listeners.
/// </summary>
public class RecognitionPipeline : MonoBehaviour
{
    [Header("Output Filtering")]
    [Tooltip("Ignore short recognition results made only of punctuation/symbols.")]
    public bool suppressLikelyNoiseTokens = true;

    [Tooltip("Maximum length considered noise when no letters/digits are present.")]
    [Range(1, 8)]
    public int noiseOnlyMaxLength = 3;

    // ── Events ───────────────────────────────────────────────────────

    /// <summary>Fired with the final text selected from ML Kit candidates.</summary>
    public event Action<string> OnFinalTextRecognized;

    // ── Singleton accessor ───────────────────────────────────────────
    public static RecognitionPipeline Instance { get; private set; }

    // ── Runtime ──────────────────────────────────────────────────────
    private DigitalInkBridge inkBridge;
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

        Debug.Log("[RecognitionPipeline] Initialised (ML Kit only).");
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

        string bestText = SanitizeRecognizedText(SelectBestCandidate(candidates));

        // ─────────────────────────────────────────────────────────────
        // Emit final result
        // ─────────────────────────────────────────────────────────────
        if (!string.IsNullOrEmpty(bestText))
        {
            OnFinalTextRecognized?.Invoke(bestText);
        }

        processing = false;

        // Process any candidates that arrived while we were busy
        if (pendingCandidates.Count > 0)
        {
            var next = pendingCandidates.Dequeue();
            StartCoroutine(RunPipeline(next));
        }

        yield break;
    }

    /// <summary>Clear pending recognition work (e.g. when the board is cleared).</summary>
    public void ClearContext()
    {
        pendingCandidates.Clear();
    }

    // ==================================================================
    // HELPERS
    // ==================================================================

    private static string SelectBestCandidate(List<InkCandidate> candidates)
    {
        if (candidates == null || candidates.Count == 0)
            return string.Empty;

        InkCandidate best = candidates[0];
        float bestScore = best.score;

        for (int i = 1; i < candidates.Count; i++)
        {
            var candidate = candidates[i];

            if (string.IsNullOrWhiteSpace(best.text) && !string.IsNullOrWhiteSpace(candidate.text))
            {
                best = candidate;
                bestScore = candidate.score;
                continue;
            }

            if (candidate.score > bestScore)
            {
                best = candidate;
                bestScore = candidate.score;
            }
        }

        return best.text ?? string.Empty;
    }

    private string SanitizeRecognizedText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        string trimmed = text.Trim();

        if (suppressLikelyNoiseTokens
            && trimmed.Length <= noiseOnlyMaxLength
            && !ContainsLetterOrDigit(trimmed))
        {
            return string.Empty;
        }

        return trimmed;
    }

    private static bool ContainsLetterOrDigit(string text)
    {
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
                return true;
        }

        return false;
    }
}
