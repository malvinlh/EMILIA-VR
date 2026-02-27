using System;
using System.Collections;
using SQLite;
using UnityEngine;
using EMILIA.Data;

public class LocalUserService : MonoBehaviour
{
    #region Dependencies

    private SQLiteConnection _database;

    #endregion

    #region Constants

    private const string FullNameMismatchMessage =
        "Masukkan nama lengkap yang sama dengan yang Anda gunakan saat pertama kali mendaftar.";

    #endregion

    #region Unity Callbacks

    private void Awake()
    {
        _database = DatabaseManager.Instance.DB;
    }

    #endregion

    #region Public API

    /// <summary>
    /// Inserts a new user or updates an existing one, keyed by nickname.
    /// Mirrors the Supabase UpsertUser signature.
    /// </summary>
    public IEnumerator UpsertUser(
        string nickname,
        string fullName,
        Action onSuccess,
        Action<string> onError
    )
    {
        try
        {
            var existing         = _database.Find<User>(nickname);
            var normalizedName   = NormalizeFullName(fullName);

            // If a full name was previously set, require the same name now
            if (existing != null
                && existing.Username != null
                && existing.Username != normalizedName)
            {
                onError?.Invoke(FullNameMismatchMessage);
                yield break;
            }

            if (existing == null)
                CreateUser(nickname, normalizedName);
            else
                UpdateUser(existing, normalizedName);

            onSuccess?.Invoke();
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex.Message);
        }

        yield break;
    }

    #endregion

    #region Helpers

    private static string NormalizeFullName(string fullName) =>
        string.IsNullOrWhiteSpace(fullName) ? null : fullName;

    private void CreateUser(string nickname, string normalizedFullName)
    {
        var newUser = new User
        {
            Id        = nickname,
            Name      = nickname,
            Username  = normalizedFullName,
            CreatedAt = DateTime.UtcNow
        };
        _database.Insert(newUser);
    }

    private void UpdateUser(User existingUser, string normalizedFullName)
    {
        existingUser.Username = normalizedFullName;
        _database.Update(existingUser);
    }

    #endregion
}