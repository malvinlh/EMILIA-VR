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
    [Tooltip("Whiteboard UI GameObject (separate from preNDuringJournalGroup). " +
             "Disabled when entering review/post-journal phases; re-enabled when session resets.")]
    public GameObject whiteboardUIGroup;

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

    /// <summary>True while the player should carry the sealed bottle to the wine rack.</summary>
    public bool IsWaitingForRack => _state == ReviewState.WaitingForRack;

    private ReviewState _state      = ReviewState.Inactive;
    private bool        _keepChosen;
    private Action<bool> _onComplete;   // true = save the journal
    private float        _preJournalXROriginY;

    private VRDialoguePanel _dialoguePanel;
    private VRDialogueFader _dialogueFader;
    private GameObject _choicePanel;
    private GameObject _screenFadeOverlay;
    private Transform  _xrOriginTransform;

    // Bottle's original local transform — captured once at scene start so it can be
    // restored at the beginning of each new journaling session's cork phase.
    private Transform  _bottleOriginalParent;
    private Vector3    _bottleOriginalLocalPos;
    private Quaternion _bottleOriginalLocalRot;
    private bool       _bottleOriginalStored;

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

        // Store the bottle's original local transform so it can be reset each session.
        if (bottleRoot != null)
        {
            _bottleOriginalParent   = bottleRoot.parent;
            _bottleOriginalLocalPos = bottleRoot.localPosition;
            _bottleOriginalLocalRot = bottleRoot.localRotation;
            _bottleOriginalStored   = true;
        }
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
        Debug.Log("[JournalReview] ReviewCoroutine started. Hiding WhiteboardUI.");

        // Hide the whiteboard UI immediately — it must not be visible during review or post-journal.
        if (whiteboardUIGroup != null) whiteboardUIGroup.SetActive(false);
        else Debug.LogWarning("[JournalReview] whiteboardUIGroup not assigned — whiteboard UI may remain visible during review.");

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
        Debug.Log($"[JournalReview] BeginCorkPhase — keepChosen={_keepChosen}, bottleRoot={bottleRoot?.name ?? "NULL"}");

        // Reset the cork back to its original state before revealing the group.
        if (bottleNeckZone != null) bottleNeckZone.ResetForNewSession();

        // Restore bottle transform first (kinematic so gravity doesn't fire before position is set).
        if (bottleRoot != null && _bottleOriginalStored)
        {
            bottleRoot.SetParent(_bottleOriginalParent, worldPositionStays: false);
            bottleRoot.localPosition = _bottleOriginalLocalPos;
            bottleRoot.localRotation = _bottleOriginalLocalRot;
            var rb = bottleRoot.GetComponent<Rigidbody>();
            if (rb != null) { rb.isKinematic = true; rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
        }

        // Hide the whiteboard UI groups.
        if (preNDuringJournalGroup != null) preNDuringJournalGroup.SetActive(false);
        if (whiteboardUIGroup      != null) whiteboardUIGroup.SetActive(false);

        // postJournalGroup is activated once on the first session and left active forever.
        // Subsequent sessions just re-enable individual components via EnableBottleComponents.
        if (postJournalGroup != null && !postJournalGroup.activeSelf)
            postJournalGroup.SetActive(true);

        // Re-enable all bottle (and sealed cork) components so everything is visible/grabbable.
        EnableBottleComponents();

        // Release physics now that the bottle is at the correct position and visible.
        if (bottleRoot != null)
        {
            var rb = bottleRoot.GetComponent<Rigidbody>();
            if (rb != null) { rb.isKinematic = false; rb.useGravity = true; }

            // Refresh ItemAutoReset origin so blink-back uses the post-restore position.
            bottleRoot.GetComponent<ItemAutoReset>()?.RefreshOrigin();
        }

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
            Debug.Log($"[JournalReview] Cork sealed — state → WaitingForRack. bottleRoot={bottleRoot?.name ?? "NULL"}");

            ShowDialogue(
                "Carry the bottle to the bottle rack nearby — just bring it close and it will rest there.");
        }
        else
        {
            _state = ReviewState.WaitingForBottle;
            Debug.Log($"[JournalReview] Cork sealed — state → WaitingForBottle. bottleRoot={bottleRoot?.name ?? "NULL"}");

            ShowDialogue(
                "When you're ready, walk to the edge and toss the bottle into the ocean. " +
                "You've already done the hard work.");
        }
    }

    // ================================================================
    // UPDATE — BOTTLE OCEAN DETECTION
    // ================================================================

    /// <summary>True while the player should throw the bottle into the sea.</summary>
    public bool IsWaitingForBottle => _state == ReviewState.WaitingForBottle;

    private void Update()
    {
        if (_state != ReviewState.WaitingForBottle) return;

        // Safety fallback: bottle components were disabled by external means (e.g. the
        // XRGrabInteractable was turned off without going through HandleBottleInSea).
        // Checking the grab interactable is more reliable than a null check now that we
        // never call SetActive(false) or Destroy on bottleRoot.
        if (bottleRoot == null) { CompleteReview(saveJournal: false); return; }
        var grab = bottleRoot.GetComponent<XRGrabInteractable>();
        if (grab != null && !grab.enabled)
            CompleteReview(saveJournal: false);
    }

    // ================================================================
    // SEA DETECTION (called by SeaBottleDetector)
    // ================================================================

    /// <summary>
    /// Called by SeaBottleDetector when the thrown bottle contacts the Sea mesh collider.
    /// Destroys the bottle, shows a short farewell dialogue, then ends the session.
    /// </summary>
    public void HandleBottleInSea()
    {
        Debug.Log($"[JournalReview] HandleBottleInSea called — state={_state}, bottleRoot={bottleRoot?.name ?? "NULL (already destroyed)"}");
        if (_state != ReviewState.WaitingForBottle)
        {
            Debug.LogWarning($"[JournalReview] HandleBottleInSea ignored — wrong state ({_state}).");
            return;
        }
        if (bottleRoot != null)
        {
            Debug.Log($"[JournalReview] Disposing bottle (sea) — name={bottleRoot.name}");
            DisableBottleComponents();
        }
        Debug.Log("[JournalReview] Bottle disposed (sea). Starting BottleDisposedCoroutine.");
        StartCoroutine(BottleDisposedCoroutine(
            "Letting go takes courage too.\nThe ocean will carry it — and so will you.",
            saveJournal: false));
    }

    // ================================================================
    // RACK SELECTION (called by WineRackProximity)
    // ================================================================

    /// <summary>
    /// Called by WineRackProximity when the player brings the held bottle into the
    /// wine rack's trigger zone. Destroys the bottle, shows a short dialogue, then ends the session.
    /// </summary>
    public void HandleBottleRacked()
    {
        Debug.Log($"[JournalReview] HandleBottleRacked called — state={_state}, bottleRoot={bottleRoot?.name ?? "NULL (already destroyed)"}");
        if (_state != ReviewState.WaitingForRack)
        {
            Debug.LogWarning($"[JournalReview] HandleBottleRacked ignored — wrong state ({_state}).");
            return;
        }
        if (bottleRoot != null)
        {
            Debug.Log($"[JournalReview] Disposing bottle (rack) — name={bottleRoot.name}");
            DisableBottleComponents();
        }
        Debug.Log("[JournalReview] Bottle disposed (rack). Starting BottleDisposedCoroutine.");
        StartCoroutine(BottleDisposedCoroutine(
            "Your words are safe now.\nRest easy — you showed up for yourself today.",
            saveJournal: true));
    }

    // ================================================================
    // BOTTLE DISPOSED DIALOGUE + COMPLETION
    // ================================================================

    private IEnumerator BottleDisposedCoroutine(string message, bool saveJournal)
    {
        // Lock state immediately so neither handler can fire again.
        _state = ReviewState.Complete;
        Debug.Log($"[JournalReview] BottleDisposedCoroutine — state locked to Complete. saveJournal={saveJournal}. dialoguePanel={(_dialoguePanel != null ? "OK" : "NULL")}");
        HideChoicePanel();

        // Avatar says something short and calming.
        ShowDialogue(message);

        // Wait for the typewriter to finish the message.
        bool done = false;
        if (_dialoguePanel != null)
        {
            void OnDone() { _dialoguePanel.OnContentFullyDisplayed -= OnDone; done = true; }
            _dialoguePanel.OnContentFullyDisplayed += OnDone;
        }
        else
        {
            Debug.LogWarning("[JournalReview] dialoguePanel is null — skipping typewriter wait.");
            done = true;
        }

        yield return new WaitUntil(() => done);
        Debug.Log("[JournalReview] Typewriter done. Waiting 1s...");
        yield return new WaitForSeconds(1f);

        // Fade out dialogue and hide avatar.
        _dialogueFader?.FadeOut();
        if (avatarRoot != null)
            avatarRoot.gameObject.SetActive(false);

        // Re-enable the start button area, whiteboard UI, and hide the post-journal bottle.
        ResetSceneGroups();

        Debug.Log($"[JournalReview] Invoking _onComplete (null={_onComplete == null}). About to reset state to Inactive.");
        _onComplete?.Invoke(saveJournal);
        _state = ReviewState.Inactive; // reset so BeginReview() works on the next session
        Debug.Log("[JournalReview] BottleDisposedCoroutine complete — state = Inactive.");
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
        // PostJournal group is intentionally NOT deactivated here. Toggling a group's
        // SetActive between sessions causes Unity to silently refuse to re-show children
        // that were explicitly set inactive at any prior point in the hierarchy chain.
        // Instead we keep the group active and manage individual item visibility via
        // renderer/collider/interactable components (see DisableBottleComponents /
        // EnableBottleComponents). Only the whiteboard UI groups change here.
        Debug.Log("[JournalReview] ResetSceneGroups — PreNDuringJournal ON, WhiteboardUI ON.");
        if (preNDuringJournalGroup != null) preNDuringJournalGroup.SetActive(true);
        if (whiteboardUIGroup      != null) whiteboardUIGroup.SetActive(true);

        // Reposition the bottle to its authored origin so it is ready for the next session.
        // Components remain disabled (DisableBottleComponents was called at disposal);
        // EnableBottleComponents in BeginCorkPhase makes everything visible again.
        if (bottleRoot != null && _bottleOriginalStored)
        {
            bottleRoot.SetParent(_bottleOriginalParent, worldPositionStays: false);
            bottleRoot.localPosition = _bottleOriginalLocalPos;
            bottleRoot.localRotation = _bottleOriginalLocalRot;
            var rb = bottleRoot.GetComponent<Rigidbody>();
            if (rb != null) { rb.isKinematic = true; rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
            Debug.Log($"[JournalReview] ResetSceneGroups — bottleRoot '{bottleRoot.name}' repositioned for next session.");
        }
    }

    // ================================================================
    // BOTTLE COMPONENT HELPERS
    // ================================================================

    /// <summary>
    /// Hides the bottle (and any sealed cork that is a child of it) without calling
    /// SetActive so the GameObject stays alive and can be fully restored next session.
    /// Also disables the XRGrabInteractable so ItemAutoReset's guard suppresses blink-back
    /// while the bottle is in the "disposed" state.
    /// </summary>
    private void DisableBottleComponents()
    {
        if (bottleRoot == null) return;

        var grab = bottleRoot.GetComponent<XRGrabInteractable>();
        if (grab != null) grab.enabled = false;

        var rb = bottleRoot.GetComponent<Rigidbody>();
        if (rb != null) { rb.isKinematic = true; rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }

        foreach (var r in bottleRoot.GetComponentsInChildren<Renderer>(true))
            r.enabled = false;
        foreach (var c in bottleRoot.GetComponentsInChildren<Collider>(true))
            c.enabled = false;
    }

    /// <summary>
    /// Re-enables all renderers and colliders across the entire PostJournal group
    /// (bottle and cork), then re-enables the bottle's XRGrabInteractable.
    /// Called at the start of each cork phase so both items are interactable.
    /// </summary>
    private void EnableBottleComponents()
    {
        // Re-enable across the whole post-journal group so the cork (restored to its
        // original parent by CorkSnapZone.ResetForNewSession) is also made visible.
        if (postJournalGroup != null)
        {
            foreach (var r in postJournalGroup.GetComponentsInChildren<Renderer>(true))
                r.enabled = true;
            foreach (var c in postJournalGroup.GetComponentsInChildren<Collider>(true))
                c.enabled = true;
        }

        if (bottleRoot == null) return;
        var grab = bottleRoot.GetComponent<XRGrabInteractable>();
        if (grab != null) grab.enabled = true;
    }

    // ================================================================
    // COMPLETION
    // ================================================================

    private void CompleteReview(bool saveJournal)
    {
        Debug.Log($"[JournalReview] CompleteReview called — state={_state}, saveJournal={saveJournal}, _onComplete null={_onComplete == null}");
        if (_state == ReviewState.Complete) { Debug.LogWarning("[JournalReview] CompleteReview skipped — already Complete."); return; }
        _state = ReviewState.Complete;

        _dialogueFader?.FadeOut();
        HideChoicePanel();

        if (avatarRoot != null)
            avatarRoot.gameObject.SetActive(false);

        ResetSceneGroups();
        _onComplete?.Invoke(saveJournal);
        _state = ReviewState.Inactive;
        Debug.Log("[JournalReview] CompleteReview done — state = Inactive.");
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
