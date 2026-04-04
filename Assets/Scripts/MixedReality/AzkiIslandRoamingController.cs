using Unity.AI.Navigation;
using UnityEngine;
using UnityEngine.AI;

/// <summary>
/// Roaming controller for AZKi.
///
/// - Captures the authored scene pose once and can lock back to it for review moments.
/// - Builds/uses a NavMeshSurface at runtime and drives random patrol movement.
/// - Enforces only Idle, IdleB, and Walk animator states for patrol behavior.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(NavMeshAgent))]
[RequireComponent(typeof(Animator))]
public class AzkiIslandRoamingController : MonoBehaviour
{
    [Header("Navigation")]
    [Tooltip("Optional NavMeshSurface. If empty, one is auto-found or created at runtime.")]
    [SerializeField] private NavMeshSurface navMeshSurface;

    [Tooltip("Radius (meters) from the authored AZKi position used for patrol destinations.")]
    [Min(0.5f)]
    [SerializeField] private float patrolRadius = 12f;

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

    [Tooltip("If true, builds NavMeshSurface once at runtime when roaming starts.")]
    [SerializeField] private bool buildNavMeshOnStart = true;

    [Header("Idle Timing")]
    [Tooltip("Minimum idle duration between patrol points.")]
    [Min(0f)]
    [SerializeField] private float minIdleDuration = 1.8f;

    [Tooltip("Maximum idle duration between patrol points.")]
    [Min(0f)]
    [SerializeField] private float maxIdleDuration = 4.2f;

    [Header("Animation")]
    [Tooltip("Animator state path for patrol idle.")]
    [SerializeField] private string idleStateName = "Base Layer.Idle";

    [Tooltip("Animator state path for alternate patrol idle.")]
    [SerializeField] private string idleBStateName = "Base Layer.IdleB";

    [Tooltip("Animator state path for patrol walk.")]
    [SerializeField] private string walkStateName = "Base Layer.Walk";

    [Tooltip("Cross-fade duration used when changing patrol animation state.")]
    [Range(0f, 0.5f)]
    [SerializeField] private float stateCrossFadeSeconds = 0.12f;

    [Tooltip("Minimum hold time before switching to another patrol animation state.")]
    [Range(0f, 2f)]
    [SerializeField] private float minStateHoldSeconds = 0.5f;

    [Tooltip("Velocity threshold used to classify walk vs idle.")]
    [Range(0.01f, 2f)]
    [SerializeField] private float movingVelocityThreshold = 0.12f;

    private enum PatrolAnimation
    {
        Idle,
        IdleB,
        Walk
    }

    private NavMeshAgent _agent;
    private Animator _animator;
    private NpcCharacterControllerGravity _gravityController;
    private NavMeshPath _pathBuffer;

    private Vector3 _authoredPosition;
    private Quaternion _authoredRotation;
    private bool _authoredPoseCaptured;

    private bool _navMeshBuilt;
    private bool _isLocked;
    private bool _isIdling;
    private float _idleUntilTime;

    private PatrolAnimation _currentPatrolAnimation = PatrolAnimation.Idle;
    private float _nextAnimSwitchTime;

    private int _idleHash;
    private int _idleBHash;
    private int _walkHash;
    private int _idleShortHash;
    private int _idleBShortHash;
    private int _walkShortHash;

    private void Awake()
    {
        _agent = GetComponent<NavMeshAgent>();
        _animator = GetComponent<Animator>();
        _gravityController = GetComponent<NpcCharacterControllerGravity>();
        _pathBuffer = new NavMeshPath();

        CaptureAuthoredPose();
        CacheAnimatorStateHashes();

        if (_animator != null)
            _animator.applyRootMotion = false;
    }

    private void Start()
    {
        ResumeRoaming();
    }

