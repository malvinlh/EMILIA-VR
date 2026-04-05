using System;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;
using UnityEngine.XR.Interaction.Toolkit.UI;
using EMILIA.Data;

/// <summary>
/// Attach to the Wine Rack GameObject (or a dedicated child interactable zone).
///
/// Controller trigger while OUTSIDE an active journaling session (SessionState.Idle)
/// toggles the JournalListCanvas. Inside the canvas the user navigates via two panels:
///
///   HomeCanvas   — scrollable list of journals (one JournalEntry prefab per entry).
///   JournalCanvas — read-only detail view of a selected journal.
///
/// Clicking the BackButton on a JournalEntry opens JournalCanvas and hides HomeCanvas.
/// PreviousButton inside JournalCanvas returns to HomeCanvas.
///
/// Inspector setup:
///   journalListCanvas  — top-level World Space canvas (inactive at start).
///   homeCanvas         — child panel that hosts the journal list.
///   journalCanvas      — child panel that shows a selected journal's detail.
///   detailTitleField   — TMP_InputField for the journal title (forced read-only at runtime).
///   detailContentText  — TextMeshProUGUI that displays the journal content.
///   previousButton     — Button in JournalCanvas that navigates back to the list.
///   listContentParent  — Content Transform inside HomeCanvas's ScrollRect.
///   journalEntryPrefab — Prefab with TitleText, ContentText, TimestampBG/TimestampText,
///                        and a BackButton that triggers the detail view.
///   bgNoJournals       — Shown when there are no saved journals.
///   closeButton        — Optional X button on JournalListCanvas to dismiss everything.
/// </summary>
[RequireComponent(typeof(XRSimpleInteractable))]
public class WineRackJournalListController : MonoBehaviour
{
    [Header("Journal List Canvas")]
    [Tooltip("Top-level World Space canvas. Starts inactive; toggled by controller trigger.")]
    public GameObject journalListCanvas;

    [Header("Home (List) Canvas")]
    [Tooltip("Child panel that shows the scrollable journal list.")]
    public GameObject homeCanvas;

    [Header("Detail (Journal) Canvas")]
    [Tooltip("Child panel that shows a single journal read-only.")]
    public GameObject journalCanvas;

    [Tooltip("TMP_InputField inside JournalCanvas for the title (TitleInputField).")]
    public TMP_InputField detailTitleField;

    [Tooltip("TMP_InputField inside JournalCanvas/MainCanvas for the content (ContentInputField (TMP)). "
           + "Both fields are forced non-interactable at runtime so the Quest keyboard never pops up.")]
    public TMP_InputField detailContentField;

    [Tooltip("PreviousButton inside JournalCanvas — returns to the list.")]
    public Button previousButton;

    [Header("List Content")]
    [Tooltip("Content Transform inside HomeCanvas's ScrollRect → Viewport.")]
    public Transform listContentParent;

    [Tooltip("JournalEntry prefab. Must have TitleText, ContentText, TimestampBG/TimestampText "
           + "and a BackButton that opens the detail view.")]
    public GameObject journalEntryPrefab;

    [Tooltip("Shown when the user has no saved journals.")]
    public GameObject bgNoJournals;

    [Header("Close Button (Optional)")]
    [Tooltip("Button that dismisses the entire JournalListCanvas.")]
    public Button closeButton;

    [Header("Detail Backdrop (Runtime)")]
    [Tooltip("When enabled, creates/updates an opaque backdrop behind JournalCanvas content to stop world objects showing through.")]
    public bool enforceOpaqueDetailBackdrop = true;

    [Tooltip("Fallback color for runtime detail backdrop. Alpha is clamped to at least 0.9.")]
    public Color detailBackdropColor = new Color(0.98f, 0.94f, 0.96f, 1f);

    [Tooltip("Optional sprite for runtime detail backdrop. Leave empty for flat color fill.")]
    public Sprite detailBackdropSprite;

    // ── Private State ────────────────────────────────────────────────
    private const string AutoDetailBackdropName = "__AutoDetailBackdrop";
    private XRSimpleInteractable _interactable;
    private bool _isVisible;

    // ================================================================
    // LIFECYCLE
    // ================================================================

    private void Awake()
    {
        _interactable = GetComponent<XRSimpleInteractable>();
    }

