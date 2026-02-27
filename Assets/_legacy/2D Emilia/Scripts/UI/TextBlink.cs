using UnityEngine;
using TMPro;

/// <summary>
/// Makes a TextMeshProUGUI element blink by smoothly oscillating its alpha value.
/// 
/// Usage:
/// - Attach this script to any GameObject.
/// - Assign a TextMeshProUGUI component to <see cref="targetText"/>.
/// - Adjust <see cref="blinkSpeed"/> in the Inspector to control blink speed.
/// </summary>
public class TextBlink : MonoBehaviour
{
    [Header("Blink Settings")]
    [Tooltip("The TextMeshProUGUI component to blink.")]
    [SerializeField] private TextMeshProUGUI targetText;

    [Tooltip("Blink speed multiplier. Higher values = faster blinking.")]
    [SerializeField] private float blinkSpeed = 2f;

    /// <summary>
    /// The original color of the text, cached at start.
    /// </summary>
    private Color _originalColor;

    #region Unity Lifecycle

    private void Start()
    {
        if (targetText != null)
        {
            _originalColor = targetText.color;
        }
        else
        {
            Debug.LogWarning("[TextBlink] No target text assigned.");
        }
    }

    private void Update()
    {
        if (targetText == null) return;

        // Oscillate alpha between 0 and 1 using a sine wave
        float alpha = (Mathf.Sin(Time.time * blinkSpeed) + 1f) * 0.5f;

        Color newColor = _originalColor;
        newColor.a = alpha;
        targetText.color = newColor;
    }

    #endregion
}