using UnityEngine;
using UnityEngine.XR.Hands;

/// <summary>
/// Renders a virtual pen prop along the real stylus axis. The prop is
/// anchored at the grip point (between thumb and index finger) and extends
/// BOTH ways: the shaft runs past the grip out to the back end of the pen,
/// and past the grip toward the tip (the red sphere). This matches how a
/// real pen looks in hand — the pen passes through the grip, it doesn't
/// originate from the wrist.
///
/// Axis definition:
///   - Grip point  = midpoint between ThumbTip and IndexTip (the natural
///                    pinch point where a thin pen is held).
///   - Tip         = <see cref="StylusTipProvider.TipWorldPosition"/>.
///   - Shaft axis  = unit vector from grip to tip.
///   - Back end    = grip - axis * backLength (sticks out past the hand).
///
/// The shaft therefore runs from back-end → tip, passing through the grip.
/// No wrist-originating line and no visual detachment between shaft and
/// tip sphere.
/// </summary>
[DefaultExecutionOrder(-10)]
public class StylusVisualProp : MonoBehaviour
{
    [Header("References")]
    public StylusTipProvider tipProvider;
    public StylusWristTracker wristTracker;

    [Header("Hand")]
    [Tooltip("Which hand holds the stylus. Used to read thumb/index joints for the grip point.")]
    public Handedness stylusHand = Handedness.Right;

    [Header("Shaft")]
    [Tooltip("Radius of the pen shaft (metres).")]
    public float shaftRadius = 0.005f;
    [Tooltip("Length of the shaft that sticks out past the grip toward the back (metres). " +
             "Real pens typically have 7-10cm behind the grip point.")]
    public float backLength = 0.08f;
    [Tooltip("Colour of the pen shaft.")]
    public Color shaftColor = new Color(0.15f, 0.15f, 0.18f, 1f);

    [Header("Tip")]
    [Tooltip("Radius of the tip sphere (metres).")]
    public float tipRadius = 0.0045f;
    [Tooltip("Colour of the pen tip marker.")]
    public Color tipColor = new Color(0.95f, 0.25f, 0.25f, 1f);

    [Header("Grip Blending")]
    [Tooltip("How strongly to bias the grip point from the raw thumb-index midpoint " +
             "toward the line between the wrist and the tip. 0 = pure finger midpoint (most " +
             "anatomical), 1 = snap onto the wrist→tip line (can look stiff). Small values " +
             "(0.1-0.3) smooth out thumb/index jitter without losing realism.")]
    [Range(0f, 1f)] public float gripSmoothingToWristLine = 0.15f;

    [Header("Visibility")]
    [Tooltip("Hide the prop when the tip confidence is below this threshold.")]
    public float minConfidence = 0.1f;

    /// <summary>
    /// External gate. When false, the prop is force-hidden regardless of tracking
    /// state. Toggled by <see cref="JournalSessionManager"/> to hide the pen during
    /// non-writing states (review, intro, etc.).
    /// </summary>
    public bool PropEnabled { get; private set; } = true;

    public void SetPropEnabled(bool enabled)
    {
        PropEnabled = enabled;
        if (!enabled) SetVisible(false);
    }

    // ── Runtime objects ──────────────────────────────────────────────
    private GameObject root;
    private GameObject shaft;
    private GameObject tipSphere;
    private Material shaftMaterial;
    private Material tipMaterial;

    private void Awake()
    {
        BuildProp();
        SetVisible(false);
    }

    private void OnDestroy()
    {
        if (root != null) Destroy(root);
        if (shaftMaterial != null) Destroy(shaftMaterial);
        if (tipMaterial != null) Destroy(tipMaterial);
    }

