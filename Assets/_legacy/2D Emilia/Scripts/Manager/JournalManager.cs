using System;
using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using EMILIA.Data;

/// <summary>
/// Manages the Journal feature: lists user journals, handles create/edit flows,
/// validates inputs, and coordinates delete confirmation.
/// 
/// UI model:
/// - Home canvas shows a scrollable list of entries (each entry prefab wires Edit/Delete).
/// - Journal canvas is reused for Create and Edit modes.
/// - Delete canvas shows a confirmation dialog.
/// 
/// Data model:
/// - Uses PlayerPrefs "Nickname" as the current user id.
/// - Delegates persistence to <see cref="ServiceManager.Instance.JournalService"/>.
/// </summary>
public class JournalManager : MonoBehaviour
{
    #region Inspector

    [Header("Home (List)")]
    [Tooltip("Prefab used to render each journal entry in the list.")]
    [SerializeField] private GameObject journalEntryPrefab;     // Prefab item list
    [Tooltip("Content Transform under the ScrollRect where entries are instantiated.")]
    [SerializeField] private Transform  homeContentParent;      // HomeCanvas/Viewport/Content
    [Tooltip("New button in the Home canvas.")]
    [SerializeField] private Button     newJournalButton;       // "New" button on Home
    [Tooltip("Background object shown when there are no notes.")]
    [SerializeField] private GameObject bgNoNotes;              // BG-NoNotes (active when empty)

    [Header("Canvases")]
    [Tooltip("Home canvas (list view).")]
    [SerializeField] private GameObject homeCanvas;
    [Tooltip("Journal canvas used for both Create and Edit.")]
    [SerializeField] private GameObject journalCanvas;          // Used for Create & Edit
    [Tooltip("Delete confirmation canvas.")]
    [SerializeField] private GameObject deleteCanvas;

    [Header("JournalCanvas (Create & Edit)")]
    [Tooltip("Header text that shows 'Create Journal' / 'Edit Journal' (optional).")]
    [SerializeField] private TextMeshProUGUI headerText;
    [Tooltip("Input field for the journal title (may be empty; derived from content).")]
    [SerializeField] private TMP_InputField  titleInput;
    [Tooltip("Input field for the journal content (required).")]
    [SerializeField] private TMP_InputField  contentInput;
    [Tooltip("Save button.")]
    [SerializeField] private Button          saveButton;
    [Tooltip("Back button to return to Home (optional).")]
    [SerializeField] private Button          backButton;

    [Header("DeleteCanvas")]
    [Tooltip("Confirmation text (optional).")]
    [SerializeField] private TextMeshProUGUI deleteTitleText;
    [SerializeField] private Button          deleteYesButton;
    [SerializeField] private Button          deleteNoButton;
    [SerializeField] private Button          deleteNoButton2;

    [Header("Validation (Optional)")]
    [Tooltip("Label to show validation errors.")]
    [SerializeField] private TextMeshProUGUI validationLabel;

    #endregion

    #region Fields

    private const string PrefKeyNickname = "Nickname";

    /// <summary>UI mode for the journal canvas.</summary>
    private enum JournalMode { Create, Edit }

    private JournalMode _mode;
    private Journal _currentJournal;        // Edit context
    private Journal _journalPendingDelete;  // Delete context

    [SerializeField] private TextMeshProUGUI aiReasonText;

    #endregion

    #region Unity

    /// <summary>
    /// Initializes canvases, wires UI events, and fetches journals for the current user.
    /// </summary>
    private void Start()
    {
        InitCanvases();
        BindUI();
        FetchUserJournals();
    }

    #endregion

    #region Init & Helpers

    /// <summary>
    /// Shows Home canvas by default and displays the "no notes" background initially.
    /// </summary>
    private void InitCanvases()
    {
        ShowOnly(homeCanvas);
        if (bgNoNotes != null) bgNoNotes.SetActive(true);
    }