    private void Update()
    {
        if (_isLocked)
        {
            EnforceAnimation(PatrolAnimation.Idle, force: false);
            return;
        }

        if (_agent == null || !_agent.enabled)
            return;

        if (!_agent.isOnNavMesh)
            return;

        bool isWalking = _agent.velocity.sqrMagnitude >= movingVelocityThreshold * movingVelocityThreshold;

        if (_isIdling)
        {
            bool waitingForIdleBCompletion =
                _currentPatrolAnimation == PatrolAnimation.IdleB &&
                !HasCompletedIdleBPlayback();

            if (waitingForIdleBCompletion)
            {
                EnforceAnimation(PatrolAnimation.IdleB, force: false);
                return;
            }

            // IdleB is a one-shot: once completed, continue idling in Idle.
            if (_currentPatrolAnimation == PatrolAnimation.IdleB)
                EnforceAnimation(PatrolAnimation.Idle, force: true);
            else
                EnforceAnimation(_currentPatrolAnimation, force: false);

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

        _isLocked = true;
        _isIdling = false;

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
    /// Enables roaming behavior. If needed, builds a runtime NavMesh first.
    /// </summary>
    public void ResumeRoaming()
    {
        InitializeIfNeeded();

        _isLocked = false;

        if (_gravityController != null)
            _gravityController.enabled = false;

        if (_agent == null)
            return;

        if (!_agent.enabled)
            _agent.enabled = true;

        if (buildNavMeshOnStart && !_navMeshBuilt)
            BuildRuntimeNavMesh();

        if (!EnsureAgentOnNavMesh())
        {
            EnforceAnimation(PatrolAnimation.Idle, force: true);
            return;
        }

        _agent.isStopped = false;
        _agent.updateRotation = true;
        _isIdling = false;

        SetNextDestination();
    }

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
        if (_gravityController == null)
            _gravityController = GetComponent<NpcCharacterControllerGravity>();
        if (_pathBuffer == null)
            _pathBuffer = new NavMeshPath();

        if (!_authoredPoseCaptured)
            CaptureAuthoredPose();

        if (_idleHash == 0 || _idleBHash == 0 || _walkHash == 0)
            CacheAnimatorStateHashes();
    }

    private void CacheAnimatorStateHashes()
    {
        _idleHash = Animator.StringToHash(idleStateName);
        _idleBHash = Animator.StringToHash(idleBStateName);
        _walkHash = Animator.StringToHash(walkStateName);

        _idleShortHash = HashShortStateName(idleStateName);
        _idleBShortHash = HashShortStateName(idleBStateName);
        _walkShortHash = HashShortStateName(walkStateName);
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
            navMeshSurface.useGeometry = NavMeshCollectGeometry.RenderMeshes;
        }

        navMeshSurface.BuildNavMesh();
        _navMeshBuilt = true;
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
        EnforceAnimation(PatrolAnimation.Walk, force: false);
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

        if (_agent != null)
            _agent.ResetPath();

        _idleUntilTime = Time.time + Random.Range(minIdleDuration, maxIdleDuration);
        PatrolAnimation idleVariant = Random.value < 0.5f ? PatrolAnimation.Idle : PatrolAnimation.IdleB;
        EnforceAnimation(idleVariant, force: true);
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
            _ => _walkHash
        };

        int targetShortHash = target switch
        {
            PatrolAnimation.Idle => _idleShortHash,
            PatrolAnimation.IdleB => _idleBShortHash,
            _ => _walkShortHash
        };

        if (!_animator.HasState(0, targetHash))
            return;

        // IdleB is non-interruptible until its first cycle is complete.
        if (!force && target != PatrolAnimation.IdleB && !HasCompletedIdleBPlayback())
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

    private bool HasCompletedIdleBPlayback()
    {
        if (_animator == null)
            return true;

        var current = _animator.GetCurrentAnimatorStateInfo(0);
        bool currentIsIdleB = StateMatches(current, _idleBHash, _idleBShortHash);
        if (!currentIsIdleB)
            return true;

        return current.normalizedTime >= 1f;
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
