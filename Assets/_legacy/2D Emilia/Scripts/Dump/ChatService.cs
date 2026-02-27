// // ChatService.cs
// using UnityEngine;
// using System;
// using System.Collections;

// [Serializable]
// public class InsertRequest
// {
//     public string id;
//     public string conversation_id;
//     public string sender;
//     public string message;
//     public string sent_at;
// }

// [Serializable]
// public class ConversationUpsert
// {
//     public string id;
//     public string user_id;
//     public string started_at;
// }

// [Serializable]
// public class ConversationIdWrapper
// {
//     public string id;
// }

// [Serializable]
// public class ConversationIdListWrapper
// {
//     public ConversationIdWrapper[] items;
// }

// [Serializable]
// public class Message
// {
//     public string id;
//     public string conversation_id;
//     public string sender;
//     public string message;
//     public string sent_at;
// }

// [Serializable]
// public class Conversation
// {
//     public string id;
//     public string user_id;
//     public string started_at;
//     public string ended_at;
//     public Message[] messages;
// }

// [Serializable]
// public class ConversationListWrapper
// {
//     public Conversation[] items;
// }

// [Serializable]
// public class SnippetWrapper
// {
//     public string message;
// }

// [Serializable]
// public class SnippetListWrapper
// {
//     public SnippetWrapper[] items;
// }

// /// <summary>
// /// Semua operasi REST untuk tabel `conversations` & `messages`.
// /// </summary>
// public class ChatService : SupabaseHttpClient
// {
//     const string ConvTable = "conversations";
//     const string MsgTable  = "messages";

//     /// <summary>
//     /// 1) Ambil semua conversation.id untuk this user
//     /// 2) Order by started_at desc
//     /// </summary>
//     public IEnumerator FetchUserConversations(
//         string userId,
//         Action<string[]> onSuccess,
//         Action<string> onError = null
//     )
//     {
//         string path = $"{ConvTable}?select=id&user_id=eq.{userId}&order=started_at.desc";
//         Debug.Log($"[ChatService] FetchUserConversations → {path}");
//         yield return StartCoroutine(
//             SendRequest(
//                 path:      path,
//                 method:    "GET",
//                 bodyJson:  null,
//                 onSuccess: resp =>
//                 {
//                     var wrapped = $"{{\"items\":{resp}}}";
//                     var list    = JsonUtility.FromJson<ConversationIdListWrapper>(wrapped);
//                     if (list.items != null)
//                     {
//                         var ids = new string[list.items.Length];
//                         for (int i = 0; i < ids.Length; i++)
//                             ids[i] = list.items[i].id;
//                         onSuccess(ids);
//                     }
//                     else
//                     {
//                         onSuccess(Array.Empty<string>());
//                     }
//                 },
//                 onError: err => onError?.Invoke(err)
//             )
//         );
//     }

//     /// <summary>
//     /// Create a brand-new conversation (no upsert).
//     /// </summary>
//     public IEnumerator CreateConversation(
//         string conversationId,
//         string userId,
//         Action onSuccess,
//         Action<string> onError
//     )
//     {
//         Debug.Log($"[ChatService] CreateConversation id={conversationId} user_id={userId}");
//         var payload = new ConversationUpsert
//         {
//             id         = conversationId,
//             user_id    = userId,
//             started_at = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss")
//         };
//         string json = "[" + JsonUtility.ToJson(payload) + "]";

//         yield return StartCoroutine(
//             SendRequest(
//                 path:         ConvTable,
//                 method:       "POST",
//                 bodyJson:     json,
//                 onSuccess:    _ => onSuccess?.Invoke(),
//                 onError:      err => onError?.Invoke(err),
//                 preferHeader: "return=representation"
//             )
//         );
//     }

