using UnityEngine;
using TMPro; // Remove if you prefer using UnityEngine.UI.Text instead

/// <summary>
/// Displays the current system time on a UI text element.
/// Updates once per frame via Unity's <see cref="Update"/> loop.
/// 
/// Notes:
/// - By default this uses <see cref="TextMeshProUGUI"/>.
/// - To use Unity's legacy UI <c>Text</c>, replace the field type and remove TMP references.
/// </summary>
public class CurrentTimeDisplay : MonoBehaviour
{
    [Header("UI Reference")]
    [Tooltip("TMP text component where the current system time will be displayed.")]
    public TextMeshProUGUI timeText;

    // If using legacy UI:
    // public UnityEngine.UI.Text timeText;

    /// <summary>
    /// Unity callback called every frame.
    /// Updates the text component with the current local system time.
    /// </summary>
    private void Update()
    {
        if (timeText == null)
        {
            return; // Avoid null reference exceptions
        }

        // Get the current local system time, formatted as hh:mm AM/PM
        string currentTime = System.DateTime.Now.ToString("hh:mm tt");

        // Display in the UI text
        timeText.text = currentTime;
    }
}