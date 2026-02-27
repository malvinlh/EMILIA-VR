// using UnityEngine;
// using System;
// using System.Collections;

// [Serializable]
// public class Journal
// {
//     public string id;
//     public string user_id;
//     public string title;
//     public string content;
//     public string created_at;
//     public string updated_at;
// }

// [Serializable]
// public class JournalListWrapper
// {
//     public Journal[] items;
// }

// // “Payload” untuk PATCH
// [Serializable]
// public class UpdateJournalRequest
// {
//     public string title;
//     public string content;
//     public string updated_at;
// }

// [Serializable]
// public class CreateJournalRequest
// {
//     public string id;
//     public string user_id;
//     public string title;
//     public string content;
//     public string created_at;
//     public string updated_at;
// }

// /// <summary>
// /// Supabase client untuk tabel journals.
// /// </summary>
// public class JournalService : SupabaseHttpClient
// {
//     const string Table = "journals";

//     /// <summary>
//     /// Ambil semua jurnal milik user ini, order terbaru di atas.
//     /// </summary>
//     public IEnumerator FetchUserJournals(
//         string userId,
//         Action<Journal[]> onSuccess,
//         Action<string> onError = null
//     )
//     {
//         // GET /rest/v1/journals?select=*&user_id=eq.{userId}&order=created_at.desc
//         string path =
//             $"{Table}?select=*&user_id=eq.{Uri.EscapeDataString(userId)}" +
//             "&order=created_at.desc";
//         Debug.Log($"[JournalService] FetchUserJournals → {path}");

//         yield return StartCoroutine(
//             SendRequest(
//                 path:      path,
//                 method:    "GET",
//                 bodyJson:  null,
//                 onSuccess: resp =>
//                 {
//                     // bungkus jadi { "items": [...] }
//                     var wrapped = $"{{\"items\":{resp}}}";
//                     var list    = JsonUtility.FromJson<JournalListWrapper>(wrapped);
//                     onSuccess(list.items ?? Array.Empty<Journal>());
//                 },
//                 onError: err => onError?.Invoke(err)
//             )
//         );
//     }

//     /// <summary>
//     /// Create a brand-new journal row.
//     /// </summary>
//     public IEnumerator CreateJournal(
//         string userId,
//         string title,
//         string content,
//         string createdAtIso,    // in "yyyy-MM-ddTHH:mm:ss" UTC or local as you prefer
//         Action<Journal> onSuccess,
//         Action<string> onError = null
//     )
//     {
//         var payload = new CreateJournalRequest {
//             id         = Guid.NewGuid().ToString(),
//             user_id    = userId,
//             title      = title,
//             content    = content,
//             created_at = createdAtIso,
//             updated_at = createdAtIso
//         };
//         string json = "[" + JsonUtility.ToJson(payload) + "]";

//         string path = "journals";
//         Debug.Log($"[JournalService] CreateJournal → {path}  BODY: {json}");

//         yield return StartCoroutine(
//             SendRequest(
//                 path:         path,
//                 method:       "POST",
//                 bodyJson:     json,
//                 onSuccess:    respJson =>
//                 {
//                     // expectation: return=representation ⇒ respJson is array with the created object
//                     var wrapped = $"{{\"items\":{respJson}}}";
//                     var list    = JsonUtility.FromJson<JournalListWrapper>(wrapped);
//                     if (list.items != null && list.items.Length > 0)
//                         onSuccess(list.items[0]);
//                     else
//                         onError?.Invoke("Malformed response");
//                 },
//                 onError:      err => onError?.Invoke(err),
//                 preferHeader: "return=representation"
//             )
//         );
//     }

//     /// <summary>
//     /// Update title, content & updated_at pada journal ini.
//     /// </summary>
//     public IEnumerator UpdateJournal(
//         string journalId,
//         string newTitle,
//         string newContent,
//         string newUpdatedAt,
//         Action onSuccess,
//         Action<string> onError = null
//     )
//     {
//         var payload = new UpdateJournalRequest {
//             title      = newTitle,
//             content    = newContent,
//             updated_at = newUpdatedAt
//         };
//         string json = JsonUtility.ToJson(payload);

//         // PATCH /journals?id=eq.{journalId}
//         string path = $"{Table}?id=eq.{Uri.EscapeDataString(journalId)}";
//         Debug.Log($"[JournalService] UpdateJournal → {path} BODY {json}");

//         yield return StartCoroutine(
//             SendRequest(
//                 path:         path,
//                 method:       "PATCH",
//                 bodyJson:     json,
//                 onSuccess:    _ => onSuccess.Invoke(),
//                 onError:      err => onError?.Invoke(err),
//                 preferHeader: "return=representation"
//             )
//         );
//     }

//     /// <summary>
//     /// Hapus 1 jurnal berdasarkan ID.
//     /// DELETE /journals?id=eq.{journalId}
//     /// </summary>
//     public IEnumerator DeleteJournal(
//         string journalId,
//         Action onSuccess,
//         Action<string> onError = null
//     )
//     {
//         string path = $"{Table}?id=eq.{Uri.EscapeDataString(journalId)}";
//         Debug.Log($"[JournalService] DeleteJournal → {path}");
//         yield return StartCoroutine(
//             SendRequest(
//                 path:      path,
//                 method:    "DELETE",
//                 bodyJson:  null,
//                 onSuccess: _ => onSuccess?.Invoke(),
//                 onError:   err => onError?.Invoke(err),
//                 preferHeader: "return=representation"
//             )
//         );
//     }
// }