using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Roaming controller for AZKi.
///
/// - Captures the authored scene pose once and can lock back to it for review moments.
/// - Builds/uses a NavMeshSurface at runtime and drives random patrol movement.
/// - Enforces patrol animation control (Idle, IdleB, Walk) plus review overrides (Talk, Cheering).
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NavMeshAgent))]
[RequireComponent(typeof(Animator))]
public class CompanionIslandRoamingController : MonoBehaviour
{
    [Header("Navigation")]
    [Tooltip("Optional NavMeshSurface. If empty, one is auto-found or created at runtime.")]
    [SerializeField] private NavMeshSurface navMeshSurface;

    [Tooltip("Radius (meters) from the authored AZKi position used for patrol destinations.")]
    [Min(0.5f)]
    [SerializeField] private float patrolRadius = 100f;

    [Tooltip("How close AZKi must be to consider a destination reached.")]
    [Min(0.05f)]
    [SerializeField] private float destinationReachedDistance = 0.45f;

    [Tooltip("Minimum walking distance for a new random destination.")]
    [Min(0.1f)]
    [SerializeField] private float minPatrolTravelDistance = 1.8f;

    [Tooltip("Sample radius used when projecting random points onto NavMesh.")]
    [Min(0.1f)]
    [SerializeField] private float navMeshSampleDistance = 4f;

    [Tooltip("Attempts before giving up finding a random patrol point this cycle.")]
    [Range(1, 64)]
    [SerializeField] private int maxPatrolPointAttempts = 12;

    [Tooltip("Runtime fallback only. Keep disabled when scene navmesh is pre-baked.")]
    [SerializeField] private bool buildNavMeshOnStart = false;

    [Tooltip("Skip runtime build when a usable NavMesh is already available near AZKi.")]
    [SerializeField] private bool skipRuntimeBuildIfNavMeshPresent = true;

    [Tooltip("Geometry source for runtime build fallback. PhysicsColliders avoids mesh read-access warnings in player builds.")]
    [SerializeField] private NavMeshCollectGeometry runtimeBuildGeometry = NavMeshCollectGeometry.PhysicsColliders;

    [Header("Idle Timing")]
    [Tooltip("Minimum idle duration between patrol points.")]
    [Min(0f)]
    [SerializeField] private float minIdleDuration = 1.8f;

    [Tooltip("Maximum idle duration between patrol points.")]
    [Min(0f)]
    [SerializeField] private float maxIdleDuration = 4.2f;

    [Header("IdleB Frequency")]
    [Tooltip("Chance to play IdleB when an idle window starts and cooldown is satisfied.")]
    [Range(0f, 1f)]
    [SerializeField] private float idleBChancePerIdleWindow = 0.3f;

    [Tooltip("Minimum number of idle windows between IdleB plays.")]
    [Min(0)]
    [SerializeField] private int minIdleWindowsBetweenIdleB = 2;

    [Header("Waiting Idle Timing")]
    [Min(0f)]
    [SerializeField] private float minWaitingBaseIdleDuration = 0.9f;

    [Min(0f)]
    [SerializeField] private float maxWaitingBaseIdleDuration = 1.8f;

    [Header("Animation")]
    [Tooltip("Animator state path for patrol idle.")]
    [SerializeField] private string idleStateName = "Base Layer.Idle";

    [Tooltip("Animator state path for alternate patrol idle.")]
    [SerializeField] private string idleBStateName = "Base Layer.IdleB";

    [Tooltip("Animator state path for alternate patrol idle C.")]
    [SerializeField] private string idleCStateName = "Base Layer.IdleC";

    [Tooltip("Animator state path for alternate patrol idle D.")]
    [SerializeField] private string idleDStateName = "Base Layer.IdleD";

    [Tooltip("Animator state path for patrol walk.")]
    [SerializeField] private string walkStateName = "Base Layer.Walk";

    [Tooltip("Animator state path for review talking animation.")]
    [SerializeField] private string talkStateName = "Base Layer.Talk";

    [Tooltip("Animator state path for ending cheering animation.")]
    [SerializeField] private string cheeringStateName = "Base Layer.Cheering";

    [Tooltip("Normalized time used when freezing the final cheering pose (1.0 = end of clip).")]
    [Range(0.9f, 1f)]
    [SerializeField] private float cheeringHoldNormalizedTime = 0.995f;

    [Tooltip("Cross-fade duration used when changing patrol animation state.")]
    [Range(0f, 0.5f)]
    [SerializeField] private float stateCrossFadeSeconds = 0.12f;

    [Tooltip("Minimum hold time before switching to another patrol animation state.")]
    [Range(0f, 2f)]
    [SerializeField] private float minStateHoldSeconds = 0.5f;

    [Tooltip("Velocity threshold used to classify walk vs idle.")]
    [Range(0.01f, 2f)]
    [SerializeField] private float movingVelocityThreshold = 0.12f;

    [Header("Proximity Engagement")]
    [Tooltip("Rotation speed (deg/s) when turning to face the player during NPC engagement.")]
    [Min(1f)]
    [SerializeField] private float proximityTurnSpeed = 540f;

