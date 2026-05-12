// APIChatService.cs
using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Client untuk endpoint POST /chat
/// Content-Type: multipart/form-data
/// Fields:
///  - username (string)
///  - question (string)
///  - audio (file, opsional)
/// Response JSON: { "response": "<jawaban ai>" }
/// </summary>
public class APIChatService : MonoBehaviour
{
    #region Singleton

    public static APIChatService Instance { get; private set; }

    private void Awake()
    {
        if (Instance == null) Instance = this;
        else if (Instance != this) Destroy(gameObject);
    }

    #endregion

    #region Inspector

    [Header("API")]
    [SerializeField] private string baseUrl    = "http://192.168.31.71:8000";
    [SerializeField] private string endpoint   = "/chat";
    [SerializeField] private int    timeoutSec = 30;

    #endregion

    #region Data Models

    [Serializable]
    private class ChatResponse
    {
        public string response;
    }

    #endregion

    #region Public API

    /// <summary>
    /// Convenience overload tanpa audio.
    /// </summary>
    public IEnumerator SendPrompt(
        string username,
        string question,
        Action<string> onSuccess,
        Action<string> onError = null)
    {
        return SendPrompt(username, question, null, null, null, onSuccess, onError);
    }

    /// <summary>
    /// Kirim prompt ke /chat (audio opsional).
    /// </summary>
    public IEnumerator SendPrompt(
        string username,
        string question,
        byte[] audioBytes,
        string audioFileName,
        string audioMime,
        Action<string> onSuccess,
        Action<string> onError = null)
    {
        var form = new WWWForm();
        form.AddField("username", username ?? "");
        form.AddField("question", question ?? "");

        if (audioBytes != null && audioBytes.Length > 0)
        {
            var fname = string.IsNullOrEmpty(audioFileName) ? "audio.wav" : audioFileName;
            var mime  = string.IsNullOrEmpty(audioMime) ? "application/octet-stream" : audioMime;
            form.AddBinaryData("audio", audioBytes, fname, mime);
        }

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
                var parsed = JsonUtility.FromJson<ChatResponse>(req.downloadHandler.text);
                var text   = parsed?.response?.Trim();
                if (string.IsNullOrEmpty(text))
                {
                    onError?.Invoke("Response kosong / tidak valid.");
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
}