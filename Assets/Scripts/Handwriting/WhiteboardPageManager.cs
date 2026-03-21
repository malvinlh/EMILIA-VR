using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Owns the whiteboard UI canvas (ResultText, PrevArrow, NextArrow) and
/// audio for page turns.  Canvas *positioning* and *sizing* are driven
/// by ScribbleManager.Initialize() so the axes always match the
/// handwriting-text orientation.  This component just exposes references
/// and handles button events / result-text updates.
///
/// Setup in Inspector:
///   uiCanvas   → the WorldSpace Canvas that holds ResultText and arrow buttons
///   resultText → the TMP_Text child that shows accumulated page text
///   prevButton → PreviousArrow Button
///   nextButton → NextArrow Button
///   pageTurnSfx → page-turn audio clip (Assets/Sounds/SFX/PageTurning/…)
/// </summary>
[DefaultExecutionOrder(100)]
public class WhiteboardPageManager : MonoBehaviour
{
    // ── Serialized references ──────────────────────────────────────────
    [Header("Whiteboard UI")]
    [Tooltip("The WorldSpace Canvas that holds all whiteboard UI.")]
    public Canvas uiCanvas;

    [Tooltip("TMP label showing the accumulated text for the current page.")]
    public TMP_Text resultText;

    [Tooltip("Previous-page arrow button.")]
    public Button prevButton;

    [Tooltip("Next-page arrow button.")]
    public Button nextButton;

    [Tooltip("TMP label showing the current page number (e.g. '1 / 2').")]
    public TMP_Text pageNumberText;

    [Header("Audio")]
    [Tooltip("Played once each time the user flips a page.")]
    public AudioClip pageTurnSfx;

    [Header("Editor Preview")]
    [Tooltip("The Whiteboard this UI overlays. Required for editor layout preview.")]
    public Whiteboard whiteboard;

    [Tooltip("Match boardMargin in ScribbleManager for an accurate text-area overlay.")]
    public float previewBoardMargin = 0.015f;

    [Tooltip("Match buttonAreaReserve in ScribbleManager for an accurate preview.")]
    public float previewButtonAreaReserve = 0.14f;

    // ── Canvas pixel density (must match ScribbleManager usage) ───────
    /// <summary>Canvas pixels per metre.  1 px = 1 mm at PPU = 1000.</summary>
    public const float PPU = 1000f;

    // ── Properties ─────────────────────────────────────────────────────
    public static WhiteboardPageManager Instance { get; private set; }

    /// <summary>The RectTransform of the UI canvas — ScribbleManager uses
    /// this to size/position the canvas and to measure canvas-local coords.</summary>
    public RectTransform CanvasRect =>
        uiCanvas != null ? (RectTransform)uiCanvas.transform : null;

    // ── Preview state (written by PreviewLayout, read by editor script) ──
    /// <summary>True once PreviewLayout() has run successfully at least once.</summary>
    [HideInInspector] public bool    previewHasData;
    [HideInInspector] public Vector3 previewCanvasPos;
    [HideInInspector] public Vector3 previewTextRight;
    [HideInInspector] public Vector3 previewTextForward;
    [HideInInspector] public float   previewPhysW;
    [HideInInspector] public float   previewPhysH;

    // ── Private ────────────────────────────────────────────────────────
    private AudioSource audioSource;
    private const string TAG = "[WhiteboardPageManager]";

    // ==================================================================
    // EDITOR PREVIEW
    // ==================================================================

    /// <summary>
    /// Context-menu entry: preview using the last active Scene View camera
    /// (falls back to Camera.main at runtime).
    /// </summary>
    [ContextMenu("Preview Layout")]
    public void PreviewLayout()
    {
        Camera cam = Camera.main;
#if UNITY_EDITOR
        var sv = UnityEditor.SceneView.lastActiveSceneView;
        if (sv != null) cam = sv.camera;
#endif
        PreviewLayout(cam);
    }