    private void Start()
    {
        if (journalListCanvas != null)
        {
            journalListCanvas.SetActive(false);
            EnsureTrackedRaycaster(journalListCanvas);
        }

        // Ensure each child canvas can be hit by the XRI controller ray.
        if (homeCanvas != null && homeCanvas.GetComponent<Canvas>() != null)
            EnsureTrackedRaycaster(homeCanvas);

        if (journalCanvas != null)
        {
            journalCanvas.SetActive(false);
            if (journalCanvas.GetComponent<Canvas>() != null)
                EnsureTrackedRaycaster(journalCanvas);
        }

        EnsureOpaqueDetailBackdrop();

        // Force both display fields permanently non-interactable so the Quest
        // keyboard never opens when the user accidentally touches them.
        if (detailTitleField != null)   detailTitleField.interactable  = false;
        if (detailContentField != null) detailContentField.interactable = false;

        if (previousButton != null)
            previousButton.onClick.AddListener(HideDetail);

        if (closeButton != null)
            closeButton.onClick.AddListener(HideCanvas);
    }

    /// <summary>
    /// Replaces a standard GraphicRaycaster with TrackedDeviceGraphicRaycaster so
    /// XRI controller rays can reach buttons and scroll rects on this canvas.
    /// </summary>
    private static void EnsureTrackedRaycaster(GameObject canvasRoot)
    {
        if (canvasRoot.GetComponent<TrackedDeviceGraphicRaycaster>() != null) return;

        var legacy = canvasRoot.GetComponent<GraphicRaycaster>();
        if (legacy != null) Object.Destroy(legacy);

        canvasRoot.AddComponent<TrackedDeviceGraphicRaycaster>();
        Debug.Log("[WineRackJournalList] Replaced GraphicRaycaster with TrackedDeviceGraphicRaycaster.");
    }

    private void Update()
    {
        // Auto-close the moment a journaling session begins.
        if (!_isVisible) return;
        var mgr = JournalSessionManager.Instance;
        if (mgr != null && mgr.CurrentState != JournalSessionManager.SessionState.Idle)
            HideCanvas();
    }

    private void OnEnable()
    {
        if (_interactable != null)
            _interactable.selectEntered.AddListener(OnControllerSelect);
    }

    private void OnDisable()
    {
        if (_interactable != null)
            _interactable.selectEntered.RemoveListener(OnControllerSelect);

        HideCanvas();
    }

    // ================================================================
    // CONTROLLER INPUT
    // ================================================================

    private void OnControllerSelect(SelectEnterEventArgs args)
    {
        var mgr = JournalSessionManager.Instance;
        if (mgr != null && mgr.CurrentState != JournalSessionManager.SessionState.Idle)
        {
            Debug.Log("[WineRackJournalList] Blocked — session is not Idle " +
                      $"(current state: {mgr.CurrentState}).");
            return;
        }

        if (_isVisible)
            HideCanvas();
        else
            ShowCanvas();
    }

    // ================================================================
    // CANVAS VISIBILITY
    // ================================================================

    private void ShowCanvas()
    {
        _isVisible = true;
        if (journalListCanvas != null)
            journalListCanvas.SetActive(true);

        // Always open on the list view.
        if (homeCanvas != null)    homeCanvas.SetActive(true);
        if (journalCanvas != null) journalCanvas.SetActive(false);

        FetchAndPopulate();
    }

    public void HideCanvas()
    {
        _isVisible = false;
        if (journalListCanvas != null)
            journalListCanvas.SetActive(false);
    }

    // ================================================================
    // DETAIL VIEW
    // ================================================================

    private void ShowDetail(Journal journal)
    {
        if (detailTitleField != null)
            detailTitleField.text = journal.Title;

        if (detailContentField != null)
            detailContentField.text = journal.Content;

        if (homeCanvas != null)    homeCanvas.SetActive(false);
        if (journalCanvas != null)
        {
            EnsureOpaqueDetailBackdrop();
            journalCanvas.SetActive(true);
        }
    }

    private void HideDetail()
    {
        if (journalCanvas != null) journalCanvas.SetActive(false);
        if (homeCanvas != null)    homeCanvas.SetActive(true);
    }

    // ================================================================
    // DATA FETCH
    // ================================================================

