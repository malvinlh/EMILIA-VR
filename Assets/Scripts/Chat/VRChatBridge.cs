using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using EMILIA.Data;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using UnityEngine.Serialization;

/// <summary>
/// Orchestrates the VR chat experience in the 3D_Chat scene.
///
/// Connects <see cref="ServiceManager"/> (API + local data) to <see cref="VRDialoguePanel"/>
/// for display. Manages conversation lifecycle, message persistence, topic generation,
/// and periodic summarization — mirroring the patterns in the legacy ChatManager but
/// tailored for VR (voice-only input, single dialogue panel, history panel).
///
/// Wire references in the Inspector:
/// - <see cref="_dialoguePanel"/>: the VRDialoguePanel in the scene
/// - <see cref="_recorder"/>: a RecordAudio component for microphone input
/// </summary>
public class VRChatBridge : MonoBehaviour
{
    #region Inspector

    [Header("VR Dialogue")]
    [SerializeField] private VRDialoguePanel _dialoguePanel;

    [Header("Avatar")]
    [Tooltip("Used in 3D_Journal_Bedroom scene.")]
    [FormerlySerializedAs("_companionPatrol")]
    [SerializeField] private AvatarChatWaypointPatrolController _avatarPatrol;

    [Tooltip("Used in 3D_Journal_Beach scene.")]
    [FormerlySerializedAs("_companionRoaming")]
    [SerializeField] private AvatarIslandRoamingController _avatarRoaming;

    [Header("Audio Recording")]
    [SerializeField] private RecordAudio _recorder;

    [Header("Settings")]
    [Tooltip("Enable agentic/reasoning mode by default.")]
    [SerializeField] private bool _startInReasoningMode;

    #endregion

    // ── Singleton (convenient for external blocks like JournalStartButton) ──
    public static VRChatBridge Instance { get; private set; }

    #region Constants

    private const string PrefKeyNickname     = "Nickname";
    private const string BotSender           = "Bot";
    private const string ReasoningSender     = "Reasoning";

    private static readonly Regex ConversationRegex =
        new(@"cv(\d+)$", RegexOptions.Compiled);

    #endregion

    #region State

    private string _userId;
    private string _currentConversationId;
    private bool   _isReasoningMode;
    private bool   _isAwaitingResponse;
    private bool   _isControlInputLocked;

    // Mic double-fire guard (mirrors JournalMicController's pattern)
    private const float MicClickCooldownSec = 0.25f;
    private int   _lastMicClickFrame = -1;
    private float _lastMicClickTime  = -999f;
    private float _recordingStartTime = -999f;
    private const float MinRecordingSec = 1.0f;

    private readonly Dictionary<string, List<Message>> _messageCache = new();
    private readonly List<string>                      _userConvs    = new();
    private readonly Dictionary<string, string>        _topicCache   = new();
    private readonly HashSet<string>                   _topicRequested = new();
    private readonly Dictionary<string, int>           _lastSummarizedPairCount = new();

    #endregion

    #region Events

    /// <summary>Fired when the conversation list changes (new, deleted, title updated).</summary>
    public event Action OnConversationListChanged;

    /// <summary>Fired when reasoning mode is toggled. Passes the new state.</summary>
    public event Action<bool> OnReasoningModeChanged;

    /// <summary>Fired when the active conversation changes.</summary>
    public event Action<string> OnActiveConversationChanged;

    /// <summary>Fired when the microphone recording state changes.</summary>
    public event Action<bool> OnMicStateChanged;

    /// <summary>Fired when VR chat controls should lock or unlock.</summary>
    public event Action<bool> OnControlInputLockChanged;

    /// <summary>Fired when the message list for the active conversation changes.</summary>
    public event Action OnMessagesChanged;

    #endregion

