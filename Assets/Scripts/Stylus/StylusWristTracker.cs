using UnityEngine;
using UnityEngine.XR.Hands;

/// <summary>
/// Pure hand-tracking pen tip estimation. Reads the wrist joint pose from
/// XRHandSubsystem and applies a calibrated local-space offset to compute
/// the world-space pen tip position.
///
/// The wrist joint is chosen because it has the lowest jitter of the hand
/// joints and is the natural pivot for a held pen. Calibration stores a
/// single Vector3 offset in wrist-local coordinates; this fully determines
/// the rigid pen-tip relationship because the wrist joint also provides
/// orientation (6DOF).
/// </summary>
public class StylusWristTracker : MonoBehaviour
{
    [Tooltip("Which hand holds the stylus.")]
    public Handedness handedness = Handedness.Right;

    private XRHandSubsystem handSubsystem;
    private Transform cameraOffsetTransform;

    // ── Calibration state ────────────────────────────────────────────
    private Vector3 wristLocalOffset;
    private bool isCalibrated;

    public bool IsCalibrated => isCalibrated;

    private void Awake()
    {
        ResolveCameraOffsetTransform();
    }

    /// <summary>
    /// Compute and store the wrist-to-tip offset from a known target world
    /// position that the pen tip is currently touching. Call this during
    /// calibration when the user holds the pen tip on a known reference point.
    /// Returns true if the wrist pose was readable and the offset was stored.
    /// </summary>
    public bool CalibrateFromTargetPosition(Vector3 targetWorldPos)
    {
        if (!TryGetWristPose(out Vector3 wristWorldPos, out Quaternion wristWorldRot))
            return false;

        wristLocalOffset = Quaternion.Inverse(wristWorldRot) * (targetWorldPos - wristWorldPos);
        isCalibrated = true;

        Debug.Log($"[StylusWristTracker] Calibrated. wristLocalOffset={wristLocalOffset} " +
                  $"(magnitude={wristLocalOffset.magnitude:F3}m)");
        return true;
    }

    /// <summary>
    /// Set the wrist-local offset directly (e.g., from an averaged sample
    /// accumulated over a hold period).
    /// </summary>
    public void SetWristOffset(Vector3 localOffset)
    {
        wristLocalOffset = localOffset;
        isCalibrated = true;
    }

    public void ClearCalibration()
    {
        wristLocalOffset = Vector3.zero;
        isCalibrated = false;
    }

    /// <summary>
    /// Compute the current pen-tip world position from the wrist joint.
    /// Returns false if the hand is not tracked or calibration is missing.
    /// </summary>
    public bool TryGetTipPosition(out Vector3 worldPosition, out float confidence)
    {
        worldPosition = Vector3.zero;
        confidence = 0f;

        if (!isCalibrated) return false;
        if (!TryGetWristPose(out Vector3 wristWorldPos, out Quaternion wristWorldRot)) return false;

        worldPosition = wristWorldPos + wristWorldRot * wristLocalOffset;
        confidence = 1f; // wrist tracking is binary (tracked or not)
        return true;
    }

    /// <summary>
    /// Read the wrist joint pose and convert from session space to world space.
    /// Uses the same CameraFloorOffsetObject transform chain as WhiteboardPen,
    /// which is required because Device tracking mode applies a camera Y offset.
    /// </summary>
    public bool TryGetWristPose(out Vector3 worldPosition, out Quaternion worldRotation)
    {
        worldPosition = Vector3.zero;
        worldRotation = Quaternion.identity;

        if (handSubsystem == null || !handSubsystem.running)
        {
            handSubsystem = WhiteboardPen.GetHandSubsystem();
            if (handSubsystem == null) return false;
        }

        if (cameraOffsetTransform == null)
            ResolveCameraOffsetTransform();

        XRHand hand = handedness == Handedness.Left
            ? handSubsystem.leftHand
            : handSubsystem.rightHand;

        if (!hand.isTracked) return false;

        XRHandJoint wristJoint = hand.GetJoint(XRHandJointID.Wrist);
        if (!wristJoint.TryGetPose(out Pose wristPose)) return false;

        if (cameraOffsetTransform != null)
        {
            worldPosition = cameraOffsetTransform.TransformPoint(wristPose.position);
            worldRotation = cameraOffsetTransform.rotation * wristPose.rotation;
        }
        else
        {
            worldPosition = wristPose.position;
            worldRotation = wristPose.rotation;
        }

        return true;
    }

    /// <summary>
    /// Read the index-tip pose in world space. Used during calibration as a
    /// proxy for the pen tip (the user's index finger naturally sits near
    /// the pen tip when gripping a thin pen).
    /// </summary>
    public bool TryGetIndexTipWorldPosition(out Vector3 worldPosition)
    {
        worldPosition = Vector3.zero;

        if (handSubsystem == null || !handSubsystem.running)
        {
            handSubsystem = WhiteboardPen.GetHandSubsystem();
            if (handSubsystem == null) return false;
        }

        if (cameraOffsetTransform == null)
            ResolveCameraOffsetTransform();

        XRHand hand = handedness == Handedness.Left
            ? handSubsystem.leftHand
            : handSubsystem.rightHand;

        if (!hand.isTracked) return false;

        XRHandJoint tipJoint = hand.GetJoint(XRHandJointID.IndexTip);
        if (!tipJoint.TryGetPose(out Pose tipPose)) return false;

        worldPosition = cameraOffsetTransform != null
            ? cameraOffsetTransform.TransformPoint(tipPose.position)
            : tipPose.position;
        return true;
    }

    private void ResolveCameraOffsetTransform()
    {
        var xrOriginComp = FindAnyObjectByType<Unity.XR.CoreUtils.XROrigin>();
        if (xrOriginComp != null && xrOriginComp.CameraFloorOffsetObject != null)
        {
            cameraOffsetTransform = xrOriginComp.CameraFloorOffsetObject.transform;
            return;
        }
        Camera cam = Camera.main;
        if (cam != null && cam.transform.parent != null)
            cameraOffsetTransform = cam.transform.parent;
    }
}
