using UnityEngine;
using UnityEngine.UI;
using TMPro;
using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.IO;
using EMILIA.Data;

/// <summary>
/// Orchestrates the chat experience:
/// - builds chat bubbles and history sidebar
/// - manages send/mic UI, auto-scroll, and avatar animations
/// - coordinates conversation lifecycle and message persistence
/// - integrates with normal and “agentic/reasoning” AI flows
/// </summary>
public class ChatManager : MonoBehaviour
{
    #region Inspector Fields

    [Header("Prefabs & UI References")]
    [SerializeField] private GameObject     _userBubblePrefab;
    [SerializeField] private GameObject     _aiBubblePrefab;
    [SerializeField] private GameObject     _historyButtonPrefab;
    [SerializeField] private Transform      _chatContentParent;
    [SerializeField] private Transform      _chatHistoryParent;
    [SerializeField] private TMP_InputField _inputField;
    [SerializeField] private Button         _newChatButton;
    [SerializeField] private Button         _sendButton;

    [Header("Reasoning (Agentic)")]
    [SerializeField] private Button         _reasoningSendButton; // toggle ON
    [SerializeField] private Button         _reasoningStopButton; // toggle OFF

    [Header("Audio Recording")]
    [SerializeField] private RecordAudio    _recorder; // single mic button handled by RecordAudio

    [Header("Delete Confirmation UI")]
    [SerializeField] private GameObject     _deleteChatSetting;
    [SerializeField] private Button         _deleteYesButton;
    [SerializeField] private Button         _deleteNoButton;

    [Header("Avatar Fade Settings")]
    [SerializeField] private Image[] _avatarImages;
    [SerializeField] private float avatarFadeTime = 0.25f;  // fade-out & fade-in
    [SerializeField] private float idleAlpha     = 1.0f;
    [SerializeField] private float thinkingAlpha = 1.0f;

    [Header("Animator Settings")]
    [SerializeField] private Animator anim;
    [SerializeField] private float animCrossFadeTime = 0.3f;

    [Header("Scroll")]
    [SerializeField] private ScrollRect _scrollRect;
    [SerializeField, Range(0f, 0.2f)] private float _autoScrollThreshold = 0.05f; // 0 = bottom, 1 = top
    private bool _autoScrollEnabled = true;
    private Coroutine _scrollCo;

    [Header("Navigation")]
    [SerializeField] private Button _homeButton;

    #endregion

    #region Constants & Fields

    private const string PrefKeyNickname     = "Nickname";
    private const string TypingPlaceholderId = "__TYPING__";
    private const string NewChatLabel        = "New Chat";
    private const string BotSender           = "Bot";
    private const string ReasoningSender     = "Reasoning";

    /// <summary>Matches "_cvNN" suffix and captures the NN part.</summary>
    private static readonly Regex ConversationRegex =
        new Regex(@"cv(\d+)$", RegexOptions.Compiled);

    private static readonly int IdleHash     = Animator.StringToHash("Base Layer.idle-3");
    private static readonly int ThinkingHash = Animator.StringToHash("Base Layer.idle-2");

    [SerializeField] private int animatorLayerIndex = 0;

    /// <summary>Public for other systems to set the active user.</summary>
    [HideInInspector] public string CurrentUserId;

    private string _currentConversationId;
    private string _pendingDeleteId;

    private bool   _isAwaitingResponse;
    private bool   _isReasoningMode;
    private bool   _isMicOn;

    /// <summary>In-memory cache of conversationId → messages.</summary>
    private readonly Dictionary<string, List<Message>> _messageCache = new();

    /// <summary>Set of conversationIds currently "typing".</summary>
    private readonly HashSet<string> _isTyping = new();

    /// <summary>User's known conversation ids.</summary>
    private readonly List<string> _userConvs = new();

    /// <summary>ConversationId → generated topic/title.</summary>
    private readonly Dictionary<string, string> _topicCache = new();

    /// <summary>Conversations for which a topic generation call is in flight.</summary>
    private readonly HashSet<string> _topicRequested = new();

    /// <summary>ConversationId → last summarized user/bot pair count.</summary>
    private readonly Dictionary<string, int> _lastSummarizedPairCount = new();

    private Coroutine _switchCo;

    /// <summary>Tracks if we already autoscrolled when typing first appeared in a conversation.</summary>
    private readonly HashSet<string> _typingAutoscrolled = new();

    /// <summary>True if any conversation is in "typing" state.</summary>
    private bool IsAnyTyping => _isTyping is { Count: > 0 };

    #endregion

    #region Unity

    /// <summary>
    /// Unity lifecycle. Caches avatars, loads user, and initializes UI wiring.
    /// </summary>
    private void Awake()
    {
        if (_avatarImages == null || _avatarImages.Length == 0)
        {
            _avatarImages = GetComponentsInChildren<Image>(true);
        }

        LoadCurrentUser();
        InitializeUI();
    }

    /// <summary>
    /// After Awake: fetch history so the sidebar is populated.
    /// </summary>
    private void Start()
    {
        FetchAndPopulateHistory();
    }

    /// <summary>Subscribes to recorder events when enabled.</summary>
    private void OnEnable()
    {
        if (_recorder == null) return;
        _recorder.OnSaved += OnAudioSaved;
        _recorder.OnMicStateChanged += HandleMicStateChanged;
    }

