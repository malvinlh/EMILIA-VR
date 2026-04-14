using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using OpenCVForUnity.CoreModule;
using OpenCVForUnity.ImgprocModule;

/// <summary>
/// Detects a green rubber band on a DIY stylus in the passthrough camera
/// feed, and unprojects the detection to a world-space point on the writing
/// plane. Produces a CV-based tip estimate used to correct mid-session grip
/// drift in <see cref="StylusTipProvider"/>.
///
/// Pipeline (throttled to <see cref="targetHz"/> on the main thread):
///   1. Acquire latest CPU image via <see cref="ARCameraManager"/>.
///   2. Convert to RGBA, build a small working Mat.
///   3. HSV threshold -> largest green blob -> centroid (px).
///   4. Unproject centroid with camera intrinsics -> world ray.
///   5. Intersect ray with writing plane -> world position.
///
/// Graceful degradation: if camera image access or intrinsics retrieval fails
/// once at startup, <see cref="IsAvailable"/> stays false and the detector
/// idles. The rest of the stylus pipeline continues on wrist-only tracking.
/// </summary>
public class GreenBandDetector : MonoBehaviour
{
    [Header("References")]
    [Tooltip("AR Camera Manager on the XR Origin main camera. Auto-resolved if left null.")]
    public ARCameraManager arCameraManager;

    [Header("Processing")]
    [Tooltip("Target detection frequency (Hz). Lower is cheaper; 15-20 Hz is typically enough.")]
    public float targetHz = 20f;
    [Tooltip("Downscale factor for the working image. 2 = half resolution (4x faster).")]
    [Range(1, 4)] public int downscale = 2;

    [Header("HSV Green Range")]
    [Tooltip("Lower HSV bound. OpenCV uses H:0-179, S:0-255, V:0-255.")]
    public Vector3 hsvLower = new Vector3(35f, 80f, 60f);
    [Tooltip("Upper HSV bound.")]
    public Vector3 hsvUpper = new Vector3(85f, 255f, 255f);

    [Header("Detection")]
    [Tooltip("Minimum contour area (pixels, in downscaled image) to accept as a detection.")]
    public int minContourArea = 20;
    [Tooltip("Area (pixels, in downscaled image) that maps to confidence = 1.0.")]
    public int confidenceFullArea = 400;

    [Header("Debug")]
    public bool logDetections = false;

    // ── Output ───────────────────────────────────────────────────────
    public bool IsAvailable { get; private set; }
    public bool HasRecentDetection { get; private set; }
    public float LastConfidence { get; private set; }
    public Vector2 LastCentroidPx { get; private set; }
    public float LastDetectionTime { get; private set; }

    // ── OpenCV buffers (reused) ──────────────────────────────────────
    private Mat matRgba;     // full-res RGBA (from XRCpuImage)
    private Mat matWorking;  // downscaled RGB
    private Mat matHsv;
    private Mat matMask;
    private Mat matHierarchy;
    private byte[] cpuBuffer;
    private int bufferWidth;
    private int bufferHeight;

    // ── Unprojection data from latest frame ──────────────────────────
    private XRCameraIntrinsics latestIntrinsics;
    private bool hasIntrinsics;
    private Matrix4x4 latestCameraToWorld;
    private Vector2Int latestImageSize;

    // ── Scheduling ───────────────────────────────────────────────────
    private float nextProcessTime;
    private bool startupProbeDone;

    private void OnEnable()
    {
        IsAvailable = false;
        HasRecentDetection = false;
        startupProbeDone = false;
    }

    private void OnDisable()
    {
        DisposeMats();
    }

    private void OnDestroy()
    {
        DisposeMats();
    }

