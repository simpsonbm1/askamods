using System;
using System.Collections.Generic;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using SandSailorStudio.Streaming;
using SSSGame;
using SSSGame.Combat;
using SSSGame.UI;
using UnityEngine;
using UnityEngine.EventSystems;

namespace DenRespawnMod.Patches;

// Runtime-PROVEN to fire for dens (SeedScout mod used this hook already).
[HarmonyPatch(typeof(Den), nameof(Den.Start))]
internal static class DenStartPatch
{
    static void Postfix(Den __instance)
    {
        try
        {
            if (__instance == null) return;
            Plugin.TrackDen(__instance);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[DenRespawn] DenStartPatch: {ex}");
        }
    }
}

// Dedupe in Plugin.TrackDen makes a double-capture (Start + Spawned) harmless.
[HarmonyPatch(typeof(Den), nameof(Den.Spawned))]
internal static class DenSpawnedPatch
{
    static void Postfix(Den __instance)
    {
        try
        {
            if (__instance == null) return;
            Plugin.TrackDen(__instance);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[DenRespawn] DenSpawnedPatch: {ex}");
        }
    }
}

// Method returns Boolean with no params — safe signature (the project's by-ref gotcha is about
// the GAME method's own Single&/Int32&/Boolean& params, not Harmony's own "ref __result").
[HarmonyPatch(typeof(Den), nameof(Den.IsBlockedByStructures))]
internal static class DenIsBlockedByStructuresPatch
{
    private static bool _fireVerified;
    private static bool _suppressedLogged;

    static void Postfix(Den __instance, ref bool __result)
    {
        try
        {
            if (!_fireVerified)
            {
                _fireVerified = true;
                Plugin.Logger.LogInfo("[DenRespawn] FIRE-VERIFY: IsBlockedByStructures patch is live");
            }

            if (Plugin.AllowRespawnNearStructures.Value && __result)
            {
                __result = false;
                if (!_suppressedLogged)
                {
                    _suppressedLogged = true;
                    Plugin.Logger.LogInfo("[DenRespawn] Suppressed a den structure-block (IsBlockedByStructures -> false)");
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[DenRespawn] DenIsBlockedByStructuresPatch: {ex}");
        }
    }
}

[HarmonyPatch(typeof(PopulationSpawner), "_UpdateBlockedByStructures")]
internal static class PopulationSpawnerBlockedByStructuresPatch
{
    private static bool _fireVerified;
    private static bool _clearedLogged;

    static void Postfix(PopulationSpawner __instance)
    {
        try
        {
            if (!_fireVerified)
            {
                _fireVerified = true;
                Plugin.Logger.LogInfo("[DenRespawn] FIRE-VERIFY: PopulationSpawner._UpdateBlockedByStructures patch is live");
            }

            if (__instance == null) return;

            if (Plugin.AllowRespawnNearStructures.Value && __instance.RespawningBlockedByStructures)
            {
                __instance.RespawningBlockedByStructures = false;
                if (!_clearedLogged)
                {
                    _clearedLogged = true;
                    Plugin.Logger.LogInfo("[DenRespawn] Cleared RespawningBlockedByStructures on a spawner");
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[DenRespawn] PopulationSpawnerBlockedByStructuresPatch: {ex}");
        }
    }
}

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.Spawned))]
internal static class PlayerSpawnedPatch
{
    static void Postfix(PlayerCharacter __instance)
    {
        try
        {
            if (!__instance.HasAuthority) return;
            Plugin.LocalPlayer = __instance;
            Plugin.Logger.LogInfo("[DenRespawn] Local player character registered.");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[DenRespawn] PlayerSpawnedPatch: {ex}");
        }
    }
}

[HarmonyPatch(typeof(PlayerCharacter), nameof(PlayerCharacter.Despawned))]
internal static class PlayerDespawnedPatch
{
    static void Postfix(PlayerCharacter __instance)
    {
        try
        {
            if (Plugin.LocalPlayer == __instance)
            {
                Plugin.LocalPlayer = null;
                Plugin.Logger.LogInfo("[DenRespawn] Local player character cleared.");
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[DenRespawn] PlayerDespawnedPatch: {ex}");
        }
    }
}

// Revive gate (v1.1.0). Den.Revive() is Void with no params — a safe signature (the project's
// by-ref gotcha is about the GAME method's own Single&/Int32&/Boolean& params, not this one).
// Distinguishes OUR calls (Plugin.AllowReviveCall, set only while DenTracker.RunRefresh is inside
// den.Revive()) from FOREIGN calls — the game's own ~yearly natural respawn driver, or any other
// unknown caller. The foreign-call log line is unconditional (our detector for the natural path);
// suppression only happens when SuppressNaturalRespawns is on. Whole body try/catch — on any
// exception, return true so we never accidentally block the game's own revival.
[HarmonyPatch(typeof(Den), nameof(Den.Revive))]
internal static class DenRevivePatch
{
    static bool Prefix(Den __instance)
    {
        try
        {
            if (Plugin.AllowReviveCall) return true;

            string name = "?";
            try { name = __instance != null ? __instance.GetName() : "?"; } catch { }

            Plugin.Logger.LogInfo($"[DenRespawn] Foreign Den.Revive() on '{name}' (day {DayCounter.CurrentDay}) — natural respawn driver?");

            if (Plugin.SuppressNaturalRespawns.Value)
            {
                Plugin.Logger.LogInfo($"[DenRespawn] BLOCKED natural revive of '{name}'");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[DenRespawn] DenRevivePatch error: {ex}");
            return true;
        }
    }
}

// Capture the WorldStreamingManager from its own lifecycle — never FindObjectsByType (IL2CPP
// trampoline crash). Mirrors SeedScoutMod's StreamingCapturePatch exactly. Feeds
// DenTracker.EnqueueRemoteRefresh's force-load path; refreshed on every world load.
[HarmonyPatch(typeof(WorldStreamingManager), "Awake")]
internal static class DenStreamingCapturePatch
{
    static void Postfix(WorldStreamingManager __instance)
    {
        try
        {
            Plugin.StreamingManager = __instance;
            Plugin.Logger.LogInfo("[DenRespawn] WorldStreamingManager captured.");
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[DenRespawn] DenStreamingCapturePatch: {ex}");
        }
    }
}

// Map revive (v1.1.0/v1.1.2): Shift+Left-click (configurable modifier) a den's map pin to revive
// that den remotely. Shared tail + modifier gate used by both the (probably-inlined) native click
// method patch and the vtable-dispatched OnPointerClick patch below.
internal static class DenMapRevive
{
    private static bool _modifierParsed;
    private static KeyCode _modifierKey;
    private static bool _modifierEnabled;

