using UnityEngine;

/// <summary>
/// Manages Chat UI visibility and AZKi NPC-style engagement.
///
/// Attach to the AZKi root GameObject (or any persistent scene object).
///
/// Works in two scene configurations:
///   • Beach  – AZKi uses <see cref="AzkiIslandRoamingController"/> for free roaming.
///   • Bedroom – AZKi uses <see cref="AzkiChatWaypointPatrolController"/> for standing-point patrol.
///
/// The controller auto-detects which movement component is present at Start() and
/// routes proximity-engagement calls accordingly. No scene-name checks are needed.
///
/// Behaviour (same in both scenes):
/// - While the player is outside <see cref="proximityRadius"/>, Chat UI is hidden and
///   AZKi moves normally.
/// - When the player enters the radius: Chat UI is shown, AZKi pauses, plays Idle, and
///   continuously faces the player.
/// - While in proximity AND a chat response is visible, AZKi plays Talk.
///   When the response is dismissed she returns to Idle.
/// - When the player leaves the radius: Chat UI is hidden and AZKi resumes movement.
///
/// Wire references in the Inspector:
/// - <see cref="chatUIRoot"/>        – ChatUIRoot GameObject (parent of all chat panels)
/// - <see cref="roamingController"/> – AzkiIslandRoamingController (Beach scene, auto-found if empty)
/// - <see cref="patrolController"/>  – AzkiChatWaypointPatrolController (Bedroom scene, auto-found if empty)
/// - <see cref="dialoguePanel"/>     – VRDialoguePanel in the scene
/// </summary>
[DisallowMultipleComponent]
public class AzkiProximityUIController : MonoBehaviour
{
    [Header("Chat UI")]
    [Tooltip("Root GameObject of the Chat UI to show/hide based on proximity.")]
    [SerializeField] private GameObject chatUIRoot;

    [Tooltip("Distance (meters) at which the player triggers NPC engagement.")]
    [Min(0.1f)]
    [SerializeField] private float proximityRadius = 3f;

    [Header("AZKi – Movement Controllers")]
    [Tooltip("AzkiIslandRoamingController on the AZKi root (Beach scene). Auto-found if empty.")]
    [SerializeField] private AzkiIslandRoamingController roamingController;

    [Tooltip("AzkiChatWaypointPatrolController on the AZKi root (Bedroom scene). Auto-found if empty.")]
    [SerializeField] private AzkiChatWaypointPatrolController patrolController;

    [Header("Dialogue")]
    [Tooltip("VRDialoguePanel in the scene. Auto-found if empty.")]
    [SerializeField] private VRDialoguePanel dialoguePanel;

    // ── Runtime state ────────────────────────────────────────────────────

    private bool      _playerInProximity;
    private Transform _playerTransform;
    private bool      _wasJournaling;   // tracks last journaling state so we can react on change

    // ── Unity lifecycle ──────────────────────────────────────────────────

    private void Start()
    {
        // Make sure Chat UI starts hidden.
        SetChatUIVisible(false);

        // Auto-resolve the roaming controller (Beach scene).
        if (roamingController == null)
            roamingController = GetComponentInParent<AzkiIslandRoamingController>();
        if (roamingController == null)
            roamingController = FindFirstObjectByType<AzkiIslandRoamingController>();

        // Auto-resolve the patrol controller (Bedroom scene).
        if (patrolController == null)
            patrolController = GetComponentInParent<AzkiChatWaypointPatrolController>();
        if (patrolController == null)
            patrolController = FindFirstObjectByType<AzkiChatWaypointPatrolController>();

        if (dialoguePanel == null)
            dialoguePanel = FindFirstObjectByType<VRDialoguePanel>();

        if (dialoguePanel != null)
        {
            dialoguePanel.OnAssistantResponseVisibilityChanged += OnDialogueVisibilityChanged;

            // Sync immediately if the panel is already showing something.
            if (_playerInProximity && dialoguePanel.IsAssistantResponseVisible)
                CallPlayTalkWhileEngaged();
        }
    }

    private void OnDestroy()
    {
        if (dialoguePanel != null)
            dialoguePanel.OnAssistantResponseVisibilityChanged -= OnDialogueVisibilityChanged;
    }

