using System.IO;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Samples.SpatialKeyboard;
using UnityEngine.XR.Interaction.Toolkit.UI;

/// <summary>
/// Editor tool that programmatically builds the VR chat prefabs:
///   1. VRDialoguePanel
///   2. VRControlPanel
///   3. VRHistoryItem
///   4. VRHistoryPanel
///   5. VRKeyboardInputPanel (testing — keyboard text input)
///
/// Run via menu: <b>Tools ▸ EMILIA ▸ Build Chat Prefabs</b>
/// Saves to <c>Assets/Prefabs/</c>. Existing prefabs are overwritten after confirmation.
/// </summary>
public static class ChatPrefabBuilder
{
    // ─── Paths ───────────────────────────────────────────────────────────
    private const string PrefabFolder = "Assets/Prefabs";
    private const string FontPathComfortaa = "Assets/Fonts/Comfortaa-Bold SDF.asset";
    private const string FontPathInter     = "Assets/Fonts/Inter_18pt-Regular SDF.asset";

    // ─── Colors (from visual-design spec) ────────────────────────────────
    private static readonly Color PanelBg       = new(20 / 255f, 20 / 255f, 30 / 255f, 210 / 255f);
    private static readonly Color AccentBlue    = new(115 / 255f, 166 / 255f, 242 / 255f, 153 / 255f);
    private static readonly Color NameColor     = new(140 / 255f, 191 / 255f, 255 / 255f, 1f);
    private static readonly Color BodyTextColor = new(235 / 255f, 235 / 255f, 242 / 255f, 1f);
    private static readonly Color MutedGray     = new(153 / 255f, 166 / 255f, 179 / 255f, 204 / 255f);
    private static readonly Color QuoteBarColor = new(115 / 255f, 166 / 255f, 242 / 255f, 0.4f);
    private static readonly Color ButtonBg      = new(35 / 255f, 35 / 255f, 50 / 255f, 220 / 255f);
    private static readonly Color DeleteRed     = new(0.85f, 0.25f, 0.25f, 1f);

    // ─── Menu Entry ──────────────────────────────────────────────────────
    [MenuItem("Tools/EMILIA/Build Chat Prefabs")]
    public static void BuildAll()
    {
        // Load fonts
        var comfortaa = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontPathComfortaa);
        var inter     = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(FontPathInter);
        if (comfortaa == null || inter == null)
        {
            EditorUtility.DisplayDialog("Missing Fonts",
                $"Could not load TMP font assets.\n\n" +
                $"Comfortaa: {(comfortaa != null ? "OK" : "MISSING")} ({FontPathComfortaa})\n" +
                $"Inter: {(inter != null ? "OK" : "MISSING")} ({FontPathInter})",
                "OK");
            return;
        }

        if (!AssetDatabase.IsValidFolder(PrefabFolder))
            AssetDatabase.CreateFolder("Assets", "Prefabs");

