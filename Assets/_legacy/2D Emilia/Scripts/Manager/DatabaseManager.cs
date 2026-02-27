using System.IO;
using UnityEngine;
using SQLite;

namespace EMILIA.Data
{
    /// <summary>
    /// Manages the SQLite database connection and ensures tables are created.
    /// Implements the Singleton pattern so there is only one instance in the application.
    /// </summary>
    public class DatabaseManager : MonoBehaviour
    {
        /// <summary>
        /// The name of the SQLite database file.
        /// </summary>
        private const string DatabaseFileName = "emilia.db";

        #region Singleton

        private static DatabaseManager _instance;

        /// <summary>
        /// Singleton instance of the <see cref="DatabaseManager"/>.
        /// </summary>
        public static DatabaseManager Instance
        {
            get => _instance;
            private set => _instance = value;
        }

        #endregion

        #region Database Connection

        private SQLiteConnection _db;

        /// <summary>
        /// The active SQLite database connection.
        /// All CRUD operations should use this property.
        /// </summary>
        public SQLiteConnection DB => _db;

        #endregion

        #region Unity Callbacks

        /// <summary>
        /// Called when the script instance is being loaded.
        /// Ensures the Singleton instance is set up and initializes the database connection.
        /// </summary>
        private void Awake()
        {
            SetupSingleton();
            InitializeDatabase();
        }

        /// <summary>
        /// Called when the application is quitting.
        /// Ensures that the database connection is closed properly.
        /// </summary>
        private void OnApplicationQuit()
        {
            CloseDatabase();
        }

        #endregion

        #region Initialization

        /// <summary>
        /// Ensures only one <see cref="DatabaseManager"/> exists in the application.
        /// If another instance exists, the duplicate is destroyed.
        /// </summary>
        private void SetupSingleton()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }

            Instance = this;
            DontDestroyOnLoad(gameObject);
        }

        /// <summary>
        /// Initializes the database connection, enables foreign keys, and ensures tables are created.
        /// </summary>
        private void InitializeDatabase()
        {
            var path = GetDatabasePath();
            Debug.Log($"[DatabaseManager] Opening SQLite DB at: {path}");

            _db = CreateConnection(path);
            EnableForeignKeys(_db);
            CreateTables();
        }

        #endregion

        #region Database Helpers

        /// <summary>
        /// Returns the full file path of the database.
        /// Creates the directory if it does not exist.
        /// </summary>
        private static string GetDatabasePath()
        {
            string folderPath = @"D:\Emilia\AI";

            if (!Directory.Exists(folderPath))
            {
                Directory.CreateDirectory(folderPath);
            }

            return Path.Combine(folderPath, DatabaseFileName);
        }

        /// <summary>
        /// Creates a new SQLite connection with the specified path and default settings.
        /// </summary>
        /// <param name="path">The full path to the database file.</param>
        private static SQLiteConnection CreateConnection(string path)
        {
            var connString = new SQLiteConnectionString(
                databasePath: path,
                storeDateTimeAsTicks: false,
                openFlags: SQLiteOpenFlags.ReadWrite | SQLiteOpenFlags.Create
            );

            return new SQLiteConnection(connString);
        }

        /// <summary>
        /// Enables SQLite foreign key constraints for the given connection.
        /// </summary>
        /// <param name="connection">The SQLite connection.</param>
        private static void EnableForeignKeys(SQLiteConnection connection)
        {
            connection.Execute("PRAGMA foreign_keys = ON;");
        }

        /// <summary>
        /// Ensures all necessary tables exist in the database.
        /// If they do not exist, they will be created.
        /// </summary>
        private void CreateTables()
        {
            _db.CreateTable<User>();
            _db.CreateTable<Conversation>();
            _db.CreateTable<Message>();
            _db.CreateTable<Journal>();
            _db.CreateTable<Summary>();

            Debug.Log("[DatabaseManager] Tables ensured: users, conversations, messages, journals, summary");
        }

        /// <summary>
        /// Closes the database connection if it is open.
        /// </summary>
        private void CloseDatabase()
        {
            _db?.Close();
            _db = null;
        }

        #endregion
    }
}