    private void LateUpdate()
    {
        if (!PropEnabled) { SetVisible(false); return; }
        if (tipProvider == null || wristTracker == null) { SetVisible(false); return; }
        if (!tipProvider.IsCalibrated || !tipProvider.TipWorldPosition.HasValue
            || tipProvider.Confidence < minConfidence) { SetVisible(false); return; }

        Vector3 tip = tipProvider.TipWorldPosition.Value;

        if (!TryGetGripPoint(out Vector3 grip))
        {
            // No finger joints available — fall back to wrist as the grip
            // proxy, but only because something is better than nothing.
            if (!wristTracker.TryGetWristPose(out grip, out _)) { SetVisible(false); return; }
        }

        // Optional: blend the grip toward the wrist-to-tip line to iron out
        // thumb/index jitter. Stays identical to the raw midpoint when the
        // smoothing weight is 0.
        if (gripSmoothingToWristLine > 0f
            && wristTracker.TryGetWristPose(out Vector3 wristPos, out _))
        {
            Vector3 wristToTip = tip - wristPos;
            float len2 = wristToTip.sqrMagnitude;
            if (len2 > 1e-6f)
            {
                float t = Vector3.Dot(grip - wristPos, wristToTip) / len2;
                Vector3 gripOnLine = wristPos + wristToTip * t;
                grip = Vector3.Lerp(grip, gripOnLine, gripSmoothingToWristLine);
            }
        }

        Vector3 gripToTip = tip - grip;
        float gripToTipLen = gripToTip.magnitude;
        if (gripToTipLen < 0.005f) { SetVisible(false); return; }

        Vector3 axis = gripToTip / gripToTipLen;
        Vector3 backEnd = grip - axis * backLength;

        float totalLength = (tip - backEnd).magnitude;
        Vector3 center = (backEnd + tip) * 0.5f;

        // Cylinder primitive is 2m tall along Y; scale Y = length/2.
        shaft.transform.position = center;
        shaft.transform.rotation = Quaternion.FromToRotation(Vector3.up, axis);
        shaft.transform.localScale = new Vector3(shaftRadius * 2f,
                                                 totalLength * 0.5f,
                                                 shaftRadius * 2f);

        tipSphere.transform.position = tip;
        tipSphere.transform.localScale = Vector3.one * (tipRadius * 2f);

        SetVisible(true);
    }

    /// <summary>
    /// Grip point = midpoint between ThumbTip and IndexTip in world space.
    /// This is where the pen is actually held when using a standard tripod grip.
    /// </summary>
    private bool TryGetGripPoint(out Vector3 gripWorld)
    {
        gripWorld = Vector3.zero;

        var subsystem = WhiteboardPen.GetHandSubsystem();
        if (subsystem == null || !subsystem.running) return false;

        XRHand hand = stylusHand == Handedness.Left ? subsystem.leftHand : subsystem.rightHand;
        if (!hand.isTracked) return false;

        if (!hand.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out Pose thumbPose)) return false;
        if (!hand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out Pose indexPose)) return false;

        var origin = FindAnyObjectByType<Unity.XR.CoreUtils.XROrigin>();
        Transform offset = (origin != null && origin.CameraFloorOffsetObject != null)
            ? origin.CameraFloorOffsetObject.transform
            : (Camera.main != null ? Camera.main.transform.parent : null);

        Vector3 thumbW = offset != null ? offset.TransformPoint(thumbPose.position) : thumbPose.position;
        Vector3 indexW = offset != null ? offset.TransformPoint(indexPose.position) : indexPose.position;

        gripWorld = (thumbW + indexW) * 0.5f;
        return true;
    }

    private void BuildProp()
    {
        root = new GameObject("StylusVisualProp");
        root.transform.SetParent(transform, false);

        shaft = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
        shaft.name = "Shaft";
        shaft.transform.SetParent(root.transform, false);
        var shaftCol = shaft.GetComponent<Collider>();
        if (shaftCol != null) Destroy(shaftCol);
        shaftMaterial = CreateMaterial(shaftColor);
        var shaftRend = shaft.GetComponent<Renderer>();
        shaftRend.material = shaftMaterial;
        shaftRend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        shaftRend.receiveShadows = false;

        tipSphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        tipSphere.name = "Tip";
        tipSphere.transform.SetParent(root.transform, false);
        var tipCol = tipSphere.GetComponent<Collider>();
        if (tipCol != null) Destroy(tipCol);
        tipMaterial = CreateMaterial(tipColor);
        var tipRend = tipSphere.GetComponent<Renderer>();
        tipRend.material = tipMaterial;
        tipRend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        tipRend.receiveShadows = false;
    }

    private void SetVisible(bool visible)
    {
        if (root != null && root.activeSelf != visible)
            root.SetActive(visible);
    }

    private static Material CreateMaterial(Color color)
    {
        var shader = Shader.Find("Universal Render Pipeline/Lit");
        if (shader == null) shader = Shader.Find("Universal Render Pipeline/Unlit");
        var mat = new Material(shader);
        mat.color = color;
        return mat;
    }
}