    #region Unity

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(this); return; }
        Instance = this;
        _isReasoningMode = _startInReasoningMode;
        EnsureAvatarPatrol();
    }

    private void Start()
    {
        EnsureAvatarPatrol();
        _userId = PlayerPrefs.GetString(PrefKeyNickname, "");
        FetchConversationIds();
    }

    private void OnEnable()
    {
        if (_dialoguePanel == null)
            _dialoguePanel = FindFirstObjectByType<VRDialoguePanel>();

        if (_recorder != null)
        {
            // Subscribe to OnEncoded (in-memory bytes) instead of OnSaved (post-disk-write)
            // so the chat transcription request fires the moment the WAV is encoded,
            // in parallel with the off-thread file write — no disk round-trip.
            _recorder.OnEncoded += OnAudioEncoded;
            _recorder.OnMicStateChanged += OnMicStateProxy;
        }

        if (_dialoguePanel != null)
            _dialoguePanel.OnPresentationVisibilityChanged += OnDialoguePresentationVisibilityChanged;

        RefreshControlInputLock();
    }

    private void OnDisable()
    {
        if (_recorder != null)
        {
            _recorder.OnEncoded -= OnAudioEncoded;
            _recorder.OnMicStateChanged -= OnMicStateProxy;
        }

        if (_dialoguePanel != null)
            _dialoguePanel.OnPresentationVisibilityChanged -= OnDialoguePresentationVisibilityChanged;
    }

    #endregion

    #region Public API

    /// <summary>Toggle reasoning/agentic mode on or off.</summary>
    public void SetReasoningMode(bool enabled)
    {
        _isReasoningMode = enabled;
        OnReasoningModeChanged?.Invoke(_isReasoningMode);
    }

    /// <summary>Toggles reasoning mode to the opposite state.</summary>
    public void ToggleReasoningMode() => SetReasoningMode(!_isReasoningMode);

    /// <summary>Current reasoning mode state.</summary>
    public bool IsReasoningMode => _isReasoningMode;

    /// <summary>True while waiting for an AI response.</summary>
    public bool IsBusy => _isAwaitingResponse;

    /// <summary>True while control-panel and keyboard input should be disabled.</summary>
    public bool IsControlInputLocked => _isControlInputLocked;

    /// <summary>The currently active conversation id, or null if none.</summary>
    public string CurrentConversationId => _currentConversationId;

    /// <summary>Returns the user's conversation ids (most recent first).</summary>
    public IReadOnlyList<string> ConversationIds => _userConvs;

    /// <summary>The current user id (nickname from PlayerPrefs).</summary>
    public string UserId => _userId;

    /// <summary>
    /// Returns a read-only snapshot of messages for the current conversation.
    /// Returns null if no conversation is active or messages not yet loaded.
    /// </summary>
    public IReadOnlyList<Message> GetCurrentMessages()
    {
        if (_currentConversationId != null && _messageCache.TryGetValue(_currentConversationId, out var msgs))
            return msgs;
        return null;
    }

    /// <summary>
    /// Returns a read-only snapshot of messages for any conversation by id.
    /// Returns null if the conversation has not been fetched into the cache yet.
    /// </summary>
    public IReadOnlyList<Message> GetMessagesForConversation(string conversationId)
    {
        if (!string.IsNullOrEmpty(conversationId) && _messageCache.TryGetValue(conversationId, out var msgs))
            return msgs;
        return null;
    }

    /// <summary>
    /// Fetches a conversation's messages into the local cache and fires
    /// <see cref="OnMessagesChanged"/> when ready, WITHOUT altering the active
    /// conversation or touching the dialogue panel.
    /// Use this from the history panel so the dialogue panel is undisturbed.
    /// </summary>
    public void FetchMessagesForHistory(string conversationId)
    {
        if (string.IsNullOrEmpty(conversationId)) return;
        StartCoroutine(CoFetchForHistory(conversationId));
    }

    private IEnumerator CoFetchForHistory(string convoId)
    {
        if (!_messageCache.ContainsKey(convoId))
        {
            Message[] fetched = null;
            yield return ServiceManager.Instance.ChatService.FetchConversationWithMessages(
                convoId,
                _userId,
                msgs => fetched = msgs,
                err  => Debug.LogError($"[VRChatBridge] FetchForHistory failed: {err}")
            );

            if (fetched != null)
                _messageCache[convoId] = new List<Message>(fetched);
            else
                yield break;
        }

        OnMessagesChanged?.Invoke();
    }

    /// <summary>
    /// Gets the display title for a conversation (topic or fallback).
    /// </summary>
    public string GetConversationTitle(string convoId)
    {
        if (_topicCache.TryGetValue(convoId, out var cached) && !string.IsNullOrWhiteSpace(cached))
            return cached;

        var dbTitle = ServiceManager.Instance.ChatService.GetConversationTitle(convoId);
        if (!string.IsNullOrWhiteSpace(dbTitle))
        {
            _topicCache[convoId] = dbTitle;
            return dbTitle;
        }

        return "New Chat";
    }

    /// <summary>
    /// Starts a fresh conversation. Clears the current conversation context
    /// so the next voice input creates a new one.
    /// </summary>
    public void StartNewChat()
    {
        if (IsControlInputLocked) return;

        _currentConversationId = null;
        _dialoguePanel.Hide();
        OnActiveConversationChanged?.Invoke(null);
    }

    /// <summary>
    /// Loads a past conversation by id and shows the last AI response
    /// on the dialogue panel.
    /// </summary>
    public void LoadConversation(string conversationId)
    {
        if (_isAwaitingResponse) return;
        if (string.IsNullOrEmpty(conversationId)) return;

        _currentConversationId = conversationId;
        OnActiveConversationChanged?.Invoke(conversationId);

        StartCoroutine(CoLoadConversation(conversationId));
    }

    /// <summary>
    /// Deletes a conversation and its messages from storage.
    /// If the deleted conversation is the active one, clears the panel.
    /// </summary>
    public void DeleteConversation(string conversationId)
    {
        if (string.IsNullOrEmpty(conversationId)) return;

        StartCoroutine(CoDeleteConversation(conversationId));
    }

    /// <summary>
    /// Sends a text message directly (from keyboard, debug console, etc.).
    /// This is the main entry point for non-voice input.
    /// </summary>
    public bool SendTextMessage(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (IsControlInputLocked) return false;

        StartCoroutine(CoSendText(text.Trim()));
        return true;
    }

    /// <summary>
    /// Toggles microphone recording on/off. Call from VR control panel.
    /// Has a per-frame and time-based cooldown to prevent double-fire from VR poke gestures.
    /// </summary>
    public void ToggleMic()
    {
        if (_recorder == null) return;
        if (IsControlInputLocked) return;

        // Same double-fire guard pattern as JournalMicController.
        float now = Time.unscaledTime;
        if (_lastMicClickFrame == Time.frameCount)
        {
            Debug.Log("[VRChatBridge] ToggleMic: duplicate click ignored in frame " + Time.frameCount);
            return;
        }
        if (now - _lastMicClickTime < MicClickCooldownSec)
        {
            Debug.Log($"[VRChatBridge] ToggleMic: cooldown ({now - _lastMicClickTime:F3}s < {MicClickCooldownSec:F3}s).");
            return;
        }
        _lastMicClickFrame = Time.frameCount;
        _lastMicClickTime  = now;

        if (_recorder.IsRecording)
        {
            float elapsed = Time.realtimeSinceStartup - _recordingStartTime;
            if (elapsed < MinRecordingSec)
            {
                Debug.Log($"[VRChatBridge] ToggleMic: stop ignored after {elapsed:F2}s (min {MinRecordingSec:F2}s).");
                return;
            }
            _recorder.StopRecording();
        }
        else
            _recorder.StartRecording();
    }

    /// <summary>Whether the microphone is currently recording.</summary>
    public bool IsRecording => _recorder != null && _recorder.IsRecording;

    /// <summary>
    /// False while the bridge is busy (awaiting AI response) or mic is recording.
    /// JournalStartButton checks this before allowing a session to start.
    /// </summary>
    public bool IsChatInputAllowed => !_isAwaitingResponse && !IsRecording;

    #endregion

    #region Load Conversation

    private IEnumerator CoLoadConversation(string convoId)
    {
        // Fetch from DB if not cached
        if (!_messageCache.ContainsKey(convoId))
        {
            Message[] fetched = null;
            yield return ServiceManager.Instance.ChatService.FetchConversationWithMessages(
                convoId,
                _userId,
                msgs => fetched = msgs,
                err  => Debug.LogError($"[VRChatBridge] Load conversation failed: {err}")
            );

            if (fetched != null)
                _messageCache[convoId] = new List<Message>(fetched);
            else
                yield break;
        }

        var messages = _messageCache[convoId];
        if (messages.Count == 0)
        {
            _dialoguePanel.Hide();
            yield break;
        }

        // Find the last bot response (and any preceding reasoning)
        ShowLastResponse(messages);
        OnMessagesChanged?.Invoke();
    }

    /// <summary>
    /// Shows the most recent AI response from a message list on the dialogue panel.
    /// Handles both standard and agentic (reasoning + response) messages.
    /// </summary>
    private void ShowLastResponse(List<Message> messages)
    {
        // Walk backward to find the last Bot message
        Message lastBot = null;
        Message lastReasoning = null;

        for (int i = messages.Count - 1; i >= 0; i--)
        {
            if (messages[i].Sender == BotSender)
            {
                lastBot = messages[i];

                // Check if preceded by a Reasoning message
                if (i > 0 && messages[i - 1].Sender == ReasoningSender)
                    lastReasoning = messages[i - 1];

                break;
            }
        }

        if (lastBot == null)
        {
            _dialoguePanel.Hide();
            return;
        }

        if (lastReasoning != null)
            _dialoguePanel.ShowAgentic(lastReasoning.Text, lastBot.Text);
        else
            _dialoguePanel.ShowText(lastBot.Text);
    }

    #endregion

    #region Delete Conversation

    private IEnumerator CoDeleteConversation(string convoId)
    {
        // Delete messages first, then the conversation
        bool messagesDeleted = false;
        yield return ServiceManager.Instance.ChatService.DeleteMessagesForConversation(
            convoId,
            onSuccess: () => messagesDeleted = true,
            onError:   err => Debug.LogError($"[VRChatBridge] DeleteMessages failed: {err}")
        );

        if (!messagesDeleted) yield break;

        bool convoDeleted = false;
        yield return ServiceManager.Instance.ChatService.DeleteConversation(
            convoId,
            onSuccess: () => convoDeleted = true,
            onError:   err => Debug.LogError($"[VRChatBridge] DeleteConversation failed: {err}")
        );

        if (!convoDeleted) yield break;

        // Clean up local state
        _messageCache.Remove(convoId);
        _topicCache.Remove(convoId);
        _topicRequested.Remove(convoId);
        _lastSummarizedPairCount.Remove(convoId);
        _userConvs.Remove(convoId);

        // If this was the active conversation, clear the panel
        if (_currentConversationId == convoId)
        {
            _currentConversationId = null;
            _dialoguePanel.Hide();
            OnActiveConversationChanged?.Invoke(null);
        }

        OnConversationListChanged?.Invoke();
    }

    #endregion

    #region Audio → Transcribe → Send

    private void OnMicStateProxy(bool isRecording)
    {
        if (isRecording)
            _recordingStartTime = Time.realtimeSinceStartup;

        OnMicStateChanged?.Invoke(isRecording);
    }

    private void OnDialoguePresentationVisibilityChanged(bool _)
    {
        RefreshControlInputLock();
    }

    private void OnAudioEncoded(byte[] wavBytes, string fileName)
    {
        if (wavBytes == null || wavBytes.Length == 0) return;
        if (_isAwaitingResponse) return;

        StartCoroutine(TranscribeAndSend(wavBytes, fileName));
    }

    private IEnumerator TranscribeAndSend(byte[] wavBytes, string fileName)
    {
        SetAwaitingResponse(true);
        _dialoguePanel.ShowTypingIndicator();

        string transcribed = null;
        yield return ServiceManager.Instance.TranscribeApi.Transcribe(
            wavBytes,
            string.IsNullOrEmpty(fileName) ? "audio.wav" : fileName,
            "audio/wav",
            onSuccess: text => transcribed = text,
            onError:   err  => Debug.LogError($"[VRChatBridge] Transcribe error: {err}")
        );

        if (string.IsNullOrWhiteSpace(transcribed))
        {
            Debug.Log("[VRChatBridge] Transcription empty; aborting.");
            SetAwaitingResponse(false);
            _dialoguePanel.Hide();
            yield break;
        }

        // Hand off to the shared text-send flow
        yield return CoSendTextCore(transcribed);
    }

    /// <summary>Coroutine entry point for SendTextMessage.</summary>
    private IEnumerator CoSendText(string text)
    {
        SetAwaitingResponse(true);
        _dialoguePanel.ShowTypingIndicator();
        yield return CoSendTextCore(text);
    }

    /// <summary>Shared send flow used by both voice and keyboard paths.</summary>
    private IEnumerator CoSendTextCore(string text)
    {
        if (string.IsNullOrEmpty(_currentConversationId))
            yield return StartNewConversation(text);
        else
            yield return SendUserMessage(text, _currentConversationId);
    }

    #endregion

    #region Conversation Lifecycle

    private IEnumerator StartNewConversation(string text)
    {
        string convoId = GenerateConversationId();
        _currentConversationId = convoId;
        _userConvs.Add(convoId);
        _messageCache[convoId] = new List<Message>();

        bool created = false;
        yield return ServiceManager.Instance.ChatService.CreateConversation(
            convoId,
            _userId,
            onSuccess: () => created = true,
            onError:   err => Debug.LogError($"[VRChatBridge] CreateConversation failed: {err}")
        );

        if (created)
            yield return SendUserMessage(text, convoId);
        else
        {
            SetAwaitingResponse(false);
            _dialoguePanel.Hide();
        }
    }

    private IEnumerator SendUserMessage(string text, string convoId)
    {
        var userMsg = new Message
        {
            Id             = Guid.NewGuid().ToString(),
            ConversationId = convoId,
            Sender         = _userId,
            Text           = text,
            SentAt         = DateTime.UtcNow
        };

        if (!_messageCache.ContainsKey(convoId))
            _messageCache[convoId] = new List<Message>();
        _messageCache[convoId].Add(userMsg);
        OnMessagesChanged?.Invoke();

        bool inserted = false;
        yield return ServiceManager.Instance.ChatService.InsertMessage(
            convoId, _userId, text,
            onSuccess: () => inserted = true,
            onError:   err => Debug.LogError($"[VRChatBridge] InsertMessage failed: {err}")
        );

        if (!inserted)
        {
            SetAwaitingResponse(false);
            _dialoguePanel.Hide();
            yield break;
        }

        if (_isReasoningMode)
            yield return HandleAgenticTurn(text, convoId);
        else
            yield return HandleAITurn(text, convoId);
    }

    #endregion

    #region AI Response Handling

    private IEnumerator HandleAITurn(string userMessage, string convoId)
    {
        _dialoguePanel.ShowTypingIndicator();

        string aiResponse = null;
        string apiError   = null;

        yield return ServiceManager.Instance.ChatApi.SendPrompt(
            username:      _userId,
            question:      userMessage,
            audioBytes:    null,
            audioFileName: null,
            audioMime:     null,
            onSuccess:     resp => aiResponse = resp,
            onError:       err  => apiError = err
        );

        if (!string.IsNullOrEmpty(apiError))
        {
            Debug.LogError($"[VRChatBridge] Chat API error: {apiError}");
            SetAwaitingResponse(false);
            _dialoguePanel.Hide();
            yield break;
        }

        // Persist bot message
        var botMsg = new Message
        {
            Id             = Guid.NewGuid().ToString(),
            ConversationId = convoId,
            Sender         = BotSender,
            Text           = aiResponse,
            SentAt         = DateTime.UtcNow
        };
        _messageCache[convoId].Add(botMsg);
        StartCoroutine(ServiceManager.Instance.ChatService.InsertMessage(convoId, BotSender, aiResponse));

        // Display
        _dialoguePanel.ShowText(aiResponse);
        SetAwaitingResponse(false);
        OnMessagesChanged?.Invoke();

        // Post-response hooks
        TryGenerateTopicOnce(convoId, userMessage, aiResponse);
        TrySummarizeEveryTwoPairs(convoId);
        OnConversationListChanged?.Invoke();
    }

    private IEnumerator HandleAgenticTurn(string userMessage, string convoId)
    {
        _dialoguePanel.ShowTypingIndicator();

        APIAgenticService.AgenticResult result = null;
        string apiError = null;

        yield return ServiceManager.Instance.AgenticApi.Send(
            userId:   _userId,
            username: _userId,
            question: userMessage,
            onSuccess: res => result = res,
            onError:   err => apiError = err
        );

        if (!string.IsNullOrEmpty(apiError) || result == null)
        {
            Debug.LogError($"[VRChatBridge] Agentic API error: {apiError}");
            SetAwaitingResponse(false);
            _dialoguePanel.Hide();
            yield break;
        }

        // Persist reasoning (if any)
        if (!string.IsNullOrWhiteSpace(result.reasoning))
        {
            var rMsg = new Message
            {
                Id             = Guid.NewGuid().ToString(),
                ConversationId = convoId,
                Sender         = ReasoningSender,
                Text           = result.reasoning,
                SentAt         = DateTime.UtcNow
            };
            _messageCache[convoId].Add(rMsg);
            StartCoroutine(ServiceManager.Instance.ChatService.InsertMessage(convoId, ReasoningSender, rMsg.Text));
        }

        // Persist bot response
        string botText = result.response ?? string.Empty;
        var botMsg = new Message
        {
            Id             = Guid.NewGuid().ToString(),
            ConversationId = convoId,
            Sender         = BotSender,
            Text           = botText,
            SentAt         = DateTime.UtcNow
        };
        _messageCache[convoId].Add(botMsg);
        StartCoroutine(ServiceManager.Instance.ChatService.InsertMessage(convoId, BotSender, botText));

        // Persist agentic summary if provided
        if (!string.IsNullOrWhiteSpace(result.summary))
        {
            StartCoroutine(ServiceManager.Instance.ChatService.InsertSummary(convoId, result.summary));
        }

        // Display
        OnMessagesChanged?.Invoke();
        _dialoguePanel.ShowAgentic(result.reasoning, botText);
        SetAwaitingResponse(false);

        // Post-response hooks
        TryGenerateTopicOnce(convoId, userMessage, botText);
        TrySummarizeEveryTwoPairs(convoId);
        OnConversationListChanged?.Invoke();
    }

    #endregion

    #region Topic & Summary

    private void TryGenerateTopicOnce(string convoId, string userText, string botText)
    {
        if (string.IsNullOrWhiteSpace(userText) || string.IsNullOrWhiteSpace(botText)) return;

        var dbTitle = ServiceManager.Instance.ChatService.GetConversationTitle(convoId);
        if (!string.IsNullOrWhiteSpace(dbTitle))
        {
            _topicCache[convoId] = dbTitle;
            return;
        }

        if (_topicCache.ContainsKey(convoId) || _topicRequested.Contains(convoId)) return;
        _topicRequested.Add(convoId);

        StartCoroutine(ServiceManager.Instance.TopicApi.GetTopic(
            userText, botText,
            onSuccess: topic =>
            {
                _topicRequested.Remove(convoId);
                if (!string.IsNullOrWhiteSpace(topic))
                {
                    _topicCache[convoId] = topic;
                    ServiceManager.Instance.ChatService.UpdateConversationTitle(convoId, topic);
                }
            },
            onError: err =>
            {
                _topicRequested.Remove(convoId);
                Debug.LogWarning($"[VRChatBridge] Topic error: {err}");
            }
        ));
    }

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
            onSuccess: text => Debug.Log($"[VRChatBridge] Summary ({convoId}) pairs={pairs}: {text}"),
            onError:   err  => Debug.LogWarning($"[VRChatBridge] Summary error ({convoId}): {err}")
        ));
    }

    #endregion

    #region Helpers

    private void FetchConversationIds()
    {
        StartCoroutine(ServiceManager.Instance.ChatService.FetchUserConversations(
            _userId,
            convIds =>
            {
                _userConvs.Clear();
                _userConvs.AddRange(convIds);
                OnConversationListChanged?.Invoke();
            },
            err => Debug.LogError($"[VRChatBridge] Fetch conv IDs failed: {err}")
        ));
    }

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

        return $"{_userId}_cv{nextIdx:00}";
    }

    /// <summary>
    /// Scene name used by the Beach scene (checked via Contains so it is
    /// robust to path prefixes Unity may include in the loaded scene name).
    /// </summary>
    private const string BeachSceneName   = "3D_Journal_Beach";
    private const string BedroomSceneName = "3D_Journal_Bedroom";

    private void EnsureAvatarPatrol()
    {
        if (_dialoguePanel == null)
            _dialoguePanel = FindFirstObjectByType<VRDialoguePanel>();

        string currentScene = SceneManager.GetActiveScene().name;

        if (currentScene.Contains(BeachSceneName))
        {
            // ── Beach scene: use AvatarIslandRoamingController ──────────────
            EnsureAvatarRoaming();
        }
        else
        {
            // ── Bedroom (and any other) scene: use AvatarChatWaypointPatrolController ──
            EnsureAvatarWaypointPatrol();
        }
    }

    // ── Beach-scene helper ────────────────────────────────────────────────

    private void EnsureAvatarRoaming()
    {
        if (_avatarRoaming == null)
            _avatarRoaming = FindFirstObjectByType<AvatarIslandRoamingController>();

        if (_avatarRoaming == null)
        {
            // Try to find EMILIA host and attach the controller.
            GameObject avatarHost = FindPreferredAvatarHostObject();
            if (avatarHost != null)
                _avatarRoaming = avatarHost.AddComponent<AvatarIslandRoamingController>();
        }

        if (_avatarRoaming != null && _dialoguePanel != null)
            _avatarRoaming.SetDialoguePanel(_dialoguePanel);
    }

    // ── Bedroom-scene helper (existing logic, extracted for clarity) ──────

    private void EnsureAvatarWaypointPatrol()
    {
        GameObject avatarHost = FindPreferredAvatarHostObject();
        if (avatarHost != null)
        {
            var hostPatrol = avatarHost.GetComponent<AvatarChatWaypointPatrolController>();
            if (hostPatrol == null)
                hostPatrol = avatarHost.AddComponent<AvatarChatWaypointPatrolController>();

            // Keep the parent-hosted controller as the active one.
            var existingControllers = FindObjectsOfType<AvatarChatWaypointPatrolController>(true);
            foreach (var controller in existingControllers)
            {
                if (controller == null || controller == hostPatrol)
                    continue;

                controller.enabled = false;
            }

            _avatarPatrol = hostPatrol;
        }
        else if (_avatarPatrol == null)
        {
            _avatarPatrol = FindFirstObjectByType<AvatarChatWaypointPatrolController>();
        }

        if (_avatarPatrol != null && _dialoguePanel != null)
            _avatarPatrol.SetDialoguePanel(_dialoguePanel);
    }

    private GameObject FindPreferredAvatarHostObject()
    {
        Transform bestAnimatorTransform = null;
        int bestAnimatorDepth = int.MaxValue;

        var animators = FindObjectsOfType<Animator>(true);
        foreach (var animator in animators)
        {
            if (animator == null)
                continue;

            if (animator.name.IndexOf("emilia", StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            int depth = GetTransformDepth(animator.transform);
            if (depth < bestAnimatorDepth)
            {
                bestAnimatorDepth = depth;
                bestAnimatorTransform = animator.transform;
            }
        }

        if (bestAnimatorTransform != null)
            return ClimbToPreferredParent(bestAnimatorTransform).gameObject;

        Transform bestByName = null;
        int bestNameDepth = int.MaxValue;

        var allTransforms = FindObjectsOfType<Transform>(true);
        foreach (var t in allTransforms)
        {
            if (t == null)
                continue;

            if (t.name.IndexOf("emilia", StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            int depth = GetTransformDepth(t);
            if (depth < bestNameDepth)
            {
                bestNameDepth = depth;
                bestByName = t;
            }
        }

        if (bestByName == null)
            return null;

        return ClimbToPreferredParent(bestByName).gameObject;
    }

    private static Transform ClimbToPreferredParent(Transform start)
    {
        Transform current = start;

        while (current.parent != null)
        {
            Transform parent = current.parent;

            bool parentLooksLikeAvatar =
                parent.name.IndexOf("emilia", StringComparison.OrdinalIgnoreCase) >= 0;
            bool parentHasCoreComponents =
                parent.GetComponent<Animator>() != null ||
                parent.GetComponent<NavMeshAgent>() != null ||
                parent.GetComponent<CharacterController>() != null;

            if (!parentLooksLikeAvatar && !parentHasCoreComponents)
                break;

            current = parent;
        }

        return current;
    }

    private static int GetTransformDepth(Transform t)
    {
        int depth = 0;
        Transform cursor = t;

        while (cursor.parent != null)
        {
            depth++;
            cursor = cursor.parent;
        }

        return depth;
    }

    private void SetAwaitingResponse(bool awaiting)
    {
        _isAwaitingResponse = awaiting;
        RefreshControlInputLock();
    }

    private void RefreshControlInputLock()
    {
        bool shouldLock =
            _isAwaitingResponse ||
            (_dialoguePanel != null && _dialoguePanel.IsPresentationVisible);

        if (_isControlInputLocked == shouldLock)
            return;

        _isControlInputLocked = shouldLock;
        OnControlInputLockChanged?.Invoke(shouldLock);
    }

    #endregion
}
