using HarmonyLib;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace ZeroTaskWorkersMod
{
    // v0.1.0 was diagnostics-only (logging postfixes). v0.2.0 adds the first behavior-changing
    // patches: prefixes on the four _CanAddVillagerToTaskData implementations that can block
    // task inheritance for a new worker, gated by config + a load/authority/exemption checklist
    // in Diag.ShouldBlock. All prior logging postfixes are unchanged.
    internal static class Diag
    {
        // Set to Time.time in every DeserializeTaskData(ForRecreation) postfix, BEFORE those
        // postfixes' logging-gate early-return, so the grace window always tracks reality even
        // with logging off.
        internal static float LastDeserializeAt = -1f;

        // Should ws (Workstation-family) block a new villager from inheriting a task right now?
        // Every step is guarded so a throw means "don't block" — never break the vanilla path.
        internal static bool ShouldBlock(SSSGame.Workstation ws)
        {
            try
            {
                // a. World present.
                if (!(ZeroTaskTracker.WorldGateSeenAt >= 0f)) return false;

                // b. Grace window: block only once a deserialize has actually been seen this
                // process AND enough time has passed since it — never block on an as-yet-unseen
                // deserialize (v0.2.0 had this backwards: AddToTaskDatas fires BEFORE
                // DeserializeTaskData at world load, so LastDeserializeAt is still -1 at that
                // point, which must mean "don't block", not "block"). Plus a belt-and-braces
                // load-frame check against WorldGateSeenAt.
                if (!(LastDeserializeAt >= 0f && (Time.time - LastDeserializeAt) > Plugin.LoadGraceSeconds.Value)) return false;
                if (!(Time.time - ZeroTaskTracker.WorldGateSeenAt > Plugin.LoadGraceSeconds.Value)) return false;

                // c. Authority.
                try { if (!ws.HasStateAuthority) return false; } catch { return false; }

                // d. Buildstation-family exemption (unassigning auto-transfers here; never block it).
                string nativeClass = NativeClassNameOf(ws);
                if (nativeClass == "Buildstation" || nativeClass == "BoatBuildingStation" || nativeClass == "HarboringStation")
                    return false;

                // e. Name match.
                if (Plugin.ApplyToAllBuildings.Value) return true;

                string structName;
                try { structName = ws._structure?.GetName(); } catch { structName = null; }
                if (string.IsNullOrEmpty(structName) || structName == "?") return false;

                var entries = (Plugin.BuildingNameList.Value ?? "").Split(',');
                foreach (var raw in entries)
                {
                    var entry = raw.Trim();
                    if (entry.Length == 0) continue;
                    if (structName.IndexOf(entry, System.StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
                return false;
            }
            catch { return false; }
        }
        internal static string NativeClassName(Il2CppObjectBase o)
        {
            try
            {
                var cls = IL2CPP.il2cpp_object_get_class(o.Pointer);
                return System.Runtime.InteropServices.Marshal.PtrToStringAnsi(IL2CPP.il2cpp_class_get_name(cls)) ?? "?";
            }
            catch { return "?"; }
        }

        // Workstation/Buildstation/HarboringStation etc. don't derive from Il2CppObjectBase in the
        // compile-time interop reference (same "managed casts lie" gotcha as UnityEngine.Object) —
        // route through object + an `is` check, same pattern as TaskDesc below.
        internal static string NativeClassNameOf(object o)
        {
            try { return o is Il2CppObjectBase b ? NativeClassName(b) : "?"; }
            catch { return "?"; }
        }

        internal static string StructName(SSSGame.Workstation ws)
        {
            try { return ws?._structure?.GetName() ?? "?"; } catch { return "?"; }
        }

        internal static string TaskDesc(SSSGame.AI.WorkstationTaskData td)
        {
            try
            {
                string cls = (object)td is Il2CppObjectBase b ? NativeClassName(b) : "?";
                string item = td?.itemInfoQuantity?.itemInfo?.Name ?? "-";
                return $"{cls}(item='{item}')";
            }
            catch { return "?"; }
        }
    }

    // ---- Group A: AddToTaskDatas (the inheritance moment) ----

    [HarmonyPatch(typeof(SSSGame.Workstation), nameof(SSSGame.Workstation.AddToTaskDatas))]
    public static class Workstation_AddToTaskDatas_Patch
    {
        public static void Postfix(SSSGame.Workstation __instance, SSSGame.Villager villagerToAdd)
        {
            if (!Plugin.LogTaskEvents.Value) return;
            try
            {
                Plugin.Log.LogInfo($"[ZTW] AddToTaskDatas(Workstation) villager='{villagerToAdd?.GetName()}' station={Diag.NativeClassNameOf(__instance)} structure='{Diag.StructName(__instance)}' world={ZeroTaskTracker.WorldTag}");
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(SSSGame.HarboringStation), nameof(SSSGame.HarboringStation.AddToTaskDatas))]
    public static class HarboringStation_AddToTaskDatas_Patch
    {
        public static void Postfix(SSSGame.HarboringStation __instance, SSSGame.Villager villagerToAdd)
        {
            if (!Plugin.LogTaskEvents.Value) return;
            try
            {
                Plugin.Log.LogInfo($"[ZTW] AddToTaskDatas(HarboringStation) villager='{villagerToAdd?.GetName()}' station={Diag.NativeClassNameOf(__instance)} structure='{Diag.StructName(__instance)}' world={ZeroTaskTracker.WorldTag}");
            }
            catch { }
        }
    }

    // ---- Group B: _CanAddVillagerToTaskData (the candidate gate) ----

    [HarmonyPatch(typeof(SSSGame.Workstation), nameof(SSSGame.Workstation._CanAddVillagerToTaskData))]
    public static class Workstation_CanAddVillagerToTaskData_Patch
    {
        public static bool Prefix(SSSGame.Workstation __instance, SSSGame.Villager villagerToAdd, SSSGame.AI.WorkstationTaskData taskData, ref bool __result)
        {
            try
            {
                if (!Diag.ShouldBlock(__instance)) return true;
                __result = false;
                if (Plugin.LogTaskEvents.Value)
                    Plugin.Log.LogInfo($"[ZTW] BLOCKED inheritance villager='{villagerToAdd?.GetName()}' task={Diag.TaskDesc(taskData)} structure='{Diag.StructName(__instance)}'");
                return false;
            }
            catch { return true; }
        }

        public static void Postfix(SSSGame.Workstation __instance, SSSGame.Villager villagerToAdd, SSSGame.AI.WorkstationTaskData taskData, bool __result)
        {
            if (!Plugin.LogTaskEvents.Value) return;
            try
            {
                Plugin.Log.LogInfo($"[ZTW] CanAdd(Workstation) villager='{villagerToAdd?.GetName()}' task={Diag.TaskDesc(taskData)} result={__result} structure='{Diag.StructName(__instance)}' world={ZeroTaskTracker.WorldTag}");
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(SSSGame.Buildstation), nameof(SSSGame.Buildstation._CanAddVillagerToTaskData))]
    public static class Buildstation_CanAddVillagerToTaskData_Patch
    {
        public static bool Prefix(SSSGame.Buildstation __instance, SSSGame.Villager villagerToAdd, SSSGame.AI.WorkstationTaskData taskData, ref bool __result)
        {
            try
            {
                if (!Diag.ShouldBlock(__instance)) return true;
                __result = false;
                if (Plugin.LogTaskEvents.Value)
                    Plugin.Log.LogInfo($"[ZTW] BLOCKED inheritance villager='{villagerToAdd?.GetName()}' task={Diag.TaskDesc(taskData)} structure='{Diag.StructName(__instance)}'");
                return false;
            }
            catch { return true; }
        }

        public static void Postfix(SSSGame.Buildstation __instance, SSSGame.Villager villagerToAdd, SSSGame.AI.WorkstationTaskData taskData, bool __result)
        {
            if (!Plugin.LogTaskEvents.Value) return;
            try
            {
                Plugin.Log.LogInfo($"[ZTW] CanAdd(Buildstation) villager='{villagerToAdd?.GetName()}' task={Diag.TaskDesc(taskData)} result={__result} structure='{Diag.StructName(__instance)}' world={ZeroTaskTracker.WorldTag}");
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(SSSGame.Marketplace), nameof(SSSGame.Marketplace._CanAddVillagerToTaskData))]
    public static class Marketplace_CanAddVillagerToTaskData_Patch
    {
        public static bool Prefix(SSSGame.Marketplace __instance, SSSGame.Villager villagerToAdd, SSSGame.AI.WorkstationTaskData taskData, ref bool __result)
        {
            try
            {
                if (!Diag.ShouldBlock(__instance)) return true;
                __result = false;
                if (Plugin.LogTaskEvents.Value)
                    Plugin.Log.LogInfo($"[ZTW] BLOCKED inheritance villager='{villagerToAdd?.GetName()}' task={Diag.TaskDesc(taskData)} structure='{Diag.StructName(__instance)}'");
                return false;
            }
            catch { return true; }
        }

        public static void Postfix(SSSGame.Marketplace __instance, SSSGame.Villager villagerToAdd, SSSGame.AI.WorkstationTaskData taskData, bool __result)
        {
            if (!Plugin.LogTaskEvents.Value) return;
            try
            {
                Plugin.Log.LogInfo($"[ZTW] CanAdd(Marketplace) villager='{villagerToAdd?.GetName()}' task={Diag.TaskDesc(taskData)} result={__result} structure='{Diag.StructName(__instance)}' world={ZeroTaskTracker.WorldTag}");
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(SSSGame.ResourceStorage), nameof(SSSGame.ResourceStorage._CanAddVillagerToTaskData))]
    public static class ResourceStorage_CanAddVillagerToTaskData_Patch
    {
        public static bool Prefix(SSSGame.ResourceStorage __instance, SSSGame.Villager villagerToAdd, SSSGame.AI.WorkstationTaskData taskData, ref bool __result)
        {
            try
            {
                if (!Diag.ShouldBlock(__instance)) return true;
                __result = false;
                if (Plugin.LogTaskEvents.Value)
                    Plugin.Log.LogInfo($"[ZTW] BLOCKED inheritance villager='{villagerToAdd?.GetName()}' task={Diag.TaskDesc(taskData)} structure='{Diag.StructName(__instance)}'");
                return false;
            }
            catch { return true; }
        }

        public static void Postfix(SSSGame.ResourceStorage __instance, SSSGame.Villager villagerToAdd, SSSGame.AI.WorkstationTaskData taskData, bool __result)
        {
            if (!Plugin.LogTaskEvents.Value) return;
            try
            {
                Plugin.Log.LogInfo($"[ZTW] CanAdd(ResourceStorage) villager='{villagerToAdd?.GetName()}' task={Diag.TaskDesc(taskData)} result={__result} structure='{Diag.StructName(__instance)}' world={ZeroTaskTracker.WorldTag}");
            }
            catch { }
        }
    }

    // ---- Group C: RemoveFromTaskDatas ----

    [HarmonyPatch(typeof(SSSGame.Workstation), nameof(SSSGame.Workstation.RemoveFromTaskDatas))]
    public static class Workstation_RemoveFromTaskDatas_Patch
    {
        public static void Postfix(SSSGame.Workstation __instance, SSSGame.Villager villagerToRemove)
        {
            if (!Plugin.LogTaskEvents.Value) return;
            try
            {
                Plugin.Log.LogInfo($"[ZTW] RemoveFromTaskDatas villager='{villagerToRemove?.GetName()}' structure='{Diag.StructName(__instance)}' world={ZeroTaskTracker.WorldTag}");
            }
            catch { }
        }
    }

    // ---- Group D: save/load + upgrade paths ----

    [HarmonyPatch(typeof(SSSGame.Workstation), nameof(SSSGame.Workstation.DeserializeTaskData))]
    public static class Workstation_DeserializeTaskData_Patch
    {
        public static void Postfix(SSSGame.Workstation __instance, bool __result)
        {
            Diag.LastDeserializeAt = Time.time;
            if (!Plugin.LogTaskEvents.Value) return;
            try
            {
                Plugin.Log.LogInfo($"[ZTW] DeserializeTaskData(Workstation) structure='{Diag.StructName(__instance)}' result={__result} world={ZeroTaskTracker.WorldTag}");
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(SSSGame.Buildstation), nameof(SSSGame.Buildstation.DeserializeTaskData))]
    public static class Buildstation_DeserializeTaskData_Patch
    {
        public static void Postfix(SSSGame.Buildstation __instance, bool __result)
        {
            Diag.LastDeserializeAt = Time.time;
            if (!Plugin.LogTaskEvents.Value) return;
            try
            {
                Plugin.Log.LogInfo($"[ZTW] DeserializeTaskData(Buildstation) structure='{Diag.StructName(__instance)}' result={__result} world={ZeroTaskTracker.WorldTag}");
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(SSSGame.Workstation), nameof(SSSGame.Workstation.DeserializeTaskDataForRecreation))]
    public static class Workstation_DeserializeTaskDataForRecreation_Patch
    {
        public static void Postfix(SSSGame.Workstation __instance, bool __result)
        {
            Diag.LastDeserializeAt = Time.time;
            if (!Plugin.LogTaskEvents.Value) return;
            try
            {
                Plugin.Log.LogInfo($"[ZTW] DeserializeTaskDataForRecreation(Workstation) structure='{Diag.StructName(__instance)}' result={__result} world={ZeroTaskTracker.WorldTag}");
            }
            catch { }
        }
    }

    // ---- Group E: SetTaskAgent (hire-chain context) ----

    [HarmonyPatch(typeof(SSSGame.Workstation), nameof(SSSGame.Workstation.SetTaskAgent))]
    public static class Workstation_SetTaskAgent_Patch
    {
        public static void Postfix(SSSGame.Workstation __instance, bool __result)
        {
            if (!Plugin.LogTaskEvents.Value) return;
            try
            {
                Plugin.Log.LogInfo($"[ZTW] SetTaskAgent(Workstation) result={__result} station={Diag.NativeClassNameOf(__instance)} structure='{Diag.StructName(__instance)}' world={ZeroTaskTracker.WorldTag}");
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(SSSGame.Buildstation), nameof(SSSGame.Buildstation.SetTaskAgent))]
    public static class Buildstation_SetTaskAgent_Patch
    {
        public static void Postfix(SSSGame.Buildstation __instance, bool __result)
        {
            if (!Plugin.LogTaskEvents.Value) return;
            try
            {
                Plugin.Log.LogInfo($"[ZTW] SetTaskAgent(Buildstation) result={__result} station={Diag.NativeClassNameOf(__instance)} structure='{Diag.StructName(__instance)}' world={ZeroTaskTracker.WorldTag}");
            }
            catch { }
        }
    }
}
