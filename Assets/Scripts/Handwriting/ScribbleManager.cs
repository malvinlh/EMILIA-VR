using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Hands;
using TMPro;

/// <summary>
/// Apple Scribble-style handwriting-to-text manager for VR journaling.
///
/// TEXT DISPLAY: All recognised text is shown via the ResultText TMP_Text
/// component owned by WhiteboardPageManager.  Style (font, size, colour,
/// alignment) is set entirely in the Inspector on that component.
///
/// PAGES: Each page stores its own word list and undo stack.  Previous /
/// Next arrow buttons are wired by WhiteboardPageManager; ScribbleManager
/// exposes GoToPrevPage() / GoToNextPage() which those buttons call.
/// A new page is created automatically when the current page is full.
///
/// SCRATCH-TO-DELETE: Uses estimated word bounds (stored per word at
/// placement time) and the raw pen touch points.
///
/// UNDO: Hand-menu button repurposed to "Undo" via SetupHandMenuUndo().
///
/// Setup: Add to any GameObject. Assign nothing — all deps are found at runtime.
/// </summary>
[DefaultExecutionOrder(200)]
public class ScribbleManager : MonoBehaviour
{
    // ── Configuration ──────────────────────────────────────────────────
    [Header("Layout")]
    [Tooltip("Horizontal gap between words in metres.")]
    public float wordSpacing = 0.008f;

    [Tooltip("Margin from board edges in metres.")]
    public float boardMargin = 0.015f;

    [Tooltip("Metres reserved at the canvas bottom for navigation buttons.")]
    public float buttonAreaReserve = 0.14f;

    [Header("Scratch Detection")]
    [Tooltip("Minimum direction reversals to detect a scratch gesture.")]
    public int minScratchReversals = 3;

    [Tooltip("Minimum displacement (m) between direction reversals.")]
    public float minReversalDisplacement = 0.005f;

    [Tooltip("Maximum bounding-box diagonal (m) for a scratch gesture.")]
    public float maxScratchExtent = 0.15f;

    [Header("Undo")]
    [Tooltip("Maximum undo history size per page.")]
    public int maxUndoSteps = 30;

    [Header("Debug")]
    [Tooltip("Log scratch evaluation details even when no words are hit.")]
    public bool verboseScratchLog = false;

    // ── Public API ──────────────────────────────────────────────────────
    public event Action<string> OnTextChanged;
    public static ScribbleManager Instance { get; private set; }

    // ── Inner types ─────────────────────────────────────────────────────
    private class ScribbleWord
    {
        public string  text;
        public Vector3 worldCenter;
        public Bounds  worldBounds;
    }

    private enum ActionType { Add, Delete }

    private class ScribbleAction
    {
        public ActionType  type;
        public ScribbleWord word;
        public int          listIndex;
    }

    private class PageData
    {
        public readonly List<ScribbleWord>    words      = new List<ScribbleWord>();
        public readonly Stack<ScribbleAction> undoStack  = new Stack<ScribbleAction>();

