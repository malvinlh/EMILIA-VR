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

    [Header("Audio")]
    [Tooltip("Splash SFX played immediately on first valid bottle-sea contact.")]
    public AudioClip seaSplashClip;
    [Tooltip("Volume for the sea splash SFX.")]
    [Range(0f, 1f)] public float seaSplashVolume = 1f;
    [Tooltip("Dedicated source used for splash playback. If null and auto-create is enabled, one is created on this GameObject.")]
    public AudioSource seaSplashSource;
    [Tooltip("Play splash in 2D so it is always audible regardless of listener distance.")]
    public bool playSplashAs2D = true;
    [Tooltip("Create/fetch an AudioSource automatically when seaSplashSource is not assigned.")]
    public bool autoCreateSplashSource = true;

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

        EnsureSeaSplashSource();
        if (seaSplashClip != null && !seaSplashClip.preloadAudioData && seaSplashClip.loadState == AudioDataLoadState.Unloaded)
            seaSplashClip.LoadAudioData();
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

        _triggered = true;
        // First action on initial sea contact: play splash before bottle disposal flow.
        PlaySeaSplash(other.position);
        Debug.Log($"[SeaBottleDetector] Bottle '{other.name}' matched tag '{bottleTag}' — triggering HandleBottleInSea.");
        reviewController.HandleBottleInSea();

        // Reset after a delay so a new session can trigger it again.
        Invoke(nameof(ResetTrigger), 5f);
    }

    private void PlaySeaSplash(Vector3 worldPosition)
    {
        if (seaSplashClip == null)
        {
            Debug.LogWarning("[SeaBottleDetector] seaSplashClip is not assigned. Skipping splash SFX.");
            return;
        }

        AudioSource source = EnsureSeaSplashSource();
        if (source == null)
        {
            // Last-resort fallback if source setup is intentionally disabled.
            AudioSource.PlayClipAtPoint(seaSplashClip, worldPosition, seaSplashVolume);
            return;
        }

        source.spatialBlend = playSplashAs2D ? 0f : 1f;
        if (!playSplashAs2D)
            source.transform.position = worldPosition;

        source.volume = 1f;
        source.PlayOneShot(seaSplashClip, seaSplashVolume);
    }

    private AudioSource EnsureSeaSplashSource()
    {
        if (seaSplashSource != null)
            return seaSplashSource;

        if (!autoCreateSplashSource)
            return null;

        seaSplashSource = GetComponent<AudioSource>();
        if (seaSplashSource == null)
            seaSplashSource = gameObject.AddComponent<AudioSource>();

        seaSplashSource.playOnAwake = false;
        seaSplashSource.loop = false;
        seaSplashSource.mute = false;
        seaSplashSource.priority = 0;
        seaSplashSource.dopplerLevel = 0f;
        seaSplashSource.rolloffMode = AudioRolloffMode.Linear;
        seaSplashSource.minDistance = 1f;
        seaSplashSource.maxDistance = 25f;
        seaSplashSource.outputAudioMixerGroup = null;

        return seaSplashSource;
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
