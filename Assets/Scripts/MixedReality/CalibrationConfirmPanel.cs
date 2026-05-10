using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.UI;

/// <summary>
/// Floating confirmation panel shown on 2nd+ journaling sessions (same-scene or cross-scene).
/// Asks "Apakah kamu ingin kalibrasi ulang?" with two buttons:
///   • "Ya, Kalibrasi Ulang"  → fires onRecalibrate callback
///   • "Tidak, Lanjutkan"     → fires onSkip callback
///
/// Built as a world-space Canvas (matching JournalReviewController's choice panel style)
/// with TrackedDeviceGraphicRaycaster so both controller ray and hand ray interaction work.
/// Procedurally created at runtime — no prefab or scene changes required.
/// </summary>
public class CalibrationConfirmPanel : MonoBehaviour
{
    private const float kForwardDist = 0.60f; // metres in front of camera

    // Color palette — mirrors JournalReviewController / VRDialoguePanel visual identity
    private static readonly Color s_PanelBg     = new Color(0.969f, 0.918f, 0.918f, 1.00f); // warm blush
    private static readonly Color s_AccentMauve = new Color(0.780f, 0.663f, 0.722f, 1.00f); // dusty rose
    private static readonly Color s_TextDark    = new Color(0.369f, 0.329f, 0.349f, 1.00f); // dark brownish-purple
    private static readonly Color s_BtnYa       = new Color(0.490f, 0.730f, 0.560f, 1.00f); // sage green
    private static readonly Color s_BtnTidak    = new Color(0.790f, 0.470f, 0.450f, 1.00f); // warm coral

    private Action      _onRecalibrate;
    private Action      _onSkip;
    private GameObject  _panelRoot;

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
        DestroyVisuals();
        _panelRoot = BuildPanel();
        PositionInFrontOfCamera(_panelRoot.transform);
        _panelRoot.SetActive(true);
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
        if (cam == null) return;

        Vector3 lookDir = _panelRoot.transform.position - cam.transform.position;
        if (lookDir.sqrMagnitude > 0.001f)
            _panelRoot.transform.rotation = Quaternion.LookRotation(lookDir);
    }

    private void OnDestroy() => DestroyVisuals();

    // ================================================================
    // PANEL CONSTRUCTION  (mirrors JournalReviewController.BuildChoicePanel)
    // ================================================================

    private GameObject BuildPanel()
    {
        Vector2 panelSize  = new Vector2(640f, 300f);
        float   panelScale = 0.001f;

        // Root — world-space canvas
        var root   = new GameObject("CalibrationChoicePanel");
        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        root.GetComponent<RectTransform>().sizeDelta = panelSize;
        root.AddComponent<CanvasScaler>();
        root.AddComponent<TrackedDeviceGraphicRaycaster>(); // XRI-compatible raycaster
        root.transform.localScale = Vector3.one * panelScale;

        // Background panel
        var bg    = new GameObject("Background");
        bg.transform.SetParent(root.transform, false);
        var bgImg = bg.AddComponent<Image>();
        bgImg.color = s_PanelBg;
        bg.GetComponent<RectTransform>().sizeDelta = panelSize;

        // Top accent bar (mauve, like the VRDialoguePanel border)
        var bar    = new GameObject("AccentBar");
        bar.transform.SetParent(bg.transform, false);
        var barImg = bar.AddComponent<Image>();
        barImg.color = s_AccentMauve;
        var barRect = bar.GetComponent<RectTransform>();
        barRect.anchorMin = new Vector2(0f, 1f);
        barRect.anchorMax = new Vector2(1f, 1f);
        barRect.pivot     = new Vector2(0.5f, 1f);
        barRect.sizeDelta = new Vector2(0f, 26f);

        // Question text
        var qGO  = new GameObject("Question");
        qGO.transform.SetParent(bg.transform, false);
        var qTmp = qGO.AddComponent<TextMeshProUGUI>();
        qTmp.text      = "Apakah kamu ingin kalibrasi ulang?";
        qTmp.fontSize  = 28f;
        qTmp.alignment = TextAlignmentOptions.Center;
        qTmp.color     = s_TextDark;
        qTmp.fontStyle = FontStyles.Bold;
        var qRect = qGO.GetComponent<RectTransform>();
        qRect.anchorMin = new Vector2(0.05f, 0.42f);
        qRect.anchorMax = new Vector2(0.95f, 0.88f);
        qRect.offsetMin = qRect.offsetMax = Vector2.zero;

        // Button row
        var row     = new GameObject("Buttons");
        row.transform.SetParent(bg.transform, false);
        var rowRect = row.AddComponent<RectTransform>();
        rowRect.anchorMin = new Vector2(0.05f, 0.06f);
        rowRect.anchorMax = new Vector2(0.95f, 0.36f);
        rowRect.offsetMin = rowRect.offsetMax = Vector2.zero;

        var yaBtn = MakeButton("Ya, Kalibrasi Ulang", s_BtnYa, Color.white,
            row.transform, new Vector2(0f, 0f), new Vector2(0.44f, 1f));
        yaBtn.onClick.AddListener(OnYaClicked);

        var tidakBtn = MakeButton("Tidak, Lanjutkan", s_BtnTidak, Color.white,
            row.transform, new Vector2(0.56f, 0f), new Vector2(1f, 1f));
        tidakBtn.onClick.AddListener(OnTidakClicked);

        root.SetActive(false);
        return root;
    }

    private static Button MakeButton(string label, Color bgColor, Color textColor,
        Transform parent, Vector2 anchorMin, Vector2 anchorMax)
    {
        var go   = new GameObject(label.Replace(' ', '_'));
        go.transform.SetParent(parent, false);

        var img  = go.AddComponent<Image>();
        img.color = bgColor;

        var btn  = go.AddComponent<Button>();
        btn.targetGraphic = img;

        var rect = go.GetComponent<RectTransform>();
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.offsetMin = rect.offsetMax = Vector2.zero;

        var lblGO = new GameObject("Label");
        lblGO.transform.SetParent(go.transform, false);
        var tmp   = lblGO.AddComponent<TextMeshProUGUI>();
        tmp.text      = label;
        tmp.fontSize  = 22f;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.color     = textColor;
        tmp.fontStyle = FontStyles.Bold;
        var lblRect   = lblGO.GetComponent<RectTransform>();
        lblRect.anchorMin = Vector2.zero;
        lblRect.anchorMax = Vector2.one;
        lblRect.offsetMin = lblRect.offsetMax = Vector2.zero;

        return btn;
    }

    // ================================================================
    // POSITIONING
    // ================================================================

    private static void PositionInFrontOfCamera(Transform panel)
    {
        Camera cam = Camera.main;
        if (cam == null) return;

        Vector3 fwd = cam.transform.forward;
        fwd.y = 0f;
        if (fwd.sqrMagnitude < 0.001f) fwd = cam.transform.forward;
        fwd.Normalize();

        panel.position = cam.transform.position + fwd * kForwardDist + Vector3.down * 0.05f;
        panel.rotation = Quaternion.LookRotation(fwd);
    }

    // ================================================================
    // BUTTON HANDLERS
    // ================================================================

    private void OnYaClicked()
    {
        var cb = _onRecalibrate;
        Hide();
        cb?.Invoke();
    }

    private void OnTidakClicked()
    {
        var cb = _onSkip;
        Hide();
        cb?.Invoke();
    }

    // ================================================================
    // CLEANUP
    // ================================================================

    private void DestroyVisuals()
    {
        if (_panelRoot != null) { Destroy(_panelRoot); _panelRoot = null; }
    }
}
