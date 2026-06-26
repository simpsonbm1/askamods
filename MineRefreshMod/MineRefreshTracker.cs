using System;
using System.Collections.Generic;
using SSSGame;
using SSSGame.Network;
using SandSailorStudio.Procedural;
using UnityEngine;

namespace MineRefreshMod;

public class MineRefreshTracker : MonoBehaviour
{
    private KeyCode _triggerKey = KeyCode.U;
    private string _guiMessage = "";
    private float _guiMessageExpiry = 0f;

    private void Start()
    {
        if (Enum.TryParse<KeyCode>(Plugin.TriggerHotkey.Value, true, out var parsedKey))
        {
            _triggerKey = parsedKey;
            Plugin.Logger.LogInfo($"[MineRefreshMod] Parsed hotkey: {_triggerKey}");
        }
        else
        {
            Plugin.Logger.LogWarning($"[MineRefreshMod] Failed to parse hotkey '{Plugin.TriggerHotkey.Value}'. Defaulting to 'U'.");
            _triggerKey = KeyCode.U;
        }
    }

    private void Update()
    {
        if (Input.GetKeyDown(_triggerKey))
        {
            try
            {
                TriggerRefresh();
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[MineRefreshMod] Error during mine refresh: {ex}");
                ShowMessage("Error occurred during mine refresh! Check BepInEx log.");
            }
        }
    }

