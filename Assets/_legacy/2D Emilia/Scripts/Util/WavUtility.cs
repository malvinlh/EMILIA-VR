using System;
using System.IO;
using UnityEngine;

/// <summary>
/// Utility class for saving Unity <see cref="AudioClip"/> data as a standard PCM 16-bit WAV file.
/// 
/// Notes:
/// - Only supports 16-bit PCM format (no compression).
/// - Adds a 44-byte WAV header before raw sample data.
/// - Creates the target directory if it does not exist.
/// </summary>
public static class WavUtility
{
    private const int HEADER_SIZE = 44;

    #region Public API

    /// <summary>
    /// Saves an <see cref="AudioClip"/> to the given file path as a WAV file.
    /// </summary>
    /// <param name="filePath">Destination path (with or without ".wav" extension).</param>
    /// <param name="clip">The Unity <see cref="AudioClip"/> to save.</param>
    public static void Save(string filePath, AudioClip clip)
    {
        if (clip == null)
        {
            Debug.LogError("[WavUtility] Cannot save null AudioClip.");
            return;
        }

        if (!filePath.ToLower().EndsWith(".wav"))
            filePath += ".wav";

        // Ensure target directory exists
        Directory.CreateDirectory(Path.GetDirectoryName(filePath));

        using (FileStream fileStream = CreateEmpty(filePath))
        {
            ConvertAndWrite(fileStream, clip);
            WriteHeader(fileStream, clip);
        }
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Creates a new empty WAV file with a placeholder header.
    /// </summary>
    private static FileStream CreateEmpty(string filePath)
    {
        FileStream fileStream = new FileStream(filePath, FileMode.Create);
        byte emptyByte = new byte();

        // Reserve space for header
        for (int i = 0; i < HEADER_SIZE; i++)
            fileStream.WriteByte(emptyByte);

        return fileStream;
    }

    /// <summary>
    /// Converts Unity float samples (-1..1) to 16-bit PCM and writes them to the file stream.
    /// </summary>
    private static void ConvertAndWrite(FileStream fileStream, AudioClip clip)
    {
        float[] samples = new float[clip.samples * clip.channels];
        clip.GetData(samples, 0);

        short[] intData = new short[samples.Length];
        byte[] bytesData = new byte[samples.Length * 2];

        const float rescaleFactor = 32767f; // max range of Int16

        for (int i = 0; i < samples.Length; i++)
        {
            intData[i] = (short)(samples[i] * rescaleFactor);
            byte[] byteArr = BitConverter.GetBytes(intData[i]);
            byteArr.CopyTo(bytesData, i * 2);
        }

        fileStream.Write(bytesData, 0, bytesData.Length);
    }

    /// <summary>
    /// Writes a standard 44-byte WAV header for PCM 16-bit format.
    /// </summary>
    private static void WriteHeader(FileStream fileStream, AudioClip clip)
    {
        int hz = clip.frequency;
        int channels = clip.channels;
        int samples = clip.samples;

        fileStream.Seek(0, SeekOrigin.Begin);

        // Chunk ID "RIFF"
        fileStream.Write(System.Text.Encoding.UTF8.GetBytes("RIFF"), 0, 4);

        // Chunk size (file size - 8 bytes)
        fileStream.Write(BitConverter.GetBytes(fileStream.Length - 8), 0, 4);

        // Format "WAVE"
        fileStream.Write(System.Text.Encoding.UTF8.GetBytes("WAVE"), 0, 4);

        // Subchunk1 ID "fmt "
        fileStream.Write(System.Text.Encoding.UTF8.GetBytes("fmt "), 0, 4);

        // Subchunk1 size (16 for PCM)
        fileStream.Write(BitConverter.GetBytes(16), 0, 4);

        // Audio format (1 = PCM)
        fileStream.Write(BitConverter.GetBytes((ushort)1), 0, 2);

        // Number of channels
        fileStream.Write(BitConverter.GetBytes(channels), 0, 2);

        // Sample rate
        fileStream.Write(BitConverter.GetBytes(hz), 0, 4);

        // Byte rate (SampleRate * Channels * BytesPerSample)
        fileStream.Write(BitConverter.GetBytes(hz * channels * 2), 0, 4);

        // Block align (Channels * BytesPerSample)
        fileStream.Write(BitConverter.GetBytes((ushort)(channels * 2)), 0, 2);

        // Bits per sample
        fileStream.Write(BitConverter.GetBytes((ushort)16), 0, 2);

        // Subchunk2 ID "data"
        fileStream.Write(System.Text.Encoding.UTF8.GetBytes("data"), 0, 4);

        // Subchunk2 size (NumSamples * Channels * BytesPerSample)
        fileStream.Write(BitConverter.GetBytes(samples * channels * 2), 0, 4);
    }

    #endregion
}