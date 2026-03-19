using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
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
///   <item>Undo (left hand thumb + index finger pinch)</item>
///   <item>Auto-clear (handwriting ink clears after recognition)</item>
/// </list>
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

    [Tooltip("Scale of the 3D text objects (1 = 1 m per text unit).")]
    public float textScale = 0.004f;

    [Tooltip("Font size for TextMeshPro 3D text.")]
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
    private XRHandSubsystem handSubsystem;

    // ── Text orientation (locked at init) ────────────────────────────
    private Vector3 textRight;
    private Vector3 textForward;
    private Quaternion textBaseRotation;

    // ── Flowing layout state ─────────────────────────────────────────
    private Vector3 cursorPosition;
    private Vector3 lineStartPosition;
    private float lineHeight;
    private float boardWidthAlongRight;
    private float cursorOffsetRight;
    private int currentLineIndex;

    // ── Scratch detection ────────────────────────────────────────────
    private readonly List<Vector3> scratchPoints = new List<Vector3>();
    private bool wasPenDrawing;

    // ── Undo gesture ─────────────────────────────────────────────────
    private bool undoPinchActive;
    private float undoPinchCandidateStart = -1f;
    private float lastUndoTime;
    private const float UNDO_COOLDOWN = 0.5f;
    private const float PINCH_TRIGGER_DISTANCE = 0.014f;
    private const float PINCH_RELEASE_DISTANCE = 0.022f;
    private const float PINCH_HOLD_TIME = 0.10f;
    private const float NON_INDEX_SEPARATION_BIAS = 0.008f;

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
            Debug.LogWarning($"{TAG} OnWhiteboardSpawned: GameObject '{wb.name}' has no Whiteboard component.");
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
            Debug.LogWarning($"{TAG} No right-hand WhiteboardPen found at init — will retry each frame until found.");

        pipeline = RecognitionPipeline.Instance;
        inkBridge = DigitalInkBridge.Instance;

        // Subscribe to recognition results
        onTextRecognizedDelegate = OnTextRecognized;
        if (pipeline != null)
        {
            pipeline.OnFinalTextRecognized += onTextRecognizedDelegate;
            Debug.Log($"{TAG} Subscribed to RecognitionPipeline.OnFinalTextRecognized.");
        }
        else if (inkBridge != null)
        {
            inkBridge.OnTextRecognized += onTextRecognizedDelegate;
            Debug.Log($"{TAG} RecognitionPipeline not found — subscribed to DigitalInkBridge.OnTextRecognized instead.");
        }
        else
        {
            Debug.LogError($"{TAG} Neither RecognitionPipeline nor DigitalInkBridge found — text recognition will not work.");
        }

        // Subscribe to pen events (if pen was found at init time)
        if (pen != null)
            SubscribeToPen();

        // Destroy RecognizedTextDisplay — ScribbleManager replaces it.
        // We must Destroy (not just disable) because disabling a MonoBehaviour
        // does NOT stop running coroutines or event subscriptions. Its Subscribe()
        // coroutine would still complete and hook into the pipeline.
        var rtd = FindAnyObjectByType<RecognizedTextDisplay>();
        if (rtd != null)
        {
            string rtdObjName = rtd.gameObject.name;
            // Hide text immediately (Destroy is deferred to end of frame)
            if (rtd.displayText != null) rtd.displayText.text = "";
            rtd.StopAllCoroutines();
            Destroy(rtd);
            Debug.Log($"{TAG} Destroyed RecognizedTextDisplay on '{rtdObjName}'.");
        }

        LockTextOrientation();
        InitializeLayout();

        initialized = true;
        Debug.Log($"{TAG} Initialized. Board width={boardWidthAlongRight:F3}m, lineHeight={lineHeight:F4}m.");
    }

    private void LockTextOrientation()
    {
        if (whiteboard == null) return;

        Transform boardT = whiteboard.transform;
        Camera cam = Camera.main;
        Vector3 boardNormal = boardT.up.normalized;

        // Use board-plane axes directly so text remains visible/aligned even
        // if the board is not perfectly horizontal.
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

        // Ensure forward points away from user (camera forward direction)
        if (cam != null)
        {
            Vector3 camFwd = cam.transform.forward;
            camFwd = Vector3.ProjectOnPlane(camFwd, boardNormal);
            if (camFwd.sqrMagnitude > 0.001f && Vector3.Dot(textForward, camFwd) < 0f)
                textForward = -textForward;
        }

        Debug.Log($"{TAG} Text orientation locked — right={textRight}, forward={textForward}, " +
                  $"boardRight={boardRight}, boardFwd={boardFwd}, boardYRot={boardT.eulerAngles.y:F1}°.");

        // TextMeshPro 3D uses local XY for glyph plane and +Z as face normal.
        // Build rotation so text lies on board plane (+Z aligned to board normal).
        textBaseRotation = Quaternion.LookRotation(boardNormal, textForward);
    }

    /// <summary>The whiteboard mesh is a Unity Plane (10×10 units).</summary>
    private const float PLANE_MESH_SIZE = 10f;

    private void InitializeLayout()
    {
        if (whiteboard == null) return;

        Transform boardT = whiteboard.transform;
        Vector3 boardNormal = boardT.up.normalized;

        // The whiteboard is a Unity Plane mesh (10×10 units).  Physical
        // world-space dimensions are simply localScale × 10.  This avoids
        // relying on Collider.bounds (AABB) which inflates when rotated,
        // and avoids the BoxCollider vs MeshCollider distinction entirely.
        Vector3 boardRight = boardT.right;
        Vector3 boardFwd   = boardT.forward;
        boardRight.y = 0f; boardRight.Normalize();
        boardFwd.y   = 0f; boardFwd.Normalize();

        float physicalX = boardT.localScale.x * PLANE_MESH_SIZE;
        float physicalZ = boardT.localScale.z * PLANE_MESH_SIZE;

        boardWidthAlongRight    = Mathf.Abs(physicalX * Vector3.Dot(boardRight, textRight))
                                + Mathf.Abs(physicalZ * Vector3.Dot(boardFwd,   textRight));
        float boardDepthAlongForward = Mathf.Abs(physicalX * Vector3.Dot(boardRight, textForward))
                                     + Mathf.Abs(physicalZ * Vector3.Dot(boardFwd,   textForward));

        lineHeight = fontSize * textScale * 1.8f;

        // Start at top-left of the board (far from user, left side).
        Vector3 center = boardT.position + boardNormal * textHeightOffset;

        lineStartPosition = center
            - textRight   * (boardWidthAlongRight    * 0.5f - boardMargin)
            + textForward * (boardDepthAlongForward  * 0.5f - boardMargin);

        cursorPosition    = lineStartPosition;
        cursorOffsetRight = 0f;
        currentLineIndex  = 0;

        Debug.Log($"{TAG} Layout initialized — " +
                  $"physicalX={physicalX:F3}m, physicalZ={physicalZ:F3}m, " +
                  $"boardWidth={boardWidthAlongRight:F3}m, boardDepth={boardDepthAlongForward:F3}m, " +
                  $"lineHeight={lineHeight:F4}m, startPos={lineStartPosition}.");
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

        Debug.Log($"{TAG} Late-bound to WhiteboardPen '{pen.gameObject.name}' (OnStrokesFlushed, OnBoardCleared).");
    }

    private void Update()
    {
        if (!initialized) return;

        // Deferred pen binding — the pen's GameObject may become active
        // after ScribbleManager initializes (XR Origin children).
        if (pen == null)
        {
            TryFindPen();
            if (pen != null)
            {
                Debug.Log($"{TAG} WhiteboardPen (right hand) found on deferred search: '{pen.gameObject.name}'.");
                SubscribeToPen();
            }
        }

        CheckScratchGesture();
        CheckUndoGesture();
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
            Debug.Log($"{TAG} OnTextRecognized: received empty/whitespace — ignored.");
            return;
        }

        Debug.Log($"{TAG} OnTextRecognized: \"{text}\"");

        // Clear handwriting ink from the board
        if (whiteboard != null) whiteboard.ClearToBackground();

        // Consume stroke metadata (keeps queue in sync)
        if (pendingMeta.Count > 0) pendingMeta.Dequeue();

        // Split into words and place in flowing layout
        string[] parts = text.Trim().Split(
            new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

        Debug.Log($"{TAG} Placing {parts.Length} word(s): [{string.Join(", ", parts)}]");

        foreach (string word in parts)
            PlaceWord(word);

        Debug.Log($"{TAG} Total words on board: {words.Count}. Full text: \"{GetFullText()}\"");

        FireTextChanged();
    }

    // ==================================================================
    // WORD PLACEMENT & LAYOUT
    // ==================================================================

    private const int WHITEBOARD_LAYER = 10;

    private void PlaceWord(string word)
    {
        // Create 3D TextMeshPro object on the same layer as the whiteboard
        // so it renders wherever the board is visible.
        GameObject wordObj = new GameObject($"ScribbleWord_{words.Count}");
        wordObj.layer = (whiteboard != null) ? whiteboard.gameObject.layer : WHITEBOARD_LAYER;
        if (whiteboard != null)
            wordObj.transform.SetParent(whiteboard.transform, true);

        TextMeshPro tmp = wordObj.AddComponent<TextMeshPro>();
        tmp.text = word;
        tmp.fontSize = fontSize;
        tmp.color = textColor;
        tmp.alignment = TextAlignmentOptions.MidlineLeft;
        tmp.enableAutoSizing = false;
        tmp.overflowMode = TextOverflowModes.Overflow;
        if (fontAsset != null) tmp.font = fontAsset;

        wordObj.transform.rotation = textBaseRotation;
        wordObj.transform.localScale = Vector3.one * textScale;

        if (tmp.renderer is MeshRenderer mr)
        {
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
        }

        // Measure rendered width
        tmp.ForceMeshUpdate();
        float wordWidth = tmp.preferredWidth * textScale;

        // Line-wrap if necessary
        float availableWidth = boardWidthAlongRight - 2f * boardMargin;
        if (cursorOffsetRight + wordWidth > availableWidth && cursorOffsetRight > 0.001f)
        {
            currentLineIndex++;
            lineStartPosition -= textForward * lineHeight;
            cursorPosition = lineStartPosition;
            cursorOffsetRight = 0f;
            Debug.Log($"{TAG} Line wrap — now on line {currentLineIndex}.");
        }

        // Position the word
        wordObj.transform.position = cursorPosition;

        // World bounds for scratch overlap detection (generous Y extent)
        Bounds wb = new Bounds(
            cursorPosition + textRight * (wordWidth * 0.5f),
            new Vector3(wordWidth + 0.01f, 0.04f, lineHeight + 0.01f));

        Debug.Log($"{TAG} Placed word \"{word}\" at {cursorPosition} " +
                  $"(width={wordWidth:F4}m, line={currentLineIndex}).");

        // Advance cursor
        cursorOffsetRight += wordWidth + wordSpacing;
        cursorPosition = lineStartPosition + textRight * cursorOffsetRight;

        var sw = new ScribbleWord
        {
            text = word,
            worldCenter = wb.center,
            worldBounds = wb,
            gameObject = wordObj
        };

        words.Add(sw);
        PushUndo(new ScribbleAction
        {
            type = ActionType.Add,
            word = sw,
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
                Debug.Log($"{TAG} Undo ADD — removed word \"{action.word.text}\". " +
                          $"Words remaining: {words.Count}, undo stack: {undoStack.Count}.");
                break;

            case ActionType.Delete:
                if (action.word.gameObject != null)
                    action.word.gameObject.SetActive(true);
                int idx = Mathf.Clamp(action.listIndex, 0, words.Count);
                words.Insert(idx, action.word);
                Debug.Log($"{TAG} Undo DELETE — restored word \"{action.word.text}\" at index {idx}. " +
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
            var tmp = w.gameObject.GetComponent<TextMeshPro>();
            if (tmp == null) continue;
            float ww = tmp.preferredWidth * textScale;

            float available = boardWidthAlongRight - 2f * boardMargin;
            if (cursorOffsetRight + ww > available && cursorOffsetRight > 0.001f)
            {
                currentLineIndex++;
                lineStartPosition -= textForward * lineHeight;
                cursorPosition = lineStartPosition;
                cursorOffsetRight = 0f;
            }
            cursorOffsetRight += ww + wordSpacing;
            cursorPosition = lineStartPosition + textRight * cursorOffsetRight;
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

            // Periodic check during the stroke
            if (scratchPoints.Count >= 15 && scratchPoints.Count % 5 == 0)
            {
                int reversals = CountReversals(scratchPoints);
                if (verboseScratchLog)
                    Debug.Log($"{TAG} Scratch mid-stroke check — pts={scratchPoints.Count}, reversals={reversals}/{minScratchReversals}.");

                if (reversals >= minScratchReversals && TryHandleScratch(reversals))
                    return;
            }
        }
        else if (wasPenDrawing)
        {
            // Final check when stroke ends
            if (scratchPoints.Count >= 10)
            {
                int reversals = CountReversals(scratchPoints);
                if (verboseScratchLog || reversals >= minScratchReversals)
                    Debug.Log($"{TAG} Scratch end-of-stroke check — pts={scratchPoints.Count}, reversals={reversals}/{minScratchReversals}.");

                if (reversals >= minScratchReversals)
                    TryHandleScratch(reversals);
            }

            scratchPoints.Clear();
        }

        wasPenDrawing = drawing;
    }

    /// <summary>Returns the reversal count for a point list (used for logging and gesture check).</summary>
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

    private bool IsScratchGesture(List<Vector3> pts)
    {
        return CountReversals(pts) >= minScratchReversals;
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
            Debug.Log($"{TAG} Scratch gesture detected (reversals={reversals}) but no words overlapped.");
        }

        return deleted;
    }

    private void DeleteWord(ScribbleWord word)
    {
        int index = words.IndexOf(word);
        if (index < 0)
        {
            Debug.LogWarning($"{TAG} DeleteWord: word \"{word.text}\" not found in list.");
            return;
        }

        Debug.Log($"{TAG} Deleting word \"{word.text}\" at index {index}.");

        var tmp = word.gameObject.GetComponent<TextMeshPro>();
        if (tmp != null)
            StartCoroutine(AnimateWordDelete(word, tmp));
        else
            word.gameObject.SetActive(false);

        words.RemoveAt(index);

        PushUndo(new ScribbleAction
        {
            type = ActionType.Delete,
            word = word,
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
    // UNDO GESTURE  (left hand thumb + index pinch)
    // ==================================================================

    private void CheckUndoGesture()
    {
        if (Time.time - lastUndoTime < UNDO_COOLDOWN) return;

        if (handSubsystem == null || !handSubsystem.running)
        {
            handSubsystem = WhiteboardPen.GetHandSubsystem();
            if (handSubsystem == null) return;
        }

        XRHand left = handSubsystem.leftHand;
        if (!left.isTracked)
        {
            undoPinchActive = false;
            undoPinchCandidateStart = -1f;
            return;
        }

        if (!left.GetJoint(XRHandJointID.ThumbTip).TryGetPose(out Pose thumbPose)) return;
        if (!left.GetJoint(XRHandJointID.IndexTip).TryGetPose(out Pose indexPose)) return;
        if (!left.GetJoint(XRHandJointID.MiddleTip).TryGetPose(out Pose middlePose)) return;
        if (!left.GetJoint(XRHandJointID.RingTip).TryGetPose(out Pose ringPose)) return;

        float thumbIndex = Vector3.Distance(thumbPose.position, indexPose.position);
        float thumbMiddle = Vector3.Distance(thumbPose.position, middlePose.position);
        float thumbRing = Vector3.Distance(thumbPose.position, ringPose.position);

        bool indexPinch = thumbIndex <= PINCH_TRIGGER_DISTANCE;
        bool otherFingersSeparated =
            thumbMiddle > thumbIndex + NON_INDEX_SEPARATION_BIAS &&
            thumbRing > thumbIndex + NON_INDEX_SEPARATION_BIAS;

        if (!undoPinchActive && indexPinch && otherFingersSeparated)
        {
            if (undoPinchCandidateStart < 0f)
                undoPinchCandidateStart = Time.time;

            if (Time.time - undoPinchCandidateStart >= PINCH_HOLD_TIME)
            {
                undoPinchActive = true;
                undoPinchCandidateStart = -1f;
                Debug.Log($"{TAG} Undo pinch detected (left thumb+index). Triggering Undo.");
                Undo();
                lastUndoTime = Time.time;
            }
        }
        else if (undoPinchActive && thumbIndex >= PINCH_RELEASE_DISTANCE)
        {
            undoPinchActive = false;
            undoPinchCandidateStart = -1f;
        }
        else if (!indexPinch || !otherFingersSeparated)
        {
            undoPinchCandidateStart = -1f;
        }
    }

    // ==================================================================
    // ANIMATIONS
    // ==================================================================

    private IEnumerator AnimateWordAppear(TextMeshPro tmp)
    {
        Color c = textColor;
        c.a = 0f;
        tmp.color = c;

        float t = 0f;
        while (t < 0.25f)
        {
            t += Time.deltaTime;
            c.a = Mathf.Lerp(0f, textColor.a, t / 0.25f);
            tmp.color = c;
            yield return null;
        }
        tmp.color = textColor;
    }

    private IEnumerator AnimateWordDelete(ScribbleWord word, TextMeshPro tmp)
    {
        tmp.color = new Color(0.8f, 0.2f, 0.2f, 1f);   // flash red
        yield return new WaitForSeconds(0.12f);

        float t = 0f;
        Color c = tmp.color;
        while (t < 0.12f)
        {
            t += Time.deltaTime;
            c.a = Mathf.Lerp(1f, 0f, t / 0.12f);
            tmp.color = c;
            yield return null;
        }
        word.gameObject.SetActive(false);
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
        InitializeLayout();
        FireTextChanged();

        Debug.Log($"{TAG} ClearAll — destroyed {wordCount} word(s), undo history wiped.");
    }

    private void FireTextChanged()
    {
        OnTextChanged?.Invoke(GetFullText());
    }
}
