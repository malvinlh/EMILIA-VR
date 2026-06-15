using System;
using System.Collections.Generic;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Waypoint patrol controller for EMILIA in 3D_Chat.
/// Walks between fixed standing points, idles at each stop, and plays Talk
/// while assistant response text is visible in the dialogue panel.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NavMeshAgent))]
[RequireComponent(typeof(Animator))]
public class AvatarChatWaypointPatrolController : MonoBehaviour
{
    [Header("Standing Points")]
    [Tooltip("Parent transform that contains standing points for EMILIA patrol.")]
    [SerializeField] private Transform standingPointsRoot;

    [Tooltip("Auto-find/create root object with this name when reference is empty.")]
    [SerializeField] private string standingPointsRootName = "StandingPoints";

    [Tooltip("Preferred wall object name. If not found, falls back to FloorBedroom parent.")]
    [SerializeField] private string wallBedroomName = "WallBedroom";

    [Tooltip("Create standing points automatically if not found or too few.")]
    [SerializeField] private bool createStandingPointsIfMissing = true;

    [Min(2)]
    [SerializeField] private int minimumStandingPoints = 4;

    [Min(0.1f)]
    [SerializeField] private float generatedPointRadius = 2.6f;

    [SerializeField] private float generatedPointHeight = 0f;

    [Header("Navigation")]
    [SerializeField] private NavMeshSurface navMeshSurface;

    [Tooltip("Runtime fallback only. Keep disabled when scene navmesh is pre-baked.")]
    [SerializeField] private bool buildNavMeshOnStart = false;

    [Tooltip("Skip runtime build when NavMesh is already available near EMILIA or patrol points.")]
    [SerializeField] private bool skipRuntimeBuildIfNavMeshPresent = true;

    [Tooltip("Geometry source for runtime NavMesh build. PhysicsColliders avoids mesh read-access warnings in player builds.")]
    [SerializeField] private NavMeshCollectGeometry runtimeBuildGeometry = NavMeshCollectGeometry.PhysicsColliders;

    [Min(0.1f)]
    [SerializeField] private float navMeshSampleDistance = 2.5f;

    [Min(0.05f)]
    [SerializeField] private float destinationReachedDistance = 0.35f;

    [Min(0.1f)]
    [SerializeField] private float walkSpeed = 0.8f;

    [Range(0.01f, 1f)]
    [SerializeField] private float movingVelocityThreshold = 0.08f;

    [Header("Stop Timing")]
    [Min(0f)]
    [SerializeField] private float minStopDuration = 2f;

    [Min(0f)]
    [SerializeField] private float maxStopDuration = 4f;

    [Header("IdleB Frequency")]
    [Range(0f, 1f)]
    [SerializeField] private float idleBChancePerStop = 0.3f;

    [Min(0)]
    [SerializeField] private int minStopsBetweenIdleB = 2;

    [Header("Waiting Idle Timing")]
    [Min(0f)]
    [SerializeField] private float minWaitingBaseIdleDuration = 0.9f;

    [Min(0f)]
    [SerializeField] private float maxWaitingBaseIdleDuration = 1.8f;

    [Header("Facing")]
    [SerializeField] private bool snapFacingToAxis = true;

    [Tooltip("If true, stop-facing ignores standing-point yaw and randomly chooses world X or Z axis.")]
    [SerializeField] private bool randomStopFacingBetweenXAndZ = true;

    [Tooltip("When random stop-facing is enabled, include negative axis directions too (-X/-Z).")]
    [SerializeField] private bool includeNegativeAxisDirections = true;

    [Min(1f)]
    [SerializeField] private float stopTurnSpeed = 360f;

    [Header("Talk Facing")]
    [Tooltip("Smoothly align EMILIA toward the player before starting Talk animation.")]
    [SerializeField] private bool useHybridPreTalkAlignment = true;

    [Min(1f)]
    [SerializeField] private float preTalkTurnSpeed = 900f;

    [Range(0.5f, 45f)]
    [SerializeField] private float preTalkStartAngleThreshold = 12f;

    [Range(0f, 1f)]
    [SerializeField] private float preTalkMaxDelaySeconds = 0.22f;

    [Tooltip("Keep rotating toward the player while Talk animation is active.")]
    [SerializeField] private bool trackPlayerWhileTalking = true;

    [Min(1f)]
    [SerializeField] private float talkTurnSpeed = 540f;

    [Header("Animation")]
    [SerializeField] private string idleStateName = "Base Layer.Idle";

    [SerializeField] private string idleBStateName = "Base Layer.IdleB";

    [SerializeField] private string idleCStateName = "Base Layer.IdleC";

    [SerializeField] private string idleDStateName = "Base Layer.IdleD";

    [SerializeField] private string walkStateName = "Base Layer.Walk";

    [SerializeField] private string talkStateName     = "Base Layer.Talk";
    [SerializeField] private string cheeringStateName = "Base Layer.Cheering";

    [Tooltip("Normalized time used when freezing the final cheering pose (1.0 = end of clip).")]
    [Range(0.9f, 1f)]
    [SerializeField] private float cheeringHoldNormalizedTime = 0.995f;

    [Range(0f, 0.5f)]
    [SerializeField] private float stateCrossFadeSeconds = 0.12f;

