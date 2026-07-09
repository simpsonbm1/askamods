using System;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using SandSailorStudio.Inventory;
using SandSailorStudio.WorldGen;
using SSSGame;
using UnityEngine;

namespace DenRespawnMod;

// Pin recolor after a den refresh (v1.1.3). After a successful den revive the map pin stays GREY
// instead of turning red — vanilla presumably recolors it from an event path our own revive
// bypasses. MarkerObject.RefreshSpawnersHostileStatus(noNotification) re-evaluates spawner
// hostility and recolors the pin; this class locates the nearest AreaInstance's marker and calls it.
internal static class MarkerRefresher
{
    private const float MaxMatchDistance = 100f;

    internal static void RefreshPinNear(Vector3 denPos)
    {
        AreaInstance? nearestArea = null;
        float nearestDist = float.MaxValue;
        ItemComponent? markerIc = null;

        try
        {
            var biomes = Plugin.Biomes;
            if (biomes == null)
            {
                Plugin.Logger.LogWarning("[DenRespawn] MarkerRefresher: no BiomesManager captured — skipping pin refresh.");
                return;
            }

            var worldGen = biomes._worldGenerator;
            if (worldGen == null)
            {
                Plugin.Logger.LogWarning("[DenRespawn] MarkerRefresher: BiomesManager._worldGenerator is null.");
                return;
            }

            var map = worldGen.GetDataMap();
            if (map == null)
            {
                Plugin.Logger.LogWarning("[DenRespawn] MarkerRefresher: GetDataMap() returned null.");
                return;
            }

            var dict = map._areaInstances;
            if (dict == null) return;

            // Enumerate exactly like SeedScoutMod\Scout.cs DumpAreas (~line 551).
            var en = dict.Values.GetEnumerator();
            while (en.MoveNext())
            {
                AreaInstance area = en.Current;
                if (area == null) continue;

                Vector2Int p;
                try { p = area.position; } catch { continue; }

                float dx = p.x - denPos.x;
                float dz = p.y - denPos.z;
                float dist = Mathf.Sqrt(dx * dx + dz * dz);

                if (dist > MaxMatchDistance) continue;
                if (dist >= nearestDist) continue;

                IAreaInstanceMarkerHandler? handler = null;
                try { handler = area.areaInstanceMarkerHandler; } catch { }
                if (handler == null) continue;

                ItemComponent? ic = null;
                try { ic = handler.GetMarkerObject(); } catch { }
                if (ic == null) continue;

                nearestArea = area;
                nearestDist = dist;
                markerIc = ic;
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[DenRespawn] MarkerRefresher.RefreshPinNear error: {ex}");
            return;
        }

        bool foundArea = nearestArea != null;
        string distStr = foundArea ? nearestDist.ToString("F0") : "n/a";
        string clsName = "none";
        bool refreshed = false;

        if (markerIc != null)
        {
            try
            {
                // Managed casts lie for interop objects materialized under a base declared type
                // (GetMarkerObject() is declared to return ItemComponent) — box to Il2CppObjectBase,
                // read the NATIVE class name, and rewrap only if it's really a MarkerObject.
                if ((object)markerIc is Il2CppObjectBase icBase)
                {
                    IntPtr cls = IL2CPP.il2cpp_object_get_class(icBase.Pointer);
                    clsName = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(cls)) ?? "?";

                    if (clsName == "MarkerObject")
                    {
                        var mo = new MarkerObject(icBase.Pointer);
                        // Some markers (observed on long-defeated Baby Crawler Dens 2026-07-09)
                        // have no _biomePopulation wired up, and RefreshSpawnersHostileStatus
                        // NREs internally on them — skip those instead of tripping the catch.
                        bool havePopulation = false;
                        try { havePopulation = mo._biomePopulation != null; } catch { }
                        if (havePopulation)
                        {
                            // noNotification=false: let the game fire its native "monsters are back"
                            // toast — the vanilla event path that would normally announce a revive is
                            // bypassed on remote revives. Pins vanilla already refreshed (up-close
                            // revives) see no status change here, so no duplicate toast is expected.
                            mo.RefreshSpawnersHostileStatus(false);
                            refreshed = true;
                        }
                        else if (Plugin.DenDiagnostics.Value)
                        {
                            Plugin.Logger.LogInfo("[DenRespawn] MarkerRefresher: marker has no _biomePopulation — skipped recolor.");
                        }
                    }
                    else
                    {
                        Plugin.Logger.LogInfo($"[DenRespawn] MarkerRefresher: marker ItemComponent native class was '{clsName}', not MarkerObject — skipped recolor.");
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[DenRespawn] MarkerRefresher: error rewrapping/recoloring marker: {ex}");
            }
        }

        if (Plugin.DenDiagnostics.Value)
        {
            string posStr = denPos.ToString();
            Plugin.Logger.LogInfo($"[DenRespawn] Pin refresh near {posStr}: area={(foundArea ? "found" : "none")} dist={distStr} marker={clsName} refreshed={refreshed}");
        }
    }
}
