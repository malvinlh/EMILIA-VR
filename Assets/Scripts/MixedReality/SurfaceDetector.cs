using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;

/// <summary>
/// Detects a real-world table surface using a "palms-flat" gesture — similar
/// to Meta Quest 3's Surface Keyboard detection.
///
/// Both palms must face down on a surface for a configurable hold duration.
/// The table plane is computed from fingertip + palm joint positions.
/// A visual progress indicator (sphere) scales and colour-lerps during the hold.
/// </summary>
public class SurfaceDetector : MonoBehaviour
{
    [Header("Detection Parameters")]
    [Tooltip("How long (seconds) both palms must be flat before detection confirms.")]
    [Range(0.5f, 5f)]
    public float holdDuration = 2f;

    [Tooltip("Max angle (degrees) between palm-down normal and world-down for 'flat' classification.")]
    [Range(5f, 30f)]
    public float palmDownAngleThreshold = 20f;

    [Tooltip("Max vertical spread (metres) allowed among fingertips relative to palm height.")]
    [Range(0.01f, 0.08f)]
    public float fingertipYTolerance = 0.03f;

    [Header("Table Size Estimation")]
    [Tooltip("Multiplier applied to palm-to-palm distance to estimate table width.")]
    public float widthMultiplier = 1.5f;

    [Tooltip("Default table depth (metres) when only two hands are available.")]
    public float defaultDepth = 0.4f;

    [Tooltip("Minimum whiteboard size on any axis (metres).")]
    public Vector2 minSize = new Vector2(0.3f, 0.2f);

    [Tooltip("Maximum whiteboard size on any axis (metres).")]
    public Vector2 maxSize = new Vector2(0.8f, 0.6f);

    [Header("Visual Feedback")]
    [Tooltip("Prefab for the progress indicator sphere. Reuses MarkingSphere from WhiteboardUtils.")]
    public GameObject indicatorPrefab;

    [Tooltip("Layer for objects visible during passthrough. " +
             "Must match PassthroughManager.passthroughUILayer.")]
    public int passthroughUILayer = 31;

    // ── Events ──────────────────────────────────────────────────────────
    public event Action<float> OnDetectionProgress;    // 0..1
    public event Action<TablePlane> OnTableDetected;
    public event Action OnDetectionLost;

    // ── State ───────────────────────────────────────────────────────────
    private XRHandSubsystem handSubsystem;
    private float holdTimer;
    private bool wasDetecting;

    private GameObject indicator;
    private static readonly Vector3 INDICATOR_MIN = new Vector3(0.01f, 0.01f, 0.01f);
    private static readonly Vector3 INDICATOR_MAX = new Vector3(0.05f, 0.05f, 0.05f);

    public bool IsDetecting { get; private set; }
    public bool HasDetected { get; private set; }

    /// <summary>Detected table surface data.</summary>
    public struct TablePlane
    {
        public Vector3 position;
        public Quaternion rotation;
        public Vector2 size;
    }

    // Fingertip joint IDs for both hands
    private static readonly XRHandJointID[] FingertipJoints = new[]
    {
        XRHandJointID.ThumbTip,
        XRHandJointID.IndexTip,
        XRHandJointID.MiddleTip,
        XRHandJointID.RingTip,
        XRHandJointID.LittleTip
    };

    private void Update()
    {
        if (HasDetected) return;

        // Acquire hand subsystem
        if (handSubsystem == null || !handSubsystem.running)
        {
            handSubsystem = WhiteboardPen.GetHandSubsystem();
            if (handSubsystem == null) return;
        }

        XRHand leftHand = handSubsystem.leftHand;
        XRHand rightHand = handSubsystem.rightHand;

        if (!leftHand.isTracked || !rightHand.isTracked)
        {
            ResetDetection();
            return;
        }

        // Check if both palms are flat on a surface
        bool leftFlat = IsPalmFlat(leftHand, out Vector3 leftPalmPos);
        bool rightFlat = IsPalmFlat(rightHand, out Vector3 rightPalmPos);

        if (leftFlat && rightFlat)
        {
            holdTimer += Time.deltaTime;
            float progress = Mathf.Clamp01(holdTimer / holdDuration);

            IsDetecting = true;
            OnDetectionProgress?.Invoke(progress);

            // Update visual indicator
            Vector3 midpoint = (leftPalmPos + rightPalmPos) / 2f;
            UpdateIndicator(midpoint, progress);

            if (holdTimer >= holdDuration)
            {
                // Detection complete — compute table plane
                TablePlane table = ComputeTablePlane(leftHand, rightHand, leftPalmPos, rightPalmPos);
                HasDetected = true;
                IsDetecting = false;
                DestroyIndicator();
                Debug.Log($"[SurfaceDetector] Table detected at {table.position}, size={table.size}");
                OnTableDetected?.Invoke(table);
            }
        }
        else
        {
            if (wasDetecting)
            {
                ResetDetection();
                OnDetectionLost?.Invoke();
            }
        }

        wasDetecting = IsDetecting;
    }

