// APITranscribeService.cs
using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

public class APITranscribeService : MonoBehaviour
{
    #region Singleton
    public static APITranscribeService Instance { get; private set; }
    private void Awake()
    {
        if (Instance == null) Instance = this;
        else if (Instance != this) Destroy(gameObject);
    }
    #endregion

    #region Inspector
    [Header("API")]
    [SerializeField] private string baseUrl    = "http://192.168.31.71:8000";
    [SerializeField] private string endpoint   = "/transcribe";
    [SerializeField] private int    timeoutSec = 99999;
    #endregion

    #region Models
    [Serializable] private class RespResponse  { public string response; }
    [Serializable] private class RespText      { public string text; }
    [Serializable] private class RespTranscript{ public string transcript; }
    #endregion

    #region Public API

    public IEnumerator Transcribe(
        byte[] audioBytes,
        string audioFileName,
        string audioMime,
        Action<string> onSuccess,
        Action<string> onError = null)
    {
        // === STUB MODE: chat-vs-journal + scene auto-routed ===========
        // Audio is still recorded on-device (user behavior preserved on camera);
        // the bytes are simply ignored here and a scene/context-appropriate
        // placeholder transcript is returned.
        var sm = ServiceManager.Instance;
        var scene = sm != null ? sm.CurrentStubScene : ServiceManager.StubScene.Beach;
        bool inJournaling = JournalSessionManager.Instance != null
                            && JournalSessionManager.Instance.CurrentState
                               == JournalSessionManager.SessionState.Journaling;
        int journalIndex = sm != null ? sm.JournalIndexInScene : 0;
        float stubDelay = inJournaling ? 4f : 1f;
        Debug.Log($"[STUB] APITranscribeService.Transcribe scene={scene} inJournaling={inJournaling} journalIndex={journalIndex} delay={stubDelay}s");

        yield return new WaitForSeconds(stubDelay);

        string transcript;
        if (inJournaling)
        {
            bool sad = journalIndex >= 1;
            if (scene == ServiceManager.StubScene.Bedroom && sad)
                transcript = "Aku tidak bisa tidur lagi karena pikiranku terus mengulang hal-hal yang seharusnya tidak penting lagi. Aku menulis ini supaya akhirnya ada yang menampungnya selain pikiranku.";
            else if (scene == ServiceManager.StubScene.Bedroom)
                transcript = "Malam ini cukup melelahkan, tetapi anehnya aku merasa puas. Aku menyelesaikan satu hal yang sudah lama aku tunda. Untuk sekali ini aku merasa cukup dengan diriku sendiri, dan itu rasanya sangat tenang.";
            else if (sad)
                transcript = "Aku duduk di pasir tapi pikiranku tidak ikut tenang seperti biasanya. Ada percakapan yang terus berputar di kepalaku, kata-kata yang seharusnya tidak perlu lagi kuingat.";
            else
                transcript = "Hari ini aku berjalan di tepi pantai dan langit berwarna jingga seperti lukisan. Aku duduk lama menatap ombak sampai napasku ikut tenang. Aku tidak ingin lupa rasanya hari ini.";
        }
        else
        {
            // Chat-voice path: also signal APIChatService to use the voice reply flavour.
            if (sm != null) sm.PendingChatVoiceFlag = true;

            if (scene == ServiceManager.StubScene.Bedroom)
                transcript = "Bagaimana cara yang baik untuk mengelola perasaan sedih?";
            else
                transcript = "Hai Emilia, aku merasa sedih karena kucingku tadi hilang.";
        }

        onSuccess?.Invoke(transcript);
        yield break;
        // ==============================================================

        /*  ORIGINAL HTTP IMPLEMENTATION, kept for re-enable
        if (audioBytes == null || audioBytes.Length == 0)
        { onError?.Invoke("Audio kosong."); yield break; }

        var form  = new WWWForm();
        var fname = string.IsNullOrEmpty(audioFileName) ? "audio.wav" : audioFileName;
        var mime  = string.IsNullOrEmpty(audioMime)     ? "audio/wav"  : audioMime;
        form.AddBinaryData("audio", audioBytes, fname, mime);

        using (var req = UnityWebRequest.Post($"{baseUrl}{endpoint}", form))
        {
            req.timeout = timeoutSec;
            req.chunkedTransfer = false;
            req.SetRequestHeader("Accept", "application/json");

            yield return req.SendWebRequest();

#if UNITY_2020_2_OR_NEWER
            if (req.result != UnityWebRequest.Result.Success)
#else
            if (req.isNetworkError || req.isHttpError)
#endif
            { onError?.Invoke($"HTTP {(int)req.responseCode}: {req.error}"); yield break; }

            var raw = req.downloadHandler.text;
            if (string.IsNullOrWhiteSpace(raw))
            { onError?.Invoke("Empty body"); yield break; }

            // 1) Bentuk utama: { "response": "..." }
            try {
                var r = JsonUtility.FromJson<RespResponse>(raw);
                if (!string.IsNullOrWhiteSpace(r?.response)) { onSuccess?.Invoke(r.response.Trim()); yield break; }
            } catch { }

            // 2) Alternatif: { "text": "..." }
            try {
                var r = JsonUtility.FromJson<RespText>(raw);
                if (!string.IsNullOrWhiteSpace(r?.text)) { onSuccess?.Invoke(r.text.Trim()); yield break; }
            } catch { }

            // 3) Alternatif: { "transcript": "..." }
            try {
                var r = JsonUtility.FromJson<RespTranscript>(raw);
                if (!string.IsNullOrWhiteSpace(r?.transcript)) { onSuccess?.Invoke(r.transcript.Trim()); yield break; }
            } catch { }

            // 4) Kalau server kirim plain text (tanpa JSON)
            if (!raw.TrimStart().StartsWith("{"))
            { onSuccess?.Invoke(raw.Trim()); yield break; }

            onError?.Invoke("Format respons transcribe tidak dikenal.");
        }
        */
    }

    public IEnumerator TranscribeFile(
        string filePath,
        Action<string> onSuccess,
        Action<string> onError = null)
    {
        if (!System.IO.File.Exists(filePath))
        { onError?.Invoke($"File tidak ditemukan: {filePath}"); yield break; }

        var bytes = System.IO.File.ReadAllBytes(filePath);
        var fname = System.IO.Path.GetFileName(filePath);
        yield return Transcribe(bytes, fname, "audio/wav", onSuccess, onError);
    }

    #endregion
}