using UnityEngine;

/// <summary>
/// Positions the VR dialogue panel using a two-mode system:
///
/// 1. **Character-anchored** — When the player faces the character (within a gaze cone),
///    the panel floats near the character's chest, billboard-facing the player.
/// 2. **Soft-follow** — When the player looks away, the panel smoothly drifts to a
///    comfortable position below the player's gaze center.
///
/// Transitions between modes use <see cref="Vector3.SmoothDamp"/> and
/// <see cref="Quaternion.Slerp"/> so the panel never snaps or causes discomfort.
/// </summary>
public class DialoguePanelPositioner : MonoBehaviour
{
    [Header("References")]
    [Tooltip("Transform near the character's chest (e.g., EMILIA_dialogue_anchor). Billboard origin.")]
    [SerializeField] private Transform _characterAnchor;
    [Tooltip("XR Camera (Main Camera). Auto-resolved if null.")]
    [SerializeField] private Transform _camera;

    [Header("Character-Anchored Mode")]
    [Tooltip("Offset from character anchor in the anchor's local space (Y = up from chest).")]
    [SerializeField] private Vector3 _anchorOffset = new(0f, 0.15f, 0.35f);

    [Header("Soft-Follow Mode")]
    [Tooltip("Distance in front of the camera when following.")]
    [SerializeField] private float _followDistance = 1.8f;
    [Tooltip("Degrees below the camera's forward to position the panel.")]
    [SerializeField] private float _followDownAngle = 17f;

    [Header("Gaze Threshold")]
    [Tooltip("Dot-product threshold for 'looking at character'. 0.55 ≈ 56° half-cone.")]
    [SerializeField, Range(0f, 1f)] private float _gazeThreshold = 0.55f;

    [Header("Smoothing")]
    [SerializeField] private float _blendSpeed = 2.5f;     // mode blend per second
    [SerializeField] private float _positionSmoothTime = 0.35f;
    [SerializeField] private float _rotationSmoothSpeed = 5f;

    private float   _blendFactor;   // 0 = character-anchored, 1 = soft-follow
    private Vector3 _velocity;      // for SmoothDamp

    private void Start()
    {
        if (_camera == null)
            _camera = Camera.main != null ? Camera.main.transform : null;
    }

    private void LateUpdate()
    {
        if (_camera == null || _characterAnchor == null) return;

        // --- Determine gaze direction ---
        Vector3 toCharacter = (_characterAnchor.position - _camera.position).normalized;
        float dot = Vector3.Dot(_camera.forward, toCharacter);

        // --- Blend factor: 0 = anchored, 1 = follow ---
        float targetBlend = dot >= _gazeThreshold ? 0f : 1f;
        _blendFactor = Mathf.MoveTowards(_blendFactor, targetBlend, _blendSpeed * Time.deltaTime);

        // --- Compute target positions for each mode ---
        Vector3 anchoredPos = ComputeAnchoredPosition();
        Vector3 followPos   = ComputeFollowPosition();

        Vector3 targetPos = Vector3.Lerp(anchoredPos, followPos, _blendFactor);

        // --- Smooth position ---
        transform.position = Vector3.SmoothDamp(transform.position, targetPos, ref _velocity, _positionSmoothTime);

        // --- Billboard rotation (always face camera) ---
        Vector3 lookDir = _camera.position - transform.position;
        if (lookDir.sqrMagnitude > 0.001f)
        {
            Quaternion targetRot = Quaternion.LookRotation(-lookDir, Vector3.up);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, _rotationSmoothSpeed * Time.deltaTime);
        }
    }

    private Vector3 ComputeAnchoredPosition()
    {
        // Position relative to character, pushed toward the camera so it's readable
        Vector3 worldOffset = _characterAnchor.TransformDirection(_anchorOffset);
        Vector3 basePos = _characterAnchor.position + worldOffset;

        // Nudge toward camera so the panel isn't clipping the character
        Vector3 toCamera = (_camera.position - basePos).normalized;
        return basePos + toCamera * 0.1f;
    }

    private Vector3 ComputeFollowPosition()
    {
        // Position in front of camera, angled slightly downward
        Quaternion downTilt = Quaternion.AngleAxis(_followDownAngle, _camera.right);
        Vector3 forward = downTilt * _camera.forward;
        return _camera.position + forward * _followDistance;
    }
}
