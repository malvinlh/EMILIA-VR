using System;
using System.Collections;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.UI;
using UnityEngine.Serialization;

/// <summary>Determines which post-journal terminal flow to use.</summary>
public enum ReviewMode { BeachBottle, BedroomPaper }

/// <summary>
/// Manages the post-journal review flow that runs after the user presses DONE.
///
/// Flow:
///   1. Screen fades to black; EMILIA is locked to its authored scene position and
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
///   • avatarRoot              — EMILIA root Transform
///   • avatarRoamingController — Optional roaming controller (auto-added if omitted)
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
    [Tooltip("Root transform of the EMILIA avatar. Review mode locks this avatar back to its authored scene position.")]
    [FormerlySerializedAs("companionRoot")]
    public Transform avatarRoot;

    [Header("Avatar Roaming")]
    [Tooltip("Optional roaming controller. If missing, one is auto-added to avatarRoot at runtime.")]
    [FormerlySerializedAs("companionRoamingController")]
    [SerializeField] private AvatarIslandRoamingController avatarRoamingController;
    [Tooltip("Bedroom waypoint patrol controller. When set, prevents AvatarIslandRoamingController from being added.")]
    [SerializeField] private AvatarChatWaypointPatrolController _waypointController;

    [Tooltip("Enable EMILIA roaming as soon as this scene starts.")]
    [FormerlySerializedAs("startCompanionRoamingOnAwake")]
    [SerializeField] private bool startAvatarRoamingOnAwake = true;

    [Header("Dialogue")]
    [Tooltip("VRDialoguePanel root GameObject (contains VRDialoguePanel + VRDialogueFader).")]
    public GameObject dialoguePanelGO;

    [Header("Stand Point")]
    [Tooltip("Player is teleported here (XZ) when the review begins. " +
             "Place as a child of the environment so it moves with the island. " +
             "If null, only the camera yaw snap runs (no XZ teleport).")]
    public Transform standPoint;

    [Header("Avatar Review Position")]
    [Tooltip("EMILIA is teleported to this transform when review begins (while screen is black). " +
             "Assign an empty GameObject at the desired review stand position. " +
             "If null, EMILIA appears at its current scene position (legacy behaviour).")]
    [FormerlySerializedAs("reviewCompanionStandPoint")]
    [SerializeField] private Transform reviewAvatarStandPoint;

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
    [Tooltip("VintageMicrophone prop — disabled during cork/post-journal phase, re-enabled on session reset.")]
    public GameObject vintageMicrophone;
    [Tooltip("BottlePreDuring prop — disabled during cork/post-journal phase, re-enabled on session reset.")]
    public GameObject bottlePreDuringProp;

    [Header("Choice Panel")]
    [Tooltip("Vertical offset for the confirmation panel relative to EMILIA. Set per scene in the Inspector.")]
    [SerializeField] private float choicePanelHeight = 1.2f;

    [Header("Cork")]
    [Tooltip("CorkSnapZone script on the PostJournal bottle neck child trigger collider.")]
    public CorkSnapZone bottleNeckZone;
    [Tooltip("Renderers that must stay disabled when EnableBottleComponents runs " +
             "(e.g. the CorkPlaceholder mesh — a transform-only reference that should never be visible).")]
    [SerializeField] private Renderer[] _placeholderRenderers;

    [Tooltip("Colliders that must stay disabled when EnableBottleComponents runs " +
             "(e.g. the CorkPlaceholder collider — should never participate in physics).")]
    [SerializeField] private Collider[] _placeholderColliders;

    [Header("Terminal Detectors")]
    [Tooltip("Wine-rack proximity detector. Its trigger collider is disabled during the discard " +
             "path and after KEEP completion, then re-enabled at each session start.")]
    [SerializeField] private WineRackProximity _rackDetector;
    [Tooltip("Paper shredder detector (BedroomPaper mode). Armed on release path, disarmed on keep path.")]
    [SerializeField] private PaperShredder _shredderDetector;

    [Header("Mode")]
    [Tooltip("BeachBottle = bottle+cork+ocean/rack. BedroomPaper = paper+shredder/rack (no cork).")]
    [SerializeField] private ReviewMode _mode = ReviewMode.BeachBottle;

    [Header("Audio")]
    [Tooltip("AudioSource that plays the cork-plug SFX. May be null — skipped silently if unassigned.")]
    public AudioSource plugSfxSource;

    [Header("Debug")]
    [Tooltip("When enabled, emits detailed state snapshots with '[JRC][STATE]' prefix for logcat filtering.")]
    [SerializeField] private bool _debugStateLogs = true;

    // ── State ─────────────────────────────────────────────────────────────
    private enum ReviewState
    {
        Inactive,
        ShowingComment,
        ShowingChoice,
        WaitingForCork,
        WaitingForBottle,
        WaitingForRack,
        WaitingForShredder,   // BedroomPaper release path
        Complete
    }

    /// <summary>True while the player should carry the sealed bottle to the wine rack.</summary>
    public bool IsWaitingForRack => _state == ReviewState.WaitingForRack;

    /// <summary>True while the player should feed the paper into the shredder (BedroomPaper release).</summary>
    public bool IsWaitingForShredder => _state == ReviewState.WaitingForShredder;

    private ReviewState  _state      = ReviewState.Inactive;
    private bool         _keepChosen;
    private Action<bool> _onComplete;   // true = save the journal
    private float        _preJournalXROriginY;

    // ── Sentiment (for dialogue) ───────────────────────────────────────────
    private string _journalContent = string.Empty; // set by BeginReview, used by FetchSentimentForDialogue
    private string _aiDialogueText = null;          // null = not ready or API failed (use fallback)
    private bool   _sentimentReady = false;

    private VRDialoguePanel _dialoguePanel;
    private VRDialogueFader _dialogueFader;
    private GameObject _choicePanel;
    private GameObject _screenFadeOverlay;
    private Transform  _xrOriginTransform;

    // PostJournal root's original local transform — captured once at scene start so
    // repeated sessions can always restore the whole hierarchy to authored placement.
    private Transform  _postJournalOriginalParent;
    private Vector3    _postJournalOriginalLocalPos;
    private Quaternion _postJournalOriginalLocalRot;
    private Vector3    _postJournalOriginalLocalScale;
    private bool       _postJournalOriginalStored;

    // Bottle's original local transform — captured once at scene start so it can be
    // restored at the beginning of each new journaling session's cork phase.
    private Transform  _bottleOriginalParent;
    private Vector3    _bottleOriginalLocalPos;
    private Quaternion _bottleOriginalLocalRot;
    private Vector3    _bottleOriginalLocalScale;
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
        {
            postJournalGroup.SetActive(false);

            _postJournalOriginalParent     = postJournalGroup.transform.parent;
            _postJournalOriginalLocalPos   = postJournalGroup.transform.localPosition;
            _postJournalOriginalLocalRot   = postJournalGroup.transform.localRotation;
            _postJournalOriginalLocalScale = postJournalGroup.transform.localScale;
            _postJournalOriginalStored     = true;
        }

        // Store the bottle's original local transform so it can be reset each session.
        if (bottleRoot != null)
        {
            _bottleOriginalParent   = bottleRoot.parent;
            _bottleOriginalLocalPos = bottleRoot.localPosition;
            _bottleOriginalLocalRot = bottleRoot.localRotation;
            _bottleOriginalLocalScale = bottleRoot.localScale;
            _bottleOriginalStored   = true;
        }

        EnsureAvatarRoamingController();
        if (startAvatarRoamingOnAwake)
            ResumeAvatarRoaming();

        LogStateSnapshot("Awake.AfterInit");
    }

    // ================================================================
    // PUBLIC API
    // ================================================================

    /// <summary>
    /// Called by JournalSessionManager.EndSession() to run the review flow.
    /// <paramref name="onComplete"/> receives true if the journal should be saved.
    /// <paramref name="preJournalXROriginY"/> is the XR Origin Y before TeleportToSeatPoint —
    /// used to restore the player to their natural standing height during the review.
    /// <paramref name="journalContent"/> is the written journal text — sent to the /sentiment
    /// API so the AI's <c>reason</c> replaces the hardcoded dialogue comment.
    /// </summary>
    public void BeginReview(Action<bool> onComplete, float preJournalXROriginY, string journalContent = "")
    {
        if (_state != ReviewState.Inactive)
        {
            Debug.LogWarning("[JournalReview] BeginReview called while already in progress.");
            return;
        }
        _onComplete          = onComplete;
        _preJournalXROriginY = preJournalXROriginY;
        _journalContent      = journalContent ?? string.Empty;
        _aiDialogueText      = null;
        _sentimentReady      = false;
        LogStateSnapshot("BeginReview.Start");
        StartCoroutine(ReviewCoroutine());
    }

    /// <summary>
    /// Called when journaling starts so EMILIA is hidden and cannot locomote during writing.
    /// </summary>
    public void EnterJournalingMode()
    {
        EnsureAvatarRoamingController();
        avatarRoamingController?.LockAtAuthoredPose();

        if (avatarRoot != null)
            avatarRoot.gameObject.SetActive(false);

        HideChoicePanel();
        _dialoguePanel?.Hide();
        _dialogueFader?.HideImmediate();
        if (_screenFadeOverlay != null)
            _screenFadeOverlay.SetActive(false);

        LogStateSnapshot("EnterJournalingMode");
    }

    /// <summary>
    /// Called by JournalSessionManager after the journaling session is fully ended.
    /// Releases any review lock/pose hold and returns EMILIA to roaming.
    /// </summary>
    public void OnSessionEnded()
    {
        ResumeAvatarRoaming();
        LogStateSnapshot("OnSessionEnded");
    }

    // ================================================================
    // REVIEW FLOW
    // ================================================================

    private IEnumerator ReviewCoroutine()
    {
        _state = ReviewState.ShowingComment;
        Debug.Log("[JournalReview] ReviewCoroutine started. Hiding WhiteboardUI.");

        // 0. Kick off sentiment analysis immediately — the ~1 s of screen fades below
        //    acts as a natural buffer so the AI response is (usually) ready before the
        //    dialogue panel needs to open. Falls back gracefully if the API fails.
        if (!string.IsNullOrWhiteSpace(_journalContent) && ServiceManager.Instance?.SentimentApi != null)
            StartCoroutine(FetchSentimentForDialogue());
        else
            _sentimentReady = true; // no content or no service — skip wait

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

        // 6. Wait for the sentiment API — should already be done during the fades above.
        //    No timeout: the AI comment must be shown to the user (not a hardcoded fallback),
        //    so we wait unconditionally until the response arrives or the request errors out.
        // Show the VR dialogue panel's typing indicator while waiting for the sentiment API
        // — reuse the same waiting UI used for chat responses.
        _dialoguePanel?.ShowTypingIndicator();
        yield return new WaitUntil(() => _sentimentReady);

        // 7. Show AI comment — real reason from /sentiment, or hardcoded fallback.
        string dialogueText = !string.IsNullOrWhiteSpace(_aiDialogueText)
            ? _aiDialogueText
            : "Hari ini kamu telah mengambil langkah berarti dengan menuangkan pikiranmu ke dalam kata-kata. " +
              "Merefleksikan apa yang kamu tulis dapat membantumu memahami perasaanmu lebih baik " +
              "dan menemukan kejernihan di saat-saat yang penuh ketidakpastian.\n\n" +
              "Kata-katamu berarti, dan begitu juga dirimu. Aku bangga kamu sudah hadir.";

            // During the AI comment panel, EMILIA should talk in a loop.
            AvatarTalkLoop();
        ShowDialogue(dialogueText);

        // 8. Wait for the typewriter to finish the last page.
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

        // After the AI comment finishes, stop talking and return to standby idle pose.
        AvatarLock();

        // 9. Present the keep-or-release choice.
        _state = ReviewState.ShowingChoice;
        ShowChoicePanel();
    }

    /// <summary>
    /// Calls /sentiment with the journal content and stores the AI's <c>reason</c> in
    /// <see cref="_aiDialogueText"/>. Sets <see cref="_sentimentReady"/> when done (success
    /// or failure) so <see cref="ReviewCoroutine"/> can proceed.
    /// </summary>
    private IEnumerator FetchSentimentForDialogue()
    {
        Debug.Log("[JournalReview] Fetching AI sentiment for dialogue...");
        yield return ServiceManager.Instance.SentimentApi.AnalyzeJournal(
            _journalContent,
            onSuccess: result =>
            {
                _aiDialogueText = result.reason;
                _sentimentReady = true;
                Debug.Log($"[JournalReview] Sentiment received — tone: {result.tone}");
            },
            onError: err =>
            {
                Debug.LogWarning($"[JournalReview] Sentiment API error — fallback dialogue will be used. Error: {err}");
                _aiDialogueText = null;
                _sentimentReady = true;
            }
        );
    }

    // ── Avatar ───────────────────────────────────────────────────────────

    // Dispatch to whichever controller is on the avatar (Beach = Island, Bedroom = Waypoint).
    private void AvatarLock()         { avatarRoamingController?.LockAtAuthoredPose();  _waypointController?.LockAtAuthoredPose(); }
    private void AvatarTalkLoop()     { avatarRoamingController?.PlayTalkLoop();         _waypointController?.PlayTalkLoop(); }
    private void AvatarCheerOnce()    { avatarRoamingController?.PlayCheeringOneShot();  _waypointController?.PlayCheeringOneShot(); }
    private void AvatarResumePatrol() { avatarRoamingController?.ResumeRoaming();        _waypointController?.ResumeRoaming(); }

    private void EnableAvatar()
    {
        if (avatarRoot == null) return;

        // 1. Move first
        if (reviewAvatarStandPoint != null)
        {
            avatarRoot.position = reviewAvatarStandPoint.position;
            avatarRoot.rotation = reviewAvatarStandPoint.rotation;
        }

        avatarRoot.gameObject.SetActive(true);

        // 2. Ensure controller exists BEFORE capture
        EnsureAvatarRoamingController();

        // 3. Now safely capture the new authored pose
        if (reviewAvatarStandPoint != null)
            avatarRoamingController?.CaptureAuthoredPose();

        // 4. Lock to it
        AvatarLock();
    }

    private void EnsureAvatarRoamingController()
    {
        if (avatarRoot == null) return;

        // Bedroom: prefer the waypoint controller already on the avatar hierarchy.
        if (_waypointController == null)
            _waypointController = avatarRoot.GetComponentInChildren<AvatarChatWaypointPatrolController>(true);
        if (_waypointController == null)
            _waypointController = FindFirstObjectByType<AvatarChatWaypointPatrolController>();

        if (_waypointController != null)
        {
            if (avatarRoamingController != null)
                avatarRoamingController.enabled = false;

            var strayRoamer = avatarRoot.GetComponent<AvatarIslandRoamingController>();
            if (strayRoamer != null)
                strayRoamer.enabled = false;

            return;
        }

        if (_mode == ReviewMode.BedroomPaper)
        {
            Debug.LogWarning("[JournalReview] Bedroom mode could not find AvatarChatWaypointPatrolController under avatarRoot or in the scene.");
            return;
        }

        // Beach fallback: free-roam on NavMesh.
        if (avatarRoamingController == null)
            avatarRoamingController = avatarRoot.GetComponent<AvatarIslandRoamingController>();
        if (avatarRoamingController == null)
            avatarRoamingController = avatarRoot.gameObject.AddComponent<AvatarIslandRoamingController>();
    }

    private void ResumeAvatarRoaming()
    {
        if (avatarRoot == null) return;

        avatarRoot.gameObject.SetActive(true);
        EnsureAvatarRoamingController();
        AvatarResumePatrol();
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
                + Vector3.up * choicePanelHeight;          // comfortable chest / reading height

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

        // Match VRDialoguePanel dimensions so the choice panel looks the same size.
        Vector2 panelSize  = new Vector2(640f, 300f);
        float   panelScale = 0.001f;
        if (dialoguePanelGO != null)
        {
            var drt = dialoguePanelGO.GetComponent<RectTransform>();
            if (drt != null) { panelSize = drt.sizeDelta; panelScale = dialoguePanelGO.transform.localScale.x; }
        }

        root.GetComponent<RectTransform>().sizeDelta = panelSize;
        root.AddComponent<CanvasScaler>();
        root.AddComponent<TrackedDeviceGraphicRaycaster>(); // XRI-compatible raycaster
        root.transform.localScale = Vector3.one * panelScale;

        // ── Background panel ──────────────────────────────────────────
        var bg    = new GameObject("Background");
        bg.transform.SetParent(root.transform, false);
        var bgImg = bg.AddComponent<Image>();
        bgImg.color = s_PanelBg;
        bg.GetComponent<RectTransform>().sizeDelta = panelSize;

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
        qTmp.text      = "Apakah kamu ingin menyimpan journal ini?";
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
        var keepBtn = MakeButton("Simpan", s_BtnKeep, Color.white,
            row.transform, new Vector2(0f, 0f), new Vector2(0.44f, 1f));
        keepBtn.onClick.AddListener(OnKeepChosen);

        // "Let it go" — discard → ocean path
        var releaseBtn = MakeButton("Buang", s_BtnRelease, Color.white,
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

    /// <summary>User wants to keep (save) the journal.</summary>
    private void OnKeepChosen()
    {
        if (_state != ReviewState.ShowingChoice) return;
        _keepChosen = true;
        HideChoicePanel();
        if (_mode == ReviewMode.BedroomPaper) BeginPaperPhase();
        else BeginCorkPhase();
    }

    /// <summary>User wants to release (discard) the journal.</summary>
    private void OnReleaseChosen()
    {
        if (_state != ReviewState.ShowingChoice) return;
        _keepChosen = false;
        HideChoicePanel();
        if (_mode == ReviewMode.BedroomPaper) BeginPaperPhase();
        else BeginCorkPhase();
    }

    // ================================================================
    // CORK PHASE
    // ================================================================

    private void BeginCorkPhase()
    {
        Debug.Log($"[JournalReview] BeginCorkPhase — keepChosen={_keepChosen}, bottleRoot={bottleRoot?.name ?? "NULL"}");
        LogStateSnapshot("BeginCorkPhase.BeforeRestore");

        // Hard-restore the PostJournal root first in case any runtime script
        // moved/disabled it between sessions.
        RestorePostJournalRoot();
        if (postJournalGroup != null)
        {
            postJournalGroup.SetActive(true);
            ForceActivateHierarchy(postJournalGroup.transform);
        }

        // Reset the cork back to its original state before revealing the group.
        if (bottleNeckZone != null) bottleNeckZone.ResetForNewSession();

        // Ensure the bottle's original transform is captured before we try to restore it.
        // This is a lazy fallback for the case where Awake() threw before reaching the
        // capture block (e.g. VRDialogueFader.HideImmediate null-ref on Awake execution-order race).
        // BeginCorkPhase for Session 1 is always called while the bottle is still at its
        // authored position, so the lazy capture here is always correct.
        EnsureBottleOriginalStored();

        // Restore bottle transform first (kinematic so gravity doesn't fire before position is set).
        if (bottleRoot != null && _bottleOriginalStored)
        {
            if (!bottleRoot.gameObject.activeSelf)
                bottleRoot.gameObject.SetActive(true);

            bottleRoot.SetParent(_bottleOriginalParent, worldPositionStays: false);
            bottleRoot.localPosition = _bottleOriginalLocalPos;
            bottleRoot.localRotation = _bottleOriginalLocalRot;
            bottleRoot.localScale    = _bottleOriginalLocalScale;
            var rb = bottleRoot.GetComponent<Rigidbody>();
            if (rb != null) { rb.isKinematic = true; rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
        }

        // Hide the whiteboard UI groups.
        if (preNDuringJournalGroup != null) preNDuringJournalGroup.SetActive(false);
        if (whiteboardUIGroup      != null) whiteboardUIGroup.SetActive(false);
        if (vintageMicrophone      != null) vintageMicrophone.SetActive(false);
        if (bottlePreDuringProp    != null) bottlePreDuringProp.SetActive(false);

        // Re-enable all bottle (and sealed cork) components so everything is visible/grabbable.
        EnableBottleComponents();
        LogStateSnapshot("BeginCorkPhase.AfterEnableComponents");

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
        ShowDialogue("Sebelum pergi, segel botolmu dengan memasukkan gabus ke dalam leher botol.");

        if (bottleNeckZone != null)
            bottleNeckZone.OnCorkSealed += OnCorkSealed;
        else
            Debug.LogWarning("[JournalReview] bottleNeckZone not assigned — cork step skipped.");

        _state = ReviewState.WaitingForCork;
        LogStateSnapshot("BeginCorkPhase.WaitingForCork");
    }

    private void OnCorkSealed()
    {
        LogStateSnapshot("OnCorkSealed.Entry");

        if (bottleNeckZone != null)
            bottleNeckZone.OnCorkSealed -= OnCorkSealed;

        if (plugSfxSource != null)
            plugSfxSource.Play();

        if (_keepChosen)
        {
            _state = ReviewState.WaitingForRack;
            Debug.Log($"[JournalReview] Cork sealed — state → WaitingForRack. bottleRoot={bottleRoot?.name ?? "NULL"}");

            ShowDialogue(
                "Bawa botol itu ke rak botol di dekatmu. Cukup dekatkan dan botol akan beristirahat di sana.");
        }
        else
        {
            _state = ReviewState.WaitingForBottle;
            Debug.Log($"[JournalReview] Cork sealed — state → WaitingForBottle. bottleRoot={bottleRoot?.name ?? "NULL"}");

            // Suppress auto-reset so the bottle can reach the sea without being
            // blinked back to its origin mid-flight, while keeping held collision
            // ignores active if the player is still holding it.
            if (bottleRoot != null)
            {
                var autoReset = bottleRoot.GetComponent<ItemAutoReset>();
                if (autoReset != null)
                    autoReset.SetResetSuppressed(true);
            }

            // Creative terminal guard: proactively kill the rack trigger so it cannot
            // accidentally complete the KEEP path while the player carries the bottle to
            // the sea. The sea collider stays active — it is the intended terminal here.
            if (_rackDetector != null)
                _rackDetector.GetComponent<Collider>().enabled = false;

            ShowDialogue(
                "Saat kamu siap, berjalanlah ke tepi dan lempar botol itu ke laut. " +
                "Kamu sudah melakukan bagian yang paling sulit.");
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
        LogStateSnapshot("HandleBottleInSea.Entry");
        if (_state != ReviewState.WaitingForBottle)
        {
            Debug.LogWarning($"[JournalReview] HandleBottleInSea ignored — wrong state ({_state}).");
            return;
        }
        if (bottleRoot != null)
        {
            Debug.Log($"[JournalReview] Disposing bottle (sea) — name={bottleRoot.name}");
            DisableBottleComponents();
            LogStateSnapshot("HandleBottleInSea.AfterDisableBottleComponents");
        }
        Debug.Log("[JournalReview] Bottle disposed (sea). Starting BottleDisposedCoroutine.");
        StartCoroutine(BottleDisposedCoroutine(
            "Melepaskan juga butuh keberanian.\nLaut akan membawanya, begitu juga kamu.",
            saveJournal: false));
    }

    // ================================================================
    // PAPER PHASE (BedroomPaper mode — no cork, branches immediately on choice)
    // ================================================================

    private void BeginPaperPhase()
    {
        Debug.Log($"[JournalReview] BeginPaperPhase — keepChosen={_keepChosen}");
        LogStateSnapshot("BeginPaperPhase.BeforeRestore");

        RestorePostJournalRoot();
        if (postJournalGroup != null)
        {
            postJournalGroup.SetActive(true);
            ForceActivateHierarchy(postJournalGroup.transform);
        }

        EnsureBottleOriginalStored();

        if (bottleRoot != null && _bottleOriginalStored)
        {
            if (!bottleRoot.gameObject.activeSelf) bottleRoot.gameObject.SetActive(true);
            bottleRoot.SetParent(_bottleOriginalParent, worldPositionStays: false);
            bottleRoot.localPosition = _bottleOriginalLocalPos;
            bottleRoot.localRotation = _bottleOriginalLocalRot;
            bottleRoot.localScale    = _bottleOriginalLocalScale;
            var rb = bottleRoot.GetComponent<Rigidbody>();
            if (rb != null) { rb.isKinematic = true; rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
        }

        if (preNDuringJournalGroup != null) preNDuringJournalGroup.SetActive(false);
        if (whiteboardUIGroup      != null) whiteboardUIGroup.SetActive(false);
        if (vintageMicrophone      != null) vintageMicrophone.SetActive(false);
        if (bottlePreDuringProp    != null) bottlePreDuringProp.SetActive(false);

        EnableBottleComponents();
        LogStateSnapshot("BeginPaperPhase.AfterEnableComponents");

        if (bottleRoot != null)
        {
            var rb = bottleRoot.GetComponent<Rigidbody>();
            if (rb != null) { rb.isKinematic = false; rb.useGravity = true; }
            bottleRoot.GetComponent<ItemAutoReset>()?.RefreshOrigin();
        }

        JournalSessionManager.Instance?.AllowLocomotion();

        if (_keepChosen)
        {
            _state = ReviewState.WaitingForRack;
            _shredderDetector?.Disarm();
            ShowDialogue("Bawa kertas jurnalmu ke rak buku di dekatmu untuk disimpan dengan aman.");
        }
        else
        {
            _state = ReviewState.WaitingForShredder;
            if (_rackDetector != null) _rackDetector.GetComponent<Collider>().enabled = false;
            _shredderDetector?.Arm();
            ShowDialogue("Saat kamu siap, masukkan kertas itu ke dalam penghancur untuk melepaskannya.");
        }

        LogStateSnapshot("BeginPaperPhase.Done");
    }

    // ================================================================
    // SHREDDER DETECTION (called by PaperShredder — BedroomPaper mode)
    // ================================================================

    /// <summary>Called by PaperShredder after the pull-in animation completes.</summary>
    public void HandlePaperShredded()
    {
        Debug.Log($"[JournalReview] HandlePaperShredded — state={_state}");
        if (_state != ReviewState.WaitingForShredder)
        {
            Debug.LogWarning($"[JournalReview] HandlePaperShredded ignored — wrong state ({_state}).");
            return;
        }
        if (bottleRoot != null) DisableBottleComponents();
        _state = ReviewState.Complete;
        StartCoroutine(BottleDisposedCoroutine(
            "Melepaskan juga butuh keberanian.\nPenghancur akan membawanya, begitu juga kamu.",
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
        LogStateSnapshot("HandleBottleRacked.Entry");
        if (_state != ReviewState.WaitingForRack)
        {
            Debug.LogWarning($"[JournalReview] HandleBottleRacked ignored — wrong state ({_state}).");
            return;
        }
        if (bottleRoot != null)
        {
            Debug.Log($"[JournalReview] Disposing bottle (rack) — name={bottleRoot.name}");
            DisableBottleComponents();
            LogStateSnapshot("HandleBottleRacked.AfterDisableBottleComponents");
        }

        // KEEP terminal cleanup: disable the rack collider so it cannot re-fire between
        // now and when the session fully resets. The sea collider is untouched —
        // state = Complete (set at the top of BottleDisposedCoroutine) is its guard.
        if (_rackDetector != null)
            _rackDetector.GetComponent<Collider>().enabled = false;

        Debug.Log("[JournalReview] Bottle disposed (rack). Starting BottleDisposedCoroutine.");
        StartCoroutine(BottleDisposedCoroutine(
            "Kata-katamu kini aman.\nIstirahatlah, kamu telah hadir untuk dirimu sendiri hari ini.",
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

        // During ending dialogue, EMILIA plays a one-shot cheering animation.
        AvatarCheerOnce();

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

        // Fade out dialogue. EMILIA holds the final cheering pose until locomotion is re-enabled.
        _dialogueFader?.FadeOut();

        // Wait until the cheering animation has played fully and EMILIA is holding the
        // final pose before we hand control back to EndSessionCoroutine (which calls
        // ResumeAvatarRoaming). Cap the wait at 5 s to guard against edge cases.
        float cheerWaitStart = Time.time;
        yield return new WaitUntil(() =>
            ((avatarRoamingController == null || avatarRoamingController.IsCheeringPoseHeld) &&
             (_waypointController     == null || _waypointController.IsCheeringPoseHeld))
            || Time.time - cheerWaitStart > 5f);

        // Keep holding the cheer pose until the dialogue panel has fully faded out.
        // This prevents the animation from snapping away while text is still visible.
        yield return new WaitUntil(() => _dialogueFader == null || _dialogueFader.IsHidden);

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
        LogStateSnapshot("ResetSceneGroups.Before");

        RestorePostJournalRoot();

        // PostJournal group is intentionally NOT deactivated here. Toggling a group's
        // SetActive between sessions causes Unity to silently refuse to re-show children
        // that were explicitly set inactive at any prior point in the hierarchy chain.
        // Instead we keep the group active and manage individual item visibility via
        // renderer/collider/interactable components (see DisableBottleComponents /
        // EnableBottleComponents). Only the whiteboard UI groups change here.
        Debug.Log("[JournalReview] ResetSceneGroups — PreNDuringJournal ON, WhiteboardUI ON.");
        if (preNDuringJournalGroup != null) preNDuringJournalGroup.SetActive(true);
        if (whiteboardUIGroup      != null) whiteboardUIGroup.SetActive(true);
        if (vintageMicrophone      != null) vintageMicrophone.SetActive(true);
        if (bottlePreDuringProp    != null) bottlePreDuringProp.SetActive(true);

        // Reposition the bottle to its authored origin so it is ready for the next session.
        // Components remain disabled (DisableBottleComponents was called at disposal);
        // EnableBottleComponents in BeginCorkPhase makes everything visible again.
        if (bottleRoot != null && _bottleOriginalStored)
        {
            if (!bottleRoot.gameObject.activeSelf)
                bottleRoot.gameObject.SetActive(true);

            bottleRoot.SetParent(_bottleOriginalParent, worldPositionStays: false);
            bottleRoot.localPosition = _bottleOriginalLocalPos;
            bottleRoot.localRotation = _bottleOriginalLocalRot;
            bottleRoot.localScale    = _bottleOriginalLocalScale;
            var rb = bottleRoot.GetComponent<Rigidbody>();
            if (rb != null) { rb.isKinematic = true; rb.linearVelocity = Vector3.zero; rb.angularVelocity = Vector3.zero; }
            Debug.Log($"[JournalReview] ResetSceneGroups — bottleRoot '{bottleRoot.name}' repositioned for next session.");
        }

        LogStateSnapshot("ResetSceneGroups.After");
    }

    /// <summary>
    /// Restores the PostJournal root to its authored hierarchy and local transform.
    /// </summary>
    private void RestorePostJournalRoot()
    {
        if (postJournalGroup == null || !_postJournalOriginalStored) return;

        var tf = postJournalGroup.transform;
        tf.SetParent(_postJournalOriginalParent, worldPositionStays: false);
        tf.localPosition = _postJournalOriginalLocalPos;
        tf.localRotation = _postJournalOriginalLocalRot;
        tf.localScale    = _postJournalOriginalLocalScale;
    }

    /// <summary>
    /// Ensures all descendants are active. This recovers from accidental SetActive(false)
    /// calls on BottlePost/CorkPost that can persist across sessions.
    /// </summary>
    private static void ForceActivateHierarchy(Transform root)
    {
        if (root == null) return;
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
            if (!t.gameObject.activeSelf)
                t.gameObject.SetActive(true);
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
        // Skip any renderers listed in _placeholderRenderers (e.g. CorkPlaceholder)
        // — those are transform-only references and must never be made visible.
        if (postJournalGroup != null)
        {
            foreach (var r in postJournalGroup.GetComponentsInChildren<Renderer>(true))
            {
                if (IsPlaceholderRenderer(r))
                    continue;
                r.enabled = true;
            }
            foreach (var c in postJournalGroup.GetComponentsInChildren<Collider>(true))
            {
                if (IsPlaceholderCollider(c)) continue;
                c.enabled = true;
            }
        }

        if (bottleRoot == null) return;

        // Fail-safe: if all non-placeholder bottle renderers are still disabled for any
        // reason, force-enable them so the player can continue the session.
        bool anyBottleRendererEnabled = false;
        foreach (var r in bottleRoot.GetComponentsInChildren<Renderer>(true))
        {
            if (r == null || IsPlaceholderRenderer(r)) continue;
            if (r.enabled) { anyBottleRendererEnabled = true; break; }
        }
        if (!anyBottleRendererEnabled)
        {
            foreach (var r in bottleRoot.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || IsPlaceholderRenderer(r)) continue;
                r.enabled = true;
            }
        }

        var grab = bottleRoot.GetComponent<XRGrabInteractable>();
        if (grab != null) grab.enabled = true;

        // Re-enable auto-reset for the new session (may have been suppressed by a
        // previous discard path where WaitingForBottle disabled it).
        var autoReset = bottleRoot.GetComponent<ItemAutoReset>();
        if (autoReset != null)
        {
            autoReset.enabled = true;
            autoReset.SetResetSuppressed(false);
        }

        // Restore the rack proximity trigger for the next session — it was disabled
        // either at KEEP completion or at DISCARD path entry.
        if (_rackDetector != null)
            _rackDetector.GetComponent<Collider>().enabled = true;
    }

    /// <summary>
    /// Captures the bottle's authored local transform on first call.
    /// No-op after the first successful capture — safe to call every session.
    /// Use this as a lazy fallback so Awake() crashes (e.g. script-execution-order
    /// races) don't permanently break bottle repositioning.
    /// </summary>
    private void EnsureBottleOriginalStored()
    {
        if (_bottleOriginalStored || bottleRoot == null) return;
        _bottleOriginalParent     = bottleRoot.parent;
        _bottleOriginalLocalPos   = bottleRoot.localPosition;
        _bottleOriginalLocalRot   = bottleRoot.localRotation;
        _bottleOriginalLocalScale = bottleRoot.localScale;
        _bottleOriginalStored     = true;
        Debug.Log($"[JournalReview] Lazy-captured bottleRoot '{bottleRoot.name}' original transform (worldPos={bottleRoot.position}).");
    }

    private bool IsPlaceholderRenderer(Renderer renderer)
    {
        return renderer != null
               && _placeholderRenderers != null
               && System.Array.IndexOf(_placeholderRenderers, renderer) >= 0;
    }

    private bool IsPlaceholderCollider(Collider collider)
    {
        return collider != null
               && _placeholderColliders != null
               && System.Array.IndexOf(_placeholderColliders, collider) >= 0;
    }

    private void LogStateSnapshot(string phase)
    {
        if (!_debugStateLogs) return;

        Transform corkTf = bottleNeckZone != null ? bottleNeckZone.DebugCorkTransform : null;

        int totalRenderers = 0;
        int enabledRenderers = 0;
        int totalColliders = 0;
        int enabledColliders = 0;

        if (postJournalGroup != null)
        {
            foreach (var r in postJournalGroup.GetComponentsInChildren<Renderer>(true))
            {
                totalRenderers++;
                if (r != null && r.enabled) enabledRenderers++;
            }

            foreach (var c in postJournalGroup.GetComponentsInChildren<Collider>(true))
            {
                totalColliders++;
                if (c != null && c.enabled) enabledColliders++;
            }
        }

        var bottleGrab = bottleRoot != null ? bottleRoot.GetComponent<XRGrabInteractable>() : null;
        var bottleRb = bottleRoot != null ? bottleRoot.GetComponent<Rigidbody>() : null;
        var bottleAutoReset = bottleRoot != null ? bottleRoot.GetComponent<ItemAutoReset>() : null;
        var rackCollider = _rackDetector != null ? _rackDetector.GetComponent<Collider>() : null;

        Debug.Log(
            $"[JRC][STATE] {phase} | " +
            $"state={_state}, keepChosen={_keepChosen} | " +
            $"post={DescribeGameObject(postJournalGroup)} | " +
            $"bottle={DescribeTransform(bottleRoot)} | " +
            $"cork={DescribeTransform(corkTf)} | " +
            $"renderers={enabledRenderers}/{totalRenderers} | " +
            $"colliders={enabledColliders}/{totalColliders} | " +
            $"bottleGrab={(bottleGrab != null ? bottleGrab.enabled.ToString() : "NULL")} | " +
            $"bottleAutoReset={(bottleAutoReset != null ? bottleAutoReset.enabled.ToString() : "NULL")} | " +
            $"bottleRb={(bottleRb != null ? $"kin={bottleRb.isKinematic},grav={bottleRb.useGravity},detect={bottleRb.detectCollisions}" : "NULL")} | " +
            $"rackCollider={(rackCollider != null ? rackCollider.enabled.ToString() : "NULL")}");
    }

    private static string DescribeGameObject(GameObject go)
    {
        if (go == null) return "NULL";
        return $"{go.name}(activeSelf={go.activeSelf},activeInHierarchy={go.activeInHierarchy})";
    }

    private static string DescribeTransform(Transform tf)
    {
        if (tf == null) return "NULL";
        string parentName = tf.parent != null ? tf.parent.name : "NULL";
        return $"{tf.name}(activeSelf={tf.gameObject.activeSelf},activeInHierarchy={tf.gameObject.activeInHierarchy},parent={parentName},localPos={tf.localPosition},localScale={tf.localScale},worldPos={tf.position})";
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
