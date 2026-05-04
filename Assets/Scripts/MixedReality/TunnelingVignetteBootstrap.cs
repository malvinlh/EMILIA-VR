using UnityEngine;
using Unity.XR.CoreUtils;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Comfort;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Movement;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Turning;

[DisallowMultipleComponent]
public class TunnelingVignetteBootstrap : MonoBehaviour
{
    [Tooltip("TunnelingVignette.prefab from XRI Starter Assets > TunnelingVignette folder.")]
    [SerializeField] private GameObject vignettePrefab;

    [Header("Vignette parameters (comfort)")]
    [Range(0f, 1f)] [SerializeField] private float apertureSize = 0.7f;
    [Range(0f, 1f)] [SerializeField] private float featheringEffect = 0.2f;
    [SerializeField] private float easeInTime = 0.1f;
    [SerializeField] private float easeOutTime = 0.3f;
    [SerializeField] private Color vignetteColor = Color.black;

    private void Start()
    {
        if (vignettePrefab == null)
        {
            Debug.LogWarning("[TunnelingVignetteBootstrap] vignettePrefab not assigned; comfort vignette will be inactive.", this);
            return;
        }

        var origin = FindAnyObjectByType<XROrigin>();
        if (origin == null || origin.Camera == null)
        {
            Debug.LogWarning("[TunnelingVignetteBootstrap] No XROrigin/Camera in scene; skipping vignette setup.", this);
            return;
        }

        var instance = Instantiate(vignettePrefab, origin.Camera.transform);
        instance.transform.localPosition = Vector3.zero;
        instance.transform.localRotation = Quaternion.identity;
        instance.transform.localScale = Vector3.one;

        var controller = instance.GetComponent<TunnelingVignetteController>();
        if (controller == null) return;

        controller.defaultParameters.apertureSize = apertureSize;
        controller.defaultParameters.featheringEffect = featheringEffect;
        controller.defaultParameters.easeInTime = easeInTime;
        controller.defaultParameters.easeOutTime = easeOutTime;
        controller.defaultParameters.vignetteColor = vignetteColor;

        controller.locomotionVignetteProviders.Clear();

        foreach (var move in FindObjectsByType<ContinuousMoveProvider>(FindObjectsSortMode.None))
        {
            controller.locomotionVignetteProviders.Add(new LocomotionVignetteProvider
            {
                locomotionProvider = move,
                enabled = true,
            });
        }

        foreach (var turn in FindObjectsByType<ContinuousTurnProvider>(FindObjectsSortMode.None))
        {
            controller.locomotionVignetteProviders.Add(new LocomotionVignetteProvider
            {
                locomotionProvider = turn,
                enabled = true,
            });
        }
    }
}