    private void Update()
    {
        if (arCameraManager == null)
        {
            Camera cam = Camera.main;
            if (cam != null) arCameraManager = cam.GetComponent<ARCameraManager>();
            if (arCameraManager == null) return;
        }

        if (Time.time < nextProcessTime) return;
        nextProcessTime = Time.time + 1f / Mathf.Max(1f, targetHz);

        if (!arCameraManager.TryAcquireLatestCpuImage(out XRCpuImage cpuImage))
        {
            if (!startupProbeDone)
            {
                startupProbeDone = true;
                Debug.LogWarning("[GreenBandDetector] TryAcquireLatestCpuImage returned false. " +
                                 "Passthrough CPU access may be unavailable on this platform. " +
                                 "Disabling CV fusion; falling back to wrist-only tracking.");
            }
            return;
        }

        try
        {
            if (!startupProbeDone)
            {
                startupProbeDone = true;
                IsAvailable = true;
                Debug.Log("[GreenBandDetector] CPU image access OK. CV fusion active.");
            }

            if (arCameraManager.TryGetIntrinsics(out XRCameraIntrinsics intrinsics))
            {
                latestIntrinsics = intrinsics;
                hasIntrinsics = true;
            }

            latestCameraToWorld = arCameraManager.GetComponent<Camera>() != null
                ? arCameraManager.GetComponent<Camera>().cameraToWorldMatrix
                : Camera.main.cameraToWorldMatrix;

            ProcessImage(cpuImage);
        }
        finally
        {
            cpuImage.Dispose();
        }

        // Expire stale detections so consumers see HasRecentDetection flip off.
        if (HasRecentDetection && Time.time - LastDetectionTime > 0.2f)
            HasRecentDetection = false;
    }

    private void ProcessImage(XRCpuImage cpuImage)
    {
        int srcW = cpuImage.width;
        int srcH = cpuImage.height;

        // Convert XRCpuImage to RGBA32 byte buffer at native resolution.
        var conversionParams = new XRCpuImage.ConversionParams
        {
            inputRect = new RectInt(0, 0, srcW, srcH),
            outputDimensions = new Vector2Int(srcW, srcH),
            outputFormat = TextureFormat.RGBA32,
            transformation = XRCpuImage.Transformation.None
        };
        int size = cpuImage.GetConvertedDataSize(conversionParams);
        if (cpuBuffer == null || cpuBuffer.Length < size)
            cpuBuffer = new byte[size];

        GCHandle handle = GCHandle.Alloc(cpuBuffer, GCHandleType.Pinned);
        try
        {
            cpuImage.Convert(conversionParams, handle.AddrOfPinnedObject(), size);
        }
        finally
        {
            handle.Free();
        }

        if (matRgba == null || bufferWidth != srcW || bufferHeight != srcH)
        {
            DisposeMats();
            matRgba = new Mat(srcH, srcW, CvType.CV_8UC4);
            bufferWidth = srcW;
            bufferHeight = srcH;
        }
        matRgba.put(0, 0, cpuBuffer);

        int dstW = Mathf.Max(32, srcW / downscale);
        int dstH = Mathf.Max(32, srcH / downscale);

        if (matWorking == null || matWorking.cols() != dstW || matWorking.rows() != dstH)
        {
            matWorking?.Dispose();
            matHsv?.Dispose();
            matMask?.Dispose();
            matWorking = new Mat();
            matHsv = new Mat();
            matMask = new Mat();
        }

        if (downscale > 1)
            Imgproc.resize(matRgba, matWorking, new Size(dstW, dstH), 0, 0, Imgproc.INTER_LINEAR);
        else
            matRgba.copyTo(matWorking);

        // RGBA -> HSV (OpenCV needs RGB intermediate).
        Imgproc.cvtColor(matWorking, matWorking, Imgproc.COLOR_RGBA2RGB);
        Imgproc.cvtColor(matWorking, matHsv, Imgproc.COLOR_RGB2HSV);

        Core.inRange(matHsv,
            new Scalar(hsvLower.x, hsvLower.y, hsvLower.z),
            new Scalar(hsvUpper.x, hsvUpper.y, hsvUpper.z),
            matMask);

        Imgproc.GaussianBlur(matMask, matMask, new Size(5, 5), 0);

        List<MatOfPoint> contours = new List<MatOfPoint>();
        if (matHierarchy == null) matHierarchy = new Mat();
        Imgproc.findContours(matMask, contours, matHierarchy,
            Imgproc.RETR_EXTERNAL, Imgproc.CHAIN_APPROX_SIMPLE);

        MatOfPoint largest = null;
        double largestArea = 0;
        for (int i = 0; i < contours.Count; i++)
        {
            double area = Imgproc.contourArea(contours[i]);
            if (area > largestArea)
            {
                largestArea = area;
                largest = contours[i];
            }
        }

        if (largest == null || largestArea < minContourArea)
        {
            for (int i = 0; i < contours.Count; i++) contours[i].Dispose();
            return;
        }

        Moments m = Imgproc.moments(largest);
        if (m.m00 == 0)
        {
            for (int i = 0; i < contours.Count; i++) contours[i].Dispose();
            return;
        }

        // Centroid in downscaled-pixel coords -> upscale back to source pixels.
        float cxDown = (float)(m.m10 / m.m00);
        float cyDown = (float)(m.m01 / m.m00);
        float cx = cxDown * ((float)srcW / dstW);
        float cy = cyDown * ((float)srcH / dstH);

        LastCentroidPx = new Vector2(cx, cy);
        LastConfidence = Mathf.Clamp01((float)largestArea / Mathf.Max(1f, confidenceFullArea));
        LastDetectionTime = Time.time;
        HasRecentDetection = true;
        latestImageSize = new Vector2Int(srcW, srcH);

        if (logDetections)
            Debug.Log($"[GreenBandDetector] centroid=({cx:F0},{cy:F0}) area={largestArea:F0} conf={LastConfidence:F2}");

        for (int i = 0; i < contours.Count; i++) contours[i].Dispose();
    }