//     /// <summary>
//     /// Fetch satu conversation + semua pesannya, filter by user_id.
//     /// </summary>
//     public IEnumerator FetchConversationWithMessages(
//         string conversationId,
//         string userId,
//         Action<Message[]> onResult,
//         Action<string> onError = null
//     )
//     {
//         string path =
//             $"{ConvTable}?select=*,messages(*)" +
//             $"&id=eq.{conversationId}&user_id=eq.{userId}";
//         Debug.Log($"[ChatService] FetchConversationWithMessages → {path}");
//         yield return StartCoroutine(
//             SendRequest(
//                 path:      path,
//                 method:    "GET",
//                 bodyJson:  null,
//                 onSuccess: resp =>
//                 {
//                     var wrapped = $"{{\"items\":{resp}}}";
//                     var w       = JsonUtility.FromJson<ConversationListWrapper>(wrapped);
//                     if (w.items != null && w.items.Length > 0)
//                         onResult(w.items[0].messages);
//                     else
//                         onResult(Array.Empty<Message>());
//                 },
//                 onError: err => onError?.Invoke(err)
//             )
//         );
//     }

//     /// <summary>
//     /// Insert satu message baru ke tabel `messages`.
//     /// </summary>
//     public IEnumerator InsertMessage(
//         string conversationId,
//         string sender,
//         string content,
//         Action onSuccess = null,
//         Action<string> onError = null
//     )
//     {
//         Debug.Log($"[ChatService] InsertMessage convo={conversationId}, sender={sender}");
//         if (string.IsNullOrEmpty(content))
//         {
//             onError?.Invoke("Empty content");
//             yield break;
//         }

//         var req = new InsertRequest
//         {
//             id              = Guid.NewGuid().ToString(),
//             conversation_id = conversationId,
//             sender          = sender,
//             message         = content,
//             sent_at         = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss")
//         };
//         string json = "[" + JsonUtility.ToJson(req) + "]";

//         yield return StartCoroutine(
//             SendRequest(
//                 path:         MsgTable,
//                 method:       "POST",
//                 bodyJson:     json,
//                 onSuccess:    _ => onSuccess?.Invoke(),
//                 onError:      err => onError?.Invoke(err),
//                 preferHeader: "return=representation"
//             )
//         );
//     }

//     /// <summary>
//     /// Ambil pesan pertama (oldest) untuk satu conversation.
//     /// </summary>
//     public IEnumerator FetchFirstMessage(
//         string conversationId,
//         Action<string> onResult,
//         Action<string> onError = null
//     )
//     {
//         // GET /messages?select=message&conversation_id=eq.{conversationId}&order=sent_at.asc&limit=1
//         string path = $"{MsgTable}" +
//                     $"?select=message" +
//                     $"&conversation_id=eq.{conversationId}" +
//                     $"&order=sent_at.asc" +
//                     $"&limit=1";
//         Debug.Log($"[ChatService] FetchFirstMessage → {path}");
//         yield return StartCoroutine(
//             SendRequest(
//                 path:      path,
//                 method:    "GET",
//                 bodyJson:  null,
//                 onSuccess: resp =>
//                 {
//                     var wrapped = $"{{\"items\":{resp}}}";
//                     var list    = JsonUtility.FromJson<SnippetListWrapper>(wrapped);
//                     if (list.items != null && list.items.Length > 0)
//                         onResult(list.items[0].message);
//                     else
//                         onResult(string.Empty);
//                 },
//                 onError: err => onError?.Invoke(err)
//             )
//         );
//     }

//     /// <summary>
//     /// Hapus semua messages untuk array conversation IDs.
//     /// DELETE /messages?conversation_id=in.(id1,id2,…)
//     /// </summary>
//     public IEnumerator DeleteMessages(
//         string[] convIds,
//         Action onSuccess,
//         Action<string> onError = null
//     )
//     {
//         if (convIds == null || convIds.Length == 0)
//         {
//             onSuccess?.Invoke();
//             yield break;
//         }

