using SSSGame;
using UnityEngine;

namespace HealthRegenMod;

public class RegenTracker : MonoBehaviour
{
    private PlayerCharacter? _trackedPlayer;
    private float _lastSeenDamageTime = -1f;
    private float _secondsSinceLastHit;
    private float _secondsSinceLastTick;

    void Update()
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
            _secondsSinceLastHit += Time.deltaTime;
        }

        if (_secondsSinceLastHit < Plugin.OutOfCombatSeconds.Value) return;
        if (player.CurrentHealth >= player.MaxHealth) return;

        float interval = Plugin.SecondsPerTick.Value;
        if (interval <= 0f)
        {
            // Continuous/smooth mode — HealPerTick is treated as HP per second.
            player.CurrentHealth = Mathf.Min(
                player.CurrentHealth + Plugin.HealPerTick.Value * Time.deltaTime,
                player.MaxHealth);
            return;
        }

        // Discrete tick mode: heal HealPerTick HP every `interval` seconds.
        _secondsSinceLastTick += Time.deltaTime;
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
}
