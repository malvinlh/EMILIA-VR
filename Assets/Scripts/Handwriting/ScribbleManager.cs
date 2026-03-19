using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Hands;
using TMPro;

/// <summary>
/// Apple Scribble-like handwriting-to-text manager for VR journaling.
/// Converts handwritten strokes into clean typed text rendered on the
/// whiteboard surface, with scratch-to-delete and undo via hand gestures.
///
/// <b>Features:</b>
/// <list type="bullet">
///   <item>Handwriting to clean text (flowing layout on the board)</item>
///   <item>Scratch-to-delete (zigzag over a word to erase it)</item>
///   <item>Undo (hand / wrist menu button)</item>
///   <item>Auto-clear (handwriting ink clears after recognition)</item>
/// </list>
///
/// <b>Text rendering:</b> Uses a WorldSpace Canvas + TextMeshProUGUI so that
/// (a) the UI shader is always included in URP Android builds (no stripping),
/// (b) text follows the whiteboard if it drifts/repositions, and
/// (c) text is visually "on" the board surface from any VR camera angle.
///
/// <b>Setup:</b> Add to any GameObject in a journal scene. Finds its
/// dependencies (Whiteboard, WhiteboardPen, recognition pipeline) at runtime.
/// </summary>
[DefaultExecutionOrder(200)]
public class ScribbleManager : MonoBehaviour
{
    // ── Configuration ────────────────────────────────────────────────
    [Header("Text Rendering")]
    [Tooltip("Height above the whiteboard surface for rendered text.")]
    public float textHeightOffset = 0.002f;

    [Tooltip("Scale of the 3D text objects (1 canvas pixel = textScale metres).")]
    public float textScale = 0.004f;

    [Tooltip("Font size for TextMeshProUGUI text (canvas pixels).")]
    public float fontSize = 36f;

    [Tooltip("Color of rendered text.")]
    public Color textColor = new Color(0.15f, 0.15f, 0.15f, 1f);

    [Tooltip("Optional TMP font asset. Uses TMP default if null.")]
    public TMP_FontAsset fontAsset;

    [Tooltip("Horizontal spacing between words (world units).")]
    public float wordSpacing = 0.008f;

    [Tooltip("Margin from board edges (world units).")]
    public float boardMargin = 0.015f;

    [Header("Scratch Detection")]
    [Tooltip("Minimum direction reversals to detect a scratch gesture.")]
    public int minScratchReversals = 4;

    [Tooltip("Minimum displacement (m) between direction reversals.")]
    public float minReversalDisplacement = 0.005f;

    [Tooltip("Maximum bounding-box diagonal (m) for a scratch gesture.")]
    public float maxScratchExtent = 0.15f;

    [Header("Undo")]
    [Tooltip("Maximum undo history size.")]
    public int maxUndoSteps = 30;

    [Header("Debug")]
    [Tooltip("Log scratch gesture evaluation details even when no words are hit.")]
    public bool verboseScratchLog = false;

    // ── Public API ───────────────────────────────────────────────────
    /// <summary>Fired when the accumulated journal text changes.</summary>
    public event Action<string> OnTextChanged;

    public static ScribbleManager Instance { get; private set; }

    // ── Internal types ───────────────────────────────────────────────
    private class ScribbleWord
    {
        public string text;
        public Vector3 worldCenter;
        public Bounds worldBounds;
        public GameObject gameObject;
    }

    private enum ActionType { Add, Delete }

    private class ScribbleAction
    {
        public ActionType type;
        public ScribbleWord word;
        public int listIndex;
    }

    // ── State ────────────────────────────────────────────────────────
    private readonly List<ScribbleWord> words = new List<ScribbleWord>();
    private readonly Stack<ScribbleAction> undoStack = new Stack<ScribbleAction>();
    private readonly Queue<WhiteboardPen.StrokeMetadata> pendingMeta =
        new Queue<WhiteboardPen.StrokeMetadata>();
    private bool initialized;

    // ── References ───────────────────────────────────────────────────
    private Whiteboard whiteboard;
    private WhiteboardPen pen;
    private RecognitionPipeline pipeline;
    private DigitalInkBridge inkBridge;

    // ── Text orientation (locked at init) ────────────────────────────
    private Vector3 textRight;
    private Vector3 textForward;
    private Vector3 textSurfaceNormal;
    private Quaternion textBaseRotation;

