using UnityEngine;

/// <summary>
/// Applies a slow sine-wave "breathing" to a Light's intensity.
/// Attach to a GameObject with a Light component (Point / Spot recommended).
/// Designed for VR calm-room environments — temporal variation keeps a scene
/// feeling alive without triggering discomfort.
/// </summary>
[RequireComponent(typeof(Light))]
public class SubtleLightBreathing : MonoBehaviour
{
    [Tooltip("How many full cycles per second (Hz).  0.08–0.12 mimics restful breathing rate.")]
    [Range(0.01f, 0.5f)]
    public float frequency = 0.1f;

    [Tooltip("Max deviation from the base intensity (±).")]
    [Range(0f, 1f)]
    public float amplitude = 0.08f;

    [Tooltip("Random phase offset so multiple lights don't pulse in sync.")]
    public bool randomizePhase = true;

    private Light _light;
    private float _baseIntensity;
    private float _phase;

    private void Awake()
    {
        _light = GetComponent<Light>();
        _baseIntensity = _light.intensity;
        _phase = randomizePhase ? Random.Range(0f, Mathf.PI * 2f) : 0f;
    }

    private void Update()
    {
        float wave = Mathf.Sin(Time.time * frequency * Mathf.PI * 2f + _phase);
        _light.intensity = _baseIntensity + wave * amplitude;
    }
}
