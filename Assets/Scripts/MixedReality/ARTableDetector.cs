using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.Hands;
using Unity.XR.CoreUtils;

/// <summary>
/// Detects a real-world table using AR Foundation plane data (Meta Quest 3 Scene Model)
/// combined with a palm-flat hand gesture for user confirmation.
///
/// Primary path: ARPlaneManager provides actual table geometry; hands confirm which plane.
/// Fallback path: If no AR planes are available (Space Setup not done), reverts to
/// hand-only detection identical to the legacy SurfaceDetector approach.
/// </summary>
public class ARTableDetector : MonoBehaviour
{
    // ================================================================
    // INSPECTOR
    // ================================================================

    [Header("AR References")]
    [Tooltip("ARPlaneManager for real table geometry. Auto-found if null.")]
    public ARPlaneManager planeManager;

    [Header("Coordinate Space")]
    [Tooltip("XR Origin root transform. Required to convert hand joint positions " +
             "from session/tracking space to world space. If null, positions are " +
             "assumed to already be in world space (only valid if XR Origin is at origin).")]
    public Transform xrOrigin;

    [Header("Table Height Heuristics")]
    [Tooltip("Min height above floor (metres) to classify as table.")]
    public float minTableHeight = 0.45f;
    [Tooltip("Max height above floor (metres) to classify as table.")]
    public float maxTableHeight = 1.10f;
    [Tooltip("How close (metres) hand Y must be to a plane Y to match.")]
    public float handPlaneMatchRadius = 0.15f;

    [Header("Hand Confirmation")]
    [Tooltip("Max angle between palm-down and world-down.")]
    [Range(5f, 30f)]
    public float palmDownAngleThreshold = 20f;
    [Tooltip("Max vertical spread among fingertips relative to palm Y.")]
    [Range(0.01f, 0.08f)]
    public float fingertipYTolerance = 0.03f;
    [Tooltip("How long (seconds) both palms must be flat to confirm.")]
    [Range(0.5f, 5f)]
    public float holdDuration = 1.5f;

    [Header("Fallback (No AR Planes)")]
    [Tooltip("Seconds to wait for AR planes before falling back to hand-only.")]
    public float planeFallbackTimeout = 5f;
    [Tooltip("Multiplier on palm distance for fallback width estimate.")]
    public float fallbackWidthMultiplier = 1.5f;
    [Tooltip("Default depth for fallback detection.")]
    public float fallbackDefaultDepth = 0.4f;

    [Header("Fallback Size Clamps")]
    public Vector2 minSize = new Vector2(0.3f, 0.2f);
    public Vector2 maxSize = new Vector2(0.8f, 0.6f);

    [Header("Fallback Surface Check")]
    [Tooltip("Max vertical drift (metres) of the palm midpoint during the hold before " +
             "the confirmation resets. A hand resting on a surface stays nearly " +
             "stationary in Y; a floating hand drifts. Only used in fallback (no AR planes) mode.")]
    [Range(0.005f, 0.10f)]
    public float fallbackStabilityThreshold = 0.015f;

    // ================================================================
    // EVENTS
    // ================================================================

    public event Action<List<CandidatePlane>> OnCandidatesUpdated;
    public event Action<float> OnConfirmationProgress;
    public event Action<DetectedTable> OnTableConfirmed;
    public event Action OnConfirmationLost;

    // ================================================================
    // DATA TYPES
    // ================================================================

    /// <summary>Enhanced detection result with AR data + user pose.</summary>
    public struct DetectedTable
    {
        public Vector3 position;
        public Quaternion rotation;
        public Vector2 size;
        public Vector3 userHeadPosition;
        public Vector3 userForward;
        public ARPlane sourcePlane;   // null if hand-only fallback

        /// <summary>
        /// Average camera/eye height (world Y) sampled across the entire hold period.
        /// More accurate than a single-frame snapshot for calibration.
        /// </summary>
        public float avgEyeY;

        /// <summary>
        /// Average palm-surface Y (world Y) sampled across the hold period.
        /// Palm midpoint Y minus palm thickness — used as the true table surface
        /// reference, more accurate than the AR plane Y for height calibration.
        /// </summary>
        public float avgPalmSurfaceY;
    }