    private enum PatrolAnimation
    {
        Idle,
        IdleB,
        IdleC,
        IdleD,
        Walk,
        Talk,
        Cheering
    }

    private enum AnimationOverrideMode
    {
        None,
        TalkLoop,
        CheeringOnce
    }

    private NavMeshAgent _agent;
    private Animator _animator;
    private CompanionBlendShapeExpressionController _blendShapeController;
    private NpcCharacterControllerGravity _gravityController;
    private NavMeshPath _pathBuffer;

    private Vector3 _authoredPosition;
    private Quaternion _authoredRotation;
    private bool _authoredPoseCaptured;

    private bool _navMeshBuilt;
    private bool _loggedMissingNavMesh;
    private bool _isLocked;
    private bool _isIdling;
    private float _idleUntilTime;
    private PatrolAnimation _currentIdleWindowAnimation = PatrolAnimation.Idle;
    private int _idleWindowsSinceAlternateIdle = int.MaxValue;
    private bool _isWaitingForResponse;
    private bool _talkActive;
    private PatrolAnimation _currentWaitingIdleAnimation = PatrolAnimation.Idle;
    private float _waitingIdleUntilTime;

    private PatrolAnimation _currentPatrolAnimation = PatrolAnimation.Idle;
    private float _nextAnimSwitchTime;

    private AnimationOverrideMode _overrideMode = AnimationOverrideMode.None;
    private bool _cheeringFinished;
    private bool _isCheeringPoseHeld;
    private float _defaultAnimatorSpeed = 1f;

    // Proximity-engagement state (NPC idle-and-face-player mode)
    private bool _isInProximityEngagement;
    private Transform _engagementTarget;
    private Transform _cachedPlayerTransform;

    private VRDialoguePanel _dialoguePanel;

    private int _idleHash;
    private int _idleBHash;
    private int _idleCHash;
    private int _idleDHash;
    private int _walkHash;
    private int _talkHash;
    private int _cheeringHash;
    private int _idleShortHash;
    private int _idleBShortHash;
    private int _idleCShortHash;
    private int _idleDShortHash;
    private int _walkShortHash;
    private int _talkShortHash;
    private int _cheeringShortHash;

    private void Awake()
    {
        _agent = GetComponent<NavMeshAgent>();
        _animator = GetComponent<Animator>();
        EnsureBlendShapeController();
        _gravityController = GetComponent<NpcCharacterControllerGravity>();
        _pathBuffer = new NavMeshPath();

        CaptureAuthoredPose();
        CacheAnimatorStateHashes();

        if (_animator != null)
        {
            _animator.applyRootMotion = false;
            _defaultAnimatorSpeed = Mathf.Approximately(_animator.speed, 0f) ? 1f : _animator.speed;
        }
    }

    private void Start()
    {
        if (maxIdleDuration < minIdleDuration)
            maxIdleDuration = minIdleDuration;
        if (maxWaitingBaseIdleDuration < minWaitingBaseIdleDuration)
            maxWaitingBaseIdleDuration = minWaitingBaseIdleDuration;

        ResumeRoaming();
    }

    private void Update()
    {
        if (_isLocked)
        {
            HandleLockedMode();
            return;
        }

        SyncDialogueStateFromPanel();

        if (_isWaitingForResponse)
        {
            HandleWaitingMode();
            return;
        }

        if (_talkActive)
        {
            HandleTalkMode();
            return;
        }

        if (_isInProximityEngagement)
        {
            HandleProximityEngagement();
            return;
        }

        if (_agent == null || !_agent.enabled)
            return;

        if (!_agent.isOnNavMesh)
            return;

        bool isWalking = _agent.velocity.sqrMagnitude >= movingVelocityThreshold * movingVelocityThreshold;

        if (_isIdling)
        {
            if (IsAlternateIdleState(_currentIdleWindowAnimation))
            {
                if (!HasCompletedIdleVariantPlayback(_currentIdleWindowAnimation))
                {
                    EnforceAnimation(_currentIdleWindowAnimation, force: false);
                    return;
                }

                _currentIdleWindowAnimation = PatrolAnimation.Idle;
                EnforceAnimation(PatrolAnimation.Idle, force: true);
            }
            else
            {
                EnforceAnimation(_currentIdleWindowAnimation, force: false);
            }

            if (Time.time >= _idleUntilTime)
            {
                _isIdling = false;
                SetNextDestination();
            }
            return;
        }

        if (isWalking)
        {
            EnforceAnimation(PatrolAnimation.Walk, force: false);
            return;
        }

        if (!_agent.pathPending && _agent.remainingDistance <= destinationReachedDistance)
            BeginIdleWindow();
    }

