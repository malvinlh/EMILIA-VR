using UnityEngine;

/// <summary>
/// Serializable data container that maps a logical key to a Unity scene name.
/// 
/// This is typically used in <see cref="SceneFlowManager"/> to make scene loading
/// more robust by referring to a simple key instead of hardcoding scene names.
/// </summary>
[System.Serializable]
public class SceneInfo
{
    [Tooltip("Logical identifier for the scene (used by code to load scenes).")]
    public string sceneKey;

    [Tooltip("Name of the scene as defined in Unity's Build Settings.")]
    public string sceneName;
}