    /// <summary>
    /// Replicates ScribbleManager's orientation logic for the given camera,
    /// positions the canvas on the whiteboard surface, and stores layout
    /// data in the preview fields so the editor script can draw overlays.
    ///
    /// Safe to call in both Edit mode and Play mode.
    /// </summary>
    public void PreviewLayout(Camera cam)
    {
        if (uiCanvas == null)
        {
            Debug.LogWarning($"{TAG} PreviewLayout: uiCanvas not assigned.");
            return;
        }
        if (whiteboard == null)
        {
            Debug.LogWarning($"{TAG} PreviewLayout: whiteboard not assigned.");
            return;
        }

        // ── Replicate ScribbleManager.LockTextOrientation() ──────────
        Transform boardT     = whiteboard.transform;
        Vector3   boardNormal = boardT.up.normalized;

        if (cam != null)
        {
            Vector3 toCam = (cam.transform.position - boardT.position).normalized;
            if (Vector3.Dot(boardNormal, toCam) < 0f) boardNormal = -boardNormal;
        }

        Vector3 boardRight = boardT.right.normalized;
        Vector3 boardFwd   = boardT.forward.normalized;

        Vector3 camRight = (cam != null) ? cam.transform.right : Vector3.right;
        camRight = Vector3.ProjectOnPlane(camRight, boardNormal);
        if (camRight.sqrMagnitude > 0.001f) camRight.Normalize();
        else                                camRight = Vector3.right;

        float dotR = Vector3.Dot(boardRight, camRight);
        float dotF = Vector3.Dot(boardFwd,   camRight);

        Vector3 textRight, textForward;
        if (Mathf.Abs(dotR) >= Mathf.Abs(dotF))
        {
            textRight   = dotR >= 0f ? boardRight : -boardRight;
            textForward = Vector3.Cross(boardNormal, textRight).normalized;
        }
        else
        {
            textRight   = dotF >= 0f ? boardFwd : -boardFwd;
            textForward = Vector3.Cross(boardNormal, textRight).normalized;
        }

        if (cam != null)
        {
            Vector3 camFwd = cam.transform.forward;
            camFwd = Vector3.ProjectOnPlane(camFwd, boardNormal);
            if (camFwd.sqrMagnitude > 0.001f && Vector3.Dot(textForward, camFwd) < 0f)
                textForward = -textForward;
        }

        Vector3    canvasForward = Vector3.Cross(textRight, textForward);
        Quaternion canvasRot     = Quaternion.LookRotation(canvasForward, textForward);

        float   physW     = boardT.lossyScale.x * 10f;
        float   physH     = boardT.lossyScale.z * 10f;
        Vector3 canvasPos = boardT.position + boardNormal * 0.003f;

        // ── Store for editor overlay ──────────────────────────────────
        previewHasData     = true;
        previewCanvasPos   = canvasPos;
        previewTextRight   = textRight;
        previewTextForward = textForward;
        previewPhysW       = physW;
        previewPhysH       = physH;

        // ── Apply canvas transform ─────────────────────────────────────
        PositionCanvas(canvasPos, canvasRot, physW, physH);

        // ── Remove 'Button' text labels from arrow buttons ─────────────
#if UNITY_EDITOR
        EditorCleanButtonLabel(prevButton);
        EditorCleanButtonLabel(nextButton);
#endif

        Debug.Log($"{TAG} PreviewLayout — physW={physW * 100f:F1} cm, " +
                  $"physH={physH * 100f:F1} cm, " +
                  $"canvas {physW * PPU:F0}×{physH * PPU:F0} px, " +
                  $"textRight={textRight}, textForward={textForward}.");
    }

    // ==================================================================
    // LIFECYCLE
    // ==================================================================

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
    }

    private void Start()
    {
        // Audio source (3-D so the sound comes from the whiteboard)
        audioSource = GetComponent<AudioSource>();
        if (audioSource == null)
        {
            audioSource = gameObject.AddComponent<AudioSource>();
            audioSource.playOnAwake  = false;
            audioSource.spatialBlend = 1f;
            audioSource.rolloffMode  = AudioRolloffMode.Linear;
            audioSource.maxDistance  = 3f;
        }

        // Wire buttons → ScribbleManager (which manages page logic)
        if (prevButton != null)
            prevButton.onClick.AddListener(OnPrevClicked);
        if (nextButton != null)
            nextButton.onClick.AddListener(OnNextClicked);

        // Remove the auto-generated "Button" text label from arrow buttons
        CleanButtonLabel(prevButton);
        CleanButtonLabel(nextButton);

        // Ensure ResultText never renders outside its RectTransform bounds
        if (resultText != null)
        {
            resultText.enableWordWrapping = true;
            resultText.overflowMode       = TMPro.TextOverflowModes.Truncate;
        }

        // Start dimmed (interactable=false); ScribbleManager calls UpdateUI() once initialized.
        SetButtonVisibility(false, false);

        Debug.Log($"{TAG} Ready. Canvas will be positioned by ScribbleManager.");
    }

    private void OnDestroy()
    {
        if (prevButton != null) prevButton.onClick.RemoveListener(OnPrevClicked);
        if (nextButton != null) nextButton.onClick.RemoveListener(OnNextClicked);
        if (Instance == this) Instance = null;
    }

    // ── Button label cleanup ───────────────────────────────────────────

    /// <summary>Destroys any TMP / legacy Text children of <paramref name="btn"/> at runtime.</summary>
    private static void CleanButtonLabel(Button btn)
    {
        if (btn == null) return;
        foreach (var t in btn.GetComponentsInChildren<TMP_Text>(true))
            Destroy(t.gameObject);
        foreach (var t in btn.GetComponentsInChildren<Text>(true))
            Destroy(t.gameObject);
    }