    public struct CandidatePlane
    {
        public ARPlane plane;
        public float score;
    }

    // ================================================================
    // STATE
    // ================================================================

    private XRHandSubsystem handSubsystem;
    private float holdTimer;
    private bool wasConfirming;
    private float planeScanTimer;
    private bool usingFallback;
    private float floorHeight = float.MaxValue;
    private bool floorDetected;

    private ARPlane matchedPlane;
    private List<CandidatePlane> candidates = new List<CandidatePlane>();
    private float holdMinY = float.MaxValue;
    private float holdMaxY = float.MinValue;

    // Accumulators for eye/surface averaging during hold
    private float holdEyeYAccum;
    private float holdPalmYAccum;
    private int holdSampleCount;
    // Fingertip accumulator — all 10 fingertip joints averaged across hold frames.
    // Fingertip joints sit ~3-5mm above the physical contact surface, much closer
    // than the palm centre (~15-20mm), giving a more accurate table-surface estimate.
    private float holdFingertipYAccum;
    private int holdFingertipSampleCount;

    // Cached reference to the Camera Floor Offset Object — the transform that
    // converts session/tracking space to world space through the same chain as
    // the camera. Using XROrigin.root.TransformPoint() skips the CameraYOffset,
    // causing a vertical mismatch between camera and hand positions.
    private Transform cameraOffsetTransform;

    public bool IsConfirming { get; private set; }
    public bool HasConfirmed { get; private set; }

    private static readonly XRHandJointID[] FingertipJoints =
    {
        XRHandJointID.ThumbTip,
        XRHandJointID.IndexTip,
        XRHandJointID.MiddleTip,
        XRHandJointID.RingTip,
        XRHandJointID.LittleTip
    };

    // ================================================================
    // LIFECYCLE
    // ================================================================

    private void OnEnable()
    {
        planeScanTimer = 0f;
        usingFallback = false;
        floorHeight = float.MaxValue;
        floorDetected = false;
        cameraOffsetTransform = null;

        // Auto-find XR Origin if not assigned
        if (xrOrigin == null)
        {
            var origin = FindAnyObjectByType<XROrigin>();
            if (origin != null)
            {
                xrOrigin = origin.transform;
                Debug.Log($"[ARTableDetector] Auto-found XR Origin: {xrOrigin.name}");
            }
        }

        // Resolve the Camera Floor Offset Object — this is the correct transform
        // for session-to-world conversion because it includes the CameraYOffset
        // that the XR Origin root's Transform.TransformPoint() misses.
        ResolveCameraOffsetTransform();
    }

    private void ResolveCameraOffsetTransform()
    {
        // Try to get it from the XROrigin component
        if (xrOrigin != null)
        {
            var xrOriginComp = xrOrigin.GetComponent<XROrigin>();
            if (xrOriginComp != null && xrOriginComp.CameraFloorOffsetObject != null)
            {
                cameraOffsetTransform = xrOriginComp.CameraFloorOffsetObject.transform;
                Debug.Log($"[ARTableDetector] Using CameraFloorOffsetObject: " +
                          $"{cameraOffsetTransform.name} (Y={cameraOffsetTransform.position.y:F2})");
                return;
            }
        }

        // Fallback: walk up from the main camera to find its parent
        Camera cam = Camera.main;
        if (cam != null && cam.transform.parent != null)
        {
            cameraOffsetTransform = cam.transform.parent;
            Debug.Log($"[ARTableDetector] Using camera parent as offset: " +
                      $"{cameraOffsetTransform.name} (Y={cameraOffsetTransform.position.y:F2})");
        }
    }

