using System;
using UnityEngine;

namespace TorchFuelMod;

public class TorchFuelTracker : MonoBehaviour
{
    private float _timer;

    void Update()
    {
        _timer += Time.deltaTime;
        if (_timer < Plugin.CheckIntervalSeconds.Value) return;
        _timer = 0f;

        var tracked = Plugin.TrackedFireStructures;
        for (int i = tracked.Count - 1; i >= 0; i--)
        {
            var fire = tracked[i];
            if (fire == null || !fire.IsSpawned)
            {
                tracked.RemoveAt(i);
                continue;
            }

            try
            {
                float deficit = fire.MaxFuelVolume - fire.CurrentFuelVolume;
                if (deficit > 0.01f)
                    fire.Rpc_AddFuel(deficit);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[TorchFuelMod] Failed to top off fuel: {ex}");
            }
        }
    }
}