    // ── WorldSpace canvas (text is rendered here) ─────────────────────
    // Using TextMeshProUGUI instead of TextMeshPro (3D) because the UGUI
    // shader is always bundled in Android/URP builds — the 3D TMP
    // Distance Field shader can be stripped on Quest, making text invisible.
    private Canvas scribbleCanvas;

    // ── Flowing layout state ─────────────────────────────────────────
    private Vector3 cursorPosition;
    private Vector3 lineStartPosition;
    private float lineHeight;
    private float boardWidthAlongRight;
    private float boardDepthAlongForward;
    private float cursorOffsetRight;
    private int currentLineIndex;

    // ── Scratch detection ────────────────────────────────────────────
    private readonly List<Vector3> scratchPoints = new List<Vector3>();
    private bool wasPenDrawing;

    // ── Undo (hand menu button) ───────────────────────────────────────
    private Button undoButton;

    // ── Event delegates (stored for unsubscription) ──────────────────
    private Action<string> onTextRecognizedDelegate;
    private Action<WhiteboardPen.StrokeMetadata> onStrokesFlushedDelegate;
    private Action onBoardClearedDelegate;

    // ── Log tag ──────────────────────────────────────────────────────
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
        var utils = FindAnyObjectByType<WhiteboardUtils>();
        if (utils != null)
            utils.OnWhiteboardSpawned += OnWhiteboardSpawned;

