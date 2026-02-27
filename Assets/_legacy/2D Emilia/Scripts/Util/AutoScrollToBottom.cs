using UnityEngine;
using UnityEngine.UI;
using System.Collections;

/// <summary>
/// Utility component that automatically scrolls a <see cref="ScrollRect"/> 
/// to the bottom of its content. 
/// 
/// Usage:
/// - Attach this script to a GameObject in the same UI hierarchy as a ScrollRect.
/// - Assign the target ScrollRect in the Inspector.
/// - Call <see cref="ScrollToBottom"/> after adding new content to force the
///   scroll view to display the most recent elements.
/// </summary>
public class AutoScrollToBottom : MonoBehaviour
{
    [Header("UI Reference")]
    [Tooltip("The ScrollRect to be scrolled to the bottom.")]
    [SerializeField] private ScrollRect scrollRect;

    #region Public API

    /// <summary>
    /// Requests the scroll view to move to the bottom. 
    /// This uses a coroutine to ensure layout updates are applied first.
    /// </summary>
    public void ScrollToBottom()
    {
        if (scrollRect == null)
        {
            Debug.LogWarning("[AutoScrollToBottom] No ScrollRect assigned.");
            return;
        }

        StartCoroutine(ScrollToBottomNextFrame());
    }

    #endregion

    #region Private Helpers

    /// <summary>
    /// Coroutine that waits one frame so Unity finishes layout rebuilding,
    /// then forces the ScrollRect to the bottom of its content.
    /// </summary>
    private IEnumerator ScrollToBottomNextFrame()
    {
        yield return null; // wait 1 frame for layout to complete

        // Ensure layout is rebuilt before applying the final scroll position
        LayoutRebuilder.ForceRebuildLayoutImmediate(scrollRect.content);

        // 0 = bottom, 1 = top
        scrollRect.verticalNormalizedPosition = 0f;
    }

    #endregion
}