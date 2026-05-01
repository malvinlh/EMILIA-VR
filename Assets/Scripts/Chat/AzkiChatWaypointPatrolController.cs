using System;
using System.Collections.Generic;
using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Waypoint patrol controller for AZKi in 3D_Chat.
/// Walks between fixed standing points, idles at each stop, and plays Talk
/// while assistant response text is visible in the dialogue panel.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NavMeshAgent))]
[RequireComponent(typeof(Animator))]
public class AzkiChatWaypointPatrolController : MonoBehaviour
{
    [Header("Standing Points")]
    [Tooltip("Parent transform that contains standing points for AZKi patrol.")]
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

    [Tooltip("Skip runtime build when NavMesh is already available near AZKi or patrol points.")]
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

    [Header("Facing")]
    [SerializeField] private bool snapFacingToAxis = true;

    [Tooltip("If true, stop-facing ignores standing-point yaw and randomly chooses world X or Z axis.")]
    [SerializeField] private bool randomStopFacingBetweenXAndZ = true;

    [Tooltip("When random stop-facing is enabled, include negative axis directions too (-X/-Z).")]
    [SerializeField] private bool includeNegativeAxisDirections = true;

    [Min(1f)]
    [SerializeField] private float stopTurnSpeed = 360f;

    [Header("Talk Facing")]
    [Tooltip("Smoothly align AZKi toward the player before starting Talk animation.")]
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

    [SerializeField] private string walkStateName = "Base Layer.Walk";