//         string inClause = string.Join(",", convIds);
//         string path     = $"{MsgTable}?conversation_id=in.({Uri.EscapeDataString(inClause)})";
//         Debug.Log($"[ChatService] DeleteMessages → {path}");

//         yield return StartCoroutine(
//             SendRequest(
//                 path:      path,
//                 method:    "DELETE",
//                 bodyJson:  null,
//                 onSuccess: _ => onSuccess?.Invoke(),
//                 onError:   err => onError?.Invoke(err)
//             )
//         );
//     }

//     /// <summary>
//     /// Hapus semua conversations untuk satu user.
//     /// DELETE /conversations?user_id=eq.{userId}
//     /// </summary>
//     public IEnumerator DeleteConversations(
//         string userId,
//         Action onSuccess,
//         Action<string> onError = null
//     )
//     {
//         string path = $"{ConvTable}?user_id=eq.{Uri.EscapeDataString(userId)}";
//         Debug.Log($"[ChatService] DeleteConversations → {path}");

//         yield return StartCoroutine(
//             SendRequest(
//                 path:      path,
//                 method:    "DELETE",
//                 bodyJson:  null,
//                 onSuccess: _ => onSuccess?.Invoke(),
//                 onError:   err => onError?.Invoke(err)
//             )
//         );
//     }

//     /// <summary>
//     /// 1) FetchUserConversations
//     /// 2) DeleteMessages(convIds)
//     /// 3) DeleteConversations(userId)
//     /// </summary>
//     public IEnumerator DeleteAllChats(
//         string userId,
//         Action onSuccess,
//         Action<string> onError = null
//     )
//     {
//         // 1) ambil semua convIds
//         yield return StartCoroutine(FetchUserConversations(
//             userId,
//             convIds =>
//             {
//                 // 2) delete semua messages
//                 StartCoroutine(DeleteMessages(
//                     convIds,
//                     onSuccess: () =>
//                     {
//                         // 3) delete semua conversations
//                         StartCoroutine(DeleteConversations(
//                             userId,
//                             onSuccess: onSuccess,
//                             onError:   onError
//                         ));
//                     },
//                     onError: err => onError?.Invoke(err)
//                 ));
//             },
//             err => onError?.Invoke("FetchUserConversations failed: " + err)
//         ));
//     }

//     /// <summary>
//     /// Hapus semua message milik conversation ini.
//     /// </summary>
//     public IEnumerator DeleteMessagesForConversation(
//         string conversationId,
//         Action onSuccess,
//         Action<string> onError = null
//     )
//     {
//         string path = $"{MsgTable}?conversation_id=eq.{Uri.EscapeDataString(conversationId)}";
//         Debug.Log($"[ChatService] DeleteMessagesForConversation → DELETE {path}");
//         yield return StartCoroutine(
//             SendRequest(
//                 path:        path,
//                 method:      "DELETE",
//                 bodyJson:    null,
//                 onSuccess:   _ => onSuccess?.Invoke(),
//                 onError:     err => onError?.Invoke(err),
//                 preferHeader:"return=minimal"
//             )
//         );
//     }

//     /// <summary>
//     /// Hapus conversation row (selesai setelah pesan di‐cascade dihapus).
//     /// </summary>
//     public IEnumerator DeleteConversation(
//         string conversationId,
//         Action onSuccess,
//         Action<string> onError = null
//     )
//     {
//         string path = $"{ConvTable}?id=eq.{Uri.EscapeDataString(conversationId)}";
//         Debug.Log($"[ChatService] DeleteConversation → DELETE {path}");
//         yield return StartCoroutine(
//             SendRequest(
//                 path:        path,
//                 method:      "DELETE",
//                 bodyJson:    null,
//                 onSuccess:   _ => onSuccess?.Invoke(),
//                 onError:     err => onError?.Invoke(err),
//                 preferHeader:"return=minimal"
//             )
//         );
//     }
// }
