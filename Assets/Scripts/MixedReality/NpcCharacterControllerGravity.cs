using UnityEngine;

/// <summary>
/// Adds simple gravity behavior to a CharacterController-driven NPC.
/// This keeps the avatar grounded like a typical game NPC without requiring a Rigidbody.
/// </summary>
[RequireComponent(typeof(CharacterController))]
public class NpcCharacterControllerGravity : MonoBehaviour
{
    [Header("Gravity")]
    [SerializeField] private float gravity = -18f;
    [SerializeField] private float groundedStickVelocity = -2f;
    [SerializeField] private float terminalVelocity = -35f;

    [Header("Controller Safety")]
    [Tooltip("Small margin to keep Step Offset under Unity's scaled validation limit.")]
    [SerializeField] private float stepOffsetSafetyMargin = 0.01f;

    private CharacterController _controller;
    private float _verticalVelocity;

    private void Awake()
    {
        _controller = GetComponent<CharacterController>();
    }

    private void OnEnable()
    {
        if (_controller == null)
            _controller = GetComponent<CharacterController>();

        ClampStepOffsetToValidRange();
        _verticalVelocity = groundedStickVelocity;
    }

    private void OnValidate()
    {
        if (_controller == null)
            _controller = GetComponent<CharacterController>();

        if (_controller != null)
            ClampStepOffsetToValidRange();
    }

    private void Update()
    {
        if (_controller == null || !_controller.enabled)
            return;

        if (_controller.isGrounded)
        {
            if (_verticalVelocity < groundedStickVelocity)
                _verticalVelocity = groundedStickVelocity;
        }
        else
        {
            _verticalVelocity += gravity * Time.deltaTime;
            if (_verticalVelocity < terminalVelocity)
                _verticalVelocity = terminalVelocity;
        }

        Vector3 motion = Vector3.up * (_verticalVelocity * Time.deltaTime);
        CollisionFlags flags = _controller.Move(motion);

        if ((flags & CollisionFlags.Below) != 0 && _verticalVelocity < groundedStickVelocity)
            _verticalVelocity = groundedStickVelocity;
    }

    private void ClampStepOffsetToValidRange()
    {
        float scaleY = Mathf.Max(Mathf.Abs(transform.lossyScale.y), 0.0001f);
        float scaleXZ = Mathf.Max(Mathf.Abs(transform.lossyScale.x), Mathf.Abs(transform.lossyScale.z));
        scaleXZ = Mathf.Max(scaleXZ, 0.0001f);

        float scaledHeight = _controller.height * scaleY;
        float scaledRadius = _controller.radius * scaleXZ;
        float maxAllowedStepOffset = Mathf.Max(0f, scaledHeight + (scaledRadius * 2f) - stepOffsetSafetyMargin);

        if (_controller.stepOffset > maxAllowedStepOffset)
            _controller.stepOffset = maxAllowedStepOffset;

        if (_controller.stepOffset < 0f)
            _controller.stepOffset = 0f;
    }
}
