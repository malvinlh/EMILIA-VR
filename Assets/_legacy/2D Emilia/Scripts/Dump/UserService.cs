// using UnityEngine;
// using System.Collections;

// [System.Serializable]
// public class UserUpsert
// {
//     public string name;      // primary key = nickname
//     public string username;  // full name (nullable)
// }

// public class UserService : SupabaseHttpClient
// {
//     const string Table = "users";

//     /// <summary>
//     /// INSERT or UPDATE user berdasarkan primary key `name` (nickname).
//     /// </summary>
//     public IEnumerator UpsertUser(
//         string nickname,
//         string fullName,
//         System.Action onSuccess,
//         System.Action<string> onError
//     ) 
//     {
//         // build payload
//         var payload = new UserUpsert {
//             name     = nickname,
//             username = string.IsNullOrWhiteSpace(fullName) ? null : fullName
//         };
//         // wrap as JSON array
//         string jsonArray = "[" + JsonUtility.ToJson(payload) + "]";

//         string path = $"{Table}?on_conflict=name";
//         yield return StartCoroutine(
//             SendRequest(
//                 path:          path,
//                 method:        "POST",
//                 bodyJson:      jsonArray,
//                 onSuccess:     _ => onSuccess?.Invoke(),
//                 onError:       err => onError?.Invoke(err),
//                 preferHeader:  "resolution=merge-duplicates"
//             )
//         );
//     }
// }