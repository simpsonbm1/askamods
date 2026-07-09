using System;
using System.Collections.Generic;
using SSSGame;
using UnityEngine;

namespace HealthRegenMod;

public class RegenTracker : MonoBehaviour
{
    private PlayerCharacter? _trackedPlayer;
    private float _lastSeenDamageTime = -1f;
    private float _secondsSinceLastHit;
    private float _secondsSinceLastTick;

    private class VState
    {
        public float lastSeenDamageTime = -1f;
        public float sinceHit;
        public float sinceTick;
        public bool regenActive;
    }

    private static readonly Dictionary<Villager, VState> _vStates = new();
    private float _dtAccum;

    internal static void ForgetVillager(Villager v)
    {
        try
        {
            if (v != null) _vStates.Remove(v);
        }
        catch (Exception ex) { Plugin.Logger.LogError($"[HealthRegenMod] ForgetVillager: {ex}"); }
    }

    void Update()
    {
        // Throttled to every 4th frame for framerate (project convention) — accumulate delta
        // time every frame regardless so throttled-out time isn't lost.
        _dtAccum += Time.deltaTime;
        if ((Time.frameCount & 3) != 0) return;

        float dt = _dtAccum;
        _dtAccum = 0f;

        // Independent passes — the player pass's early-returns (no player, full HP, in combat)
        // must not starve the villager pass.
        UpdatePlayer(dt);
        UpdateVillagers(dt);
    }

    private void UpdatePlayer(float dt)
    {
        var player = Plugin.LocalPlayer;
        if (player != _trackedPlayer)
        {
            // Reference changed (acquired on spawn, cleared on despawn) — reset tracking.
            _trackedPlayer = player;
            _lastSeenDamageTime = -1f;
            _secondsSinceLastHit = 0f;
            _secondsSinceLastTick = 0f;
        }

        if (player == null) return;

        if (player.IsDead)
        {
            _secondsSinceLastHit = 0f;
            _secondsSinceLastTick = 0f;
            return;
        }

        // No event to subscribe to (IL2CPP delegate subscription is unreliable here) —
        // detect a new hit by watching LastDamageTime change instead of polling "in combat".
        var history = player.GetDamageTakenHistory();
        float lastDamageTime = history != null ? history.LastDamageTime : -1f;

        if (lastDamageTime != _lastSeenDamageTime)
        {
            _lastSeenDamageTime = lastDamageTime;
            _secondsSinceLastHit = 0f;
            _secondsSinceLastTick = 0f;
        }
        else
        {
            _secondsSinceLastHit += dt;
        }

        if (_secondsSinceLastHit < Plugin.OutOfCombatSeconds.Value) return;
        if (player.CurrentHealth >= player.MaxHealth) return;

        float interval = Plugin.SecondsPerTick.Value;
        if (interval <= 0f)
        {
            // Continuous/smooth mode — HealPerTick is treated as HP per second.
            player.CurrentHealth = Mathf.Min(
                player.CurrentHealth + Plugin.HealPerTick.Value * dt,
                player.MaxHealth);
            return;
        }

        // Discrete tick mode: heal HealPerTick HP every `interval` seconds.
        _secondsSinceLastTick += dt;
        while (_secondsSinceLastTick >= interval)
        {
            _secondsSinceLastTick -= interval;
            player.CurrentHealth = Mathf.Min(
                player.CurrentHealth + Plugin.HealPerTick.Value,
                player.MaxHealth);
            if (player.CurrentHealth >= player.MaxHealth)
            {
                _secondsSinceLastTick = 0f;
                break;
            }
        }
    }

    private void UpdateVillagers(float dt)
    {
        if (!Plugin.ApplyToVillagers.Value) return;

        for (int i = Plugin.TrackedVillagers.Count - 1; i >= 0; i--)
        {
            var v = Plugin.TrackedVillagers[i];
            if (v == null)
            {
                Plugin.TrackedVillagers.RemoveAt(i);
                continue;
            }

            try
            {
                if (v.IsDead)
                {
                    if (_vStates.TryGetValue(v, out var deadState))
                    {
                        deadState.sinceHit = 0f;
                        deadState.sinceTick = 0f;
                        deadState.regenActive = false;
                    }
                    continue;
                }

                if (!v.HasAuthority) continue;

                if (!_vStates.TryGetValue(v, out var state))
                {
                    state = new VState();
                    _vStates[v] = state;
                }

                var hist = v.GetDamageTakenHistory();
                float last = hist != null ? hist.LastDamageTime : -1f;

                if (last != state.lastSeenDamageTime)
                {
                    state.lastSeenDamageTime = last;
                    state.sinceHit = 0f;
                    state.sinceTick = 0f;
                    if (state.regenActive && Plugin.VillagerDebugLogging.Value)
                        Plugin.Logger.LogInfo("[HealthRegenMod] Villager regen interrupted (took damage).");
                    state.regenActive = false;
                }
                else
                {
                    state.sinceHit += dt;
                }

                if (state.sinceHit < Plugin.VillagerOutOfCombatSeconds.Value) continue;

                if (v.CurrentHealth >= v.MaxHealth)
                {
                    state.regenActive = false;
                    continue;
                }

                if (!state.regenActive && Plugin.VillagerDebugLogging.Value)
                {
                    Plugin.Logger.LogInfo($"[HealthRegenMod] Villager regen started (hp {v.CurrentHealth:F0}/{v.MaxHealth:F0}).");
                }
                state.regenActive = true;

                float vInterval = Plugin.VillagerSecondsPerTick.Value;
                if (vInterval <= 0f)
                {
                    v.CurrentHealth = Mathf.Min(v.CurrentHealth + Plugin.VillagerHealPerTick.Value * dt, v.MaxHealth);
                    continue;
                }

                state.sinceTick += dt;
                while (state.sinceTick >= vInterval)
                {
                    state.sinceTick -= vInterval;
                    v.CurrentHealth = Mathf.Min(v.CurrentHealth + Plugin.VillagerHealPerTick.Value, v.MaxHealth);
                    if (v.CurrentHealth >= v.MaxHealth)
                    {
                        state.sinceTick = 0f;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[HealthRegenMod] Villager regen loop error: {ex}");
            }
        }
    }
}