    /// <summary>
    /// Check if a hand's palm faces down and all fingertips are within
    /// tolerance of the palm height (indicating the hand is resting flat).
    /// </summary>
    private bool IsPalmFlat(XRHand hand, out Vector3 palmPosition)
    {
        palmPosition = Vector3.zero;

        // Get palm pose
        XRHandJoint palmJoint = hand.GetJoint(XRHandJointID.Palm);
        if (!palmJoint.TryGetPose(out Pose palmPose))
            return false;

        palmPosition = palmPose.position;

        // Check palm normal faces down.
        // The palm joint's "down" direction (negative Y in palm local space)
        // should align with world down when the palm is face-down on a table.
        Vector3 palmDown = palmPose.rotation * (-Vector3.up);
        float angle = Vector3.Angle(palmDown, Vector3.down);

        if (angle > palmDownAngleThreshold)
            return false;

        // Check all fingertips are near the palm Y-height (flat hand, not curled)
        float palmY = palmPosition.y;

        foreach (var jointID in FingertipJoints)
        {
            XRHandJoint joint = hand.GetJoint(jointID);
            if (!joint.TryGetPose(out Pose tipPose))
                return false;

            float yDiff = Mathf.Abs(tipPose.position.y - palmY);
            if (yDiff > fingertipYTolerance)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Compute the table plane from hand joint positions.
    /// </summary>
    private TablePlane ComputeTablePlane(XRHand leftHand, XRHand rightHand,
        Vector3 leftPalmPos, Vector3 rightPalmPos)
    {
        // Collect all fingertip + palm positions for plane fitting
        List<Vector3> points = new List<Vector3>();
        points.Add(leftPalmPos);
        points.Add(rightPalmPos);

        CollectFingertipPositions(leftHand, points);
        CollectFingertipPositions(rightHand, points);

        // Average Y as table height
        float avgY = 0f;
        foreach (var p in points)
            avgY += p.y;
        avgY /= points.Count;

        // Table center (XZ midpoint between palms, Y = average height)
        Vector3 center = new Vector3(
            (leftPalmPos.x + rightPalmPos.x) / 2f,
            avgY,
            (leftPalmPos.z + rightPalmPos.z) / 2f
        );

        // Estimate size from palm distance
        float palmDistance = Vector3.Distance(
            new Vector3(leftPalmPos.x, 0f, leftPalmPos.z),
            new Vector3(rightPalmPos.x, 0f, rightPalmPos.z)
        );

        float width = Mathf.Clamp(palmDistance * widthMultiplier, minSize.x, maxSize.x);
        float depth = Mathf.Clamp(defaultDepth, minSize.y, maxSize.y);

        // Rotation: face up (normal = Vector3.up), oriented so the "forward"
        // axis points from the user toward the table (using camera forward projected
        // onto the horizontal plane).
        Camera cam = Camera.main;
        Vector3 userForward = cam != null
            ? Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized
            : Vector3.forward;

        Quaternion rotation = Quaternion.LookRotation(userForward, Vector3.up);

        return new TablePlane
        {
            position = center,
            rotation = rotation,
            size = new Vector2(width, depth)
        };
    }

    private void CollectFingertipPositions(XRHand hand, List<Vector3> points)
    {
        foreach (var jointID in FingertipJoints)
        {
            XRHandJoint joint = hand.GetJoint(jointID);
            if (joint.TryGetPose(out Pose pose))
                points.Add(pose.position);
        }
    }

    private void UpdateIndicator(Vector3 position, float progress)
    {
        if (indicatorPrefab == null) return;

        if (indicator == null)
        {
            indicator = Instantiate(indicatorPrefab, position, Quaternion.identity);
            // Put on passthrough UI layer so it's visible during passthrough
            PassthroughManager.SetLayerRecursive(indicator, passthroughUILayer);
        }

        indicator.transform.position = position;
        indicator.transform.localScale = Vector3.Lerp(INDICATOR_MIN, INDICATOR_MAX, progress);

        var renderer = indicator.GetComponent<Renderer>();
        if (renderer != null)
        {
            renderer.material.color = Color.Lerp(Color.red, Color.green, progress);
        }
    }

    private void DestroyIndicator()
    {
        if (indicator != null)
        {
            Destroy(indicator);
            indicator = null;
        }
    }

    private void ResetDetection()
    {
        holdTimer = 0f;
        IsDetecting = false;
        DestroyIndicator();
    }

    /// <summary>
    /// Reset the detector to allow a new detection cycle.
    /// </summary>
    public void ResetState()
    {
        HasDetected = false;
        ResetDetection();
    }
}