    private void Update()
    {
        if (HasConfirmed) return;

        // Acquire hand subsystem
        if (handSubsystem == null || !handSubsystem.running)
        {
            handSubsystem = WhiteboardPen.GetHandSubsystem();
            if (handSubsystem == null) return;
        }

        // Scan AR planes for table candidates
        ScanPlanes();

        // Hand confirmation
        XRHand leftHand = handSubsystem.leftHand;
        XRHand rightHand = handSubsystem.rightHand;

        if (!leftHand.isTracked || !rightHand.isTracked)
        {
            ResetConfirmation();
            return;
        }

        bool leftFlat = IsPalmFlat(leftHand, out Vector3 leftPalmPos);
        bool rightFlat = IsPalmFlat(rightHand, out Vector3 rightPalmPos);

        if (leftFlat && rightFlat)
        {
            // Try to match palms to an AR plane candidate
            if (!usingFallback)
                matchedPlane = FindMatchingPlane(leftPalmPos, rightPalmPos);

            holdTimer += Time.deltaTime;
            float progress = Mathf.Clamp01(holdTimer / holdDuration);

            // Accumulate eye and palm heights every frame for averaging
            Camera camSample = Camera.main;
            if (camSample != null) holdEyeYAccum += camSample.transform.position.y;
            holdPalmYAccum += (leftPalmPos.y + rightPalmPos.y) * 0.5f;
            holdSampleCount++;

            // Accumulate all 10 fingertip world-space Y values.
            // Fingertip joints are ~3-5mm above the physical surface, far closer than
            // the palm centre (~15-20mm), so their average gives a more accurate
            // table surface estimate with less bias.
            foreach (var jointID in FingertipJoints)
            {
                if (leftHand.GetJoint(jointID).TryGetPose(out Pose lTip))
                {
                    holdFingertipYAccum += SessionToWorld(lTip.position).y;
                    holdFingertipSampleCount++;
                }
                if (rightHand.GetJoint(jointID).TryGetPose(out Pose rTip))
                {
                    holdFingertipYAccum += SessionToWorld(rTip.position).y;
                    holdFingertipSampleCount++;
                }
            }

            IsConfirming = true;
            OnConfirmationProgress?.Invoke(progress);

            if (holdTimer >= holdDuration)
            {
                HasConfirmed = true;
                IsConfirming = false;

                // Compute averages — more stable than single-frame snapshot
                Camera cam = Camera.main;
                float avgEyeY = holdSampleCount > 0
                    ? holdEyeYAccum / holdSampleCount
                    : (cam != null ? cam.transform.position.y : 0f);
                float avgPalmY = holdSampleCount > 0
                    ? holdPalmYAccum / holdSampleCount
                    : (leftPalmPos.y + rightPalmPos.y) * 0.5f;

                DetectedTable result;
                if (matchedPlane != null)
                    result = BuildFromARPlane(matchedPlane, leftPalmPos, rightPalmPos);
                else
                    result = BuildFromHands(leftHand, rightHand, leftPalmPos, rightPalmPos);

                // Use fingertip average for surface Y if we have samples (preferred).
                // Fingertip joints sit ~3mm above the physical contact surface, giving
                // a much smaller and more consistent offset than the palm centre (~15-20mm).
                // Fall back to palm average if no fingertip data.
                float avgFingertipY = holdFingertipSampleCount > 0
                    ? holdFingertipYAccum / holdFingertipSampleCount
                    : avgPalmY;
                float surfaceY = holdFingertipSampleCount > 0
                    ? avgFingertipY - 0.003f   // 3mm fingertip joint-to-skin offset
                    : avgPalmY - 0.012f;       // fallback: palm centre offset

                result.avgEyeY = avgEyeY;
                result.avgPalmSurfaceY = surfaceY;

                Debug.Log($"[ARTableDetector] Table confirmed at {result.position}, " +
                          $"size={result.size}, AR={matchedPlane != null}, " +
                          $"avgEyeY={avgEyeY:F3}m, surfaceY={result.avgPalmSurfaceY:F3}m " +
                          $"(frameSamples={holdSampleCount}, fingertipSamples={holdFingertipSampleCount})");
                OnTableConfirmed?.Invoke(result);
            }
        }
        else
        {
            if (wasConfirming)
            {
                ResetConfirmation();
                OnConfirmationLost?.Invoke();
            }
        }

        wasConfirming = IsConfirming;
    }

