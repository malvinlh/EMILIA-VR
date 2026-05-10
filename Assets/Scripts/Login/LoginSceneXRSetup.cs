using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit.Inputs;

/// Prevents XRInputModalityManager from switching to Hand mode in the Login scene.
/// On Meta Quest 3, hand tracking runs continuously, causing the modality manager to
/// hide controller GameObjects the moment hand joints are detected — even while holding
/// controllers. The Login scene does not use hand interaction, so locking it to
/// Controller mode is correct behaviour.
[DefaultExecutionOrder(-1000)]
[DisallowMultipleComponent]
public class LoginSceneXRSetup : MonoBehaviour
{
    private void Awake()
    {
        var modalityManager = GetComponentInChildren<XRInputModalityManager>(true);
        if (modalityManager == null)
            return;

        GameObject leftCtrl  = modalityManager.leftController;
        GameObject rightCtrl = modalityManager.rightController;

        modalityManager.enabled = false;

        leftCtrl?.SetActive(true);
        rightCtrl?.SetActive(true);
    }
}
