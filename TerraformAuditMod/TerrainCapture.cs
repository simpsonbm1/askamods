using System;
using System.Collections.Generic;
using HarmonyLib;
using SSSGame;

namespace TerraformAuditMod;

// StreamingTerrainVS is a plain UnityEngine.MonoBehaviour with a PUBLIC non-virtual Awake() - it is
// NOT a NetworkBehaviour, so patching it is safe under this project's interop rules.
//
// StreamingTerrainVS.Awake() is the capture point: a postfix here registers every streamed terrain
// chunk into a static list as it appears, since there is no other enumeration of "currently
// streamed terrain" available.
//
// StreamingTerrainVS.Init() must NOT be patched. A Harmony patch on it throws
// System.NullReferenceException inside Harmony's generated replacement method (the `DMD<...>`
// frame) on nearly every terrain-streaming call - the postfix body itself never runs the code that
// would throw, so the fault is in the patched method, not the postfix. This floods the log
// (thousands of `Il2CppInterop` trampoline error frames per session) and breaks world load/terrain
// streaming.
[HarmonyPatch(typeof(StreamingTerrainVS), nameof(StreamingTerrainVS.Awake))]
public static class StreamingTerrainVSAwakePatch
{
    [HarmonyPostfix]
    public static void Postfix(StreamingTerrainVS __instance)
    {
        try { TerrainCapture.Register(__instance); }
        catch (Exception ex) { Plugin.Logger.LogError($"[TerraformAudit] Awake postfix error: {ex}"); }
    }
}

internal static class TerrainCapture
{
    private static readonly List<StreamingTerrainVS> _chunks = new();

    // Fire-verification - a patch that silently never runs is indistinguishable from a wrong
    // approach, so the capture point logs an unconditional first-fire line.
    private static bool _awakeFireLogged;

    public static void Register(StreamingTerrainVS? instance)
    {
        if (!_awakeFireLogged)
        {
            _awakeFireLogged = true;
            Plugin.Logger.LogInfo("[TerraformAudit] StreamingTerrainVS.Awake postfix FIRED (first).");
        }

        if (instance == null) return;

        lock (_chunks)
        {
            foreach (var existing in _chunks)
            {
                if (ReferenceEquals(existing, instance)) return;
            }
            _chunks.Add(instance);
        }
    }

    public static List<StreamingTerrainVS> GetChunks()
    {
        lock (_chunks)
        {
            // Terrain chunks are pooled and reused by the streaming system, so this list
            // accumulates wrappers whose native object has since been freed. A stale wrapper's
            // null-check can itself throw rather than cleanly evaluate to true, so each entry is
            // tested independently and removed on either outcome instead of letting one bad
            // wrapper's exception abort the whole cleanup.
            for (int i = _chunks.Count - 1; i >= 0; i--)
            {
                try
                {
                    if (_chunks[i] == null) _chunks.RemoveAt(i);
                }
                catch
                {
                    _chunks.RemoveAt(i);
                }
            }
            return new List<StreamingTerrainVS>(_chunks);
        }
    }

    public static void Clear()
    {
        lock (_chunks) { _chunks.Clear(); }
    }
}
