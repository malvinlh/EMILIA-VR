using UnityEngine;

/// <summary>
/// Attach to the Sea GameObject (which has a Mesh Collider).
///
/// When the journal bottle contacts the sea surface during the discard path
/// (WaitingForBottle state), the bottle is destroyed and the avatar delivers a short
/// closing dialogue before the session ends.
///
/// Bottle is identified by tag (default "JournalBottle") — set this tag on the PostJournal
/// bottle root GameObject in the Inspector, then match it in the Bottle Tag field here.
///
/// Supports both trigger and solid collider setups:
///   - isTrigger = true  → bottle passes through the surface (OnTriggerEnter)
///   - isTrigger = false → bottle physically hits the surface (OnCollisionEnter)
///
/// Inspector wiring:
///   reviewController — JournalReviewController in the scene.
///   bottleTag        — Tag set on the PostJournal bottle root (default "JournalBottle").
/// </summary>
public class SeaBottleDetector : MonoBehaviour
{
    [Header("References")]
    [Tooltip("JournalReviewController that manages the post-journal flow.")]
    public JournalReviewController reviewController;

    [Header("Detection")]
    [Tooltip("Tag assigned to the PostJournal bottle root GameObject.")]
    public string bottleTag = "JournalBottle";

    private bool _triggered;

    // ================================================================
    // LIFECYCLE
    // ================================================================

    private void Awake()
    {
        Debug.Log("[SeaBottleDetector] Awake — script is attached and running.");

        if (reviewController == null)
            Debug.LogError("[SeaBottleDetector] reviewController is not assigned in the Inspector!");

        if (string.IsNullOrEmpty(bottleTag))
            Debug.LogError("[SeaBottleDetector] bottleTag is empty — bottle detection will never match.");
    }

    // ================================================================
    // TRIGGER (isTrigger = true on the Mesh Collider)
    // ================================================================

    private void OnTriggerEnter(Collider other)
    {
        Debug.Log($"[SeaBottleDetector] OnTriggerEnter fired by: {other.name} (tag={other.tag}) — _triggered={_triggered}, IsWaitingForBottle={reviewController?.IsWaitingForBottle}");
        TryHandleBottle(other.transform);
    }

    // ================================================================
    // COLLISION (isTrigger = false on the Mesh Collider)
    // ================================================================

    private void OnCollisionEnter(Collision collision)
    {
        Debug.Log($"[SeaBottleDetector] OnCollisionEnter fired by: {collision.gameObject.name} (tag={collision.gameObject.tag}) — _triggered={_triggered}, IsWaitingForBottle={reviewController?.IsWaitingForBottle}");
        TryHandleBottle(collision.transform);
    }

    // ================================================================
    // SHARED
    // ================================================================

    private void TryHandleBottle(Transform other)
    {
        if (_triggered) return;
        if (reviewController == null || !reviewController.IsWaitingForBottle) return;

        // Walk up the hierarchy from the collider to find the tagged bottle root.
        if (!HasBottleTag(other))
        {
            Debug.Log($"[SeaBottleDetector] Ignored '{other.name}' — no '{bottleTag}' tag found in hierarchy.");
            return;
        }

        Debug.Log($"[SeaBottleDetector] Bottle '{other.name}' matched tag '{bottleTag}' — triggering HandleBottleInSea.");
        _triggered = true;
        reviewController.HandleBottleInSea();

        // Reset after a delay so a new session can trigger it again.
        Invoke(nameof(ResetTrigger), 5f);
    }

    /// <summary>
    /// Walks up the parent chain from <paramref name="t"/> looking for a GameObject
    /// whose tag matches <see cref="bottleTag"/>. Returns true if found.
    /// </summary>
    private bool HasBottleTag(Transform t)
    {
        while (t != null)
        {
            if (t.CompareTag(bottleTag)) return true;
            t = t.parent;
        }
        return false;
    }

    private void ResetTrigger() => _triggered = false;
}
