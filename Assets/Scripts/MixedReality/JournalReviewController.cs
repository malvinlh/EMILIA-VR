using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.UI;

/// <summary>
/// Manages the post-journal review flow that runs after the user presses DONE.
///
/// Flow:
///   1. Screen fades to black; AZKi avatar is enabled at its authored scene position and
///      the player's camera yaw is snapped to face the avatar invisibly.
///   2. Screen fades back in; an AI comment appears via VRDialoguePanel.
///   3. After the comment a "Keep or release?" choice panel appears near the avatar.
///   4. Keep/Discard chosen → PreNDuringJournal disabled, PostJournal enabled, locomotion
///      unlocked so the player can walk freely.
///   5. WaitingForCork — player grabs cork (controller or hand), brings to bottle neck →
///      CorkSnapZone snaps it permanently and fires OnCorkSealed.
///   6. Keep path → player carries bottle to bottle rack → bottle destroyed, groups reset.
///   7. Release path → player throws bottle into ocean → Y-threshold detection →
///      bottle destroyed, groups reset.
///
/// Wire up in Inspector:
///   • avatarRoot              — AZKi root Transform (must be inactive at scene start)
///   • dialoguePanelGO         — VRDialoguePanel root GameObject
///   • journalChairTable       — used for ocean Y-threshold only
///   • bottleRoot              — PostJournal bottle root Transform
///   • wineRack                — Bottle Rack root Transform (optional; falls back to GameObject.Find)
///   • preNDuringJournalGroup  — PreNDuringJournal GameObject (whiteboard + writing bottle)
///   • postJournalGroup        — PostJournal GameObject (grab bottle + cork)
///   • bottleNeckZone          — CorkSnapZone on the PostJournal bottle neck child
///   • plugSfxSource           — AudioSource for cork plug SFX (may be null)
/// </summary>
public class JournalReviewController : MonoBehaviour
{
    [Header("Avatar")]
    [Tooltip("Root transform of the AZKi avatar. Activated at its authored scene position when review begins.")]
    public Transform avatarRoot;

    [Header("Dialogue")]
    [Tooltip("VRDialoguePanel root GameObject (contains VRDialoguePanel + VRDialogueFader).")]
    public GameObject dialoguePanelGO;

    [Header("Stand Point")]
    [Tooltip("Player is teleported here (XZ) when the review begins. " +
             "Place as a child of the environment so it moves with the island. " +
             "If null, only the camera yaw snap runs (no XZ teleport).")]
    public Transform standPoint;

    [Header("Scene Objects")]
    [Tooltip("JournalChairTable Transform — used as Y threshold for the ocean bottle detection.")]
    public Transform journalChairTable;
    [Tooltip("PostJournal bottle root Transform (the one the player physically grabs).")]
    public Transform bottleRoot;
    [Tooltip("Bottle Rack root Transform. Falls back to GameObject.Find(\"Wine Rack\") if null.")]
    public Transform wineRack;

    [Header("Scene Groups")]
    [Tooltip("PreNDuringJournal group — whiteboard + writing bottle. Active during idle/journaling, hidden during post-journal.")]
    public GameObject preNDuringJournalGroup;
    [Tooltip("PostJournal group — grab bottle + cork. Hidden during idle/journaling, active during post-journal.")]
    public GameObject postJournalGroup;

    [Header("Cork")]
    [Tooltip("CorkSnapZone script on the PostJournal bottle neck child trigger collider.")]
    public CorkSnapZone bottleNeckZone;

    [Header("Audio")]
    [Tooltip("AudioSource that plays the cork-plug SFX. May be null — skipped silently if unassigned.")]
    public AudioSource plugSfxSource;

    // ── State ─────────────────────────────────────────────────────────────
    private enum ReviewState
    {
        Inactive,
        ShowingComment,
        ShowingChoice,
        WaitingForCork,
        WaitingForBottle,
        WaitingForRack,
        Complete
    }

    private ReviewState _state      = ReviewState.Inactive;
    private bool        _keepChosen;
    private Action<bool> _onComplete;   // true = save the journal
    private float        _preJournalXROriginY;

