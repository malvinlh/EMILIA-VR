using UnityEngine;

/// <summary>
/// Manages Chat UI visibility and AZKi NPC-style engagement.
///
/// Attach to the AZKi root GameObject (or any persistent scene object).
///
/// Works in two scene configurations:
///   - Beach: AZKi uses <see cref="CompanionIslandRoamingController"/> for free roaming.
///   - Bedroom: AZKi uses <see cref="CompanionChatWaypointPatrolController"/> for standing-point patrol.
///
/// The controller auto-detects which movement component is present at Start() and
/// routes proximity-engagement calls accordingly. No scene-name checks are needed.
///
/// Behaviour (same in both scenes):
/// - While the player is outside <see cref="proximityRadius"/>, Chat UI is hidden and
///   AZKi moves normally.
/// - When the player enters the radius: Chat UI is shown, AZKi pauses, plays Idle, and
///   continuously faces the player.
/// - When the player leaves the radius: Chat UI is hidden and AZKi resumes movement.
///
/// Dialogue-driven Talk and waiting-idle behavior are owned by the movement controllers.
/// </summary>
[DisallowMultipleComponent]
public class CompanionProximityUIController : MonoBehaviour
{
    [Header("Chat UI")]
    [Tooltip("Root GameObject of the Chat UI to show/hide based on proximity.")]
    [SerializeField] private GameObject chatUIRoot;

    [Tooltip("Distance (meters) at which the player triggers NPC engagement.")]
    [Min(0.1f)]
    [SerializeField] private float proximityRadius = 3f;

    [Header("AZKi - Movement Controllers")]
    [Tooltip("CompanionIslandRoamingController on the AZKi root (Beach scene). Auto-found if empty.")]
    [SerializeField] private CompanionIslandRoamingController roamingController;

    [Tooltip("CompanionChatWaypointPatrolController on the AZKi root (Bedroom scene). Auto-found if empty.")]
    [SerializeField] private CompanionChatWaypointPatrolController patrolController;

    [Header("Dialogue")]
    [Tooltip("VRDialoguePanel in the scene. Kept for scene compatibility.")]
    [SerializeField] private VRDialoguePanel dialoguePanel;

    private bool _playerInProximity;
    private Transform _playerTransform;
    private bool _wasJournaling;

    private void Start()
    {
        SetChatUIVisible(false);

        if (roamingController == null)
            roamingController = GetComponentInParent<CompanionIslandRoamingController>();
        if (roamingController == null)
            roamingController = FindFirstObjectByType<CompanionIslandRoamingController>();

        if (patrolController == null)
            patrolController = GetComponentInParent<CompanionChatWaypointPatrolController>();
        if (patrolController == null)
            patrolController = FindFirstObjectByType<CompanionChatWaypointPatrolController>();

        if (dialoguePanel == null)
            dialoguePanel = FindFirstObjectByType<VRDialoguePanel>();
    }

    private void Update()
    {
        bool isJournaling = IsJournalingActive();
        if (isJournaling != _wasJournaling)
        {
            _wasJournaling = isJournaling;
            if (isJournaling && _playerInProximity)
            {
                _playerInProximity = false;
                SetChatUIVisible(false);
                CallExitProximityEngagement();
            }
        }

        if (isJournaling)
            return;

        Transform player = ResolvePlayerTransform();
        if (player == null)
            return;

        float sqrDist = (transform.position - player.position).sqrMagnitude;
        bool inRange = sqrDist <= proximityRadius * proximityRadius;

        if (inRange && !_playerInProximity)
            OnPlayerEnterProximity(player);
        else if (!inRange && _playerInProximity)
            OnPlayerExitProximity();
    }

    private void OnPlayerEnterProximity(Transform player)
    {
        _playerInProximity = true;
        SetChatUIVisible(true);
        CallEnterProximityEngagement(player);
    }

    private void OnPlayerExitProximity()
    {
        _playerInProximity = false;
        SetChatUIVisible(false);
        CallExitProximityEngagement();
    }

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

    private static bool IsJournalingActive()
    {
        var mgr = JournalSessionManager.Instance;
        if (mgr == null)
            return false;

        return mgr.CurrentState == JournalSessionManager.SessionState.Journaling ||
               mgr.CurrentState == JournalSessionManager.SessionState.Ending;
    }

    private void SetChatUIVisible(bool visible)
    {
        if (chatUIRoot != null)
            chatUIRoot.SetActive(visible);
    }

    private Transform ResolvePlayerTransform()
    {
        if (_playerTransform != null)
            return _playerTransform;

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
