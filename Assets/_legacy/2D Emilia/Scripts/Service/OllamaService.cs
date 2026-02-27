using System.Collections;
using UnityEngine;
using UnityEngine.Networking;
using System.Text;
using System.Text.RegularExpressions;
using System;

public static class OllamaService
{
    public static IEnumerator SendPrompt(string prompt, Action<string> onComplete)
    {
        string url = "http://localhost:11434/api/generate";
        string jsonPayload = $"{{\"model\":\"llama3.2:latest\", \"prompt\":\"{EscapeJson(prompt)}\", \"stream\": true}}";

        UnityWebRequest request = new UnityWebRequest(url, "POST");
        byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonPayload);
        request.uploadHandler = new UploadHandlerRaw(bodyRaw);
        request.downloadHandler = new DownloadHandlerBuffer();
        request.SetRequestHeader("Content-Type", "application/json");

        yield return request.SendWebRequest();

        if (request.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError("❌ Ollama error: " + request.error);
            onComplete("Gagal mendapatkan respon dari AI.");
        }
        else
        {
            string rawText = request.downloadHandler.text;
            string fullResponse = ParseStreamedResponse(rawText);
            onComplete(fullResponse);
        }
    }

    private static string EscapeJson(string input)
    {
        return input.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    private static string ParseStreamedResponse(string raw)
    {
        StringBuilder result = new StringBuilder();
        MatchCollection matches = Regex.Matches(raw, "\"response\"\\s*:\\s*\"(.*?)\"", RegexOptions.Singleline);
        foreach (Match match in matches)
        {
            if (match.Groups.Count > 1)
            {
                string part = match.Groups[1].Value.Replace("\\n", "\n").Replace("\\\"", "\"");
                result.Append(part);
            }
        }
        return result.ToString();
    }
}