using UnityEngine;

/// <summary>
/// Spawns ripple effects and plays splash SFX on the water surface
/// when the XR player moves. Attach this to the XR Origin GameObject.
/// </summary>
public class WaterFootstepEffect : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The water ground transform. Ripples spawn at this Y height.")]
    [SerializeField] private Transform waterSurface;

    [Header("Splash SFX")]
    [SerializeField] private AudioClip[] splashClips;
    [SerializeField, Range(0f, 1f)] private float splashVolume = 0.4f;

    [Header("Ripple Settings")]
    [Tooltip("Material using the Custom/WaterRipple shader.")]
    [SerializeField] private Material rippleMaterial;
    [SerializeField] private float rippleSize = 1.2f;
    [SerializeField] private float rippleDuration = 0.8f;

    [Header("Step Detection")]
    [Tooltip("Distance the player must move (XZ) before a footstep triggers.")]
    [SerializeField] private float stepDistance = 0.65f;
    [Tooltip("Minimum seconds between footstep triggers.")]
    [SerializeField] private float stepCooldown = 0.45f;

    private Vector3 _lastStepPosition;
    private Camera _playerCamera;
    private float _lastStepTime;

    // Simple object pool for ripple quads
    private const int PoolSize = 10;
    private GameObject[] _ripplePool;
    private MaterialPropertyBlock[] _propBlocks;
    private float[] _rippleStartTimes;
    private int _nextRippleIndex;

    private AudioSource _audioSource;

    private void Awake()
    {
        _playerCamera = Camera.main;
        _lastStepPosition = GetPlayerFootPosition();
        _lastStepTime = -999f;

        // Set up audio source for splash SFX
        _audioSource = gameObject.AddComponent<AudioSource>();
        _audioSource.spatialBlend = 1f; // 3D sound
        _audioSource.playOnAwake = false;
        _audioSource.volume = splashVolume;

        InitializeRipplePool();
    }

    private void InitializeRipplePool()
    {
        _ripplePool = new GameObject[PoolSize];
        _propBlocks = new MaterialPropertyBlock[PoolSize];
        _rippleStartTimes = new float[PoolSize];

        for (int i = 0; i < PoolSize; i++)
        {
            var rippleGO = GameObject.CreatePrimitive(PrimitiveType.Quad);
            rippleGO.name = "WaterRipple_" + i;
            rippleGO.transform.SetParent(transform, false);

            // Face upward (water surface is horizontal)
            rippleGO.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            rippleGO.transform.localScale = Vector3.one * rippleSize;

            // Remove collider — we don't need collision on ripple quads
            var col = rippleGO.GetComponent<Collider>();
            if (col != null) Destroy(col);

            var renderer = rippleGO.GetComponent<MeshRenderer>();
            renderer.material = rippleMaterial;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            _propBlocks[i] = new MaterialPropertyBlock();
            _rippleStartTimes[i] = -999f;
            rippleGO.SetActive(false);

            _ripplePool[i] = rippleGO;
        }
    }

    private void Update()
    {
        DetectStep();
        UpdateRipples();
    }

    /// <summary>
    /// Returns the player's foot position by projecting the camera XZ onto the water surface.
    /// Uses Camera.main (the actual head position) so it works with both
    /// XR hardware and the XR Device Simulator.
    /// </summary>
    private Vector3 GetPlayerFootPosition()
    {
        if (_playerCamera == null)
            _playerCamera = Camera.main;

        Vector3 camPos = _playerCamera != null ? _playerCamera.transform.position : transform.position;
        return new Vector3(camPos.x, 0f, camPos.z);
    }

    private void DetectStep()
    {
        Vector3 currentPos = GetPlayerFootPosition();

        // Only measure XZ movement (ignore vertical)
        Vector2 currentXZ = new Vector2(currentPos.x, currentPos.z);
        Vector2 lastXZ = new Vector2(_lastStepPosition.x, _lastStepPosition.z);

        float dist = Vector2.Distance(currentXZ, lastXZ);
        bool cooledDown = (Time.time - _lastStepTime) >= stepCooldown;

        if (dist >= stepDistance && cooledDown)
        {
            TriggerFootstep(currentPos);
            _lastStepPosition = currentPos;
            _lastStepTime = Time.time;
        }
    }

    private void TriggerFootstep(Vector3 playerPos)
    {
        // Determine spawn position on the water surface
        float waterY = waterSurface != null ? waterSurface.position.y + 0.01f : 0.01f;
        Vector3 ripplePos = new Vector3(playerPos.x, waterY, playerPos.z);

        SpawnRipple(ripplePos);
        PlaySplashSFX();
    }

    private void SpawnRipple(Vector3 position)
    {
        int idx = _nextRippleIndex;
        _nextRippleIndex = (_nextRippleIndex + 1) % PoolSize;

        var rippleGO = _ripplePool[idx];
        rippleGO.transform.position = position;
        rippleGO.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
        rippleGO.transform.localScale = Vector3.one * rippleSize;
        rippleGO.SetActive(true);
        _rippleStartTimes[idx] = Time.time;
    }

    private void UpdateRipples()
    {
        int progressID = Shader.PropertyToID("_Progress");

        for (int i = 0; i < PoolSize; i++)
        {
            if (!_ripplePool[i].activeSelf) continue;

            float elapsed = Time.time - _rippleStartTimes[i];
            float t = elapsed / rippleDuration;

            if (t >= 1f)
            {
                _ripplePool[i].SetActive(false);
                continue;
            }

            // Update the _Progress property via MaterialPropertyBlock (no material cloning)
            _propBlocks[i].SetFloat(progressID, t);
            _ripplePool[i].GetComponent<MeshRenderer>().SetPropertyBlock(_propBlocks[i]);
        }
    }

    private void PlaySplashSFX()
    {
        if (splashClips == null || splashClips.Length == 0) return;

        int idx = Random.Range(0, splashClips.Length);
        _audioSource.pitch = Random.Range(0.9f, 1.1f); // slight variation
        _audioSource.PlayOneShot(splashClips[idx], splashVolume);
    }
}
