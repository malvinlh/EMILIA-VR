// broken
// using UnityEngine;
// using UnityEngine.UI;
// using System;
// using System.IO;

// [RequireComponent(typeof(AudioSource))]
// public class AudioStreamRecorder : MonoBehaviour
// {
//     [Header("Recording Settings")]
//     public string outputFileName = "recording.wav";
//     public int    sampleRate     = 44100;

//     [Header("UI References")]
//     public GameObject micStartButton;
//     public GameObject micStopButton;

//     FileStream fileStream;
//     bool       isRecording = false;
//     int        channels    = 1;

//     void Awake()
//     {
//         // ensure AudioSource exists and loops
//         var src = GetComponent<AudioSource>();
//         src.loop = true;

//         // initial UI state
//         micStartButton.SetActive(true);
//         micStopButton.SetActive(false);
//     }

//     void OnEnable()
//     {
//         if (micStartButton != null && micStartButton.TryGetComponent<Button>(out var startBtn))
//             startBtn.onClick.AddListener(OnStartButtonClicked);
//         if (micStopButton  != null && micStopButton .TryGetComponent<Button>(out var stopBtn))
//             stopBtn.onClick.AddListener(OnStopButtonClicked);
//     }

//     void OnDisable()
//     {
//         if (micStartButton != null && micStartButton.TryGetComponent<Button>(out var startBtnRemove))
//             startBtnRemove.onClick.RemoveListener(OnStartButtonClicked);
//         if (micStopButton  != null && micStopButton .TryGetComponent<Button>(out var stopBtnRemove))
//             stopBtnRemove.onClick.RemoveListener(OnStopButtonClicked);
//     }

//     void OnStartButtonClicked()
//     {
//         StartRecording();
//         micStartButton.SetActive(false);
//         micStopButton.SetActive(true);
//     }

//     void OnStopButtonClicked()
//     {
//         StopRecording();
//         micStopButton.SetActive(false);
//         micStartButton.SetActive(true);
//     }

//     public void StartRecording()
//     {
//         if (isRecording) return;

//         // open file & placeholder header
//         string path = Path.Combine(Application.persistentDataPath, outputFileName);
//         fileStream = new FileStream(path, FileMode.Create);
//         fileStream.Write(new byte[44], 0, 44);

//         // start mic → AudioSource
//         var src = GetComponent<AudioSource>();
//         src.clip = Microphone.Start(null, true, 1, sampleRate);
//         while (Microphone.GetPosition(null) <= 0) {} // wait
//         channels = src.clip.channels;
//         src.Play();

//         isRecording = true;
//         Debug.Log("Recording…");
//     }

//     void OnAudioFilterRead(float[] data, int ch)
//     {
//         if (!isRecording) return;

//         // write each float[] as 16-bit PCM
//         byte[] pcm = new byte[data.Length * 2];
//         for (int i = 0; i < data.Length; i++)
//         {
//             short val = (short)(Mathf.Clamp(data[i], -1f, 1f) * short.MaxValue);
//             byte[] b  = BitConverter.GetBytes(val);
//             pcm[i * 2]     = b[0];
//             pcm[i * 2 + 1] = b[1];
//         }
//         fileStream.Write(pcm, 0, pcm.Length);
//     }

//     public void StopRecording()
//     {
//         if (!isRecording) return;
//         isRecording = false;

//         var src = GetComponent<AudioSource>();
//         src.Stop();
//         Microphone.End(null);

//         // finalize header
//         int fileSize = (int)fileStream.Length;
//         int dataSize = fileSize - 44;
//         fileStream.Seek(0, SeekOrigin.Begin);
//         using (var w = new BinaryWriter(fileStream))
//         {
//             w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
//             w.Write(fileSize - 8);
//             w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
//             w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
//             w.Write(16);
//             w.Write((short)1);
//             w.Write((short)channels);
//             w.Write(sampleRate);
//             w.Write(sampleRate * channels * 2);
//             w.Write((short)(channels * 2));
//             w.Write((short)16);
//             w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
//             w.Write(dataSize);
//         }
//         fileStream.Close();

//         Debug.Log($"Saved WAV to: {Application.persistentDataPath}/{outputFileName}");
//     }
// }