        int built = 0;
        built += BuildDialoguePanel(comfortaa, inter) ? 1 : 0;
        built += BuildControlPanel(comfortaa, inter)  ? 1 : 0;
        built += BuildHistoryItem(inter)              ? 1 : 0; // item before panel (panel refs it)
        built += BuildHistoryPanel(comfortaa, inter)  ? 1 : 0;
        built += BuildKeyboardInputPanel(inter)       ? 1 : 0;

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        EditorUtility.DisplayDialog("Build Chat Prefabs",
            $"Done — {built}/5 prefabs saved to {PrefabFolder}/.", "OK");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  1. VR DIALOGUE PANEL
    // ═══════════════════════════════════════════════════════════════════════
    private static bool BuildDialoguePanel(TMP_FontAsset comfortaa, TMP_FontAsset inter)
    {
        string path = $"{PrefabFolder}/VRDialoguePanel.prefab";
        if (!ConfirmOverwrite(path)) return false;

        // ── Root ──
        var root = new GameObject("VRDialoguePanel");
        var panelComp     = root.AddComponent<VRDialoguePanel>();
        var positioner    = root.AddComponent<DialoguePanelPositioner>();
        var fader         = root.AddComponent<VRDialogueFader>();

        // ── Canvas (World Space, 600×250 units → scale 0.001 → 0.6m × 0.25m) ──
        var canvasGo = CreateChild(root, "Canvas");
        var canvas   = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvasGo.AddComponent<TrackedDeviceGraphicRaycaster>();
        var canvasGroup = canvasGo.AddComponent<CanvasGroup>();

        var canvasRect = canvasGo.GetComponent<RectTransform>();
        canvasRect.sizeDelta     = new Vector2(600, 250);
        canvasRect.localScale    = Vector3.one * 0.001f;
        canvasRect.localPosition = Vector3.zero;

        // ── Panel (background) ──
        var panel = CreateUIChild(canvasGo, "Panel");
        var panelImg = panel.AddComponent<Image>();
        panelImg.color = PanelBg;
        Stretch(panel);

        // ── AccentBar ──
        var accentBar = CreateUIChild(panel, "AccentBar");
        var accentImg = accentBar.AddComponent<Image>();
        accentImg.color = AccentBlue;
        var accentRect = accentBar.GetComponent<RectTransform>();
        SetAnchors(accentRect, new Vector2(0, 1), new Vector2(1, 1));
        accentRect.pivot      = new Vector2(0.5f, 1f);
        accentRect.sizeDelta  = new Vector2(0, 3);
        accentRect.anchoredPosition = Vector2.zero;

        // ── NameLabel ──
        var nameLabel = CreateTMP(panel, "NameLabel", "EMILIA", comfortaa, 36f, NameColor);
        var nameRect  = nameLabel.GetComponent<RectTransform>();
        SetAnchors(nameRect, new Vector2(0, 1), new Vector2(1, 1));
        nameRect.pivot            = new Vector2(0, 1);
        nameRect.anchoredPosition = new Vector2(16, -12);
        nameRect.sizeDelta        = new Vector2(-32, 40);

        // ── QuotePanel (hidden by default) ──
        var quotePanel = CreateUIChild(panel, "QuotePanel");
        quotePanel.SetActive(false);
        var quoteLayout = quotePanel.AddComponent<HorizontalLayoutGroup>();
        quoteLayout.padding = new RectOffset(16, 16, 0, 0);
        quoteLayout.spacing = 8;
        quoteLayout.childForceExpandWidth  = false;
        quoteLayout.childForceExpandHeight = true;
        var quoteRect = quotePanel.GetComponent<RectTransform>();
        SetAnchors(quoteRect, new Vector2(0, 1), new Vector2(1, 1));
        quoteRect.pivot            = new Vector2(0, 1);
        quoteRect.anchoredPosition = new Vector2(0, -56);
        quoteRect.sizeDelta        = new Vector2(0, 50);

        // QuoteBar
        var quoteBar = CreateUIChild(quotePanel, "QuoteBar");
        var quoteBarImg = quoteBar.AddComponent<Image>();
        quoteBarImg.color = QuoteBarColor;
        var qbLayout = quoteBar.AddComponent<LayoutElement>();
        qbLayout.preferredWidth = 3;

        // QuoteText
        var quoteTextGo = CreateTMP(quotePanel, "QuoteText", "", inter, 24f, MutedGray);
        var quoteTextTmp = quoteTextGo.GetComponent<TMP_Text>();
        quoteTextTmp.fontStyle = FontStyles.Italic;
        quoteTextTmp.enableWordWrapping = true;
        quoteTextTmp.overflowMode = TextOverflowModes.Ellipsis;
        var qtLayout = quoteTextGo.AddComponent<LayoutElement>();
        qtLayout.flexibleWidth = 1;

        // ── BodyText ──
        var bodyTextGo = CreateTMP(panel, "BodyText", "", inter, 28f, BodyTextColor);
        var bodyTextTmp = bodyTextGo.GetComponent<TMP_Text>();
        bodyTextTmp.enableWordWrapping = true;
        bodyTextTmp.overflowMode = TextOverflowModes.Truncate;
        bodyTextTmp.richText = true;
        var bodyRect = bodyTextGo.GetComponent<RectTransform>();
        SetAnchors(bodyRect, Vector2.zero, Vector2.one);
        bodyRect.offsetMin = new Vector2(16, 40);   // left, bottom
        bodyRect.offsetMax = new Vector2(-16, -56);  // right, top

        // ── Footer ──
        var footer = CreateUIChild(panel, "Footer");
        var footerLayout = footer.AddComponent<HorizontalLayoutGroup>();
        footerLayout.padding = new RectOffset(16, 16, 0, 8);
        footerLayout.spacing = 8;
        footerLayout.childAlignment = TextAnchor.MiddleLeft;
        footerLayout.childForceExpandWidth = false;
        footerLayout.childForceExpandHeight = false;
        var footerRect = footer.GetComponent<RectTransform>();
        SetAnchors(footerRect, Vector2.zero, new Vector2(1, 0));
        footerRect.pivot    = new Vector2(0.5f, 0);
        footerRect.sizeDelta = new Vector2(0, 32);
        footerRect.anchoredPosition = Vector2.zero;

        // PageIndicator ("1/3")
        var pageIndGo = CreateTMP(footer, "PageIndicator", "", inter, 20f, MutedGray);
        var piLayout  = pageIndGo.AddComponent<LayoutElement>();
        piLayout.preferredWidth = 60;

        // ContinuePrompt ("▼")
        var continueGo = CreateTMP(footer, "ContinuePrompt", "▼", inter, 22f, AccentBlue);
        var cpLayout   = continueGo.AddComponent<LayoutElement>();
        cpLayout.preferredWidth = 30;

        // ── PokeTarget (transparent full-panel button for pagination advance) ──
        var pokeTarget = CreateUIChild(panel, "PokeTarget");
        Stretch(pokeTarget);
        var pokeImg = pokeTarget.AddComponent<Image>();
        pokeImg.color = new Color(0, 0, 0, 0); // invisible but catches raycasts
        pokeImg.raycastTarget = true;
        var pokeBtn = pokeTarget.AddComponent<Button>();
        pokeBtn.targetGraphic = pokeImg;
        pokeBtn.transition = Selectable.Transition.None; // no visual change on click

        // ── Wire serialized fields ──
        var so = new SerializedObject(panelComp);
        so.FindProperty("_bodyText").objectReferenceValue        = bodyTextGo.GetComponent<TMP_Text>();
        so.FindProperty("_nameLabel").objectReferenceValue       = nameLabel.GetComponent<TMP_Text>();
        so.FindProperty("_quotePanel").objectReferenceValue      = quotePanel;
        so.FindProperty("_quoteText").objectReferenceValue       = quoteTextGo.GetComponent<TMP_Text>();
        so.FindProperty("_pageIndicator").objectReferenceValue   = pageIndGo.GetComponent<TMP_Text>();
        so.FindProperty("_continuePrompt").objectReferenceValue  = continueGo.GetComponent<TMP_Text>();
        so.FindProperty("_pokeTarget").objectReferenceValue      = pokeBtn;
        so.ApplyModifiedPropertiesWithoutUndo();

        // ── Save ──
        SavePrefab(root, path);
        return true;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  2. VR CONTROL PANEL
    // ═══════════════════════════════════════════════════════════════════════
    private static bool BuildControlPanel(TMP_FontAsset comfortaa, TMP_FontAsset inter)
    {
        string path = $"{PrefabFolder}/VRControlPanel.prefab";
        if (!ConfirmOverwrite(path)) return false;

        // ── Root ──
        var root = new GameObject("VRControlPanel");
        var panelComp = root.AddComponent<VRControlPanel>();

        // ── Canvas (World Space, 340×120 → 0.34m × 0.12m) ──
        var canvasGo = CreateChild(root, "Canvas");
        var canvas   = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvasGo.AddComponent<TrackedDeviceGraphicRaycaster>();

        var canvasRect = canvasGo.GetComponent<RectTransform>();
        canvasRect.sizeDelta     = new Vector2(440, 120);
        canvasRect.localScale    = Vector3.one * 0.001f;
        canvasRect.localPosition = Vector3.zero;

        // ── Panel bg ──
        var panel = CreateUIChild(canvasGo, "Panel");
        var panelImg = panel.AddComponent<Image>();
        panelImg.color = PanelBg;
        Stretch(panel);
        var panelLayout = panel.AddComponent<HorizontalLayoutGroup>();
        panelLayout.padding = new RectOffset(10, 10, 10, 10);
        panelLayout.spacing = 8;
        panelLayout.childAlignment = TextAnchor.MiddleCenter;
        panelLayout.childForceExpandWidth  = true;
        panelLayout.childForceExpandHeight = true;

        // ── New Chat Button ──
        var (newChatBtn, newChatPoke) = CreatePokeButton(panel, "NewChatButton", "New Chat", inter, 20f);

        // ── Reasoning Toggle ──
        var (reasonBtn, reasonPoke) = CreatePokeButton(panel, "ReasoningToggle", "Standard", inter, 20f);

        // Indicator image (small color dot inside the reasoning button)
        var indicator = CreateUIChild(reasonBtn, "Indicator");
        var indicatorImg = indicator.AddComponent<Image>();
        indicatorImg.color = new Color(0.55f, 0.65f, 0.75f, 1f); // _standardColor
        var indRect = indicator.GetComponent<RectTransform>();
        SetAnchors(indRect, new Vector2(1, 1), new Vector2(1, 1));
        indRect.pivot = new Vector2(1, 1);
        indRect.anchoredPosition = new Vector2(-4, -4);
        indRect.sizeDelta = new Vector2(12, 12);

        // ── History Button ──
        var (historyBtn, historyPoke) = CreatePokeButton(panel, "HistoryButton", "History", inter, 20f);

        // ── Mic Button ──
        var (micBtn, micPoke) = CreatePokeButton(panel, "MicButton", "\ud83c\udfa4 Mic", inter, 20f);

        // ── Wire serialized fields ──
        var so = new SerializedObject(panelComp);
        // _chatBridge and _historyPanel will be wired in the scene (can't reference scene objects from prefab)
        so.FindProperty("_newChatButton").objectReferenceValue          = newChatBtn.GetComponent<Button>();
        so.FindProperty("_reasoningToggleButton").objectReferenceValue  = reasonBtn.GetComponent<Button>();
        so.FindProperty("_historyButton").objectReferenceValue          = historyBtn.GetComponent<Button>();
        so.FindProperty("_micButton").objectReferenceValue              = micBtn.GetComponent<Button>();
        so.FindProperty("_newChatPoke").objectReferenceValue            = newChatPoke;
        so.FindProperty("_reasoningPoke").objectReferenceValue          = reasonPoke;
        so.FindProperty("_historyPoke").objectReferenceValue            = historyPoke;
        so.FindProperty("_micPoke").objectReferenceValue                = micPoke;
        // The toggle label — the TMP_Text child of the ReasoningToggle button
        so.FindProperty("_reasoningLabel").objectReferenceValue         = reasonBtn.GetComponentInChildren<TMP_Text>();
        so.FindProperty("_reasoningIndicator").objectReferenceValue     = indicatorImg;
        so.FindProperty("_micLabel").objectReferenceValue               = micBtn.GetComponentInChildren<TMP_Text>();
        so.ApplyModifiedPropertiesWithoutUndo();

        SavePrefab(root, path);
        return true;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  3. VR HISTORY ITEM (must be built before HistoryPanel)
    // ═══════════════════════════════════════════════════════════════════════
    private static bool BuildHistoryItem(TMP_FontAsset inter)
    {
        string path = $"{PrefabFolder}/VRHistoryItem.prefab";
        if (!ConfirmOverwrite(path)) return false;

        // ── Root ──
        var root = new GameObject("VRHistoryItem");
        var rootRect = root.AddComponent<RectTransform>();
        rootRect.sizeDelta = new Vector2(0, 50);

        // Layout element — VLG in scroll content controls width and height
        var rootLE = root.AddComponent<LayoutElement>();
        rootLE.minHeight       = 50;
        rootLE.preferredHeight = 50;
        rootLE.flexibleWidth   = 1;

        // Background (raycastTarget for TrackedDeviceGraphicRaycaster)
        var bgImg = root.AddComponent<Image>();
        bgImg.color = new Color(1f, 1f, 1f, 0.06f);
        bgImg.raycastTarget = true;

        // Button component (receives all XR input via TrackedDeviceGraphicRaycaster)
        var button = root.AddComponent<Button>();
        button.targetGraphic = bgImg;
        var colors = button.colors;
        colors.normalColor      = new Color(1f, 1f, 1f, 0.06f);
        colors.highlightedColor = new Color(1f, 1f, 1f, 0.15f);
        colors.pressedColor     = new Color(1f, 1f, 1f, 0.25f);
        colors.selectedColor    = new Color(1f, 1f, 1f, 0.12f);
        button.colors = colors;

        // ── TitleLabel ──
        var titleGo = CreateTMP(root, "TitleLabel", "Conversation", inter, 22f, BodyTextColor);
        var titleRect = titleGo.GetComponent<RectTransform>();
        SetAnchors(titleRect, Vector2.zero, Vector2.one);
        titleRect.offsetMin = new Vector2(12, 4);
        titleRect.offsetMax = new Vector2(-50, -4);
        var titleTmp = titleGo.GetComponent<TMP_Text>();
        titleTmp.alignment = TextAlignmentOptions.MidlineLeft;
        titleTmp.overflowMode = TextOverflowModes.Ellipsis;
        titleTmp.enableWordWrapping = false;
        titleTmp.raycastTarget = false;

        // ── DeleteButton ──
        var deleteGo = CreateUIChild(root, "DeleteButton");
        var deleteRect = deleteGo.GetComponent<RectTransform>();
        SetAnchors(deleteRect, new Vector2(1, 0), new Vector2(1, 1));
        deleteRect.pivot = new Vector2(1, 0.5f);
        deleteRect.anchoredPosition = new Vector2(-6, 0);
        deleteRect.sizeDelta = new Vector2(36, 36);

        var delImg = deleteGo.AddComponent<Image>();
        delImg.color = new Color(1f, 1f, 1f, 0.08f);

        var delBtn = deleteGo.AddComponent<Button>();
        delBtn.targetGraphic = delImg;
        var delColors = delBtn.colors;
        delColors.normalColor      = new Color(1f, 1f, 1f, 0.08f);
        delColors.highlightedColor = DeleteRed;
        delColors.pressedColor     = new Color(0.7f, 0.15f, 0.15f, 1f);
        delBtn.colors = delColors;

        var delLabel = CreateTMP(deleteGo, "Label", "✕", inter, 20f, new Color(0.8f, 0.3f, 0.3f, 1f));
        var dlRect   = delLabel.GetComponent<RectTransform>();
        Stretch(delLabel);
        delLabel.GetComponent<TMP_Text>().alignment = TextAlignmentOptions.Center;

        SavePrefab(root, path);
        return true;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  4. VR HISTORY PANEL
    // ═══════════════════════════════════════════════════════════════════════
    private static bool BuildHistoryPanel(TMP_FontAsset comfortaa, TMP_FontAsset inter)
    {
        string path = $"{PrefabFolder}/VRHistoryPanel.prefab";
        if (!ConfirmOverwrite(path)) return false;

        // Load the history item prefab we just built
        var historyItemPrefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{PrefabFolder}/VRHistoryItem.prefab");

        // ── Root ──
        var root = new GameObject("VRHistoryPanel");
        var histPanelComp = root.AddComponent<VRHistoryPanel>();
        var canvasGroup   = root.AddComponent<CanvasGroup>();

        // ── Canvas (World Space, 400×500 → 0.4m × 0.5m) ──
        var canvasGo = CreateChild(root, "Canvas");
        var canvas   = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvasGo.AddComponent<TrackedDeviceGraphicRaycaster>();

        var canvasRect = canvasGo.GetComponent<RectTransform>();
        canvasRect.sizeDelta     = new Vector2(400, 500);
        canvasRect.localScale    = Vector3.one * 0.001f;
        canvasRect.localPosition = Vector3.zero;

        // ── Background ──
        var bg = CreateUIChild(canvasGo, "Background");
        var bgImg = bg.AddComponent<Image>();
        bgImg.color = PanelBg;
        Stretch(bg);

        // ── Header ──
        var header = CreateUIChild(bg, "Header");
        var headerLayout = header.AddComponent<HorizontalLayoutGroup>();
        headerLayout.padding = new RectOffset(12, 12, 8, 8);
        headerLayout.spacing = 8;
        headerLayout.childAlignment = TextAnchor.MiddleCenter;
        headerLayout.childForceExpandWidth = false;
        headerLayout.childForceExpandHeight = true;
        var headerRect = header.GetComponent<RectTransform>();
        SetAnchors(headerRect, new Vector2(0, 1), new Vector2(1, 1));
        headerRect.pivot = new Vector2(0.5f, 1);
        headerRect.sizeDelta = new Vector2(0, 50);
        headerRect.anchoredPosition = Vector2.zero;

        // Title
        var titleGo = CreateTMP(header, "TitleLabel", "Conversations", comfortaa, 28f, NameColor);
        var titleLE = titleGo.AddComponent<LayoutElement>();
        titleLE.flexibleWidth = 1;

        // Close button
        var closeGo = CreateUIChild(header, "CloseButton");
        var closeImg = closeGo.AddComponent<Image>();
        closeImg.color = new Color(1f, 1f, 1f, 0.1f);
        closeImg.raycastTarget = true;
        var closeBtn = closeGo.AddComponent<Button>();
        closeBtn.targetGraphic = closeImg;
        var closeLE = closeGo.AddComponent<LayoutElement>();
        closeLE.preferredWidth = 40;
        closeLE.preferredHeight = 40;

        var closeLabelGo = CreateTMP(closeGo, "Label", "✕", inter, 24f, BodyTextColor);
        Stretch(closeLabelGo);
        closeLabelGo.GetComponent<TMP_Text>().alignment = TextAlignmentOptions.Center;

        // ── ScrollView ──
        var scrollViewGo = CreateUIChild(bg, "ScrollView");
        var scrollRect = scrollViewGo.AddComponent<ScrollRect>();
        scrollRect.horizontal = false;
        scrollRect.vertical   = true;
        scrollRect.movementType = ScrollRect.MovementType.Clamped;
        var svRect = scrollViewGo.GetComponent<RectTransform>();
        SetAnchors(svRect, Vector2.zero, Vector2.one);
        svRect.offsetMin = new Vector2(0, 0);
        svRect.offsetMax = new Vector2(0, -50); // below header

        // Viewport
        var viewport = CreateUIChild(scrollViewGo, "Viewport");
        viewport.AddComponent<RectMask2D>();
        Stretch(viewport);
        scrollRect.viewport = viewport.GetComponent<RectTransform>();

        // Content
        var content = CreateUIChild(viewport, "Content");
        var contentRect = content.GetComponent<RectTransform>();
        SetAnchors(contentRect, new Vector2(0, 1), new Vector2(1, 1));
        contentRect.pivot = new Vector2(0.5f, 1);
        contentRect.sizeDelta = new Vector2(0, 0);
        contentRect.anchoredPosition = Vector2.zero;
        var vlg = content.AddComponent<VerticalLayoutGroup>();
        vlg.padding = new RectOffset(8, 8, 4, 4);
        vlg.spacing = 4;
        vlg.childAlignment = TextAnchor.UpperCenter;
        vlg.childForceExpandWidth  = true;
        vlg.childForceExpandHeight = false;
        var csf = content.AddComponent<ContentSizeFitter>();
        csf.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        scrollRect.content = contentRect;

        // ── EmptyLabel ──
        var emptyGo = CreateTMP(bg, "EmptyLabel", "No conversations yet", inter, 22f, MutedGray);
        var emptyRect = emptyGo.GetComponent<RectTransform>();
        SetAnchors(emptyRect, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f));
        emptyRect.sizeDelta = new Vector2(300, 50);
        emptyGo.GetComponent<TMP_Text>().alignment = TextAlignmentOptions.Center;
        emptyGo.SetActive(false);

        // ── Wire serialized fields ──
        var so = new SerializedObject(histPanelComp);
        // _chatBridge wired in scene
        so.FindProperty("_contentParent").objectReferenceValue    = content.transform;
        so.FindProperty("_historyItemPrefab").objectReferenceValue = historyItemPrefab;
        so.FindProperty("_closeButton").objectReferenceValue       = closeBtn;
        so.FindProperty("_emptyStateLabel").objectReferenceValue   = emptyGo;
        so.ApplyModifiedPropertiesWithoutUndo();

        SavePrefab(root, path);
        return true;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  5. VR KEYBOARD INPUT PANEL (Testing — disable for production)
    // ═══════════════════════════════════════════════════════════════════════
    private static bool BuildKeyboardInputPanel(TMP_FontAsset inter)
    {
        string path = $"{PrefabFolder}/VRKeyboardInputPanel.prefab";
        if (!ConfirmOverwrite(path)) return false;

        // ── Root ──
        var root = new GameObject("VRKeyboardInputPanel");
        var kbInput = root.AddComponent<VRKeyboardInput>();

        // ── Canvas (World Space, 400×70 → 0.4m × 0.07m) ──
        var canvasGo = CreateChild(root, "Canvas");
        var canvas   = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        canvasGo.AddComponent<TrackedDeviceGraphicRaycaster>();

        var canvasRect = canvasGo.GetComponent<RectTransform>();
        canvasRect.sizeDelta     = new Vector2(400, 70);
        canvasRect.localScale    = Vector3.one * 0.001f;
        canvasRect.localPosition = Vector3.zero;

        // ── Background panel ──
        var panel = CreateUIChild(canvasGo, "Panel");
        var panelImg = panel.AddComponent<Image>();
        panelImg.color = PanelBg;
        Stretch(panel);
        var panelLayout = panel.AddComponent<HorizontalLayoutGroup>();
        panelLayout.padding = new RectOffset(8, 8, 8, 8);
        panelLayout.spacing = 6;
        panelLayout.childAlignment = TextAnchor.MiddleCenter;
        panelLayout.childForceExpandHeight = true;
        panelLayout.childForceExpandWidth  = false;

        // ── TMP_InputField ──
        var inputGo = CreateUIChild(panel, "InputField");
        var inputImg = inputGo.AddComponent<Image>();
        inputImg.color = new Color(0.12f, 0.12f, 0.18f, 1f);
        var inputLE = inputGo.AddComponent<LayoutElement>();
        inputLE.flexibleWidth = 1;
        inputLE.minWidth = 280;

        // Text Area (child required by TMP_InputField)
        var textArea = CreateUIChild(inputGo, "Text Area");
        textArea.AddComponent<RectMask2D>();
        Stretch(textArea);
        var textAreaRect = textArea.GetComponent<RectTransform>();
        textAreaRect.offsetMin = new Vector2(8, 4);
        textAreaRect.offsetMax = new Vector2(-8, -4);

        // Placeholder
        var placeholderGo = CreateTMP(textArea, "Placeholder", "Type here...", inter, 22f, MutedGray);
        Stretch(placeholderGo);
        var placeholderTmp = placeholderGo.GetComponent<TMP_Text>();
        placeholderTmp.fontStyle = FontStyles.Italic;
        placeholderTmp.alignment = TextAlignmentOptions.MidlineLeft;
        placeholderTmp.raycastTarget = false;

        // Input text
        var inputTextGo = CreateTMP(textArea, "Text", "", inter, 22f, BodyTextColor);
        Stretch(inputTextGo);
        var inputTextTmp = inputTextGo.GetComponent<TMP_Text>();
        inputTextTmp.alignment = TextAlignmentOptions.MidlineLeft;
        inputTextTmp.raycastTarget = false;

        // TMP_InputField component
        var inputField = inputGo.AddComponent<TMP_InputField>();
        inputField.textViewport = textAreaRect;
        inputField.textComponent = inputTextTmp;
        inputField.placeholder   = placeholderTmp;
        inputField.fontAsset     = inter;
        inputField.pointSize     = 22;
        inputField.lineType      = TMP_InputField.LineType.SingleLine;
        // Required by XRKeyboardDisplay — prevents system keyboard and preserves caret
        inputField.shouldHideSoftKeyboard = true;
        inputField.resetOnDeActivation    = false;

        // ── Send Button ──
        var (sendBtnGo, sendPoke) = CreatePokeButton(panel, "SendButton", "Send", inter, 22f);
        var sendLE = sendBtnGo.AddComponent<LayoutElement>();
        sendLE.preferredWidth = 80;

        // XRKeyboardDisplay — bridges the TMP_InputField to the scene's GlobalNonNativeKeyboard.
        // When the input field gains focus (poke/ray-select), XRKeyboardDisplay calls
        // GlobalNonNativeKeyboard.instance.ShowKeyboard(inputField), opening the world keyboard.
        var kbDisplay = inputGo.AddComponent<XRKeyboardDisplay>();
        var soDisplay = new SerializedObject(kbDisplay);
        // m_UseSceneKeyboard = false → falls through to GlobalNonNativeKeyboard.instance
        var useProp = soDisplay.FindProperty("m_UseSceneKeyboard");
        if (useProp != null) useProp.boolValue = false;
        var clearSubmitProp = soDisplay.FindProperty("m_ClearTextOnSubmit");
        if (clearSubmitProp != null) clearSubmitProp.boolValue = true;
        var clearOpenProp = soDisplay.FindProperty("m_ClearTextOnOpen");
        if (clearOpenProp != null) clearOpenProp.boolValue = false;
        var updateKeyPressProp = soDisplay.FindProperty("m_UpdateOnKeyPress");
        if (updateKeyPressProp != null) updateKeyPressProp.boolValue = true;
        // Wire the input field reference so XRKeyboardDisplay can observe it
        var inputFieldProp = soDisplay.FindProperty("m_InputField");
        if (inputFieldProp != null) inputFieldProp.objectReferenceValue = inputField;
        soDisplay.ApplyModifiedPropertiesWithoutUndo();

        // ── Wire VRKeyboardInput serialized fields ──
        var so = new SerializedObject(kbInput);
        // _chatBridge wired in scene
        so.FindProperty("_inputField").objectReferenceValue      = inputField;
        so.FindProperty("_sendButton").objectReferenceValue      = sendBtnGo.GetComponent<Button>();
        so.FindProperty("_keyboardDisplay").objectReferenceValue = kbDisplay;
        so.ApplyModifiedPropertiesWithoutUndo();

        SavePrefab(root, path);
        return true;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  HELPERS
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Creates a plain child GameObject under <paramref name="parent"/>.</summary>
    private static GameObject CreateChild(GameObject parent, string name)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent.transform, false);
        return go;
    }

    /// <summary>Creates a child with a <see cref="RectTransform"/> (required for UI).</summary>
    private static GameObject CreateUIChild(GameObject parent, string name)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent.transform, false);
        return go;
    }

