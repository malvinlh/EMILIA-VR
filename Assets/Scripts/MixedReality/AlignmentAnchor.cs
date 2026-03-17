using UnityEngine;
using UnityEngine.XR.ARFoundation;

/// <summary>
/// Creates and manages an ARAnchor at the confirmed table position to resist
/// tracking drift. After alignment, continuously corrects JournalChairTable
/// to match any drift corrections applied to the anchor by the XR runtime.
///
/// If anchor creation fails (e.g. no anchor support), the system degrades
/// gracefully — alignment still works, just without drift correction.
/// </summary>
public class AlignmentAnchor : MonoBehaviour
{
    [Header("References")]
    [Tooltip("ARAnchorManager for spatial anchor creation. Auto-found if null.")]
    public ARAnchorManager anchorManager;

    [Tooltip("The JournalChairTable transform to continuously correct.")]
    public Transform targetToAlign;

    // ── State ────────────────────────────────────────────────────────
    private ARAnchor activeAnchor;
    private Vector3 anchorToTargetOffset;
    private Quaternion anchorToTargetRotDelta;
    private bool isTracking;
    private int anchorCreationId;  // guards against late async completions

    // ================================================================
    // PUBLIC API
    // ================================================================

    /// <summary>
    /// Create a spatial anchor at the detected table pose and record
    /// the offset to the target transform (which has already been aligned).
    /// </summary>
    public async void CreateAnchorAtTable(Pose tablePose)
    {
        ReleaseAnchor();

        // Track this creation attempt so late completions from a previous
        // (cancelled) call don't install an orphaned anchor.
        int myId = ++anchorCreationId;

        if (anchorManager == null)
        {
            anchorManager = FindAnyObjectByType<ARAnchorManager>();
            if (anchorManager == null)
            {
                Debug.LogWarning("[AlignmentAnchor] No ARAnchorManager found — " +
                                 "drift correction disabled.");
                return;
            }
        }

        if (targetToAlign == null)
        {
            Debug.LogWarning("[AlignmentAnchor] targetToAlign is null.");
            return;
        }

        var result = await anchorManager.TryAddAnchorAsync(tablePose);

        // If ReleaseAnchor() was called (or a new CreateAnchor started) while
        // we were awaiting, discard this result to prevent an orphaned anchor.
        if (myId != anchorCreationId)
        {
            if (result.status.IsSuccess() && result.value != null)
                Destroy(result.value.gameObject);
            Debug.Log("[AlignmentAnchor] Anchor creation completed after release — discarded.");
            return;
        }

        if (result.status.IsSuccess())
        {
            activeAnchor = result.value;
            RecordOffset();
            isTracking = true;
            Debug.Log($"[AlignmentAnchor] Anchor created at {tablePose.position}. " +
                      $"Drift correction active.");
        }
        else
        {
            Debug.LogWarning($"[AlignmentAnchor] Anchor creation failed: {result.status}. " +
                             "Drift correction disabled.");
        }
    }

    /// <summary>
    /// Release the active anchor and stop drift correction.
    /// </summary>
    public void ReleaseAnchor()
    {
        isTracking = false;
        anchorCreationId++;  // invalidate any pending async creation

        if (activeAnchor != null)
        {
            Destroy(activeAnchor.gameObject);
            activeAnchor = null;
            Debug.Log("[AlignmentAnchor] Anchor released.");
        }
    }

    /// <summary>Whether the anchor is active and tracking.</summary>
    public bool IsAnchored => isTracking && activeAnchor != null;

    /// <summary>
    /// Recompute anchor-to-target offset after external code repositions the target.
    /// Useful when runtime calibration adjusts target height/pose.
    /// </summary>
    public void RefreshTargetOffset()
    {
        if (!isTracking || activeAnchor == null || targetToAlign == null) return;
        RecordOffset();
    }

    // ================================================================
    // DRIFT CORRECTION
    // ================================================================

    private void LateUpdate()
    {
        if (!isTracking || activeAnchor == null || targetToAlign == null) return;

        // Continuously recompute the target position/rotation from the anchor
        // If the XR runtime adjusts the anchor pose (drift correction),
        // the target transform follows automatically.
        targetToAlign.position = activeAnchor.transform.position
            + activeAnchor.transform.rotation * anchorToTargetOffset;
        targetToAlign.rotation = activeAnchor.transform.rotation * anchorToTargetRotDelta;
    }

    // ================================================================
    // INTERNAL
    // ================================================================

    private void RecordOffset()
    {
        if (activeAnchor == null || targetToAlign == null) return;

        Quaternion invAnchorRot = Quaternion.Inverse(activeAnchor.transform.rotation);
        anchorToTargetOffset = invAnchorRot * (targetToAlign.position - activeAnchor.transform.position);
        anchorToTargetRotDelta = invAnchorRot * targetToAlign.rotation;
    }

    private void OnDestroy()
    {
        ReleaseAnchor();
    }
}