    [Range(0f, 2f)]
    [SerializeField] private float minStateHoldSeconds = 0.25f;

    [Header("Dialogue")]
    [SerializeField] private VRDialoguePanel dialoguePanel;

    private enum AnimState
    {
        Idle,
        IdleB,
        IdleC,
        IdleD,
        Walk,
        Talk
    }

    private NavMeshAgent _agent;
    private Animator _animator;
    private NpcCharacterControllerGravity _gravityController;
    private NavMeshPath _pathBuffer;

    private readonly List<Transform> _standingPoints = new();

    private int _currentPointIndex = -1;
    private bool _isStopping;
    private AnimState _currentStopIdleState = AnimState.Idle;
    private float _stopUntilTime;
    private Quaternion _targetStopRotation;
    private bool _isWaitingForResponse;
    private AnimState _currentWaitingIdleState = AnimState.Idle;
    private float _waitingIdleUntilTime;

    private bool _talkActive;
    private bool _loggedMissingNavMesh;
    private Transform _cachedPlayerTransform;
    private bool _isAligningBeforeTalk;
    private bool _talkLoopStarted;
    private float _preTalkDeadlineTime;

    // Proximity-engagement state (mirrors AvatarIslandRoamingController's API)
    private bool _isInProximityEngagement;
    private Transform _engagementTarget;

    private int _stopsSinceAlternateIdle = int.MaxValue;
    private AnimState _currentAnimState = AnimState.Idle;
    private float _nextAnimSwitchTime;

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

    // ── Journal review lock ───────────────────────────────────────────────
    private bool _journalLocked;
    private bool _isCheeringPoseHeld;
    private bool _isCheeringPlaying;

    private void Awake()
    {
        _agent = GetComponent<NavMeshAgent>();
        _animator = GetComponent<Animator>();
        _gravityController = GetComponent<NpcCharacterControllerGravity>();
        _pathBuffer = new NavMeshPath();

        CacheAnimatorStateHashes();

        if (_animator != null)
            _animator.applyRootMotion = false;

        if (_gravityController != null)
            _gravityController.enabled = false;

        if (_agent != null)
        {
            _agent.speed = walkSpeed;
            _agent.stoppingDistance = destinationReachedDistance;
            _agent.autoBraking = true;
        }
    }

    private void Start()
    {
        if (maxStopDuration < minStopDuration)
            maxStopDuration = minStopDuration;
        if (maxWaitingBaseIdleDuration < minWaitingBaseIdleDuration)
            maxWaitingBaseIdleDuration = minWaitingBaseIdleDuration;

        ResolveStandingPoints();

        if (buildNavMeshOnStart)
            BuildRuntimeNavMeshIfNeeded();

        if (dialoguePanel == null)
            dialoguePanel = FindFirstObjectByType<VRDialoguePanel>();

        SetDialoguePanel(dialoguePanel);

        if (!EnsureAgentOnNavMesh())
        {
            if (!_loggedMissingNavMesh)
            {
                _loggedMissingNavMesh = true;
                Debug.LogWarning("[AvatarChatWaypointPatrolController] No NavMesh found for EMILIA. Bake NavMesh for 3D_Chat or enable runtime build fallback.", this);
            }

            EnforceAnimation(AnimState.Idle, force: true);
            return;
        }

        BeginStopWindow();
    }

    private void OnDisable()
    {
        SetDialoguePanel(null);
    }

    private void OnDestroy()
    {
        SetDialoguePanel(null);
    }

