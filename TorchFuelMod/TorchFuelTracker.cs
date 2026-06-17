using System;
using SSSGame;
using SSSGame.Weather;
using UnityEngine;

namespace TorchFuelMod;

public class TorchFuelTracker : MonoBehaviour
{
    private float _timer;
    private bool _wasRaining;

    void Update()
    {
        _timer += Time.deltaTime;
        if (_timer < Plugin.CheckIntervalSeconds.Value) return;
        _timer = 0f;

        bool preventExtinguish = Plugin.PreventRainExtinguish.Value;
        bool autoRelight = Plugin.AutoRelightAfterRain.Value;

        bool isRaining = false;
        if (preventExtinguish || autoRelight)
        {
            var weather = WeatherSystem.Instance;
            isRaining = weather != null && weather.Precipitation_Rain > 0.05f;
        }

        bool rainJustStopped = _wasRaining && !isRaining;

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
                // Enforce ignoreWeather flag so rain/wind can never extinguish the torch.
                if (preventExtinguish && !fire.ignoreWeather)
                    fire.ignoreWeather = true;

                var state = fire.CurrentFireState;
                bool isOut = state == FireStructure.FireState.Extinguished
                          || state == FireStructure.FireState.Smoldering;

                if (isOut && fire.CurrentFuelVolume > 0f)
                {
                    // Re-light if we're preventing rain extinguish (safety net for already-out torches),
                    // or if auto-relight is on and rain just stopped.
                    if (preventExtinguish || (autoRelight && rainJustStopped))
                    {
                        fire.Rpc_ChangeFireState(FireStructure.FireState.Burning);
                        Plugin.Logger.LogInfo($"[TorchFuelMod] Re-lit '{fire.OwnerStructure?.StructureName}'.");
                    }
                }

                // Top off fuel.
                float deficit = fire.MaxFuelVolume - fire.CurrentFuelVolume;
                if (deficit > 0.01f)
                    fire.Rpc_AddFuel(deficit);
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[TorchFuelMod] Failed to update fire: {ex}");
            }
        }

        _wasRaining = isRaining;
    }
}