    /// <summary>
    /// Freezes AZKi at the authored scene pose and keeps patrol animation idle.
    /// </summary>
    public void LockAtAuthoredPose()
    {
        InitializeIfNeeded();
        ReleaseCheeringPoseHold();

        _isLocked = true;
        _isIdling = false;
        _talkActive = false;
        _isWaitingForResponse = false;
        _overrideMode = AnimationOverrideMode.None;
        _cheeringFinished = false;
        _currentIdleWindowAnimation = PatrolAnimation.Idle;
        ResetWaitingIdleCycle();

        if (_gravityController != null)
            _gravityController.enabled = false;

        if (_agent != null)
        {
            if (!_agent.enabled)
                _agent.enabled = true;

            if (_agent.isOnNavMesh)
            {
                _agent.isStopped = true;
                _agent.ResetPath();
                _agent.velocity = Vector3.zero;
                _agent.Warp(_authoredPosition);
            }
            else
            {
                transform.position = _authoredPosition;
            }

            _agent.updateRotation = false;
        }

        transform.rotation = _authoredRotation;
        EnforceAnimation(PatrolAnimation.Idle, force: true);
    }

    /// <summary>
    /// Plays the Talk animation in a forced loop while AZKi remains locked at authored pose.
    /// </summary>
    public void PlayTalkLoop()
    {
        LockAtAuthoredPose();
        _overrideMode = AnimationOverrideMode.TalkLoop;
        EnforceAnimation(PatrolAnimation.Talk, force: true);
    }

    /// <summary>
    /// Plays the Cheering animation once while AZKi remains locked at authored pose.
    /// </summary>
    public void PlayCheeringOneShot()
    {
        LockAtAuthoredPose();
        ReleaseCheeringPoseHold();
        _overrideMode = AnimationOverrideMode.CheeringOnce;
        _cheeringFinished = false;
        EnforceAnimation(PatrolAnimation.Cheering, force: true);
    }

    /// <summary>
    /// True once the cheering animation has played fully and AZKi is holding the final pose.
    /// Poll this to know when it is safe to resume roaming after a cheering one-shot.
    /// </summary>
    public bool IsCheeringPoseHeld => _isCheeringPoseHeld;

    /// <summary>
    /// Enables roaming behavior. If needed, builds a runtime NavMesh first.
    /// </summary>
    public void ResumeRoaming()
    {
        InitializeIfNeeded();
        ReleaseCheeringPoseHold();

        _isLocked = false;
        _talkActive = false;
        _isWaitingForResponse = false;
        _overrideMode = AnimationOverrideMode.None;
        _cheeringFinished = false;
        ResetWaitingIdleCycle();

        if (_gravityController != null)
            _gravityController.enabled = false;

        if (_agent == null)
            return;

        if (!_agent.enabled)
            _agent.enabled = true;

        if (buildNavMeshOnStart && !_navMeshBuilt)
            BuildRuntimeNavMeshIfNeeded();

        if (!EnsureAgentOnNavMesh())
        {
            if (!_loggedMissingNavMesh)
            {
                _loggedMissingNavMesh = true;
                Debug.LogWarning("[CompanionIslandRoamingController] No NavMesh found for AZKi. Bake NavMesh for the scene or enable runtime build fallback.", this);
            }

            EnforceAnimation(PatrolAnimation.Idle, force: true);
            return;
        }

        _agent.isStopped = false;
        _agent.updateRotation = true;
        _isIdling = false;
        _currentIdleWindowAnimation = PatrolAnimation.Idle;

        SetNextDestination();
    }

    // ── Proximity Engagement ──────────────────────────────────────────────

    /// <summary>
    /// Pauses roaming at the current position, plays Idle, and continuously
    /// faces <paramref name="player"/> until <see cref="ExitProximityEngagement"/> is called.
    /// </summary>
    public void EnterProximityEngagement(Transform player)
    {
        InitializeIfNeeded();
        ReleaseCheeringPoseHold();

        _isInProximityEngagement = true;
        _isLocked  = false;
        _overrideMode = AnimationOverrideMode.None;
        _engagementTarget = player;

        if (_gravityController != null)
            _gravityController.enabled = false;

        if (_agent != null)
        {
            if (!_agent.enabled) _agent.enabled = true;
            if (!_talkActive && !_isWaitingForResponse)
                StopAgentMovementForDialogue();
        }

        if (!_talkActive && !_isWaitingForResponse)
            EnforceAnimation(PatrolAnimation.Idle, force: true);
    }

    /// <summary>
    /// Exits proximity engagement and resumes normal roaming.
    /// </summary>
    public void ExitProximityEngagement()
    {
        _isInProximityEngagement = false;
        _engagementTarget = null;

        if (_talkActive || _isWaitingForResponse)
            return;

        _overrideMode = AnimationOverrideMode.None;
        ResumeRoaming();
    }

    /// <summary>
    /// While in proximity engagement, switches AZKi to Talk loop
    /// (e.g. when a chat response is displayed).
    /// </summary>
    public void PlayTalkWhileEngaged()
    {
        if (!_isInProximityEngagement || _talkActive || _isWaitingForResponse) return;
        EnforceAnimation(PatrolAnimation.Talk, force: true);
    }

