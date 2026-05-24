using System;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Root service locator for the application.
/// 
/// Responsibilities:
/// - Enforces a single persistent instance across scene loads (Singleton).
/// - Instantiates and exposes all app-wide services (local data + HTTP API clients).
/// 
/// Notes:
/// - Attach this to a GameObject in the initial scene.
/// - The object is marked DontDestroyOnLoad so services persist between scenes.
/// - This is a simple, Unity-friendly service locator; if you later adopt DI,
///   you can replace <see cref="InitializeServices"/> with DI container wiring.
/// </summary>
public class ServiceManager : MonoBehaviour
{
    #region Singleton

    /// <summary>
    /// Global singleton instance of <see cref="ServiceManager"/>.
    /// </summary>
    public static ServiceManager Instance { get; private set; }

    #endregion

    #region Stub Mode (FastAPI unreachable, scene-aware placeholder routing)

    public enum StubScene { Beach, Bedroom, Unknown }

    /// <summary>
    /// Maps the active Unity scene name to a stub scene flavour. Used by the
    /// stubbed API services to pick scene-specific placeholder copy.
    /// </summary>
    public StubScene CurrentStubScene
    {
        get
        {
            var n = SceneManager.GetActiveScene().name ?? string.Empty;
            if (n.IndexOf("Beach",   StringComparison.OrdinalIgnoreCase) >= 0) return StubScene.Beach;
            if (n.IndexOf("Bedroom", StringComparison.OrdinalIgnoreCase) >= 0) return StubScene.Bedroom;
            return StubScene.Unknown;
        }
    }

    [Header("Stub Mode (auto-routed per scene + action)")]
    [Tooltip("Counts completed sentiment calls in the currently-loaded scene. " +
             "1st journal = Happy/KEEP, 2nd+ = Sad/DISCARD. Resets on scene load.")]
    [SerializeField] private int _journalIndexInScene = 0;
    public int JournalIndexInScene => _journalIndexInScene;

    /// <summary>
    /// Set by <see cref="APITranscribeService"/> when it returns a chat-mode
    /// transcript; consumed by <see cref="APIChatService"/> on the next
    /// <c>SendPrompt</c> so the chat reply picks the voice flavour.
    /// </summary>
    [NonSerialized] public bool PendingChatVoiceFlag = false;

    public void NotifyJournalSentimentConsumed() => _journalIndexInScene++;

    public void ResetSceneStubState()
    {
        _journalIndexInScene = 0;
        PendingChatVoiceFlag = false;
    }

    #endregion

    #region Services

    /// <summary>Local user data access (CRUD).</summary>
    [HideInInspector] public LocalUserService    UserService    { get; private set; }

    /// <summary>Local conversation/message data access (CRUD).</summary>
    [HideInInspector] public LocalChatService    ChatService    { get; private set; }

    /// <summary>Local journal data access (CRUD).</summary>
    [HideInInspector] public LocalJournalService JournalService { get; private set; }

    /// <summary>Remote chat/completions API client.</summary>
    [HideInInspector] public APIChatService      ChatApi        { get; private set; }

    /// <summary>Remote topic/title generation API client.</summary>
    [HideInInspector] public APITopicService     TopicApi       { get; private set; }

    /// <summary>Remote conversation summarization API client.</summary>
    [HideInInspector] public APISummaryService   SummaryApi     { get; private set; }

    /// <summary>Remote agentic/reasoning flow API client.</summary>
    [HideInInspector] public APIAgenticService   AgenticApi     { get; private set; }

    /// <summary>Remote speech-to-text/transcription API client.</summary>
    [HideInInspector] public APITranscribeService TranscribeApi { get; private set; }
    
    /// <summary>Remote speech-to-text/transcription API client.</summary>
    [HideInInspector] public APISentimentService SentimentApi { get; private set; }


    #endregion

    #region Unity Callbacks

    /// <summary>
    /// Unity lifecycle: ensures the singleton instance and initializes all services.
    /// </summary>
    private void Awake()
    {
        if (!InitializeSingleton())
        {
            return;
        }

        InitializeServices();
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    private void OnDestroy()
    {
        if (Instance == this)
            SceneManager.sceneLoaded -= OnSceneLoaded;
    }

    private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        ResetSceneStubState();
        Debug.Log($"[STUB] ServiceManager scene loaded '{scene.name}' -> stub state reset.");
    }

    #endregion

    #region Initialization

    /// <summary>
    /// Enforces a single <see cref="ServiceManager"/> instance.
    /// Destroys duplicates and persists the surviving instance across scene loads.
    /// </summary>
    /// <returns>True if this is the active instance; otherwise false.</returns>
    private bool InitializeSingleton()
    {
        if (Instance != null)
        {
            Destroy(gameObject);
            return false;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
        return true;
    }

    /// <summary>
    /// Attaches and initializes all required services as components on the same GameObject.
    /// 
    /// Why AddComponent:
    /// - Keeps services visible in the Inspector (for debugging).
    /// - Lets services use Unity callbacks (Start/Update/OnEnable) if needed.
    /// 
    /// Ordering:
    /// - If services depend on each other, reorder or add explicit initialization hooks here.
    /// </summary>
    private void InitializeServices()
    {
        UserService     = gameObject.AddComponent<LocalUserService>();
        ChatService     = gameObject.AddComponent<LocalChatService>();
        JournalService  = gameObject.AddComponent<LocalJournalService>();

        ChatApi         = gameObject.AddComponent<APIChatService>();
        TopicApi        = gameObject.AddComponent<APITopicService>();
        SummaryApi      = gameObject.AddComponent<APISummaryService>();
        AgenticApi      = gameObject.AddComponent<APIAgenticService>();
        TranscribeApi   = gameObject.AddComponent<APITranscribeService>();
        SentimentApi    = gameObject.AddComponent<APISentimentService>();
    }

    #endregion
}