    [SerializeField] private string talkStateName     = "Base Layer.Talk";
    [SerializeField] private string cheeringStateName = "Base Layer.Cheering";

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
        Walk,
        Talk
    }

    private NavMeshAgent _agent;
    private Animator _animator;
    private AzkiBlendShapeExpressionController _blendShapeController;
    private NpcCharacterControllerGravity _gravityController;
    private NavMeshPath _pathBuffer;

    private readonly List<Transform> _standingPoints = new();

    private int _currentPointIndex = -1;
    private bool _isStopping;
    private bool _currentStopUsesIdleB;
    private float _stopUntilTime;
    private Quaternion _targetStopRotation;

    private bool _talkActive;
    private bool _loggedMissingNavMesh;
    private Transform _cachedPlayerTransform;
    private bool _isAligningBeforeTalk;
    private bool _talkLoopStarted;
    private float _preTalkDeadlineTime;

    // Proximity-engagement state (mirrors AzkiIslandRoamingController's API)
    private bool _isInProximityEngagement;
    private Transform _engagementTarget;

    private int _stopsSinceIdleB = int.MaxValue;
    private AnimState _currentAnimState = AnimState.Idle;
    private float _nextAnimSwitchTime;

    private int _idleHash;
    private int _idleBHash;
    private int _walkHash;
    private int _talkHash;
    private int _cheeringHash;
    private int _idleShortHash;
    private int _idleBShortHash;
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

        EnsureBlendShapeController();
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
                Debug.LogWarning("[AzkiChatWaypointPatrolController] No NavMesh found for AZKi. Bake NavMesh for 3D_Chat or enable runtime build fallback.", this);
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
            if (_isCheeringPlaying) CheckCheeringComplete();
            return;
        }

        SyncTalkStateFromPanel();

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

    // ── Proximity Engagement (mirrors AzkiIslandRoamingController API) ──────

    /// <summary>
    /// Pauses patrol at the current position, plays Idle, and continuously
    /// faces <paramref name="player"/> until <see cref="ExitProximityEngagement"/> is called.
    /// </summary>
    public void EnterProximityEngagement(Transform player)
    {
        _isInProximityEngagement = true;
        _engagementTarget = player;

        // Cancel any active talk so the agent stops cleanly.
        if (_talkActive)
        {
            _talkActive = false;
            _isAligningBeforeTalk = false;
            _talkLoopStarted = false;
        }

        if (_agent != null && _agent.enabled)
        {
            if (_agent.isOnNavMesh)
            {
                _agent.isStopped = true;
                _agent.ResetPath();
            }
            _agent.updateRotation = false;
        }

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

        if (_agent != null && _agent.enabled && _agent.isOnNavMesh)
            _agent.updateRotation = true;

        BeginStopWindow();
    }

    /// <summary>
    /// While in proximity engagement, switches AZKi to Talk loop
    /// (e.g. when a chat response is displayed).
    /// </summary>
    public void PlayTalkWhileEngaged()
    {
        if (!_isInProximityEngagement) return;
        SetTalkActive(true);
    }

    /// <summary>
    /// While in proximity engagement, returns AZKi to Idle
    /// (e.g. when dialogue is dismissed).
    /// </summary>
    public void ReturnToIdleWhileEngaged()
    {
        if (!_isInProximityEngagement) return;
        SetTalkActive(false);
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
            dialoguePanel.OnAssistantResponseVisibilityChanged -= OnAssistantResponseVisibilityChanged;

        dialoguePanel = panel;

        if (dialoguePanel != null)
        {
            dialoguePanel.OnAssistantResponseVisibilityChanged += OnAssistantResponseVisibilityChanged;
            SetTalkActive(dialoguePanel.IsAssistantResponseVisible);
        }
        else
        {
            SetTalkActive(false);
        }
    }

    private void OnAssistantResponseVisibilityChanged(bool isVisible)
    {
        SetTalkActive(isVisible);
    }

    // ── JournalReviewController integration ─────────────────────────────────
    // Match the call surface of AzkiIslandRoamingController so JRC dispatches
    // to either controller type without knowing which scene it is in.

    public void LockAtAuthoredPose()
    {
        _journalLocked      = true;
        _isCheeringPoseHeld = false;
        _isCheeringPlaying  = false;
        _talkActive = false;
        _isStopping = false;
        _isAligningBeforeTalk = false;
        _talkLoopStarted = false;
        if (_agent != null)
        {
            if (_agent.isOnNavMesh) { _agent.isStopped = true; _agent.ResetPath(); }
            _agent.updateRotation = false;
        }
        EnforceAnimation(AnimState.Idle, force: true);
    }

    public void ResumeRoaming()
    {
        _journalLocked = false;
        if (_animator != null) _animator.speed = 1f;
        _talkActive = false;
        _isAligningBeforeTalk = false;
        _talkLoopStarted = false;
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

    private void CheckCheeringComplete()
    {
        if (_animator == null || !_isCheeringPlaying) return;
        var info = _animator.GetCurrentAnimatorStateInfo(0);
        bool inCheer = info.shortNameHash == _cheeringShortHash
                    || info.fullPathHash  == _cheeringHash;
        if (inCheer && info.normalizedTime >= 0.99f && !_animator.IsInTransition(0))
        {
            _isCheeringPoseHeld = true;
            _isCheeringPlaying  = false;
            _animator.speed     = 0f;
        }
    }

    private void SyncTalkStateFromPanel()
    {
        if (dialoguePanel == null)
            return;

        SetTalkActive(dialoguePanel.IsAssistantResponseVisible);
    }

    private void SetTalkActive(bool active)
    {
        if (_talkActive == active)
            return;

        _talkActive = active;

        if (_talkActive)
        {
            if (_agent != null && _agent.enabled)
            {
                if (_agent.isOnNavMesh)
                {
                    _agent.isStopped = true;
                    _agent.ResetPath();
                }
                _agent.updateRotation = false;
            }

            _isStopping = false;
            _currentStopUsesIdleB = false;
            _talkLoopStarted = false;

            if (useHybridPreTalkAlignment)
            {
                _isAligningBeforeTalk = true;
                _preTalkDeadlineTime = Time.time + Mathf.Max(0f, preTalkMaxDelaySeconds);
                EnforceAnimation(AnimState.Idle, force: true);
            }
            else
            {
                _isAligningBeforeTalk = false;
                FacePlayerBeforeTalk();
                StartTalkLoop();
            }

            return;
        }

        _isAligningBeforeTalk = false;
        _talkLoopStarted = false;

        BeginStopWindow();
    }

    private void HandleTalkMode()
    {
        if (_agent != null && _agent.enabled)
        {
            if (_agent.isOnNavMesh)
                _agent.isStopped = true;

            _agent.updateRotation = false;
        }

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

        EnforceAnimation(AnimState.Talk, force: false);

        if (_animator == null || _animator.IsInTransition(0))
            return;

        var info = _animator.GetCurrentAnimatorStateInfo(0);
        if (StateMatches(info, _talkHash, _talkShortHash) && info.normalizedTime >= 1f)
            _animator.Play(_talkHash, 0, 0f);
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

    private void HandleStopWindow()
    {
        RotateTowardsTargetStopRotation();

        if (_currentStopUsesIdleB)
        {
            if (!HasCompletedIdleBPlayback())
            {
                EnforceAnimation(AnimState.IdleB, force: false);
                return;
            }

            // IdleB one-shot is complete: immediately resume walking.
            _currentStopUsesIdleB = false;
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

        bool playIdleB = ShouldPlayIdleB();
        _currentStopUsesIdleB = playIdleB;
        if (playIdleB)
            _stopsSinceIdleB = 0;
        else if (_stopsSinceIdleB < int.MaxValue)
            _stopsSinceIdleB++;

        EnforceAnimation(playIdleB ? AnimState.IdleB : AnimState.Idle, force: true);
    }

    private bool ShouldPlayIdleB()
    {
        if (_animator == null)
            return false;

        if (!_animator.HasState(0, _idleHash) || !_animator.HasState(0, _idleBHash))
            return false;

        if (_stopsSinceIdleB < minStopsBetweenIdleB)
            return false;

        return UnityEngine.Random.value <= idleBChancePerStop;
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

        _currentStopUsesIdleB = false;

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
            var surfaceObject = new GameObject("__AZKi_Chat_NavMeshSurfaceRuntime");
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

    private void EnsureBlendShapeController()
    {
        if (_blendShapeController == null)
            _blendShapeController = GetComponent<AzkiBlendShapeExpressionController>();

        if (_blendShapeController == null)
            _blendShapeController = gameObject.AddComponent<AzkiBlendShapeExpressionController>();
    }

    private void CacheAnimatorStateHashes()
    {
        _idleHash = Animator.StringToHash(idleStateName);
        _idleBHash = Animator.StringToHash(idleBStateName);
        _walkHash = Animator.StringToHash(walkStateName);
        _talkHash = Animator.StringToHash(talkStateName);

        _idleShortHash = HashShortStateName(idleStateName);
        _idleBShortHash = HashShortStateName(idleBStateName);
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

        // Keep IdleB as a one-shot that must finish before switching out.
        if (!force && targetState != AnimState.IdleB && !HasCompletedIdleBPlayback())
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

    private bool HasCompletedIdleBPlayback()
    {
        if (_animator == null)
            return true;

        if (_animator.IsInTransition(0))
        {
            var next = _animator.GetNextAnimatorStateInfo(0);
            if (StateMatches(next, _idleBHash, _idleBShortHash))
                return false;
        }

        var current = _animator.GetCurrentAnimatorStateInfo(0);
        bool currentIsIdleB = StateMatches(current, _idleBHash, _idleBShortHash);
        if (!currentIsIdleB)
            return true;

        return current.normalizedTime >= 1f;
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
