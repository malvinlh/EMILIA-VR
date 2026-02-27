using System.Linq;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Controls UI behavior related to the sidebar canvas.
/// 
/// Currently responsible for hiding all "DeleteButton" objects inside
/// the assigned sidebar canvas. This is useful when you want to prevent
/// accidental deletions or toggle UI state dynamically.
/// </summary>
public class GOController : MonoBehaviour
{
    [Header("Sidebar Canvas")]
    [Tooltip("Root sidebar canvas containing Delete buttons and other UI elements.")]
    [SerializeField] private GameObject sidebarCanvas;

    /// <summary>
    /// Hides all child buttons named "DeleteButton" inside the sidebar canvas.
    /// - If the sidebar is null or inactive, nothing happens.
    /// - Uses a deep search (includes inactive children).
    /// </summary>
    public void HideTrashIcons()
    {
        // Early exit if sidebar is missing or not active
        if (sidebarCanvas == null || !sidebarCanvas.activeSelf)
        {
            return;
        }

        // Find all Button components under the sidebar that are named "DeleteButton"
        var deleteButtons = sidebarCanvas
            .GetComponentsInChildren<Button>(includeInactive: true)
            .Where(b => b.gameObject.name == "DeleteButton");

        // Hide each matching button
        foreach (var btn in deleteButtons)
        {
            btn.gameObject.SetActive(false);
        }
    }
}