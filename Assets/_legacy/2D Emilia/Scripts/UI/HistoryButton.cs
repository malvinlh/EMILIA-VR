using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.EventSystems;

[RequireComponent(typeof(Button))]
/// <summary>
/// Represents a single entry in the conversation history sidebar.
/// 
/// - Displays a truncated label for the conversation (with ellipsis if too long).
/// - Stores the underlying conversation ID and notifies <see cref="ChatManager"/>
///   when clicked.
/// - Shows a trash icon when hovered (used to delete the conversation).
/// </summary>
public class HistoryButton : MonoBehaviour, IPointerEnterHandler, IPointerExitHandler
{
    #region Inspector Fields

    [Header("UI Elements")]
    [Tooltip("Label text component for displaying conversation titles.")]
    [SerializeField] private TextMeshProUGUI _labelText;

    [Tooltip("Trash/delete icon that appears on hover.")]
    [SerializeField] private GameObject _trashIcon;

    [Header("Display Clamp (UI-only)")]
    [Tooltip("Maximum number of characters shown in the label (for UI only).")]
    [SerializeField] private int _maxLabelChars = 35;

    [Tooltip("Suffix appended when label is truncated.")]
    [SerializeField] private string _ellipsis = "…";

    #endregion

    #region Private Fields

    private string _conversationId;
    private string _fullLabel;    // Full, untruncated label
    private string _pendingLabel; // Buffer used if the GameObject is inactive
    private ChatManager _chatManager;

    #endregion

    #region Properties

    /// <summary>
    /// Gets the ID of the conversation associated with this history button.
    /// </summary>
    public string ConversationId => _conversationId;

    #endregion

    #region Unity Lifecycle

    private void Awake()
    {
        _chatManager = FindFirstObjectByType<ChatManager>();

        // Register click listener
        var btn = GetComponent<Button>();
        btn.onClick.AddListener(OnClicked);

        if (_labelText == null)
        {
            _labelText = GetComponentInChildren<TextMeshProUGUI>();
        }

        if (_trashIcon == null)
        {
            _trashIcon = transform.Find("DeleteButton")?.gameObject;
        }

        if (_trashIcon != null)
        {
            _trashIcon.SetActive(false); // hidden by default
        }
    }

    private void OnEnable()
    {
        // Apply any pending label if the button becomes active
        if (!string.IsNullOrEmpty(_pendingLabel))
        {
            _fullLabel = _pendingLabel;
            ApplyDisplayClamp(_fullLabel);
        }
    }

    #endregion

    #region Public API

    /// <summary>
    /// Assigns the conversation ID to this button.
    /// </summary>
    public void SetConversationId(string id) => _conversationId = id;

    /// <summary>
    /// Sets the label text for this button.
    /// Will clamp the display if necessary, but retains the full label internally.
    /// </summary>
    public void SetLabel(string text)
    {
        _pendingLabel = text ?? string.Empty;
        _fullLabel = _pendingLabel;

        if (_labelText != null && isActiveAndEnabled)
        {
            ApplyDisplayClamp(_fullLabel);
        }
    }

    /// <summary>
    /// Gets the full, non-truncated label string.
    /// </summary>
    public string GetCurrentLabel()
    {
        if (!string.IsNullOrEmpty(_fullLabel)) return _fullLabel;
        if (!string.IsNullOrEmpty(_pendingLabel)) return _pendingLabel;
        return _labelText != null ? _labelText.text : string.Empty;
    }

    #endregion

    #region Event Handlers

    /// <summary>
    /// Called when the button itself is clicked. 
    /// Informs <see cref="ChatManager"/> that the user selected this conversation.
    /// </summary>
    private void OnClicked()
    {
        Debug.Log($"[HistoryButton] Loading conversation '{_conversationId}'");
        _chatManager.OnHistoryClicked(_conversationId);
    }

    /// <summary>
    /// Shows the trash icon when the pointer enters the button area.
    /// </summary>
    public void OnPointerEnter(PointerEventData eventData)
    {
        if (_trashIcon != null)
        {
            _trashIcon.SetActive(true);
        }
    }

    /// <summary>
    /// Hides the trash icon when the pointer exits the button area.
    /// </summary>
    public void OnPointerExit(PointerEventData eventData)
    {
        if (_trashIcon != null)
        {
            _trashIcon.SetActive(false);
        }
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Applies truncation (clamp) to the displayed label text
    /// if it exceeds the max character count.
    /// </summary>
    private void ApplyDisplayClamp(string fullText)
    {
        if (_labelText == null) return;
        _labelText.text = ClampByLength(fullText, _maxLabelChars, _ellipsis);
    }

    /// <summary>
    /// Utility to clamp text by character count and add ellipsis.
    /// </summary>
    private static string ClampByLength(string input, int maxChars, string ellipsis)
    {
        if (string.IsNullOrEmpty(input) || maxChars <= 0) return string.Empty;
        if (input.Length <= maxChars) return input;
        return input.Substring(0, maxChars) + (string.IsNullOrEmpty(ellipsis) ? "" : ellipsis);
    }

    #endregion
}