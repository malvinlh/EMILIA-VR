using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Hands;
using TMPro;

/// <summary>
/// Inline text cursor and word-selection for the journal ResultText.
///
/// CURSOR PLACEMENT
///   Right-hand index + thumb pinch (tap).  A ray is cast from the index
///   fingertip along the finger direction.  When the ray hits ResultText the
///   nearest word-gap is selected and a blinking caret appears.  New words
///   from handwriting / voice are inserted at that position; Backspace
///   removes the word immediately before the caret.
///
/// WORD SELECTION
///   Pinch + drag over ResultText.  The anchor is fixed at the pinch-start
///   word gap; the active end follows the ray while pinching.  On release,
///   all words between the two gaps are highlighted.  The Backspace button
///   (via WhiteboardPageManager) then deletes the selected words.
///
/// RAY VISUAL (LineRenderer, auto-created at runtime)
///   Green  — ray hits ResultText      (cursor / selection zone)
///   Blue   — ray hits HandwritingArea (draw-hint; WhiteboardPen handles drawing)
///   Grey   — ray hits neither zone
///
/// Inspector setup:
///   resultText      → Body/ResultText (TextMeshProUGUI)
///   handwritingArea → Body/HandwritingArea (RectTransform)
///   cursorBar       → a thin Image child of the canvas  (3 px wide × ~30 px tall,
///                     anchor centre, pivot centre)
///   canvasRoot      → WhiteboardUI RectTransform (the Canvas)
///   rayLine         → leave empty — auto-created at runtime
/// </summary>
[DefaultExecutionOrder(210)]
public class JournalInlineCursor : MonoBehaviour
{
    // ── Singleton ─────────────────────────────────────────────────────────
    public static JournalInlineCursor Instance { get; private set; }

    // ── Inspector ─────────────────────────────────────────────────────────
    [Header("References")]
    [SerializeField] private TextMeshProUGUI resultText;
    [SerializeField] private RectTransform   handwritingArea;
    [SerializeField] private RectTransform   cursorBar;    // thin Image — blinking caret
    [SerializeField] private RectTransform   canvasRoot;   // WhiteboardUI canvas RT

    [Header("Ray")]
    [SerializeField] private LineRenderer rayLine;         // leave null → auto-created
    [SerializeField] private float        rayMaxLength  = 2f;
    [SerializeField] private float        rayWidth      = 0.003f;
    [SerializeField] private Color        rayCursorColor  = new Color(0.20f, 0.90f, 0.20f, 0.85f);
    [SerializeField] private Color        rayDrawColor    = new Color(0.20f, 0.60f, 0.95f, 0.85f);
    [SerializeField] private Color        rayDefaultColor = new Color(0.55f, 0.55f, 0.55f, 0.40f);

    [Header("Selection highlight")]
    [SerializeField] private Color selectionColor = new Color(0.20f, 0.50f, 1.00f, 0.35f);

    [Header("Pinch")]
    [SerializeField] [Range(0.005f, 0.05f)] private float pinchThreshold   = 0.020f;
    [SerializeField] [Range(0.005f, 0.05f)] private float releaseThreshold = 0.030f;

    [Header("Button poke")]
    [SerializeField] [Range(0f, 1f)] private float pokeClickCooldownSec = 0.25f;

    [Header("Cursor blink")]
    [SerializeField] [Range(0.1f, 2f)] private float blinkOnTime  = 0.5f;
    [SerializeField] [Range(0.1f, 2f)] private float blinkOffTime = 0.3f;

    // ── State machine ─────────────────────────────────────────────────────
    private enum CursorState { Idle, Dragging, CursorActive, SelectionActive }
    private CursorState _state     = CursorState.Idle;
    private int         _anchorGap;  // word-gap index where drag started
    private int         _activeGap;  // word-gap index at current ray hit

    // ── Runtime ───────────────────────────────────────────────────────────
    private XRHandSubsystem          _handSubsystem;
    private bool                     _wasPinching;
    private Image                    _cursorImage;
    private Coroutine                _blinkCo;
    private readonly List<RectTransform> _highlights = new List<RectTransform>();
    private int                      _lastKnownPage = -1;

    // Cached Camera Floor Offset Object — required to convert session-space
    // joint positions to world space (same pattern as WhiteboardPen).
    private Transform _cameraOffsetTransform;