    /// <summary>
    /// While in proximity engagement, returns AZKi to Idle
    /// (e.g. when dialogue is dismissed).
    /// </summary>
    public void ReturnToIdleWhileEngaged()
    {
        if (!_isInProximityEngagement || _talkActive || _isWaitingForResponse) return;
        EnforceAnimation(PatrolAnimation.Idle, force: true);
    }

    /// <summary>
    /// Wires the dialogue panel so Talk/Idle follow response visibility directly,
    /// matching the bedroom-scene pattern in CompanionChatWaypointPatrolController.
    /// </summary>
    public void SetDialoguePanel(VRDialoguePanel panel)
    {
        if (_dialoguePanel != null)
        {
            _dialoguePanel.OnAssistantResponseVisibilityChanged -= OnDialogueVisibilityChanged;
            _dialoguePanel.OnTypingIndicatorVisibilityChanged -= OnTypingIndicatorVisibilityChanged;
        }

        _dialoguePanel = panel;

        if (_dialoguePanel != null)
        {
            _dialoguePanel.OnAssistantResponseVisibilityChanged += OnDialogueVisibilityChanged;
            _dialoguePanel.OnTypingIndicatorVisibilityChanged += OnTypingIndicatorVisibilityChanged;
        }

        SyncDialogueStateFromPanel();
    }

    private void OnDialogueVisibilityChanged(bool isVisible)
    {
        SyncDialogueStateFromPanel();
    }

    private void OnTypingIndicatorVisibilityChanged(bool isVisible)
    {
        SyncDialogueStateFromPanel();
    }

    private void OnDestroy()
    {
        if (_dialoguePanel != null)
        {
            _dialoguePanel.OnAssistantResponseVisibilityChanged -= OnDialogueVisibilityChanged;
            _dialoguePanel.OnTypingIndicatorVisibilityChanged -= OnTypingIndicatorVisibilityChanged;
        }
    }

    private void SyncDialogueStateFromPanel()
    {
        bool waitingForResponse = _dialoguePanel != null && _dialoguePanel.IsTypingIndicatorVisible;
        bool talkActive = !waitingForResponse &&
                          _dialoguePanel != null &&
                          _dialoguePanel.IsAssistantResponseVisible;

        if (_isWaitingForResponse != waitingForResponse)
        {
            _isWaitingForResponse = waitingForResponse;
            if (_isWaitingForResponse)
                EnterWaitingState();
            else
                ExitWaitingState(enteringTalk: talkActive);
        }

        if (_talkActive != talkActive)
        {
            _talkActive = talkActive;
            if (_talkActive)
                EnterTalkState();
            else
                ExitTalkState(enteringWaiting: _isWaitingForResponse);
        }
    }

    private void EnterTalkState()
    {
        StopAgentMovementForDialogue();
        _isIdling = false;
        _currentIdleWindowAnimation = PatrolAnimation.Idle;
        ResetWaitingIdleCycle();
        EnforceAnimation(PatrolAnimation.Talk, force: true);
    }

    private void ExitTalkState(bool enteringWaiting)
    {
        if (enteringWaiting || _isLocked)
            return;

        if (_isInProximityEngagement)
        {
            EnforceAnimation(PatrolAnimation.Idle, force: true);
            return;
        }

        ResumeRoaming();
    }

    private void EnterWaitingState()
    {
        StopAgentMovementForDialogue();
        _isIdling = false;
        _currentIdleWindowAnimation = PatrolAnimation.Idle;
        ResetWaitingIdleCycle();
        StartWaitingIdleCycle(force: true);
    }

    private void ExitWaitingState(bool enteringTalk)
    {
        ResetWaitingIdleCycle();

        if (enteringTalk || _isLocked)
            return;

        if (_isInProximityEngagement)
        {
            EnforceAnimation(PatrolAnimation.Idle, force: true);
            return;
        }

        ResumeRoaming();
    }

    private void HandleTalkMode()
    {
        StopAgentMovementForDialogue();
        RotateTowardsPlayer(proximityTurnSpeed);
        EnforceAnimation(PatrolAnimation.Talk, force: false);

        if (_animator == null || _animator.IsInTransition(0))
            return;

        var info = _animator.GetCurrentAnimatorStateInfo(0);
        if (StateMatches(info, _talkHash, _talkShortHash) && info.normalizedTime >= 1f)
            _animator.Play(_talkHash, 0, 0f);
    }

    private void HandleWaitingMode()
    {
        StopAgentMovementForDialogue();

        if (IsAlternateIdleState(_currentWaitingIdleAnimation))
        {
            if (!HasCompletedIdleVariantPlayback(_currentWaitingIdleAnimation))
            {
                EnforceAnimation(_currentWaitingIdleAnimation, force: false);
                return;
            }

            StartWaitingBaseIdleDwell(force: true);
            return;
        }

        EnforceAnimation(PatrolAnimation.Idle, force: false);

        if (Time.time >= _waitingIdleUntilTime)
            StartWaitingIdleCycle(force: false);
    }