        public string GetFullText()
        {
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < words.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(words[i].text);
            }
            return sb.ToString();
        }
    }

    // ── State ────────────────────────────────────────────────────────────
    private readonly List<PageData> pages = new List<PageData> { new PageData() };
    private int currentPageIndex;
    private PageData CurrentPage => pages[currentPageIndex];

    private readonly Queue<WhiteboardPen.StrokeMetadata> pendingMeta =
        new Queue<WhiteboardPen.StrokeMetadata>();
    private bool initialized;

    // ── References ───────────────────────────────────────────────────────
    private Whiteboard          whiteboard;
    private WhiteboardPen       pen;
    private RecognitionPipeline pipeline;
    private DigitalInkBridge    inkBridge;
    private TextMeshProUGUI     measureTMP;   // hidden TMP used only for word-width measurement

    // ── Text orientation (locked at init) ────────────────────────────────
    private Vector3    textRight;         // +X direction text flows (world space)
    private Vector3    textForward;       // +Y direction lines advance (away from user)
    private Vector3    textSurfaceNormal; // board normal facing user (world space)
    private Quaternion textBaseRotation;  // canvas rotation

    // ── Layout state ─────────────────────────────────────────────────────
    private Vector3 cursorPosition;
    private Vector3 lineStartPosition;   // top-left corner of text area (world space)
    private float   lineHeightWorld;
    private float   boardWidthWorld;     // usable width in metres
    private float   textAreaHeight;      // usable height in metres (excludes button area)
    private float   cursorOffsetRight;
    private int     currentLineIndex;

    // ── Scratch ──────────────────────────────────────────────────────────
    private readonly List<Vector3> scratchPoints = new List<Vector3>();
    private bool wasPenDrawing;

    // ── Undo button (hand menu) ──────────────────────────────────────────
    private Button undoButton;

    // ── Event delegates (stored for clean unsubscription) ────────────────
    private Action<string>                      onTextRecognizedDelegate;
    private Action<WhiteboardPen.StrokeMetadata> onStrokesFlushedDelegate;
    private Action                              onBoardClearedDelegate;

    private const string TAG = "[ScribbleManager]";

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
        whiteboard = FindAnyObjectByType<Whiteboard>();
        if (whiteboard != null)
        {
            Debug.Log($"{TAG} Whiteboard found at start — initializing immediately.");
            Initialize();
        }
        else
        {
            Debug.LogWarning($"{TAG} No Whiteboard found in scene — ScribbleManager inactive.");
        }
    }

    private void Initialize()
    {
        // ── Pen ──────────────────────────────────────────────────────
        TryFindPen();
        if (pen != null)
            Debug.Log($"{TAG} WhiteboardPen found: '{pen.gameObject.name}'.");
        else
            Debug.LogWarning($"{TAG} No right-hand WhiteboardPen at init — will retry each frame.");

        // ── Recognition pipeline ─────────────────────────────────────
        pipeline  = RecognitionPipeline.Instance;
        inkBridge = DigitalInkBridge.Instance;

        onTextRecognizedDelegate = OnTextRecognized;
        if (pipeline != null)
        {
            pipeline.OnFinalTextRecognized += onTextRecognizedDelegate;
            Debug.Log($"{TAG} Subscribed to RecognitionPipeline.OnFinalTextRecognized.");
        }
        else if (inkBridge != null)
        {
            inkBridge.OnTextRecognized += onTextRecognizedDelegate;
            Debug.Log($"{TAG} Subscribed to DigitalInkBridge.OnTextRecognized (no pipeline).");
        }
        else
        {
            Debug.LogError($"{TAG} Neither RecognitionPipeline nor DigitalInkBridge found.");
        }

        if (pen != null) SubscribeToPen();

        // ── Text orientation ─────────────────────────────────────────
        LockTextOrientation();

        // ── Canvas setup via WhiteboardPageManager ───────────────────
        SetupCanvas();

        // ── Layout ───────────────────────────────────────────────────
        InitializeLayout();

        // ── Measurement TMP ──────────────────────────────────────────
        SetupMeasureTMP();

        // ── Undo button on hand menu ──────────────────────────────────
        SetupHandMenuUndo();

        initialized = true;

        // Refresh UI (shows empty page 1)
        RefreshUI();

        Debug.Log($"{TAG} Initialized — board {boardWidthWorld:F3}×{textAreaHeight:F3}m, " +
                  $"lineH={lineHeightWorld:F4}m, pages={pages.Count}.");
    }

    // ==================================================================
    // TEXT ORIENTATION  (kept from original — works correctly for rotated boards)
    // ==================================================================

    private void LockTextOrientation()
    {
        if (whiteboard == null) return;

        Transform boardT     = whiteboard.transform;
        Camera    cam        = Camera.main;
        Vector3   boardNormal = boardT.up.normalized;

        // Always text on the side facing the user
        if (cam != null)
        {
            Vector3 toCam = (cam.transform.position - boardT.position).normalized;
            if (Vector3.Dot(boardNormal, toCam) < 0f)
                boardNormal = -boardNormal;
        }
        textSurfaceNormal = boardNormal;

        Vector3 boardRight = boardT.right.normalized;
        Vector3 boardFwd   = boardT.forward.normalized;

        Vector3 camRight = (cam != null) ? cam.transform.right : Vector3.right;
        camRight = Vector3.ProjectOnPlane(camRight, boardNormal);
        if (camRight.sqrMagnitude > 0.001f) camRight.Normalize();
        else camRight = Vector3.right;

        float dotR = Vector3.Dot(boardRight, camRight);
        float dotF = Vector3.Dot(boardFwd,   camRight);

        if (Mathf.Abs(dotR) >= Mathf.Abs(dotF))
        {
            textRight   = dotR >= 0 ? boardRight : -boardRight;
            textForward = Vector3.Cross(boardNormal, textRight).normalized;
        }
        else
        {
            textRight   = dotF >= 0 ? boardFwd : -boardFwd;
            textForward = Vector3.Cross(boardNormal, textRight).normalized;
        }

        // Ensure textForward points away from user (= "up" on the page)
        if (cam != null)
        {
            Vector3 camFwd = cam.transform.forward;
            camFwd = Vector3.ProjectOnPlane(camFwd, boardNormal);
            if (camFwd.sqrMagnitude > 0.001f && Vector3.Dot(textForward, camFwd) < 0f)
                textForward = -textForward;
        }

        // Canvas rotation: +Z into board surface, +Y = textForward, +X = textRight.
        // Using cross(textRight, textForward) gives the board normal pointing toward
        // the user (which is INTO the surface from above for a horizontal board).
        // LookRotation(into-surface, textForward) then gives canvas +X = textRight.
        Vector3 canvasForward = Vector3.Cross(textRight, textForward); // INTO board
        textBaseRotation = Quaternion.LookRotation(canvasForward, textForward);

        Debug.Log($"{TAG} Orientation locked — right={textRight}, forward={textForward}, " +
                  $"boardYRot={boardT.eulerAngles.y:F1}°.");
    }

    // ==================================================================
    // CANVAS SETUP
    // ==================================================================

    private void SetupCanvas()
    {
        var pm = WhiteboardPageManager.Instance;
        if (pm == null || pm.CanvasRect == null)
        {
            Debug.LogWarning($"{TAG} WhiteboardPageManager not found — canvas not set up.");
            return;
        }

        Transform boardT = whiteboard.transform;
        float physW = boardT.lossyScale.x * 10f;
        float physH = boardT.lossyScale.z * 10f;

        // Place canvas slightly above the board surface (along surface normal)
        float safeOffset = Mathf.Max(0.003f, 0.003f);
        Vector3 canvasPos = boardT.position + textSurfaceNormal * safeOffset;

        pm.PositionCanvas(canvasPos, textBaseRotation, physW, physH);
    }

    // ==================================================================
    // LAYOUT
    // ==================================================================

    private void InitializeLayout()
    {
        if (whiteboard == null) return;

        Transform boardT = whiteboard.transform;
        float physW = boardT.lossyScale.x * 10f;
        float physH = boardT.lossyScale.z * 10f;

        boardWidthWorld = physW - 2f * boardMargin;
        textAreaHeight  = physH - buttonAreaReserve - 2f * boardMargin;

        // Line height: read ResultText font size if available, otherwise default 24 px
        var pm = WhiteboardPageManager.Instance;
        float fontSizePx = (pm != null && pm.resultText != null)
            ? pm.resultText.fontSize
            : 24f;
        lineHeightWorld = fontSizePx * 1.2f / WhiteboardPageManager.PPU;

        // Canvas origin (world) — matches where PositionCanvas placed it
        Vector3 canvasOrigin = boardT.position + textSurfaceNormal * 0.003f;

        // Text area top: canvas centre + physH/2 - margin, in textForward direction
        // textForward = "away from user" = canvas +Y = "up on the page"
        float topOffset = physH * 0.5f - boardMargin;

        lineStartPosition = canvasOrigin
            + textForward * topOffset
            - textRight   * (physW * 0.5f - boardMargin);

        cursorPosition    = lineStartPosition;
        cursorOffsetRight = 0f;
        currentLineIndex  = 0;

        Debug.Log($"{TAG} Layout: physW={physW:F3}m physH={physH:F3}m " +
                  $"usable={boardWidthWorld:F3}×{textAreaHeight:F3}m " +
                  $"lineH={lineHeightWorld:F4}m startPos={lineStartPosition}.");
    }

    // ==================================================================
    // MEASUREMENT TMP  (invisible — only used for word-width estimation)
    // ==================================================================

    private void SetupMeasureTMP()
    {
        if (measureTMP != null) return;

        var pm = WhiteboardPageManager.Instance;
        if (pm?.CanvasRect == null) return;

        var go = new GameObject("__WordMeasure");
        go.hideFlags = HideFlags.HideAndDontSave;
        go.transform.SetParent(pm.CanvasRect, false);

        measureTMP = go.AddComponent<TextMeshProUGUI>();
        measureTMP.enableWordWrapping = false;
        measureTMP.overflowMode       = TextOverflowModes.Overflow;
        measureTMP.enableAutoSizing   = false;
        measureTMP.color              = Color.clear;

        // Copy style from ResultText so measurements match rendered widths
        if (pm.resultText != null)
        {
            measureTMP.font             = pm.resultText.font;
            measureTMP.fontSize         = pm.resultText.fontSize;
            measureTMP.fontStyle        = pm.resultText.fontStyle;
            measureTMP.characterSpacing = pm.resultText.characterSpacing;
        }

        var cr = go.GetComponent<CanvasRenderer>();
        if (cr != null) cr.SetAlpha(0f);
    }

    /// <summary>Returns the estimated world-space width of <paramref name="word"/>.</summary>
    private float MeasureWordWidth(string word)
    {
        if (measureTMP == null || string.IsNullOrEmpty(word))
            return word != null ? word.Length * 0.010f : 0f;

        measureTMP.text = word;
        measureTMP.ForceMeshUpdate();
        return measureTMP.preferredWidth / WhiteboardPageManager.PPU;
    }

    // ==================================================================
    // PEN BINDING
    // ==================================================================

    private void TryFindPen()
    {
        foreach (var p in FindObjectsByType<WhiteboardPen>(
                     FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (p.handedness == Handedness.Right) { pen = p; break; }
        }
    }

    private void SubscribeToPen()
    {
        if (pen == null) return;
        onStrokesFlushedDelegate = meta => pendingMeta.Enqueue(meta);
        pen.OnStrokesFlushed    += onStrokesFlushedDelegate;
        onBoardClearedDelegate   = ClearCurrentPage;
        pen.OnBoardCleared      += onBoardClearedDelegate;
        Debug.Log($"{TAG} Subscribed to WhiteboardPen '{pen.gameObject.name}'.");
    }

    // ==================================================================
    // UPDATE
    // ==================================================================

    private void Update()
    {
        if (!initialized) return;

        // Deferred pen binding (XR Origin children may be inactive at Start)
        if (pen == null)
        {
            TryFindPen();
            if (pen != null)
            {
                Debug.Log($"{TAG} WhiteboardPen found on deferred search: '{pen.gameObject.name}'.");
                SubscribeToPen();
            }
        }

        CheckScratchGesture();
    }

    // ==================================================================
    // RECOGNITION HANDLER
    // ==================================================================

    private void OnTextRecognized(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            Debug.Log($"{TAG} OnTextRecognized: empty — ignored.");
            return;
        }

        Debug.Log($"{TAG} OnTextRecognized: \"{text}\"");

        if (whiteboard != null) whiteboard.ClearToBackground();
        if (pendingMeta.Count > 0) pendingMeta.Dequeue();

        string[] parts = text.Trim().Split(
            new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

        Debug.Log($"{TAG} Placing {parts.Length} word(s): [{string.Join(", ", parts)}]");

        foreach (string word in parts)
            PlaceWord(word);

        Debug.Log($"{TAG} Page {currentPageIndex + 1}/{pages.Count} — " +
                  $"\"{CurrentPage.GetFullText()}\"");

        FireTextChanged();
    }

    // ==================================================================
    // WORD PLACEMENT & LAYOUT
    // ==================================================================

    private void PlaceWord(string word)
    {
        float wordWidthWorld = MeasureWordWidth(word);

        // ── Clamp single oversized word to board width ────────────────
        if (wordWidthWorld > boardWidthWorld && word.Length > 1)
        {
            // Estimate max chars that fit; trim and append ellipsis
            float avgCharW = wordWidthWorld / word.Length;
            int maxChars = Mathf.Max(1, Mathf.FloorToInt(boardWidthWorld / avgCharW) - 1);
            word = word[..maxChars] + "…";
            wordWidthWorld = MeasureWordWidth(word);
            Debug.Log($"{TAG} Word truncated to fit board: \"{word}\"");
        }

        // ── Line-wrap if necessary ───────────────────────────────────
        if (cursorOffsetRight + wordWidthWorld > boardWidthWorld &&
            cursorOffsetRight > 0.001f)
        {
            currentLineIndex++;
            lineStartPosition -= textForward * lineHeightWorld;
            cursorPosition     = lineStartPosition;
            cursorOffsetRight  = 0f;
            Debug.Log($"{TAG} Line wrap → line {currentLineIndex}.");
        }

        // ── Estimated world bounds (for scratch detection) ───────────
        // textRight and textForward may have components in both X and Z
        // (rotated board), so we project onto world AABB.
        Vector3 wordWorldCenter = cursorPosition + textRight * (wordWidthWorld * 0.5f);
        Vector3 halfExtentRight = textRight   * (wordWidthWorld * 0.5f + 0.005f);
        Vector3 halfExtentFwd   = textForward * (lineHeightWorld * 0.5f + 0.005f);
        Bounds  wb = new Bounds(wordWorldCenter, new Vector3(
            Mathf.Abs(halfExtentRight.x) + Mathf.Abs(halfExtentFwd.x),
            0.05f,
            Mathf.Abs(halfExtentRight.z) + Mathf.Abs(halfExtentFwd.z)));

        Debug.Log($"{TAG} Placed \"{word}\" at {cursorPosition}, " +
                  $"width={wordWidthWorld:F4}m, line={currentLineIndex}.");

        // ── Advance cursor ───────────────────────────────────────────
        cursorOffsetRight += wordWidthWorld + wordSpacing;
        cursorPosition     = lineStartPosition + textRight * cursorOffsetRight;

        var sw = new ScribbleWord
        {
            text        = word,
            worldCenter = wb.center,
            worldBounds = wb
        };

        CurrentPage.words.Add(sw);
        PushUndo(new ScribbleAction
        {
            type      = ActionType.Add,
            word      = sw,
            listIndex = CurrentPage.words.Count - 1
        });

        // ── Update display ───────────────────────────────────────────
        RefreshUI();

        // ── Board-full: silently create next page so Next button appears ─
        if (IsBoardFull() && currentPageIndex == pages.Count - 1)
        {
            pages.Add(new PageData());
            Debug.Log($"{TAG} Board full — created page {pages.Count}. " +
                      "Press Next to continue on the new page.");
            RefreshUI(); // re-run so Next button becomes visible
        }
    }

    private bool IsBoardFull()
    {
        // Primary: ask TMP whether the current text already overflows the
        // ResultText bounds.  preferredHeight is the natural (unconstrained)
        // height TMP needs; rect.height is the actual visible area.
        var pm = WhiteboardPageManager.Instance;
        if (pm?.resultText != null)
        {
            pm.resultText.ForceMeshUpdate();
            float needed   = pm.resultText.preferredHeight;
            float available = ((RectTransform)pm.resultText.transform).rect.height;
            return needed > available;
        }

        // Fallback when UI is unavailable: use layout estimate
        return (currentLineIndex + 1) * lineHeightWorld >= textAreaHeight;
    }

    // ==================================================================
    // PAGE NAVIGATION
    // ==================================================================

    /// <summary>Navigate to the previous page (called by WhiteboardPageManager).</summary>
    public void GoToPrevPage()
    {
        if (currentPageIndex <= 0)
        {
            Debug.Log($"{TAG} Already on first page.");
            return;
        }
        GoToPage(currentPageIndex - 1);
    }

    /// <summary>Navigate to the next page (called by WhiteboardPageManager).</summary>
    public void GoToNextPage()
    {
        if (currentPageIndex >= pages.Count - 1)
        {
            Debug.Log($"{TAG} Already on last page — no next page.");
            return;
        }
        GoToPage(currentPageIndex + 1);
    }

    private void GoToPage(int index)
    {
        if (index < 0 || index >= pages.Count) return;

        currentPageIndex = index;

        // Clear ink — past pages show only typed text, not ink strokes
        if (whiteboard != null) whiteboard.ClearToBackground();

        // Reset cursor to beginning of page, then advance to end of that page's words
        InitializeLayout();
        RecomputeCursor();

        RefreshUI();
        FireTextChanged();

        Debug.Log($"{TAG} GoToPage({index + 1}/{pages.Count}) — " +
                  $"\"{CurrentPage.GetFullText()}\"");
    }

    // ==================================================================
    // UNDO
    // ==================================================================

    private void PushUndo(ScribbleAction action)
    {
        var stack = CurrentPage.undoStack;
        stack.Push(action);
        if (stack.Count > maxUndoSteps)
        {
            var arr = stack.ToArray();
            stack.Clear();
            for (int i = Mathf.Min(maxUndoSteps - 1, arr.Length - 1); i >= 0; i--)
                stack.Push(arr[i]);
        }
    }

    public void Undo()
    {
        var stack = CurrentPage.undoStack;
        if (stack.Count == 0)
        {
            Debug.Log($"{TAG} Undo: stack empty.");
            return;
        }

        var action = stack.Pop();
        switch (action.type)
        {
            case ActionType.Add:
                CurrentPage.words.Remove(action.word);
                RecomputeCursor();
                Debug.Log($"{TAG} Undo ADD — removed \"{action.word.text}\". " +
                          $"Words: {CurrentPage.words.Count}.");
                break;

            case ActionType.Delete:
                int idx = Mathf.Clamp(action.listIndex, 0, CurrentPage.words.Count);
                CurrentPage.words.Insert(idx, action.word);
                RecomputeCursor();
                Debug.Log($"{TAG} Undo DELETE — restored \"{action.word.text}\" at {idx}.");
                break;
        }

        RefreshUI();
        FireTextChanged();
    }

    private void RecomputeCursor()
    {
        // Reset layout then replay word advances on current page
        InitializeLayout();
        foreach (var w in CurrentPage.words)
        {
            float ww = MeasureWordWidth(w.text);
            if (cursorOffsetRight + ww > boardWidthWorld && cursorOffsetRight > 0.001f)
            {
                currentLineIndex++;
                lineStartPosition -= textForward * lineHeightWorld;
                cursorPosition     = lineStartPosition;
                cursorOffsetRight  = 0f;
            }
            cursorOffsetRight += ww + wordSpacing;
            cursorPosition     = lineStartPosition + textRight * cursorOffsetRight;
        }
    }

    // ==================================================================
    // SCRATCH DETECTION
    // ==================================================================

    private void CheckScratchGesture()
    {
        if (pen == null) return;

        bool drawing = pen.IsDrawing;

        if (drawing)
        {
            Vector3? tp = pen.CurrentTouchWorldPoint;
            if (tp.HasValue) scratchPoints.Add(tp.Value);

            if (scratchPoints.Count >= 15 && scratchPoints.Count % 5 == 0)
            {
                int reversals = CountReversals(scratchPoints);
                if (verboseScratchLog)
                    Debug.Log($"{TAG} Scratch mid-stroke — pts={scratchPoints.Count}, " +
                              $"reversals={reversals}/{minScratchReversals}.");
                if (reversals >= minScratchReversals)
                {
                    TryHandleScratch(reversals);
                    return;
                }
            }
        }
        else if (wasPenDrawing)
        {
            if (scratchPoints.Count >= 10)
            {
                int reversals = CountReversals(scratchPoints);
                if (verboseScratchLog || reversals >= minScratchReversals)
                    Debug.Log($"{TAG} Scratch end-of-stroke — pts={scratchPoints.Count}, " +
                              $"reversals={reversals}/{minScratchReversals}.");
                if (reversals >= minScratchReversals)
                {
                    TryHandleScratch(reversals);
                }
            }
            scratchPoints.Clear();
        }

        wasPenDrawing = drawing;
    }

    private int CountReversals(List<Vector3> pts)
    {
        if (pts.Count < 10) return 0;

        float minX = float.MaxValue, maxX = float.MinValue;
        float minZ = float.MaxValue, maxZ = float.MinValue;
        foreach (var p in pts)
        {
            if (p.x < minX) minX = p.x; if (p.x > maxX) maxX = p.x;
            if (p.z < minZ) minZ = p.z; if (p.z > maxZ) maxZ = p.z;
        }
        float extX = maxX - minX, extZ = maxZ - minZ;
        float diag = Mathf.Sqrt(extX * extX + extZ * extZ);
        if (diag > maxScratchExtent || diag < 0.005f) return 0;

        bool useX = extX >= extZ;
        int  reversals = 0;
        float lastDir = 0f, accum = 0f;

        for (int i = 1; i < pts.Count; i++)
        {
            float d = useX ? (pts[i].x - pts[i-1].x) : (pts[i].z - pts[i-1].z);
            accum += Mathf.Abs(d);
            if (Mathf.Abs(d) < 0.0005f) continue;
            float dir = Mathf.Sign(d);
            if (lastDir != 0f && dir != lastDir && accum >= minReversalDisplacement)
            {
                reversals++;
                accum = 0f;
            }
            lastDir = dir;
        }
        return reversals;
    }

    private bool TryHandleScratch(int reversals = -1)
    {
        Bounds scratch = new Bounds(scratchPoints[0], Vector3.zero);
        foreach (var p in scratchPoints) scratch.Encapsulate(p);
        scratch.Expand(0.01f);

        bool deleted = false;
        var  deletedWords = new List<string>();

        for (int i = CurrentPage.words.Count - 1; i >= 0; i--)
        {
            var w = CurrentPage.words[i];
            if (OverlapsXZ(scratch, w.worldBounds))
            {
                deletedWords.Add(w.text);
                DeleteWord(w);
                deleted = true;
            }
        }

        if (deleted)
        {
            if (pen != null)       pen.ClearStrokeBuffer();
            if (whiteboard != null) whiteboard.ClearToBackground();
            scratchPoints.Clear();

            string revStr = reversals >= 0 ? $"reversals={reversals}, " : "";
            Debug.Log($"{TAG} Scratch-to-delete — {revStr}" +
                      $"deleted [{string.Join(", ", deletedWords)}]. " +
                      $"Words remaining: {CurrentPage.words.Count}.");
        }
        else if (verboseScratchLog)
        {
            Debug.Log($"{TAG} Scratch detected (reversals={reversals}) — no words overlapped.");
        }

        return deleted;
    }

    private void DeleteWord(ScribbleWord word)
    {
        int index = CurrentPage.words.IndexOf(word);
        if (index < 0)
        {
            Debug.LogWarning($"{TAG} DeleteWord: '{word.text}' not in current page.");
            return;
        }

        CurrentPage.words.RemoveAt(index);
        RecomputeCursor();

        PushUndo(new ScribbleAction
        {
            type      = ActionType.Delete,
            word      = word,
            listIndex = index
        });

        RefreshUI();
        FireTextChanged();

        Debug.Log($"{TAG} Deleted \"{word.text}\" at index {index}.");
    }

    private static bool OverlapsXZ(Bounds a, Bounds b)
    {
        return Mathf.Abs(a.center.x - b.center.x) < (a.extents.x + b.extents.x)
            && Mathf.Abs(a.center.z - b.center.z) < (a.extents.z + b.extents.z);
    }

    // ==================================================================
    // CLEAR / RESET
    // ==================================================================

    /// <summary>Clear the current page's words and whiteboard ink.</summary>
    public void ClearCurrentPage()
    {
        int count = CurrentPage.words.Count;
        CurrentPage.words.Clear();
        CurrentPage.undoStack.Clear();
        pendingMeta.Clear();
        scratchPoints.Clear();

        if (whiteboard != null) whiteboard.ClearToBackground();

        InitializeLayout();
        RefreshUI();
        FireTextChanged();

        Debug.Log($"{TAG} ClearCurrentPage — removed {count} word(s).");
    }

    /// <summary>Clear all pages and reset to a single blank page.</summary>
    public void ClearAll()
    {
        pages.Clear();
        pages.Add(new PageData());
        currentPageIndex = 0;

        pendingMeta.Clear();
        scratchPoints.Clear();

        if (whiteboard != null) whiteboard.ClearToBackground();

        InitializeLayout();
        RefreshUI();
        FireTextChanged();

        Debug.Log($"{TAG} ClearAll.");
    }

    // ==================================================================
    // UNDO — HAND MENU BUTTON
    // ==================================================================

    /// <summary>
    /// Finds the wrist / hand menu and repurposes its first button as "Undo".
    /// Uses TMP_Text (base class) so it works regardless of component type.
    /// </summary>
    private void SetupHandMenuUndo()
    {
        var handMenu = GameObject.Find("Hand Menu With Button Activation");
        if (handMenu == null)
        {
            Debug.LogWarning($"{TAG} Hand menu not found — Undo button not configured.");
            return;
        }

        var buttons = handMenu.GetComponentsInChildren<Button>(true);
        if (buttons.Length == 0)
        {
            Debug.LogWarning($"{TAG} No Button in hand menu — Undo button not configured.");
            return;
        }

        undoButton = buttons[0];

        var label = undoButton.GetComponentInChildren<TMP_Text>(true);
        if (label != null)
            label.text = "Undo";
        else
            Debug.LogWarning($"{TAG} No TMP_Text on '{undoButton.gameObject.name}' — label unchanged.");

        undoButton.onClick.RemoveAllListeners();
        undoButton.onClick.AddListener(Undo);

        Debug.Log($"{TAG} Undo button configured on '{undoButton.gameObject.name}'.");
    }

    // ==================================================================
    // PUBLIC HELPERS
    // ==================================================================

    /// <summary>Returns accumulated text across all pages.</summary>
    public string GetFullJournalText()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < pages.Count; i++)
        {
            if (sb.Length > 0) sb.Append('\n');
            sb.Append($"[Page {i + 1}] ");
            sb.Append(pages[i].GetFullText());
        }
        return sb.ToString();
    }

    // ==================================================================
    // INTERNAL HELPERS
    // ==================================================================

    private void RefreshUI()
    {
        WhiteboardPageManager.Instance?.UpdateUI(
            CurrentPage.GetFullText(),
            currentPageIndex,
            pages.Count);
        UpdateWordBoundsFromTMP();
    }

    /// <summary>
    /// Reads the actual rendered positions of each word from TMP's characterInfo
    /// and updates worldBounds for accurate scratch-to-delete hit testing.
    /// This is required because TMP alignment (centre, right, etc.) places text
    /// at positions that differ from our left-aligned cursor calculations.
    /// </summary>
    private void UpdateWordBoundsFromTMP()
    {
        var pm = WhiteboardPageManager.Instance;
        if (pm?.resultText == null) return;

        pm.resultText.ForceMeshUpdate();
        TMP_TextInfo textInfo = pm.resultText.textInfo;
        var rt    = (RectTransform)pm.resultText.transform;
        var words = CurrentPage.words;

        int charOffset = 0;
        for (int wi = 0; wi < words.Count; wi++)
        {
            int wordLen    = words[wi].text.Length;
            int lastCharIdx = charOffset + wordLen - 1;
            if (lastCharIdx >= textInfo.characterCount) break;

            TMP_CharacterInfo fc = textInfo.characterInfo[charOffset];
            TMP_CharacterInfo lc = textInfo.characterInfo[lastCharIdx];

            // Convert all four corners from ResultText local (canvas px) to world space.
            // rt.TransformPoint handles canvas position, rotation, and 0.001 scale.
            Vector3 w0 = rt.TransformPoint(fc.bottomLeft.x,  fc.bottomLeft.y,  0f);
            Vector3 w1 = rt.TransformPoint(fc.topLeft.x,     fc.topLeft.y,     0f);
            Vector3 w2 = rt.TransformPoint(lc.bottomRight.x, lc.bottomRight.y, 0f);
            Vector3 w3 = rt.TransformPoint(lc.topRight.x,    lc.topRight.y,    0f);

            float minX = Mathf.Min(w0.x, w1.x, w2.x, w3.x);
            float maxX = Mathf.Max(w0.x, w1.x, w2.x, w3.x);
            float minZ = Mathf.Min(w0.z, w1.z, w2.z, w3.z);
            float maxZ = Mathf.Max(w0.z, w1.z, w2.z, w3.z);

            Vector3 center = new Vector3((minX + maxX) * 0.5f,
                                         (w0.y + w2.y) * 0.5f,
                                         (minZ + maxZ) * 0.5f);
            Vector3 size   = new Vector3(maxX - minX + 0.01f, 0.05f, maxZ - minZ + 0.01f);

            words[wi].worldCenter = center;
            words[wi].worldBounds = new Bounds(center, size);

            charOffset += wordLen + 1; // +1 for the space separator
        }
    }

    private void FireTextChanged()
    {
        OnTextChanged?.Invoke(CurrentPage.GetFullText());
    }

    // ==================================================================
    // CLEANUP
    // ==================================================================

    private void OnDestroy()
    {
        if (pipeline != null && onTextRecognizedDelegate != null)
            pipeline.OnFinalTextRecognized -= onTextRecognizedDelegate;
        else if (inkBridge != null && onTextRecognizedDelegate != null)
            inkBridge.OnTextRecognized -= onTextRecognizedDelegate;

        if (pen != null)
        {
            if (onStrokesFlushedDelegate != null) pen.OnStrokesFlushed -= onStrokesFlushedDelegate;
            if (onBoardClearedDelegate   != null) pen.OnBoardCleared   -= onBoardClearedDelegate;
        }

        if (undoButton != null)
            undoButton.onClick.RemoveListener(Undo);

        if (measureTMP != null && measureTMP.gameObject != null)
            Destroy(measureTMP.gameObject);

        if (Instance == this) Instance = null;

        Debug.Log($"{TAG} Destroyed — all events unsubscribed.");
    }
}
