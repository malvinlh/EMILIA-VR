using UnityEngine;
using UnityEngine.XR;
using System.Collections.Generic;

public class SeatedAutoCalibrate : MonoBehaviour
{
    void Start()
    {
        var subsystems = new List<XRInputSubsystem>();
        SubsystemManager.GetSubsystems(subsystems);

        foreach (var s in subsystems)
        {
            s.TryRecenter();
        }
    }
}
