using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Client untuk endpoint POST /sentiment
/// Content-Type: multipart/form-data
/// Fields:
///  - journal (string)
///
/// Response JSON:
/// {
///   "decision": "simpan" | "buang",
///   "reason": "<komentar AI>",
///   "tone": "positif" | "negatif"
/// }
/// </summary>
public class APISentimentService : MonoBehaviour
{
    #region Singleton

    public static APISentimentService Instance { get; private set; }

    private void Awake()
    {
        if (Instance == null) Instance = this;
        else if (Instance != this) Destroy(gameObject);
    }

    #endregion

    #region Inspector

    [Header("API")]
    [SerializeField] private string baseUrl    = "http://192.168.31.71:8000";
    [SerializeField] private string endpoint   = "/sentiment";
    [SerializeField] private int    timeoutSec = 30;

    #endregion

    #region Data Models

    [Serializable]
    public class SentimentResponse
    {
        public string decision; // "simpan" | "buang"
        public string reason;   // AIComment
        public string tone;     // "positif" | "negatif"
    }

    #endregion

    #region Public API

    /// <summary>
    /// Kirim konten jurnal ke endpoint /sentiment untuk dianalisis AI.
    /// </summary>
    /// <param name="journalContent">Isi jurnal (string).</param>
    /// <param name="onSuccess">Callback jika berhasil, berisi SentimentResponse.</param>
    /// <param name="onError">Callback jika gagal.</param>
    public IEnumerator AnalyzeJournal(
        string journalContent,
        Action<SentimentResponse> onSuccess,
        Action<string> onError = null)
    {
        if (string.IsNullOrWhiteSpace(journalContent))
        {
            onError?.Invoke("Journal content kosong.");
            yield break;
        }

        var form = new WWWForm();
        form.AddField("journal", journalContent);

        var url = $"{baseUrl}{endpoint}";
        using (var req = UnityWebRequest.Post(url, form))
        {
            req.timeout = timeoutSec;

            yield return req.SendWebRequest();

#if UNITY_2020_2_OR_NEWER
            if (req.result != UnityWebRequest.Result.Success)
#else
            if (req.isNetworkError || req.isHttpError)
#endif
            {
                onError?.Invoke($"HTTP {(int)req.responseCode}: {req.error}");
                yield break;
            }

            try
            {
                var json = req.downloadHandler.text;
                var parsed = JsonUtility.FromJson<SentimentResponse>(json);

                if (parsed == null ||
                    string.IsNullOrEmpty(parsed.decision) ||
                    string.IsNullOrEmpty(parsed.reason) ||
                    string.IsNullOrEmpty(parsed.tone))
                {
                    onError?.Invoke("Response AI tidak lengkap atau tidak valid.");
                }
                else
                {
                    onSuccess?.Invoke(parsed);
                }
            }
            catch (Exception e)
            {
                onError?.Invoke($"Gagal parse JSON: {e.Message}");
            }
        }
    }

    #endregion
}