    // Time-window dedupe (v1.1.3) — replaces the old Time.frameCount guard. Multiple click-paths
    // (positional-fallback MapMenu.OnPointerClick, the per-frame hover+click check in
    // DenTracker.Update()) can observe the same physical click on different frames, so a
    // same-frame guard wasn't enough to prevent double-handling.
    private static float _lastHandledAt = -999f;

    // Den pins are labeled "Small Cemetery"/"Large Cemetery" on the map while the internal type
    // name is "Skeleton Den (Cluster)" — matched in TryRevive's looksLikeDen check below (v1.1.3).
    internal static readonly HashSet<string> KnownDenTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Wulfar Den", "Skeleton Den", "Skeleton Den Cluster", "Wight Den", "Draugar den", "Baby Crawler Den", "Large Cemetery"
    };

    // The open MapMenu, for the widget->marker resolver below. Populated primarily from
    // MapMenu.OnActivate (fires when the map opens) and, as a free safety net, from
    // DenMapPointerClickPatch's own postfix. Never cached across world sessions — nulled in
    // Plugin.NoteWorldLeft alongside the mod's other per-world statics.
    internal static MapMenu? MapMenuInstance;

    // Hover tracking (v1.1.4). CompassObjectiveMarker.OnSelect fires on mere mouse-hover (not
    // click) — confirmed in-game — so OnSelect/OnDeselect now only track which pin is currently
    // hovered; the actual revive trigger is a per-frame Shift+Left-mouse-press check in
    // DenTracker.Update(). Never cached across world sessions — cleared in Plugin.NoteWorldLeft
    // alongside the mod's other per-world statics.
    internal static IntPtr HoveredWidgetPtr = IntPtr.Zero;
    internal static string HoveredName = "";
    internal static Vector3 HoveredPos;
    internal static bool HaveHovered;