    /// <summary>Unsubscribes from recorder events when disabled.</summary>
    private void OnDisable()
    {
        if (_recorder == null) return;
        _recorder.OnSaved -= OnAudioSaved;
        _recorder.OnMicStateChanged -= HandleMicStateChanged;
    }

    #endregion

    #region Init & Session

    /// <summary>
    /// Loads the user from PlayerPrefs and resets the session state if user changed.
    /// </summary>
    private void LoadCurrentUser()
    {
        var nick = PlayerPrefs.GetString(PrefKeyNickname, "");
        if (string.IsNullOrEmpty(CurrentUserId))
        {
            CurrentUserId = nick;
        }
        else if (CurrentUserId != nick)
        {
            OnUserChanged(nick);
            return;
        }

        _currentConversationId = null;
        ClearChat();
    }

    /// <summary>
    /// Wires up UI, sets initial visibility states, and registers button handlers.
    /// </summary>
    private void InitializeUI()
    {
        if (_deleteChatSetting != null) _deleteChatSetting.SetActive(false);

        _newChatButton?.onClick.AddListener(OnNewChatClicked);
        _sendButton    ?.onClick.AddListener(OnSendClicked);

        if (_reasoningSendButton != null) _reasoningSendButton.onClick.AddListener(EnterReasoningMode);
        if (_reasoningStopButton != null) _reasoningStopButton.onClick.AddListener(ExitReasoningMode);

        if (_scrollRect != null) _scrollRect.onValueChanged.AddListener(OnScrollChanged);

        // Initial input state
        if (_inputField != null)
        {
            _inputField.text = string.Empty;
            _inputField.onValueChanged.AddListener(OnInputChanged);
        }
        if (_sendButton != null) _sendButton.gameObject.SetActive(false);

        _isReasoningMode = false;
        _isMicOn         = _recorder != null && _recorder.IsRecording;

        ApplyReasoningVisibilityByMode();
        UpdateMicAndSendVisibility();
        UpdateOtherInteractables();

        _deleteNoButton?.onClick.AddListener(() =>
        {
            if (_deleteChatSetting != null) _deleteChatSetting.SetActive(false);
            _pendingDeleteId = null;
        });
        _deleteYesButton?.onClick.AddListener(ConfirmDeleteConversation);
    }

    /// <summary>
    /// Input change handler to toggle send/mic visibility.
    /// </summary>
    private void OnInputChanged(string _)
    {
        UpdateMicAndSendVisibility();
    }

    /// <summary>
    /// Switches the active user and fully resets the in-memory session state.
    /// </summary>
    /// <param name="newUserId">New user id to set as active.</param>
    public void OnUserChanged(string newUserId)
    {
        if (string.Equals(CurrentUserId, newUserId, StringComparison.Ordinal)) return;
        CurrentUserId = newUserId;

        ResetSessionState();
        FetchAndPopulateHistory();
    }

    /// <summary>
    /// Clears all caches and brings the UI back to a neutral state for a fresh session.
    /// </summary>
    private void ResetSessionState()
    {
        _messageCache.Clear();
        _isTyping.Clear();
        _topicCache.Clear();
        _topicRequested.Clear();
        _lastSummarizedPairCount.Clear();
        _userConvs.Clear();
        _currentConversationId = null;
        _typingAutoscrolled.Clear();
        ClearChat();

        _isReasoningMode = false;
        _isMicOn         = false;

        ApplyReasoningVisibilityByMode();
        UpdateMicAndSendVisibility();
        UpdateOtherInteractables();
    }

    #endregion

    #region Reasoning Toggle (FadeOut → Switch → FadeIn)

    /// <summary>Enters “reasoning/agentic” mode and animates the avatar to the thinking state.</summary>
    private void EnterReasoningMode()
    {
        _isReasoningMode = true;
        StartSwitchWithFade(ThinkingHash, thinkingAlpha);
        ApplyReasoningVisibilityByMode();
        UpdateOtherInteractables();
    }

    /// <summary>Exits “reasoning/agentic” mode and animates the avatar back to idle.</summary>
    private void ExitReasoningMode()
    {
        _isReasoningMode = false;
        StartSwitchWithFade(IdleHash, idleAlpha);
        ApplyReasoningVisibilityByMode();
        UpdateOtherInteractables();
    }

    /// <summary>Starts the fade-out → state switch → fade-in animation chain.</summary>
    private void StartSwitchWithFade(int targetStateHash, float appearAlpha)
    {
        if (_switchCo != null) StopCoroutine(_switchCo);
        _switchCo = StartCoroutine(CoSwitchAnimWithFade(targetStateHash, appearAlpha));
    }

    /// <summary>Coroutine: fades out, switches animator state, then fades in.</summary>
    private IEnumerator CoSwitchAnimWithFade(int targetHash, float appearAlpha)
    {
        yield return CoFadeImages(0f, avatarFadeTime);
        anim.Play(targetHash, animatorLayerIndex, 0f);
        anim.Update(0f);
        yield return CoFadeImages(appearAlpha, avatarFadeTime);
    }

