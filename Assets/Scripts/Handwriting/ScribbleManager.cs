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

    [Header("Undo")]
    [Tooltip("Maximum undo history size per page.")]
    public int maxUndoSteps = 30;

    // ── Public API ──────────────────────────────────────────────────────
    public event Action<string> OnTextChanged;
    public static ScribbleManager Instance { get; private set; }

    // ── Inner types ─────────────────────────────────────────────────────
    private class ScribbleWord
    {
        public string text;
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
    // Page 0 is always the first title page.  _titlePageCount tracks how many
    // leading pages are title pages (grows when the title overflows a page).
    // Content pages begin at index _titlePageCount.
    private readonly List<PageData> pages = new List<PageData> { new PageData(), new PageData() };
    private int currentPageIndex;
    private int _titlePageCount = 1;
    // Once the user moves from the last title page to content via Next,
    // keep the title/content split stable for the rest of the session.
    private bool _titleBoundaryLocked;
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
    private Vector3 lineStartPosition;   // top-left corner of text area (world space)
    private float   lineHeightWorld;
    private float   boardWidthWorld;     // usable width in metres
    private float   textAreaHeight;      // usable height in metres (excludes button area)
    private float   cursorOffsetRight;
    private int     currentLineIndex;
    private int     _insertCursor = -1;  // ≥0 = insert before that word index; -1 = append

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

        var pm = WhiteboardPageManager.Instance;

        // Use HandwritingArea canvas width if assigned, else fall back to board width.
        if (pm?.handwritingArea != null)
            boardWidthWorld = pm.handwritingArea.rect.width / WhiteboardPageManager.PPU;
        else
            boardWidthWorld = physW - 2f * boardMargin;

        textAreaHeight = physH - buttonAreaReserve - 2f * boardMargin;

        // Line height: read ResultText font size if available, otherwise default 24 px
        float fontSizePx = (pm?.resultText != null) ? pm.resultText.fontSize : 24f;
        lineHeightWorld = fontSizePx * 1.2f / WhiteboardPageManager.PPU;

        // lineStartPosition is still used by SetupCanvas/canvas orientation — keep it.
        Vector3 canvasOrigin = boardT.position + textSurfaceNormal * 0.003f;
        float topOffset = physH * 0.5f - boardMargin;
        lineStartPosition = canvasOrigin
            + textForward * topOffset
            - textRight   * (physW * 0.5f - boardMargin);

        cursorOffsetRight = 0f;
        currentLineIndex  = 0;

        Debug.Log($"{TAG} Layout: boardW={boardWidthWorld:F3}m textH={textAreaHeight:F3}m " +
                  $"lineH={lineHeightWorld:F4}m.");
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

        var sw = new ScribbleWord { text = word };
        int insertIndex;

        if (_insertCursor >= 0 && _insertCursor <= CurrentPage.words.Count)
        {
            // ── Insert at inline cursor (inline editing mode) ─────────
            CurrentPage.words.Insert(_insertCursor, sw);
            insertIndex = _insertCursor;
            _insertCursor++;
            RecomputeCursor(); // fix layout state after mid-list insertion
        }
        else
        {
            // ── Append mode: use layout state for line-wrap detection ─
            if (cursorOffsetRight + wordWidthWorld > boardWidthWorld &&
                cursorOffsetRight > 0.001f)
            {
                currentLineIndex++;
                lineStartPosition -= textForward * lineHeightWorld;
                cursorOffsetRight  = 0f;
                Debug.Log($"{TAG} Line wrap → line {currentLineIndex}.");
            }
            cursorOffsetRight += wordWidthWorld + wordSpacing;
            CurrentPage.words.Add(sw);
            insertIndex = CurrentPage.words.Count - 1;
        }

        PushUndo(new ScribbleAction
        {
            type      = ActionType.Add,
            word      = sw,
            listIndex = insertIndex
        });

        // ── Update display ───────────────────────────────────────────
        RefreshUI();

        // ── Board-full: create next page and auto-advance ────────────────
        if (IsBoardFull())
        {
            bool isTitlePage = currentPageIndex < _titlePageCount;
            bool isLastTitlePage = isTitlePage && currentPageIndex == _titlePageCount - 1;

            if (isLastTitlePage && !_titleBoundaryLocked)
            {
                // Title overflow: insert a new title page right before content.
                pages.Insert(_titlePageCount, new PageData());
                _titlePageCount++;
                Debug.Log($"{TAG} Title full — auto-advancing to title page {_titlePageCount}.");
                GoToPage(currentPageIndex + 1);
            }
            else if (!isTitlePage && currentPageIndex == pages.Count - 1)
            {
                pages.Add(new PageData());
                Debug.Log($"{TAG} Content full — auto-advancing to content page {pages.Count - _titlePageCount}.");
                GoToPage(currentPageIndex + 1);
            }
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

        // Lock the title/content split once the user explicitly leaves
        // the last title page using Next.
        if (!_titleBoundaryLocked && currentPageIndex == _titlePageCount - 1)
        {
            _titleBoundaryLocked = true;
            Debug.Log($"{TAG} Title finalized at page {_titlePageCount}. Following pages are content.");
        }

        GoToPage(currentPageIndex + 1);
    }

    private void GoToPage(int index)
    {
        if (index < 0 || index >= pages.Count) return;

        currentPageIndex = index;
        _insertCursor = -1;

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

        if (action.type == ActionType.Add)
            TrimEmptyPages();
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
                cursorOffsetRight = 0f;
            }
            cursorOffsetRight += ww + wordSpacing;
        }
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
        TrimEmptyPages();
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
        _insertCursor = -1;

        if (whiteboard != null) whiteboard.ClearToBackground();

        InitializeLayout();
        RefreshUI();
        FireTextChanged();

        Debug.Log($"{TAG} ClearCurrentPage — removed {count} word(s).");
        TrimEmptyPages();
    }

    /// <summary>Clear all pages and reset to title page + one blank content page.</summary>
    public void ClearAll()
    {
        pages.Clear();
        pages.Add(new PageData()); // page 0 = title
        pages.Add(new PageData()); // page 1 = first content page
        currentPageIndex = 0;
        _titlePageCount  = 1;
        _titleBoundaryLocked = false;
        _insertCursor    = -1;

        pendingMeta.Clear();

        if (whiteboard != null) whiteboard.ClearToBackground();

        InitializeLayout();
        RefreshUI();
        FireTextChanged();

        Debug.Log($"{TAG} ClearAll.");
    }

    // ==================================================================
    // PUBLIC HELPERS
    // ==================================================================

    /// <summary>
    /// Deletes the last word on the current page.
    /// Called by the Backspace button in the Footer.
    /// </summary>
    public void DeleteLastWord()
    {
        if (!initialized) return;
        var words = CurrentPage.words;
        if (words.Count == 0) return;

        if (_insertCursor > 0 && _insertCursor <= words.Count)
        {
            // Backspace at cursor: delete the word just before the cursor position
            int targetIndex = _insertCursor - 1;
            DeleteWord(words[targetIndex]);
            _insertCursor = Mathf.Max(0, _insertCursor - 1);
        }
        else
        {
            DeleteWord(words[words.Count - 1]);
        }
    }

    /// <summary>
    /// Deletes words at indices [startWordIndex .. endWordIndex] inclusive,
    /// then places the insert cursor at startWordIndex.
    /// Called by JournalInlineCursor when the user backspaces a word selection.
    /// </summary>
    public void DeleteWordRange(int startWordIndex, int endWordIndex)
    {
        if (!initialized) return;
        var words = CurrentPage.words;
        if (words.Count == 0) return;

        startWordIndex = Mathf.Clamp(startWordIndex, 0, words.Count - 1);
        endWordIndex   = Mathf.Clamp(endWordIndex,   0, words.Count - 1);
        if (startWordIndex > endWordIndex) return;

        int count = endWordIndex - startWordIndex + 1;
        for (int i = 0; i < count; i++)
        {
            var w = words[startWordIndex];
            words.RemoveAt(startWordIndex);
            PushUndo(new ScribbleAction
            {
                type      = ActionType.Delete,
                word      = w,
                listIndex = startWordIndex
            });
        }

        _insertCursor = Mathf.Clamp(startWordIndex, 0, words.Count);
        RecomputeCursor();
        RefreshUI();
        FireTextChanged();

        Debug.Log($"{TAG} DeleteWordRange [{startWordIndex}..{endWordIndex}] — {count} word(s) removed.");
        TrimEmptyPages();
    }

    /// <summary>
    /// Sets the inline-edit insert cursor to the given word-gap index.
    /// Index 0 = before first word; index N = after word N-1 (between N-1 and N).
    /// While active, new words and backspace operate at this position.
    /// Called by JournalInlineCursor.
    /// </summary>
    public void SetInsertCursor(int wordIndex)
    {
        _insertCursor = Mathf.Clamp(wordIndex, 0, CurrentPage.words.Count);
        Debug.Log($"{TAG} Insert cursor → {_insertCursor} (of {CurrentPage.words.Count} words).");
    }

    /// <summary>Clears the inline cursor, returning to end-of-text append mode.</summary>
    public void ClearInsertCursor()
    {
        if (_insertCursor < 0) return;
        _insertCursor = -1;
        Debug.Log($"{TAG} Insert cursor cleared.");
    }

    /// <summary>Word count on the current page. Used by JournalInlineCursor.</summary>
    public int CurrentWordCount => CurrentPage.words.Count;

    /// <summary>Current insert-cursor word-gap index (≥0), or -1 in append mode.
    /// JournalInlineCursor reads this to keep its caret visual in sync after
    /// words are inserted.</summary>
    public int InsertCursorIndex => _insertCursor;

    /// <summary>Zero-based index of the page currently on screen.
    /// JournalInlineCursor watches this to dismiss selection/cursor on page turn.</summary>
    public int CurrentPageIndex => currentPageIndex;

    /// <summary>Number of leading pages that are title pages (page 0 .. TitlePageCount-1).
    /// Content starts at index TitlePageCount.</summary>
    public int TitlePageCount => _titlePageCount;

    /// <summary>
    /// Injects voice-transcribed text into the current page.
    /// Does NOT clear whiteboard ink — active strokes remain until the recognition timer fires.
    /// Called by JournalMicController after Gemini speech-to-text.
    /// </summary>
    public void AddVoiceText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (!initialized) return;

        Debug.Log($"{TAG} AddVoiceText: \"{text}\"");

        string[] parts = text.Trim().Split(
            new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (string word in parts)
        {
            // If the current page is full, navigate to the next one before placing
            // the word so it appears on screen rather than being truncated.
            if (IsBoardFull() && currentPageIndex < pages.Count - 1)
                GoToPage(currentPageIndex + 1);

            PlaceWord(word);
        }

        RefreshUI();
        FireTextChanged();

        Debug.Log($"{TAG} Voice added — page {currentPageIndex + 1}: \"{CurrentPage.GetFullText()}\"");
    }

    /// <summary>Returns accumulated text across all pages (includes title as [Page 1]).</summary>
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

    /// <summary>
    /// Returns the text written across all title pages (pages 0 .. TitlePageCount-1),
    /// joined with a space. Title can span multiple pages if the user wrote a long title.
    /// </summary>
    public string GetTitleText()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < _titlePageCount && i < pages.Count; i++)
        {
            var t = pages[i].GetFullText();
            if (string.IsNullOrWhiteSpace(t)) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(t);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Returns the journal content from all content pages (pages TitlePageCount+),
    /// newline-separated. Content always starts after all title pages.
    /// </summary>
    public string GetContentText()
    {
        var sb = new System.Text.StringBuilder();
        for (int i = _titlePageCount; i < pages.Count; i++)
        {
            var text = pages[i].GetFullText();
            if (string.IsNullOrWhiteSpace(text)) continue;
            if (sb.Length > 0) sb.Append('\n');
            sb.Append(text);
        }
        return sb.ToString();
    }

    // ==================================================================
    // INTERNAL HELPERS
    // ==================================================================

    /// <summary>
    /// Removes the current page if it is empty and is not the sole page of its
    /// type (title or content), then navigates to the preceding page.
    /// Called after any word-removal operation (delete, undo, clear).
    /// </summary>
    private void TrimEmptyPages()
    {
        if (CurrentPage.words.Count > 0) return;

        bool isTitlePage = currentPageIndex < _titlePageCount;
        bool canRemove   = isTitlePage
            ? _titlePageCount > 1
            : pages.Count - _titlePageCount > 1;

        if (!canRemove) return;

        int removeIdx = currentPageIndex;
        pages.RemoveAt(removeIdx);
        if (isTitlePage) _titlePageCount--;

        currentPageIndex = Mathf.Clamp(removeIdx - 1, 0, pages.Count - 1);
        _insertCursor    = -1;

        if (whiteboard != null) whiteboard.ClearToBackground();
        InitializeLayout();
        RecomputeCursor();
        RefreshUI();
        FireTextChanged();

        Debug.Log($"{TAG} Removed empty {(isTitlePage ? "title" : "content")} page. " +
                  $"Now on page {currentPageIndex + 1}/{pages.Count}.");
    }

    private void RefreshUI()
    {
        WhiteboardPageManager.Instance?.UpdateUI(
            CurrentPage.GetFullText(),
            currentPageIndex,
            pages.Count,
            _titlePageCount);
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

        if (measureTMP != null && measureTMP.gameObject != null)
            Destroy(measureTMP.gameObject);

        if (Instance == this) Instance = null;

        Debug.Log($"{TAG} Destroyed — all events unsubscribed.");
    }
}