    internal static bool ModifierHeld()
    {
        if (!_modifierParsed)
        {
            _modifierParsed = true;
            string modStr = Plugin.MapReviveModifier.Value;
            if (string.IsNullOrWhiteSpace(modStr))
            {
                _modifierEnabled = false;
            }
            else if (Enum.TryParse<KeyCode>(modStr, true, out var kc))
            {
                _modifierKey = kc;
                _modifierEnabled = true;
            }
            else
            {
                _modifierEnabled = false;
                Plugin.Logger.LogWarning($"[DenRespawn] MapReviveModifier '{modStr}' is not a valid KeyCode — map revive disabled.");
            }
        }

        if (!_modifierEnabled) return false;
        return Input.GetKey(_modifierKey);
    }

    // Shared widget->marker resolver (v1.1.3), extracted from the old DenMapPointerClickPatch inline
    // scan. Given a clicked pin widget, finds its matching icon in MapMenuInstance's
    // _objectiveContainer._icons by IL2CPP pointer equality (boxing pattern on BOTH sides — managed
    // as/is casts lie for interop objects under a base declared type) and reads the marker's name +
    // world position off its WorldObjectiveMarker.
    internal static bool ResolveMarkerFromWidget(CompassObjectiveMarker widget, out string name, out Vector3 pos)
    {
        name = "";
        pos = default;

        try
        {
            var mapMenu = MapMenuInstance;
            if (mapMenu == null || widget == null) return false;

            var container = mapMenu._objectiveContainer;
            var icons = container?._icons;
            if (icons == null) return false;

            IntPtr widgetPtr = (object)widget is Il2CppObjectBase widgetBase ? widgetBase.Pointer : IntPtr.Zero;
            if (widgetPtr == IntPtr.Zero) return false;

            for (int i = 0; i < icons.Count; i++)
            {
                var icon = icons[i];
                if (icon == null) continue;

                var iconWidget = icon.widget;
                IntPtr iconWidgetPtr = (object)iconWidget is Il2CppObjectBase iconWidgetBase ? iconWidgetBase.Pointer : IntPtr.Zero;
                if (iconWidgetPtr == IntPtr.Zero || iconWidgetPtr != widgetPtr) continue;

                var wMarker = icon._wMarker;
                if (wMarker == null) return false;

                bool havePos = false;
                try { name = wMarker.CustomName ?? ""; } catch { }
                try { pos = wMarker.transform.position; havePos = true; } catch { }
                return havePos;
            }
        }
        catch { }

        return false;
    }

    // Double-handling guard: if more than one click-path (the DenTracker.Update() hover+click
    // check, MapMenu.OnPointerClick) fires for the same physical click, only the first one through
    // wins within the time window.
    internal static void TryRevive(string name, Vector3 pos)
    {
        if (Time.unscaledTime - _lastHandledAt < 0.3f) return;
        _lastHandledAt = Time.unscaledTime;

        var rec = DenRegistry.FindNearest(pos, Plugin.MapPinMatchRadius.Value);

        if (rec == null)
        {
            bool looksLikeDen = name.IndexOf("Den", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Cemetery", StringComparison.OrdinalIgnoreCase) >= 0
                || KnownDenTypes.Contains(name);
            if (looksLikeDen)
            {
                // Provisional record — state unknown until scanned, so leave Defeated at its
                // default (false) so a never-visited pinned den is still eligible for revive.
                rec = DenRegistry.Upsert(pos, name);
            }
        }

        if (rec == null)
        {
            DenTracker.Instance?.Notify("No known den at this map pin");
            Plugin.Logger.LogInfo($"[DenRespawn] Map click: no known den near pin '{name}'.");
            return;
        }

        float dist = -1f;
        if (Plugin.LocalPlayer != null)
        {
            try { dist = Vector3.Distance(Plugin.LocalPlayer.transform.position, pos); } catch { }
        }

        DenTracker.Instance?.EnqueueRemoteRefresh(rec, "map");

        string msg = dist >= 0
            ? $"Reviving {rec.TypeName} ({dist:F0} m away)..."
            : $"Reviving {rec.TypeName}...";
        DenTracker.Instance?.Notify(msg);
    }
}

// Original map-click hook. _OnMarkersLeftClick is a small private method suspected to be
// AOT-inlined into its caller (its FIRE-VERIFY line has never appeared in the log despite no
// patch errors) — kept as a harmless no-op fallback in case it turns out NOT to be inlined on
// some path; DenMapRevive's frame guard prevents double-handling if it ever fires alongside
// DenMapPointerClickPatch below.
[HarmonyPatch(typeof(MapMenu), "_OnMarkersLeftClick")]
internal static class DenMapClickPatch
{
    private static bool _fireVerified;