    private void FetchAndPopulate()
    {
        if (ServiceManager.Instance == null)
        {
            Debug.LogError("[WineRackJournalList] ServiceManager.Instance is null. " +
                           "Ensure ServiceManager is present in the scene.");
            return;
        }

        var userId = PlayerPrefs.GetString("Nickname", string.Empty);
        if (string.IsNullOrEmpty(userId))
        {
            Debug.LogError("[WineRackJournalList] No user ID found in PlayerPrefs key 'Nickname'.");
            return;
        }

        StartCoroutine(
            ServiceManager.Instance.JournalService.FetchUserJournals(
                userId,
                OnJournalsReceived,
                err => Debug.LogError($"[WineRackJournalList] Fetch failed: {err}")
            )
        );
    }

    // ================================================================
    // LIST POPULATION
    // ================================================================

    private void OnJournalsReceived(Journal[] journals)
    {
        // Clear previous entries.
        if (listContentParent != null)
        {
            foreach (Transform child in listContentParent)
                Destroy(child.gameObject);
        }

        bool empty = journals == null || journals.Length == 0;
        if (bgNoJournals != null)
            bgNoJournals.SetActive(empty);

        if (empty) return;

        foreach (var j in journals)
        {
            var go = Instantiate(journalEntryPrefab, listContentParent);
            go.name = $"Journal_{j.Id}";

            SetTMP(go, "TitleText",   TruncateTitleForList(j.Title));
            SetTMP(go, "ContentText", j.Content);
            SetTMP(go, "TimestampBG/TimestampText",
                j.CreatedAt.ToString("dd/MM/yyyy hh:mm tt", CultureInfo.InvariantCulture));

            // Wire BackButton on the entry to open the detail view.
            var viewBtn = go.GetComponentInChildren<Button>(includeInactive: true);
            if (viewBtn != null)
            {
                var captured = j;
                viewBtn.onClick.AddListener(() => ShowDetail(captured));
            }
            else
            {
                Debug.LogWarning($"[WineRackJournalList] No Button found on entry '{j.Title}' — " +
                                 "check that JournalEntry prefab has a BackButton.");
            }
        }
    }

    // ================================================================
    // HELPERS
    // ================================================================

    private void EnsureOpaqueDetailBackdrop()
    {
        if (!enforceOpaqueDetailBackdrop || journalCanvas == null)
            return;

        var journalRect = journalCanvas.GetComponent<RectTransform>();
        if (journalRect == null)
            return;

        Transform existing = journalRect.Find(AutoDetailBackdropName);
        Image backdropImage;

        if (existing == null)
        {
            var go = new GameObject(AutoDetailBackdropName, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            var backdropRect = go.GetComponent<RectTransform>();
            backdropRect.SetParent(journalRect, false);
            backdropRect.anchorMin = Vector2.zero;
            backdropRect.anchorMax = Vector2.one;
            backdropRect.offsetMin = Vector2.zero;
            backdropRect.offsetMax = Vector2.zero;
            backdropRect.SetAsFirstSibling();

            backdropImage = go.GetComponent<Image>();
        }
        else
        {
            var existingRect = existing as RectTransform;
            if (existingRect != null)
            {
                existingRect.anchorMin = Vector2.zero;
                existingRect.anchorMax = Vector2.one;
                existingRect.offsetMin = Vector2.zero;
                existingRect.offsetMax = Vector2.zero;
                existingRect.SetAsFirstSibling();
            }

            backdropImage = existing.GetComponent<Image>();
            if (backdropImage == null)
                backdropImage = existing.gameObject.AddComponent<Image>();
        }

        Color color = detailBackdropColor;
        color.a = Mathf.Max(0.9f, Mathf.Clamp01(color.a));
        backdropImage.color = color;
        backdropImage.sprite = detailBackdropSprite;
        backdropImage.type = detailBackdropSprite != null ? Image.Type.Sliced : Image.Type.Simple;
        backdropImage.raycastTarget = false;
    }

    private static string TruncateTitleForList(string title, int maxWords = 5)
    {
        if (string.IsNullOrWhiteSpace(title))
            return string.Empty;

        if (maxWords < 1)
            maxWords = 1;

        string[] words = title.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= maxWords)
            return string.Join(" ", words);

        return string.Join(" ", words, 0, maxWords) + "...";
    }

    private static void SetTMP(GameObject parent, string path, string value)
    {
        var t = parent.transform.Find(path)?.GetComponent<TextMeshProUGUI>();
        if (t != null) t.text = value;
    }
}
