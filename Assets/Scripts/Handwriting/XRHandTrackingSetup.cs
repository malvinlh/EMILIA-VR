using UnityEngine;
using UnityEngine.XR.Hands;

/// <summary>
/// Helper MonoBehaviour that can be placed on the XR Origin to verify
/// that hand tracking is running at startup and log diagnostics.
///
/// SCENE SETUP for XR Interaction Toolkit 3.0.10 + XR Hands 1.5.x + Meta Quest 3:
///
/// 1. Add an XR Origin (GameObject > XR > XR Origin - XR Rig) to the scene.
///    This creates the Camera Offset, Main Camera, and Left/Right Controller
///    hierarchy automatically.
///
/// 2. On the XR Origin GameObject, add an "XR Hand Tracking Manager" component
///    (from com.unity.xr.hands). This starts the hand subsystem.
///
/// 3. Create two empty child GameObjects under XR Origin, named:
///       "RightHandPen"  and  "LeftHandPen"
///    Add a WhiteboardPen component to each:
///       - RightHandPen → Handedness = Right
///       - LeftHandPen  → Handedness = Left
///
/// 4. Create an empty GameObject named "WhiteboardManager".
///    Add the WhiteboardUtils component to it.
///    Assign WhiteboardPrefab and SpherePrefab in the Inspector.
///
/// 5. Make sure the Whiteboard prefab has a Collider on layer 10 ("Whiteboard").
///
/// 6. In Project Settings > XR Plug-in Management:
///    - Android tab: enable "OpenXR". Disable Oculus loader if still present.
///    - Under OpenXR > Features, enable:
///         • Meta Quest Feature
///         • Hand Tracking Subsystem (Meta)
///    - Under OpenXR > Interaction Profiles, add:
///         • Meta Quest Touch Plus Controller Profile
///
/// 7. Remove leftover Oculus loader/settings assets if present:
///       Assets/XR/Loaders/Oculus Loader.asset
///       Assets/XR/Settings/Oculus Settings.asset
///
/// 8. Build target: Android, Min API Level 29+, Target API 32+, IL2CPP, ARM64.
/// </summary>
public class XRHandTrackingSetup : MonoBehaviour
{
    private void Start()
    {
        Debug.Log("[XRHandTrackingSetup] Waiting for XR Hand subsystem...");
    }

    private void Update()
    {
        var subsystem = WhiteboardPen.GetHandSubsystem();
        if (subsystem != null && subsystem.running)
        {
            Debug.Log("[XRHandTrackingSetup] XR Hand subsystem is running. Hand tracking ready!");
            // Disable this component after confirmation — no need to keep checking.
            enabled = false;
        }
    }
}
