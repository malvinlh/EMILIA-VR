using System;
using UnityEngine;

/// <summary>
/// Detects when the player (XR Origin camera) enters or exits a trigger zone.
/// Attach to a GameObject with a SphereCollider set as trigger.
/// </summary>
[RequireComponent(typeof(SphereCollider))]
public class ProximityTrigger : MonoBehaviour
{
    [Tooltip("Tag of the player/camera object. Default: MainCamera.")]
    public string playerTag = "MainCamera";

    public event Action OnPlayerEnter;
    public event Action OnPlayerExit;

    public bool IsPlayerInside { get; private set; }

    private void Awake()
    {
        var col = GetComponent<SphereCollider>();
        col.isTrigger = true;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (!other.CompareTag(playerTag)) return;
        if (IsPlayerInside) return;

        IsPlayerInside = true;
        OnPlayerEnter?.Invoke();
    }

    private void OnTriggerExit(Collider other)
    {
        if (!other.CompareTag(playerTag)) return;
        if (!IsPlayerInside) return;

        IsPlayerInside = false;
        OnPlayerExit?.Invoke();
    }
}