    private void HandleProximityEngagement()
    {
        StopAgentMovementForDialogue();

        // Continuously face the player.
        if (_engagementTarget != null)
        {
            Vector3 toTarget = _engagementTarget.position - transform.position;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude > 0.0001f)
            {
                Quaternion targetRot = Quaternion.LookRotation(toTarget.normalized, Vector3.up);
                transform.rotation = Quaternion.RotateTowards(
                    transform.rotation, targetRot, proximityTurnSpeed * Time.deltaTime);
            }
        }

        EnforceAnimation(PatrolAnimation.Idle, force: false);
    }

    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Captures the scene-authored AZKi pose used during review standby.
    /// </summary>
    public void CaptureAuthoredPose()
    {
        _authoredPosition = transform.position;
        _authoredRotation = transform.rotation;
        _authoredPoseCaptured = true;
    }

    private void InitializeIfNeeded()
    {
        if (_agent == null)
            _agent = GetComponent<NavMeshAgent>();
        if (_animator == null)
            _animator = GetComponent<Animator>();
        EnsureBlendShapeController();
        if (_gravityController == null)
            _gravityController = GetComponent<NpcCharacterControllerGravity>();
        if (_pathBuffer == null)
            _pathBuffer = new NavMeshPath();

        if (!_authoredPoseCaptured)
            CaptureAuthoredPose();

        if (_idleHash == 0 || _idleBHash == 0 || _walkHash == 0 || _talkHash == 0 || _cheeringHash == 0)
            CacheAnimatorStateHashes();
    }

    private void EnsureBlendShapeController()
    {
        if (_blendShapeController == null)
            _blendShapeController = GetComponent<CompanionBlendShapeExpressionController>();

        if (_blendShapeController == null)
            _blendShapeController = gameObject.AddComponent<CompanionBlendShapeExpressionController>();
    }

    private void CacheAnimatorStateHashes()
    {
        _idleHash = Animator.StringToHash(idleStateName);
        _idleBHash = Animator.StringToHash(idleBStateName);
        _idleCHash = Animator.StringToHash(idleCStateName);
        _idleDHash = Animator.StringToHash(idleDStateName);
        _walkHash = Animator.StringToHash(walkStateName);
        _talkHash = Animator.StringToHash(talkStateName);
        _cheeringHash = Animator.StringToHash(cheeringStateName);

        _idleShortHash = HashShortStateName(idleStateName);
        _idleBShortHash = HashShortStateName(idleBStateName);
        _idleCShortHash = HashShortStateName(idleCStateName);
        _idleDShortHash = HashShortStateName(idleDStateName);
        _walkShortHash = HashShortStateName(walkStateName);
        _talkShortHash = HashShortStateName(talkStateName);
        _cheeringShortHash = HashShortStateName(cheeringStateName);
    }

    private void BuildRuntimeNavMesh()
    {
        if (navMeshSurface == null)
            navMeshSurface = FindFirstObjectByType<NavMeshSurface>();

        if (navMeshSurface == null)
        {
            var surfaceGo = new GameObject("__AZKi_NavMeshSurfaceRuntime");
            navMeshSurface = surfaceGo.AddComponent<NavMeshSurface>();
            navMeshSurface.collectObjects = CollectObjects.All;
        }

        navMeshSurface.useGeometry = runtimeBuildGeometry;

        navMeshSurface.BuildNavMesh();
        _navMeshBuilt = true;
    }

    private void BuildRuntimeNavMeshIfNeeded()
    {
        if (skipRuntimeBuildIfNavMeshPresent && IsNavMeshAvailable())
            return;

        BuildRuntimeNavMesh();
    }

    private bool IsNavMeshAvailable()
    {
        if (NavMesh.SamplePosition(transform.position, out _, navMeshSampleDistance, NavMesh.AllAreas))
            return true;

        return NavMesh.SamplePosition(_authoredPosition, out _, navMeshSampleDistance * 2f, NavMesh.AllAreas);
    }

    private bool EnsureAgentOnNavMesh()
    {
        if (_agent == null)
            return false;

        if (_agent.isOnNavMesh)
            return true;

        if (NavMesh.SamplePosition(transform.position, out NavMeshHit hit, navMeshSampleDistance, NavMesh.AllAreas))
        {
            _agent.Warp(hit.position);
            return true;
        }

        if (NavMesh.SamplePosition(_authoredPosition, out hit, navMeshSampleDistance * 2f, NavMesh.AllAreas))
        {
            _agent.Warp(hit.position);
            return true;
        }

        return false;
    }

    private void StopAgentMovementForDialogue()
    {
        if (_agent == null || !_agent.enabled)
            return;

        if (_agent.isOnNavMesh)
        {
            _agent.isStopped = true;
            _agent.ResetPath();
            _agent.velocity = Vector3.zero;
        }

        _agent.updateRotation = false;
    }

    private bool RotateTowardsPlayer(float turnSpeedDegPerSec)
    {
        Transform player = ResolvePlayerTransform();
        if (player == null)
            return false;

        Vector3 toPlayer = player.position - transform.position;
        toPlayer.y = 0f;

        if (toPlayer.sqrMagnitude < 0.0001f)
            return true;

        Quaternion targetRotation = Quaternion.LookRotation(toPlayer.normalized, Vector3.up);
        transform.rotation = Quaternion.RotateTowards(
            transform.rotation,
            targetRotation,
            turnSpeedDegPerSec * Time.deltaTime
        );

        return true;
    }

    private Transform ResolvePlayerTransform()
    {
        if (_cachedPlayerTransform != null)
            return _cachedPlayerTransform;

        Camera mainCam = Camera.main;
        if (mainCam != null)
        {
            _cachedPlayerTransform = mainCam.transform;
            return _cachedPlayerTransform;
        }

        Camera anyCam = FindFirstObjectByType<Camera>();
        if (anyCam != null)
        {
            _cachedPlayerTransform = anyCam.transform;
            return _cachedPlayerTransform;
        }

        return null;
    }

    private void SetNextDestination()
    {
        if (_agent == null || !_agent.enabled || !_agent.isOnNavMesh)
            return;

        if (!TryGetPatrolDestination(out Vector3 destination))
        {
            BeginIdleWindow();
            return;
        }

        _agent.SetDestination(destination);
        EnforceAnimation(PatrolAnimation.Walk, force: true);
    }

    private bool TryGetPatrolDestination(out Vector3 destination)
    {
        destination = transform.position;

        for (int i = 0; i < maxPatrolPointAttempts; i++)
        {
            Vector2 randomOffset = Random.insideUnitCircle * patrolRadius;
            Vector3 candidate = _authoredPosition + new Vector3(randomOffset.x, 0f, randomOffset.y);

            if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, navMeshSampleDistance, NavMesh.AllAreas))
                continue;

            float travelDistance = Vector3.Distance(transform.position, hit.position);
            if (travelDistance < minPatrolTravelDistance)
                continue;

            if (!_agent.CalculatePath(hit.position, _pathBuffer))
                continue;

            if (_pathBuffer.status != NavMeshPathStatus.PathComplete)
                continue;

            destination = hit.position;
            return true;
        }

        return false;
    }

    private void BeginIdleWindow()
    {
        _isIdling = true;
        _currentIdleWindowAnimation = PatrolAnimation.Idle;

        if (_agent != null)
            _agent.ResetPath();

        _idleUntilTime = Time.time + Random.Range(minIdleDuration, maxIdleDuration);

        _currentIdleWindowAnimation = SelectIdleWindowAnimation();
        EnforceAnimation(_currentIdleWindowAnimation, force: true);
    }

    private PatrolAnimation SelectIdleWindowAnimation()
    {
        if (_animator == null)
            return PatrolAnimation.Idle;

        int availableAlternateCount = CountAvailableAlternateIdleStates();
        if (!_animator.HasState(0, _idleHash) || availableAlternateCount == 0)
        {
            IncrementAlternateIdleCooldown();
            return PatrolAnimation.Idle;
        }

        if (_idleWindowsSinceAlternateIdle < minIdleWindowsBetweenIdleB ||
            Random.value > idleBChancePerIdleWindow)
        {
            IncrementAlternateIdleCooldown();
            return PatrolAnimation.Idle;
        }

        _idleWindowsSinceAlternateIdle = 0;
        return GetAvailableAlternateIdleByOrdinal(Random.Range(0, availableAlternateCount));
    }

    private void ResetWaitingIdleCycle()
    {
        _currentWaitingIdleAnimation = PatrolAnimation.Idle;
        _waitingIdleUntilTime = 0f;
    }

    private void StartWaitingIdleCycle(bool force)
    {
        _currentWaitingIdleAnimation = SelectWaitingIdleAnimation();

        if (_currentWaitingIdleAnimation == PatrolAnimation.Idle)
        {
            _waitingIdleUntilTime = Time.time + GetWaitingBaseIdleDuration();
            EnforceAnimation(PatrolAnimation.Idle, force);
            return;
        }

        _waitingIdleUntilTime = 0f;
        EnforceAnimation(_currentWaitingIdleAnimation, force: true);
    }

    private void StartWaitingBaseIdleDwell(bool force)
    {
        _currentWaitingIdleAnimation = PatrolAnimation.Idle;
        _waitingIdleUntilTime = Time.time + GetWaitingBaseIdleDuration();
        EnforceAnimation(PatrolAnimation.Idle, force);
    }

    private PatrolAnimation SelectWaitingIdleAnimation()
    {
        int availableAlternateCount = CountAvailableAlternateIdleStates();
        int choice = Random.Range(0, availableAlternateCount + 1);
        return choice == 0
            ? PatrolAnimation.Idle
            : GetAvailableAlternateIdleByOrdinal(choice - 1);
    }

    private float GetWaitingBaseIdleDuration()
    {
        return maxWaitingBaseIdleDuration > minWaitingBaseIdleDuration
            ? Random.Range(minWaitingBaseIdleDuration, maxWaitingBaseIdleDuration)
            : minWaitingBaseIdleDuration;
    }

    private void IncrementAlternateIdleCooldown()
    {
        if (_idleWindowsSinceAlternateIdle < int.MaxValue)
            _idleWindowsSinceAlternateIdle++;
    }

    private int CountAvailableAlternateIdleStates()
    {
        int count = 0;

        if (HasAnimationState(PatrolAnimation.IdleB)) count++;
        if (HasAnimationState(PatrolAnimation.IdleC)) count++;
        if (HasAnimationState(PatrolAnimation.IdleD)) count++;

        return count;
    }

    private PatrolAnimation GetAvailableAlternateIdleByOrdinal(int ordinal)
    {
        if (HasAnimationState(PatrolAnimation.IdleB))
        {
            if (ordinal == 0) return PatrolAnimation.IdleB;
            ordinal--;
        }

        if (HasAnimationState(PatrolAnimation.IdleC))
        {
            if (ordinal == 0) return PatrolAnimation.IdleC;
            ordinal--;
        }

        if (HasAnimationState(PatrolAnimation.IdleD) && ordinal == 0)
            return PatrolAnimation.IdleD;

        return PatrolAnimation.Idle;
    }

    private void HandleLockedMode()
    {
        switch (_overrideMode)
        {
            case AnimationOverrideMode.TalkLoop:
                EnforceAnimation(PatrolAnimation.Talk, force: false);

                if (_animator == null || _animator.IsInTransition(0))
                    return;

                var talkInfo = _animator.GetCurrentAnimatorStateInfo(0);
                if (StateMatches(talkInfo, _talkHash, _talkShortHash) && talkInfo.normalizedTime >= 1f)
                    _animator.Play(_talkHash, 0, 0f);
                return;

            case AnimationOverrideMode.CheeringOnce:
                if (_isCheeringPoseHeld)
                {
                    if (_animator == null)
                        return;

                    if (_animator.IsInTransition(0))
                        return;

                    var heldInfo = _animator.GetCurrentAnimatorStateInfo(0);
                    if (!StateMatches(heldInfo, _cheeringHash, _cheeringShortHash))
                    {
                        ReleaseCheeringPoseHold();
                        _cheeringFinished = false;
                        EnforceAnimation(PatrolAnimation.Cheering, force: true);
                    }
                    return;
                }

                if (!_cheeringFinished)
                {
                    EnforceAnimation(PatrolAnimation.Cheering, force: false);

                    if (_animator == null || _animator.IsInTransition(0))
                        return;

                    var cheerInfo = _animator.GetCurrentAnimatorStateInfo(0);
                    if (!StateMatches(cheerInfo, _cheeringHash, _cheeringShortHash))
                    {
                        EnforceAnimation(PatrolAnimation.Cheering, force: true);
                        return;
                    }

                    if (cheerInfo.normalizedTime >= 1f)
                        HoldCheeringPose();
                }
                else
                {
                    HoldCheeringPose();
                }
                return;

            default:
                EnforceAnimation(PatrolAnimation.Idle, force: false);
                return;
        }
    }

    private void HoldCheeringPose()
    {
        if (_animator == null || !_animator.HasState(0, _cheeringHash))
            return;

        float holdTime = Mathf.Clamp01(cheeringHoldNormalizedTime);
        _animator.Play(_cheeringHash, 0, holdTime);
        _animator.Update(0f);
        _animator.speed = 0f;

        _cheeringFinished = true;
        _isCheeringPoseHeld = true;
    }

    private void ReleaseCheeringPoseHold()
    {
        if (_animator != null)
        {
            float restoreSpeed = Mathf.Approximately(_defaultAnimatorSpeed, 0f) ? 1f : _defaultAnimatorSpeed;
            if (_isCheeringPoseHeld || Mathf.Approximately(_animator.speed, 0f))
                _animator.speed = restoreSpeed;
        }

        _isCheeringPoseHeld = false;
    }

    private void EnforceAnimation(PatrolAnimation target, bool force)
    {
        if (_animator == null)
            return;

        if (!force && Time.time < _nextAnimSwitchTime && target != _currentPatrolAnimation)
            return;

        int targetHash = target switch
        {
            PatrolAnimation.Idle => _idleHash,
            PatrolAnimation.IdleB => _idleBHash,
            PatrolAnimation.IdleC => _idleCHash,
            PatrolAnimation.IdleD => _idleDHash,
            PatrolAnimation.Walk => _walkHash,
            PatrolAnimation.Talk => _talkHash,
            _ => _cheeringHash
        };

        int targetShortHash = target switch
        {
            PatrolAnimation.Idle => _idleShortHash,
            PatrolAnimation.IdleB => _idleBShortHash,
            PatrolAnimation.IdleC => _idleCShortHash,
            PatrolAnimation.IdleD => _idleDShortHash,
            PatrolAnimation.Walk => _walkShortHash,
            PatrolAnimation.Talk => _talkShortHash,
            _ => _cheeringShortHash
        };

        if (!_animator.HasState(0, targetHash))
            return;

        // Alternate idle states are non-interruptible until their first cycle is complete.
        if (!force && !IsAlternateIdleState(target) && !HasCompletedCurrentAlternateIdlePlayback())
            return;

        // If we're already transitioning to the requested state, don't restart the transition.
        if (_animator.IsInTransition(0))
        {
            var nextInfo = _animator.GetNextAnimatorStateInfo(0);
            if (StateMatches(nextInfo, targetHash, targetShortHash))
            {
                _currentPatrolAnimation = target;
                return;
            }
        }

        bool needsTransition = force || target != _currentPatrolAnimation;
        if (!needsTransition)
        {
            var stateInfo = _animator.GetCurrentAnimatorStateInfo(0);
            if (!StateMatches(stateInfo, targetHash, targetShortHash))
                needsTransition = true;
        }

        if (!needsTransition)
            return;

        _animator.CrossFadeInFixedTime(targetHash, stateCrossFadeSeconds);
        _currentPatrolAnimation = target;
        _nextAnimSwitchTime = Time.time + minStateHoldSeconds;
    }

    private bool HasCompletedCurrentAlternateIdlePlayback()
    {
        if (!TryGetActiveAlternateIdleState(out PatrolAnimation activeState))
            return true;

        return HasCompletedIdleVariantPlayback(activeState);
    }

    private bool HasCompletedIdleVariantPlayback(PatrolAnimation state)
    {
        if (_animator == null || !IsAlternateIdleState(state))
            return true;

        GetAnimationStateHashes(state, out int fullHash, out int shortHash);

        if (_animator.IsInTransition(0))
        {
            var next = _animator.GetNextAnimatorStateInfo(0);
            if (StateMatches(next, fullHash, shortHash))
                return false;
        }

        var current = _animator.GetCurrentAnimatorStateInfo(0);
        if (!StateMatches(current, fullHash, shortHash))
            return true;

        return current.normalizedTime >= 1f;
    }

    private bool TryGetActiveAlternateIdleState(out PatrolAnimation state)
    {
        state = PatrolAnimation.Idle;

        if (_animator == null)
            return false;

        if (_animator.IsInTransition(0) &&
            TryMatchAlternateIdleState(_animator.GetNextAnimatorStateInfo(0), out state))
        {
            return true;
        }

        return TryMatchAlternateIdleState(_animator.GetCurrentAnimatorStateInfo(0), out state);
    }

    private bool TryMatchAlternateIdleState(AnimatorStateInfo stateInfo, out PatrolAnimation state)
    {
        if (StateMatches(stateInfo, _idleBHash, _idleBShortHash))
        {
            state = PatrolAnimation.IdleB;
            return true;
        }

        if (StateMatches(stateInfo, _idleCHash, _idleCShortHash))
        {
            state = PatrolAnimation.IdleC;
            return true;
        }

        if (StateMatches(stateInfo, _idleDHash, _idleDShortHash))
        {
            state = PatrolAnimation.IdleD;
            return true;
        }

        state = PatrolAnimation.Idle;
        return false;
    }

    private bool IsAlternateIdleState(PatrolAnimation state)
    {
        return state == PatrolAnimation.IdleB ||
               state == PatrolAnimation.IdleC ||
               state == PatrolAnimation.IdleD;
    }

    private bool HasAnimationState(PatrolAnimation state)
    {
        if (_animator == null)
            return false;

        GetAnimationStateHashes(state, out int fullHash, out _);
        return fullHash != 0 && _animator.HasState(0, fullHash);
    }

    private void GetAnimationStateHashes(PatrolAnimation state, out int fullHash, out int shortHash)
    {
        switch (state)
        {
            case PatrolAnimation.Idle:
                fullHash = _idleHash;
                shortHash = _idleShortHash;
                return;
            case PatrolAnimation.IdleB:
                fullHash = _idleBHash;
                shortHash = _idleBShortHash;
                return;
            case PatrolAnimation.IdleC:
                fullHash = _idleCHash;
                shortHash = _idleCShortHash;
                return;
            case PatrolAnimation.IdleD:
                fullHash = _idleDHash;
                shortHash = _idleDShortHash;
                return;
            case PatrolAnimation.Walk:
                fullHash = _walkHash;
                shortHash = _walkShortHash;
                return;
            case PatrolAnimation.Talk:
                fullHash = _talkHash;
                shortHash = _talkShortHash;
                return;
            default:
                fullHash = _cheeringHash;
                shortHash = _cheeringShortHash;
                return;
        }
    }

    private static bool StateMatches(AnimatorStateInfo stateInfo, int fullHash, int shortHash)
    {
        return stateInfo.fullPathHash == fullHash || stateInfo.shortNameHash == shortHash;
    }

    private static int HashShortStateName(string statePath)
    {
        if (string.IsNullOrWhiteSpace(statePath))
            return 0;

        int lastDotIndex = statePath.LastIndexOf('.');
        string shortName = lastDotIndex >= 0 && lastDotIndex < statePath.Length - 1
            ? statePath[(lastDotIndex + 1)..]
            : statePath;

        return Animator.StringToHash(shortName);
    }
}
