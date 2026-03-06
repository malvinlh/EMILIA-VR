using UnityEngine;

/// <summary>
/// Tracks horizontal player movement and spawns ripples on the water surface
/// at regular step intervals.
///
/// Attach to the **XR Origin** (or any root player transform).
/// Works with XR Interaction Toolkit locomotion (continuous move, teleport, etc.)
/// as well as regular editor / keyboard movement.
/// </summary>
public class PlayerFootRippleSpawner : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The WaterRippleManager on the water ground. Auto-found if left empty.")]
    public WaterRippleManager rippleManager;

    [Tooltip("Transform whose horizontal position is tracked. Defaults to this object.")]
    public Transform trackingTarget;

    [Header("Step Detection")]
    [Tooltip("Horizontal distance (m) the player must travel before spawning a ripple.")]
    public float stepDistance = 0.5f;

    [Tooltip("Minimum seconds between consecutive ripples.")]
    public float minStepInterval = 0.25f;

    [Tooltip("Minimum horizontal speed (m/s) to count as 'moving'.")]
    public float movementThreshold = 0.05f;

    [Header("Ripple")]
    [Tooltip("Strength of each spawned ripple (0-1).")]
    [Range(0f, 1f)]
    public float rippleStrength = 1f;

    [Header("Raycasting")]
    [Tooltip("How far downward to raycast for the water surface.")]
    public float raycastDistance = 10f;

    [Tooltip("Layer mask that includes the water ground. Default = Everything.")]
    public LayerMask waterLayerMask = ~0;

    // ---- internal ----
    private Vector3 _lastHPos;
    private float   _distAccum;
    private float   _lastSpawnTime;

    // ---------------------------------------------------
    void Start()
    {
        if (trackingTarget == null)
            trackingTarget = transform;

        _lastHPos = Horizontal(trackingTarget.position);

        // Auto-find ripple manager if unassigned
        if (rippleManager == null)
            rippleManager = FindAnyObjectByType<WaterRippleManager>();
    }

    void Update()
    {
        if (rippleManager == null || trackingTarget == null) return;

        Vector3 hPos  = Horizontal(trackingTarget.position);
        float   delta = Vector3.Distance(hPos, _lastHPos);

        // Only accumulate when actually moving
        if (delta > movementThreshold * Time.deltaTime)
        {
            _distAccum += delta;

            if (_distAccum >= stepDistance &&
                Time.time - _lastSpawnTime >= minStepInterval)
            {
                SpawnAtFeet();
                _distAccum     = 0f;
                _lastSpawnTime = Time.time;
            }
        }

        _lastHPos = hPos;
    }

    // ---------------------------------------------------
    private void SpawnAtFeet()
    {
        Vector3 origin = trackingTarget.position + Vector3.up * 0.5f;

        if (Physics.Raycast(origin, Vector3.down, out RaycastHit hit,
                            raycastDistance, waterLayerMask))
        {
            rippleManager.SpawnRipple(hit.point, rippleStrength);
        }
        else
        {
            // Fallback: project player XZ onto the water surface Y
            float waterY = rippleManager.transform.position.y;
            Vector3 fallback = new Vector3(
                trackingTarget.position.x,
                waterY,
                trackingTarget.position.z);
            rippleManager.SpawnRipple(fallback, rippleStrength);
        }
    }

    private static Vector3 Horizontal(Vector3 v)
    {
        v.y = 0f;
        return v;
    }
}
