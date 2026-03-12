using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

/// <summary>
/// VR-native conversation history panel that shows a list of past conversations.
///
/// The panel appears as a floating world-space UI that can be shown/hidden via
/// <see cref="Toggle"/>. Each conversation item is a pokeable button that loads
/// the conversation into the dialogue panel. A delete button per item allows removal.
///
/// Prefab hierarchy:
/// <code>
/// VRHistoryPanel (root)
///   +-- [VRHistoryPanel.cs]
///   +-- [CanvasGroup] (for fade)
///   +-- Canvas (World Space, ~0.4m x 0.5m)
///       +-- Background (Image, dark panel)
///           +-- Header
///           |   +-- TitleLabel (TMP "Conversations")
///           |   +-- CloseButton (Button + XRSimpleInteractable)
///           +-- ScrollView (scroll with mask, vertical)
///               +-- Viewport
///                   +-- Content (VerticalLayoutGroup)
///                       +-- [HistoryItem instances, instantiated at runtime]
/// </code>
///
/// HistoryItem prefab:
/// <code>
/// HistoryItem
///   +-- [XRSimpleInteractable + BoxCollider (trigger)]
///   +-- Background (Image, subtle highlight on hover)
///   +-- TitleLabel (TMP, conversation topic)
///   +-- DeleteButton (Button + small "X")
/// </code>
/// </summary>
public class VRHistoryPanel : MonoBehaviour
{
    #region Inspector

    [Header("References")]
    [SerializeField] private VRChatBridge _chatBridge;

    [Header("Content")]
    [Tooltip("Parent Transform for history item instances (Content of ScrollView).")]
    [SerializeField] private Transform _contentParent;

    [Tooltip("Prefab for a single conversation item in the list.")]
    [SerializeField] private GameObject _historyItemPrefab;

    [Header("Panel Controls")]
    [SerializeField] private Button _closeButton;
    [SerializeField] private XRSimpleInteractable _closePoke;

    [Header("Empty State")]
    [Tooltip("Text shown when there are no conversations.")]
    [SerializeField] private GameObject _emptyStateLabel;

    [Header("Animation")]
    [SerializeField] private float _fadeSpeed = 4f;

    #endregion

    #region State

    private CanvasGroup _canvasGroup;
    private bool _isVisible;
    private float _targetAlpha;
    private readonly List<GameObject> _spawnedItems = new();

    #endregion

    #region Unity

    private void Awake()
    {
        _canvasGroup = GetComponent<CanvasGroup>();
        if (_canvasGroup == null)
            _canvasGroup = gameObject.AddComponent<CanvasGroup>();

        // Start hidden
        _canvasGroup.alpha = 0f;
        _canvasGroup.interactable   = false;
        _canvasGroup.blocksRaycasts = false;
        _targetAlpha = 0f;
        _isVisible = false;

        if (_closeButton != null)
            _closeButton.onClick.AddListener(Hide);
        if (_closePoke != null)
            _closePoke.selectEntered.AddListener(_ => Hide());
    }

    private void OnEnable()
    {
        if (_chatBridge != null)
        {
            _chatBridge.OnConversationListChanged  += Rebuild;
            _chatBridge.OnActiveConversationChanged += OnActiveChanged;
        }
    }

    private void OnDisable()
    {
        if (_chatBridge != null)
        {
            _chatBridge.OnConversationListChanged  -= Rebuild;
            _chatBridge.OnActiveConversationChanged -= OnActiveChanged;
        }
    }

    private void Update()
    {
        if (!Mathf.Approximately(_canvasGroup.alpha, _targetAlpha))
        {
            _canvasGroup.alpha = Mathf.MoveTowards(_canvasGroup.alpha, _targetAlpha, _fadeSpeed * Time.deltaTime);

            bool interactable = _canvasGroup.alpha > 0.95f;
            _canvasGroup.interactable   = interactable;
            _canvasGroup.blocksRaycasts = interactable;
        }
    }

    #endregion

    #region Public API

    /// <summary>Shows the panel and populates the conversation list.</summary>
    public void Show()
    {
        _isVisible = true;
        _targetAlpha = 1f;
        Rebuild();
    }

    /// <summary>Hides the panel.</summary>
    public void Hide()
    {
        _isVisible = false;
        _targetAlpha = 0f;
    }

    /// <summary>Toggles visibility.</summary>
    public void Toggle()
    {
        if (_isVisible) Hide();
        else            Show();
    }

    /// <summary>Whether the panel is currently visible or fading in.</summary>
    public bool IsVisible => _isVisible;

    #endregion

    #region Build List

    /// <summary>Clears and rebuilds the conversation list from VRChatBridge state.</summary>
    private void Rebuild()
    {
        if (!_isVisible) return;

        ClearItems();

        var convIds = _chatBridge.ConversationIds;
        if (convIds == null || convIds.Count == 0)
        {
            if (_emptyStateLabel != null) _emptyStateLabel.SetActive(true);
            return;
        }

        if (_emptyStateLabel != null) _emptyStateLabel.SetActive(false);

        string activeId = _chatBridge.CurrentConversationId;

        // Most recent first
        for (int i = convIds.Count - 1; i >= 0; i--)
        {
            string convoId = convIds[i];
            CreateItem(convoId, convoId == activeId);
        }
    }

    private void CreateItem(string convoId, bool isActive)
    {
        if (_historyItemPrefab == null || _contentParent == null) return;

        var go = Instantiate(_historyItemPrefab, _contentParent);
        _spawnedItems.Add(go);

        // Set title text
        var titleTmp = go.GetComponentInChildren<TMP_Text>();
        if (titleTmp != null)
        {
            string title = _chatBridge.GetConversationTitle(convoId);
            titleTmp.text = title;
        }

        // Highlight active conversation
        var bg = go.GetComponent<Image>();
        if (bg != null && isActive)
            bg.color = new Color(bg.color.r, bg.color.g, bg.color.b, 0.3f);

        // Wire poke/click to load this conversation
        var interactable = go.GetComponent<XRSimpleInteractable>();
        if (interactable != null)
        {
            interactable.selectEntered.AddListener(_ =>
            {
                _chatBridge.LoadConversation(convoId);
                Hide();
            });
        }

        // Also support standard Button click (for controller ray)
        var button = go.GetComponent<Button>();
        if (button != null)
        {
            button.onClick.AddListener(() =>
            {
                _chatBridge.LoadConversation(convoId);
                Hide();
            });
        }

        // Wire delete button if present
        var deleteBtn = go.transform.Find("DeleteButton")?.GetComponent<Button>();
        if (deleteBtn != null)
        {
            deleteBtn.onClick.AddListener(() => _chatBridge.DeleteConversation(convoId));
        }
    }

    private void ClearItems()
    {
        for (int i = _spawnedItems.Count - 1; i >= 0; i--)
        {
            if (_spawnedItems[i] != null)
                Destroy(_spawnedItems[i]);
        }
        _spawnedItems.Clear();
    }

    #endregion

    #region Event Handlers

    private void OnActiveChanged(string newConvoId)
    {
        if (_isVisible) Rebuild();
    }

    #endregion
}