    /// <summary>
    /// Wires up button callbacks and ensures single listeners are attached.
    /// </summary>
    private void BindUI()
    {
        if (newJournalButton != null)
        {
            newJournalButton.onClick.AddListener(OpenCreate);
        }

        if (backButton != null)
        {
            backButton.onClick.AddListener(() => ShowOnly(homeCanvas));
        }

        if (saveButton != null)
        {
            saveButton.onClick.RemoveAllListeners();
            saveButton.onClick.AddListener(OnSaveClicked);
        }

        if (deleteNoButton != null)
        {
            deleteNoButton.onClick.AddListener(() => ShowOnly(homeCanvas));
        }

        if (deleteNoButton2 != null)
        {
            deleteNoButton2.onClick.AddListener(() => ShowOnly(homeCanvas));
        }

        if (deleteYesButton != null)
        {
            deleteYesButton.onClick.RemoveAllListeners();
            deleteYesButton.onClick.AddListener(ConfirmDeleteJournal);
        }
    }

    /// <summary>
    /// Activates only the specified canvas and deactivates the others.
    /// </summary>
    /// <param name="target">Canvas GameObject to show.</param>
    private void ShowOnly(GameObject target)
    {
        if (homeCanvas    != null) homeCanvas.SetActive(target == homeCanvas);
        if (journalCanvas != null) journalCanvas.SetActive(target == journalCanvas);
        if (deleteCanvas  != null) deleteCanvas.SetActive(target == deleteCanvas);
    }

    /// <summary>
    /// Removes all instantiated journal entry items from the list.
    /// </summary>
    private void ClearHomeList()
    {
        foreach (Transform c in homeContentParent)
        {
            Destroy(c.gameObject);
        }
    }

    /// <summary>
    /// Convenience: finds a TMP text under <paramref name="parent"/> by relative path and sets its text.
    /// </summary>
    private static void SetTMP(GameObject parent, string path, string value)
    {
        var t = parent.transform.Find(path)?.GetComponent<TextMeshProUGUI>();
        if (t != null) t.text = value;
    }

    /// <summary>
    /// Formats the journal's creation (or updated) time for display.
    /// </summary>
    /// <remarks>
    /// Uses pattern "dd/MM/yyyy hh:mm tt" with <see cref="CultureInfo.InvariantCulture"/>.
    /// </remarks>
    private static string FormatCreatedAt(Journal j)
    {
        var dt = j.CreatedAt;
        return dt.ToString("dd/MM/yyyy hh:mm tt", CultureInfo.InvariantCulture);
    }

    // ----- validation helpers -----

    /// <summary>
    /// Wires validation to title/content inputs and refreshes initial state.
    /// </summary>
    private void WireValidation()
    {
        titleInput.onValueChanged.RemoveAllListeners();
        contentInput.onValueChanged.RemoveAllListeners();

        titleInput.onValueChanged.AddListener(_ => RefreshValidation());
        contentInput.onValueChanged.AddListener(_ => RefreshValidation());

        RefreshValidation();
    }

    /// <summary>
    /// Re-evaluates validation, updates UI error label and save button interactivity.
    /// </summary>
    private void RefreshValidation()
    {
        string msg;
        bool ok = ValidateInputs(out msg);

        if (validationLabel != null) validationLabel.text = ok ? string.Empty : msg;
        if (saveButton != null) saveButton.interactable = ok;
    }

    /// <summary>
    /// Business rule: content is required; title may be empty (auto-derived).
    /// </summary>
    /// <param name="message">Output validation message if invalid; otherwise null.</param>
    /// <returns>True if inputs are valid; otherwise false.</returns>
    private bool ValidateInputs(out string message)
    {
        var c = (contentInput?.text ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(c))
        {
            message = "Journal content must not be empty.";
            return false;
        }

        message = null;
        return true;
    }

