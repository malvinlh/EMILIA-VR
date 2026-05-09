/// <summary>
/// Static cross-scene cache for table-tap calibration data.
/// Survives scene transitions for the lifetime of the app process.
/// Populated by JournalSessionManager after a successful calibration;
/// consumed by JournalSessionManager.Start() in subsequent scenes.
/// </summary>
public static class JournalCalibrationCache
{
    public static bool IsValid { get; private set; }
    public static TableTapCalibrator.DetectedTable Table { get; private set; }
    public static float CapturedRealEyeHeight { get; private set; }

    public static void Store(TableTapCalibrator.DetectedTable table, float eyeHeight)
    {
        Table = table;
        CapturedRealEyeHeight = eyeHeight;
        IsValid = true;
        UnityEngine.Debug.Log("[JournalCalibrationCache] Stored calibration data.");
    }

    public static void Invalidate()
    {
        IsValid = false;
        UnityEngine.Debug.Log("[JournalCalibrationCache] Cache invalidated.");
    }
}
