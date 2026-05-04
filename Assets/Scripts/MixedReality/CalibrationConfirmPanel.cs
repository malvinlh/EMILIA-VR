using System;
using TMPro;
using UnityEngine;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>
/// Floating confirmation panel shown on 2nd+ journaling sessions in the same
/// scene visit. Asks "Apakah kamu ingin kalibrasi ulang?" with two buttons:
///   • "Ya, Kalibrasi Ulang"  → fires onRecalibrate callback
///   • "Tidak, Lanjutkan"     → fires onSkip callback
///
/// Procedurally created at runtime — no prefab or scene changes required.
/// Attach to an empty GameObject owned by JournalSessionManager.
/// Call Show() to display and Hide() to dismiss.
/// </summary>
public class CalibrationConfirmPanel : MonoBehaviour
{
    // ================================================================
    // CONSTANTS
    // ================================================================

    private const float kForwardDist  = 0.60f;  // metres in front of camera
    private const float kPokeCooldown = 0.50f;  // brief grace period after Show()

    private static readonly Color kBgColor    = new Color(0.05f, 0.05f, 0.10f, 0.82f);
    private static readonly Color kBtnYaColor = new Color(0.18f, 0.72f, 0.35f, 0.95f); // green
    private static readonly Color kBtnNoColor = new Color(0.25f, 0.45f, 0.85f, 0.95f); // blue

    // ================================================================
    // STATE
    // ================================================================

    private Action _onRecalibrate;
    private Action _onSkip;
    private XRHandSubsystem _handSubsystem;
    private float _pokeCooldown;

    // Runtime-created child root. Destroyed and rebuilt each Show() call.
    private GameObject _panelRoot;

    private struct BtnData
    {
        public BoxCollider col;
        public XRSimpleInteractable xri;
    }
    private BtnData _btnYa;
    private BtnData _btnTidak;

    // ================================================================
    // PUBLIC API
    // ================================================================

    /// <summary>
    /// Builds and displays the panel in front of the current camera.
    /// Calling Show() a second time replaces the previous panel.
    /// </summary>
    public void Show(Action onRecalibrate, Action onSkip)
    {
        _onRecalibrate = onRecalibrate;
        _onSkip        = onSkip;
        _pokeCooldown  = kPokeCooldown;
        DestroyVisuals();
        BuildVisuals();
        PositionInFrontOfCamera();
        gameObject.SetActive(true);
        enabled = true;
    }

    /// <summary>
    /// Destroys panel visuals and clears callbacks.
    /// The component and its host GameObject are kept alive for reuse.
    /// </summary>
    public void Hide()
    {
        _onRecalibrate = null;
        _onSkip        = null;
        DestroyVisuals();
        enabled = false;
    }

    // ================================================================
    // LIFECYCLE
    // ================================================================

    private void Update()
    {
        if (_panelRoot == null) return;

        // Billboard toward camera every frame.
        Camera cam = Camera.main;
        if (cam != null)
        {
            Vector3 fwd = cam.transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude > 0.001f)
                transform.rotation = Quaternion.LookRotation(fwd.normalized);
        }

        if (_pokeCooldown > 0f) { _pokeCooldown -= Time.deltaTime; return; }

        if (_handSubsystem == null || !_handSubsystem.running)
            _handSubsystem = WhiteboardPen.GetHandSubsystem();
        if (_handSubsystem == null) return;

