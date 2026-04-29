using System.Collections.Generic;
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
///
/// Calibration supports two modes:
///   1. Single-sample: <see cref="CalibrateFromTargetPosition"/> — legacy
///      one-shot path, kept for scripted recalibration.
///   2. Multi-sample: <see cref="AccumulateSample"/> repeatedly, then
///      <see cref="FinalizeOffset"/> — averages per-sample offsets across
///      varied wrist poses to cancel rotation-dependent noise. This is the
///      primary entry point used by the interactive calibration UI.
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

    // ── Multi-sample accumulator ─────────────────────────────────────
    // Each sample records the raw ingredients needed for the closed-form
    // least-squares solve: offset = mean_i [ wristRot_i^-1 * (target_i - wristPos_i) ].
    private struct CalibrationSample
    {
        public Vector3 wristPos;
        public Quaternion wristRot;
        public Vector3 targetWorldPos;
    }
    private readonly List<CalibrationSample> samples = new List<CalibrationSample>(16);

    public bool IsCalibrated => isCalibrated;
    public int SampleCount => samples.Count;
    public Vector3 CurrentOffset => wristLocalOffset;

    private void Awake()
    {
        ResolveCameraOffsetTransform();
    }

    /// <summary>
    /// Compute and store the wrist-to-tip offset from a known target world
    /// position that the pen tip is currently touching. Single-sample path.
    /// </summary>
    public bool CalibrateFromTargetPosition(Vector3 targetWorldPos)
    {
        if (!TryGetWristPose(out Vector3 wristWorldPos, out Quaternion wristWorldRot))
            return false;

        wristLocalOffset = Quaternion.Inverse(wristWorldRot) * (targetWorldPos - wristWorldPos);
        isCalibrated = true;

        Debug.Log($"[StylusWristTracker] Calibrated (single-sample). wristLocalOffset={wristLocalOffset} " +
                  $"(magnitude={wristLocalOffset.magnitude:F3}m)");
        return true;
    }

    /// <summary>
    /// Set the wrist-local offset directly (e.g., from a prior persisted value).
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
        samples.Clear();
    }

    /// <summary>Discard any previously accumulated samples without touching the current offset.</summary>
    public void ClearSamples() => samples.Clear();

    /// <summary>
    /// Record one (wrist pose, target world position) sample. The wrist pose
    /// is read now from the live hand joint; the caller supplies the target
    /// where the pen tip is physically touching (e.g., the opposite index
    /// fingertip). Returns false if the wrist joint was not trackable.
    /// </summary>
    public bool AccumulateSample(Vector3 targetWorldPos)
    {
        if (!TryGetWristPose(out Vector3 wristPos, out Quaternion wristRot))
            return false;

        samples.Add(new CalibrationSample
        {
            wristPos = wristPos,
            wristRot = wristRot,
            targetWorldPos = targetWorldPos,
        });
        return true;
    }

    /// <summary>
    /// Solve for the wrist-local offset that minimises the sum of squared
    /// world-space residuals across all accumulated samples. Closed form:
    ///   offset = (1/N) * sum_i [ wristRot_i^-1 * (target_i - wristPos_i) ]
    /// Returns true and writes the RMS residual (metres) in <paramref name="rmsResidualMeters"/>
    /// on success. Requires at least two samples.
    /// </summary>
    public bool FinalizeOffset(out float rmsResidualMeters)
    {
        rmsResidualMeters = 0f;
        if (samples.Count < 2) return false;

        Vector3 sum = Vector3.zero;
        for (int i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            sum += Quaternion.Inverse(s.wristRot) * (s.targetWorldPos - s.wristPos);
        }
        Vector3 offset = sum / samples.Count;

        // RMS residual of the fit in world space.
        float sumSq = 0f;
        for (int i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            Vector3 predicted = s.wristPos + s.wristRot * offset;
            sumSq += (predicted - s.targetWorldPos).sqrMagnitude;
        }
        rmsResidualMeters = Mathf.Sqrt(sumSq / samples.Count);

        wristLocalOffset = offset;
        isCalibrated = true;

        Debug.Log($"[StylusWristTracker] Calibrated (N={samples.Count}). " +
                  $"offset={offset} mag={offset.magnitude:F3}m rms={rmsResidualMeters * 1000f:F1}mm");
        return true;
    }

    /// <summary>
    /// Apply a single mid-session correction using one new sample. Blends the
    /// new per-sample offset into the current offset at <paramref name="blend"/>
    /// weight (0 = keep current, 1 = replace). Useful for the "quick nudge"
    /// hotkey when the user notices drift during writing.
    /// </summary>
    public bool NudgeOffset(Vector3 targetWorldPos, float blend)
    {
        if (!isCalibrated) return CalibrateFromTargetPosition(targetWorldPos);
        if (!TryGetWristPose(out Vector3 wristPos, out Quaternion wristRot))
            return false;

        Vector3 fresh = Quaternion.Inverse(wristRot) * (targetWorldPos - wristPos);
        wristLocalOffset = Vector3.Lerp(wristLocalOffset, fresh, Mathf.Clamp01(blend));
        Debug.Log($"[StylusWristTracker] Offset nudged (blend={blend:F2}). New={wristLocalOffset}");
        return true;
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
    /// Read the index-tip pose in world space for the configured stylus hand.
    /// Used as a proximity proxy during calibration: the user's index finger
    /// naturally sits near the pen tip when gripping a thin DIY pen.
    /// </summary>
    public bool TryGetIndexTipWorldPosition(out Vector3 worldPosition)
    {
        return TryGetIndexTipForHand(handedness, out worldPosition);
    }

    /// <summary>
    /// Read the index-tip pose in world space for an explicit hand. Used by
    /// the calibration controller to locate the OPPOSITE hand's fingertip
    /// (the contact target).
    /// </summary>
    public bool TryGetIndexTipForHand(Handedness hand, out Vector3 worldPosition)
    {
        return TryGetHandJointWorldPosition(hand, XRHandJointID.IndexTip, out worldPosition);
    }

    /// <summary>
    /// Read an arbitrary XR Hands joint pose and convert it from session space
    /// to world space using the same CameraFloorOffsetObject transform chain
    /// as <see cref="TryGetWristPose"/>.
    /// </summary>
    public bool TryGetHandJointWorldPosition(Handedness hand, XRHandJointID jointID, out Vector3 worldPosition)
    {
        worldPosition = Vector3.zero;

        if (handSubsystem == null || !handSubsystem.running)
        {
            handSubsystem = WhiteboardPen.GetHandSubsystem();
            if (handSubsystem == null) return false;
        }

        if (cameraOffsetTransform == null)
            ResolveCameraOffsetTransform();

        XRHand xrHand = hand == Handedness.Left
            ? handSubsystem.leftHand
            : handSubsystem.rightHand;

        if (!xrHand.isTracked) return false;

        XRHandJoint joint = xrHand.GetJoint(jointID);
        if (!joint.TryGetPose(out Pose jointPose)) return false;

        worldPosition = cameraOffsetTransform != null
            ? cameraOffsetTransform.TransformPoint(jointPose.position)
            : jointPose.position;
        return true;
    }

    /// <summary>
    /// Read the world-space distance between <see cref="XRHandJointID.ThumbTip"/>
    /// and <see cref="XRHandJointID.MiddleTip"/> on the given hand. Used as the
    /// explicit "capture now" signal during stylus calibration — the user
    /// pinches thumb to middle finger while keeping the index extended (the
    /// index holds the target sphere). Returns false if either joint or the
    /// hand itself is not tracked.
    /// </summary>
    public bool TryGetPinchGap(Handedness hand, out float meters)
    {
        meters = float.PositiveInfinity;
        if (!TryGetHandJointWorldPosition(hand, XRHandJointID.ThumbTip, out Vector3 thumbTip)) return false;
        if (!TryGetHandJointWorldPosition(hand, XRHandJointID.MiddleTip, out Vector3 middleTip)) return false;
        meters = Vector3.Distance(thumbTip, middleTip);
        return true;
    }

    /// <summary>
    /// Run the closed-form solve on the current accumulator without committing the result.
    /// Returns false if fewer than 2 samples are present.
    /// Also returns the maximum pairwise rotational diversity across captured wrist poses.
    /// </summary>
    public bool PreviewSolve(out float rmsResidualMeters, out float diversityDeg)
    {
        rmsResidualMeters = 0f;
        diversityDeg = 0f;
        if (samples.Count < 2) return false;

        Vector3 sum = Vector3.zero;
        for (int i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            sum += Quaternion.Inverse(s.wristRot) * (s.targetWorldPos - s.wristPos);
        }
        Vector3 offset = sum / samples.Count;

        float sumSq = 0f;
        for (int i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            Vector3 predicted = s.wristPos + s.wristRot * offset;
            sumSq += (predicted - s.targetWorldPos).sqrMagnitude;
        }
        rmsResidualMeters = Mathf.Sqrt(sumSq / samples.Count);
        diversityDeg = ComputeRotationalDiversity();
        return true;
    }

    /// <summary>
    /// Compute the current pen-tip world position using the mean of accumulated
    /// samples as a preview offset (without committing). Returns false if fewer
    /// than 2 samples are present or the wrist is not tracked.
    /// </summary>
    public bool TryGetPreviewTipPosition(out Vector3 tipWorld)
    {
        tipWorld = Vector3.zero;
        if (samples.Count < 2) return false;
        if (!TryGetWristPose(out Vector3 wristPos, out Quaternion wristRot)) return false;

        Vector3 sum = Vector3.zero;
        for (int i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            sum += Quaternion.Inverse(s.wristRot) * (s.targetWorldPos - s.wristPos);
        }
        tipWorld = wristPos + wristRot * (sum / samples.Count);
        return true;
    }

    /// <summary>
    /// Maximum pairwise angular distance (degrees) across all captured wrist
    /// orientations. Low values mean all samples were taken at the same wrist
    /// angle — the solve is poorly conditioned.
    /// </summary>
    public float ComputeRotationalDiversity()
    {
        float maxAngle = 0f;
        for (int i = 0; i < samples.Count - 1; i++)
            for (int j = i + 1; j < samples.Count; j++)
            {
                float a = Quaternion.Angle(samples[i].wristRot, samples[j].wristRot);
                if (a > maxAngle) maxAngle = a;
            }
        return maxAngle;
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