    // ================================================================
    // AR PLANE SCANNING
    // ================================================================

    private void ScanPlanes()
    {
        if (planeManager == null || usingFallback) return;

        planeScanTimer += Time.deltaTime;

        // Check for fallback timeout
        if (planeManager.trackables.count == 0)
        {
            if (planeScanTimer >= planeFallbackTimeout)
            {
                Debug.LogWarning("[ARTableDetector] No AR planes found — switching to hand-only fallback.");
                usingFallback = true;
            }
            return;
        }

        // Detect floor height from lowest horizontal plane
        DetectFloorHeight();

        // Score table candidates
        candidates.Clear();
        Camera cam = Camera.main;
        Vector3 headPos = cam != null ? cam.transform.position : Vector3.zero;

        foreach (var plane in planeManager.trackables)
        {
            if (plane.alignment != PlaneAlignment.HorizontalUp) continue;
            if (plane.subsumedBy != null) continue;

            float planeY = plane.transform.position.y;

            // If we have floor reference, filter by height above floor
            if (floorDetected)
            {
                float heightAboveFloor = planeY - floorHeight;
                if (heightAboveFloor < minTableHeight || heightAboveFloor > maxTableHeight)
                    continue;
            }

            float score = ScorePlane(plane, headPos);
            candidates.Add(new CandidatePlane { plane = plane, score = score });
        }

        // Sort by score descending
        candidates.Sort((a, b) => b.score.CompareTo(a.score));

        if (candidates.Count > 0)
            OnCandidatesUpdated?.Invoke(candidates);
    }

    private void DetectFloorHeight()
    {
        if (floorDetected) return;

        float lowestY = float.MaxValue;
        foreach (var plane in planeManager.trackables)
        {
            if (plane.alignment != PlaneAlignment.HorizontalUp) continue;
            if (plane.transform.position.y < lowestY)
                lowestY = plane.transform.position.y;
        }

        if (lowestY < float.MaxValue)
        {
            floorHeight = lowestY;
            floorDetected = true;
            Debug.Log($"[ARTableDetector] Floor height detected: {floorHeight:F3}m");
        }
    }

    private float ScorePlane(ARPlane plane, Vector3 headPos)
    {
        float score = 0f;

        Vector3 planeCenter = plane.transform.position;

        // Proximity to user (closer is better, up to 3m)
        float dist = Vector3.Distance(
            new Vector3(planeCenter.x, 0f, planeCenter.z),
            new Vector3(headPos.x, 0f, headPos.z));
        score += Mathf.Clamp01(1f - dist / 3f) * 30f;

        // Size (larger planes score higher)
        float area = plane.size.x * plane.size.y;
        score += Mathf.Clamp(area, 0f, 2f) * 10f;

        // Height plausibility (0.65-0.85m above floor is typical desk)
        if (floorDetected)
        {
            float h = planeCenter.y - floorHeight;
            float idealH = 0.75f;
            float hDiff = Mathf.Abs(h - idealH);
            score += Mathf.Clamp01(1f - hDiff / 0.3f) * 20f;
        }

        // Classification bonus (if available on future firmware)
        if (plane.classifications.HasFlag(PlaneClassifications.Table))
            score += 50f;

        return score;
    }

    // ================================================================
    // PLANE MATCHING
    // ================================================================

    /// <summary>
    /// Find which candidate plane the user's palms are resting on.
    /// </summary>
    private ARPlane FindMatchingPlane(Vector3 leftPalm, Vector3 rightPalm)
    {
        float avgPalmY = (leftPalm.y + rightPalm.y) / 2f;

        ARPlane bestMatch = null;
        float bestScore = float.MinValue;

        foreach (var candidate in candidates)
        {
            float planeY = candidate.plane.transform.position.y;
            float yDiff = Mathf.Abs(avgPalmY - planeY);

            if (yDiff > handPlaneMatchRadius) continue;

            // Check if palms are within the plane's XZ bounds (rough check)
            Vector3 palmMid = (leftPalm + rightPalm) / 2f;
            Vector3 localPos = candidate.plane.transform.InverseTransformPoint(palmMid);
            Vector2 halfSize = candidate.plane.size / 2f;

            if (Mathf.Abs(localPos.x) <= halfSize.x + 0.1f &&
                Mathf.Abs(localPos.z) <= halfSize.y + 0.1f)
            {
                // Within bounds — use candidate score as priority
                if (candidate.score > bestScore)
                {
                    bestMatch = candidate.plane;
                    bestScore = candidate.score;
                }
            }
        }

        return bestMatch;
    }