    /// <summary>
    /// Derives a short title from content using the first N words and caps total length.
    /// </summary>
    /// <param name="content">Content to sample words from.</param>
    /// <param name="maxWords">Maximum number of words to use.</param>
    /// <param name="maxChars">Maximum character length.</param>
    private static string DeriveTitleFromContent(string content, int maxWords = 3, int maxChars = 60)
    {
        if (string.IsNullOrWhiteSpace(content)) return "Untitled";

        var words = Regex.Matches(content.Trim(), @"\S+");
        int take = Math.Min(maxWords, words.Count);

        string title = string.Empty;
        for (int i = 0; i < take; i++)
        {
            if (i > 0) title += " ";
            title += words[i].Value;
        }

        if (title.Length > maxChars)
        {
            title = title.Substring(0, maxChars);
        }

        return title;
    }

    #endregion

    #region Data

    /// <summary>
    /// Fetches all journals for the current user (from PlayerPrefs "Nickname").
    /// </summary>
    private void FetchUserJournals()
    {
        var userId = PlayerPrefs.GetString(PrefKeyNickname, string.Empty);
        if (string.IsNullOrEmpty(userId))
        {
            Debug.LogError("[JournalManager] No nickname in PlayerPrefs!");
            return;
        }

        StartCoroutine(
            ServiceManager.Instance.JournalService.FetchUserJournals(
                userId,
                OnJournalsReceived,
                err => Debug.LogError($"Fetch journals failed: {err}")
            )
        );
    }

    /// <summary>
    /// Populates the Home list with journal cards (or shows empty state).
    /// </summary>
    /// <param name="journals">Array of user journals (may be null).</param>
    private void OnJournalsReceived(Journal[] journals)
    {
        ClearHomeList();

        // Toggle "no notes" background
        if (bgNoNotes != null)
        {
            bgNoNotes.SetActive(journals == null || journals.Length == 0);
        }

        if (journals == null) return;

        foreach (var j in journals)
        {
            var go = Instantiate(journalEntryPrefab, homeContentParent);
            go.name = $"Journal_{j.Id}";

            // Fill prefab text (update the paths to match your prefab hierarchy)
            SetTMP(go, "TitleText",                 j.Title);
            SetTMP(go, "ContentText",               j.Content);
            SetTMP(go, "TimestampBG/TimestampText", FormatCreatedAt(j));

            // Wire Edit & Delete from the prefab component
            var entry = go.GetComponent<JournalEntry>();
            if (entry == null) entry = go.AddComponent<JournalEntry>();
            entry.Setup(
                onEdit:   () => OpenEdit(j),
                onDelete: () => OpenDeleteDialog(j)
            );
        }
    }

    #endregion

    #region Create & Edit (JournalCanvas)

    /// <summary>
    /// Opens the Journal canvas in Create mode and wires validation.
    /// </summary>
    private void OpenCreate()
    {
        _mode = JournalMode.Create;
        _currentJournal = null;

        if (headerText != null) headerText.text = "Create Journal";
        if (titleInput   != null) titleInput.text   = string.Empty; // title may be auto-derived
        if (contentInput != null) contentInput.text = string.Empty;

        ShowOnly(journalCanvas);
        WireValidation();
    }

    /// <summary>
    /// Opens the Journal canvas in Edit mode with the selected journal populated.
    /// </summary>
    /// <param name="j">Journal to edit.</param>
    private void OpenEdit(Journal j)
    {
        _mode = JournalMode.Edit;
        _currentJournal = j;

        if (headerText  != null) headerText.text  = "Edit Journal";
        if (titleInput  != null) titleInput.text  = j.Title;
        if (contentInput!= null) contentInput.text= j.Content;

        ShowOnly(journalCanvas);
        WireValidation();
    }