    private void TriggerRefresh()
    {
        Plugin.Logger.LogInfo("[MineRefreshMod] [DEBUG] TriggerRefresh started.");

        if (Plugin.LocalPlayer == null)
        {
            Plugin.Logger.LogWarning("[MineRefreshMod] Cannot trigger refresh: Local player is not spawned.");
            return;
        }

        var playerPos = Plugin.LocalPlayer.transform.position;
        var cavesManager = Plugin.GameCavesManager;
        
        if (cavesManager == null)
        {
            Plugin.Logger.LogWarning("[MineRefreshMod] CavesManager not captured yet.");
            ShowMessage("Cannot refresh: CavesManager not found!");
            return;
        }

        Plugin.Logger.LogInfo("[MineRefreshMod] [DEBUG] Retrieving all caves.");
        var caves = cavesManager.GetAllCaves();
        if (caves == null)
        {
            Plugin.Logger.LogInfo("[MineRefreshMod] No caves list returned.");
            ShowMessage("No mines or caves found in this world!");
            return;
        }

        Plugin.Logger.LogInfo("[MineRefreshMod] [DEBUG] Casting caves list to collection.");
        var collection = caves.TryCast<Il2CppSystem.Collections.Generic.IReadOnlyCollection<CaveEntrance>>();
        if (collection == null)
        {
            ShowMessage("No mine entrance detected!");
            return;
        }

        Plugin.Logger.LogInfo($"[MineRefreshMod] [DEBUG] Finding closest entrance out of {collection.Count} caves.");
        CaveEntrance? closestEntrance = null;
        float minDistance = float.MaxValue;
        int caveCount = collection.Count;

        for (int i = 0; i < caveCount; i++)
        {
            var cave = caves[i];
            if (cave == null) continue;

            float dist = Vector3.Distance(playerPos, cave.transform.position);
            if (dist < minDistance)
            {
                minDistance = dist;
                closestEntrance = cave;
            }
        }

        if (closestEntrance == null)
        {
            ShowMessage("No mine entrance detected!");
            return;
        }

        Plugin.Logger.LogInfo($"[MineRefreshMod] [DEBUG] Closest entrance: {closestEntrance.caveId}, distance: {minDistance}m.");

        if (Plugin.TriggerOnlyNearEntrance.Value && minDistance > Plugin.MaxEntranceDistance.Value)
        {
            Plugin.Logger.LogInfo($"[MineRefreshMod] Too far from closest mine. Distance={minDistance:F1}m, Max={Plugin.MaxEntranceDistance.Value}m");
            ShowMessage($"Too far from mine entrance! Stand closer ({minDistance:F1}m away).");
            return;
        }

        // Find all connected sub-hallways (nodes) using a multi-method search
        var caveNodesSet = new HashSet<CaveNode>();

        // Method 1: Hierarchy search under the entrance (most common Unity structure)
        Plugin.Logger.LogInfo("[MineRefreshMod] [DEBUG] Method 1: Searching for CaveNodes under entrance transform.");
        var hierarchyNodes = new List<CaveNode>();
        try
        {
            GetComponentsInChildrenRecursive(closestEntrance.transform, hierarchyNodes);
            Plugin.Logger.LogInfo($"[MineRefreshMod] [DEBUG] Entrance hierarchy search found {hierarchyNodes.Count} CaveNodes.");
            foreach (var n in hierarchyNodes)
            {
                if (n != null) caveNodesSet.Add(n);
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[MineRefreshMod] [DEBUG] Error in entrance hierarchy search: {ex}");
        }

        // Method 2: Parent hierarchy search (in case hallways are siblings sharing a common root)
        if (caveNodesSet.Count <= 1 && closestEntrance.transform.parent != null)
        {
            Plugin.Logger.LogInfo("[MineRefreshMod] [DEBUG] Method 2: Searching for CaveNodes under parent transform (siblings).");
            var parentNodes = new List<CaveNode>();
            try
            {
                GetComponentsInChildrenRecursive(closestEntrance.transform.parent, parentNodes);
                Plugin.Logger.LogInfo($"[MineRefreshMod] [DEBUG] Parent hierarchy search found {parentNodes.Count} CaveNodes.");
                foreach (var n in parentNodes)
                {
                    if (n != null) caveNodesSet.Add(n);
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[MineRefreshMod] [DEBUG] Error in parent hierarchy search: {ex}");
            }
        }

        // Method 3: Graph search (fallback)
        Plugin.Logger.LogInfo("[MineRefreshMod] [DEBUG] Method 3: Traversing connections graph.");
        var visited = new HashSet<int>();
        var graphNodes = new List<CaveNode>();
        try
        {
            GetCaveNodesRecursive(closestEntrance, visited, graphNodes);
            Plugin.Logger.LogInfo($"[MineRefreshMod] [DEBUG] Graph search found {graphNodes.Count} CaveNodes.");
            foreach (var n in graphNodes)
            {
                if (n != null) caveNodesSet.Add(n);
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[MineRefreshMod] [DEBUG] Error in graph search: {ex}");
        }

        var caveNodes = new List<CaveNode>(caveNodesSet);
        Plugin.Logger.LogInfo($"[MineRefreshMod] [DEBUG] Total unique CaveNodes identified: {caveNodes.Count}.");

        Plugin.Logger.LogInfo("[MineRefreshMod] [DEBUG] Running proximity safety check.");
        bool someoneInside = false;
        Character? trappedCharacter = null;
        float safetyRadiusSqr = Plugin.SafetyRadius.Value * Plugin.SafetyRadius.Value;

        lock (Plugin.ActiveCharacters)
        {
            for (int i = 0; i < Plugin.ActiveCharacters.Count; i++)
            {
                var c = Plugin.ActiveCharacters[i];
                if (c == null || c == Plugin.LocalPlayer || c.gameObject == null) continue;

                bool isAlive = false;
                try { isAlive = c.IsAlive(); } catch { }
                if (!isAlive) continue;

                foreach (var node in caveNodes)
                {
                    if (node == null) continue;
                    float distSqr = (c.transform.position - node.transform.position).sqrMagnitude;
                    if (distSqr < safetyRadiusSqr)
                    {
                        someoneInside = true;
                        trappedCharacter = c;
                        break;
                    }
                }

                if (someoneInside) break;
            }
        }

        if (someoneInside && trappedCharacter != null)
        {
            Plugin.Logger.LogWarning($"[MineRefreshMod] Refresh blocked. Worker/Player '{trappedCharacter.GetName()}' is inside the mine.");
            ShowMessage($"Cannot refresh: {trappedCharacter.GetName()} is inside the mine!");
            return;
        }

        Plugin.Logger.LogInfo("[MineRefreshMod] [DEBUG] Proximity check passed. Checking host/server authority.");
        if (Plugin.LocalPlayer.NetworkObject == null || Plugin.LocalPlayer.NetworkObject.Runner == null || !Plugin.LocalPlayer.NetworkObject.Runner.IsServer)
        {
            Plugin.Logger.LogWarning("[MineRefreshMod] Refresh blocked: Client requested, but only the host can execute the refresh.");
            ShowMessage("Only the host/server can refresh the mine!");
            return;
        }

        Plugin.Logger.LogInfo("[MineRefreshMod] [DEBUG] Host authority confirmed. Executing refresh.");
        int wallsRefreshed = 0;
        int nodesUncollapsed = 0;
        int itemsSpawned = 0;

        for (int nodeIdx = 0; nodeIdx < caveNodes.Count; nodeIdx++)
        {
            var node = caveNodes[nodeIdx];
            if (node == null) continue;

            Plugin.Logger.LogInfo($"[MineRefreshMod] [DEBUG] Processing node index {node.index} (index {nodeIdx + 1}/{caveNodes.Count}).");

            var caveData = node.PersistentData;
            DigData? digData = null;

            if (caveData != null)
            {
                Plugin.Logger.LogInfo($"[MineRefreshMod] [DEBUG] Node {node.index}: Fetching DigData.");
                try
                {
                    digData = caveData.GetDigData(node.index, DataAccessMode.FETCH);
                    if (digData != null)
                    {
                        Plugin.Logger.LogInfo($"[MineRefreshMod] [DEBUG] Node {node.index}: Resetting DigData.");
                        digData.ResetCrackData();
                        digData.wallIndexLeft = 0;
                        digData.wallIndexRight = 0;
                        Plugin.Logger.LogInfo($"[MineRefreshMod] [DEBUG] Node {node.index}: Setting DigData dirty.");
                        digData.SetDirty();
                    }
                    else
                    {
                        Plugin.Logger.LogInfo($"[MineRefreshMod] [DEBUG] Node {node.index}: DigData is null.");
                    }
                }
                catch (Exception ex)
                {
                    Plugin.Logger.LogError($"[MineRefreshMod] [DEBUG] Node {node.index}: Exception handling DigData: {ex}");
                }
            }
            else
            {
                Plugin.Logger.LogInfo($"[MineRefreshMod] [DEBUG] Node {node.index}: PersistentData is null.");
            }

            Plugin.Logger.LogInfo($"[MineRefreshMod] [DEBUG] Node {node.index}: Checking collapse state (IsCollapsed={node.IsCollapsed}, open={node.open}).");
            try
            {
                if (node.IsCollapsed || !node.open)
                {
                    Plugin.Logger.LogInfo($"[MineRefreshMod] [DEBUG] Node {node.index}: Reopening node.");
                    node.open = true;
                    
                    Plugin.Logger.LogInfo($"[MineRefreshMod] [DEBUG] Node {node.index}: Setting _isCollapsed to false.");
                    node._isCollapsed = false;
                    
                    Plugin.Logger.LogInfo($"[MineRefreshMod] [DEBUG] Node {node.index}: Calling UpdateCollapsedState().");
                    node.UpdateCollapsedState();
                    nodesUncollapsed++;
                }
            }
            catch (Exception ex)
            {
                Plugin.Logger.LogError($"[MineRefreshMod] [DEBUG] Node {node.index}: Exception handling collapse state: {ex}");
            }

            if (Plugin.RespawnItems.Value && node.caveItemSpawners != null)
            {
                Plugin.Logger.LogInfo($"[MineRefreshMod] [DEBUG] Node {node.index}: Running {node.caveItemSpawners.Count} item spawners.");
                for (int i = 0; i < node.caveItemSpawners.Count; i++)
                {
                    var spawner = node.caveItemSpawners[i];
                    if (spawner != null)
                    {
                        try
                        {
                            Plugin.Logger.LogInfo($"[MineRefreshMod] [DEBUG] Node {node.index}: Spawner {i + 1}: Running.");
                            spawner.Run();
                            itemsSpawned++;
                        }
                        catch (Exception ex)
                        {
                            Plugin.Logger.LogError($"[MineRefreshMod] [DEBUG] Node {node.index}: Exception running spawner {i + 1}: {ex}");
                        }
                    }
                }
            }
        }

        // Global Wall Refresh using Network Identity and Hierarchy Search
        Plugin.Logger.LogInfo("[MineRefreshMod] [DEBUG] Searching for all CaveWallInteraction components.");
        var wallSet = new HashSet<CaveWallInteraction>();

        // 1. Search under CavesManager.cavesRoot (global root for all caves)
        try
        {
            if (cavesManager.cavesRoot != null)
            {
                var rootWalls = new List<CaveWallInteraction>();
                GetComponentsInChildrenRecursive(cavesManager.cavesRoot, rootWalls);
                Plugin.Logger.LogInfo($"[MineRefreshMod] [DEBUG] Found {rootWalls.Count} CaveWallInteraction components under cavesRoot.");
                foreach (var w in rootWalls)
                {
                    if (w != null) wallSet.Add(w);
                }
            }
            else
            {
                Plugin.Logger.LogWarning("[MineRefreshMod] [DEBUG] CavesManager.cavesRoot is null.");
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[MineRefreshMod] [DEBUG] Error searching under cavesRoot: {ex}");
        }

        // 2. Search under closest entrance's parent (fallback/sibling structure)
        try
        {
            if (closestEntrance.transform.parent != null)
            {
                var parentWalls = new List<CaveWallInteraction>();
                GetComponentsInChildrenRecursive(closestEntrance.transform.parent, parentWalls);
                Plugin.Logger.LogInfo($"[MineRefreshMod] [DEBUG] Found {parentWalls.Count} CaveWallInteraction components under entrance parent.");
                foreach (var w in parentWalls)
                {
                    if (w != null) wallSet.Add(w);
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Logger.LogError($"[MineRefreshMod] [DEBUG] Error searching under entrance parent: {ex}");
        }

        var allWalls = new List<CaveWallInteraction>(wallSet);
        Plugin.Logger.LogInfo($"[MineRefreshMod] [DEBUG] Total unique CaveWallInteraction components discovered: {allWalls.Count}.");

        if (allWalls.Count > 0)
        {
            int matchedWallsCount = 0;
            Plugin.Logger.LogInfo($"[MineRefreshMod] [DEBUG] Filtering and refreshing walls for Cave ID {closestEntrance.caveId}.");
            for (int i = 0; i < allWalls.Count; i++)
            {
                var wall = allWalls[i];
                if (wall == null) continue;

                // Check network identity to match cave ID
                int wallCaveId = -1;
                int wallNodeId = -1;
                if (wall._interactable != null)
                {
                    wallCaveId = wall._interactable._identity.caveId;
                    wallNodeId = wall._interactable._identity.nodeId;
                }

                // If cave ID matches the closest entrance's cave ID, refresh the wall
                if (wallCaveId == closestEntrance.caveId)
                {
                    matchedWallsCount++;
                    // Find the logical CaveNode corresponding to this wall's nodeId
                    CaveNode? associatedNode = caveNodes.Find(n => n.index == wallNodeId);

                    // Fallback to spatial lookup if node ID wasn't found in our list
                    if (associatedNode == null)
                    {
                        float minNodeDist = float.MaxValue;
                        foreach (var node in caveNodes)
                        {
                            if (node == null) continue;
                            float dist = Vector3.Distance(wall.transform.position, node.transform.position);
                            if (dist < minNodeDist)
                            {
                                minNodeDist = dist;
                                associatedNode = node;
                            }
                        }
                    }

                    if (associatedNode != null)
                    {
                        try
                        {
                            var caveData = associatedNode.PersistentData;
                            DigData? digData = null;
                            if (caveData != null)
                            {
                                digData = caveData.GetDigData(associatedNode.index, DataAccessMode.FETCH);
                            }

                            // Fully restore the wall's physical and visual state
                            wall.collapseWall = false;
                            if (wall.gameObject != null)
                            {
                                wall.gameObject.SetActive(true);
                            }

                            if (digData != null)
                            {
                                wall.RefreshDigData(digData);
                            }

                            wall.RefreshRendererData();
                            wall.SetRenderersVisible(true);
                            wallsRefreshed++;
                        }
                        catch (Exception ex)
                        {
                            Plugin.Logger.LogError($"[MineRefreshMod] [DEBUG] Wall {i + 1} (Cave {wallCaveId}, Node {wallNodeId}): Exception in refreshing: {ex}");
                        }
                    }
                    else
                    {
                        Plugin.Logger.LogWarning($"[MineRefreshMod] [DEBUG] Wall {i + 1} (Cave {wallCaveId}, Node {wallNodeId}): Could not associate with any CaveNode!");
                    }
                }
            }
            Plugin.Logger.LogInfo($"[MineRefreshMod] [DEBUG] Matched and processed {matchedWallsCount} walls for cave ID {closestEntrance.caveId} out of {allWalls.Count} total walls.");
        }

        Plugin.Logger.LogInfo($"[MineRefreshMod] Mine successfully refreshed! Hallways={caveNodes.Count}, WallsRestored={wallsRefreshed}, ClearedCollapses={nodesUncollapsed}, SpawnersRun={itemsSpawned}");
        ShowMessage("Mine successfully refreshed! Resources regenerated.");
    }

    private void GetCaveNodesRecursive(LSystemNode node, HashSet<int> visited, List<CaveNode> results)
    {
        if (node == null) return;
        if (visited.Contains(node.index)) return;
        visited.Add(node.index);

        var caveNode = node as CaveNode; // Use standard C# casting
        if (caveNode != null)
        {
            results.Add(caveNode);
        }

        if (node.connections != null)
        {
            for (int i = 0; i < node.connections.Count; i++)
            {
                var conn = node.connections[i];
                if (conn != null)
                {
                    GetCaveNodesRecursive(conn, visited, results);
                }
            }
        }
    }

    private void GetComponentsInChildrenRecursive<T>(Transform parent, List<T> results) where T : Component
    {
        if (parent == null) return;

        var comp = parent.GetComponent<T>();
        if (comp != null)
        {
            results.Add(comp);
        }

        int childCount = parent.childCount;
        for (int i = 0; i < childCount; i++)
        {
            var child = parent.GetChild(i);
            if (child != null)
            {
                GetComponentsInChildrenRecursive(child, results);
            }
        }
    }

    private void ShowMessage(string message)
    {
        _guiMessage = message;
        _guiMessageExpiry = Time.time + 5.0f;
    }

    private void OnGUI()
    {
        if (Time.time < _guiMessageExpiry && !string.IsNullOrEmpty(_guiMessage))
        {
            // Drop shadow style
            var shadowStyle = new GUIStyle(GUI.skin.label);
            shadowStyle.fontSize = 24;
            shadowStyle.fontStyle = FontStyle.Bold;
            shadowStyle.alignment = TextAnchor.MiddleCenter;
            shadowStyle.normal.textColor = Color.black;

            // Main text style
            var textStyle = new GUIStyle(shadowStyle);
            textStyle.normal.textColor = Color.yellow;

            // Render drop shadow
            GUI.Label(new Rect(0, 81, Screen.width, 100), _guiMessage, shadowStyle);
            
            // Render main text
            GUI.Label(new Rect(0, 80, Screen.width, 100), _guiMessage, textStyle);
        }
    }
}
