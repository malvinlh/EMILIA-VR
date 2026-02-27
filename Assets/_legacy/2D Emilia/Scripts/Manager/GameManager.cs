using UnityEngine;

/// <summary>
/// Central bootstrapper for the game.  
/// 
/// Responsibilities:
/// - Implements a global <see cref="GameManager"/> singleton.
/// - Ensures all core service managers (database, audio, scene flow, etc.) are initialized.
/// - Instantiates their prefabs only if no instance currently exists.
/// 
/// Attach this script to a persistent GameObject in your initial scene.
/// </summary>
public class GameManager : MonoBehaviour
{
    #region Singleton

    /// <summary>
    /// Global singleton instance of the <see cref="GameManager"/>.
    /// </summary>
    public static GameManager Instance { get; private set; }

    #endregion

    #region Inspector Fields

    [Header("Manager Prefabs")]
    [Tooltip("Prefab for the DatabaseManager (required).")]
    [SerializeField] private GameObject dbManagerPrefab;

    [Tooltip("Prefab for the AudioManager (required).")]
    [SerializeField] private GameObject audioManagerPrefab;

    [Tooltip("Prefab for the SceneFlowManager (required).")]
    [SerializeField] private GameObject sceneFlowManagerPrefab;

    [Tooltip("Prefab for the ServiceManager (required).")]
    [SerializeField] private GameObject serviceManagerPrefab;

    [Tooltip("Prefab for the FadeManager (required).")]
    [SerializeField] private GameObject fadeManagerPrefab;

    #endregion

    #region Unity Callbacks

    private void Awake()
    {
        if (!TryInitSingleton()) return;
        InitializeManagers();
    }

    #endregion

    #region Initialization

    /// <summary>
    /// Initializes the singleton pattern.  
    /// Returns true if this object is the active owner of the singleton,
    /// otherwise destroys itself to enforce uniqueness.
    /// </summary>
    private bool TryInitSingleton()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
            return true;
        }

        Destroy(gameObject);
        return false;
    }

    /// <summary>
    /// Ensures all core managers exist in the scene by instantiating their prefabs if needed.  
    /// Checks the corresponding static <c>Instance</c> property for each manager.
    /// </summary>
    private void InitializeManagers()
    {
        EnsureManager(EMILIA.Data.DatabaseManager.Instance, dbManagerPrefab);
        EnsureManager(AudioManager.Instance,                audioManagerPrefab);
        EnsureManager(SceneFlowManager.Instance,            sceneFlowManagerPrefab);
        EnsureManager(ServiceManager.Instance,              serviceManagerPrefab);
        EnsureManager(FadeManager.Instance,                 fadeManagerPrefab);
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Instantiates the given <paramref name="prefab"/> if the <paramref name="existing"/> instance is null.  
    /// Used to guarantee that a manager is always available at runtime.
    /// </summary>
    /// <param name="existing">The current manager instance, or null if not present.</param>
    /// <param name="prefab">Prefab to instantiate if the manager does not exist.</param>
    private static void EnsureManager(Object existing, GameObject prefab)
    {
        if (existing == null && prefab != null)
        {
            Instantiate(prefab);
        }
    }

    #endregion
}