    private VRDialoguePanel _dialoguePanel;
    private VRDialogueFader _dialogueFader;
    private GameObject _choicePanel;
    private GameObject _screenFadeOverlay;
    private Transform  _xrOriginTransform;

    // ================================================================
    // UNITY
    // ================================================================

    private void Awake()
    {
        if (dialoguePanelGO != null)
        {
            _dialoguePanel = dialoguePanelGO.GetComponent<VRDialoguePanel>();
            _dialogueFader = dialoguePanelGO.GetComponent<VRDialogueFader>();
        }

        // Start the panel hidden so it doesn't flash on scene load.
        _dialogueFader?.HideImmediate();

        // PostJournal group is inactive until Keep/Discard is chosen.
        if (postJournalGroup != null)
            postJournalGroup.SetActive(false);
    }

    // ================================================================
    // PUBLIC API
    // ================================================================

    /// <summary>
    /// Called by JournalSessionManager.EndSession() to run the review flow.
    /// <paramref name="onComplete"/> receives true if the journal should be saved.
    /// <paramref name="preJournalXROriginY"/> is the XR Origin Y before TeleportToSeatPoint —
    /// used to restore the player to their natural standing height during the review.
    /// </summary>
    public void BeginReview(Action<bool> onComplete, float preJournalXROriginY)
    {
        if (_state != ReviewState.Inactive)
        {
            Debug.LogWarning("[JournalReview] BeginReview called while already in progress.");
            return;
        }
        _onComplete           = onComplete;
        _preJournalXROriginY  = preJournalXROriginY;
        StartCoroutine(ReviewCoroutine());
    }

    // ================================================================
    // REVIEW FLOW
    // ================================================================

    private IEnumerator ReviewCoroutine()
    {
        _state = ReviewState.ShowingComment;

        // 1. Fade to black — avatar activation and camera snap happen invisibly.
        yield return StartCoroutine(FadeScreen(1f, 0.5f));

        // 2. Enable avatar at its authored scene position (no runtime repositioning).
        EnableAvatar();

        // 3. Teleport player to StandPoint (XZ) at pre-journaling standing height,
        //    then snap camera yaw to face the avatar — both invisible during blackout.
        TeleportToStandPoint();
        SnapCameraToFaceAvatar();

        // 4. Fade back in — player now sees the avatar.
        yield return StartCoroutine(FadeScreen(0f, 0.5f));

        // 5. Re-enable the controller far-cast ray.
        //    PokeGestureDetector (on the XR Origin Hands prefab) disables far-casting
        //    while the index finger is in writing pose. If hand tracking stops or the
        //    poke state isn't cleanly resolved during the journaling→review transition,
        //    enableFarCasting is left false and the controller trigger can't hit UI.
        RestoreControllerRay();

        // 6. Show AI comment via dialogue panel.
        const string aiComment =
            "You've taken a meaningful step today by putting your thoughts into words. " +
            "Reflecting on what you've written can help you better understand your feelings " +
            "and find clarity in moments of uncertainty.\n\n" +
            "Your words matter — and so do you. I'm proud of you for showing up.\n\n" +
            "— EMILIA";

        ShowDialogue(aiComment);

        // 7. Wait for the typewriter to finish the last page.
        bool commentDone = false;
        if (_dialoguePanel != null)
        {
            _dialoguePanel.OnContentFullyDisplayed += OnCommentFinished;
            void OnCommentFinished()
            {
                _dialoguePanel.OnContentFullyDisplayed -= OnCommentFinished;
                commentDone = true;
            }
        }
        else
        {
            commentDone = true;
        }

        yield return new WaitUntil(() => commentDone);
        yield return new WaitForSeconds(0.6f);

        // 8. Present the keep-or-release choice.
        _state = ReviewState.ShowingChoice;
        ShowChoicePanel();
    }

    // ── Avatar ───────────────────────────────────────────────────────────

    private void EnableAvatar()
    {
        if (avatarRoot == null) return;
        avatarRoot.gameObject.SetActive(true);
    }

    // ── Dialogue ─────────────────────────────────────────────────────────

    private void ShowDialogue(string text)
    {
        if (_dialoguePanel == null) return;
        _dialogueFader?.FadeIn();
        _dialoguePanel.ShowText(text);
    }

