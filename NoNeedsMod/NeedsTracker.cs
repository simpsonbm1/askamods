using System;
using SSSGame;
using UnityEngine;

namespace NoNeedsMod;

public class NeedsTracker : MonoBehaviour
{
    private float _dtAccum;
    private float _realSecondsAccum;

    private PlayerCharacter? _trackedPlayer;
    private PlayerSurvival? _playerSurvival;

    private bool _playerPinnedOnce;
    private bool _villagersPinnedOnce;

    private string? _lastPlayerError;
    private string? _lastVillagerError;

    void Update()
    {
        _dtAccum += Time.deltaTime;
        _realSecondsAccum += Time.deltaTime;

        float interval = Mathf.Max(Plugin.TickSeconds.Value, 0.25f);
        if (_dtAccum < interval) return;

        _dtAccum = 0f;

        bool playerPinned = false;
        int villagersPinned = 0;

        try
        {
            playerPinned = PinPlayer();
        }
        catch (Exception ex)
        {
            string msg = ex.Message;
            if (msg != _lastPlayerError)
            {
                _lastPlayerError = msg;
                Plugin.Logger.LogError($"[NoNeedsMod] Player pin error: {ex}");
            }
        }

        try
        {
            villagersPinned = PinVillagers();
        }
        catch (Exception ex)
        {
            string msg = ex.Message;
            if (msg != _lastVillagerError)
            {
                _lastVillagerError = msg;
                Plugin.Logger.LogError($"[NoNeedsMod] Villager pin error: {ex}");
            }
        }

        if (Plugin.DebugLogging.Value && villagersPinned > 0 && !_villagersPinnedOnce)
        {
            _villagersPinnedOnce = true;
            Plugin.Logger.LogInfo($"[NoNeedsMod] Pinned needs for {villagersPinned} villagers");
        }

        if (_realSecondsAccum >= 60f)
        {
            _realSecondsAccum = 0f;
            if (Plugin.DebugLogging.Value)
            {
                Plugin.Logger.LogInfo($"[NoNeedsMod] tick: player={(playerPinned ? "yes" : "no")} villagers={villagersPinned}");
            }
        }
    }

    private bool PinPlayer()
    {
        if (!Plugin.PlayerEnabled.Value) return false;

        var player = Plugin.LocalPlayer;
        if (player != _trackedPlayer)
        {
            _trackedPlayer = player;
            _playerSurvival = null;
        }

        if (player == null) return false;

        if (_playerSurvival == null)
        {
            _playerSurvival = player.GetComponent<PlayerSurvival>();
        }

        var survival = _playerSurvival;
        if (survival == null) return false;
        if (!survival._hasAuthority) return false;
        if (!survival.Initialized) return false;

        bool loggedFirstPin = false;
        float foodBefore = 0f;
        float foodMax = 0f;

        if (Plugin.PlayerFood.Value)
        {
            var a = survival._foodVAttr;
            if (a != null)
            {
                if (!_playerPinnedOnce)
                {
                    foodBefore = a.GetValue();
                    foodMax = a.max;
                    loggedFirstPin = true;
                }
                a.SetValue(a.max);
            }
        }

        if (Plugin.PlayerWater.Value)
        {
            var a = survival._waterVAttr;
            if (a != null) a.SetValue(a.max);
        }

        if (Plugin.PlayerWarmth.Value)
        {
            var a = survival._warmthVAttr;
            if (a != null) a.SetValue(a.max);
        }

        if (Plugin.PlayerEnergy.Value)
        {
            var a = survival._energyVAttr;
            if (a != null) a.SetValue(a.max);
        }

        if (Plugin.DebugLogging.Value && loggedFirstPin && !_playerPinnedOnce)
        {
            _playerPinnedOnce = true;
            Plugin.Logger.LogInfo($"[NoNeedsMod] Player needs pinned (food {foodBefore:F1}->{foodMax:F1})");
        }

        return true;
    }

    private int PinVillagers()
    {
        if (!Plugin.VillagersEnabled.Value) return 0;

        int pinned = 0;

        for (int i = Plugin.TrackedVillagers.Count - 1; i >= 0; i--)
        {
            var v = Plugin.TrackedVillagers[i];
            if (v == null)
            {
                Plugin.TrackedVillagers.RemoveAt(i);
                continue;
            }

            if (!v.HasAuthority) continue;

            var survival = v.GetSurvival();
            if (survival == null) continue;
            if (!survival._hasAuthority) continue;
            if (!survival.Initialized) continue;

            if (Plugin.VillagersFood.Value)
            {
                var a = survival._foodVAttr;
                if (a != null) a.SetValue(a.max);
            }

            if (Plugin.VillagersWater.Value)
            {
                var a = survival._waterVAttr;
                if (a != null) a.SetValue(a.max);
            }

            if (Plugin.VillagersWarmth.Value)
            {
                var a = survival._warmthVAttr;
                if (a != null) a.SetValue(a.max);
            }

            if (Plugin.VillagersRest.Value)
            {
                var r = survival._restVariableAttribute;
                if (r != null) r.SetValue(r.max);
            }

            if (Plugin.VillagersHappiness.Value)
            {
                var h = v._happinessVAttr;
                if (h != null) h.SetValue(h.max);
            }

            pinned++;
        }

        return pinned;
    }
}