    private void Update()
    {
        if (_journalLocked)
        {
            HandleJournalLockedMode();
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

        // Proximity engagement: freeze patrol and face the player.
        if (_isInProximityEngagement)
        {
            HandleProximityEngagement();
            return;
        }

        if (_agent == null || !_agent.enabled || !_agent.isOnNavMesh)
        {
            EnforceAnimation(AnimState.Idle, force: false);
            return;
        }

        if (_isStopping)
        {
            HandleStopWindow();
            return;
        }

        if (!_agent.pathPending && _agent.remainingDistance <= destinationReachedDistance)
        {
            BeginStopWindow();
            return;
        }

        bool isMoving = _agent.velocity.sqrMagnitude >= movingVelocityThreshold * movingVelocityThreshold;
        if (isMoving)
            EnforceAnimation(AnimState.Walk, force: false);
    }

    // ── Proximity Engagement (mirrors AvatarIslandRoamingController API) ──────

    /// <summary>
    /// Pauses patrol at the current position, plays Idle, and continuously
    /// faces <paramref name="player"/> until <see cref="ExitProximityEngagement"/> is called.
    /// </summary>
    public void EnterProximityEngagement(Transform player)
    {
        _isInProximityEngagement = true;
        _engagementTarget = player;

        if (_talkActive || _isWaitingForResponse)
            return;

        StopAgentMovementForDialogue();

        _isStopping = false;
        EnforceAnimation(AnimState.Idle, force: true);
    }

    /// <summary>
    /// Exits proximity engagement and resumes normal patrol.
    /// </summary>
    public void ExitProximityEngagement()
    {
        _isInProximityEngagement = false;
        _engagementTarget = null;

        if (_talkActive || _isWaitingForResponse)
            return;

        if (_agent != null && _agent.enabled && _agent.isOnNavMesh)
            _agent.updateRotation = true;

        BeginStopWindow();
    }

    /// <summary>
    /// While in proximity engagement, switches EMILIA to Talk loop
    /// (e.g. when a chat response is displayed).
    /// </summary>
    public void PlayTalkWhileEngaged()
    {
        if (!_isInProximityEngagement || _talkActive || _isWaitingForResponse) return;
        EnforceAnimation(AnimState.Talk, force: true);
    }

    /// <summary>
    /// While in proximity engagement, returns EMILIA to Idle
    /// (e.g. when dialogue is dismissed).
    /// </summary>
    public void ReturnToIdleWhileEngaged()
    {
        if (!_isInProximityEngagement || _talkActive || _isWaitingForResponse) return;
        EnforceAnimation(AnimState.Idle, force: true);
    }

    private void HandleProximityEngagement()
    {
        // Keep the agent frozen.
        if (_agent != null && _agent.enabled && _agent.isOnNavMesh)
            _agent.isStopped = true;

        // Continuously face the player.
        if (_engagementTarget != null)
        {
            Vector3 toTarget = _engagementTarget.position - transform.position;
            toTarget.y = 0f;
            if (toTarget.sqrMagnitude > 0.0001f)
            {
                Quaternion targetRot = Quaternion.LookRotation(toTarget.normalized, Vector3.up);
                transform.rotation = Quaternion.RotateTowards(
                    transform.rotation, targetRot, stopTurnSpeed * Time.deltaTime);
            }
        }

        EnforceAnimation(AnimState.Idle, force: false);
    }

    // ── Dialogue panel binding ────────────────────────────────────────────

    public void SetDialoguePanel(VRDialoguePanel panel)
    {
        if (dialoguePanel == panel)
            return;

        if (dialoguePanel != null)
        {
            dialoguePanel.OnAssistantResponseVisibilityChanged -= OnAssistantResponseVisibilityChanged;
            dialoguePanel.OnTypingIndicatorVisibilityChanged -= OnTypingIndicatorVisibilityChanged;
        }

        dialoguePanel = panel;

        if (dialoguePanel != null)
        {
            dialoguePanel.OnAssistantResponseVisibilityChanged += OnAssistantResponseVisibilityChanged;
            dialoguePanel.OnTypingIndicatorVisibilityChanged += OnTypingIndicatorVisibilityChanged;
            SyncDialogueStateFromPanel();
        }
        else
        {
            SyncDialogueStateFromPanel();
        }
    }

    private void OnAssistantResponseVisibilityChanged(bool isVisible)
    {
        SyncDialogueStateFromPanel();
    }

    private void OnTypingIndicatorVisibilityChanged(bool isVisible)
    {
        SyncDialogueStateFromPanel();
    }

    // ── JournalReviewController integration ─────────────────────────────────
    // Match the call surface of AvatarIslandRoamingController so JRC dispatches
    // to either controller type without knowing which scene it is in.

    public void LockAtAuthoredPose()
    {
        ReleaseCheeringPoseHold();
        _journalLocked      = true;
        _isCheeringPlaying  = false;
        _talkActive = false;
        _isWaitingForResponse = false;
        _isStopping = false;
        _isAligningBeforeTalk = false;
        _talkLoopStarted = false;
        ResetWaitingIdleCycle();
        if (_agent != null)
        {
            if (_agent.isOnNavMesh) { _agent.isStopped = true; _agent.ResetPath(); _agent.velocity = Vector3.zero; }
            _agent.updateRotation = false;
        }
        EnforceAnimation(AnimState.Idle, force: true);
    }

    public void ResumeRoaming()
    {
        ReleaseCheeringPoseHold();
        _journalLocked = false;
        _isCheeringPlaying = false;
        if (_animator != null) _animator.speed = 1f;
        _talkActive = false;
        _isWaitingForResponse = false;
        _isAligningBeforeTalk = false;
        _talkLoopStarted = false;
        ResetWaitingIdleCycle();
        if (_agent != null) _agent.updateRotation = true;
        if (!EnsureAgentOnNavMesh()) { EnforceAnimation(AnimState.Idle, force: true); return; }
        BeginStopWindow();
    }

    public void PlayTalkLoop()
    {
        LockAtAuthoredPose();
        _talkActive = true;
        _talkLoopStarted = false;
        _isAligningBeforeTalk = false;
        StartTalkLoop();
    }

    public void PlayCheeringOneShot()
    {
        LockAtAuthoredPose();
        _isCheeringPoseHeld = false;
        _isCheeringPlaying  = true;
        if (_animator != null)
        {
            _animator.speed = 1f;
            _animator.CrossFadeInFixedTime(_cheeringHash, 0.12f, 0);
        }
    }

    public bool IsCheeringPoseHeld => _isCheeringPoseHeld;

    // Continuously re-asserts the Cheering state while journal-locked so the controller's
    // auto-exit Cheering->Walk transition cannot escape. Mirrors the CheeringOnce branch
    // of AvatarIslandRoamingController.HandleLockedMode.
    private void HandleJournalLockedMode()
    {
        if (!_isCheeringPlaying && !_isCheeringPoseHeld) return;
        if (_animator == null) return;

        var info = _animator.GetCurrentAnimatorStateInfo(0);
        bool inCheer = info.shortNameHash == _cheeringShortHash
                    || info.fullPathHash  == _cheeringHash;

        if (_isCheeringPoseHeld)
        {
            if (_animator.IsInTransition(0)) return;
            if (!inCheer)
            {
                _isCheeringPoseHeld = false;
                _isCheeringPlaying  = true;
                _animator.speed     = 1f;
                _animator.CrossFadeInFixedTime(_cheeringHash, stateCrossFadeSeconds, 0);
            }
            return;
        }

        if (!_animator.IsInTransition(0) && !inCheer)
        {
            _animator.CrossFadeInFixedTime(_cheeringHash, stateCrossFadeSeconds, 0);
            return;
        }

        if (inCheer && info.normalizedTime >= cheeringHoldNormalizedTime)
            HoldCheeringPose();
    }

    private void HoldCheeringPose()
    {
        if (_animator == null || !_animator.HasState(0, _cheeringHash)) return;

        float holdTime = Mathf.Clamp01(cheeringHoldNormalizedTime);
        _animator.Play(_cheeringHash, 0, holdTime);
        _animator.Update(0f);
        _animator.speed     = 0f;
        _isCheeringPlaying  = false;
        _isCheeringPoseHeld = true;
    }

    private void ReleaseCheeringPoseHold()
    {
        if (_animator != null &&
            (_isCheeringPoseHeld || Mathf.Approximately(_animator.speed, 0f)))
        {
            _animator.speed = 1f;
        }
        _isCheeringPoseHeld = false;
    }

    private void SyncDialogueStateFromPanel()
    {
        bool waitingForResponse = dialoguePanel != null && dialoguePanel.IsTypingIndicatorVisible;
        bool talkActive = !waitingForResponse &&
                          dialoguePanel != null &&
                          dialoguePanel.IsAssistantResponseVisible;

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
        _isStopping = false;
        _currentStopIdleState = AnimState.Idle;
        _talkLoopStarted = false;
        ResetWaitingIdleCycle();

        if (useHybridPreTalkAlignment)
        {
            _isAligningBeforeTalk = true;
            _preTalkDeadlineTime = Time.time + Mathf.Max(0f, preTalkMaxDelaySeconds);
            EnforceAnimation(AnimState.Idle, force: true);
            return;
        }

        _isAligningBeforeTalk = false;
        FacePlayerBeforeTalk();
        StartTalkLoop();
    }

    private void ExitTalkState(bool enteringWaiting)
    {
        _isAligningBeforeTalk = false;
        _talkLoopStarted = false;

        if (enteringWaiting || _journalLocked)
            return;

        if (_isInProximityEngagement)
        {
            EnforceAnimation(AnimState.Idle, force: true);
            return;
        }

        BeginStopWindow();
    }

    private void EnterWaitingState()
    {
        StopAgentMovementForDialogue();
        _isAligningBeforeTalk = false;
        _talkLoopStarted = false;
        _isStopping = false;
        _currentStopIdleState = AnimState.Idle;
        ResetWaitingIdleCycle();
        StartWaitingIdleCycle(force: true);
    }

    private void ExitWaitingState(bool enteringTalk)
    {
        ResetWaitingIdleCycle();

        if (enteringTalk || _journalLocked)
            return;

        if (_isInProximityEngagement)
        {
            EnforceAnimation(AnimState.Idle, force: true);
            return;
        }

        BeginStopWindow();
    }

    private void HandleTalkMode()
    {
        StopAgentMovementForDialogue();

        if (_isAligningBeforeTalk)
        {
            bool hasPlayer = RotateTowardsPlayer(preTalkTurnSpeed, out float remainingAngle);
            bool aligned = hasPlayer && remainingAngle <= preTalkStartAngleThreshold;
            bool timedOut = Time.time >= _preTalkDeadlineTime;

            if (!aligned && !timedOut)
            {
                EnforceAnimation(AnimState.Idle, force: false);
                return;
            }

            _isAligningBeforeTalk = false;
            StartTalkLoop();
        }

        RotateTowardsPlayerWhileTalking();

        if (!_talkLoopStarted)
            StartTalkLoop();

        if (_animator != null)
            _animator.speed = 1f;
        EnforceAnimation(AnimState.Talk, force: false);

        if (_animator == null || _animator.IsInTransition(0))
            return;

        var info = _animator.GetCurrentAnimatorStateInfo(0);
        if (StateMatches(info, _talkHash, _talkShortHash) && info.normalizedTime >= 1f)
        {
            _animator.Play(_talkHash, 0, 0f);
            _animator.Update(0f);
        }
    }

    private void HandleWaitingMode()
    {
        StopAgentMovementForDialogue();

        if (IsAlternateIdleState(_currentWaitingIdleState))
        {
            if (!HasCompletedIdleVariantPlayback(_currentWaitingIdleState))
            {
                EnforceAnimation(_currentWaitingIdleState, force: false);
                return;
            }

            StartWaitingBaseIdleDwell(force: true);
            return;
        }

        EnforceAnimation(AnimState.Idle, force: false);

        if (Time.time >= _waitingIdleUntilTime)
            StartWaitingIdleCycle(force: false);
    }

    private void StartTalkLoop()
    {
        if (_talkLoopStarted)
            return;

        _talkLoopStarted = true;
        EnforceAnimation(AnimState.Talk, force: true);
    }

    private void RotateTowardsPlayerWhileTalking()
    {
        if (!trackPlayerWhileTalking)
            return;

        RotateTowardsPlayer(talkTurnSpeed, out _);
    }

    private void FacePlayerBeforeTalk()
    {
        Transform player = ResolvePlayerTransform();
        if (player == null)
            return;

        Vector3 toPlayer = player.position - transform.position;
        toPlayer.y = 0f;

        if (toPlayer.sqrMagnitude < 0.0001f)
            return;

        transform.rotation = Quaternion.LookRotation(toPlayer.normalized, Vector3.up);
    }

    private bool RotateTowardsPlayer(float turnSpeedDegPerSec, out float remainingAngle)
    {
        remainingAngle = 180f;

        Transform player = ResolvePlayerTransform();
        if (player == null)
            return false;

        Vector3 toPlayer = player.position - transform.position;
        toPlayer.y = 0f;

        if (toPlayer.sqrMagnitude < 0.0001f)
        {
            remainingAngle = 0f;
            return true;
        }

        Quaternion targetRotation = Quaternion.LookRotation(toPlayer.normalized, Vector3.up);
        transform.rotation = Quaternion.RotateTowards(
            transform.rotation,
            targetRotation,
            turnSpeedDegPerSec * Time.deltaTime
        );

        remainingAngle = Quaternion.Angle(transform.rotation, targetRotation);
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

    private void HandleStopWindow()
    {
        RotateTowardsTargetStopRotation();

        if (IsAlternateIdleState(_currentStopIdleState))
        {
            if (!HasCompletedIdleVariantPlayback(_currentStopIdleState))
            {
                EnforceAnimation(_currentStopIdleState, force: false);
                return;
            }

            // Alternate idle one-shot is complete: immediately resume walking.
            _currentStopIdleState = AnimState.Idle;
            _isStopping = false;
            MoveToNextStandingPoint();
            return;
        }

        EnforceAnimation(AnimState.Idle, force: false);

        if (Time.time >= _stopUntilTime)
        {
            _isStopping = false;
            MoveToNextStandingPoint();
        }
    }

    private void BeginStopWindow()
    {
        if (_talkActive)
            return;

        _isStopping = true;

        if (_agent != null && _agent.enabled)
        {
            if (_agent.isOnNavMesh)
            {
                _agent.isStopped = true;
                _agent.ResetPath();
            }

            _agent.updateRotation = false;
        }

        _targetStopRotation = GetPointFacingRotation();

        float stopDuration = maxStopDuration > minStopDuration
            ? UnityEngine.Random.Range(minStopDuration, maxStopDuration)
            : minStopDuration;
        _stopUntilTime = Time.time + stopDuration;

        _currentStopIdleState = SelectPatrolIdleState();
        EnforceAnimation(_currentStopIdleState, force: true);
    }

    private AnimState SelectPatrolIdleState()
    {
        if (_animator == null)
            return AnimState.Idle;

        int availableAlternateCount = CountAvailableAlternateIdleStates();
        if (!_animator.HasState(0, _idleHash) || availableAlternateCount == 0)
        {
            IncrementAlternateIdleCooldown();
            return AnimState.Idle;
        }

        if (_stopsSinceAlternateIdle < minStopsBetweenIdleB ||
            UnityEngine.Random.value > idleBChancePerStop)
        {
            IncrementAlternateIdleCooldown();
            return AnimState.Idle;
        }

        _stopsSinceAlternateIdle = 0;
        return GetAvailableAlternateIdleByOrdinal(UnityEngine.Random.Range(0, availableAlternateCount));
    }

    private Quaternion GetPointFacingRotation()
    {
        float yaw;

        if (randomStopFacingBetweenXAndZ)
        {
            yaw = GetRandomStopYaw();
        }
        else if (_currentPointIndex >= 0 && _currentPointIndex < _standingPoints.Count)
        {
            yaw = _standingPoints[_currentPointIndex].rotation.eulerAngles.y;
        }
        else
        {
            yaw = transform.rotation.eulerAngles.y;
        }

        if (snapFacingToAxis)
            yaw = SnapYawToAxis(yaw);

        return Quaternion.Euler(0f, yaw, 0f);
    }

    private float GetRandomStopYaw()
    {
        bool useXAxis = UnityEngine.Random.value < 0.5f;

        if (!includeNegativeAxisDirections)
            return useXAxis ? 90f : 0f;

        bool negativeDirection = UnityEngine.Random.value < 0.5f;
        if (useXAxis)
            return negativeDirection ? 270f : 90f;

        return negativeDirection ? 180f : 0f;
    }

    private void RotateTowardsTargetStopRotation()
    {
        transform.rotation = Quaternion.RotateTowards(
            transform.rotation,
            _targetStopRotation,
            stopTurnSpeed * Time.deltaTime
        );
    }

    private static float SnapYawToAxis(float yaw)
    {
        return Mathf.Round(yaw / 90f) * 90f;
    }

    private void MoveToNextStandingPoint()
    {
        if (_agent == null || !_agent.enabled || !_agent.isOnNavMesh)
            return;

        _currentStopIdleState = AnimState.Idle;

        if (_standingPoints.Count == 0)
        {
            EnforceAnimation(AnimState.Idle, force: true);
            return;
        }

        int startIndex = UnityEngine.Random.Range(0, _standingPoints.Count);

        for (int i = 0; i < _standingPoints.Count; i++)
        {
            int candidateIndex = (startIndex + i) % _standingPoints.Count;

            if (_standingPoints.Count > 1 && candidateIndex == _currentPointIndex)
                continue;

            if (!TrySetDestination(candidateIndex))
                continue;

            _currentPointIndex = candidateIndex;
            _isStopping = false;
            _agent.isStopped = false;
            _agent.updateRotation = true;
            EnforceAnimation(AnimState.Walk, force: true);
            return;
        }

        BeginStopWindow();
    }

    private void ResetWaitingIdleCycle()
    {
        _currentWaitingIdleState = AnimState.Idle;
        _waitingIdleUntilTime = 0f;
    }

    private void StartWaitingIdleCycle(bool force)
    {
        _currentWaitingIdleState = SelectWaitingIdleState();

        if (_currentWaitingIdleState == AnimState.Idle)
        {
            _waitingIdleUntilTime = Time.time + GetWaitingBaseIdleDuration();
            EnforceAnimation(AnimState.Idle, force);
            return;
        }

        _waitingIdleUntilTime = 0f;
        EnforceAnimation(_currentWaitingIdleState, force: true);
    }

    private void StartWaitingBaseIdleDwell(bool force)
    {
        _currentWaitingIdleState = AnimState.Idle;
        _waitingIdleUntilTime = Time.time + GetWaitingBaseIdleDuration();
        EnforceAnimation(AnimState.Idle, force);
    }

    private AnimState SelectWaitingIdleState()
    {
        int availableAlternateCount = CountAvailableAlternateIdleStates();
        int choice = UnityEngine.Random.Range(0, availableAlternateCount + 1);
        return choice == 0
            ? AnimState.Idle
            : GetAvailableAlternateIdleByOrdinal(choice - 1);
    }

    private float GetWaitingBaseIdleDuration()
    {
        return maxWaitingBaseIdleDuration > minWaitingBaseIdleDuration
            ? UnityEngine.Random.Range(minWaitingBaseIdleDuration, maxWaitingBaseIdleDuration)
            : minWaitingBaseIdleDuration;
    }

    private void IncrementAlternateIdleCooldown()
    {
        if (_stopsSinceAlternateIdle < int.MaxValue)
            _stopsSinceAlternateIdle++;
    }

    private int CountAvailableAlternateIdleStates()
    {
        int count = 0;

        if (HasAnimationState(AnimState.IdleB)) count++;
        if (HasAnimationState(AnimState.IdleC)) count++;
        if (HasAnimationState(AnimState.IdleD)) count++;

        return count;
    }

    private AnimState GetAvailableAlternateIdleByOrdinal(int ordinal)
    {
        if (HasAnimationState(AnimState.IdleB))
        {
            if (ordinal == 0) return AnimState.IdleB;
            ordinal--;
        }

        if (HasAnimationState(AnimState.IdleC))
        {
            if (ordinal == 0) return AnimState.IdleC;
            ordinal--;
        }

        if (HasAnimationState(AnimState.IdleD) && ordinal == 0)
            return AnimState.IdleD;

        return AnimState.Idle;
    }

    private bool TrySetDestination(int pointIndex)
    {
        if (pointIndex < 0 || pointIndex >= _standingPoints.Count)
            return false;

        if (!TrySampleNavMesh(_standingPoints[pointIndex].position, out Vector3 destination))
            return false;

        if (!_agent.CalculatePath(destination, _pathBuffer))
            return false;

        if (_pathBuffer.status != NavMeshPathStatus.PathComplete)
            return false;

        _agent.SetDestination(destination);
        return true;
    }

    private bool TrySampleNavMesh(Vector3 worldPosition, out Vector3 sampledPosition)
    {
        sampledPosition = worldPosition;

        if (!NavMesh.SamplePosition(worldPosition, out NavMeshHit hit, navMeshSampleDistance, NavMesh.AllAreas))
            return false;

        sampledPosition = hit.position;
        return true;
    }

    private bool EnsureAgentOnNavMesh()
    {
        if (_agent == null)
            return false;

        if (_agent.isOnNavMesh)
            return true;

        if (TrySampleNavMesh(transform.position, out Vector3 sampled))
        {
            _agent.Warp(sampled);
            return true;
        }

        return false;
    }

    private void BuildRuntimeNavMeshIfNeeded()
    {
        if (skipRuntimeBuildIfNavMeshPresent && IsNavMeshAvailableForPatrol())
            return;

        BuildRuntimeNavMesh();
    }

    private bool IsNavMeshAvailableForPatrol()
    {
        if (TrySampleNavMesh(transform.position, out _))
            return true;

        for (int i = 0; i < _standingPoints.Count; i++)
        {
            Transform point = _standingPoints[i];
            if (point == null)
                continue;

            if (TrySampleNavMesh(point.position, out _))
                return true;
        }

        return false;
    }

    private void BuildRuntimeNavMesh()
    {
        if (navMeshSurface == null)
            navMeshSurface = FindFirstObjectByType<NavMeshSurface>();

        if (navMeshSurface == null)
        {
            var surfaceObject = new GameObject("__EMILIA_Chat_NavMeshSurfaceRuntime");
            navMeshSurface = surfaceObject.AddComponent<NavMeshSurface>();
            navMeshSurface.collectObjects = CollectObjects.All;
        }

        navMeshSurface.useGeometry = runtimeBuildGeometry;

        navMeshSurface.BuildNavMesh();
    }

    private void ResolveStandingPoints()
    {
        Transform wallBedroom = FindWallBedroomTransform();

        if (standingPointsRoot == null)
            standingPointsRoot = FindStandingPointsRoot();

        if (standingPointsRoot == null && createStandingPointsIfMissing)
            standingPointsRoot = CreateStandingPointsRoot(wallBedroom);

        if (standingPointsRoot != null && wallBedroom != null && standingPointsRoot.parent != wallBedroom)
            standingPointsRoot.SetParent(wallBedroom, true);

        CacheStandingPoints();

        if (standingPointsRoot != null && createStandingPointsIfMissing && _standingPoints.Count < minimumStandingPoints)
            GenerateStandingPoints(minimumStandingPoints - _standingPoints.Count);

        CacheStandingPoints();
    }

    private Transform FindStandingPointsRoot()
    {
        Transform containsMatch = null;
        Transform[] allTransforms = FindObjectsOfType<Transform>(true);

        foreach (Transform t in allTransforms)
        {
            if (t == null)
                continue;

            if (string.Equals(t.name, standingPointsRootName, StringComparison.OrdinalIgnoreCase))
                return t;

            if (containsMatch == null &&
                t.name.IndexOf(standingPointsRootName, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                containsMatch = t;
            }
        }

        return containsMatch;
    }

    private Transform FindWallBedroomTransform()
    {
        Transform containsWallBedroom = null;
        Transform floorBedroomParent = null;

        Transform[] allTransforms = FindObjectsOfType<Transform>(true);
        foreach (Transform t in allTransforms)
        {
            if (t == null)
                continue;

            if (string.Equals(t.name, wallBedroomName, StringComparison.OrdinalIgnoreCase))
                return t;

            if (containsWallBedroom == null &&
                t.name.IndexOf(wallBedroomName, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                containsWallBedroom = t;
            }

            if (floorBedroomParent == null &&
                t.name.IndexOf("FloorBedroom", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                floorBedroomParent = t.parent != null ? t.parent : t;
            }
        }

        if (containsWallBedroom != null)
            return containsWallBedroom;

        return floorBedroomParent;
    }

    private Transform CreateStandingPointsRoot(Transform preferredParent)
    {
        var rootObject = new GameObject(standingPointsRootName);
        Transform root = rootObject.transform;

        if (preferredParent != null)
            root.SetParent(preferredParent, true);

        root.position = transform.position;
        return root;
    }

    private void GenerateStandingPoints(int pointsToAdd)
    {
        if (standingPointsRoot == null || pointsToAdd <= 0)
            return;

        int existingCount = standingPointsRoot.childCount;
        int totalCount = Mathf.Max(existingCount + pointsToAdd, minimumStandingPoints);
        Vector3 center = transform.position;

        for (int i = 0; i < pointsToAdd; i++)
        {
            int ordinal = existingCount + i;
            float normalized = totalCount > 0 ? (float)ordinal / totalCount : 0f;
            float angle = normalized * Mathf.PI * 2f;

            Vector3 worldPosition = center + new Vector3(
                Mathf.Cos(angle) * generatedPointRadius,
                generatedPointHeight,
                Mathf.Sin(angle) * generatedPointRadius
            );

            if (TrySampleNavMesh(worldPosition, out Vector3 sampledPosition))
                worldPosition = sampledPosition;

            var pointObject = new GameObject($"SP_{ordinal + 1:00}");
            Transform point = pointObject.transform;
            point.SetParent(standingPointsRoot, true);
            point.position = worldPosition;

            Vector3 toCenter = center - worldPosition;
            toCenter.y = 0f;
            if (toCenter.sqrMagnitude < 0.0001f)
                toCenter = Vector3.forward;

            float yaw = Quaternion.LookRotation(toCenter.normalized).eulerAngles.y;
            if (snapFacingToAxis)
                yaw = SnapYawToAxis(yaw);

            point.rotation = Quaternion.Euler(0f, yaw, 0f);
        }
    }

    private void CacheStandingPoints()
    {
        _standingPoints.Clear();

        if (standingPointsRoot == null)
            return;

        for (int i = 0; i < standingPointsRoot.childCount; i++)
        {
            Transform child = standingPointsRoot.GetChild(i);
            if (child != null)
                _standingPoints.Add(child);
        }

        _standingPoints.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
    }

    private void CacheAnimatorStateHashes()
    {
        _idleHash = Animator.StringToHash(idleStateName);
        _idleBHash = Animator.StringToHash(idleBStateName);
        _idleCHash = Animator.StringToHash(idleCStateName);
        _idleDHash = Animator.StringToHash(idleDStateName);
        _walkHash = Animator.StringToHash(walkStateName);
        _talkHash = Animator.StringToHash(talkStateName);

        _idleShortHash = HashShortStateName(idleStateName);
        _idleBShortHash = HashShortStateName(idleBStateName);
        _idleCShortHash = HashShortStateName(idleCStateName);
        _idleDShortHash = HashShortStateName(idleDStateName);
        _walkShortHash = HashShortStateName(walkStateName);
        _talkShortHash     = HashShortStateName(talkStateName);
        _cheeringHash      = Animator.StringToHash(cheeringStateName);
        _cheeringShortHash = HashShortStateName(cheeringStateName);
    }

    private void EnforceAnimation(AnimState targetState, bool force)
    {
        if (_animator == null)
            return;

        if (!force && Time.time < _nextAnimSwitchTime && targetState != _currentAnimState)
            return;

        int targetHash;
        int targetShortHash;

        switch (targetState)
        {
            case AnimState.Idle:
                targetHash = _idleHash;
                targetShortHash = _idleShortHash;
                break;
            case AnimState.IdleB:
                targetHash = _idleBHash;
                targetShortHash = _idleBShortHash;
                break;
            case AnimState.IdleC:
                targetHash = _idleCHash;
                targetShortHash = _idleCShortHash;
                break;
            case AnimState.IdleD:
                targetHash = _idleDHash;
                targetShortHash = _idleDShortHash;
                break;
            case AnimState.Walk:
                targetHash = _walkHash;
                targetShortHash = _walkShortHash;
                break;
            default:
                targetHash = _talkHash;
                targetShortHash = _talkShortHash;
                break;
        }

        if (!_animator.HasState(0, targetHash))
            return;

        // Keep alternate idles as one-shots that must finish before switching out.
        if (!force && !IsAlternateIdleState(targetState) && !HasCompletedCurrentAlternateIdlePlayback())
            return;

        if (_animator.IsInTransition(0))
        {
            var nextInfo = _animator.GetNextAnimatorStateInfo(0);
            if (StateMatches(nextInfo, targetHash, targetShortHash))
            {
                _currentAnimState = targetState;
                return;
            }
        }

        bool needsTransition = force || targetState != _currentAnimState;
        if (!needsTransition)
        {
            var currentInfo = _animator.GetCurrentAnimatorStateInfo(0);
            if (!StateMatches(currentInfo, targetHash, targetShortHash))
                needsTransition = true;
        }

        if (!needsTransition)
            return;

        _animator.CrossFadeInFixedTime(targetHash, stateCrossFadeSeconds);
        _currentAnimState = targetState;
        _nextAnimSwitchTime = Time.time + minStateHoldSeconds;
    }

    private bool HasCompletedCurrentAlternateIdlePlayback()
    {
        if (!TryGetActiveAlternateIdleState(out AnimState activeState))
            return true;

        return HasCompletedIdleVariantPlayback(activeState);
    }

    private bool HasCompletedIdleVariantPlayback(AnimState state)
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

    private bool TryGetActiveAlternateIdleState(out AnimState state)
    {
        state = AnimState.Idle;

        if (_animator == null)
            return false;

        if (_animator.IsInTransition(0) &&
            TryMatchAlternateIdleState(_animator.GetNextAnimatorStateInfo(0), out state))
        {
            return true;
        }

        return TryMatchAlternateIdleState(_animator.GetCurrentAnimatorStateInfo(0), out state);
    }

    private bool TryMatchAlternateIdleState(AnimatorStateInfo info, out AnimState state)
    {
        if (StateMatches(info, _idleBHash, _idleBShortHash))
        {
            state = AnimState.IdleB;
            return true;
        }

        if (StateMatches(info, _idleCHash, _idleCShortHash))
        {
            state = AnimState.IdleC;
            return true;
        }

        if (StateMatches(info, _idleDHash, _idleDShortHash))
        {
            state = AnimState.IdleD;
            return true;
        }

        state = AnimState.Idle;
        return false;
    }

    private bool IsAlternateIdleState(AnimState state)
    {
        return state == AnimState.IdleB ||
               state == AnimState.IdleC ||
               state == AnimState.IdleD;
    }

    private bool HasAnimationState(AnimState state)
    {
        if (_animator == null)
            return false;

        GetAnimationStateHashes(state, out int fullHash, out _);
        return fullHash != 0 && _animator.HasState(0, fullHash);
    }

    private void GetAnimationStateHashes(AnimState state, out int fullHash, out int shortHash)
    {
        switch (state)
        {
            case AnimState.Idle:
                fullHash = _idleHash;
                shortHash = _idleShortHash;
                return;
            case AnimState.IdleB:
                fullHash = _idleBHash;
                shortHash = _idleBShortHash;
                return;
            case AnimState.IdleC:
                fullHash = _idleCHash;
                shortHash = _idleCShortHash;
                return;
            case AnimState.IdleD:
                fullHash = _idleDHash;
                shortHash = _idleDShortHash;
                return;
            case AnimState.Walk:
                fullHash = _walkHash;
                shortHash = _walkShortHash;
                return;
            default:
                fullHash = _talkHash;
                shortHash = _talkShortHash;
                return;
        }
    }

    private static bool StateMatches(AnimatorStateInfo info, int fullHash, int shortHash)
    {
        return info.fullPathHash == fullHash || info.shortNameHash == shortHash;
    }

    private static int HashShortStateName(string statePath)
    {
        if (string.IsNullOrWhiteSpace(statePath))
            return 0;

        int lastDot = statePath.LastIndexOf('.');
        string shortName = (lastDot >= 0 && lastDot < statePath.Length - 1)
            ? statePath.Substring(lastDot + 1)
            : statePath;

        return Animator.StringToHash(shortName);
    }
}