    /// <summary>Creates a TMP_Text child element.</summary>
    private static GameObject CreateTMP(GameObject parent, string name, string text,
        TMP_FontAsset font, float fontSize, Color color)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent.transform, false);

        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text      = text;
        tmp.font      = font;
        tmp.fontSize  = fontSize;
        tmp.color     = color;
        tmp.raycastTarget = false;
        return go;
    }

    /// <summary>Stretches a RectTransform to fill its parent.</summary>
    private static void Stretch(GameObject go)
    {
        var rt = go.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }

    /// <summary>Sets anchor min/max on a RectTransform.</summary>
    private static void SetAnchors(RectTransform rt, Vector2 min, Vector2 max)
    {
        rt.anchorMin = min;
        rt.anchorMax = max;
    }

    /// <summary>Creates a pokeable button with both Button + XRSimpleInteractable.</summary>
    private static (GameObject btnGo, XRSimpleInteractable poke) CreatePokeButton(
        GameObject parent, string name, string label, TMP_FontAsset font, float fontSize)
    {
        var go = CreateUIChild(parent, name);

        // Background image (acts as Button target graphic)
        var img = go.AddComponent<Image>();
        img.color = ButtonBg;

        // Button for controller ray
        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        var colors = btn.colors;
        colors.normalColor      = ButtonBg;
        colors.highlightedColor = new Color(55 / 255f, 55 / 255f, 75 / 255f, 220 / 255f);
        colors.pressedColor     = new Color(75 / 255f, 75 / 255f, 100 / 255f, 240 / 255f);
        btn.colors = colors;

        // Collider + XRSimpleInteractable for poke
        var col = go.AddComponent<BoxCollider>();
        col.isTrigger = true;
        col.size = new Vector3(100, 100, 10);
        var poke = go.AddComponent<XRSimpleInteractable>();

        // Label
        var labelGo = CreateTMP(go, "Label", label, font, fontSize, BodyTextColor);
        Stretch(labelGo);
        labelGo.GetComponent<TMP_Text>().alignment = TextAlignmentOptions.Center;

        return (go, poke);
    }

    /// <summary>Asks for confirmation if the prefab already exists.</summary>
    private static bool ConfirmOverwrite(string path)
    {
        if (!File.Exists(path)) return true;
        return EditorUtility.DisplayDialog("Overwrite Prefab?",
            $"{Path.GetFileName(path)} already exists.\nOverwrite?", "Overwrite", "Skip");
    }

    /// <summary>Saves a root GameObject as a prefab and destroys the temp object.</summary>
    private static void SavePrefab(GameObject root, string path)
    {
        PrefabUtility.SaveAsPrefabAsset(root, path);
        Object.DestroyImmediate(root);
        Debug.Log($"[ChatPrefabBuilder] Saved {path}");
    }
}
