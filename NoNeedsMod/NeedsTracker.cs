using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes;
using SandSailorStudio.Attributes;  // VariableAttribute
using SSSGame;
using UnityEngine;

namespace NoNeedsMod;

public class NeedsTracker : MonoBehaviour
{
    // Per-need bookkeeping for rate mode: the value this mod last wrote (or last observed while
    // priming). The next tick's movement is measured against it, never against the raw previous
    // frame, so scaling compounds correctly across ticks.
    private sealed class NeedState
    {
        public float Last;
        public bool Primed;
    }

    private const int PlayerNeedCount = 4;   // food, water, warmth, energy
    private const int VillagerNeedCount = 5; // food, water, warmth, rest, happiness

    private float _dtAccum;
    private float _realSecondsAccum;

    private PlayerCharacter? _trackedPlayer;
    private PlayerSurvival? _playerSurvival;

    private readonly NeedState[] _playerStates = NewStates(PlayerNeedCount);

    // Keyed by the villager's native object pointer (stable for that native object's lifetime).
    // Only floats and bools are stored — no interop wrappers are cached across world sessions.
    private readonly Dictionary<IntPtr, NeedState[]> _villagerStates = new();
    private readonly HashSet<IntPtr> _seenVillagers = new();

    private bool _playerPinnedOnce;
    private bool _villagersPinnedOnce;

    private string? _lastPlayerError;
    private string? _lastVillagerError;

    private static NeedState[] NewStates(int count)
    {
        var states = new NeedState[count];
        for (int i = 0; i < count; i++) states[i] = new NeedState();
        return states;
    }

