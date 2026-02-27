// LocalChatService.cs
using System;
using System.Collections;
using System.Linq;
using SQLite;
using UnityEngine;
using EMILIA.Data;

public class LocalChatService : MonoBehaviour
{
    #region Fields & Setup

    private const string LogTag = "[LocalChatService]";
    private SQLiteConnection _db;

    private void Awake()
    {
        _db = DatabaseManager.Instance.DB;
        _db.Execute("PRAGMA foreign_keys = ON;");

        // Migrasi ringan: tambah kolom title jika belum ada
        EnsureTitleColumn();
    }

    private void EnsureTitleColumn()
    {
        try
        {
            _db.Execute("ALTER TABLE conversations ADD COLUMN title TEXT;");
        }
        catch (Exception ex)
        {
            // Abaikan duplikat kolom; log kalau error lain
            var msg = ex.Message?.ToLower() ?? "";
            if (!msg.Contains("duplicate column name"))
                Debug.LogWarning($"{LogTag} EnsureTitleColumn warning: {ex.Message}");
        }
    }

    #endregion

    #region Queries

    /// <summary>
    /// Mengambil semua conversation ID milik user, urut terbaru di atas.
    /// </summary>
    public IEnumerator FetchUserConversations(
        string userId,
        Action<string[]> onSuccess,
        Action<string> onError = null)
    {
        try
        {
            var ids = _db.Table<Conversation>()
                         .Where(c => c.UserId == userId)
                         .OrderByDescending(c => c.StartedAt)
                         .Select(c => c.Id)
                         .ToArray();
            onSuccess(ids);
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex.Message);
        }
        yield break;
    }

    /// <summary>
    /// Mengambil semua pesan untuk satu conversation (ascending by time).
    /// </summary>
    public IEnumerator FetchConversationWithMessages(
        string conversationId,
        string userId,
        Action<Message[]> onResult,
        Action<string> onError = null)
    {
        try
        {
            var msgs = _db.Table<Message>()
                          .Where(m => m.ConversationId == conversationId)
                          .OrderBy(m => m.SentAt)
                          .ToArray();
            onResult(msgs);
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex.Message);
        }
        yield break;
    }

    /// <summary>
    /// Ambil title yang tersimpan untuk percakapan.
    /// </summary>
    public string GetConversationTitle(string conversationId)
    {
        try
        {
            var c = _db.Find<Conversation>(conversationId);
            return c?.Title;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"{LogTag} GetConversationTitle: {ex.Message}");
            return null;
        }
    }

    #endregion

    #region Commands

    /// <summary>
    /// Insert satu baris ringkasan ke tabel summary.
    /// </summary>
    public IEnumerator InsertSummary(
        string conversationId,
        string summaryText,
        Action onSuccess = null,
        Action<string> onError = null)
    {
        Debug.Log($"{LogTag} InsertSummary: {conversationId}");
        try
        {
            var row = new Summary
            {
                Id             = Guid.NewGuid().ToString(),
                ConversationId = conversationId,
                SummaryText    = summaryText ?? "",
                CreatedAt      = DateTime.UtcNow
            };

            _db.Insert(row);
            onSuccess?.Invoke();
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex.Message);
        }
        yield break;
    }

    /// <summary>
    /// Insert record percakapan baru.
    /// </summary>
    public IEnumerator CreateConversation(
        string conversationId,
        string userId,
        Action onSuccess,
        Action<string> onError = null)
    {
        try
        {
            var convo = new Conversation
            {
                Id        = conversationId,
                UserId    = userId,
                StartedAt = DateTime.UtcNow,
                Title     = null
            };
            _db.Insert(convo);
            onSuccess?.Invoke();
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex.Message);
        }
        yield break;
    }

    /// <summary>
    /// Insert satu pesan.
    /// </summary>
    public IEnumerator InsertMessage(
        string conversationId,
        string sender,
        string content,
        Action onSuccess = null,
        Action<string> onError = null)
    {
        try
        {
            var now = DateTime.UtcNow;

            var msg = new Message
            {
                Id             = Guid.NewGuid().ToString(),
                ConversationId = conversationId,
                Sender         = sender,
                Text           = content,
                SentAt         = now
            };
            _db.Insert(msg);

            // Pakai started_at sebagai "last activity"
            _db.Execute(
                "UPDATE conversations SET started_at = ? WHERE id = ?",
                now,
                conversationId
            );

            onSuccess?.Invoke();
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex.Message);
        }
        yield break;
    }

    /// <summary>
    /// Update title conversation.
    /// </summary>
    public void UpdateConversationTitle(string conversationId, string title)
    {
        try
        {
            _db.Execute("UPDATE conversations SET title = ? WHERE id = ?", title, conversationId);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"{LogTag} UpdateConversationTitle: {ex.Message}");
        }
    }

    /// <summary>
    /// Hapus semua pesan untuk sekumpulan conversation IDs.
    /// </summary>
    public IEnumerator DeleteMessages(
        string[] convIds,
        Action onSuccess,
        Action<string> onError = null)
    {
        try
        {
            foreach (var id in convIds)
                _db.Execute("DELETE FROM messages WHERE conversation_id = ?", id);

            onSuccess?.Invoke();
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex.Message);
        }
        yield break;
    }

    /// <summary>
    /// Hapus semua pesan untuk satu conversation.
    /// </summary>
    public IEnumerator DeleteMessagesForConversation(
        string conversationId,
        Action onSuccess,
        Action<string> onError = null)
    {
        try
        {
            _db.Execute("DELETE FROM messages WHERE conversation_id = ?", conversationId);
            onSuccess?.Invoke();
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex.Message);
        }
        yield break;
    }

    /// <summary>
    /// Hapus semua percakapan milik user.
    /// </summary>
    public IEnumerator DeleteConversations(
        string userId,
        Action onSuccess,
        Action<string> onError = null)
    {
        try
        {
            _db.Execute("DELETE FROM conversations WHERE user_id = ?", userId);
            onSuccess?.Invoke();
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex.Message);
        }
        yield break;
    }

    /// <summary>
    /// Hapus satu percakapan.
    /// </summary>
    public IEnumerator DeleteConversation(
        string conversationId,
        Action onSuccess,
        Action<string> onError = null)
    {
        try
        {
            _db.Execute("DELETE FROM conversations WHERE id = ?", conversationId);
            onSuccess?.Invoke();
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex.Message);
        }
        yield break;
    }

    #endregion

    #region Composite

    /// <summary>
    /// Hapus semua pesan, lalu semua percakapan milik user.
    /// </summary>
    public IEnumerator DeleteAllChats(
        string userId,
        Action onSuccess,
        Action<string> onError = null)
    {
        yield return FetchUserConversations(
            userId,
            convIds =>
            {
                StartCoroutine(
                    DeleteMessages(
                        convIds,
                        () => StartCoroutine(DeleteConversations(userId, onSuccess, onError)),
                        onError
                    )
                );
            },
            onError
        );
    }

    #endregion
}