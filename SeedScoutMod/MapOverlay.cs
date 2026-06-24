using System;
using System.Collections.Generic;
using HarmonyLib;
using SSSGame.UI;
using UnityEngine;
using UnityEngine.UI;

namespace SeedScoutMod;

// Draw POIs directly on the in-game map as plain UI Images parented to the map content:
//   caves (mines) = gray, lakes = blue, hostiles (dens/camps) = red.
// No game entities, no marker registration, no networking — cannot freeze or disconnect.
internal static class MapOverlay
{
    private static readonly List<(Vector2 world, GameObject go)> _dots = new();
    private static MapMenu? _map;

    internal static void Build(MapMenu map)
    {
        Clear();
        if (!Plugin.EnableMarkers.Value) return;
        _map = map;
        try
        {
            var content = map.Content;
            if (content == null) { Plugin.Logger.LogWarning("SeedScout: map Content null."); return; }

            // Light gray reads clearly against the blue lakes (was cyan — too close to lake blue).
            var caveGray = new Color(0.78f, 0.78f, 0.80f);
            foreach (var cave in Scout.Caves) AddDot(map, content, cave, caveGray);
            foreach (var l in Plugin.Lakes) AddDot(map, content, l.Pos, new Color(0.25f, 0.55f, 1f));
            foreach (var h in Plugin.Hostiles) AddDot(map, content, h.Pos, Color.red);

            Plugin.Logger.LogInfo($"SeedScout: drew {_dots.Count} map dot(s) " +
                                  $"(caves={Scout.Caves.Count} gray / lakes={Plugin.Lakes.Count} blue / hostiles={Plugin.Hostiles.Count} red).");
        }
        catch (Exception e) { Plugin.Logger.LogWarning($"SeedScout: map overlay build err: {e.Message}"); }
    }

    private static void AddDot(MapMenu map, RectTransform content, Vector2 world, Color color)
    {
        var go = new GameObject("SeedScout_Dot");
        var img = go.AddComponent<Image>();          // pulls in RectTransform + CanvasRenderer
        img.color = color;
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(content, false);
        rt.sizeDelta = new Vector2(16f, 16f);
        rt.anchoredPosition = map.GetMappedPosition(new Vector3(world.x, 0f, world.y));
        _dots.Add((world, go));
    }

    // Keep dots aligned as the map pans/zooms (cheap — only a handful of dots).
    internal static void Reposition()
    {
        if (_map == null) return;
        try
        {
            foreach (var (world, go) in _dots)
            {
                if (go == null) continue;
                go.GetComponent<RectTransform>().anchoredPosition =
                    _map.GetMappedPosition(new Vector3(world.x, 0f, world.y));
            }
        }
        catch { /* map tearing down */ }
    }

    internal static void Clear()
    {
        foreach (var (_, go) in _dots)
            if (go != null) UnityEngine.Object.Destroy(go);
        _dots.Clear();
        _map = null;
    }
}

[HarmonyPatch(typeof(MapMenu), "OnActivate")]
internal static class MapOnActivatePatch { static void Postfix(MapMenu __instance) => MapOverlay.Build(__instance); }

[HarmonyPatch(typeof(MapMenu), "OnUpdate")]
internal static class MapOnUpdatePatch { static void Postfix() => MapOverlay.Reposition(); }

[HarmonyPatch(typeof(MapMenu), "OnDeactivate")]
internal static class MapOnDeactivatePatch { static void Postfix() => MapOverlay.Clear(); }

[HarmonyPatch(typeof(MapMenu), "OnClosed")]
internal static class MapOnClosedPatch { static void Postfix() => MapOverlay.Clear(); }
