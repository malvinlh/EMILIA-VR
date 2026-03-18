using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;

/// <summary>
/// Utility script for creating and manipulating whiteboards via XR Hand Tracking.
/// Replaces all OVRHand / OVRSkeleton references with the cross-platform XR Hands API.
/// Works with Meta Quest 3 via the Meta OpenXR plugin.
/// </summary>
public class WhiteboardUtils : MonoBehaviour
{
    public GameObject WhiteboardPrefab;
    public GameObject SpherePrefab;

    private XRHandSubsystem handSubsystem;

    private GameObject whiteboard;

    private RaycastHit touch;

    private GameObject projectionSphereLeft;
    private GameObject projectionSphereRight;

    private Transform whiteboardTransform;

    // Camera Floor Offset transform — required to convert XRHandJoint session-space
    // positions to world space when Device tracking mode with CameraYOffset is used.
    private Transform cameraOffsetTransform;

    [SerializeField] private Transform whiteboardPlaceholder;

    private const float ADJUSTMENT_DISTANCE = .2f;
    private const int BOARD_LAYER = 10;
    private const float PINCH_THRESHOLD = 0.02f;

    /// <summary>
    /// Gets the normal vector of a plane defined by three points.
    /// </summary>
    Vector3 GetNormalVector(Vector3 p1, Vector3 p2, Vector3 p3)
    {
        return Vector3.Cross(p3 - p1, p2 - p1);
    }

    void Start()
    {
        // Initialize the projection spheres for rotation feedback.
        projectionSphereLeft = Instantiate(SpherePrefab);
        projectionSphereRight = Instantiate(SpherePrefab);

        projectionSphereLeft.SetActive(false);
        projectionSphereRight.SetActive(false);

        ResolveCameraOffsetTransform();
        SpawnAtPlaceholder();
    }

    private void ResolveCameraOffsetTransform()
    {
        var xrOrigin = FindAnyObjectByType<Unity.XR.CoreUtils.XROrigin>();
        if (xrOrigin != null && xrOrigin.CameraFloorOffsetObject != null)
        {
            cameraOffsetTransform = xrOrigin.CameraFloorOffsetObject.transform;
            return;
        }
        Camera cam = Camera.main;
        if (cam != null && cam.transform.parent != null)
            cameraOffsetTransform = cam.transform.parent;
    }

    private Vector3 JointToWorld(Vector3 sessionPos)
    {
        if (cameraOffsetTransform != null)
            return cameraOffsetTransform.TransformPoint(sessionPos);
        return sessionPos;
    }

    private void SpawnAtPlaceholder()
    {
        if (whiteboardPlaceholder == null)
            whiteboardPlaceholder = GameObject.Find("JournalChairTable/JournalTable/WhiteboardPlaceholder")?.transform;

        if (whiteboardPlaceholder == null)
        {
            Debug.LogWarning("[WhiteboardUtils] WhiteboardPlaceholder not found — whiteboard not spawned.");
            return;
        }

        BoxCollider box = whiteboardPlaceholder.GetComponent<BoxCollider>();
        Vector2 size;
        Vector3 center;
        if (box != null)
        {
            size   = new Vector2(box.bounds.size.x, box.bounds.size.z);
            center = box.bounds.center;
        }
        else
        {
            Debug.LogWarning("[WhiteboardUtils] WhiteboardPlaceholder has no BoxCollider — falling back to lossyScale.");
            size   = new Vector2(whiteboardPlaceholder.lossyScale.x, whiteboardPlaceholder.lossyScale.z);
            center = whiteboardPlaceholder.position;
        }

        SpawnAligned(center, whiteboardPlaceholder.rotation, size);
    }

    /// <summary>
    /// When true, manual gesture controls (middle-pinch create, pinky-pinch reset,
    /// dual-pinch rotate) are suppressed — the JournalSessionManager owns the whiteboard.
    /// </summary>
    [HideInInspector] public bool suppressManualGestures;

