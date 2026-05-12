// APISummaryService.cs
using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Client untuk endpoint POST /summary
/// Content-Type: application/x-www-form-urlencoded
/// Body: conv_id=<conversationId>
/// Response JSON: { "response": "<teks ringkasan>" }
/// </summary>
public class APISummaryService : MonoBehaviour
{
    #region Singleton

    public static APISummaryService Instance { get; private set; }

    private void Awake()
    {
        if (Instance == null) Instance = this;
        else if (Instance != this) Destroy(gameObject);
    }

    #endregion

    #region Inspector

    [Header("API")]
    [SerializeField] private string baseUrl    = "http://192.168.31.71:8000";
    [SerializeField] private string endpoint   = "/summary";
    [SerializeField] private int    timeoutSec = 20;

    #endregion

    #region Data Models

    [Serializable]
    private class SummaryResponse
    {
        public string response;
    }

    #endregion

    #region Public API

    /// <summary>
    /// Kirim request summary untuk sebuah conversation.
    /// </summary>
    public IEnumerator RequestSummary(
        string conversationId,
        Action<string> onSuccess,
        Action<string> onError = null)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            onError?.Invoke("conversationId kosong.");
            yield break;
        }

        // Build x-www-form-urlencoded payload
        var payload = EncodeForm(new (string key, string value)[] {
            ("conv_id", conversationId)
        });

        var url = $"{baseUrl}{endpoint}";
        using (var req = BuildPost(url, payload, "application/x-www-form-urlencoded", timeoutSec))
        {
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
                var parsed = JsonUtility.FromJson<SummaryResponse>(req.downloadHandler.text);
                var text   = parsed?.response?.Trim();
                if (string.IsNullOrEmpty(text))
                {
                    onError?.Invoke("Summary kosong / invalid.");
                }
                else
                {
                    onSuccess?.Invoke(text);
                }
            }
            catch (Exception e)
            {
                onError?.Invoke($"Gagal parse JSON: {e.Message}");
            }
        }
    }

    #endregion

    #region Helpers

    private static byte[] EncodeForm((string key, string value)[] fields)
    {
        string Enc(string s) => Uri.EscapeDataString(s ?? "");
        var sb = new StringBuilder();
        for (int i = 0; i < fields.Length; i++)
        {
            if (i > 0) sb.Append('&');
            sb.Append(Enc(fields[i].key));
            sb.Append('=');
            sb.Append(Enc(fields[i].value));
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static UnityWebRequest BuildPost(string url, byte[] body, string contentType, int timeoutSec)
    {
        var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST)
        {
            uploadHandler   = new UploadHandlerRaw(body),
            downloadHandler = new DownloadHandlerBuffer(),
            timeout         = timeoutSec
        };
        req.SetRequestHeader("Content-Type", contentType);
        return req;
    }

    #endregion
}