    // ================================================================
    // CHOICE PANEL
    // ================================================================

    private void ShowChoicePanel()
    {
        if (_choicePanel == null)
            _choicePanel = BuildChoicePanel();

        // Position in front of and at comfortable height relative to the avatar's
        // authored scene position so neither the avatar nor the panel need to move.
        if (avatarRoot != null)
        {
            _choicePanel.transform.position =
                avatarRoot.position
                + avatarRoot.forward * 0.6f   // slightly in front of avatar
                + Vector3.up * 1.2f;          // comfortable chest / reading height

            // Billboard toward the player.
            Camera cam = Camera.main;
            if (cam != null)
            {
                Vector3 lookDir = _choicePanel.transform.position - cam.transform.position;
                if (lookDir.sqrMagnitude > 0.001f)
                    _choicePanel.transform.rotation = Quaternion.LookRotation(lookDir);
            }
        }
        else
        {
            Debug.LogWarning("[JournalReview] avatarRoot is null — choice panel may be at origin.");
        }

        _choicePanel.SetActive(true);
    }

    // ── Palette — mirrors VRDialoguePanel's visual identity ──────────
    private static readonly Color s_PanelBg     = new Color(0.969f, 0.918f, 0.918f, 1.00f); // warm blush
    private static readonly Color s_AccentMauve = new Color(0.780f, 0.663f, 0.722f, 1.00f); // dusty rose
    private static readonly Color s_TextDark    = new Color(0.369f, 0.329f, 0.349f, 1.00f); // dark brownish-purple
    private static readonly Color s_BtnKeep     = new Color(0.490f, 0.730f, 0.560f, 1.00f); // sage green
    private static readonly Color s_BtnRelease  = new Color(0.790f, 0.470f, 0.450f, 1.00f); // warm coral

    private GameObject BuildChoicePanel()
    {
        // ── Root (world-space canvas) ──────────────────────────────────
        var root   = new GameObject("JournalChoicePanel");
        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;
        // Canvas rect must match content size — TrackedDeviceGraphicRaycaster bounds-checks
        // the hit point against this rect before dispatching pointer events.
        root.GetComponent<RectTransform>().sizeDelta = new Vector2(640f, 300f);
        root.AddComponent<CanvasScaler>();
        root.AddComponent<TrackedDeviceGraphicRaycaster>(); // XRI-compatible raycaster
        root.transform.localScale = Vector3.one * 0.001f;

        // ── Background panel ──────────────────────────────────────────
        var bg    = new GameObject("Background");
        bg.transform.SetParent(root.transform, false);
        var bgImg = bg.AddComponent<Image>();
        bgImg.color = s_PanelBg;
        bg.GetComponent<RectTransform>().sizeDelta = new Vector2(640f, 300f);

        // ── Top accent bar (mauve, like the VRDialoguePanel border) ───
        var bar    = new GameObject("AccentBar");
        bar.transform.SetParent(bg.transform, false);
        var barImg = bar.AddComponent<Image>();
        barImg.color = s_AccentMauve;
        var barRect = bar.GetComponent<RectTransform>();
        barRect.anchorMin = new Vector2(0f, 1f);
        barRect.anchorMax = new Vector2(1f, 1f);
        barRect.pivot     = new Vector2(0.5f, 1f);
        barRect.sizeDelta = new Vector2(0f, 26f);

        // ── Question ──────────────────────────────────────────────────
        var qGO  = new GameObject("Question");
        qGO.transform.SetParent(bg.transform, false);
        var qTmp = qGO.AddComponent<TextMeshProUGUI>();
        qTmp.text      = "Would you like to preserve this journal entry?";
        qTmp.fontSize  = 28f;
        qTmp.alignment = TextAlignmentOptions.Center;
        qTmp.color     = s_TextDark;
        qTmp.fontStyle = FontStyles.Bold;
        var qRect = qGO.GetComponent<RectTransform>();
        qRect.anchorMin = new Vector2(0.05f, 0.42f);
        qRect.anchorMax = new Vector2(0.95f, 0.88f);
        qRect.offsetMin = qRect.offsetMax = Vector2.zero;

        // ── Buttons ───────────────────────────────────────────────────
        var row     = new GameObject("Buttons");
        row.transform.SetParent(bg.transform, false);
        var rowRect = row.AddComponent<RectTransform>();
        rowRect.anchorMin = new Vector2(0.05f, 0.06f);
        rowRect.anchorMax = new Vector2(0.95f, 0.36f);
        rowRect.offsetMin = rowRect.offsetMax = Vector2.zero;

        // "Yes, keep it" — save → bottle rack path
        var keepBtn = MakeButton("Yes, keep it", s_BtnKeep, s_TextDark,
            row.transform, new Vector2(0f, 0f), new Vector2(0.44f, 1f));
        keepBtn.onClick.AddListener(OnKeepChosen);

        // "Let it go" — discard → ocean path
        var releaseBtn = MakeButton("Let it go", s_BtnRelease, Color.white,
            row.transform, new Vector2(0.56f, 0f), new Vector2(1f, 1f));
        releaseBtn.onClick.AddListener(OnReleaseChosen);

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

        // Label
        var lblGO  = new GameObject("Label");
        lblGO.transform.SetParent(go.transform, false);
        var tmp    = lblGO.AddComponent<TextMeshProUGUI>();
        tmp.text      = label;
        tmp.fontSize  = 24f;
        tmp.alignment = TextAlignmentOptions.Center;
        tmp.color     = textColor;
        tmp.fontStyle = FontStyles.Bold;
        var lblRect   = lblGO.GetComponent<RectTransform>();
        lblRect.anchorMin = Vector2.zero;
        lblRect.anchorMax = Vector2.one;
        lblRect.offsetMin = lblRect.offsetMax = Vector2.zero;

        return btn;
    }

