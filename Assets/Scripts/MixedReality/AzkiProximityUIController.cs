using UnityEngine;

/// <summary>
/// Manages Chat UI visibility and AZKi NPC-style engagement in the Beach scene.
///
/// Attach to the AZKi root GameObject (or any persistent scene object).
///
/// Behaviour:
/// - While the player is outside <see cref="proximityRadius"/>, the Chat UI is hidden and
///   AZKi roams freely.
/// - When the player enters the radius: the Chat UI is shown, AZKi pauses, plays Idle, and
///   continuously faces the player.
/// - While in proximity AND a chat response is visible in the dialogue panel, AZKi plays her
///   Talk animation. When the response is dismissed she returns to Idle.
/// - When the player leaves the radius: the Chat UI is hidden and AZKi resumes roaming.
///
/// Wire references in the Inspector:
/// - <see cref="chatUIRoot"/>      – ChatUIRoot GameObject (parent of all chat panels)
/// - <see cref="roamingController"/> – AzkiIslandRoamingController on the AZKi root
/// - <see cref="dialoguePanel"/>   – VRDialoguePanel in the scene
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

    [Header("AZKi")]
    [Tooltip("AzkiIslandRoamingController on the AZKi root. Auto-found if empty.")]
    [SerializeField] private AzkiIslandRoamingController roamingController;

    [Header("Dialogue")]
    [Tooltip("VRDialoguePanel in the scene. Auto-found if empty.")]
    [SerializeField] private VRDialoguePanel dialoguePanel;

    // ── Runtime state ────────────────────────────────────────────────────

    private bool      _playerInProximity;
    private Transform _playerTransform;

    // ── Unity lifecycle ──────────────────────────────────────────────────

    private void Start()
    {
        // Make sure Chat UI starts hidden.
        SetChatUIVisible(false);

        // Auto-resolve references.
        if (roamingController == null)
            roamingController = GetComponentInParent<AzkiIslandRoamingController>();
        if (roamingController == null)
            roamingController = FindFirstObjectByType<AzkiIslandRoamingController>();

        if (dialoguePanel == null)
            dialoguePanel = FindFirstObjectByType<VRDialoguePanel>();

        if (dialoguePanel != null)
        {
            dialoguePanel.OnAssistantResponseVisibilityChanged += OnDialogueVisibilityChanged;

            // Sync immediately if the panel is already showing something.
            if (_playerInProximity && dialoguePanel.IsAssistantResponseVisible)
                roamingController?.PlayTalkWhileEngaged();
        }
    }

    private void OnDestroy()
    {
        if (dialoguePanel != null)
            dialoguePanel.OnAssistantResponseVisibilityChanged -= OnDialogueVisibilityChanged;
    }

    private void Update()
    {
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
        roamingController?.EnterProximityEngagement(player);

        // If a response is already visible, go straight to Talk.
        if (dialoguePanel != null && dialoguePanel.IsAssistantResponseVisible)
            roamingController?.PlayTalkWhileEngaged();
    }

    private void OnPlayerExitProximity()
    {
        _playerInProximity = false;
        SetChatUIVisible(false);
        roamingController?.ExitProximityEngagement();
    }

    // ── Dialogue panel events ────────────────────────────────────────────

    private void OnDialogueVisibilityChanged(bool isVisible)
    {
        if (!_playerInProximity || roamingController == null) return;

        if (isVisible)
            roamingController.PlayTalkWhileEngaged();
        else
            roamingController.ReturnToIdleWhileEngaged();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

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
