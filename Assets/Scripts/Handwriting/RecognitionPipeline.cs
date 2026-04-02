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

        string bestText = SelectBestCandidate(candidates);

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
}
