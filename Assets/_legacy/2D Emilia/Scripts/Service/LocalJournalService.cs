using System;
using System.Collections;
using System.Linq;
using SQLite;
using UnityEngine;
using EMILIA.Data;

public class LocalJournalService : MonoBehaviour
{
    #region Dependencies
    private SQLiteConnection _db;
    #endregion

    #region Unity
    private void Awake()
    {
        _db = DatabaseManager.Instance.DB;
        _db.Execute("PRAGMA foreign_keys = ON;");
    }
    #endregion

    #region Queries

    /// <summary>
    /// Ambil semua jurnal milik user, urut terbaru di atas (berdasarkan UpdatedAt lalu CreatedAt).
    /// </summary>
    public IEnumerator FetchUserJournals(
        string userId,
        Action<Journal[]> onSuccess,
        Action<string> onError = null
    )
    {
        try
        {
            var journals = _db.Table<Journal>()
                              .Where(j => j.UserId == userId)
                              .OrderByDescending(j => j.CreatedAt)
                              .ToArray();

            onSuccess?.Invoke(journals);
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex.Message);
        }

        yield break;
    }

    #endregion

    #region Commands

    /// <summary>
    /// Insert jurnal baru dan return objeknya.
    /// </summary>
    public IEnumerator CreateJournal(
        string userId,
        string title,
        string content,
        string createdAtIso,
        Action<Journal> onSuccess,
        Action<string> onError = null
    )
    {
        try
        {
            var ts = DateTime.Parse(createdAtIso);
            var journal = new Journal
            {
                Id        = Guid.NewGuid().ToString(),
                UserId    = userId,
                Title     = title,
                Content   = content,
                CreatedAt = ts
            };

            _db.Insert(journal);
            onSuccess?.Invoke(journal);
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex.Message);
        }

        yield break;
    }

    /// <summary>
    /// Update judul, konten, dan UpdatedAt.
    /// </summary>
    public IEnumerator UpdateJournal(
        string journalId,
        string newTitle,
        string newContent,
        string newUpdatedAtIso,
        Action onSuccess,
        Action<string> onError = null
    )
    {
        try
        {
            var journal = _db.Find<Journal>(journalId);
            if (journal == null)
            {
                onError?.Invoke($"Journal '{journalId}' not found");
                yield break;
            }

            journal.Title     = newTitle;
            journal.Content   = newContent;
            //journal.UpdatedAt = DateTime.Parse(newUpdatedAtIso);

            _db.Update(journal);
            onSuccess?.Invoke();
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex.Message);
        }

        yield break;
    }

    /// <summary>
    /// Hapus jurnal by ID.
    /// </summary>
    public IEnumerator DeleteJournal(
        string journalId,
        Action onSuccess,
        Action<string> onError = null
    )
    {
        try
        {
            _db.Execute("DELETE FROM journals WHERE id = ?", journalId);
            onSuccess?.Invoke();
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex.Message);
        }

        yield break;
    }

    /// <summary>
    /// Update sentiment label and AI comment in the journal table.
    /// </summary>
    public IEnumerator UpdateJournalSentiment(
        string journalId,
        string sentimentLabel,
        string aiComment,
        Action onSuccess,
        Action<string> onError = null
    )
    {
        try
        {
            var journal = _db.Find<Journal>(journalId);
            if (journal == null)
            {
                onError?.Invoke($"Journal '{journalId}' not found");
                yield break;
            }

            journal.SentimentLabel = sentimentLabel;
            journal.AIComment     = aiComment;

            _db.Update(journal);
            onSuccess?.Invoke();
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex.Message);
        }

        yield break;
    }

    #endregion
}