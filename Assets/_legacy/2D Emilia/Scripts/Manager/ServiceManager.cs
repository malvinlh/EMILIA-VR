using UnityEngine;

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