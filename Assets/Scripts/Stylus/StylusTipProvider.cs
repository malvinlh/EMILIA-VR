using UnityEngine;

/// <summary>
/// Central coordinator that produces a single world-space pen-tip position
/// each frame. Combines the wrist-offset tracker (always available) with
/// optional CV-based correction (future GreenBandDetector). Applies
/// OneEuroFilter smoothing and optional writing-plane snap.
///
/// Consumers (e.g., WhiteboardPen) read <see cref="TipWorldPosition"/>.
/// </summary>
[DefaultExecutionOrder(-25)] // before WhiteboardPen (-20)
public class StylusTipProvider : MonoBehaviour
{
    public static StylusTipProvider Instance { get; private set; }

    [Header("References")]
    public StylusWristTracker wristTracker;
    [Tooltip("Optional CV-based green-band detector. If unavailable at runtime, " +
             "the system falls back to wrist-only tracking.")]
    public GreenBandDetector cvDetector;

    [Header("Smoothing (One Euro)")]
    [Tooltip("Minimum cutoff frequency (Hz). Lower = more smoothing at low speed.")]
    public float tipFilterMinCutoff = 2.0f;
    [Tooltip("Speed coefficient. Higher = less latency during fast movements.")]
    public float tipFilterBeta = 0.05f;
    [Tooltip("Cutoff frequency for the derivative filter (Hz).")]
    public float tipFilterDCutoff = 1.0f;
    [Tooltip("Idle time (seconds) before filters reinitialise.")]
    public float smoothingIdleResetSeconds = 0.25f;

    [Header("Writing Plane Snap")]
    [Tooltip("When the tip is within this distance (metres) of the writing plane, " +
             "begin blending it toward the plane. Replaces per-pixel depth.")]
    public float planeSnapDistance = 0.025f;

    [Header("CV Fusion (reserved)")]
    [Range(0f, 1f)] public float cvBlendWeight = 0.3f;
    [Range(0f, 1f)] public float minCvConfidence = 0.4f;

    // ── Output ───────────────────────────────────────────────────────
    public Vector3? TipWorldPosition { get; private set; }
    public float Confidence { get; private set; }
    public bool IsCalibrated => wristTracker != null && wristTracker.IsCalibrated;

    /// <summary>
    /// The writing plane used for Z/normal snapping. Set by JournalSessionManager
    /// after table detection. Default is uninitialised (no snap applied).
    /// </summary>
    public Plane WritingPlane { get; set; }
    public bool HasWritingPlane { get; private set; }

    // ── Filters ──────────────────────────────────────────────────────
    private OneEuroFilter filterX;
    private OneEuroFilter filterY;
    private OneEuroFilter filterZ;
    private float lastSampleTime;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[StylusTipProvider] Another instance exists. Disabling this one.");
            enabled = false;
            return;
        }
        Instance = this;

        BuildFilters();
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void BuildFilters()
    {
        filterX = new OneEuroFilter(tipFilterMinCutoff, tipFilterBeta, tipFilterDCutoff);
        filterY = new OneEuroFilter(tipFilterMinCutoff, tipFilterBeta, tipFilterDCutoff);
        filterZ = new OneEuroFilter(tipFilterMinCutoff, tipFilterBeta, tipFilterDCutoff);
    }

    /// <summary>
    /// Assign the writing plane used for normal-axis snapping. Typically called
    /// after ARTableDetector confirms a surface. The plane's normal defines
    /// the "up" direction for the writing surface.
    /// </summary>
    public void SetWritingPlane(Plane plane)
    {
        WritingPlane = plane;
        HasWritingPlane = true;
    }

    public void ClearWritingPlane()
    {
        HasWritingPlane = false;
    }

    private void Update()
    {
        if (wristTracker == null || !wristTracker.IsCalibrated)
        {
            TipWorldPosition = null;
            Confidence = 0f;
            return;
        }

        if (!wristTracker.TryGetTipPosition(out Vector3 wristPos, out float wristConf))
        {
            TipWorldPosition = null;
            Confidence = 0f;
            ResetFiltersIfIdle();
            return;
        }

        Vector3 fusedPos = wristPos;
        float fusedConf = wristConf;

        // ── CV fusion: blend in the green-band detection when confident ─
        if (cvDetector != null && cvDetector.IsAvailable && HasWritingPlane &&
            cvDetector.TryGetWorldPosition(WritingPlane, out Vector3 cvPos, out float cvConf) &&
            cvConf >= minCvConfidence)
        {
            float w = Mathf.Clamp01(cvBlendWeight * cvConf);
            fusedPos = Vector3.Lerp(wristPos, cvPos, w);
            fusedConf = Mathf.Max(wristConf, cvConf);
        }

        // ── Smoothing (One Euro, per axis) ───────────────────────────
        float now = Time.time;
        if (now - lastSampleTime > smoothingIdleResetSeconds)
            BuildFilters();
        lastSampleTime = now;

        Vector3 smoothed = new Vector3(
            filterX.Filter(fusedPos.x, now),
            filterY.Filter(fusedPos.y, now),
            filterZ.Filter(fusedPos.z, now));

        // ── Writing plane snap ───────────────────────────────────────
        // Within planeSnapDistance of the surface, lerp the tip onto the plane.
        // Blend factor: 0 at the outer edge, 1 when right on the plane (smooth,
        // no discontinuity). This corrects Z drift without hard-snapping.
        if (HasWritingPlane)
        {
            float signedDist = WritingPlane.GetDistanceToPoint(smoothed);
            float absDist = Mathf.Abs(signedDist);
            if (absDist < planeSnapDistance)
            {
                float blend = 1f - (absDist / planeSnapDistance); // 0..1
                smoothed -= WritingPlane.normal * (signedDist * blend);
            }
        }

        TipWorldPosition = smoothed;
        Confidence = fusedConf;
    }

    private void ResetFiltersIfIdle()
    {
        if (Time.time - lastSampleTime > smoothingIdleResetSeconds)
            BuildFilters();
    }
}