    void Update()
    {
        _dtAccum += Time.deltaTime;
        _realSecondsAccum += Time.deltaTime;

        float interval = Mathf.Max(Plugin.TickSeconds.Value, 0.25f);

        // Rate mode reads the need's own movement between ticks, so a long interval makes the bar
        // visibly overshoot before the correction lands. Tighten it while any need is rate-driven.
        if (AnyRateMode()) interval = Mathf.Min(interval, 0.5f);

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
            PruneVillagerStates();
            if (Plugin.DebugLogging.Value)
            {
                Plugin.Logger.LogInfo($"[NoNeedsMod] tick: player={(playerPinned ? "yes" : "no")} villagers={villagersPinned}");
            }
        }
    }

    // True when at least one enabled need is driven by a rate rather than pinned at max.
    private bool AnyRateMode()
    {
        if (Plugin.PlayerEnabled.Value)
        {
            if (Plugin.PlayerFood.Value && Plugin.PlayerFoodDrain.Value > 0f) return true;
            if (Plugin.PlayerWater.Value && Plugin.PlayerWaterDrain.Value > 0f) return true;
            if (Plugin.PlayerWarmth.Value && Plugin.PlayerWarmthDrain.Value > 0f) return true;
            if (Plugin.PlayerEnergy.Value && Plugin.PlayerEnergyDrain.Value > 0f) return true;
        }

        if (Plugin.VillagersEnabled.Value)
        {
            if (Plugin.VillagersFood.Value && Plugin.VillagersFoodDrain.Value > 0f) return true;
            if (Plugin.VillagersWater.Value && Plugin.VillagersWaterDrain.Value > 0f) return true;
            if (Plugin.VillagersWarmth.Value && Plugin.VillagersWarmthDrain.Value > 0f) return true;
            if (Plugin.VillagersRest.Value && Plugin.VillagersRestDrain.Value > 0f) return true;
            if (Plugin.VillagersHappiness.Value && Plugin.VillagersHappinessDrain.Value > 0f) return true;
        }

        return false;
    }

    /// <summary>
    /// Applies one need's setting to one attribute.
    /// drain == 0 pins the attribute at max (the mod's original behaviour). Otherwise the movement
    /// since the last pass is rescaled: falling movement by <paramref name="drain"/>, rising
    /// movement by <paramref name="gain"/>, and the corrected value is written back.
    /// </summary>
    private static void Apply(VariableAttribute? a, NeedState st, float drain, float gain)
    {
        if (a == null) return;

        float max = a.max;
        float min = a.min;

        if (drain <= 0f)
        {
            a.SetValue(max);
            st.Last = max;
            st.Primed = true;
            return;
        }

        float cur = a.GetValue();

        if (!st.Primed)
        {
            st.Last = cur;
            st.Primed = true;
            return;
        }

        float range = max - min;
        float delta = cur - st.Last;

        // A jump across most of the bar is a world load, respawn or sleep transition, not the
        // ordinary drain this mod governs. Re-baseline instead of scaling it.
        if (range <= 0f || Mathf.Abs(delta) >= range * 0.9f)
        {
            st.Last = cur;
            return;
        }

        float scaled = delta < 0f ? delta * drain : delta * gain;
        float corrected = Mathf.Clamp(st.Last + scaled, min, max);

        if (Mathf.Abs(corrected - cur) > 0.0001f) a.SetValue(corrected);
        st.Last = corrected;
    }

    private bool PinPlayer()
    {
        if (!Plugin.PlayerEnabled.Value) return false;

        var player = Plugin.LocalPlayer;
        if (player != _trackedPlayer)
        {
            _trackedPlayer = player;
            _playerSurvival = null;
            foreach (var s in _playerStates) s.Primed = false;
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
                Apply(a, _playerStates[0], Plugin.PlayerFoodDrain.Value, Plugin.PlayerFoodGain.Value);
            }
        }

        if (Plugin.PlayerWater.Value)
        {
            Apply(survival._waterVAttr, _playerStates[1], Plugin.PlayerWaterDrain.Value, Plugin.PlayerWaterGain.Value);
        }

        if (Plugin.PlayerWarmth.Value)
        {
            Apply(survival._warmthVAttr, _playerStates[2], Plugin.PlayerWarmthDrain.Value, Plugin.PlayerWarmthGain.Value);
        }

        if (Plugin.PlayerEnergy.Value)
        {
            Apply(survival._energyVAttr, _playerStates[3], Plugin.PlayerEnergyDrain.Value, Plugin.PlayerEnergyGain.Value);
        }

        if (Plugin.DebugLogging.Value && loggedFirstPin && !_playerPinnedOnce)
        {
            _playerPinnedOnce = true;
            Plugin.Logger.LogInfo($"[NoNeedsMod] Player needs handled (food {foodBefore:F1}, max {foodMax:F1})");
        }

        return true;
    }

    private int PinVillagers()
    {
        if (!Plugin.VillagersEnabled.Value) return 0;

        int pinned = 0;
        _seenVillagers.Clear();

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

            var states = GetVillagerStates(v);
            if (states == null) continue;

            if (Plugin.VillagersFood.Value)
            {
                Apply(survival._foodVAttr, states[0], Plugin.VillagersFoodDrain.Value, Plugin.VillagersFoodGain.Value);
            }

            if (Plugin.VillagersWater.Value)
            {
                Apply(survival._waterVAttr, states[1], Plugin.VillagersWaterDrain.Value, Plugin.VillagersWaterGain.Value);
            }

            if (Plugin.VillagersWarmth.Value)
            {
                Apply(survival._warmthVAttr, states[2], Plugin.VillagersWarmthDrain.Value, Plugin.VillagersWarmthGain.Value);
            }

            if (Plugin.VillagersRest.Value)
            {
                Apply(survival._restVariableAttribute, states[3], Plugin.VillagersRestDrain.Value, Plugin.VillagersRestGain.Value);
            }

            if (Plugin.VillagersHappiness.Value)
            {
                Apply(v._happinessVAttr, states[4], Plugin.VillagersHappinessDrain.Value, Plugin.VillagersHappinessGain.Value);
            }

            pinned++;
        }

        return pinned;
    }

    // Per-villager state, keyed by native pointer. The managed wrapper is never stored.
    private NeedState[]? GetVillagerStates(Villager v)
    {
        IntPtr key = (object)v is Il2CppObjectBase b ? b.Pointer : IntPtr.Zero;
        if (key == IntPtr.Zero) return null;

        _seenVillagers.Add(key);

        if (!_villagerStates.TryGetValue(key, out var states))
        {
            states = NewStates(VillagerNeedCount);
            _villagerStates[key] = states;
        }

        return states;
    }

    // Drop state for villagers that were not touched in the most recent pass (despawned, or the
    // world was left). Runs on the 60 s summary beat.
    private void PruneVillagerStates()
    {
        if (_villagerStates.Count == 0) return;

        List<IntPtr>? stale = null;
        foreach (var key in _villagerStates.Keys)
        {
            if (_seenVillagers.Contains(key)) continue;
            (stale ??= new List<IntPtr>()).Add(key);
        }

        if (stale == null) return;
        foreach (var key in stale) _villagerStates.Remove(key);
    }
}
