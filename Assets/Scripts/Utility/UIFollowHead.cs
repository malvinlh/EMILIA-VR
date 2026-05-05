using UnityEngine;

public class UIFollowHead : MonoBehaviour
{
    [Header("XR Camera (Head)")]
    public Transform playerCamera;

    [Header("Offset from Head (meters)")]
    public Vector3 offset = new Vector3(0f, -0.05f, 1.2f);

    [Header("Smoothing")]
    public float smoothTime = 0.15f;

    private Vector3 currentVelocity;

    void LateUpdate()
    {
        if (!playerCamera) return;

        // --- Position ---
        Vector3 targetPosition =
            playerCamera.position +
            playerCamera.forward * offset.z +
            playerCamera.up      * offset.y +
            playerCamera.right   * offset.x;

        transform.position = Vector3.SmoothDamp(
            transform.position,
            targetPosition,
            ref currentVelocity,
            smoothTime
        );

        // --- Rotation (ANTI-FLIP) ---
        Vector3 directionToCamera = transform.position - playerCamera.position;

        // Use camera up to prevent flipping
        transform.rotation = Quaternion.LookRotation(
            directionToCamera,
            playerCamera.up
        );
    }
}