        CheckPoke(_handSubsystem.leftHand);
        CheckPoke(_handSubsystem.rightHand);
    }

    private void OnDestroy() => DestroyVisuals();

    // ================================================================
    // INTERACTION
    // ================================================================

    private void CheckPoke(XRHand hand)
    {
        if (!hand.isTracked) return;
        if (!hand.GetJoint(XRHandJointID.IndexTip).TryGetPose(out Pose tip)) return;

        if (_btnYa.col != null && _btnYa.col.bounds.Contains(tip.position))
        { FireYa(); return; }

        if (_btnTidak.col != null && _btnTidak.col.bounds.Contains(tip.position))
        { FireTidak(); }
    }

    private void FireYa()
    {
        var cb = _onRecalibrate;
        Hide();
        cb?.Invoke();
    }

    private void FireTidak()
    {
        var cb = _onSkip;
        Hide();
        cb?.Invoke();
    }

    private void OnYaSelected(SelectEnterEventArgs _)    => FireYa();
    private void OnTidakSelected(SelectEnterEventArgs _) => FireTidak();

    // ================================================================
    // PANEL CONSTRUCTION
    // ================================================================

    private void BuildVisuals()
    {
        _panelRoot = new GameObject("ConfirmPanelRoot");
        _panelRoot.transform.SetParent(transform, false);

        // Background
        var bg = GameObject.CreatePrimitive(PrimitiveType.Quad);
        bg.name = "BG";
        Destroy(bg.GetComponent<Collider>());
        bg.transform.SetParent(_panelRoot.transform, false);
        bg.transform.localScale    = new Vector3(0.80f, 0.44f, 1f);
        bg.transform.localPosition = Vector3.zero;
        var bgRend = bg.GetComponent<Renderer>();
        bgRend.material            = MakeMat(kBgColor);
        bgRend.shadowCastingMode   = UnityEngine.Rendering.ShadowCastingMode.Off;
        bgRend.receiveShadows      = false;

        // Question text
        var qtGo = new GameObject("QuestionText");
        qtGo.transform.SetParent(_panelRoot.transform, false);
        qtGo.transform.localPosition = new Vector3(0f, 0.10f, -0.002f);
        var qt = qtGo.AddComponent<TextMeshPro>();
        qt.text                    = "Apakah kamu ingin\nkalibrasi ulang?";
        qt.fontSize                = 0.055f;
        qt.alignment               = TextAlignmentOptions.Center;
        qt.color                   = Color.white;
        qt.rectTransform.sizeDelta = new Vector2(0.74f, 0.22f);

        // Buttons
        _btnYa    = SpawnButton("ButtonYa",    "Ya,\nKalibrasi Ulang", kBtnYaColor, new Vector3(-0.21f, -0.10f, -0.004f));
        _btnTidak = SpawnButton("ButtonTidak", "Tidak,\nLanjutkan",    kBtnNoColor, new Vector3( 0.21f, -0.10f, -0.004f));

        _btnYa.xri.selectEntered.AddListener(OnYaSelected);
        _btnTidak.xri.selectEntered.AddListener(OnTidakSelected);
    }

    private BtnData SpawnButton(string objName, string label, Color color, Vector3 localPos)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = objName;
        go.transform.SetParent(_panelRoot.transform, false);
        go.transform.localScale    = new Vector3(0.34f, 0.10f, 0.02f);
        go.transform.localPosition = localPos;

        var col       = go.GetComponent<BoxCollider>();
        col.isTrigger = false;

        var rend = go.GetComponent<Renderer>();
        rend.material          = MakeMat(color);
        rend.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        rend.receiveShadows    = false;

        var xri = go.AddComponent<XRSimpleInteractable>();

        // Label
        var lblGo = new GameObject("Label");
        lblGo.transform.SetParent(go.transform, false);
        var tmp = lblGo.AddComponent<TextMeshPro>();
        tmp.text                    = label;
        tmp.fontSize                = 0.55f;
        tmp.alignment               = TextAlignmentOptions.Center;
        tmp.color                   = Color.white;
        tmp.enableWordWrapping      = true;
        tmp.rectTransform.sizeDelta = new Vector2(0.30f, 0.09f);
        // Scale label so it fits inside the button cube's face (cube is 0.34 × 0.10 × 0.02 m)
        lblGo.transform.localScale    = new Vector3(1f / 0.34f * 0.28f, 1f / 0.10f * 0.08f, 1f);
        lblGo.transform.localPosition = new Vector3(0f, 0f, -0.6f);

        return new BtnData { col = col, xri = xri };
    }

    private void PositionInFrontOfCamera()
    {
        Camera cam = Camera.main;
        if (cam == null) return;

        Vector3 fwd = cam.transform.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.001f) fwd = cam.transform.forward;
        fwd.Normalize();

        transform.position = cam.transform.position + fwd * kForwardDist + Vector3.down * 0.05f;
        transform.rotation = Quaternion.LookRotation(fwd);
    }

    private void DestroyVisuals()
    {
        if (_btnYa.xri    != null) { _btnYa.xri.selectEntered.RemoveListener(OnYaSelected);       _btnYa    = default; }
        if (_btnTidak.xri != null) { _btnTidak.xri.selectEntered.RemoveListener(OnTidakSelected); _btnTidak = default; }
        if (_panelRoot    != null) { Destroy(_panelRoot); _panelRoot = null; }
    }

    // ================================================================
    // MATERIAL HELPER
    // ================================================================

    private static Material MakeMat(Color color)
    {
        var shader = Shader.Find("Universal Render Pipeline/Unlit");
        var mat    = new Material(shader != null ? shader : Shader.Find("Unlit/Color"));
        mat.SetFloat("_Surface", 1f);
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.SetFloat("_Blend",    0f);
        mat.SetFloat("_ZWrite",   0f);
        mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        mat.color = color;
        return mat;
    }
}
