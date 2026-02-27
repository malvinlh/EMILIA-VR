// using UnityEngine;
// using System.Collections;

// public class MicrophoneRecorder : MonoBehaviour
// {
//     [Header("Recording Settings")]
//     public int sampleRate = 44100;
//     public int maxDurationSec = 10;
//     public string outputFilename = "MyRecording.wav";

//     private AudioClip recordingClip;
//     private bool isRecording = false;

//     /// <summary>
//     /// Call this to begin recording from the default mic.
//     /// </summary>
//     public void StartRecording()
//     {
//         if (isRecording) return;

//         if (Microphone.devices.Length == 0)
//         {
//             Debug.LogError("No microphone found!");
//             return;
//         }

//         recordingClip = Microphone.Start(
//             deviceName: null,     // default mic
//             loop: false,
//             lengthSec: maxDurationSec,
//             frequency: sampleRate
//         );
//         isRecording = true;
//         Debug.Log("Recording started...");
//         // Optionally, you could start a coroutine to auto-stop after maxDurationSec:
//         // StartCoroutine(StopAfterDelay(maxDurationSec));
//     }

//     /// <summary>
//     /// Call this to stop recording and save the WAV.
//     /// </summary>
//     public void StopRecording()
//     {
//         if (!isRecording) return;

//         Microphone.End(null);
//         isRecording = false;
//         Debug.Log("Recording stopped. Saving WAV...");

//         if (recordingClip != null)
//         {
//             WavUtility.SaveWav(outputFilename, recordingClip);
//             recordingClip = null;
//         }
//     }

//     /// <summary>
//     /// (Optional) automatically stops after a given delay.
//     /// </summary>
//     private IEnumerator StopAfterDelay(int seconds)
//     {
//         yield return new WaitForSeconds(seconds);
//         StopRecording();
//     }
// }