    void Update()
    {
        // Skip manual gestures entirely during a managed journal session
        if (suppressManualGestures) return;

        // Lazily acquire the hand subsystem
        if (handSubsystem == null || !handSubsystem.running)
        {
            handSubsystem = WhiteboardPen.GetHandSubsystem();
            if (handSubsystem == null) return;
        }

        XRHand leftHand = handSubsystem.leftHand;
        XRHand rightHand = handSubsystem.rightHand;

        if (!leftHand.isTracked || !rightHand.isTracked) return;

        // --- Get required joint poses and convert to world space ---
        if (!rightHand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out Pose rightThumbTipPose)) return;
        if (!rightHand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out Pose rightIndexTipPose)) return;
        if (!leftHand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out Pose leftThumbTipPose)) return;
        if (!leftHand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out Pose leftIndexTipPose)) return;
        Vector3 rightThumbTip  = JointToWorld(rightThumbTipPose.position);
        Vector3 rightIndexTip  = JointToWorld(rightIndexTipPose.position);
        Vector3 leftThumbTip   = JointToWorld(leftThumbTipPose.position);
        Vector3 leftIndexTip   = JointToWorld(leftIndexTipPose.position);

        // ---- Both hands index-finger pinch → rotate / reposition the board ----
        bool leftIndexPinch = IsPinching(leftThumbTip, leftIndexTip);
        bool rightIndexPinch = IsPinching(rightThumbTip, rightIndexTip);

        if (leftIndexPinch && rightIndexPinch)
        {
            projectionSphereLeft.SetActive(true);
            projectionSphereRight.SetActive(true);

            Vector3 leftThumbIndexMidpoint  = (leftThumbTip + leftIndexTip) / 2;
            Vector3 rightThumbIndexMidpoint = (rightThumbTip + rightIndexTip) / 2;

            // Get the index intermediate joint for ray direction
            if (leftHand.GetJoint(XRHandJointID.IndexIntermediate).TryGetPose(out Pose leftIndexIntermPose))
            {
                Vector3 leftDirection = leftIndexTip - JointToWorld(leftIndexIntermPose.position);

                if (Physics.Raycast(leftThumbIndexMidpoint, leftDirection, out touch, ADJUSTMENT_DISTANCE, 1 << BOARD_LAYER))
                {
                    whiteboardTransform = touch.transform;
                    whiteboardTransform.gameObject.GetComponent<Whiteboard>().isActive = false;
                }
            }

            if (whiteboardTransform != null)
            {
                Vector3 projLeft = Vector3.ProjectOnPlane(leftThumbIndexMidpoint - whiteboardTransform.position, whiteboardTransform.up) + whiteboardTransform.position;
                Vector3 projRight = Vector3.ProjectOnPlane(rightThumbIndexMidpoint - whiteboardTransform.position, whiteboardTransform.up) + whiteboardTransform.position;

                whiteboardTransform.position = whiteboardTransform.position + leftThumbIndexMidpoint - projLeft;

                Vector3 newNormal = GetNormalVector(leftThumbIndexMidpoint - Vector3.down, leftThumbIndexMidpoint, rightThumbIndexMidpoint);

                whiteboardTransform.LookAt(newNormal + whiteboardTransform.position);
                whiteboardTransform.Rotate(new Vector3(90, 0, 0), Space.Self);

                projectionSphereLeft.transform.position = projLeft;
                projectionSphereRight.transform.position = projRight;
            }
        }
        else
        {
            if (whiteboardTransform != null)
            {
                whiteboardTransform.gameObject.GetComponent<Whiteboard>().isActive = true;
                whiteboardTransform = null;
            }
            projectionSphereLeft.SetActive(false);
            projectionSphereRight.SetActive(false);
        }
    }

    /// <summary>
    /// Returns true when thumb tip and finger tip are close enough to count as a pinch.
    /// </summary>
    private bool IsPinching(Vector3 thumbTip, Vector3 fingerTip)
    {
        return Vector3.Distance(thumbTip, fingerTip) < PINCH_THRESHOLD;
    }

    // ================================================================
    // ALIGNED SPAWNING (used by JournalSessionManager)
    // ================================================================

    /// <summary>Fired when a whiteboard is created (manual or aligned).</summary>
    public event Action<GameObject> OnWhiteboardSpawned;

    /// <summary>
    /// Spawns a horizontal whiteboard at a specific world position, rotation,
    /// and size — used for table-aligned journal mode.
    ///
    /// The whiteboard is oriented face-up (writing surface = +Y) so the user
    /// writes on it like paper on a desk.
    /// </summary>
    /// <param name="position">World-space centre of the whiteboard.</param>
    /// <param name="rotation">World-space rotation (forward = user's facing direction).</param>
    /// <param name="size">Width (X) and depth (Z) in metres.</param>
    /// <returns>The instantiated whiteboard GameObject.</returns>
    public GameObject SpawnAligned(Vector3 position, Quaternion rotation, Vector2 size)
    {
        if (whiteboard != null)
        {
            Destroy(whiteboard);
        }

        whiteboard = Instantiate(WhiteboardPrefab, position, Quaternion.identity);

        // The prefab uses a Unity Plane mesh (10×10 units) with default scale 0.1
        // on each axis (making 1 unit = 1 metre). Scale to match requested size.
        float scaleX = size.x / 10f;
        float scaleZ = size.y / 10f;
        whiteboard.transform.localScale = new Vector3(scaleX, 0.1f / 10f, scaleZ);

        // The Unity Plane mesh faces +Y. The rotation from SurfaceDetector is
        // LookRotation(userForward, Vector3.up) which keeps +Y pointing up.
        // This means the plane is already horizontal / face-up — correct for
        // table-top writing.
        whiteboard.transform.rotation = rotation;

        // Slight offset above the table surface to prevent z-fighting
        whiteboard.transform.position = position + Vector3.up * 0.001f;

        whiteboard.GetComponent<Whiteboard>().Initialize();

        Debug.Log($"[WhiteboardUtils] SpawnAligned: pos={whiteboard.transform.position}, " +
                  $"rot={whiteboard.transform.rotation.eulerAngles}, " +
                  $"scale={whiteboard.transform.localScale}, size={size}");

        OnWhiteboardSpawned?.Invoke(whiteboard);

        return whiteboard;
    }
}