    // ── Button poke state ─────────────────────────────────────────────────
    // Buttons are pressed by physically bringing the index finger tip close
    // to the button surface — no XRI Interactor required.
    private const float POKE_HOVER_DIST = 0.04f;  // 4 cm — outer zone
    private const float POKE_FIRE_DIST  = 0.012f; // 12 mm — trigger press
    private bool[] _btnInZone;
    private bool[] _btnWasClose;
    private float[] _btnLastClickTime;
    private int[] _btnLastClickFrame;

    // ── Public API ────────────────────────────────────────────────────────
    /// <summary>True while a word-range selection (not just a cursor) is active.</summary>
    public bool HasActiveSelection => _state == CursorState.SelectionActive;

    /// <summary>Current world-space ray from the index finger tip, updated every tracked frame.</summary>
    public Ray     CurrentRay       { get; private set; }
    /// <summary>World-space position of the right-hand index finger tip.</summary>
    public Vector3 TipWorldPosition { get; private set; }
    /// <summary>True when the right hand is tracked and joint data is valid this frame.</summary>
    public bool    IsHandTracked    { get; private set; }

    private const string TAG = "[JournalInlineCursor]";

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
        _handSubsystem = WhiteboardPen.GetHandSubsystem();
        if (_handSubsystem == null)
            Debug.LogWarning($"{TAG} XRHandSubsystem not found — will retry each frame.");

        ResolveCameraOffsetTransform();

        if (cursorBar != null)
        {
            _cursorImage = cursorBar.GetComponent<Image>();
            cursorBar.gameObject.SetActive(false);
        }

