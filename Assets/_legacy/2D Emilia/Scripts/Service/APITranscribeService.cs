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
    [SerializeField] private string baseUrl    = "http://192.168.31.69:8000";
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
            } catch { /* ignore */ }

            // 2) Alternatif: { "text": "..." }
            try {
                var r = JsonUtility.FromJson<RespText>(raw);
                if (!string.IsNullOrWhiteSpace(r?.text)) { onSuccess?.Invoke(r.text.Trim()); yield break; }
            } catch { /* ignore */ }

            // 3) Alternatif: { "transcript": "..." }
            try {
                var r = JsonUtility.FromJson<RespTranscript>(raw);
                if (!string.IsNullOrWhiteSpace(r?.transcript)) { onSuccess?.Invoke(r.transcript.Trim()); yield break; }
            } catch { /* ignore */ }

            // 4) Kalau server kirim plain text (tanpa JSON)
            if (!raw.TrimStart().StartsWith("{"))
            { onSuccess?.Invoke(raw.Trim()); yield break; }

            onError?.Invoke("Format respons transcribe tidak dikenal.");
        }
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