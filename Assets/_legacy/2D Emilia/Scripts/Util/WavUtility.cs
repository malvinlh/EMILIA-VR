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
    /// <returns>True when saved successfully; otherwise false.</returns>
    public static bool Save(string filePath, AudioClip clip)
    {
        if (clip == null)
        {
            Debug.LogError("[WavUtility] Cannot save null AudioClip.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(filePath))
        {
            Debug.LogError("[WavUtility] Cannot save with an empty file path.");
            return false;
        }

        string targetPath = filePath.Trim();
        if (!targetPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
            targetPath += ".wav";

        string directory = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Application.persistentDataPath;
            targetPath = Path.Combine(directory, Path.GetFileName(targetPath));
        }

        try
        {
            // Ensure target directory exists
            Directory.CreateDirectory(directory);

            using (FileStream fileStream = CreateEmpty(targetPath))
            {
                ConvertAndWrite(fileStream, clip);
                WriteHeader(fileStream, clip);
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[WavUtility] Failed to save WAV to '{targetPath}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Encodes an <see cref="AudioClip"/> into an in-memory PCM 16-bit WAV byte buffer.
    /// Must be called on the main thread (uses <see cref="AudioClip.GetData"/>); the
    /// resulting <c>byte[]</c> can then be written to disk on a background thread.
    /// </summary>
    public static byte[] EncodeToBytes(AudioClip clip)
    {
        if (clip == null)
        {
            Debug.LogError("[WavUtility] Cannot encode null AudioClip.");
            return null;
        }

        try
        {
            int sampleCount = clip.samples * clip.channels;
            int dataByteLen = sampleCount * 2;                    // 16-bit PCM
            int totalLen    = HEADER_SIZE + dataByteLen;

            // Pre-size the buffer exactly so MemoryStream never reallocates.
            using (var ms = new MemoryStream(totalLen))
            {
                // Reserve header space; we'll seek back and fill it after the body.
                ms.SetLength(HEADER_SIZE);
                ms.Position = HEADER_SIZE;

                ConvertAndWrite(ms, clip);
                WriteHeader(ms, clip);

                return ms.ToArray();
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[WavUtility] Failed to encode WAV: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Encodes raw float audio samples (interleaved if multi-channel, range -1..1) into
    /// an in-memory PCM 16-bit WAV byte buffer. Lets callers skip the
    /// <see cref="AudioClip.Create"/> + second <see cref="AudioClip.GetData"/> round-trip
    /// that <see cref="EncodeToBytes(AudioClip)"/> would otherwise perform when they
    /// already have the float samples in hand (e.g. via a pooled buffer).
    /// </summary>
    /// <param name="samples">Float buffer. Only the first <paramref name="sampleCount"/> entries are read.</param>
    /// <param name="sampleCount">Number of <em>samples × channels</em> floats to encode. Capped at <paramref name="samples"/>.Length.</param>
    /// <param name="channels">Channel count (1 = mono, 2 = stereo).</param>
    /// <param name="frequency">Sample rate in Hz.</param>
    public static byte[] EncodeToBytes(float[] samples, int sampleCount, int channels, int frequency)
    {
        if (samples == null || sampleCount <= 0 || channels <= 0 || frequency <= 0)
        {
            Debug.LogError("[WavUtility] Cannot encode — invalid sample buffer or metadata.");
            return null;
        }

        if (sampleCount > samples.Length) sampleCount = samples.Length;

        try
        {
            int dataByteLen = sampleCount * 2;                    // 16-bit PCM
            int totalLen    = HEADER_SIZE + dataByteLen;

            using (var ms = new MemoryStream(totalLen))
            {
                ms.SetLength(HEADER_SIZE);
                ms.Position = HEADER_SIZE;

                WriteSamplesAsPcm16(ms, samples, sampleCount);
                WriteRawHeader(ms, sampleCount / channels, channels, frequency);

                return ms.ToArray();
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[WavUtility] Failed to encode WAV from float buffer: {ex.Message}");
            return null;
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
    /// Converts Unity float samples (-1..1) to 16-bit PCM and writes them to the stream.
    /// </summary>
    private static void ConvertAndWrite(Stream fileStream, AudioClip clip)
    {
        float[] samples = new float[clip.samples * clip.channels];
        clip.GetData(samples, 0);
        WriteSamplesAsPcm16(fileStream, samples, samples.Length);
    }

    /// <summary>
    /// Writes the first <paramref name="sampleCount"/> floats from <paramref name="samples"/>
    /// as little-endian 16-bit PCM to the stream.
    /// </summary>
    private static void WriteSamplesAsPcm16(Stream stream, float[] samples, int sampleCount)
    {
        byte[] bytesData = new byte[sampleCount * 2];

        const float rescaleFactor = 32767f; // max range of Int16

        for (int i = 0; i < sampleCount; i++)
        {
            short s = (short)(samples[i] * rescaleFactor);
            int   o = i * 2;
            bytesData[o]     = (byte)(s & 0xFF);
            bytesData[o + 1] = (byte)((s >> 8) & 0xFF);
        }

        stream.Write(bytesData, 0, bytesData.Length);
    }

    /// <summary>
    /// Writes a standard 44-byte WAV header for PCM 16-bit format using clip metadata.
    /// </summary>
    private static void WriteHeader(Stream fileStream, AudioClip clip)
    {
        WriteRawHeader(fileStream, clip.samples, clip.channels, clip.frequency);
    }

    /// <summary>
    /// Writes a standard 44-byte WAV header for PCM 16-bit format from raw metadata.
    /// </summary>
    private static void WriteRawHeader(Stream fileStream, int samplesPerChannel, int channels, int hz)
    {
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
        fileStream.Write(BitConverter.GetBytes(samplesPerChannel * channels * 2), 0, 4);
    }

    #endregion
}