    /// <summary>Coroutine: lerps alpha of all avatar images over <paramref name="dur"/>.</summary>
    private IEnumerator CoFadeImages(float targetAlpha, float dur)
    {
        if (_avatarImages == null || _avatarImages.Length == 0) yield break;

        float t = 0f;
        float start = _avatarImages[0].color.a;

        while (t < dur)
        {
            t += Time.deltaTime;
            float a = Mathf.Lerp(start, targetAlpha, t / dur);

            for (int i = 0; i < _avatarImages.Length; i++)
            {
                var c = _avatarImages[i].color;
                c.a = a;
                _avatarImages[i].color = c;
            }
            yield return null;
        }

        for (int i = 0; i < _avatarImages.Length; i++)
        {
            var c = _avatarImages[i].color;
            c.a = targetAlpha;
            _avatarImages[i].color = c;
        }
    }

    /// <summary>Shows the correct “Reasoning ON/OFF” button for the current mode.</summary>
    private void ApplyReasoningVisibilityByMode()
    {
        if (_reasoningSendButton != null)
        {
            _reasoningSendButton.gameObject.SetActive(!_isReasoningMode);
        }
        if (_reasoningStopButton != null)
        {
            _reasoningStopButton.gameObject.SetActive(_isReasoningMode);
        }
    }

    #endregion

    #region Mic/Send Visibility

    /// <summary>External callback from <see cref="RecordAudio"/> when mic state changes.</summary>
    private void HandleMicStateChanged(bool isOn)
    {
        _isMicOn = isOn;
        UpdateMicAndSendVisibility();
        UpdateOtherInteractables();
    }

    /// <summary>
    /// Decides which control to show:
    /// - Recording: mic visible, send hidden.
    /// - Text present: send visible, mic hidden.
    /// - Empty: mic visible, send hidden.
    /// While waiting for AI (any conversation typing), mic remains visible but disabled.
    /// </summary>
    private void UpdateMicAndSendVisibility()
    {
        bool hasText = !string.IsNullOrWhiteSpace(_inputField != null ? _inputField.text : null);
        bool waitingGlobal = (_isAwaitingResponse || IsAnyTyping);

        if (_isMicOn)
        {
            if (_sendButton != null) _sendButton.gameObject.SetActive(false);
            _recorder?.SetUIState(isOn: true, uiEnabled: true, forceHide: false, waitingForAI: waitingGlobal);
            return;
        }

        if (hasText)
        {
            if (_sendButton != null) _sendButton.gameObject.SetActive(true);
            _recorder?.SetUIState(isOn: false, uiEnabled: false, forceHide: true, waitingForAI: waitingGlobal);
        }
        else
        {
            if (_sendButton != null) _sendButton.gameObject.SetActive(false);
            _recorder?.SetUIState(isOn: false, uiEnabled: true, forceHide: false, waitingForAI: waitingGlobal);
        }
    }

    #endregion

    #region Awaiting & Other Interactables

    /// <summary>Sets the “awaiting AI” flag and refreshes all interactivity.</summary>
    private void SetAwaiting(bool value)
    {
        _isAwaitingResponse = value;
        UpdateMicAndSendVisibility();
        UpdateOtherInteractables();
    }

    /// <summary>Locks/unlocks UI widgets (input, reasoning toggles, home) based on AI state.</summary>
    private void UpdateOtherInteractables()
    {
        bool waitingGlobal = (_isAwaitingResponse || IsAnyTyping);

        if (_reasoningSendButton != null)
        {
            _reasoningSendButton.interactable = !waitingGlobal && !_isReasoningMode;
        }
        if (_reasoningStopButton != null)
        {
            _reasoningStopButton.interactable = !waitingGlobal && _isReasoningMode;
        }

        if (_inputField != null)
        {
            _inputField.readOnly     = waitingGlobal;
            _inputField.interactable = !waitingGlobal;

            // If locking while focused, drop focus and keep caret at end.
            if (waitingGlobal && _inputField.isFocused)
            {
                _inputField.DeactivateInputField();
                int endPos = string.IsNullOrEmpty(_inputField.text) ? 0 : _inputField.text.Length;
                _inputField.caretPosition  = endPos;
                _inputField.stringPosition = endPos;
            }
        }

        if (_homeButton != null) _homeButton.interactable = !waitingGlobal;

        // New Chat intentionally always enabled
        if (_newChatButton != null) _newChatButton.interactable = true;
    }

    #endregion

    #region Audio → Transcribe → Send

