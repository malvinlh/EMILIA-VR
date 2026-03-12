using System.Collections;
using System.Collections.Generic;
using EMILIA.Data;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Two-view history panel:
///   1. <b>Conversation List</b> — buttons with topic titles from /topic API.
///   2. <b>Chat Log</b> — VN-style message log for a selected conversation.
///
/// Clicking a conversation button switches to the Chat Log view.
/// A "Back" button in the Chat Log header returns to the Conversation List.
///
/// Prefab hierarchy:
/// <code>
/// VRHistoryPanel (root)
///   +-- [VRHistoryPanel.cs]
///   +-- [CanvasGroup]
///   +-- Canvas (World Space)
///       +-- Background
///           +-- Header (TitleLabel + BackButton + CloseButton)
///           +-- AccentLine
///           +-- ScrollView
///               +-- Viewport
///                   +-- Content (VLG)
///           +-- EmptyLabel
/// </code>
/// </summary>
public class VRHistoryPanel : MonoBehaviour
{
    #region Inspector

    [Header("References")]
    [SerializeField] private VRChatBridge _chatBridge;

    [Header("Content")]
    [SerializeField] private Transform _contentParent;
    [SerializeField] private ScrollRect _scrollRect;

    [Header("Prefab")]
    [SerializeField] private GameObject _historyItemPrefab;

    [Header("Header")]
    [SerializeField] private TMP_Text _titleLabel;
    [SerializeField] private Button _backButton;
    [SerializeField] private Button _closeButton;

    [Header("Fonts")]
    [SerializeField] private TMP_FontAsset _buttonFont;

    [Header("Empty State")]
    [SerializeField] private GameObject _emptyStateLabel;

    [Header("Animation")]
    [SerializeField] private float _fadeSpeed = 4f;

    #endregion

    #region Colors

    private static readonly Color ConvBtnNormal = new(1f, 1f, 1f, 0.06f);
    private static readonly Color ConvBtnHighlight = new(1f, 1f, 1f, 0.15f);
    private static readonly Color ConvBtnPressed = new(1f, 1f, 1f, 0.25f);
    private static readonly Color UserNameColor = new(0.85f, 0.85f, 0.85f, 1f);
    private static readonly Color EmiliaNameColor = new(140f / 255f, 191f / 255f, 255f / 255f, 1f);
    private static readonly Color UserTextColor = new(0.75f, 0.75f, 0.75f, 1f);
    private static readonly Color EmiliaTextColor = new(0.9f, 0.9f, 0.9f, 1f);

    #endregion

    #region State

    private CanvasGroup _canvasGroup;
    private bool _isVisible;
    private float _targetAlpha;
    private readonly List<GameObject> _spawnedItems = new();

    /// <summary>null = conversation list view, non-null = viewing that conversation's log.</summary>
    private string _viewingConversationId;

    #endregion

    #region Unity

    private void Awake()
    {
        _canvasGroup = GetComponent<CanvasGroup>();
        if (_canvasGroup == null)
            _canvasGroup = gameObject.AddComponent<CanvasGroup>();

        _canvasGroup.alpha = 0f;
        _canvasGroup.interactable   = false;
        _canvasGroup.blocksRaycasts = false;
        _targetAlpha = 0f;
        _isVisible = false;

        if (_closeButton != null)
            _closeButton.onClick.AddListener(Hide);

        // Ensure the content VLG forces children to fill the full width at runtime,
        // regardless of how the prefab was saved.
        if (_contentParent != null)
        {
            var vlg = _contentParent.GetComponent<VerticalLayoutGroup>();
            if (vlg != null)
                vlg.childForceExpandWidth = true;
        }

        if (_backButton != null)
        {
            _backButton.onClick.AddListener(GoBackToList);
            _backButton.gameObject.SetActive(false);
        }
    }

    private void OnEnable()
    {
        if (_chatBridge != null)
        {
            _chatBridge.OnConversationListChanged   += OnConversationListUpdated;
            _chatBridge.OnMessagesChanged            += OnMessagesUpdated;
            _chatBridge.OnActiveConversationChanged  += OnActiveChanged;
        }
    }

    private void OnDisable()
    {
        if (_chatBridge != null)
        {
            _chatBridge.OnConversationListChanged   -= OnConversationListUpdated;
            _chatBridge.OnMessagesChanged            -= OnMessagesUpdated;
            _chatBridge.OnActiveConversationChanged  -= OnActiveChanged;
        }
    }

