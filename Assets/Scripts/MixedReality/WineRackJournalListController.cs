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
/// When the player points their controller at the Wine Rack and presses the trigger
/// while OUTSIDE an active journaling session (SessionState.Idle), this controller
/// toggles the JournalListCanvas visible. The canvas shows a read-only list of the
/// user's journals — edit and delete are intentionally absent.
///
/// Session guard:
///   - Shows canvas only when JournalSessionManager.CurrentState == Idle.
///   - Blocks interaction during any active MR calibration or journaling state.
///
/// Setup requirements in the Inspector / scene:
///   1. Add an XRSimpleInteractable component to this GameObject (or let RequireComponent
///      add it). Make sure the XRI Ray Interactor on the controller can reach it.
///   2. Add a non-trigger BoxCollider so the controller ray can hit this object.
///      (The existing trigger Collider on WineRackProximity is for bottle detection;
///       add a second, non-trigger BoxCollider for XRI ray picking.)
///   3. Wire the Inspector fields described below.
///
/// Inspector fields:
///   journalListCanvas   — World Space canvas parented under WineRack (inactive at start).
///   listContentParent   — ScrollRect → Viewport → Content transform.
///   journalEntryPrefab  — A prefab containing TitleText, ContentText, and
///                         TimestampBG/TimestampText children (TextMeshProUGUI).
///                         Any EditButton / DeleteButton children are automatically
///                         deactivated at runtime so this remains read-only.
///   bgNoJournals        — GameObject shown when there are no journal entries.
///   closeButton         — Optional "X" button inside the canvas to dismiss the list.
/// </summary>
[RequireComponent(typeof(XRSimpleInteractable))]
public class WineRackJournalListController : MonoBehaviour
{
    [Header("Journal List Canvas")]
    [Tooltip("World Space canvas under WineRack. Starts inactive; toggled by controller trigger.")]
    public GameObject journalListCanvas;

    [Header("List Content")]
    [Tooltip("Content Transform inside ScrollRect → Viewport (journal entries are spawned here).")]
    public Transform listContentParent;

    [Tooltip("Read-only journal entry prefab. Any EditButton / DeleteButton children are disabled at runtime.")]
    public GameObject journalEntryPrefab;

    [Tooltip("Shown when the user has no saved journals.")]
    public GameObject bgNoJournals;

    [Header("Close Button (Optional)")]
    [Tooltip("Button inside the canvas that hides the list. Leave null to rely on trigger-toggle only.")]
    public Button closeButton;

    // ── Private State ────────────────────────────────────────────────
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
        // Canvas starts hidden.
        if (journalListCanvas != null)
        {
            journalListCanvas.SetActive(false);
            EnsureTrackedRaycaster(journalListCanvas);
        }

        if (closeButton != null)
            closeButton.onClick.AddListener(HideCanvas);
    }

    /// <summary>
    /// Swaps the standard GraphicRaycaster (copied from the 2D scene) for
    /// TrackedDeviceGraphicRaycaster so the XRI controller ray can hit canvas buttons
    /// and scroll rects — mirrors the pattern used by WhiteboardPageManager.
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
        // Safety: auto-close the canvas the moment a journaling session begins,
        // even if the user forgot to close it before pressing the start button.
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

        // Ensure canvas is hidden if this object is disabled mid-session.
        HideCanvas();
    }

    // ================================================================
    // CONTROLLER INPUT (XRI trigger / select)
    // ================================================================

    private void OnControllerSelect(SelectEnterEventArgs args)
    {
        // Block interaction during any active MR calibration or journaling phase.
        var mgr = JournalSessionManager.Instance;
        if (mgr != null && mgr.CurrentState != JournalSessionManager.SessionState.Idle)
        {
            Debug.Log("[WineRackJournalList] Blocked — session is not Idle " +
                      $"(current state: {mgr.CurrentState}).");
            return;
        }

        // Toggle the canvas.
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

        FetchAndPopulate();
    }

    /// <summary>Called by the optional close button or on toggle-off.</summary>
    public void HideCanvas()
    {
        _isVisible = false;
        if (journalListCanvas != null)
            journalListCanvas.SetActive(false);
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
    // LIST POPULATION (read-only)
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

            // Populate text fields (paths must match your prefab hierarchy).
            SetTMP(go, "TitleText",                 j.Title);
            SetTMP(go, "ContentText",               j.Content);
            SetTMP(go, "TimestampBG/TimestampText",
                j.CreatedAt.ToString("dd/MM/yyyy hh:mm tt", CultureInfo.InvariantCulture));

            // Remove edit/delete interactivity — this is a read-only view.
            DisableButtonChild(go, "EditButton");
            DisableButtonChild(go, "DeleteButton");
        }
    }

    // ================================================================
    // HELPERS
    // ================================================================

    private static void SetTMP(GameObject parent, string path, string value)
    {
        var t = parent.transform.Find(path)?.GetComponent<TextMeshProUGUI>();
        if (t != null) t.text = value;
    }

    /// <summary>Deactivates a named child button so it is invisible and non-interactive.</summary>
    private static void DisableButtonChild(GameObject parent, string childName)
    {
        var child = parent.transform.Find(childName);
        if (child != null)
            child.gameObject.SetActive(false);
    }
}
