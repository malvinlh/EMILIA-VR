// APITopicService.cs
using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Client untuk endpoint POST /topic
/// Content-Type: application/x-www-form-urlencoded
/// Body: user=<userText>&bot=<botText>
/// Response JSON: { "response": "<judul singkat>" }
/// </summary>
public class APITopicService : MonoBehaviour
{
    #region Singleton

    public static APITopicService Instance { get; private set; }

    private void Awake()
    {
        if (Instance == null) Instance = this;
        else if (Instance != this) Destroy(gameObject);
    }

    #endregion

    #region Inspector

    [Header("API")]
    [SerializeField] private string baseUrl    = "http://10.125.189.93:8000";
    [SerializeField] private string endpoint   = "/topic";
    [SerializeField] private int    timeoutSec = 15;

    #endregion

    #region Data Models

    [Serializable]
    private class TopicResponse
    {
        public string response;
    }

    #endregion

    #region Public API

    /// <summary>
    /// Generate judul topik dari pair user+bot.
    /// </summary>
    public IEnumerator GetTopic(
        string userText,
        string botText,
        Action<string> onSuccess,
        Action<string> onError = null)
    {
        // Build x-www-form-urlencoded payload
        var payload = EncodeForm(new (string key, string value)[] {
            ("user", userText ?? ""),
            ("bot",  botText  ?? "")
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
                var parsed = JsonUtility.FromJson<TopicResponse>(req.downloadHandler.text);
                var topic  = parsed?.response?.Trim();
                if (string.IsNullOrEmpty(topic))
                {
                    onError?.Invoke("Topic kosong / tidak valid.");
                }
                else
                {
                    onSuccess?.Invoke(topic);
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