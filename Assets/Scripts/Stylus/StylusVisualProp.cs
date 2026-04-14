using UnityEngine;

/// <summary>
/// Renders a virtual pen prop at the tracked stylus position. Uses a thin
/// cylinder for the shaft and a small sphere for the tip — no external model
/// required. The shaft runs from the wrist (grip point) to the tip, giving
/// the player the visual sense of holding a pen while they hold the physical
/// DIY stylus in the real world.
///
/// Attach alongside <see cref="StylusTipProvider"/>. Renders only while the
/// tracker is calibrated and a tip position is available.
/// </summary>
[DefaultExecutionOrder(-10)] // after StylusTipProvider (-25), before WhiteboardPen (-20)? run late anyway in LateUpdate
public class StylusVisualProp : MonoBehaviour
{
    [Header("References")]
    public StylusTipProvider tipProvider;
    public StylusWristTracker wristTracker;

    [Header("Shaft")]
    [Tooltip("Radius of the pen shaft (metres).")]
    public float shaftRadius = 0.005f;
    [Tooltip("How far from the wrist the shaft starts (metres). 0 = wrist joint, positive = toward tip.")]
    public float shaftStartFromWrist = 0.02f;
    [Tooltip("Colour of the pen shaft.")]
    public Color shaftColor = new Color(0.15f, 0.15f, 0.18f, 1f);

    [Header("Tip")]
    [Tooltip("Radius of the tip sphere (metres).")]
    public float tipRadius = 0.0045f;
    [Tooltip("Colour of the pen tip marker.")]
    public Color tipColor = new Color(0.95f, 0.25f, 0.25f, 1f);

    [Header("Visibility")]
    [Tooltip("Hide the prop when the tip confidence is below this threshold.")]
    public float minConfidence = 0.1f;

    // ── Runtime objects ──────────────────────────────────────────────
    private GameObject root;
    private GameObject shaft;          // cylinder
    private GameObject tipSphere;      // sphere
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
        if (tipProvider == null || wristTracker == null)
        {
            SetVisible(false);
            return;
        }

        if (!tipProvider.IsCalibrated || !tipProvider.TipWorldPosition.HasValue
            || tipProvider.Confidence < minConfidence)
        {
            SetVisible(false);
            return;
        }

        if (!wristTracker.TryGetWristPose(out Vector3 wristPos, out Quaternion wristRot))
        {
            SetVisible(false);
            return;
        }

        Vector3 tip = tipProvider.TipWorldPosition.Value;
        Vector3 wristToTip = tip - wristPos;
        float fullLength = wristToTip.magnitude;
        if (fullLength < 0.02f)
        {
            // Degenerate — hand too close to the tip (shouldn't happen).
            SetVisible(false);
            return;
        }

        Vector3 dir = wristToTip / fullLength;
        Vector3 shaftStart = wristPos + dir * shaftStartFromWrist;
        Vector3 shaftEnd = tip;
        float shaftLength = Mathf.Max(0.01f, (shaftEnd - shaftStart).magnitude);

        // Cylinder primitive is 2m tall along Y by default; scale Y = length/2.
        shaft.transform.position = (shaftStart + shaftEnd) * 0.5f;
        shaft.transform.rotation = Quaternion.FromToRotation(Vector3.up, dir);
        shaft.transform.localScale = new Vector3(shaftRadius * 2f, shaftLength * 0.5f, shaftRadius * 2f);

        tipSphere.transform.position = tip;
        tipSphere.transform.localScale = Vector3.one * (tipRadius * 2f);

        SetVisible(true);
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