    private void Update()
    {
        if (!Mathf.Approximately(_canvasGroup.alpha, _targetAlpha))
        {
            _canvasGroup.alpha = Mathf.MoveTowards(
                _canvasGroup.alpha, _targetAlpha, _fadeSpeed * Time.deltaTime);
            bool interactable = _canvasGroup.alpha > 0.95f;
            _canvasGroup.interactable   = interactable;
            _canvasGroup.blocksRaycasts = interactable;
        }
    }

    #endregion

    #region Public API

    public void Show()
    {
        _isVisible = true;
        _targetAlpha = 1f;
        _viewingConversationId = null;
        ShowConversationList();
    }

    public void Hide()
    {
        _isVisible = false;
        _targetAlpha = 0f;
    }

    public void Toggle()
    {
        if (_isVisible) Hide();
        else            Show();
    }

    public bool IsVisible => _isVisible;

    #endregion

    #region View 1 — Conversation List

    private void ShowConversationList()
    {
        _viewingConversationId = null;
        ClearItems();

        if (_titleLabel != null)
            _titleLabel.text = "Conversations";
        if (_backButton != null)
            _backButton.gameObject.SetActive(false);

        var convIds = _chatBridge.ConversationIds;
        if (convIds == null || convIds.Count == 0)
        {
            if (_emptyStateLabel != null)
            {
                _emptyStateLabel.SetActive(true);
                var tmp = _emptyStateLabel.GetComponent<TMP_Text>();
                if (tmp != null) tmp.text = "No conversations yet";
            }
            return;
        }

        if (_emptyStateLabel != null) _emptyStateLabel.SetActive(false);

        foreach (var convId in convIds)
        {
            CreateConversationButton(convId);
        }

        ForceLayoutAndScrollTop();
    }

    private void CreateConversationButton(string convId)
    {
        if (_contentParent == null) return;

        // Create button root
        var go = new GameObject("ConvBtn_" + convId);
        go.transform.SetParent(_contentParent, false);
        _spawnedItems.Add(go);

        var rect = go.AddComponent<RectTransform>();
        rect.sizeDelta = new Vector2(0, 50);

        var le = go.AddComponent<LayoutElement>();
        le.flexibleWidth   = 1;
        le.preferredHeight = 50;

        // Background image for button
        var img = go.AddComponent<Image>();
        img.color = ConvBtnNormal;
        img.raycastTarget = true;

        // Button component
        var btn = go.AddComponent<Button>();
        btn.targetGraphic = img;
        var colors = btn.colors;
        colors.normalColor      = ConvBtnNormal;
        colors.highlightedColor = ConvBtnHighlight;
        colors.pressedColor     = ConvBtnPressed;
        colors.selectedColor    = ConvBtnHighlight;
        btn.colors = colors;

        // Title text
        var textGo = new GameObject("Label");
        textGo.transform.SetParent(go.transform, false);
        var textRect = textGo.AddComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.offsetMin = new Vector2(12, 4);
        textRect.offsetMax = new Vector2(-12, -4);

        var tmp = textGo.AddComponent<TextMeshProUGUI>();
        if (_buttonFont != null) tmp.font = _buttonFont;
        string title = _chatBridge.GetConversationTitle(convId);
        tmp.text = title.Length > 40 ? title[..40] + "..." : title;
        tmp.fontSize = 22;
        tmp.color = new Color(0.92f, 0.92f, 0.95f, 1f);
        tmp.alignment = TextAlignmentOptions.MidlineLeft;
        tmp.overflowMode = TextOverflowModes.Ellipsis;
        tmp.textWrappingMode = TextWrappingModes.NoWrap;
        tmp.raycastTarget = false;

        // Click → open that conversation's chat log
        string capturedId = convId;
        btn.onClick.AddListener(() => OpenChatLog(capturedId));
    }

    #endregion

    #region View 2 — Chat Log (VN-style)

    private void OpenChatLog(string conversationId)
    {
        _viewingConversationId = conversationId;

        if (_titleLabel != null)
        {
            string title = _chatBridge.GetConversationTitle(conversationId);
            _titleLabel.text = title.Length > 25 ? title[..25] + "..." : title;
        }
        if (_backButton != null)
            _backButton.gameObject.SetActive(true);

        // Load conversation into bridge cache (triggers OnMessagesChanged → RebuildChatLog)
        _chatBridge.LoadConversation(conversationId);

        // Also build immediately from cache if already loaded
        RebuildChatLog();
    }