    private void HideChoicePanel()
    {
        if (_choicePanel != null)
            _choicePanel.SetActive(false);
    }

    // ================================================================
    // CHOICE HANDLERS
    // ================================================================

    /// <summary>User wants to keep (save) the journal → cork → bottle rack path.</summary>
    private void OnKeepChosen()
    {
        if (_state != ReviewState.ShowingChoice) return;
        _keepChosen = true;
        HideChoicePanel();
        BeginCorkPhase();
    }

    /// <summary>User wants to release (discard) the journal → cork → ocean throw path.</summary>
    private void OnReleaseChosen()
    {
        if (_state != ReviewState.ShowingChoice) return;
        _keepChosen = false;
        HideChoicePanel();
        BeginCorkPhase();
    }

    // ================================================================
    // CORK PHASE
    // ================================================================

    private void BeginCorkPhase()
    {
        // Switch scene groups: hide whiteboard area, reveal post-journal bottle + cork.
        if (preNDuringJournalGroup != null) preNDuringJournalGroup.SetActive(false);
        if (postJournalGroup       != null) postJournalGroup.SetActive(true);

        // Unlock locomotion so the player can move freely after the cork step.
        JournalSessionManager.Instance?.AllowLocomotion();

        // Guide the player to seal the bottle.
        ShowDialogue("Before you go — seal your bottle by plugging the cork into the neck.");

        if (bottleNeckZone != null)
            bottleNeckZone.OnCorkSealed += OnCorkSealed;
        else
            Debug.LogWarning("[JournalReview] bottleNeckZone not assigned — cork step skipped.");

        _state = ReviewState.WaitingForCork;
    }

    private void OnCorkSealed()
    {
        if (bottleNeckZone != null)
            bottleNeckZone.OnCorkSealed -= OnCorkSealed;

        if (plugSfxSource != null)
            plugSfxSource.Play();

        if (_keepChosen)
        {
            _state = ReviewState.WaitingForRack;

            ShowDialogue(
                "Carry the bottle to the bottle rack nearby to safely store your entry.");

            // Wire the bottle rack socket at runtime.
            // Wine Rack > Socket uses XRSocketInteractor — subscribe to its selectEntered
            // so OnRackSelected() fires when the bottle snaps into the socket.
            Transform rack = wineRack;
            if (rack == null)
            {
                var rackGO = GameObject.Find("Wine Rack");
                if (rackGO != null) rack = rackGO.transform;
            }

            if (rack != null)
            {
                var socket = rack.GetComponentInChildren<XRSocketInteractor>();
                if (socket != null)
                    socket.selectEntered.AddListener(_ => OnRackSelected());
                else
                    Debug.LogWarning("[JournalReview] No XRSocketInteractor found under Bottle Rack.");
            }
            else
            {
                Debug.LogWarning("[JournalReview] Bottle Rack not found — rack save path unavailable.");
            }
        }
        else
        {
            _state = ReviewState.WaitingForBottle;

            ShowDialogue(
                "When you're ready, walk to the edge and toss the bottle into the ocean. " +
                "You've already done the hard work.");
        }
    }

