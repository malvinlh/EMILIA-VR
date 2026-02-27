using System;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// UI component for a single journal entry in the journal list.
/// 
/// - Provides hooks for Edit and Delete actions.
/// - The <see cref="Setup"/> method must be called by <see cref="JournalManager"/>
///   (or another controller) after the entry is instantiated.
/// - Attempts to auto-find button references if they are not assigned in the Inspector.
/// </summary>
public class JournalEntry : MonoBehaviour
{
    [Header("UI References (Auto-find if empty)")]

    [Tooltip("Optional Edit button. Auto-found if not assigned.")]
    [SerializeField] private Button editButton;

    [Tooltip("Required Delete button (must exist in prefab with name 'DeleteButton').")]
    [SerializeField] private Button deleteButton;

    private Action _onEdit;
    private Action _onDelete;

    #region Public API

    /// <summary>
    /// Initializes the journal entry with callbacks for Edit and Delete actions.
    /// Should be called immediately after instantiation by the manager.
    /// </summary>
    /// <param name="onEdit">Action to invoke when Edit is clicked (optional).</param>
    /// <param name="onDelete">Action to invoke when Delete is clicked (required).</param>
    public void Setup(Action onEdit, Action onDelete)
    {
        _onEdit   = onEdit;
        _onDelete = onDelete;

        // Auto-find references if not assigned
        if (deleteButton == null)
            deleteButton = transform.Find("DeleteButton")?.GetComponent<Button>();
        if (editButton == null)
            editButton = transform.Find("EditButton")?.GetComponent<Button>();

        // Bind delete button
        if (deleteButton != null)
        {
            deleteButton.onClick.RemoveAllListeners();
            deleteButton.onClick.AddListener(() => _onDelete?.Invoke());
        }
        else
        {
            Debug.LogWarning("[JournalEntry] Delete button not found. This entry cannot be deleted.");
        }

        // Bind edit button (optional)
        if (editButton != null)
        {
            editButton.onClick.RemoveAllListeners();
            editButton.onClick.AddListener(() => _onEdit?.Invoke());
        }
    }

    #endregion
}