// using System.Collections;
// using UnityEngine;
// using UnityEngine.UI;
// using TMPro;
// using System;

// public class ChangeNameSetting : MonoBehaviour
// {
//     [Header("UI References")]
//     public TMP_InputField   input;
//     public TextMeshProUGUI  feedback;
//     public Button           saveBtn;

//     void Start()
//     {
//         // pastikan feedback awalnya kosong
//         feedback.text  = "";
//         feedback.color = Color.white;  // atau warna default UI-mu

//         saveBtn.onClick.AddListener(OnSaveClicked);
//     }

//     public void OnSaveClicked()
//     {
//         // reset feedback
//         feedback.text  = "";
        
//         string oldNick = PlayerPrefs.GetString("Nickname", "");
//         string newNick = input.text.Trim();

//         if (string.IsNullOrWhiteSpace(newNick))
//         {
//             SetError("Nickname tidak boleh kosong!");
//             return;
//         }
//         if (newNick == oldNick)
//         {
//             SetError("Nickname sama dengan yang lama.");
//             return;
//         }

//         // cek ketersediaan
//         StartCoroutine(
//             ServiceManager.Instance.UserService.IsNicknameAvailable(
//                 newNick,
//                 available =>
//                 {
//                     if (!available)
//                     {
//                         SetError("Nickname sudah dipakai.");
//                         return;
//                     }

//                     // lanjut rename
//                     StartCoroutine(
//                         ServiceManager.Instance.UserService.RenameNickname(
//                             oldNick, newNick,
//                             onSuccess: () =>
//                             {
//                                 PlayerPrefs.SetString("Nickname", newNick);
//                                 PlayerPrefs.Save();
//                                 SetSuccess("Nickname berhasil diubah!");
//                             },
//                             onError: err => SetError($"Rename gagal: {err}")
//                         )
//                     );
//                 },
//                 err => SetError($"Cek ketersediaan gagal: {err}")
//             )
//         );
//     }

//     private void SetError(string msg)
//     {
//         feedback.color = Color.red;
//         feedback.text  = msg;
//     }

//     private void SetSuccess(string msg)
//     {
//         feedback.color = Color.green;
//         feedback.text  = msg;
//     }
// }