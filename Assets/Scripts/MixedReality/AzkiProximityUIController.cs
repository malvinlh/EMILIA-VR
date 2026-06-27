using UnityEngine;

/// <summary>
/// Manages Chat UI visibility and AZKi NPC-style engagement.
///
/// Attach to the AZKi root GameObject (or any persistent scene object).
///
/// Works in two scene configurations:
///   - Beach: AZKi uses <see cref="AzkiIslandRoamingController"/> for free roaming.
///   - Bedroom: AZKi uses <see cref="AzkiChatWaypointPatrolController"/> for standing-point patrol.
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
public class AzkiProximityUIController : MonoBehaviour
{
    [Header("Chat UI")]
    [Tooltip("Root GameObject of the Chat UI to show/hide based on proximity.")]
    [SerializeField] private GameObject chatUIRoot;

    [Tooltip("Distance (meters) at which the player triggers NPC engagement.")]
    [Min(0.1f)]
    [SerializeField] private float proximityRadius = 3f;

    [Header("AZKi - Movement Controllers")]
    [Tooltip("AzkiIslandRoamingController on the AZKi root (Beach scene). Auto-found if empty.")]
    [SerializeField] private AzkiIslandRoamingController roamingController;

    [Tooltip("AzkiChatWaypointPatrolController on the AZKi root (Bedroom scene). Auto-found if empty.")]
    [SerializeField] private AzkiChatWaypointPatrolController patrolController;

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
            roamingController = GetComponentInParent<AzkiIslandRoamingController>();
        if (roamingController == null)
            roamingController = FindFirstObjectByType<AzkiIslandRoamingController>();

        if (patrolController == null)
            patrolController = GetComponentInParent<AzkiChatWaypointPatrolController>();
        if (patrolController == null)
            patrolController = FindFirstObjectByType<AzkiChatWaypointPatrolController>();

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

        // Horizontal (XZ) distance only — the player's head height must not affect "is the player near
        // the NPC". This matters in scaled scenes (e.g. Bedroom is ~2.25x): the head can sit several
        // metres above the avatar's base, and a full 3D distance would never fall within the radius.
        Vector3 planar = transform.position - player.position;
        planar.y = 0f;
        bool inRange = planar.sqrMagnitude <= proximityRadius * proximityRadius;

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
        // Always prefer the live head camera. Re-check every call instead of caching the first
        // result permanently: on a Quest build the XR head camera (MainCamera) comes up after
        // MR/passthrough init, so an early call would otherwise lock onto a wrong fallback camera
        // for the whole session (proximity then measures distance to the wrong camera and never
        // triggers). The Editor/XR Device Simulator has Camera.main ready immediately, which is
        // why this only manifested in the device build.
        Camera mainCam = Camera.main;
        if (mainCam != null)
        {
            _playerTransform = mainCam.transform;
            return _playerTransform;
        }

        // Head camera not ready yet: keep the last known-good transform if we have one.
        if (_playerTransform != null)
            return _playerTransform;

        // Transient fallback only — do NOT cache, so we still upgrade to Camera.main once it exists.
        Camera anyCam = FindFirstObjectByType<Camera>();
        return anyCam != null ? anyCam.transform : null;
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
