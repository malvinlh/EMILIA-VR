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
        // === STUB MODE: scene + input-method auto-routed ==============
        // EMILIA does not know which scene she is in, the time of day, or
        // whether the user typed or spoke. The reply text must stay neutral.
        var sm = ServiceManager.Instance;
        var scene = sm != null ? sm.CurrentStubScene : ServiceManager.StubScene.Beach;
        bool voiceMode = sm != null && sm.PendingChatVoiceFlag;
        if (sm != null) sm.PendingChatVoiceFlag = false;
        Debug.Log($"[STUB] APIChatService.SendPrompt scene={scene} voiceMode={voiceMode}");

        yield return new WaitForSeconds(7f);

        string reply;
        if (scene == ServiceManager.StubScene.Bedroom && voiceMode)
            reply = "Pertanyaan yang penting, [nickname_saat_login]. Buatku, langkah pertama biasanya bukan mencari cara untuk menghilangkannya, tapi memberi ruang untuk perasaan itu hadir dulu. Coba mulai dengan menamai apa yang kamu rasakan, lalu pelan-pelan cari kegiatan kecil yang bikin lebih ringan, entah jalan kaki, menulis, atau ngobrol seperti ini. Mau ceritain dulu, sedih yang lagi kamu rasakan ini tentang apa?";
        else if (scene == ServiceManager.StubScene.Bedroom)
            reply = "Hai [nickname_saat_login]! Namaku Emilia. Senang bisa bertemu hari ini denganmu. Apa yang lagi bikin kamu merasa perlu ngobrol? Ceritain sedikit, yuk.";
        else if (voiceMode)
            reply = "Hai [nickname_saat_login], aku turut sedih mendengarnya. Kucing yang setia menemani punya tempat khusus di hati, dan rasa kehilanganmu sah untuk hadir. Ceritain dulu sedikit tentang kucingmu, ya, aku siap mendengarkan.";
        else
            reply = "Halo [nickname_saat_login], aku di sini. Tidak perlu buru-buru. Apa yang masih menggantung di pikiranmu?";

        string nickname = string.IsNullOrEmpty(username) ? "kamu" : username;
        reply = reply.Replace("[nickname_saat_login]", nickname);

        onSuccess?.Invoke(reply);
        yield break;
        // ==============================================================

        /*  ORIGINAL HTTP IMPLEMENTATION, kept for re-enable
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
        */
    }

    #endregion
}