    // ================================================================
    // UPDATE — BOTTLE OCEAN DETECTION
    // ================================================================

    private void Update()
    {
        if (_state != ReviewState.WaitingForBottle) return;

        // If the bottle was destroyed by any other means, consider it released.
        if (bottleRoot == null)
        {
            ResetSceneGroups();
            CompleteReview(saveJournal: false);
            return;
        }

        // Ocean threshold: 3 metres below the table floor level.
        float threshold = (journalChairTable != null)
            ? journalChairTable.position.y - 3f
            : -5f;

        if (bottleRoot.position.y < threshold)
        {
            Destroy(bottleRoot.gameObject);
            ResetSceneGroups();
            CompleteReview(saveJournal: false);
        }
    }

    // ================================================================
    // RACK SELECTION
    // ================================================================

    private void OnRackSelected()
    {
        if (_state != ReviewState.WaitingForRack) return;
        if (bottleRoot != null)
            Destroy(bottleRoot.gameObject);
        ResetSceneGroups();
        CompleteReview(saveJournal: true);
    }

    // ================================================================
    // GROUP RESET
    // ================================================================

    /// <summary>
    /// Restores scene group visibility after the bottle is disposed so
    /// a new journaling session can begin immediately.
    /// </summary>
    private void ResetSceneGroups()
    {
        if (postJournalGroup       != null) postJournalGroup.SetActive(false);
        if (preNDuringJournalGroup != null) preNDuringJournalGroup.SetActive(true);
    }

    // ================================================================
    // COMPLETION
    // ================================================================

    private void CompleteReview(bool saveJournal)
    {
        if (_state == ReviewState.Complete) return;
        _state = ReviewState.Complete;

        _dialogueFader?.FadeOut();
        HideChoicePanel();

        if (avatarRoot != null)
            avatarRoot.gameObject.SetActive(false);

        _onComplete?.Invoke(saveJournal);
    }

    // ================================================================
    // SCREEN FADE
    // ================================================================

    /// <summary>Fades the full-screen black overlay to <paramref name="targetAlpha"/> over <paramref name="duration"/> seconds.</summary>
    private IEnumerator FadeScreen(float targetAlpha, float duration)
    {
        if (_screenFadeOverlay == null)
            _screenFadeOverlay = CreateFadeOverlay();
        _screenFadeOverlay.SetActive(true);

        var cg = _screenFadeOverlay.GetComponent<CanvasGroup>();
        float startAlpha = cg.alpha;
        float elapsed    = 0f;

        while (elapsed < duration)
        {
            elapsed  += Time.deltaTime;
            cg.alpha  = Mathf.Lerp(startAlpha, targetAlpha, elapsed / duration);
            yield return null;
        }
        cg.alpha = targetAlpha;

        if (targetAlpha <= 0f)
            _screenFadeOverlay.SetActive(false);
    }

    /// <summary>
    /// Creates a black world-space quad parented to the main camera.
    /// Sized to cover the full VR FOV (both eyes) so it acts as a solid
    /// screen-fade overlay.
    /// </summary>
    private GameObject CreateFadeOverlay()
    {
        Camera cam = Camera.main ?? FindFirstObjectByType<Camera>();

        var go = new GameObject("__JournalReviewFade__");
        go.transform.SetParent(cam.transform, false);

        // Push just past the near clip plane so the quad is always visible.
        float z = cam.nearClipPlane + 0.05f;
        go.transform.localPosition = new Vector3(0f, 0f, z);
        go.transform.localRotation = Quaternion.identity;
        go.transform.localScale    = Vector3.one;

        var canvas = go.AddComponent<Canvas>();
        canvas.renderMode   = RenderMode.WorldSpace;
        canvas.sortingOrder = 32767;

        // Oversized to cover Quest 3's ~110° H FOV for both eyes (accounts for IPD).
        var rt = go.GetComponent<RectTransform>();
        rt.sizeDelta = new Vector2(z * 12f, z * 9f);

        var img = go.AddComponent<Image>();
        img.color         = Color.black;
        img.raycastTarget = false;

        var cg = go.AddComponent<CanvasGroup>();
        cg.alpha          = 0f;
        cg.blocksRaycasts = false;

        go.SetActive(false);
        return go;
    }

