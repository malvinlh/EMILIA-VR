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
        // === STUB MODE: scene + journal-index auto-routed =============
        var sm = ServiceManager.Instance;
        var scene = sm != null ? sm.CurrentStubScene : ServiceManager.StubScene.Beach;
        int journalIndex = sm != null ? sm.JournalIndexInScene : 0;
        bool sad = journalIndex >= 1;
        Debug.Log($"[STUB] APISentimentService.AnalyzeJournal scene={scene} journalIndex={journalIndex} sad={sad}");

        yield return new WaitForSeconds(7f);

        SentimentResponse result;
        if (scene == ServiceManager.StubScene.Bedroom && sad)
        {
            result = new SentimentResponse
            {
                decision = "buang",
                tone     = "negatif",
                reason   = "Kamu sudah berani menuangkan beban itu ke halaman ini, dan itu sudah merupakan langkah. Biarkan halaman ini yang menyimpannya, bukan kepalamu. Lepaskan, ya."
            };
        }
        else if (scene == ServiceManager.StubScene.Bedroom)
        {
            result = new SentimentResponse
            {
                decision = "simpan",
                tone     = "positif",
                reason   = "Kamu memberi ruang untuk rasa cukup hari ini, dan rasa seperti itu tidak datang setiap hari. Simpan tulisan ini, supaya kelak kamu bisa diingatkan bahwa rasa cukup itu pernah hadir."
            };
        }
        else if (sad)
        {
            result = new SentimentResponse
            {
                decision = "buang",
                tone     = "negatif",
                reason   = "Kamu sudah berani menuangkan pikiran yang berat ini ke dalam tulisan, dan langkah itu sendiri sudah keberanian. Untuk yang sudah terlalu lama berputar, biarkan lepas saja. Kamu sudah cukup membawanya hari ini."
            };
        }
        else
        {
            result = new SentimentResponse
            {
                decision = "simpan",
                tone     = "positif",
                reason   = "Kamu sudah meluangkan waktu untuk menangkap momen kecil yang menenangkanmu hari ini, dan itu menunjukkan kamu peduli dengan dirimu sendiri. Simpan tulisan ini, supaya suatu hari nanti kamu bisa kembali ke rasa yang sama saat kamu paling membutuhkannya."
            };
        }

        onSuccess?.Invoke(result);
        if (sm != null) sm.NotifyJournalSentimentConsumed();
        yield break;
        // ==============================================================

        /*  ORIGINAL HTTP IMPLEMENTATION, kept for re-enable
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
        */
    }

    #endregion
}
