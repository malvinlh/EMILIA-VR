using UnityEngine;

/// <summary>
/// Manages interactive water ripple data and pushes it to the MiSide/WaterGround shader
/// every frame.  Attach this component to the Ground GameObject that uses the water material.
/// </summary>
[RequireComponent(typeof(Renderer))]
public class WaterRippleManager : MonoBehaviour
{
    [Header("Settings")]
    [Tooltip("Maximum simultaneous ripples (must match MAX_RIPPLES in shader — default 10).")]
    [Range(1, 10)]
    public int maxRipples = 10;

    [Tooltip("Seconds each ripple lives. Should match _RippleFadeDuration in the material.")]
    public float rippleFadeDuration = 2.0f;

    // ---- internal state ----
    private Material  _mat;
    private Vector4[] _ripples;
    private int       _nextIdx;

    // Cached shader property IDs
    private static readonly int ID_RippleData  = Shader.PropertyToID("_RippleData");
    private static readonly int ID_RippleCount = Shader.PropertyToID("_RippleCount");

    // -------------------------------------------------------
    void Awake()
    {
        _ripples = new Vector4[maxRipples];

        // Create a material instance so other objects sharing the same material are unaffected
        Renderer rend = GetComponent<Renderer>();
        if (rend != null)
            _mat = rend.material;
    }

    void Update()
    {
        if (_mat == null) return;

        float now = Time.time;

        // Expire dead ripples
        for (int i = 0; i < maxRipples; i++)
        {
            if (_ripples[i].w > 0f && now - _ripples[i].z > rippleFadeDuration + 0.5f)
                _ripples[i] = Vector4.zero;
        }

        // Push to GPU
        _mat.SetVectorArray(ID_RippleData, _ripples);
        _mat.SetInt(ID_RippleCount, maxRipples);
    }

    // -------------------------------------------------------
    /// <summary>
    /// Spawn a new ripple centred at <paramref name="worldPos"/>.
    /// Only the X and Z components are used.
    /// </summary>
    /// <param name="worldPos">World-space hit point on the water.</param>
    /// <param name="strength">Intensity multiplier (0 – 1).</param>
    public void SpawnRipple(Vector3 worldPos, float strength = 1f)
    {
        _ripples[_nextIdx] = new Vector4(
            worldPos.x,
            worldPos.z,
            Time.time,
            Mathf.Clamp01(strength)
        );
        _nextIdx = (_nextIdx + 1) % maxRipples;
    }

    // -------------------------------------------------------
    void OnDestroy()
    {
        // Clean up the instanced material to avoid leaks
        if (_mat != null)
            Destroy(_mat);
    }
}