    /// <summary>
    /// Validates inputs, derives a title if needed, and dispatches Create/Update service calls.
    /// </summary>
    private void OnSaveClicked()
    {
        if (!ValidateInputs(out _))
        {
            RefreshValidation();
            Debug.LogWarning("Content is empty — skipping save.");
            return;
        }

        var rawTitle = (titleInput  ? titleInput.text  : string.Empty).Trim();
        var content  = (contentInput? contentInput.text: string.Empty).Trim();

        // If title is empty → derive from the first 3 words of content
        var title = string.IsNullOrWhiteSpace(rawTitle)
                    ? DeriveTitleFromContent(content, 3, 60)
                    : rawTitle;

        // Persist in Asia/Jakarta (UTC+7) as ISO-like string
        var nowIso = DateTime.UtcNow.AddHours(7).ToString("yyyy-MM-ddTHH:mm:ss");

        if (_mode == JournalMode.Create)
        {
            // StartCoroutine(
            //     ServiceManager.Instance.JournalService.CreateJournal(
            //         PlayerPrefs.GetString(PrefKeyNickname, string.Empty),
            //         title,
            //         content,
            //         nowIso,
            //         onSuccess: _ =>
            //         {
            //             ShowOnly(homeCanvas);
            //             FetchUserJournals();
            //         },
            //         onError: err => Debug.LogError($"Create journal failed: {err}")
            //     )
            // );
                StartCoroutine(
                ServiceManager.Instance.JournalService.CreateJournal(
                    PlayerPrefs.GetString(PrefKeyNickname, string.Empty),
                    title,
                    content,
                    nowIso,
                    onSuccess: _ =>
                    {
                        // === TESTING ONLY: panggil AI sentiment ===
                        StartCoroutine(
                            ServiceManager.Instance.SentimentApi.AnalyzeJournal(
                                content,
                                onSuccess: result =>
                                {
                                    // 1️⃣ Tampilkan ke UI (testing)
                                    if (aiReasonText != null)
                                    {
                                        aiReasonText.text = result.reason;
                                    }

                                    // 2️⃣ SIMPAN ke SQLite
                                    StartCoroutine(
                                        ServiceManager.Instance.JournalService.UpdateJournalSentiment(
                                            _.Id,              // journal yang baru dibuat
                                            result.tone,       // sentiment_label
                                            result.reason,     // ai_comment
                                            onSuccess: () =>
                                            {
                                                Debug.Log("Sentiment & AI comment saved to DB.");
                                            },
                                            onError: err =>
                                            {
                                                Debug.LogError($"Failed to save sentiment: {err}");
                                            }
                                        )
                                    );
                                },
                                onError: err =>
                                {
                                    Debug.LogError($"Sentiment API failed: {err}");
                                }
                            )
                        );

                        ShowOnly(homeCanvas);
                        FetchUserJournals();
                    },
                    onError: err => Debug.LogError($"Create journal failed: {err}")
                )
            );
        }
        else
        {
            StartCoroutine(
                ServiceManager.Instance.JournalService.UpdateJournal(
                    _currentJournal.Id,
                    title,
                    content,
                    nowIso,
                    onSuccess: () =>
                    {
                        // Light local cache update (optional; list is re-fetched anyway)
                        _currentJournal.Title     = title;
                        _currentJournal.Content   = content;

                        ShowOnly(homeCanvas);
                        FetchUserJournals();
                    },
                    onError: err => Debug.LogError($"Update journal failed: {err}")
                )
            );
        }
    }

    #endregion

    #region Delete (DeleteCanvas)

    /// <summary>
    /// Opens the delete confirmation dialog for the specified journal.
    /// </summary>
    private void OpenDeleteDialog(Journal j)
    {
        _journalPendingDelete = j;
        if (deleteTitleText != null)
        {
            deleteTitleText.text = $"Delete \"{j.Title}\" ?";
        }
        ShowOnly(deleteCanvas);
    }

    /// <summary>
    /// Confirms deletion: calls service to delete and refreshes list on success.
    /// </summary>
    private void ConfirmDeleteJournal()
    {
        if (_journalPendingDelete == null)
        {
            ShowOnly(homeCanvas);
            return;
        }

        StartCoroutine(
            ServiceManager.Instance.JournalService.DeleteJournal(
                _journalPendingDelete.Id,
                onSuccess: () =>
                {
                    _journalPendingDelete = null;
                    ShowOnly(homeCanvas);
                    FetchUserJournals();
                },
                onError: err => Debug.LogError($"Delete journal failed: {err}")
            )
        );
    }

    #endregion
}