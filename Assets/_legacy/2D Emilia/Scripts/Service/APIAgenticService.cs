// APIAgenticService.cs
using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Client untuk endpoint POST /agentic (multipart/form-data)
/// Fields:
///  - user_id   (string)
///  - username  (string)
///  - question  (string)
///  - audio     (file, opsional)
/// Response JSON contoh:
/// {
///   "result": {
///     "reasoning": "....",
///     "response":  "....",
///     "summary":   "..." // opsional
///   }
/// }
/// </summary>
public class APIAgenticService : MonoBehaviour
{
    #region Singleton

    public static APIAgenticService Instance { get; private set; }
    private void Awake()
    {
        if (Instance == null) Instance = this;
        else if (Instance != this) Destroy(gameObject);
    }

    #endregion

    #region Inspector

    [Header("API")]
    [SerializeField] private string baseUrl    = "http://10.125.189.93:8000";
    [SerializeField] private string endpoint   = "/agentic";
    [SerializeField] private int    timeoutSec = 99999;

    #endregion

    #region Models

    [Serializable] public class AgenticResult { public string reasoning; public string response; public string summary; }
    [Serializable] private class Wrapper      { public AgenticResult result; }

    #endregion

    #region Public API

    public IEnumerator Send(
        string userId,
        string username,
        string question,
        byte[] audioBytes,
        string audioFileName,
        string audioMime,
        Action<AgenticResult> onSuccess,
        Action<string> onError = null)
    {
        var form = new WWWForm();
        form.AddField("user_id",  userId   ?? "");
        form.AddField("username", username ?? "");
        form.AddField("question", question ?? "");

        if (audioBytes != null && audioBytes.Length > 0)
        {
            var fname = string.IsNullOrEmpty(audioFileName) ? "audio.wav" : audioFileName;
            var mime  = string.IsNullOrEmpty(audioMime)     ? "application/octet-stream" : audioMime;
            form.AddBinaryData("audio", audioBytes, fname, mime);
        }

        using (var req = UnityWebRequest.Post($"{baseUrl}{endpoint}", form))
        {
            req.timeout = timeoutSec;
            yield return req.SendWebRequest();

#if UNITY_2020_2_OR_NEWER
            if (req.result != UnityWebRequest.Result.Success)
#else
            if (req.isNetworkError || req.isHttpError)
#endif
            { onError?.Invoke($"HTTP {(int)req.responseCode}: {req.error}"); yield break; }

            try
            {
                var raw    = req.downloadHandler.text;
                var parsed = JsonUtility.FromJson<Wrapper>(raw);
                var res    = parsed?.result;

                if (res == null || (string.IsNullOrWhiteSpace(res.reasoning) && string.IsNullOrWhiteSpace(res.response)))
                    onError?.Invoke("Payload agentic kosong / tidak valid.");
                else
                    onSuccess?.Invoke(res);
            }
            catch (Exception e)
            {
                onError?.Invoke($"Gagal parse JSON: {e.Message}");
            }
        }
    }

    /// <summary>Convenience tanpa audio.</summary>
    public IEnumerator Send(
        string userId,
        string username,
        string question,
        Action<AgenticResult> onSuccess,
        Action<string> onError = null)
        => Send(userId, username, question, null, null, null, onSuccess, onError);

    #endregion
}