    /// <summary>
    /// Attempt to produce a world-space point by intersecting the centroid
    /// ray with the writing plane. Returns false if no recent detection,
    /// intrinsics unavailable, or the ray runs parallel to the plane.
    /// </summary>
    public bool TryGetWorldPosition(Plane writingPlane, out Vector3 worldPos, out float confidence)
    {
        worldPos = Vector3.zero;
        confidence = 0f;

        if (!IsAvailable || !HasRecentDetection || !hasIntrinsics) return false;

        // Pixel -> camera-space ray direction using intrinsics.
        float fx = latestIntrinsics.focalLength.x;
        float fy = latestIntrinsics.focalLength.y;
        float cx = latestIntrinsics.principalPoint.x;
        float cy = latestIntrinsics.principalPoint.y;
        if (fx <= 0f || fy <= 0f) return false;

        float u = LastCentroidPx.x;
        float v = LastCentroidPx.y;

        // OpenXR/ARFoundation intrinsics: origin top-left, +y down. Unity's
        // camera ray uses +y up, so flip v.
        float xCam = (u - cx) / fx;
        float yCam = -((v - cy) / fy);
        Vector3 rayCam = new Vector3(xCam, yCam, 1f).normalized;
        Vector3 rayWorld = latestCameraToWorld.MultiplyVector(rayCam).normalized;
        Vector3 originWorld = latestCameraToWorld.MultiplyPoint(Vector3.zero);

        Ray ray = new Ray(originWorld, rayWorld);
        if (!writingPlane.Raycast(ray, out float enter)) return false;

        worldPos = ray.GetPoint(enter);
        confidence = LastConfidence;
        return true;
    }

    private void DisposeMats()
    {
        matRgba?.Dispose(); matRgba = null;
        matWorking?.Dispose(); matWorking = null;
        matHsv?.Dispose(); matHsv = null;
        matMask?.Dispose(); matMask = null;
        matHierarchy?.Dispose(); matHierarchy = null;
    }
}