    // ================================================================
    // TABLE BUILDING
    // ================================================================

    /// <summary>
    /// Build DetectedTable from an AR plane (primary path).
    /// Uses the plane's actual pose and size, adjusting rotation to face the user.
    /// </summary>
    private DetectedTable BuildFromARPlane(ARPlane plane, Vector3 leftPalm, Vector3 rightPalm)
    {
        Camera cam = Camera.main;
        Vector3 headPos = cam != null ? cam.transform.position : Vector3.zero;
        Vector3 headFwd = cam != null ? cam.transform.forward : Vector3.forward;

        // Use the palm midpoint as the "interaction center" on the plane,
        // rather than the plane's geometric center (which might be far off)
        Vector3 palmMid = (leftPalm + rightPalm) / 2f;
        Vector3 tableCenter = new Vector3(palmMid.x, plane.transform.position.y, palmMid.z);

        // Rotation: face the user (table-to-user direction)
        Vector3 tableToUser = headPos - tableCenter;
        tableToUser.y = 0f;
        if (tableToUser.sqrMagnitude < 0.01f)
        {
            tableToUser = -headFwd;
            tableToUser.y = 0f;
        }
        tableToUser.Normalize();
        Quaternion rotation = Quaternion.LookRotation(tableToUser, Vector3.up);

        return new DetectedTable
        {
            position = tableCenter,
            rotation = rotation,
            size = new Vector2(
                Mathf.Clamp(plane.size.x, minSize.x, maxSize.x),
                Mathf.Clamp(plane.size.y, minSize.y, maxSize.y)),
            userHeadPosition = headPos,
            userForward = headFwd,
            sourcePlane = plane
        };
    }

    /// <summary>
    /// Build DetectedTable from hand joints only (fallback path).
    /// Ported from legacy SurfaceDetector.ComputeTablePlane.
    /// </summary>
    private DetectedTable BuildFromHands(XRHand leftHand, XRHand rightHand,
        Vector3 leftPalmPos, Vector3 rightPalmPos)
    {
        Camera cam = Camera.main;
        Vector3 headPos = cam != null ? cam.transform.position : Vector3.zero;
        Vector3 headFwd = cam != null ? cam.transform.forward : Vector3.forward;

        // The XR palm joint is at the centre of the palm, ~12 mm above the
        // physical table surface. Using fingertip average skews even higher.
        // Subtract the palm-to-surface offset directly so the table Y matches
        // the actual contact surface (consistent with BuildFromARPlane).
        const float palmToSurface = 0.012f;
        float tableY = (leftPalmPos.y + rightPalmPos.y) / 2f - palmToSurface;

        Vector3 center = new Vector3(
            (leftPalmPos.x + rightPalmPos.x) / 2f,
            tableY,
            (leftPalmPos.z + rightPalmPos.z) / 2f
        );

        float palmDistance = Vector3.Distance(
            new Vector3(leftPalmPos.x, 0f, leftPalmPos.z),
            new Vector3(rightPalmPos.x, 0f, rightPalmPos.z));

        float width = Mathf.Clamp(palmDistance * fallbackWidthMultiplier, minSize.x, maxSize.x);
        float depth = Mathf.Clamp(fallbackDefaultDepth, minSize.y, maxSize.y);

        // Rotation: face the user (not camera forward)
        Vector3 tableToUser = headPos - center;
        tableToUser.y = 0f;
        if (tableToUser.sqrMagnitude < 0.01f)
        {
            tableToUser = -headFwd;
            tableToUser.y = 0f;
        }
        tableToUser.Normalize();
        Quaternion rotation = Quaternion.LookRotation(tableToUser, Vector3.up);

        return new DetectedTable
        {
            position = center,
            rotation = rotation,
            size = new Vector2(width, depth),
            userHeadPosition = headPos,
            userForward = headFwd,
            sourcePlane = null
        };
    }