        whiteboard = FindAnyObjectByType<Whiteboard>();
        if (whiteboard != null)
        {
            Debug.Log($"{TAG} Whiteboard found at start — initializing immediately.");
            Initialize();
        }
        else
        {
            Debug.Log($"{TAG} No Whiteboard in scene yet — waiting for OnWhiteboardSpawned.");
        }
    }

    private void OnWhiteboardSpawned(GameObject wb)
    {
        whiteboard = wb.GetComponent<Whiteboard>();
        if (whiteboard != null && !initialized)
        {
            Debug.Log($"{TAG} OnWhiteboardSpawned: received '{wb.name}' — initializing.");
            Initialize();
        }
        else if (whiteboard == null)
        {
            Debug.LogWarning($"{TAG} OnWhiteboardSpawned: '{wb.name}' has no Whiteboard component.");
        }
    }

    private void Initialize()
    {
        // Find right-hand pen — include inactive objects because the XR Origin
        // prefab children may still be inactive at Start time.
        TryFindPen();

        if (pen != null)
            Debug.Log($"{TAG} WhiteboardPen (right hand) found: '{pen.gameObject.name}'.");
        else
            Debug.LogWarning($"{TAG} No right-hand WhiteboardPen found at init — will retry each frame.");

        pipeline = RecognitionPipeline.Instance;
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
            Debug.Log($"{TAG} RecognitionPipeline not found — subscribed to DigitalInkBridge.OnTextRecognized.");
        }
        else
        {
            Debug.LogError($"{TAG} Neither RecognitionPipeline nor DigitalInkBridge found.");
        }

        if (pen != null)
            SubscribeToPen();

        var rtd = FindAnyObjectByType<RecognizedTextDisplay>();
        if (rtd != null)
        {
            string rtdObjName = rtd.gameObject.name;
            if (rtd.displayText != null) rtd.displayText.text = "";
            rtd.StopAllCoroutines();
            Destroy(rtd);
            Debug.Log($"{TAG} Destroyed RecognizedTextDisplay on '{rtdObjName}'.");
        }

        LockTextOrientation();
        InitializeLayout();
        CreateOrUpdateCanvas();
        SetupHandMenuUndo();

        initialized = true;
        Debug.Log($"{TAG} Initialized. Board width={boardWidthAlongRight:F3}m, lineHeight={lineHeight:F4}m.");
    }

    private void LockTextOrientation()
    {
        if (whiteboard == null) return;

        Transform boardT = whiteboard.transform;
        Camera cam = Camera.main;
        Vector3 boardNormal = boardT.up.normalized;

        // Always place text on the side currently facing the user.
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
        float dotF = Vector3.Dot(boardFwd, camRight);

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

        if (cam != null)
        {
            Vector3 camFwd = cam.transform.forward;
            camFwd = Vector3.ProjectOnPlane(camFwd, boardNormal);
            if (camFwd.sqrMagnitude > 0.001f && Vector3.Dot(textForward, camFwd) < 0f)
                textForward = -textForward;
        }

        // Canvas +X = textRight (text flows left-to-right).
        // Canvas +Y = textForward (lines advance away from user).
        // Canvas +Z = -boardNormal (canvas face points toward user from above).
        textBaseRotation = Quaternion.LookRotation(-boardNormal, textForward);

        Vector3 localX = textBaseRotation * Vector3.right;
        Vector3 localZ = textBaseRotation * Vector3.forward;
        Debug.Log($"{TAG} Text orientation locked — right={textRight}, forward={textForward}, " +
                  $"boardYRot={boardT.eulerAngles.y:F1}°, " +
                  $"canvasLocalX={localX} (should≈textRight), canvasLocalZ={localZ} (should point INTO board).");
    }

    /// <summary>The whiteboard mesh is a Unity Plane (10×10 units).</summary>
    private const float PLANE_MESH_SIZE = 10f;

    private void InitializeLayout()
    {
        if (whiteboard == null) return;

        Transform boardT = whiteboard.transform;
        Vector3 boardNormal = textSurfaceNormal.sqrMagnitude > 0.001f
            ? textSurfaceNormal
            : boardT.up.normalized;

        Vector3 boardRight = boardT.right;
        Vector3 boardFwd   = boardT.forward;
        boardRight.y = 0f; boardRight.Normalize();
        boardFwd.y   = 0f; boardFwd.Normalize();

        float physicalX = boardT.localScale.x * PLANE_MESH_SIZE;
        float physicalZ = boardT.localScale.z * PLANE_MESH_SIZE;

        boardWidthAlongRight    = Mathf.Abs(physicalX * Vector3.Dot(boardRight, textRight))
                                + Mathf.Abs(physicalZ * Vector3.Dot(boardFwd,   textRight));
        boardDepthAlongForward  = Mathf.Abs(physicalX * Vector3.Dot(boardRight, textForward))
                                + Mathf.Abs(physicalZ * Vector3.Dot(boardFwd,   textForward));

        lineHeight = fontSize * textScale * 1.8f;

        float safeOffset = Mathf.Max(textHeightOffset, 0.006f);
        Vector3 center = boardT.position + boardNormal * safeOffset;

        lineStartPosition = center
            - textRight   * (boardWidthAlongRight   * 0.5f - boardMargin)
            + textForward * (boardDepthAlongForward * 0.5f - boardMargin);

        cursorPosition    = lineStartPosition;
        cursorOffsetRight = 0f;
        currentLineIndex  = 0;

        Debug.Log($"{TAG} Layout initialized — " +
                  $"physicalX={physicalX:F3}m, physicalZ={physicalZ:F3}m, " +
                  $"boardWidth={boardWidthAlongRight:F3}m, boardDepth={boardDepthAlongForward:F3}m, " +
                  $"lineHeight={lineHeight:F4}m, startPos={lineStartPosition}.");
    }

    /// <summary>
    /// Creates (first call) or repositions the WorldSpace Canvas that hosts
    /// all scribble text.  The canvas lives in world space (not a child of
    /// the whiteboard) so it is immune to the whiteboard's non-uniform scale
    /// (0.09 × 0.01 × 0.10).  <see cref="Update"/> syncs its position every
    /// frame to handle spatial-anchor drift.
    /// </summary>
    private void CreateOrUpdateCanvas()
    {
        if (whiteboard == null) return;

        float safeOffset = Mathf.Max(textHeightOffset, 0.006f);
        Vector3 canvasWorldPos = whiteboard.transform.position + textSurfaceNormal * safeOffset;

        if (scribbleCanvas == null)
        {
            var canvasObj = new GameObject("ScribbleCanvas");
            scribbleCanvas = canvasObj.AddComponent<Canvas>();
            scribbleCanvas.renderMode = RenderMode.WorldSpace;

            // No GraphicRaycaster needed — canvas is display-only.
            // No EventSystem needed — no UI input on this canvas.
        }

        var rt = (RectTransform)scribbleCanvas.transform;
        rt.position   = canvasWorldPos;
        rt.rotation   = textBaseRotation;
        rt.localScale = Vector3.one * textScale;

        // Canvas size in canvas pixels (1 pixel = textScale metres).
        rt.sizeDelta = new Vector2(
            boardWidthAlongRight  / textScale,
            boardDepthAlongForward / textScale);
        rt.pivot = new Vector2(0.5f, 0.5f);

        Debug.Log($"{TAG} ScribbleCanvas created/updated at {canvasWorldPos}, " +
                  $"size=({rt.sizeDelta.x:F0}×{rt.sizeDelta.y:F0} px), scale={textScale}.");
    }

    private void TryFindPen()
    {
        foreach (var p in FindObjectsByType<WhiteboardPen>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (p.handedness == Handedness.Right) { pen = p; break; }
        }
    }

    private void SubscribeToPen()
    {
        if (pen == null) return;

        onStrokesFlushedDelegate = meta => pendingMeta.Enqueue(meta);
        pen.OnStrokesFlushed += onStrokesFlushedDelegate;

        onBoardClearedDelegate = ClearAll;
        pen.OnBoardCleared += onBoardClearedDelegate;

        Debug.Log($"{TAG} Late-bound to WhiteboardPen '{pen.gameObject.name}'.");
    }

    private void Update()
    {
        if (!initialized) return;

        // Deferred pen binding.
        if (pen == null)
        {
            TryFindPen();
            if (pen != null)
            {
                Debug.Log($"{TAG} WhiteboardPen (right hand) found on deferred search: '{pen.gameObject.name}'.");
                SubscribeToPen();
            }
        }

        // Sync canvas with whiteboard every frame (handles spatial-anchor drift).
        SyncCanvasWithWhiteboard();

        CheckScratchGesture();
    }

    /// <summary>Repositions the canvas to stay glued to the whiteboard surface.</summary>
    private void SyncCanvasWithWhiteboard()
    {
        if (scribbleCanvas == null || whiteboard == null) return;

        float safeOffset = Mathf.Max(textHeightOffset, 0.006f);
        var rt = (RectTransform)scribbleCanvas.transform;
        rt.position = whiteboard.transform.position + textSurfaceNormal * safeOffset;
        rt.rotation = textBaseRotation;
    }

    private void OnDestroy()
    {
        if (pipeline != null && onTextRecognizedDelegate != null)
            pipeline.OnFinalTextRecognized -= onTextRecognizedDelegate;
        else if (inkBridge != null && onTextRecognizedDelegate != null)
            inkBridge.OnTextRecognized -= onTextRecognizedDelegate;

        if (pen != null)
        {
            if (onStrokesFlushedDelegate != null)
                pen.OnStrokesFlushed -= onStrokesFlushedDelegate;
            if (onBoardClearedDelegate != null)
                pen.OnBoardCleared -= onBoardClearedDelegate;
        }

        if (undoButton != null)
            undoButton.onClick.RemoveListener(Undo);

        if (scribbleCanvas != null)
            Destroy(scribbleCanvas.gameObject);

        if (Instance == this) Instance = null;

        Debug.Log($"{TAG} Destroyed — unsubscribed all events.");
    }

    // ==================================================================
    // RECOGNITION HANDLER
    // ==================================================================

    private void OnTextRecognized(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            Debug.Log($"{TAG} OnTextRecognized: empty/whitespace — ignored.");
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

        Debug.Log($"{TAG} Total words: {words.Count}. Full text: \"{GetFullText()}\"");

        FireTextChanged();
    }

    // ==================================================================
    // WORD PLACEMENT & LAYOUT
    // ==================================================================

    private void PlaceWord(string word)
    {
        if (scribbleCanvas == null)
        {
            Debug.LogWarning($"{TAG} PlaceWord: canvas not ready — skipping '{word}'.");
            return;
        }

        // ── Create UGUI word element on the canvas ────────────────────
        // TextMeshProUGUI uses Unity's UI shader (always included in
        // Android/URP builds), unlike TextMeshPro 3D which uses the
        // Distance Field surface shader that can be stripped on Quest.
        var wordObj = new GameObject($"ScribbleWord_{words.Count}", typeof(RectTransform));
        wordObj.transform.SetParent(scribbleCanvas.transform, worldPositionStays: false);

        var wordRect = wordObj.GetComponent<RectTransform>();
        // Anchor at canvas centre; pivot at left-middle of word.
        wordRect.anchorMin = new Vector2(0.5f, 0.5f);
        wordRect.anchorMax = new Vector2(0.5f, 0.5f);
        wordRect.pivot     = new Vector2(0f, 0.5f);

        var tmp = wordObj.AddComponent<TextMeshProUGUI>();
        tmp.text              = word;
        tmp.fontSize          = fontSize;
        tmp.color             = new Color(textColor.r, textColor.g, textColor.b, 0f); // fade-in
        tmp.alignment         = TextAlignmentOptions.MidlineLeft;
        tmp.enableAutoSizing  = false;
        tmp.enableWordWrapping = false;
        tmp.overflowMode      = TextOverflowModes.Overflow;
        if (fontAsset != null) tmp.font = fontAsset;

        // ── Measure rendered width ────────────────────────────────────
        tmp.ForceMeshUpdate();
        float wordWidthPx    = tmp.preferredWidth;          // canvas pixels
        float wordWidthWorld = wordWidthPx * textScale;     // metres

        // ── Line-wrap if necessary ───────────────────────────────────
        float availableWorld = boardWidthAlongRight - 2f * boardMargin;
        if (cursorOffsetRight + wordWidthWorld > availableWorld && cursorOffsetRight > 0.001f)
        {
            currentLineIndex++;
            lineStartPosition -= textForward * lineHeight;
            cursorPosition     = lineStartPosition;
            cursorOffsetRight  = 0f;
            Debug.Log($"{TAG} Line wrap — now on line {currentLineIndex}.");
        }

        // ── Position on canvas (world → canvas pixels) ───────────────
        // InverseTransformPoint accounts for canvas position, rotation,
        // and scale, returning canvas-pixel coordinates.
        Vector3 canvasLocal = scribbleCanvas.transform.InverseTransformPoint(cursorPosition);
        wordRect.anchoredPosition = new Vector2(canvasLocal.x, canvasLocal.y);
        wordRect.sizeDelta        = new Vector2(wordWidthPx, fontSize * 2f);

        // ── World bounds (for scratch-to-delete detection) ───────────
        Vector3 wordWorldCenter = cursorPosition + textRight * (wordWidthWorld * 0.5f);
        Bounds wb = new Bounds(
            wordWorldCenter,
            new Vector3(wordWidthWorld + 0.01f, 0.04f, lineHeight + 0.01f));

        Debug.Log($"{TAG} Placed word \"{word}\" at cursor={cursorPosition} " +
                  $"anchoredPos=({canvasLocal.x:F1},{canvasLocal.y:F1}) px, " +
                  $"width={wordWidthWorld:F4}m, line={currentLineIndex}.");

        // ── Advance cursor ────────────────────────────────────────────
        cursorOffsetRight += wordWidthWorld + wordSpacing;
        cursorPosition     = lineStartPosition + textRight * cursorOffsetRight;

        var sw = new ScribbleWord
        {
            text        = word,
            worldCenter = wb.center,
            worldBounds = wb,
            gameObject  = wordObj
        };

        words.Add(sw);
        PushUndo(new ScribbleAction
        {
            type      = ActionType.Add,
            word      = sw,
            listIndex = words.Count - 1
        });

        StartCoroutine(AnimateWordAppear(tmp));
    }

    // ==================================================================
    // UNDO SYSTEM
    // ==================================================================

    private void PushUndo(ScribbleAction action)
    {
        undoStack.Push(action);
        if (undoStack.Count > maxUndoSteps)
        {
            var arr = undoStack.ToArray();
            undoStack.Clear();
            for (int i = Mathf.Min(maxUndoSteps - 1, arr.Length - 1); i >= 0; i--)
                undoStack.Push(arr[i]);
            Debug.Log($"{TAG} Undo stack trimmed to {maxUndoSteps} entries.");
        }
    }

    public void Undo()
    {
        if (undoStack.Count == 0)
        {
            Debug.Log($"{TAG} Undo requested but stack is empty.");
            return;
        }

        var action = undoStack.Pop();
        switch (action.type)
        {
            case ActionType.Add:
                if (action.word.gameObject != null)
                    action.word.gameObject.SetActive(false);
                words.Remove(action.word);
                RecomputeCursor();
                Debug.Log($"{TAG} Undo ADD — removed \"{action.word.text}\". " +
                          $"Words remaining: {words.Count}, undo stack: {undoStack.Count}.");
                break;

            case ActionType.Delete:
                if (action.word.gameObject != null)
                    action.word.gameObject.SetActive(true);
                int idx = Mathf.Clamp(action.listIndex, 0, words.Count);
                words.Insert(idx, action.word);
                Debug.Log($"{TAG} Undo DELETE — restored \"{action.word.text}\" at index {idx}. " +
                          $"Words: {words.Count}, undo stack: {undoStack.Count}.");
                break;
        }

        FireTextChanged();
    }

    private void RecomputeCursor()
    {
        InitializeLayout();
        foreach (var w in words)
        {
            if (w.gameObject == null || !w.gameObject.activeSelf) continue;
            var tmp = w.gameObject.GetComponent<TextMeshProUGUI>();
            if (tmp == null) continue;

            float ww = tmp.preferredWidth * textScale;
            float available = boardWidthAlongRight - 2f * boardMargin;
            if (cursorOffsetRight + ww > available && cursorOffsetRight > 0.001f)
            {
                currentLineIndex++;
                lineStartPosition -= textForward * lineHeight;
                cursorPosition     = lineStartPosition;
                cursorOffsetRight  = 0f;
            }
            cursorOffsetRight += ww + wordSpacing;
            cursorPosition     = lineStartPosition + textRight * cursorOffsetRight;
        }
        Debug.Log($"{TAG} Cursor recomputed — line={currentLineIndex}, offsetRight={cursorOffsetRight:F4}m.");
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
                    Debug.Log($"{TAG} Scratch mid-stroke — pts={scratchPoints.Count}, reversals={reversals}/{minScratchReversals}.");

                if (reversals >= minScratchReversals && TryHandleScratch(reversals))
                    return;
            }
        }
        else if (wasPenDrawing)
        {
            if (scratchPoints.Count >= 10)
            {
                int reversals = CountReversals(scratchPoints);
                if (verboseScratchLog || reversals >= minScratchReversals)
                    Debug.Log($"{TAG} Scratch end-of-stroke — pts={scratchPoints.Count}, reversals={reversals}/{minScratchReversals}.");

                if (reversals >= minScratchReversals)
                    TryHandleScratch(reversals);
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
            if (p.x < minX) minX = p.x;
            if (p.x > maxX) maxX = p.x;
            if (p.z < minZ) minZ = p.z;
            if (p.z > maxZ) maxZ = p.z;
        }

        float extX = maxX - minX;
        float extZ = maxZ - minZ;
        float diag = Mathf.Sqrt(extX * extX + extZ * extZ);

        if (diag > maxScratchExtent || diag < 0.005f) return 0;

        bool useX = extX >= extZ;
        int reversals = 0;
        float lastDir = 0f;
        float accum = 0f;

        for (int i = 1; i < pts.Count; i++)
        {
            float d = useX
                ? (pts[i].x - pts[i - 1].x)
                : (pts[i].z - pts[i - 1].z);
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
        var deletedWords = new List<string>();

        for (int i = words.Count - 1; i >= 0; i--)
        {
            var w = words[i];
            if (w.gameObject != null && w.gameObject.activeSelf
                && OverlapsXZ(scratch, w.worldBounds))
            {
                deletedWords.Add(w.text);
                DeleteWord(w);
                deleted = true;
            }
        }

        if (deleted)
        {
            if (pen != null) pen.ClearStrokeBuffer();
            if (whiteboard != null) whiteboard.ClearToBackground();
            scratchPoints.Clear();

            string revStr = reversals >= 0 ? $"reversals={reversals}, " : "";
            Debug.Log($"{TAG} Scratch-to-delete — {revStr}deleted [{string.Join(", ", deletedWords)}]. " +
                      $"Words remaining: {words.Count}.");
        }
        else if (verboseScratchLog)
        {
            Debug.Log($"{TAG} Scratch detected (reversals={reversals}) but no words overlapped.");
        }

        return deleted;
    }

    private void DeleteWord(ScribbleWord word)
    {
        int index = words.IndexOf(word);
        if (index < 0)
        {
            Debug.LogWarning($"{TAG} DeleteWord: '{word.text}' not found in list.");
            return;
        }

        Debug.Log($"{TAG} Deleting word \"{word.text}\" at index {index}.");

        var tmp = word.gameObject.GetComponent<TextMeshProUGUI>();
        if (tmp != null)
            StartCoroutine(AnimateWordDelete(word, tmp));
        else
            word.gameObject.SetActive(false);

        words.RemoveAt(index);

        PushUndo(new ScribbleAction
        {
            type      = ActionType.Delete,
            word      = word,
            listIndex = index
        });

        FireTextChanged();
    }

    private static bool OverlapsXZ(Bounds a, Bounds b)
    {
        return Mathf.Abs(a.center.x - b.center.x) < (a.extents.x + b.extents.x)
            && Mathf.Abs(a.center.z - b.center.z) < (a.extents.z + b.extents.z);
    }

    // ==================================================================
    // UNDO — HAND MENU BUTTON
    // ==================================================================

    /// <summary>
    /// Finds the hand / wrist menu at runtime and repurposes its first
    /// button as an "Undo" action.  Uses <see cref="TMP_Text"/> (the base
    /// class for both TextMeshPro and TextMeshProUGUI) so the label is
    /// updated regardless of which concrete TMP component the menu uses.
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
            Debug.LogWarning($"{TAG} No Button found in hand menu — Undo button not configured.");
            return;
        }

        undoButton = buttons[0];

        // TMP_Text is the base class for both TextMeshPro (3D) and
        // TextMeshProUGUI (Canvas), so this works regardless of the menu's
        // concrete TMP component type.
        var label = undoButton.GetComponentInChildren<TMP_Text>(true);
        if (label != null)
            label.text = "Undo";
        else
            Debug.LogWarning($"{TAG} No TMP_Text found on button '{undoButton.gameObject.name}' — label not changed.");

        undoButton.onClick.RemoveAllListeners();
        undoButton.onClick.AddListener(Undo);

        Debug.Log($"{TAG} Undo button configured on hand menu ('{undoButton.gameObject.name}', " +
                  $"label={label?.text ?? "N/A"}).");
    }

    // ==================================================================
    // ANIMATIONS
    // ==================================================================

    private IEnumerator AnimateWordAppear(TMP_Text tmp)
    {
        // tmp.color was already set to alpha=0 in PlaceWord.
        float t = 0f;
        while (t < 0.25f && tmp != null)
        {
            t += Time.deltaTime;
            Color c = textColor;
            c.a = Mathf.Lerp(0f, textColor.a, t / 0.25f);
            tmp.color = c;
            yield return null;
        }
        if (tmp != null) tmp.color = textColor;
    }

    private IEnumerator AnimateWordDelete(ScribbleWord word, TMP_Text tmp)
    {
        if (tmp != null) tmp.color = new Color(0.8f, 0.2f, 0.2f, 1f); // flash red
        yield return new WaitForSeconds(0.12f);

        float t = 0f;
        while (t < 0.12f && tmp != null)
        {
            t += Time.deltaTime;
            Color c = tmp.color;
            c.a = Mathf.Lerp(1f, 0f, t / 0.12f);
            tmp.color = c;
            yield return null;
        }
        if (word.gameObject != null) word.gameObject.SetActive(false);
    }

    // ==================================================================
    // PUBLIC HELPERS
    // ==================================================================

    /// <summary>Returns the full accumulated journal text.</summary>
    public string GetFullText()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var w in words)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(w.text);
        }
        return sb.ToString();
    }

    /// <summary>Clear all words, undo history, and the whiteboard.</summary>
    public void ClearAll()
    {
        int wordCount = words.Count;

        foreach (var w in words)
            if (w.gameObject != null) Destroy(w.gameObject);

        words.Clear();
        undoStack.Clear();
        pendingMeta.Clear();
        scratchPoints.Clear();

        if (whiteboard != null) whiteboard.ClearToBackground();

        // Reset layout (which also resets canvas size in case board moved)
        InitializeLayout();
        CreateOrUpdateCanvas();

        FireTextChanged();
        Debug.Log($"{TAG} ClearAll — destroyed {wordCount} word(s), undo history wiped.");
    }

    private void FireTextChanged()
    {
        OnTextChanged?.Invoke(GetFullText());
    }
}
