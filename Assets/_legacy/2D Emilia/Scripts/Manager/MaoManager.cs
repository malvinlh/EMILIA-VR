// using UnityEngine;
// using UnityEngine.SceneManagement;

// public class MaoManager : MonoBehaviour
// {
//     public static MaoManager Instance { get; private set; }
//     public GameObject maoPrefab; // Drag prefab Mao ke sini di Inspector

//     private GameObject maoInstance;

//     private void Awake()
//     {
//         if (Instance == null)
//         {
//             Instance = this;
//             DontDestroyOnLoad(gameObject);
//             SceneManager.sceneLoaded += OnSceneLoaded;
//         }
//         else
//         {
//             Destroy(gameObject);
//         }
//     }

//     private void OnDestroy()
//     {
//         SceneManager.sceneLoaded -= OnSceneLoaded;
//     }

//     private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
//     {
//         SpawnOrMoveMao(scene.name);
//     }

//     private void SpawnOrMoveMao(string sceneName)
//     {
//         // Instantiate Mao jika belum ada
//         if (maoInstance == null)
//         {
//             maoInstance = Instantiate(maoPrefab);
//             DontDestroyOnLoad(maoInstance); // agar tidak dihancurkan saat pindah scene
//         }

//         // Atur posisi Mao sesuai nama scene
//         Vector3 targetPos;

//         switch (sceneName)
//         {
//             case "MainMenu":
//                 targetPos = new Vector3(-310, -180, 0);
//                 break;
//             case "Chat":
//                 targetPos = new Vector3(-605, -230, 0);
//                 break;
//             default:
//                 Debug.LogWarning($"No Mao position set for scene: {sceneName}");
//                 targetPos = Vector3.zero;
//                 break;
//         }

//         maoInstance.transform.position = targetPos;
//     }
// }
