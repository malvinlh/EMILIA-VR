using UnityEngine;
using UnityEngine.UI;

[RequireComponent(typeof(Slider))]
/// <summary>
/// Handles synchronization between a UI Slider and the global audio system.
/// 
/// - Ensures the slider range is normalized (0–1).
/// - Loads the last saved master volume from PlayerPrefs.
/// - Updates <see cref="AudioManager"/> whenever the slider value changes.
/// </summary>
public class VolumeSliderHandler : MonoBehaviour
{
    [Header("UI Reference")]
    [Tooltip("Slider UI element used to control the master volume.")]
    [SerializeField] private Slider slider;

    #region Unity Lifecycle

    private void Start()
    {
        // Ensure we have a reference to the attached Slider component
        if (slider == null)
        {
            slider = GetComponent<Slider>();
        }

        // Normalize slider range (0–1)
        slider.minValue = 0f;
        slider.maxValue = 1f;

        // Load last saved master volume from PlayerPrefs, defaulting to 1 (100%)
        slider.value = PlayerPrefs.GetFloat("MasterVolume", 1f);

        // Subscribe to value change events
        slider.onValueChanged.AddListener(HandleSliderValueChanged);
    }

    private void OnDestroy()
    {
        // Clean up listener to avoid memory leaks
        if (slider != null)
        {
            slider.onValueChanged.RemoveListener(HandleSliderValueChanged);
        }
    }

    #endregion

    #region Private Methods

    /// <summary>
    /// Called whenever the slider value changes.
    /// Updates the global master volume via <see cref="AudioManager"/>.
    /// </summary>
    /// <param name="value">The new slider value (0–1).</param>
    private void HandleSliderValueChanged(float value)
    {
        if (AudioManager.Instance != null)
        {
            AudioManager.Instance.SetMasterVolume(value);
        }
        else
        {
            Debug.LogWarning("[VolumeSliderHandler] AudioManager instance not found.");
        }
    }

    #endregion
}