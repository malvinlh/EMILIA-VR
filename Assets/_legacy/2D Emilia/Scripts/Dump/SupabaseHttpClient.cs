using UnityEngine;
using UnityEngine.Networking;
using System.Collections;
using System.Text;

public class SupabaseHttpClient : MonoBehaviour
{
    // — Ganti jika perlu dengan URL + Keys milikmu —
    protected const string BaseUrl = "https://ocpnfpnkxgkmplhrnbtt.supabase.co";
    protected const string ApiKey  = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6Im9jcG5mcG5reGdrbXBsaHJuYnR0Iiwicm9sZSI6ImFub24iLCJpYXQiOjE3NTA0NDU4NTEsImV4cCI6MjA2NjAyMTg1MX0.VvdGQrAXD14-XfAQcmYqfrK9Y4Gux72AAPPoJMhPx7s";
    protected const string AuthKey = "Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6Im9jcG5mcG5reGdrbXBsaHJuYnR0Iiwicm9sZSI6ImFub24iLCJpYXQiOjE3NTA0NDU4NTEsImV4cCI6MjA2NjAyMTg1MX0.VvdGQrAXD14-XfAQcmYqfrK9Y4Gux72AAPPoJMhPx7s";

    /// <summary>
    /// Generic HTTP request to Supabase REST API.
    /// </summary>
    protected IEnumerator SendRequest(
        string path,               // e.g. "users?on_conflict=name"
        string method,             // GET, POST, PATCH, DELETE
        string bodyJson,           // JSON payload (array for POST/PATCH) or null
        System.Action<string> onSuccess,
        System.Action<string> onError,
        string preferHeader = null // e.g. "resolution=merge-duplicates" or "return=representation"
    ) {
        string url = $"{BaseUrl}/rest/v1/{path}";
        using var req = new UnityWebRequest(url, method);

        if (!string.IsNullOrEmpty(bodyJson)) {
            byte[] data = Encoding.UTF8.GetBytes(bodyJson);
            req.uploadHandler   = new UploadHandlerRaw(data);
            req.SetRequestHeader("Content-Type", "application/json");
        }

        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("apikey",       ApiKey);
        req.SetRequestHeader("Authorization", AuthKey);
        if (!string.IsNullOrEmpty(preferHeader))
            req.SetRequestHeader("Prefer", preferHeader);

        yield return req.SendWebRequest();

        if (req.result == UnityWebRequest.Result.Success && req.responseCode < 400) {
            onSuccess?.Invoke(req.downloadHandler.text);
        } else {
            onError?.Invoke(req.downloadHandler.text);
        }
    }
}