#if UNITY_EDITOR
    /// <summary>Editor-mode variant using DestroyImmediate.</summary>
    private static void EditorCleanButtonLabel(Button btn)
    {
        if (btn == null) return;
        foreach (var t in btn.GetComponentsInChildren<TMP_Text>(true))
            DestroyImmediate(t.gameObject);
        foreach (var t in btn.GetComponentsInChildren<Text>(true))
            DestroyImmediate(t.gameObject);
    }
#endif

    // ==================================================================
    // CANVAS SETUP  (called once by ScribbleManager.Initialize)
    // ==================================================================

    /// <summary>
    /// Detaches the canvas from whatever parent it has in the scene
    /// (e.g. JournalTable with a huge non-uniform scale) and places it
    /// flat on the whiteboard surface using the text orientation axes
    /// computed by ScribbleManager.
    ///
    /// <paramref name="canvasPos"/>   world-space centre of the canvas
    /// <paramref name="canvasRot"/>   rotation so canvas face points UP
    /// <paramref name="physW"/>       board width  in metres (canvas +X direction)
    /// <paramref name="physH"/>       board height in metres (canvas +Y direction)
    /// </summary>
    public void PositionCanvas(Vector3 canvasPos, Quaternion canvasRot,
                               float physW, float physH)
    {
        if (uiCanvas == null)
        {
            Debug.LogWarning($"{TAG} uiCanvas not assigned — skipping canvas setup.");
            return;
        }

        var rt = (RectTransform)uiCanvas.transform;

        // Detach from parent so our world-space assignment is not distorted
        // by the parent's non-uniform scale (e.g. JournalTable).
        rt.SetParent(null, worldPositionStays: false);

        rt.position   = canvasPos;
        rt.rotation   = canvasRot;
        rt.localScale = Vector3.one * (1f / PPU);   // 1 px = 1 mm
        rt.sizeDelta  = new Vector2(physW * PPU, physH * PPU);
        rt.pivot      = new Vector2(0.5f, 0.5f);

        uiCanvas.renderMode = RenderMode.WorldSpace;

        // Children (ResultText, PrevButton, NextButton) keep whatever positions
        // you set in the Inspector — do NOT call LayoutChildren here so the
        // manually designed scene layout is preserved at runtime.

        Debug.Log($"{TAG} Canvas positioned at {canvasPos}, " +
                  $"size=({physW * PPU:F0}×{physH * PPU:F0} px), scale=1/{PPU:F0}.");
    }

    /// <summary>
    /// Positions ResultText and arrow buttons within the canvas.
    /// Call this whenever the canvas size changes.
    /// </summary>
    public void LayoutChildren(float canvasW, float canvasH)
    {
        const float btnSize    = 50f;   // button square size in canvas pixels
        const float btnPadding = 10f;   // gap from canvas bottom edge

        float btnCentreY = -canvasH * 0.5f + btnPadding + btnSize * 0.5f;

        if (prevButton != null)
        {
            var rt = (RectTransform)prevButton.transform;
            rt.anchorMin        = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot            = new Vector2(0.5f, 0.5f);
            rt.sizeDelta        = new Vector2(btnSize, btnSize);
            rt.anchoredPosition = new Vector2(-canvasW * 0.25f, btnCentreY);
            rt.localRotation    = Quaternion.identity; // flat in canvas plane
        }

        if (nextButton != null)
        {
            var rt = (RectTransform)nextButton.transform;
            rt.anchorMin        = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot            = new Vector2(0.5f, 0.5f);
            rt.sizeDelta        = new Vector2(btnSize, btnSize);
            rt.anchoredPosition = new Vector2(canvasW * 0.25f, btnCentreY);
            rt.localRotation    = Quaternion.identity;
        }

        if (resultText != null)
        {
            var rt = (RectTransform)resultText.transform;
            float bottomReserve = btnPadding * 2f + btnSize;  // space for buttons
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(20f, bottomReserve);   // left, bottom
            rt.offsetMax = new Vector2(-20f, -20f);           // right, top
            rt.localRotation = Quaternion.identity;

        }

        Debug.Log($"{TAG} Children laid out: canvas {canvasW:F0}×{canvasH:F0} px.");
    }

    // ==================================================================
    // BUTTON EVENTS
    // ==================================================================

    private void OnPrevClicked()
    {
        if (IsWritingInProgress()) return;
        PlayPageTurn();
        ScribbleManager.Instance?.GoToPrevPage();
    }

    private void OnNextClicked()
    {
        if (IsWritingInProgress()) return;
        PlayPageTurn();
        ScribbleManager.Instance?.GoToNextPage();
    }

    /// <summary>Returns true while any WhiteboardPen is actively touching the board.</summary>
    private static bool IsWritingInProgress()
    {
        foreach (var p in FindObjectsByType<WhiteboardPen>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (p.IsDrawing) return true;
        return false;
    }

    private void PlayPageTurn()
    {
        if (pageTurnSfx != null && audioSource != null)
            audioSource.PlayOneShot(pageTurnSfx);
    }

    // ==================================================================
    // CALLED BY ScribbleManager
    // ==================================================================

    /// <summary>
    /// Updates ResultText and arrow button visibility.
    /// Called by ScribbleManager whenever words change or the page turns.
    /// </summary>
    public void UpdateUI(string pageText, int pageIndex, int totalPages)
    {
        if (resultText != null)
            resultText.text = pageText;

        if (pageNumberText != null)
            pageNumberText.text = $"{pageIndex + 1} / {totalPages}";

        SetButtonVisibility(hasPrev: pageIndex > 0,
                            hasNext: pageIndex < totalPages - 1);
    }

    private void SetButtonVisibility(bool hasPrev, bool hasNext)
    {
        // Keep buttons always visible — use interactable so Unity automatically
        // applies DisabledColor (semi-transparent) when the action is unavailable.
        if (prevButton != null) prevButton.interactable = hasPrev;
        if (nextButton != null) nextButton.interactable = hasNext;
    }

    // ==================================================================
    // EDITOR SIMULATION  (tests overflow detection without a build)
    // ==================================================================

    /// <summary>
    /// Fills ResultText with repeated sample words one at a time until TMP
    /// reports overflow, then shows the → button and page-number as they
    /// would appear at runtime when page 1 is full.
    ///
    /// Call via the Inspector context menu or the Editor tool button.
    /// Revert with "Reset Simulate" in the same menu.
    /// </summary>
    [ContextMenu("Simulate: Fill Page")]
    public void SimulatePageFill()
    {
        if (resultText == null)
        {
            Debug.LogWarning($"{TAG} SimulatePageFill: resultText not assigned.");
            return;
        }

        resultText.enableWordWrapping = true;
        resultText.overflowMode       = TMPro.TextOverflowModes.Truncate;

        var    rt        = (RectTransform)resultText.transform;
        string[] words   = { "the", "quick", "brown", "fox", "jumps", "over",
                              "the", "lazy", "dog", "and", "wrote", "in", "journal" };
        var    sb        = new System.Text.StringBuilder();
        int    wordCount = 0;

        while (wordCount < 2000)
        {
            string next      = words[wordCount % words.Length];
            string candidate = sb.Length > 0 ? sb + " " + next : next;

            resultText.text = candidate;
            resultText.ForceMeshUpdate();

            if (resultText.preferredHeight > rt.rect.height)
            {
                // Revert to last state that still fit
                resultText.text = sb.ToString();
                resultText.ForceMeshUpdate();

                // Show what runtime would show: next button visible, page "1 / 2"
                SetButtonVisibility(hasPrev: false, hasNext: true);
                if (pageNumberText != null) pageNumberText.text = "1 / 2";

                Debug.Log($"{TAG} SimulatePageFill: page full after {wordCount} words " +
                          $"({sb.Length} chars). preferredH={resultText.preferredHeight:F1} " +
                          $"available={rt.rect.height:F1} px.");
                return;
            }

            sb.Append(sb.Length > 0 ? " " + next : next);
            wordCount++;
        }

        Debug.LogWarning($"{TAG} SimulatePageFill: reached limit without overflow — " +
                         "check that the canvas has been positioned (Apply Layout Preview).");
    }

    [ContextMenu("Simulate: Reset")]
    public void SimulateReset()
    {
        if (resultText != null) resultText.text = string.Empty;
        if (pageNumberText != null) pageNumberText.text = "1 / 1";
        SetButtonVisibility(hasPrev: false, hasNext: false);
        Debug.Log($"{TAG} SimulateReset complete.");
    }
}