    /// <summary>Called by <see cref="RecordAudio"/> once an audio file is saved.</summary>
    private void OnAudioSaved(string filePath)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
        {
            Debug.LogWarning($"Audio file not found: {filePath}");
            return;
        }
        StartCoroutine(TranscribeAndSendDirect(filePath));
    }

    /// <summary>
    /// Transcribes the audio file and sends the resulting text as a user message,
    /// creating a new conversation if needed.
    /// </summary>
    private IEnumerator TranscribeAndSendDirect(string filePath)
    {
        SetAwaiting(true);

        string finalText = null;
        yield return ServiceManager.Instance.TranscribeApi.TranscribeFile(
            filePath,
            onSuccess: text => { finalText = text ?? string.Empty; },
            onError:   err  => Debug.LogError($"Transcribe error: {err}")
        );

        if (!string.IsNullOrWhiteSpace(finalText))
        {
            CreateBubble(finalText, true);

            if (string.IsNullOrEmpty(_currentConversationId))
            {
                if (_isReasoningMode) StartNewConversationForReasoning(finalText);
                else                  StartNewConversation(finalText);
            }
            else
            {
                if (_isReasoningMode) StartCoroutine(SendUserMessageReasoning(finalText, _currentConversationId));
                else                  StartCoroutine(SendUserMessage(finalText, _currentConversationId));
            }
        }
        else
        {
            Debug.Log("Transcription result empty; nothing sent.");
            SetAwaiting(false);
        }

        if (_inputField != null) _inputField.text = string.Empty;
        _isMicOn = false;
        UpdateMicAndSendVisibility();
    }

    #endregion

    #region History Sidebar

    /// <summary>Loads the user’s conversation ids and builds the sidebar buttons.</summary>
    private void FetchAndPopulateHistory()
    {
        StartCoroutine(ServiceManager.Instance.ChatService.FetchUserConversations(
            CurrentUserId,
            convIds =>
            {
                PopulateHistoryButtons(convIds);
                _userConvs.Clear();
                _userConvs.AddRange(convIds);
            },
            err => Debug.LogError($"Fetch conv IDs failed: {err}")
        ));
    }

    /// <summary>Clears existing buttons and rebuilds them for the provided ids.</summary>
    private void PopulateHistoryButtons(string[] convIds)
    {
        for (int i = _chatHistoryParent.childCount - 1; i >= 0; i--)
        {
            Destroy(_chatHistoryParent.GetChild(i).gameObject);
        }

        for (int i = 0; i < convIds.Length; i++)
        {
            SetupHistoryButton(convIds[i], i);
        }
    }

    /// <summary>
    /// Instantiates a history button, sets its label (topic or placeholder),
    /// and wires its delete interaction.
    /// </summary>
    private void SetupHistoryButton(string convId, int index)
    {
        var go = Instantiate(_historyButtonPrefab, _chatHistoryParent);
        var hb = go.GetComponent<HistoryButton>();
        hb.SetConversationId(convId);

        var placeholderText = $"Chat {index + 1}";
        hb.SetLabel(placeholderText);

        var existingTitle = ServiceManager.Instance.ChatService.GetConversationTitle(convId);
        if (!string.IsNullOrWhiteSpace(existingTitle))
        {
            _topicCache[convId] = existingTitle;
            hb.SetLabel(existingTitle);
        }
        else if (_topicCache.TryGetValue(convId, out var sessionTopic) && !string.IsNullOrWhiteSpace(sessionTopic))
        {
            hb.SetLabel(sessionTopic);
        }

        if (string.IsNullOrWhiteSpace(existingTitle))
        {
            StartCoroutine(ServiceManager.Instance.ChatService.FetchConversationWithMessages(
                convId,
                CurrentUserId,
                msgs =>
                {
                    var firstUser = msgs.FirstOrDefault(m => m.Sender != BotSender);
                    var firstBot  = msgs.FirstOrDefault(m => m.Sender == BotSender);
                    if (firstUser != null && firstBot != null)
                    {
                        TryGenerateTopicOnce(convId, firstUser.Text, firstBot.Text, hb);
                    }
                    else
                    {
                        hb.SetLabel(NewChatLabel);
                    }
                },
                err => Debug.LogWarning($"Fetch conv for topic failed: {err}")
            ));
        }

        var delBtn = go.transform.Find("DeleteButton")?.GetComponent<Button>();
        if (delBtn != null)
        {
            delBtn.onClick.AddListener(() => OnDeleteClicked(convId));
        }
    }

    /// <summary>Loads a conversation into the chat panel and refreshes UI locks.</summary>
    public void OnHistoryClicked(string conversationId)
    {
        _currentConversationId = conversationId;
        ClearChat();

        SetAwaiting(_isTyping.Contains(conversationId)); // respect global typing state
        UpdateMicAndSendVisibility();
        UpdateOtherInteractables();

        if (_messageCache.TryGetValue(conversationId, out _))
        {
            RebuildChatUI(conversationId);
        }

        StartCoroutine(ServiceManager.Instance.ChatService.FetchConversationWithMessages(
            conversationId,
            CurrentUserId,
            msgs =>
            {
                _messageCache[conversationId] = new List<Message>(msgs);

                bool typing = _isTyping.Contains(conversationId);
                if (typing)
                {
                    _messageCache[conversationId].Add(new Message {
                        Id             = TypingPlaceholderId,
                        ConversationId = conversationId,
                        Sender         = BotSender,
                        Text           = null,
                        SentAt         = DateTime.UtcNow
                    });
                }

                SetAwaiting(typing);
                UpdateMicAndSendVisibility();
                UpdateOtherInteractables();

                RebuildChatUI(conversationId);
            },
            err => Debug.LogError($"Fetch conversation failed: {err}")
        ));
    }

    #endregion

    #region Sending & New Conversation

    /// <summary>Clears the panel and resets current conversation selection.</summary>
    private void OnNewChatClicked()
    {
        ClearChat();
        _currentConversationId = null;
    }

    /// <summary>Reads the input box and sends a message via normal (non-agentic) flow.</summary>
    private void OnSendClicked()
    {
        var text = _inputField.text.Trim();
        if (string.IsNullOrEmpty(text) || _isAwaitingResponse) return;

        _inputField.text = string.Empty;
        CreateBubble(text, true);

        if (string.IsNullOrEmpty(_currentConversationId))
        {
            if (_isReasoningMode) StartNewConversationForReasoning(text);
            else                  StartNewConversation(text);
        }
        else
        {
            if (_isReasoningMode) StartCoroutine(SendUserMessageReasoning(text, _currentConversationId));
            else                  StartCoroutine(SendUserMessage(text, _currentConversationId));
        }

        UpdateMicAndSendVisibility();
    }

    /// <summary>Agentic send handler (kept if you trigger it elsewhere).</summary>
    private void OnSendReasoningClicked()
    {
        var text = _inputField.text.Trim();
        if (string.IsNullOrEmpty(text) || _isAwaitingResponse) return;

        _inputField.text = string.Empty;
        CreateBubble(text, true);

        if (string.IsNullOrEmpty(_currentConversationId))
            StartNewConversationForReasoning(text);
        else
            StartCoroutine(SendUserMessageReasoning(text, _currentConversationId));

        UpdateMicAndSendVisibility();
    }

    /// <summary>Creates a new conversation and sends the first user message (normal flow).</summary>
    private void StartNewConversation(string text)
    {
        var convoId = GenerateConversationId();
        _currentConversationId = convoId;
        _userConvs.Add(convoId);
        _messageCache[convoId] = new List<Message>();

        AddHistoryButtonForNew(convoId, text);

        StartCoroutine(ServiceManager.Instance.ChatService.CreateConversation(
            convoId,
            CurrentUserId,
            onSuccess: () => StartCoroutine(SendUserMessage(text, convoId)),
            onError:   err => Debug.LogError($"CreateConversation failed: {err}")
        ));
    }

    /// <summary>Creates a new conversation and sends the first user message (agentic flow).</summary>
    private void StartNewConversationForReasoning(string text)
    {
        var convoId = GenerateConversationId();
        _currentConversationId = convoId;
        _userConvs.Add(convoId);
        _messageCache[convoId] = new List<Message>();

        AddHistoryButtonForNew(convoId, text);

        StartCoroutine(ServiceManager.Instance.ChatService.CreateConversation(
            convoId,
            CurrentUserId,
            onSuccess: () => StartCoroutine(SendUserMessageReasoning(text, convoId)),
            onError:   err => Debug.LogError($"CreateConversation (agentic) failed: {err}")
        ));
    }

    /// <summary>
    /// Generates the next conversation id in the form "{user}_cvNN".
    /// </summary>
    private string GenerateConversationId()
    {
        int nextIdx = _userConvs
            .Select(id =>
            {
                var m = ConversationRegex.Match(id);
                return m.Success ? int.Parse(m.Groups[1].Value) : 0;
            })
            .DefaultIfEmpty(0)
            .Max() + 1;

        return $"{CurrentUserId}_cv{nextIdx:00}";
    }

    /// <summary>Adds a new history button to the top of the list.</summary>
    private void AddHistoryButtonForNew(string convId, string initialMsg)
    {
        var go = Instantiate(_historyButtonPrefab, _chatHistoryParent);
        var hb = go.GetComponent<HistoryButton>();
        hb.SetConversationId(convId);
        hb.SetLabel(NewChatLabel);
        hb.transform.SetAsFirstSibling();

        var delBtn = go.transform.Find("DeleteButton")?.GetComponent<Button>();
        if (delBtn != null)
        {
            delBtn.onClick.AddListener(() => OnDeleteClicked(convId));
        }
    }

    /// <summary>Persists the user message then triggers the AI turn (normal flow).</summary>
    private IEnumerator SendUserMessage(string text, string convoId)
    {
        var userMsg = new Message {
            Id             = Guid.NewGuid().ToString(),
            ConversationId = convoId,
            Sender         = CurrentUserId,
            Text           = text,
            SentAt         = DateTime.UtcNow
        };

        _messageCache[convoId].Add(userMsg);

        yield return ServiceManager.Instance.ChatService.InsertMessage(
            convoId,
            CurrentUserId,
            text,
            onSuccess: () => StartCoroutine(HandleAITurn(text, convoId)),
            onError:   err => Debug.LogError($"Insert message failed: {err}")
        );
    }

    /// <summary>Persists the user message then triggers the AI turn (agentic flow).</summary>
    private IEnumerator SendUserMessageReasoning(string text, string convoId)
    {
        var userMsg = new Message {
            Id             = Guid.NewGuid().ToString(),
            ConversationId = convoId,
            Sender         = CurrentUserId,
            Text           = text,
            SentAt         = DateTime.UtcNow
        };

        _messageCache[convoId].Add(userMsg);

        yield return ServiceManager.Instance.ChatService.InsertMessage(
            convoId,
            CurrentUserId,
            text,
            onSuccess: () => StartCoroutine(HandleAgenticTurn(text, convoId)),
            onError:   err => Debug.LogError($"Insert message (agentic) failed: {err}")
        );
    }

    #endregion

    #region AI Handling & Typing

    /// <summary>
    /// Handles the standard (non-agentic) AI turn:
    /// - shows typing
    /// - calls Chat API
    /// - injects bot response and saves it
    /// - updates topic and summary hooks
    /// </summary>
    private IEnumerator HandleAITurn(string userMessage, string convoId)
    {
        SetAwaiting(true);

        _recorder?.SetUIState(isOn: false, uiEnabled: false, forceHide: false, waitingForAI: true);

        _isTyping.Add(convoId);
        UpdateMicAndSendVisibility();
        UpdateOtherInteractables();

        _messageCache[convoId].Add(new Message {
            Id             = TypingPlaceholderId,
            ConversationId = convoId,
            Sender         = BotSender,
            Text           = null,
            SentAt         = DateTime.UtcNow
        });

        if (_currentConversationId == convoId) RebuildChatUI(convoId);

        yield return ServiceManager.Instance.ChatApi.SendPrompt(
            username: CurrentUserId,
            question: userMessage,
            audioBytes: null,
            audioFileName: null,
            audioMime: null,
            onSuccess: response =>
            {
                _isTyping.Remove(convoId);
                UpdateMicAndSendVisibility();
                UpdateOtherInteractables();

                _messageCache[convoId].RemoveAll(m => m.Id == TypingPlaceholderId);

                var aiMsg = new Message {
                    Id             = Guid.NewGuid().ToString(),
                    ConversationId = convoId,
                    Sender         = BotSender,
                    Text           = response,
                    SentAt         = DateTime.UtcNow
                };
                _messageCache[convoId].Add(aiMsg);

                if (_currentConversationId == convoId)
                {
                    RebuildChatUI(convoId);
                    RequestScrollToBottom(force: true);
                }

                _typingAutoscrolled.Remove(convoId);
                SaveAIMessage(response, convoId);

                SetAwaiting(false);
                _recorder?.SetUIState(isOn: false, uiEnabled: true, forceHide: false, waitingForAI: false);

                TryGenerateTopicOnce(convoId, userMessage, response);
                TrySummarizeEveryTwoPairs(convoId);
            },
            onError: err =>
            {
                Debug.LogError($"Chat API error: {err}");
                _isTyping.Remove(convoId);
                UpdateMicAndSendVisibility();
                UpdateOtherInteractables();

                _messageCache[convoId].RemoveAll(m => m.Id == TypingPlaceholderId);
                _typingAutoscrolled.Remove(convoId);

                SetAwaiting(false);
                _recorder?.SetUIState(isOn: false, uiEnabled: true, forceHide: false, waitingForAI: false);

                if (_currentConversationId == convoId) RebuildChatUI(convoId);
            }
        );
    }

    /// <summary>
    /// Handles the agentic AI turn:
    /// - shows typing
    /// - calls Agentic API
    /// - injects optional reasoning message + bot response
    /// - persists messages and optional summary
    /// </summary>
    private IEnumerator HandleAgenticTurn(string userMessage, string convoId)
    {
        SetAwaiting(true);
        _recorder?.SetUIState(isOn: false, uiEnabled: false, forceHide: false, waitingForAI: true);

        _isTyping.Add(convoId);
        UpdateMicAndSendVisibility();
        UpdateOtherInteractables();

        _messageCache[convoId].Add(new Message {
            Id             = TypingPlaceholderId,
            ConversationId = convoId,
            Sender         = BotSender,
            Text           = null,
            SentAt         = DateTime.UtcNow
        });

        if (_currentConversationId == convoId) RebuildChatUI(convoId);

        yield return ServiceManager.Instance.AgenticApi.Send(
            userId:    CurrentUserId,
            username:  CurrentUserId,
            question:  userMessage,
            onSuccess: res =>
            {
                _isTyping.Remove(convoId);
                UpdateMicAndSendVisibility();
                UpdateOtherInteractables();

                _messageCache[convoId].RemoveAll(m => m.Id == TypingPlaceholderId);

                if (!string.IsNullOrWhiteSpace(res.reasoning))
                {
                    var rMsg = new Message {
                        Id             = Guid.NewGuid().ToString(),
                        ConversationId = convoId,
                        Sender         = ReasoningSender,
                        Text           = res.reasoning,
                        SentAt         = DateTime.UtcNow
                    };
                    _messageCache[convoId].Add(rMsg);
                    StartCoroutine(ServiceManager.Instance.ChatService.InsertMessage(convoId, ReasoningSender, rMsg.Text));
                }

                var botText = res.response ?? string.Empty;
                var bMsg = new Message {
                    Id             = Guid.NewGuid().ToString(),
                    ConversationId = convoId,
                    Sender         = BotSender,
                    Text           = botText,
                    SentAt         = DateTime.UtcNow
                };
                _messageCache[convoId].Add(bMsg);
                StartCoroutine(ServiceManager.Instance.ChatService.InsertMessage(convoId, BotSender, bMsg.Text));

                if (_currentConversationId == convoId)
                {
                    RebuildChatUI(convoId);
                    RequestScrollToBottom(force: true);
                }

                _typingAutoscrolled.Remove(convoId);

                SetAwaiting(false);
                _recorder?.SetUIState(isOn: false, uiEnabled: true, forceHide: false, waitingForAI: false);

                if (!string.IsNullOrWhiteSpace(res.summary))
                {
                    StartCoroutine(ServiceManager.Instance.ChatService.InsertSummary(
                        convoId,
                        res.summary,
                        onSuccess: () => Debug.Log($"[Agentic] summary saved for {convoId}"),
                        onError:   e => Debug.LogWarning($"[Agentic] save summary failed: {e}")
                    ));
                }

                TryGenerateTopicOnce(convoId, userMessage, botText);
                TrySummarizeEveryTwoPairs(convoId);
            },
            onError: err =>
            {
                Debug.LogError($"Agentic API error: {err}");
                _isTyping.Remove(convoId);
                UpdateMicAndSendVisibility();
                UpdateOtherInteractables();

                _messageCache[convoId].RemoveAll(m => m.Id == TypingPlaceholderId);
                _typingAutoscrolled.Remove(convoId);

                SetAwaiting(false);
                _recorder?.SetUIState(isOn: false, uiEnabled: true, forceHide: false, waitingForAI: false);

                if (_currentConversationId == convoId) RebuildChatUI(convoId);
            }
        );
    }

    /// <summary>Saves the bot’s response to storage and bumps the conversation to the top of history.</summary>
    private void SaveAIMessage(string response, string convoId)
    {
        StartCoroutine(ServiceManager.Instance.ChatService.InsertMessage(
            convoId,
            BotSender,
            response,
            onSuccess: () =>
            {
                var btn = _chatHistoryParent
                    .GetComponentsInChildren<HistoryButton>()
                    .FirstOrDefault(h => h.ConversationId == convoId);
                if (btn != null) btn.transform.SetAsFirstSibling();
            },
            onError: err => Debug.LogError($"Save AI message failed: {err}")
        ));
    }

    #endregion

    #region Topic & Summary

    /// <summary>
    /// Generates a short topic once (if none exists) using the first user/bot pair.
    /// Updates DB title and the corresponding history button label.
    /// </summary>
    private void TryGenerateTopicOnce(string convoId, string userText, string botText, HistoryButton hb = null)
    {
        if (string.IsNullOrWhiteSpace(userText) || string.IsNullOrWhiteSpace(botText)) return;

        var dbTitle = ServiceManager.Instance.ChatService.GetConversationTitle(convoId);
        if (!string.IsNullOrWhiteSpace(dbTitle))
        {
            _topicCache[convoId] = dbTitle;
            (hb ?? _chatHistoryParent.GetComponentsInChildren<HistoryButton>(true)
                                     .FirstOrDefault(h => h.ConversationId == convoId))
                                     ?.SetLabel(dbTitle);
            return;
        }

        if (_topicCache.ContainsKey(convoId) || _topicRequested.Contains(convoId)) return;

        _topicRequested.Add(convoId);

        StartCoroutine(ServiceManager.Instance.TopicApi.GetTopic(
            userText,
            botText,
            onSuccess: topic =>
            {
                _topicRequested.Remove(convoId);
                if (!string.IsNullOrWhiteSpace(topic))
                {
                    _topicCache[convoId] = topic;
                    ServiceManager.Instance.ChatService.UpdateConversationTitle(convoId, topic);
                    (hb ?? _chatHistoryParent.GetComponentsInChildren<HistoryButton>(true)
                                             .FirstOrDefault(h => h.ConversationId == convoId))
                                             ?.SetLabel(topic);
                }
            },
            onError: err =>
            {
                _topicRequested.Remove(convoId);
                Debug.LogWarning($"[TopicOnce] {convoId}: {err}");
            }
        ));
    }

    /// <summary>
    /// Requests an incremental summary every two user→bot message pairs.
    /// Stores the last summarized pair count to avoid duplicate requests.
    /// </summary>
    private void TrySummarizeEveryTwoPairs(string convoId)
    {
        if (!_messageCache.TryGetValue(convoId, out var msgs) || msgs == null) return;

        var ordered = msgs.OrderBy(m => m.SentAt).ToList();
        int pairs = 0;
        bool waitingForBot = false;

        foreach (var m in ordered)
        {
            if (m.Sender == BotSender)
            {
                if (waitingForBot) { pairs++; waitingForBot = false; }
            }
            else if (m.Sender != ReasoningSender)
            {
                waitingForBot = true;
            }
        }

        if (pairs < 2 || (pairs % 2 != 0)) return;

        int last = _lastSummarizedPairCount.TryGetValue(convoId, out var v) ? v : 0;
        if (pairs == last) return;

        _lastSummarizedPairCount[convoId] = pairs;

        if (ServiceManager.Instance?.SummaryApi == null) return;

        StartCoroutine(ServiceManager.Instance.SummaryApi.RequestSummary(
            convoId,
            onSuccess: text => Debug.Log($"[SUMMARY] ({convoId}) pairs={pairs}: {text}"),
            onError:   err  => Debug.LogWarning($"[SUMMARY] ({convoId}) ERROR: {err}")
        ));
    }

    #endregion

    #region Delete Conversation

    /// <summary>Opens the confirmation UI for deleting a conversation.</summary>
    public void OnDeleteClicked(string conversationId)
    {
        _pendingDeleteId = conversationId;
        if (_deleteChatSetting != null) _deleteChatSetting.SetActive(true);
    }

    /// <summary>Confirms delete: removes messages first, then the conversation itself.</summary>
    private void ConfirmDeleteConversation()
    {
        if (string.IsNullOrEmpty(_pendingDeleteId)) return;

        StartCoroutine(ServiceManager.Instance.ChatService.DeleteMessagesForConversation(
            _pendingDeleteId,
            onSuccess: () => StartCoroutine(ProceedDeleteConversation()),
            onError:   err => Debug.LogError($"DeleteMessages failed: {err}")
        ));
    }

    /// <summary>Coroutine that deletes the conversation and refreshes the sidebar.</summary>
    private IEnumerator ProceedDeleteConversation()
    {
        yield return ServiceManager.Instance.ChatService.DeleteConversation(
            _pendingDeleteId,
            onSuccess: () =>
            {
                if (_deleteChatSetting != null) _deleteChatSetting.SetActive(false);
                FetchAndPopulateHistory();

                if (_pendingDeleteId == _currentConversationId)
                {
                    ClearChat();
                    _currentConversationId = null;
                }
                _pendingDeleteId = null;
            },
            onError: err => Debug.LogError($"DeleteConversation failed: {err}")
        );
    }

    #endregion

    #region UI Rendering

    /// <summary>
    /// Rebuilds the visible chat panel from the cached messages of the given conversation.
    /// </summary>
    private void RebuildChatUI(string convoId)
    {
        ClearChat();

        var list = _messageCache[convoId];
        for (int i = 0; i < list.Count; i++)
        {
            var m = list[i];

            if (m.Id == TypingPlaceholderId)
            {
                var go   = Instantiate(_aiBubblePrefab, _chatContentParent);
                var ctrl = go.GetComponent<ChatBubbleController>();
                StartCoroutine(AnimateTyping(ctrl));

                // Auto-scroll exactly once when typing first appears
                if (!_typingAutoscrolled.Contains(convoId))
                {
                    RequestScrollToBottom(force: false);
                    _typingAutoscrolled.Add(convoId);
                }
                continue;
            }

            if (m.Sender == ReasoningSender)
            {
                string reasoning = m.Text ?? string.Empty;
                string response  = null;

                if (i + 1 < list.Count && list[i + 1].Sender == BotSender)
                {
                    response = list[i + 1].Text ?? string.Empty;
                    i++;
                }

                var go   = Instantiate(_aiBubblePrefab, _chatContentParent);
                var ctrl = go.GetComponent<ChatBubbleController>();
                ctrl.SetAgentic(reasoning, response);
                continue;
            }

            if (m.Sender == BotSender)
            {
                var go   = Instantiate(_aiBubblePrefab, _chatContentParent);
                var ctrl = go.GetComponent<ChatBubbleController>();
                ctrl.SetText(m.Text);
                continue;
            }

            bool isUser = (m.Sender == CurrentUserId);
            var g1   = Instantiate(isUser ? _userBubblePrefab : _aiBubblePrefab, _chatContentParent);
            var c1   = g1.GetComponent<ChatBubbleController>();
            c1.SetText(m.Text);
        }

        // Caller decides whether to auto-scroll.
    }

    /// <summary>Creates and appends a single chat bubble (user or bot) to the panel.</summary>
    private void CreateBubble(string message, bool isUser)
    {
        var prefab = isUser ? _userBubblePrefab : _aiBubblePrefab;
        var go     = Instantiate(prefab, _chatContentParent);
        go.GetComponent<ChatBubbleController>()?.SetText(message);

        // For user messages, always request an immediate scroll.
        RequestScrollToBottom(force: true);
    }

    /// <summary>Removes all child chat bubble objects from the content parent.</summary>
    private void ClearChat()
    {
        for (int i = _chatContentParent.childCount - 1; i >= 0; i--)
        {
            Destroy(_chatContentParent.GetChild(i).gameObject);
        }
    }

    /// <summary>Simple typing animation coroutine (“.” → “. .” → “. . .”).</summary>
    private IEnumerator AnimateTyping(ChatBubbleController ctrl)
    {
        var dots = new[] { "", ".", ". .", ". . ." };
        int i = 0;
        while (true)
        {
            ctrl.SetText(dots[i]);
            i = (i + 1) % dots.Length;
            yield return new WaitForSeconds(0.5f);
        }
    }

    #endregion

    #region Auto Scroll

    /// <summary>
    /// Tracks whether the user is near the bottom; only then do we auto-scroll.
    /// </summary>
    private void OnScrollChanged(Vector2 _)
    {
        if (_scrollRect == null) return;
        _autoScrollEnabled = (_scrollRect.verticalNormalizedPosition <= _autoScrollThreshold);
    }

    /// <summary>
    /// Requests a scroll-to-bottom once the next layout pass completes.
    /// </summary>
    /// <param name="force">If true, scroll even when user is not near bottom.</param>
    private void RequestScrollToBottom(bool force = false)
    {
        if (_scrollRect == null || _scrollRect.content == null) return;
        if (!force && !_autoScrollEnabled) return; // respect user position unless forced
        if (_scrollCo != null) StopCoroutine(_scrollCo);
        _scrollCo = StartCoroutine(CoScrollToBottom());
    }

    /// <summary>Coroutine that waits one frame for layout, then jumps to bottom.</summary>
    private IEnumerator CoScrollToBottom()
    {
        yield return null; // wait layout
        LayoutRebuilder.ForceRebuildLayoutImmediate(_scrollRect.content);
        _scrollRect.verticalNormalizedPosition = 0f;

        // Optional: one more frame to ensure large layouts are settled
        yield return null;
        LayoutRebuilder.ForceRebuildLayoutImmediate(_scrollRect.content);
        _scrollRect.verticalNormalizedPosition = 0f;
        _scrollCo = null;
    }

    #endregion
}