    // ================================================================
    // CONTROLLER RAY
    // ================================================================

    /// <summary>
    /// Re-enables far-casting on every NearFarInteractor in the scene.
    ///
    /// During the journaling phase the user writes with hand tracking. The
    /// PokeGestureDetector on the XR Origin Hands prefab calls
    /// NearFarInteractor.set_enableFarCasting(false) while the index finger is
    /// in poke pose. If hand tracking stops or the poke-end event is missed
    /// (e.g. the user puts on controllers while still in writing pose), far
    /// casting is left permanently disabled. Forcing it back here ensures the
    /// controller ray reaches the review UI canvases.
    /// </summary>
    private static void RestoreControllerRay()
    {
        foreach (var interactor in FindObjectsByType<NearFarInteractor>(FindObjectsSortMode.None))
            interactor.enableFarCasting = true;
    }

    // ================================================================
    // PLAYER POSITIONING
    // ================================================================

    /// <summary>
    /// Moves the XR Origin so the camera's XZ lands on <see cref="standPoint"/>
    /// and the XR Origin Y is restored to the pre-journaling value (standing height).
    /// No-ops if <see cref="standPoint"/> is null.
    /// </summary>
    private void TeleportToStandPoint()
    {
        if (standPoint == null) return;

        if (_xrOriginTransform == null)
        {
            var origin = FindFirstObjectByType<Unity.XR.CoreUtils.XROrigin>();
            if (origin != null) _xrOriginTransform = origin.transform;
        }
        if (_xrOriginTransform == null) return;

        Camera cam = Camera.main;
        if (cam == null) return;

        // Shift XR Origin XZ so the camera lands at the StandPoint world XZ.
        Vector3 xzDelta = standPoint.position - cam.transform.position;
        xzDelta.y = 0f;

        // Apply XZ shift and restore pre-journaling Y (player stands at natural height).
        _xrOriginTransform.position = new Vector3(
            _xrOriginTransform.position.x + xzDelta.x,
            _preJournalXROriginY,
            _xrOriginTransform.position.z + xzDelta.z);
    }

    // ================================================================
    // CAMERA ORIENTATION
    // ================================================================

    /// <summary>
    /// Snaps the XR Origin yaw so the camera faces the avatar.
    /// Must be called during a screen blackout — the snap is instantaneous.
    /// </summary>
    private void SnapCameraToFaceAvatar()
    {
        if (avatarRoot == null) return;

        if (_xrOriginTransform == null)
        {
            var origin = FindFirstObjectByType<Unity.XR.CoreUtils.XROrigin>();
            if (origin != null) _xrOriginTransform = origin.transform;
        }
        if (_xrOriginTransform == null) return;

        Camera cam = Camera.main;
        if (cam == null) return;

        Vector3 toAvatar = avatarRoot.position - cam.transform.position;
        toAvatar.y = 0f;
        if (toAvatar.sqrMagnitude < 0.001f) return;

        float targetYaw  = Quaternion.LookRotation(toAvatar).eulerAngles.y;
        float currentYaw = cam.transform.eulerAngles.y;
        float yawDelta   = Mathf.DeltaAngle(currentYaw, targetYaw);

        _xrOriginTransform.RotateAround(cam.transform.position, Vector3.up, yawDelta);
    }

    // ================================================================
    // CLEANUP
    // ================================================================

    private void OnDestroy()
    {
        if (bottleNeckZone != null)
            bottleNeckZone.OnCorkSealed -= OnCorkSealed;
        if (_choicePanel != null)
            Destroy(_choicePanel);
        if (_screenFadeOverlay != null)
            Destroy(_screenFadeOverlay);
    }
}
