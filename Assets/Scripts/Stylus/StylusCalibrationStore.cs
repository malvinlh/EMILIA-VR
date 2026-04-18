using System;
using System.IO;
using UnityEngine;
using UnityEngine.XR.Hands;

/// <summary>
/// Diagnostic persistence for the DIY stylus calibration. The offset is NOT
/// auto-loaded on launch — the user always recalibrates each session because
/// the DIY pen can shift in the grip between days — but every successful
/// calibration is written to <c>Application.persistentDataPath/stylus_calibration.json</c>
/// so residuals can be inspected across runs.
///
/// Load API is exposed for manual diagnostic replay; it is intentionally not
/// wired into the normal boot path.
/// </summary>
public static class StylusCalibrationStore
{
    private const string FileName = "stylus_calibration.json";

    [Serializable]
    public class Record
    {
        public string timestampIso;
        public string handedness; // "Left" / "Right"
        public float offsetX, offsetY, offsetZ;
        public float offsetMagnitude;
        public float rmsResidualMeters;
        public int sampleCount;
    }

    public static string FullPath => Path.Combine(Application.persistentDataPath, FileName);

    /// <summary>
    /// Write a calibration record to disk. Silently returns false if IO fails —
    /// this is diagnostic only and must never block the session.
    /// </summary>
    public static bool Save(Handedness hand, Vector3 offset, float rmsResidualMeters, int sampleCount)
    {
        try
        {
            var record = new Record
            {
                timestampIso = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                handedness = hand.ToString(),
                offsetX = offset.x,
                offsetY = offset.y,
                offsetZ = offset.z,
                offsetMagnitude = offset.magnitude,
                rmsResidualMeters = rmsResidualMeters,
                sampleCount = sampleCount,
            };
            string json = JsonUtility.ToJson(record, prettyPrint: true);
            File.WriteAllText(FullPath, json);
            Debug.Log($"[StylusCalibrationStore] Saved to {FullPath}");
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[StylusCalibrationStore] Save failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Load a previously saved record for inspection. Returns null if the file
    /// is missing or unreadable. Not used by the normal boot path.
    /// </summary>
    public static Record Load()
    {
        try
        {
            if (!File.Exists(FullPath)) return null;
            string json = File.ReadAllText(FullPath);
            return JsonUtility.FromJson<Record>(json);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[StylusCalibrationStore] Load failed: {ex.Message}");
            return null;
        }
    }
}