    private void Update()
    {
        // ── Journaling guard ────────────────────────────────────────────
        // During a journal session the proximity Chat UI must be invisible and
        // AZKi must not enter engagement mode. If the player was already engaged
        // when the session starts, we force-exit engagement and hide the UI.
        bool isJournaling = IsJournalingActive();
        if (isJournaling != _wasJournaling)
        {
            _wasJournaling = isJournaling;
            if (isJournaling && _playerInProximity)
            {
                // Session just started while player was in range — exit gracefully.
                _playerInProximity = false;
                SetChatUIVisible(false);
                roamingController?.ExitProximityEngagement();
            }
        }
        if (isJournaling) return; // suppress proximity detection entirely during journal

        // ── Normal proximity detection ──────────────────────────────────
        Transform player = ResolvePlayerTransform();
        if (player == null) return;

        float sqrDist = (transform.position - player.position).sqrMagnitude;
        bool  inRange = sqrDist <= proximityRadius * proximityRadius;

        if (inRange && !_playerInProximity)
            OnPlayerEnterProximity(player);
        else if (!inRange && _playerInProximity)
            OnPlayerExitProximity();
    }

    // ── Proximity events ─────────────────────────────────────────────────

    private void OnPlayerEnterProximity(Transform player)
    {
        _playerInProximity = true;
        SetChatUIVisible(true);
        CallEnterProximityEngagement(player);

        // If a response is already visible, go straight to Talk.
        if (dialoguePanel != null && dialoguePanel.IsAssistantResponseVisible)
            CallPlayTalkWhileEngaged();
    }

    private void OnPlayerExitProximity()
    {
        _playerInProximity = false;
        SetChatUIVisible(false);
        CallExitProximityEngagement();
    }

    // ── Dialogue panel events ────────────────────────────────────────────

    private void OnDialogueVisibilityChanged(bool isVisible)
    {
        if (!_playerInProximity) return;

        if (isVisible)
            CallPlayTalkWhileEngaged();
        else
            CallReturnToIdleWhileEngaged();
    }

    // ── Controller-agnostic routing ───────────────────────────────────────

    /// <summary>Routes to whichever controller is active in this scene.</summary>
    private void CallEnterProximityEngagement(Transform player)
    {
        if (roamingController != null)
            roamingController.EnterProximityEngagement(player);
        else if (patrolController != null)
            patrolController.EnterProximityEngagement(player);
    }

    private void CallExitProximityEngagement()
    {
        if (roamingController != null)
            roamingController.ExitProximityEngagement();
        else if (patrolController != null)
            patrolController.ExitProximityEngagement();
    }

    private void CallPlayTalkWhileEngaged()
    {
        if (roamingController != null)
            roamingController.PlayTalkWhileEngaged();
        else if (patrolController != null)
            patrolController.PlayTalkWhileEngaged();
    }

    private void CallReturnToIdleWhileEngaged()
    {
        if (roamingController != null)
            roamingController.ReturnToIdleWhileEngaged();
        else if (patrolController != null)
            patrolController.ReturnToIdleWhileEngaged();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>Returns true while a journaling or ending session is active.</summary>
    private static bool IsJournalingActive()
    {
        var mgr = JournalSessionManager.Instance;
        if (mgr == null) return false;
        return mgr.CurrentState == JournalSessionManager.SessionState.Journaling
            || mgr.CurrentState == JournalSessionManager.SessionState.Ending;
    }

    private void SetChatUIVisible(bool visible)
    {
        if (chatUIRoot != null)
            chatUIRoot.SetActive(visible);
    }

    private Transform ResolvePlayerTransform()
    {
        if (_playerTransform != null) return _playerTransform;

        Camera mainCam = Camera.main;
        if (mainCam != null)
        {
            _playerTransform = mainCam.transform;
            return _playerTransform;
        }

        Camera anyCam = FindFirstObjectByType<Camera>();
        if (anyCam != null)
        {
            _playerTransform = anyCam.transform;
            return _playerTransform;
        }

        return null;
    }

#if UNITY_EDITOR
    private void OnDrawGizmosSelected()
    {
        Gizmos.color = new Color(0.2f, 1f, 0.4f, 0.15f);
        Gizmos.DrawSphere(transform.position, proximityRadius);
        Gizmos.color = new Color(0.2f, 1f, 0.4f, 0.8f);
        Gizmos.DrawWireSphere(transform.position, proximityRadius);
    }
#endif
}