        EnsureLineRenderer();
    }

    private void ResolveCameraOffsetTransform()
    {
        var xrOrigin = FindAnyObjectByType<Unity.XR.CoreUtils.XROrigin>();
        if (xrOrigin != null && xrOrigin.CameraFloorOffsetObject != null)
        {
            _cameraOffsetTransform = xrOrigin.CameraFloorOffsetObject.transform;
            return;
        }
        Camera cam = Camera.main;
        if (cam != null && cam.transform.parent != null)
            _cameraOffsetTransform = cam.transform.parent;
    }

    private Vector3 JointToWorld(Vector3 sessionPos)
    {
        if (_cameraOffsetTransform != null)
            return _cameraOffsetTransform.TransformPoint(sessionPos);
        return sessionPos;
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
        IsHandTracked = false;
        if (_blinkCo != null) StopCoroutine(_blinkCo);
        ScribbleManager.Instance?.ClearInsertCursor();
        foreach (var h in _highlights)
            if (h != null) Destroy(h.gameObject);
    }

    // ==================================================================
    // UPDATE
    // ==================================================================

    private void Update()
    {
        // Only active during a journaling session — no interaction on the
        // whiteboard until the user has started the journal session.
        if (JournalSessionManager.Instance == null ||
            JournalSessionManager.Instance.CurrentState != JournalSessionManager.SessionState.Journaling)
        {
            SetRayVisible(false);
            IsHandTracked = false;
            return;
        }

        // ── Lazy subsystem init ───────────────────────────────────────
        if (_handSubsystem == null)
        {
            _handSubsystem = WhiteboardPen.GetHandSubsystem();
            if (_handSubsystem == null) { IsHandTracked = false; return; }
        }

        var rightHand = _handSubsystem.rightHand;
        if (!rightHand.isTracked) { SetRayVisible(false); IsHandTracked = false; return; }

        // ── Page-change guard ─────────────────────────────────────────
        // Dismiss cursor / selection if the user navigated to a different page.
        int currentPage = ScribbleManager.Instance?.CurrentPageIndex ?? -1;
        if (currentPage != _lastKnownPage && _lastKnownPage >= 0)
            ResetToIdle();
        _lastKnownPage = currentPage;

        // ── Joint poses (session → world) ──────────────────────────────
        bool gotProximal = rightHand.GetJoint(XRHandJointID.IndexProximal)
                                    .TryGetPose(out Pose proximalPose);
        bool gotTip      = rightHand.GetJoint(XRHandJointID.IndexTip)
                                    .TryGetPose(out Pose tipPose);
        bool gotThumb    = rightHand.GetJoint(XRHandJointID.ThumbTip)
                                    .TryGetPose(out Pose thumbPose);

        if (!gotTip || !gotThumb) { SetRayVisible(false); IsHandTracked = false; return; }

        // Convert from session/tracking space to world space
        Vector3 tipWorld      = JointToWorld(tipPose.position);
        Vector3 thumbWorld    = JointToWorld(thumbPose.position);
        Vector3 proximalWorld = gotProximal ? JointToWorld(proximalPose.position) : Vector3.zero;

        // Expose tip position for external components (e.g. JournalDoneButton)
        TipWorldPosition = tipWorld;
        IsHandTracked    = true;

        // ── Button poke detection (independent of pinch / cursor logic) ──
        CheckButtonPoke(tipWorld);

        // ── Pinch detection (hysteresis) ──────────────────────────────
        float dist       = Vector3.Distance(tipWorld, thumbWorld);
        bool  isPinching = _wasPinching ? dist < releaseThreshold : dist < pinchThreshold;

        // ── Finger ray: origin = index tip, direction = proximal→tip ─
        Vector3 rayOrigin = tipWorld;
        Vector3 rayDir    = gotProximal
            ? (tipWorld - proximalWorld).normalized
            : tipPose.forward;
        if (rayDir.sqrMagnitude < 0.001f) rayDir = Vector3.forward;
        var ray = new Ray(rayOrigin, rayDir);
        CurrentRay = ray;

        // ── Hit tests ─────────────────────────────────────────────────
        Vector3 rtHit = Vector3.zero;
        Vector3 haHit = Vector3.zero;
        bool hitRT = resultText      != null
                  && RaycastRect(ray, (RectTransform)resultText.transform, out rtHit);
        bool hitHA = handwritingArea != null
                  && RaycastRect(ray, handwritingArea, out haHit);

        // ── Ray visual (only while pinching) ──────────────────────────
        if (isPinching)
        {
            Vector3 endpoint;
            Color   col;
            if      (hitRT) { endpoint = rtHit;                              col = rayCursorColor;  }
            else if (hitHA) { endpoint = haHit;                              col = rayDrawColor;    }
            else            { endpoint = rayOrigin + rayDir * rayMaxLength;  col = rayDefaultColor; }
            SetRayPositions(rayOrigin, endpoint, col);
            SetRayVisible(true);
        }
        else
        {
            SetRayVisible(false);
        }

        // ── Pinch-release edge ────────────────────────────────────────
        if (!isPinching && _wasPinching && _state == CursorState.Dragging)
            FinalizeGesture();

        _wasPinching = isPinching;

        // ── Active pinch over ResultText ──────────────────────────────
        if (isPinching && hitRT)
        {
            int gap = FindWordGapAt(rtHit);

            if (_state != CursorState.Dragging)
            {
                // New drag started — clear any existing cursor / selection
                ShowCursorBar(false);
                ClearSelectionHighlights();
                ScribbleManager.Instance?.ClearInsertCursor();
                _anchorGap = gap;
                _state     = CursorState.Dragging;
            }

            _activeGap = gap;

            // Live ScribbleManager cursor: only active when no selection yet
            if (_anchorGap == _activeGap)
                ScribbleManager.Instance?.SetInsertCursor(_anchorGap);
            else
                ScribbleManager.Instance?.ClearInsertCursor();

            UpdateLiveVisuals();
            return;
        }

        // ── Cursor-bar position sync while idle ───────────────────────
        // Keeps the caret at the correct position after ScribbleManager
        // advances the insert index when a word is typed / spoken.
        if (!isPinching && _state == CursorState.CursorActive)
        {
            int scIdx = ScribbleManager.Instance?.InsertCursorIndex ?? -1;
            if (scIdx < 0)
            {
                ResetToIdle();   // page-clear or similar wiped the cursor
            }
            else if (scIdx != _anchorGap)
            {
                _anchorGap = scIdx;
                _activeGap = scIdx;
                if (cursorBar != null && cursorBar.gameObject.activeSelf)
                    cursorBar.anchoredPosition = GetCursorCanvasPos(_anchorGap);
            }
        }
    }

    // ==================================================================
    // GESTURE FINALIZATION
    // ==================================================================

    private void FinalizeGesture()
    {
        if (_anchorGap == _activeGap)
        {
            // Tap: single cursor placement
            _state = CursorState.CursorActive;
            ScribbleManager.Instance?.SetInsertCursor(_anchorGap);
            ShowCursorBar(true);
            ClearSelectionHighlights();
            Debug.Log($"{TAG} Cursor placed at gap {_anchorGap}.");
        }
        else
        {
            // Drag: word selection
            _state = CursorState.SelectionActive;
            ScribbleManager.Instance?.ClearInsertCursor();
            ShowCursorBar(false);
            int lo = Mathf.Min(_anchorGap, _activeGap);
            int hi = Mathf.Max(_anchorGap, _activeGap);
            UpdateSelectionHighlights(lo, hi);
            Debug.Log($"{TAG} Selection: gaps [{lo}..{hi}] — {hi - lo} word(s).");
        }
    }

    // ==================================================================
    // PUBLIC API
    // ==================================================================

    /// <summary>
    /// Deletes all selected words and collapses the cursor to the selection start.
    /// Called by WhiteboardPageManager.OnBackspaceClicked when HasActiveSelection.
    /// </summary>
    public void DeleteSelection()
    {
        if (_state != CursorState.SelectionActive) return;

        int startWord = Mathf.Min(_anchorGap, _activeGap);
        int endWord   = Mathf.Max(_anchorGap, _activeGap) - 1;

        ScribbleManager.Instance?.DeleteWordRange(startWord, endWord);

        // Collapse cursor to selection start
        _anchorGap = startWord;
        _activeGap = startWord;
        _state     = CursorState.CursorActive;
        ScribbleManager.Instance?.SetInsertCursor(startWord);
        ShowCursorBar(true);
        ClearSelectionHighlights();

        Debug.Log($"{TAG} DeleteSelection: removed words [{startWord}..{endWord}].");
    }

    // ==================================================================
    // HIT TESTING — ray → RectTransform
    // ==================================================================

    /// <summary>
    /// Returns true if <paramref name="ray"/> intersects the world-space plane
    /// of <paramref name="rt"/> within the rect's bounds.
    /// The canvas forward axis points INTO the surface; the user-facing normal
    /// is therefore -rt.forward.
    /// </summary>
    private bool RaycastRect(Ray ray, RectTransform rt, out Vector3 hitPoint)
    {
        var plane = new Plane(-rt.forward, rt.position);
        if (!plane.Raycast(ray, out float dist) || dist > rayMaxLength)
        {
            hitPoint = Vector3.zero;
            return false;
        }
        hitPoint = ray.GetPoint(dist);
        Vector3 local = rt.InverseTransformPoint(hitPoint);
        Rect    r     = rt.rect;
        return local.x >= r.xMin && local.x <= r.xMax
            && local.y >= r.yMin && local.y <= r.yMax;
    }

    // ==================================================================
    // WORD-GAP DETECTION
    // ==================================================================

    /// <summary>
    /// Maps a world-space point on the ResultText plane to the nearest
    /// word-gap index (0 = before first word, N = after last word).
    /// Uses TMP character positions for accurate per-line hit detection.
    /// </summary>
    private int FindWordGapAt(Vector3 worldPos)
    {
        if (resultText == null || ScribbleManager.Instance == null) return 0;

        int wordCount = ScribbleManager.Instance.CurrentWordCount;
        if (wordCount == 0) return 0;

        resultText.ForceMeshUpdate();
        var textInfo = resultText.textInfo;
        if (textInfo == null || textInfo.wordCount == 0) return wordCount;

        var rt      = (RectTransform)resultText.transform;
        var localXY = new Vector2(rt.InverseTransformPoint(worldPos).x,
                                  rt.InverseTransformPoint(worldPos).y);

        int   tmpWords = Mathf.Min(textInfo.wordCount, wordCount);
        float bestDist = float.MaxValue;
        int   bestGap  = wordCount;  // default: after last word

        // Gap 0 — left edge of first word
        {
            var wi = textInfo.wordInfo[0];
            if (wi.characterCount > 0)
            {
                var   ch  = textInfo.characterInfo[wi.firstCharacterIndex];
                var   pos = new Vector2(ch.bottomLeft.x,
                                        (ch.topLeft.y + ch.bottomLeft.y) * 0.5f);
                float d   = Vector2.Distance(localXY, pos);
                if (d < bestDist) { bestDist = d; bestGap = 0; }
            }
        }

        // Gap i+1 — right edge of word i
        for (int i = 0; i < tmpWords; i++)
        {
            var wi = textInfo.wordInfo[i];
            if (wi.characterCount == 0) continue;
            var   ch  = textInfo.characterInfo[wi.lastCharacterIndex];
            var   pos = new Vector2(ch.bottomRight.x,
                                    (ch.topRight.y + ch.bottomRight.y) * 0.5f);
            float d   = Vector2.Distance(localXY, pos);
            if (d < bestDist) { bestDist = d; bestGap = i + 1; }
        }

        return Mathf.Clamp(bestGap, 0, wordCount);
    }

    // ==================================================================
    // CURSOR BAR VISUAL
    // ==================================================================

    private void ShowCursorBar(bool show)
    {
        if (cursorBar == null) return;

        if (show)
        {
            cursorBar.anchoredPosition = GetCursorCanvasPos(_anchorGap);
            if (!cursorBar.gameObject.activeSelf)
            {
                cursorBar.gameObject.SetActive(true);
                if (_blinkCo != null) StopCoroutine(_blinkCo);
                _blinkCo = StartCoroutine(CoBlink());
            }
        }
        else
        {
            if (_blinkCo != null) { StopCoroutine(_blinkCo); _blinkCo = null; }
            if (cursorBar.gameObject.activeSelf)
                cursorBar.gameObject.SetActive(false);
        }
    }

    private void UpdateLiveVisuals()
    {
        if (_anchorGap == _activeGap)
        {
            ClearSelectionHighlights();
            ShowCursorBar(true);
        }
        else
        {
            ShowCursorBar(false);
            UpdateSelectionHighlights(Mathf.Min(_anchorGap, _activeGap),
                                      Mathf.Max(_anchorGap, _activeGap));
        }
    }

    /// <summary>
    /// Returns the anchoredPosition (relative to canvasRoot centre) for a
    /// caret placed at the given word-gap index, derived from TMP character data.
    /// </summary>
    private Vector2 GetCursorCanvasPos(int insertIndex)
    {
        if (resultText == null || canvasRoot == null) return Vector2.zero;

        resultText.ForceMeshUpdate();
        var textInfo = resultText.textInfo;
        if (textInfo == null || textInfo.characterCount == 0) return Vector2.zero;

        int     tmpWords = textInfo.wordCount;
        Vector3 localPos;

        if (insertIndex == 0 && tmpWords > 0)
        {
            var ch   = textInfo.characterInfo[textInfo.wordInfo[0].firstCharacterIndex];
            localPos = new Vector3(ch.bottomLeft.x,
                                   (ch.topLeft.y + ch.bottomLeft.y) * 0.5f, 0f);
        }
        else
        {
            int wIdx = Mathf.Clamp(insertIndex - 1, 0, tmpWords - 1);
            var wi   = textInfo.wordInfo[wIdx];
            var ch   = textInfo.characterInfo[wi.lastCharacterIndex];
            localPos = new Vector3(ch.bottomRight.x,
                                   (ch.topRight.y + ch.bottomRight.y) * 0.5f, 0f);
        }

        // TMP local → world → canvas local
        Vector3 worldPos    = resultText.transform.TransformPoint(localPos);
        Vector3 canvasLocal = canvasRoot.InverseTransformPoint(worldPos);
        return new Vector2(canvasLocal.x, canvasLocal.y);
    }

    private IEnumerator CoBlink()
    {
        while (true)
        {
            SetCursorAlpha(1f);
            yield return new WaitForSecondsRealtime(blinkOnTime);
            SetCursorAlpha(0f);
            yield return new WaitForSecondsRealtime(blinkOffTime);
        }
    }

    private void SetCursorAlpha(float a)
    {
        if (_cursorImage == null) return;
        var c = _cursorImage.color; c.a = a; _cursorImage.color = c;
    }

    // ==================================================================
    // SELECTION HIGHLIGHT VISUALS
    // ==================================================================

    /// <summary>
    /// Shows one semi-transparent Image quad per selected word, positioned
    /// using TMP character bounding data converted to canvas space.
    /// startGap and endGap are word-gap indices (startGap &lt; endGap).
    /// Selected words: indices [startGap .. endGap-1].
    /// </summary>
    private void UpdateSelectionHighlights(int startGap, int endGap)
    {
        if (resultText == null || canvasRoot == null) { ClearSelectionHighlights(); return; }

        resultText.ForceMeshUpdate();
        var textInfo = resultText.textInfo;
        if (textInfo == null || textInfo.wordCount == 0) { ClearSelectionHighlights(); return; }

        int wordCount   = ScribbleManager.Instance?.CurrentWordCount ?? 0;
        int tmpCount    = Mathf.Min(textInfo.wordCount, wordCount);
        int startWord   = startGap;
        int endWord     = endGap - 1;
        int selectCount = Mathf.Max(0, Mathf.Min(endWord, tmpCount - 1) - startWord + 1);

        if (selectCount <= 0) { ClearSelectionHighlights(); return; }

        EnsureHighlightPool(selectCount);

        for (int i = 0; i < _highlights.Count; i++)
        {
            int wordIdx = startWord + i;
            if (i < selectCount && wordIdx < tmpCount)
            {
                var wi = textInfo.wordInfo[wordIdx];
                if (wi.characterCount == 0) { _highlights[i].gameObject.SetActive(false); continue; }

                var firstCh = textInfo.characterInfo[wi.firstCharacterIndex];
                var lastCh  = textInfo.characterInfo[wi.lastCharacterIndex];

                float x1 = firstCh.bottomLeft.x;
                float x2 = lastCh.bottomRight.x;
                float y1 = firstCh.bottomLeft.y;
                float y2 = firstCh.topLeft.y;

                Vector3 wBL = resultText.transform.TransformPoint(new Vector3(x1, y1, 0f));
                Vector3 wTR = resultText.transform.TransformPoint(new Vector3(x2, y2, 0f));
                Vector3 cBL = canvasRoot.InverseTransformPoint(wBL);
                Vector3 cTR = canvasRoot.InverseTransformPoint(wTR);

                _highlights[i].anchoredPosition = new Vector2((cBL.x + cTR.x) * 0.5f,
                                                               (cBL.y + cTR.y) * 0.5f);
                _highlights[i].sizeDelta        = new Vector2(Mathf.Abs(cTR.x - cBL.x),
                                                               Mathf.Abs(cTR.y - cBL.y));
                _highlights[i].gameObject.SetActive(true);
            }
            else
            {
                _highlights[i].gameObject.SetActive(false);
            }
        }
    }

    private void ClearSelectionHighlights()
    {
        foreach (var h in _highlights)
            if (h != null && h.gameObject.activeSelf) h.gameObject.SetActive(false);
    }

    private void EnsureHighlightPool(int count)
    {
        while (_highlights.Count < count)
        {
            if (canvasRoot == null) break;
            var go  = new GameObject("_SelectionHL");
            go.transform.SetParent(canvasRoot, false);
            var rt  = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot     = new Vector2(0.5f, 0.5f);
            var img = go.AddComponent<Image>();
            img.color         = selectionColor;
            img.raycastTarget = false;
            _highlights.Add(rt);
        }
    }

    // ==================================================================
    // UTILITIES
    // ==================================================================

    private void ResetToIdle()
    {
        _state = CursorState.Idle;
        ShowCursorBar(false);
        ClearSelectionHighlights();
        ScribbleManager.Instance?.ClearInsertCursor();
    }

    // ==================================================================
    // BUTTON POKE DETECTION
    // XRI Interactors are not present in this scene, so TrackedDevice-
    // GraphicRaycaster events never arrive. Instead we detect physical
    // finger-tip proximity and invoke onClick directly.
    // ==================================================================

    private void CheckButtonPoke(Vector3 fingertipWorld)
    {
        var pm  = WhiteboardPageManager.Instance;
        if (pm == null) return;

        Button[] btns =
        {
            pm.prevButton, pm.undoButton, pm.backspaceButton, pm.nextButton,
            JournalMicController.Instance?.MicButton
        };

        if (_btnInZone == null || _btnInZone.Length != btns.Length)
        {
            _btnInZone   = new bool[btns.Length];
            _btnWasClose = new bool[btns.Length];
            _btnLastClickTime = new float[btns.Length];
            _btnLastClickFrame = new int[btns.Length];
            for (int j = 0; j < btns.Length; j++)
            {
                _btnLastClickTime[j] = -999f;
                _btnLastClickFrame[j] = -1;
            }
        }

        for (int i = 0; i < btns.Length; i++)
        {
            var btn = btns[i];
            if (btn == null || !btn.gameObject.activeInHierarchy)
            {
                _btnInZone[i] = _btnWasClose[i] = false;
                continue;
            }

            var   rt    = (RectTransform)btn.transform;
            var   plane = new Plane(-rt.forward, rt.position);
            float sdist = plane.GetDistanceToPoint(fingertipWorld);
            float adist = Mathf.Abs(sdist);

            // Project finger onto button plane for 2D bounds check
            Vector3 onPlane = plane.ClosestPointOnPlane(fingertipWorld);
            Vector3 local   = rt.InverseTransformPoint(onPlane);
            Rect    r       = rt.rect;
            bool inBounds   = local.x >= r.xMin && local.x <= r.xMax
                           && local.y >= r.yMin && local.y <= r.yMax;

            bool inZone  = inBounds && adist <= POKE_HOVER_DIST;
            bool isClose = inBounds && adist <= POKE_FIRE_DIST;

            if (!inZone)
            {
                _btnInZone[i] = _btnWasClose[i] = false;
                continue;
            }

            _btnInZone[i] = true;

            // Rising edge: fire once per poke entry into the close zone
            if (isClose && !_btnWasClose[i])
            {
                Debug.Log($"{TAG} Poke '{btn.name}' sdist={sdist * 1000f:F1} mm interactable={btn.interactable}");
                if (btn.interactable)
                {
                    float now = Time.unscaledTime;
                    bool sameFrame = _btnLastClickFrame[i] == Time.frameCount;
                    bool inCooldown = pokeClickCooldownSec > 0f
                                   && now - _btnLastClickTime[i] < pokeClickCooldownSec;

                    if (!sameFrame && !inCooldown)
                    {
                        btn.onClick.Invoke();
                        _btnLastClickFrame[i] = Time.frameCount;
                        _btnLastClickTime[i] = now;
                    }
                    else if (inCooldown)
                    {
                        Debug.Log($"{TAG} Poke '{btn.name}' ignored by cooldown ({now - _btnLastClickTime[i]:F3}s < {pokeClickCooldownSec:F3}s).");
                    }
                }
            }
            _btnWasClose[i] = isClose;
        }
    }

    // ==================================================================
    // LINE RENDERER
    // ==================================================================

    [Header("Material (for LineRenderer — assign URP Lit or Unlit)")]
    [SerializeField] private Material rayMaterial;

    private void EnsureLineRenderer()
    {
        if (rayLine != null) return;

        rayLine = gameObject.AddComponent<LineRenderer>();
        rayLine.useWorldSpace     = true;
        rayLine.positionCount     = 2;
        rayLine.startWidth        = rayWidth;
        rayLine.endWidth          = rayWidth * 0.25f;  // taper toward target
        rayLine.numCapVertices    = 4;
        rayLine.receiveShadows    = false;
        rayLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        if (rayMaterial != null)
        {
            // Use serialized URP material — guaranteed to survive stripping
            var mat = new Material(rayMaterial);
            mat.SetFloat("_Surface", 1f); // transparent
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.SetFloat("_Blend", 0f);
            mat.SetFloat("_AlphaClip", 0f);
            mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            mat.SetFloat("_ZWrite", 0f);
            rayLine.material = mat;
        }
        else
        {
            // Fallback: try Universal Render Pipeline/Unlit first, then legacy
            var shader = Shader.Find("Universal Render Pipeline/Unlit")
                      ?? Shader.Find("Sprites/Default")
                      ?? Shader.Find("UI/Default");
            if (shader != null)
            {
                var mat = new Material(shader);
                mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                rayLine.material = mat;
            }
            else
            {
                Debug.LogWarning($"{TAG} No shader found for LineRenderer — ray will be invisible.");
            }
        }

        rayLine.enabled = false;
    }

    private void SetRayPositions(Vector3 start, Vector3 end, Color col)
    {
        if (rayLine == null) return;
        rayLine.SetPosition(0, start);
        rayLine.SetPosition(1, end);
        rayLine.startColor = col;
        rayLine.endColor   = new Color(col.r, col.g, col.b, col.a * 0.05f);
    }

    private void SetRayVisible(bool visible)
    {
        if (rayLine != null && rayLine.enabled != visible)
            rayLine.enabled = visible;
    }
}