    static void Postfix(WorldObjectiveMarker marker)
    {
        try
        {
            if (!_fireVerified)
            {
                _fireVerified = true;
                Plugin.Logger.LogInfo("[DenRespawn] FIRE-VERIFY: MapMenu._OnMarkersLeftClick patch is live");
            }

            if (!DenMapRevive.ModifierHeld()) return;
            if (marker == null) return;

            bool havePos = false;
            Vector3 pos = default;
            try { pos = marker.transform.position; havePos = true; } catch { }
            if (!havePos) return;

            string name = "";
            try { name = marker.CustomName ?? ""; } catch { }

            DenMapRevive.TryRevive(name, pos);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[DenRespawn] DenMapClickPatch error: {ex}");
        }
    }
}

// Real map-click hook (v1.1.2). MapMenu.OnPointerClick is an IPointerClickHandler interface
// method — vtable/interface-dispatched, so unlike _OnMarkersLeftClick it CANNOT be AOT-inlined
// away. We resolve the clicked pin ourselves from the UI raycast result instead of relying on the
// game to hand us a WorldObjectiveMarker.
[HarmonyPatch(typeof(MapMenu), "OnPointerClick")]
internal static class DenMapPointerClickPatch
{
    private static bool _fireVerified;

    static void Postfix(MapMenu __instance, PointerEventData eventData)
    {
        try
        {
            if (!_fireVerified)
            {
                _fireVerified = true;
                Plugin.Logger.LogInfo("[DenRespawn] FIRE-VERIFY: MapMenu.OnPointerClick patch is live");
            }

            if (eventData == null || __instance == null) return;

            // Free safety net alongside MapMenu.OnActivate — either populates MapMenuInstance for
            // the widget->marker resolver used here and by the pin-click patches below.
            DenMapRevive.MapMenuInstance = __instance;

            GameObject? go = null;
            try { go = eventData.pointerCurrentRaycast.gameObject; } catch { }
            if (go == null)
            {
                try { go = eventData.pointerPressRaycast.gameObject; } catch { }
            }

            // Walk up from the hit GameObject looking for the pin widget (singular GetComponent
            // only — the plural GetComponentsInParent throws MissingMethodException through the
            // IL2CPP trampoline).
            CompassObjectiveMarker? widget = null;
            if (go != null)
            {
                Transform? t = go.transform;
                for (int i = 0; i < 12 && t != null; i++)
                {
                    var found = t.GetComponent<CompassObjectiveMarker>();
                    if (found != null) { widget = found; break; }
                    t = t.parent;
                }
            }

            bool havePos = false;
            Vector3 pos = default;
            string name = "";

            if (widget != null)
            {
                havePos = DenMapRevive.ResolveMarkerFromWidget(widget, out name, out pos);
            }

            if (!havePos)
            {
                // Positional fallback — no widget/marker resolved, but we can still translate the
                // click into a world position and let FindNearest match an already-registered den.
                try
                {
                    if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                            __instance.Content, eventData.position, eventData.pressEventCamera, out Vector2 local))
                    {
                        pos = __instance.GetWorldPosition(local);
                        name = "";
                        havePos = true;
                    }
                }
                catch { }
            }

            if (Plugin.DenDiagnostics.Value)
            {
                string posStr = havePos ? pos.ToString() : "n/a";
                Plugin.Logger.LogInfo(
                    $"[DenRespawn] MapMenu.OnPointerClick: button={eventData.button}, modifierHeld={DenMapRevive.ModifierHeld()}, " +
                    $"hitGO='{(go != null ? go.name : "null")}', widgetFound={widget != null}, markerResolved={havePos}, " +
                    $"name='{name}', pos={posStr}");
            }

