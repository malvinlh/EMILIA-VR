using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
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

    private bool isCalibrating = false;

    private RaycastHit touch;

    private float sizeCalibrationTimer;
    private GameObject calibrationIndicator;

    private Vector3 sizeCalibrationInitialPoint;

    private GameObject projectionSphereLeft;
    private GameObject projectionSphereRight;

    private Transform whiteboardTransform;

    private const float ADJUSTMENT_DISTANCE = .2f;
    private const int BOARD_LAYER = 10;
    private const float PINCH_THRESHOLD = 0.02f;

    private Vector3 MIN_SIZE = new Vector3(.01f, .01f, .01f);
    private Vector3 MAX_SIZE = new Vector3(.05f, .05f, .05f);

    private Color MIN_COLOR = Color.red;
    private Color MAX_COLOR = Color.green;

    // Need to hold middle pinch for 2 seconds to start creating a new board
    private const float CALIBRATION_TIME = 2f;

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
    }

    void Update()
    {
        // Lazily acquire the hand subsystem
        if (handSubsystem == null || !handSubsystem.running)
        {
            handSubsystem = WhiteboardPen.GetHandSubsystem();
            if (handSubsystem == null) return;
        }

        XRHand leftHand = handSubsystem.leftHand;
        XRHand rightHand = handSubsystem.rightHand;

        if (!leftHand.isTracked || !rightHand.isTracked) return;

        // --- Get required joint poses (right hand) ---
        if (!rightHand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out Pose rightThumbTipPose)) return;
        if (!rightHand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out Pose rightIndexTipPose)) return;

        // --- Get required joint poses (left hand) ---
        if (!leftHand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out Pose leftThumbTipPose)) return;
        if (!leftHand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out Pose leftIndexTipPose)) return;
        if (!leftHand.GetJoint(XRHandJointID.MiddleTip).TryGetPose(out Pose leftMiddleTipPose)) return;
        if (!leftHand.GetJoint(XRHandJointID.LittleTip).TryGetPose(out Pose leftLittleTipPose)) return;

        // ---- Left pinky pinch → reset the scene ----
        if (IsPinching(leftThumbTipPose.position, leftLittleTipPose.position))
        {
            SceneManager.LoadScene("WhiteboardScene-XRI");
        }

        // ---- Left middle-finger pinch → calibration / board creation ----
        if (IsPinching(leftThumbTipPose.position, leftMiddleTipPose.position))
        {
            // Point between thumb tip and middle tip
            Vector3 thumbMiddleMidpoint = (leftThumbTipPose.position + leftMiddleTipPose.position) / 2;

            sizeCalibrationTimer += Time.deltaTime;

            if (calibrationIndicator == null)
            {
                calibrationIndicator = Instantiate(SpherePrefab, thumbMiddleMidpoint, Quaternion.identity);
            }
            else
            {
                calibrationIndicator.transform.localScale = Vector3.Lerp(MIN_SIZE, MAX_SIZE, sizeCalibrationTimer / CALIBRATION_TIME);
                calibrationIndicator.GetComponent<Renderer>().material.color = Color.Lerp(MIN_COLOR, MAX_COLOR, sizeCalibrationTimer / CALIBRATION_TIME);
            }

            if (sizeCalibrationTimer >= CALIBRATION_TIME)
            {
                if (!isCalibrating)
                {
                    sizeCalibrationInitialPoint = thumbMiddleMidpoint;
                    whiteboard = Instantiate(WhiteboardPrefab, sizeCalibrationInitialPoint, Quaternion.identity);
                }

                float vertDiff = sizeCalibrationInitialPoint.y - thumbMiddleMidpoint.y;
                float horizDiff = Mathf.Sqrt(
                    Mathf.Pow((sizeCalibrationInitialPoint.x - thumbMiddleMidpoint.x), 2) +
                    Mathf.Pow((sizeCalibrationInitialPoint.z - thumbMiddleMidpoint.z), 2));

                whiteboard.transform.localScale = new Vector3(horizDiff, .1f, Mathf.Abs(vertDiff)) / 10f;

                Vector3 normalVec = GetNormalVector(sizeCalibrationInitialPoint, sizeCalibrationInitialPoint + Vector3.down, thumbMiddleMidpoint);

                whiteboard.transform.LookAt(whiteboard.transform.position + normalVec);
                whiteboard.transform.Rotate(new Vector3(90, 0, 0), Space.Self);

                whiteboard.transform.position = (sizeCalibrationInitialPoint + thumbMiddleMidpoint) / 2;

                whiteboard.GetComponent<Whiteboard>().Initialize();

                isCalibrating = true;
            }
        }
        else
        {
            sizeCalibrationInitialPoint = Vector3.zero;
            sizeCalibrationTimer = 0;
            isCalibrating = false;
            Destroy(calibrationIndicator);
            calibrationIndicator = null;
        }

        // ---- Both hands index-finger pinch → rotate / reposition the board ----
        bool leftIndexPinch = IsPinching(leftThumbTipPose.position, leftIndexTipPose.position);
        bool rightIndexPinch = IsPinching(rightThumbTipPose.position, rightIndexTipPose.position);

        if (leftIndexPinch && rightIndexPinch)
        {
            projectionSphereLeft.SetActive(true);
            projectionSphereRight.SetActive(true);

            Vector3 leftThumbIndexMidpoint = (leftThumbTipPose.position + leftIndexTipPose.position) / 2;
            Vector3 rightThumbIndexMidpoint = (rightThumbTipPose.position + rightIndexTipPose.position) / 2;

            // Get the index intermediate joint for ray direction
            if (leftHand.GetJoint(XRHandJointID.IndexIntermediate).TryGetPose(out Pose leftIndexIntermPose))
            {
                Vector3 leftDirection = leftIndexTipPose.position - leftIndexIntermPose.position;

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

        // The default plane mesh is 10×10 units, and the prefab is scaled 0.1 on each axis
        // to make 1 unit = 1 metre.  We need to set the scale so the plane matches the
        // requested size in metres.
        // Formula: localScale.axis = desiredMetres / WHITEBOARD_SCALE(10)
        float scaleX = size.x / 10f;
        float scaleZ = size.y / 10f;
        whiteboard.transform.localScale = new Vector3(scaleX, 0.1f / 10f, scaleZ);

        // Orient face-up: the default Unity plane faces +Y, so we apply the
        // user-facing rotation directly (LookRotation already has up = Vector3.up).
        whiteboard.transform.rotation = rotation;
        // Flip so the plane's rendered face (+Y) points up for the writer
        whiteboard.transform.Rotate(Vector3.right, 90f, Space.Self);

        // Slight offset above the table surface to prevent z-fighting
        whiteboard.transform.position = position + Vector3.up * 0.001f;

        whiteboard.GetComponent<Whiteboard>().Initialize();

        OnWhiteboardSpawned?.Invoke(whiteboard);

        return whiteboard;
    }
}