    // ================================================================
    // PALM DETECTION (ported from SurfaceDetector)
    // ================================================================

    /// <summary>
    /// Check if a hand's palm faces down and all fingertips are within
    /// tolerance of the palm height (hand is resting flat on a surface).
    /// </summary>
    public bool IsPalmFlat(XRHand hand, out Vector3 palmPosition)
    {
        palmPosition = Vector3.zero;

        XRHandJoint palmJoint = hand.GetJoint(XRHandJointID.Palm);
        if (!palmJoint.TryGetPose(out Pose palmPose))
            return false;

        // Transform palm-down direction from session space to world space.
        // Direction transforms are unaffected by CameraYOffset (translation only),
        // so either cameraOffsetTransform or xrOrigin works for direction.
        Vector3 palmDown = palmPose.rotation * (-Vector3.up);
        if (cameraOffsetTransform != null)
            palmDown = cameraOffsetTransform.TransformDirection(palmDown);
        else if (xrOrigin != null)
            palmDown = xrOrigin.TransformDirection(palmDown);
        float angle = Vector3.Angle(palmDown, Vector3.down);
        if (angle > palmDownAngleThreshold)
            return false;

        float palmY = palmPose.position.y;
        foreach (var jointID in FingertipJoints)
        {
            XRHandJoint joint = hand.GetJoint(jointID);
            if (!joint.TryGetPose(out Pose tipPose))
                return false;

            if (Mathf.Abs(tipPose.position.y - palmY) > fingertipYTolerance)
                return false;
        }

        // Output position in world space
        palmPosition = SessionToWorld(palmPose.position);
        return true;
    }

    /// <summary>
    /// Converts a position from XR session/tracking space to Unity world space.
    ///
    /// Uses the Camera Floor Offset Object (not the XR Origin root) so that the
    /// CameraYOffset is included. Without this, hand positions end up
    /// CameraYOffset metres too low relative to the camera — e.g. a 2m offset
    /// makes the whiteboard appear 2m below the real table in passthrough.
    /// </summary>
    private Vector3 SessionToWorld(Vector3 sessionPos)
    {
        // Preferred: same transform chain the camera uses
        if (cameraOffsetTransform != null)
            return cameraOffsetTransform.TransformPoint(sessionPos);

        // Fallback: XR Origin root (correct only when CameraYOffset == 0)
        if (xrOrigin != null)
            return xrOrigin.TransformPoint(sessionPos);

        return sessionPos;
    }

    // ================================================================
    // HELPERS
    // ================================================================

    private void CollectFingertipPositions(XRHand hand, List<Vector3> points)
    {
        foreach (var jointID in FingertipJoints)
        {
            XRHandJoint joint = hand.GetJoint(jointID);
            if (joint.TryGetPose(out Pose pose))
                points.Add(SessionToWorld(pose.position));
        }
    }

    private void ResetConfirmation()
    {
        holdTimer = 0f;
        holdMinY = float.MaxValue;
        holdMaxY = float.MinValue;
        holdEyeYAccum = 0f;
        holdPalmYAccum = 0f;
        holdSampleCount = 0;
        holdFingertipYAccum = 0f;
        holdFingertipSampleCount = 0;
        IsConfirming = false;
        matchedPlane = null;
    }

    public void ResetState()
    {
        HasConfirmed = false;
        ResetConfirmation();
        candidates.Clear();
        planeScanTimer = 0f;
        usingFallback = false;
    }

    /// <summary>
    /// Forces hand-only mode immediately, skipping AR plane scanning entirely.
    /// Used when JournalSessionManager.skipPlaneDetection is enabled for
    /// Quest-style instant detection from hand joints alone.
    /// </summary>
    public void ForceHandOnlyMode()
    {
        usingFallback = true;
        Debug.Log("[ARTableDetector] Forced into hand-only mode (plane detection skipped).");
    }
}