            if (!DenMapRevive.ModifierHeld()) return;
            if (eventData.button != PointerEventData.InputButton.Left) return;
            if (!havePos) return;

            DenMapRevive.TryRevive(name, pos);
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[DenRespawn] DenMapPointerClickPatch error: {ex}");
        }
    }
}

// Populates DenMapRevive.MapMenuInstance as soon as the map opens (v1.1.3) — the primary source
// for the widget->marker resolver used by the pin-click patches below. OnActivate is a WidgetMenu
// lifecycle method (virtual override on MapMenu), not a leaf/inlined private, so it's safe to patch.
[HarmonyPatch(typeof(MapMenu), "OnActivate")]
internal static class MapMenuOnActivatePatch
{
    private static bool _fireVerified;

    static void Postfix(MapMenu __instance)
    {
        try
        {
            if (!_fireVerified)
            {
                _fireVerified = true;
                Plugin.Logger.LogInfo("[DenRespawn] FIRE-VERIFY: MapMenu.OnActivate patch is live");
            }

            DenMapRevive.MapMenuInstance = __instance;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[DenRespawn] MapMenuOnActivatePatch error: {ex}");
        }
    }
}

// Pin hover-tracking via OnSelect (v1.1.4, was click-trigger in v1.1.3). Clicks landing DIRECTLY
// on a map pin never reach MapMenu.OnPointerClick — the pin widget (CompassObjectiveMarker, an
// EventSystem selectable) swallows the click for its own compass-tracking-eyeball toggle. BUT
// OnSelect fires on mere mouse-hover too (confirmed in-game + log triage) — so this no longer
// revives directly. It only records which pin is currently hovered; DenTracker.Update() checks
// Shift+Left-mouse-press against that hover state every frame to decide when an actual click
// happened.
[HarmonyPatch(typeof(CompassObjectiveMarker), "OnSelect")]
internal static class DenPinSelectPatch
{
    private static bool _fireVerified;

    static void Postfix(CompassObjectiveMarker __instance)
    {
        try
        {
            if (!_fireVerified)
            {
                _fireVerified = true;
                Plugin.Logger.LogInfo("[DenRespawn] FIRE-VERIFY: CompassObjectiveMarker.OnSelect patch is live");
            }

            bool resolved = DenMapRevive.ResolveMarkerFromWidget(__instance, out var name, out var pos);

            if (Plugin.DenDiagnostics.Value)
            {
                string posStr = resolved ? pos.ToString() : "n/a";
                Plugin.Logger.LogInfo(
                    $"[DenRespawn] CompassObjectiveMarker.OnSelect (hover): resolved={resolved}, name='{name}', pos={posStr}");
            }

            if (!resolved) return;

            DenMapRevive.HoveredWidgetPtr = (object)__instance is Il2CppObjectBase b ? b.Pointer : IntPtr.Zero;
            DenMapRevive.HoveredName = name;
            DenMapRevive.HoveredPos = pos;
            DenMapRevive.HaveHovered = true;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[DenRespawn] DenPinSelectPatch error: {ex}");
        }
    }
}

// Pin hover-tracking via OnDeselect (v1.1.4). Clears the hover state only if the deselecting
// widget matches the currently-hovered one — a new pin's OnSelect can fire before the old pin's
// OnDeselect, and an unconditional clear would wipe out the new hover in that ordering.
[HarmonyPatch(typeof(CompassObjectiveMarker), "OnDeselect")]
internal static class DenPinDeselectPatch
{
    private static bool _fireVerified;

    static void Postfix(CompassObjectiveMarker __instance)
    {
        try
        {
            if (!_fireVerified)
            {
                _fireVerified = true;
                Plugin.Logger.LogInfo("[DenRespawn] FIRE-VERIFY: CompassObjectiveMarker.OnDeselect patch is live");
            }

            IntPtr ptr = (object)__instance is Il2CppObjectBase b ? b.Pointer : IntPtr.Zero;
            if (ptr == IntPtr.Zero) return;
            if (ptr != DenMapRevive.HoveredWidgetPtr) return;

            DenMapRevive.HaveHovered = false;
            DenMapRevive.HoveredWidgetPtr = IntPtr.Zero;
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[DenRespawn] DenPinDeselectPatch error: {ex}");
        }
    }
}
