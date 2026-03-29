using UnityEngine;

/// <summary>
/// Attach to the Wine Rack GameObject (Box Collider, isTrigger = true).
///
/// When the journal bottle enters the trigger zone during the keep-journal path
/// (WaitingForRack state), the bottle is immediately hidden and destroyed, then the
/// avatar delivers a short closing dialogue before the session ends.
///
/// Bottle is identified by tag (default "JournalBottle") — walk up the parent hierarchy
/// from the entering collider so any child collider on the bottle is matched correctly.
///
/// Inspector wiring:
///   reviewController — JournalReviewController in the scene.
///   bottleTag        — Tag set on the PostJournal bottle root (default "JournalBottle").
/// </summary>
[RequireComponent(typeof(Collider))]
public class WineRackProximity : MonoBehaviour
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
        Debug.Log("[WineRackProximity] Awake — script is attached and running.");

        var col = GetComponent<Collider>();
        if (!col.isTrigger)
            Debug.LogWarning("[WineRackProximity] Collider is not set to isTrigger — OnTriggerEnter will not fire.");

        if (reviewController == null)
            Debug.LogError("[WineRackProximity] reviewController is not assigned in the Inspector!");

        if (string.IsNullOrEmpty(bottleTag))
            Debug.LogError("[WineRackProximity] bottleTag is empty — bottle detection will never match.");
    }

    // ================================================================
    // TRIGGER DETECTION
    // ================================================================

    private void OnTriggerEnter(Collider other) => TryHandleBottle(other, "TriggerEnter");

    // Also handle Stay so the rack fires even when the user walks into
    // the zone while already holding the bottle (OnTriggerEnter may be
    // missed when a kinematic Rigidbody is already overlapping on entry).
    private void OnTriggerStay(Collider other) => TryHandleBottle(other, "TriggerStay");

    private void TryHandleBottle(Collider other, string eventName)
    {
        Debug.Log($"[WineRackProximity] {eventName} fired by: {other.name} (tag={other.tag}) — _triggered={_triggered}, IsWaitingForRack={reviewController?.IsWaitingForRack}");

        if (_triggered) return;
        if (reviewController == null || !reviewController.IsWaitingForRack) return;

        if (!HasBottleTag(other.transform))
        {
            Debug.Log($"[WineRackProximity] Ignored '{other.name}' — no '{bottleTag}' tag found in hierarchy.");
            return;
        }

        Debug.Log($"[WineRackProximity] Bottle matched tag '{bottleTag}' — triggering HandleBottleRacked.");
        _triggered = true;
        reviewController.HandleBottleRacked();

        Invoke(nameof(ResetTrigger), 5f);
    }

    // ================================================================
    // HELPERS
    // ================================================================

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