    private void RebuildChatLog()
    {
        ClearItems();

        var messages = _chatBridge.GetCurrentMessages();
        if (messages == null || messages.Count == 0)
        {
            if (_emptyStateLabel != null)
            {
                _emptyStateLabel.SetActive(true);
                var tmp = _emptyStateLabel.GetComponent<TMP_Text>();
                if (tmp != null) tmp.text = "No messages yet";
            }
            return;
        }

        if (_emptyStateLabel != null) _emptyStateLabel.SetActive(false);

        for (int i = 0; i < messages.Count; i++)
        {
            var msg = messages[i];
            if (msg.Sender == "Reasoning") continue;
            CreateMessageEntry(msg, msg.Sender != "Bot" && msg.Sender != "Reasoning");
        }

        ForceLayoutAndScrollBottom();
    }

    private void CreateMessageEntry(Message msg, bool isUser)
    {
        if (_historyItemPrefab == null || _contentParent == null) return;

        var go = Instantiate(_historyItemPrefab, _contentParent);
        _spawnedItems.Add(go);

        var nameTransform = go.transform.Find("SpeakerLabel");
        var textTransform = go.transform.Find("MessageText");

        if (nameTransform != null)
        {
            var nameTmp = nameTransform.GetComponent<TMP_Text>();
            if (nameTmp != null)
            {
                nameTmp.text  = isUser ? "You" : "EMILIA";
                nameTmp.color = isUser ? UserNameColor : EmiliaNameColor;
            }
        }

        if (textTransform != null)
        {
            var textTmp = textTransform.GetComponent<TMP_Text>();
            if (textTmp != null)
            {
                textTmp.text  = msg.Text ?? "";
                textTmp.color = isUser ? UserTextColor : EmiliaTextColor;
            }
        }
    }

    #endregion

    #region Navigation

    private void GoBackToList()
    {
        ShowConversationList();
    }

    #endregion

    #region Helpers

    private void ClearItems()
    {
        for (int i = _spawnedItems.Count - 1; i >= 0; i--)
        {
            if (_spawnedItems[i] != null)
                Destroy(_spawnedItems[i]);
        }
        _spawnedItems.Clear();
    }

    private void ForceLayoutAndScrollTop()    => StartCoroutine(RebuildLayoutAndScroll(1f));
    private void ForceLayoutAndScrollBottom() => StartCoroutine(RebuildLayoutAndScroll(0f));

    /// <summary>
    /// Waits one frame so newly spawned GameObjects (especially TMP components) complete
    /// their first-frame initialization and report correct preferred sizes, then does a
    /// two-pass layout rebuild before setting the scroll position.
    /// </summary>
    private IEnumerator RebuildLayoutAndScroll(float normalizedPosition)
    {
        // One-frame delay: lets TMP generate font atlases and preferred-size data.
        yield return null;

        if (_contentParent is RectTransform contentRect)
        {
            // Pass 1: size every child (handles nested ContentSizeFitters on history items).
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(contentRect);
            // Pass 2: re-size the content panel itself now that children are correct.
            Canvas.ForceUpdateCanvases();
            LayoutRebuilder.ForceRebuildLayoutImmediate(contentRect);
        }

        if (_scrollRect != null)
            _scrollRect.verticalNormalizedPosition = normalizedPosition;
    }

    #endregion

    #region Event Handlers

    private void OnConversationListUpdated()
    {
        // If we're on the list view, refresh it
        if (_isVisible && _viewingConversationId == null)
            ShowConversationList();
    }

    private void OnMessagesUpdated()
    {
        // If we're viewing a specific conversation's log, refresh it
        if (_isVisible && _viewingConversationId != null)
            RebuildChatLog();
    }

    private void OnActiveChanged(string newConvoId)
    {
        // If the active conversation changed while we're viewing a log, update
        if (_isVisible && _viewingConversationId != null)
        {
            _viewingConversationId = newConvoId;
            if (newConvoId == null)
                ShowConversationList();
            else
                RebuildChatLog();
        }
    }

    #endregion
}
