# Post-update patch-target scan (2026-08-31 game update).
# Verifies every Harmony patch target used by the mods still exists in the regenerated interop,
# then hunts rename candidates for the four members the launch log showed as missing.

$ErrorActionPreference = 'Stop'
Add-Type -Path "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\core\Mono.Cecil.dll"
$interopDir = "D:\SteamLibrary\steamapps\common\ASKA\BepInEx\interop"

# Mod|Type|Method  (get_X = property getter)
$targets = @"
TaskUnlockerMod|SandSailorStudio.Inventory.ItemInfoDatabase|Initialize
TerrainLevelerMod|DynamicDimensionsPlacementTool|Begin
TerrainLevelerMod|DynamicDimensionsPlacementTool|End
TerrainLevelerMod|DynamicDimensionsPlacementTool|_ValidateFootprint
TerrainLevelerMod|DynamicDimensionsPlacementTool|_OnBuildRayChanged
TerrainLevelerMod|DynamicDimensionsPlacementTool|_OnSnap
TerrainLevelerMod|TerraformingGrid|CheckLevelDifference
TerrainLevelerMod|TerraformingGrid|OnDynamicBuildingDimensionsChanged
TerrainLevelerMod|SSSGame.Network.NetworkDynamicDimensionBuildingState|ChangeGridSize
TerrainLevelerMod|SSSGame.Network.NetworkDynamicDimensionBuildingState_256|UpdateGridValidityOnNetwork
TerrainLevelerMod|SSSGame.Network.NetworkDynamicDimensionBuildingState_512|UpdateGridValidityOnNetwork
TerrainLevelerMod|SandSailorStudio.Localization.LocalizationManager|SetLanguage
TerrainLevelerMod|SandSailorStudio.Localization.LocalizationManager|Awake
TerrainLevelerMod|SandSailorStudio.Localization.LocalizationManager|Loc
TerrainLevelerMod|SSSGame.UI.BuildItemsTabPage|Init
TerrainLevelerMod|SSSGame.UI.BuildItemsTabPage|Show
TerrainLevelerMod|SandSailorStudio.Inventory.InventoryComponent|GetItemCollection
TerrainLevelerMod|TerraformingFieldInteraction|Use
TerrainLevelerMod|TerraformingGridState|Spawned
TerrainLevelerMod|TerraformingGridState|_OnInteractionMapChanged
TerrainLevelerMod|TerraformingGridState|_RefreshCellsHeights
TerrainLevelerMod|TerraformingGridState|Rpc_DebugCompleteField
TerrainLevelerMod|SSSGame.Combat.SpellsManager|Awake
SeedScoutMod|CavesManager|RegisterCaves
SeedScoutMod|WorldStreamingManager|Awake
SeedScoutMod|BiomesManager|Awake
SeedScoutMod|BiomesManager|OnDisable
ResourceMarkerRadiusMod|GatherInteraction|GatherItemsCharge
ResourceMarkerRadiusMod|ObjectiveMarkerContainer|AddMarker
ResourceMarkerRadiusMod|HarvestMarker|Awake
ResourceMarkerRadiusMod|HarvestMarker|ShowRadiusAbsolute
ResourceMarkerRadiusMod|HarvestMarker|ShowRadiusRoutine
ResourceMarkerRadiusMod|HarvestMarker|OnDestroy
ResourceMarkerRadiusMod|OutpostStructure|Awake
ResourceMarkerRadiusMod|OutpostStructure|_RefreshRanges
ResourceMarkerRadiusMod|OutpostStructure|Activate
FishFilletMod|ItemThumbnailPanel|OnPointerClick
MineRefreshMod|CavesManager|Start
MineRefreshMod|Character|Spawned
MineRefreshMod|Character|Despawned
MineRefreshMod|PlayerCharacter|Spawned
MineRefreshMod|PlayerCharacter|Despawned
BowDamageMod|Creature|TakeDamage
JotunBloodYieldMod|SSSGame.HarvestSpawner|Awake
JotunBloodYieldMod|SSSGame.LootSpawner|GetLootStack
DenRespawnMod|Den|Start
DenRespawnMod|Den|Spawned
DenRespawnMod|Den|IsBlockedByStructures
DenRespawnMod|Den|Revive
DenRespawnMod|PopulationSpawner|_UpdateBlockedByStructures
DenRespawnMod|MapMenu|_OnMarkersLeftClick
DenRespawnMod|MapMenu|OnPointerClick
DenRespawnMod|MapMenu|OnActivate
DenRespawnMod|CompassObjectiveMarker|OnSelect
DenRespawnMod|CompassObjectiveMarker|OnDeselect
HealthRegenMod|PlayerCharacter|Spawned
HealthRegenMod|Villager|Spawned
HealthRegenMod|Villager|Despawned
GroundItemVacuumMod|DynamicItemObject|OnEnable
GroundItemVacuumMod|DynamicItemObject|OnDisable
OuthouseComposterMod|ItemContainer|CanStoreItemType
OuthouseComposterMod|ItemContainer|Check
OuthouseComposterMod|ItemContainer|HasSpace
OuthouseComposterMod|ItemContainer|GetStackSize
OuthouseComposterMod|ItemContainer|GetItemCount
OuthouseComposterMod|ItemContainer|HasItem
OuthouseComposterMod|SurvivalObjectiveQuest.SatisfyObjectiveQuestData|IsWhitelistedByStorage
OuthouseComposterMod|SurvivalObjectiveQuest.SatisfyObjectiveQuestData|IsWhitelistedByAny
OuthouseComposterMod|ResourceStorage|CanCreateStorageTaskForItemInfo
CookingStationFixMod|FSM_CanStartCooking|Decide
CookingStationFixMod|FSM_FetchCookingStockpile|OnStateEnter
CookingStationFixMod|FSM_FetchCookingSupplies|OnStateEnter
CookingStationFixMod|FSM_SetCookingPlacePosition|OnStateEnter
CookingStationFixMod|FSM_ReturnCookingResults|OnStateEnter
CookingStationFixMod|CookingQuest|GetPriority
CookingStationFixMod|CookingStockpileQuest|GetPriority
ZeroTaskWorkersMod|SSSGame.Workstation|AddToTaskDatas
ZeroTaskWorkersMod|SSSGame.Workstation|_CanAddVillagerToTaskData
ZeroTaskWorkersMod|SSSGame.Workstation|RemoveFromTaskDatas
ZeroTaskWorkersMod|SSSGame.Workstation|DeserializeTaskData
ZeroTaskWorkersMod|SSSGame.Workstation|DeserializeTaskDataForRecreation
ZeroTaskWorkersMod|SSSGame.Workstation|SetTaskAgent
ZeroTaskWorkersMod|SSSGame.HarboringStation|AddToTaskDatas
ZeroTaskWorkersMod|SSSGame.Buildstation|_CanAddVillagerToTaskData
ZeroTaskWorkersMod|SSSGame.Buildstation|DeserializeTaskData
ZeroTaskWorkersMod|SSSGame.Buildstation|SetTaskAgent
ZeroTaskWorkersMod|SSSGame.Marketplace|_CanAddVillagerToTaskData
ZeroTaskWorkersMod|SSSGame.ResourceStorage|_CanAddVillagerToTaskData
NoNeedsMod|Villager|Spawned
NoNeedsMod|Villager|Despawned
DynamicVillagerNeedsMod|VillagerSchedulePanel|Set
DynamicVillagerNeedsMod|VillagerSchedulePanel|Refresh
DynamicVillagerNeedsMod|VillagerSchedulePanel|OnEnable
DynamicVillagerNeedsMod|VillagerSchedulePanel|HasUnappliedChanges
DynamicVillagerNeedsMod|Villager|Serialize
DynamicVillagerNeedsMod|VillagerSurvival|Spawned
SupplyChainMod|VillagerSocial|AddComplaint
SupplyChainMod|VillagerSocial|RemoveComplaint
SupplyChainMod|SettlementIssueTrackerWidget|AddStorageFullComplaint
SupplyChainMod|SettlementIssueTrackerWidget|RemoveStorageFullComplaint
SupplyChainMod|SettlementIssueTrackerWidget|AddMarkerComplaint
SupplyChainMod|SettlementIssueTrackerWidget|AddMineExhaustedComplain
CraftFromStorageMod|FSM_FetchBloomerySupplies|OnStateEnter
CraftFromStorageMod|FSM_FetchBloomerySupplies|OnStateExit
CraftFromStorageMod|FSM_FetchKilnSupplies|OnStateEnter
CraftFromStorageMod|FSM_FetchKilnSupplies|OnStateExit
CraftFromStorageMod|BuildMenu|OnActivate
CraftFromStorageMod|BuildMenu|OnDeactivate
CraftFromStorageMod|CrafterFetchQuest|GetPriority
CraftFromStorageMod|CrafterSpecificFetchQuest|GetPriority
CraftFromStorageMod|CraftInteraction|CheckOwnedRequirements
CraftFromStorageMod|CraftInteraction|_CheckOwnedBlueprintManifest
CraftFromStorageMod|CraftInteraction|BeginCraftingSequence
CraftFromStorageMod|CraftInteraction|_OnCraftingSuccess
CraftFromStorageMod|CraftInteraction|ActivateBlueprint
CraftFromStorageMod|AnvilInteraction|BeginCraftingSequence
CraftFromStorageMod|DyeingInteraction|BeginCraftingSequence
CraftFromStorageMod|DyeingInteraction|_OnCraftingSuccess
CraftFromStorageMod|CraftingAgent|_OnCraftingFinished
CraftFromStorageMod|CreateItemsTabPage|Show
CraftFromStorageMod|PlayerMenu|OnActivate
CraftFromStorageMod|PlayerMenu|OnClosed
CraftFromStorageMod|CraftBlueprint|Activate
CraftFromStorageMod|ItemThumbnailPanel|_UpdateAvailablility
CraftFromStorageMod|ItemThumbnailPanel|_UpdateAvailabilityStatus
CraftFromStorageMod|FSM_FetchCraftingSupplies|OnStateEnter
CraftFromStorageMod|FSM_FetchCraftingSupplies|OnStateExit
CraftFromStorageMod|FSM_UseCraftingStation|OnStateEnter
CraftFromStorageMod|FSM_ReturnCraftingSupplies|OnStateEnter
CraftFromStorageMod|CrafterFetchQuest.CrafterFetchQuestData|IsWhitelistedByStorage
CraftFromStorageMod|CraftMenu|OnActivate
CraftFromStorageMod|CraftMenu|OnClosed
VillagerFightBackMod|FleeCombatQuest.FleeCombatQuestData|get_ShouldFight
VillagerFightBackMod|FleeCombatQuest.FleeCombatQuestData|_GetSpooked
VillagerFightBackMod|CombatQuest|GetPriority
VillagerFightBackMod|CombatQuest|GetFSMBehavior
VillagerFightBackMod|FleeCombatQuest|get_TriggerPriority
VillagerFightBackMod|QuestRunner|Update
VillagerFightBackMod|FSM_SetVillagerIsFleeing|OnStateEnter
VillagerFightBackMod|FSM_CheckVillagerWarrior|Decide
VillagerFightBackMod|FSM_RunFromTarget|OnStateEnter
VillagerFightBackMod|FSM_RunFromTarget|OnStateUpdate
VillagerFightBackMod|FSM_MeleeCombat|OnStateEnter
VillagerAmmoMod|RangedManager|Awake
VillagerAmmoMod|ProjectileTargetHelper|Awake
TreeRespawnMod|BiomeItemInstance|Initialize
TreeRespawnMod|HarvestInteraction|SetWorldInstance
TreeRespawnMod|HarvestInteraction|TakeDamage
TreeRespawnMod|HarvestInteraction|CanProvideItem
TreeRespawnMod|GatherInteraction|SetWorldInstance
TreeRespawnMod|GatherInteraction|GatherItemsCharge
TreeRespawnMod|StreamingTerrainVS|Awake
TreeRespawnMod|WorldItemInstance|_OnDataChanged
TreeRespawnMod|GatherAndHarvestQuest.GatherAndHarvestData|ComplainNoResourcesFound
TreeRespawnMod|GatherAndHarvestQuest.GatherAndHarvestData|ComplainNoGatherTask
TorchFuelMod|FireStructure|Initialize
TorchFuelMod|LightOutlet|Initialize
SummonTimerMod|VillagerOutlet|Spawned
SummonTimerMod|VillagerOutlet|OnStorageMenuConfirmationPressed
SummonTimerMod|VillagerOutlet|SpawnVillager
SharedLifecycle|PlayerCharacter|Spawned
SharedLifecycle|PlayerCharacter|Despawned
"@ -split "`n" | Where-Object { $_.Trim() }

# --- index Assembly-CSharp types (nested included), dotted full names ---
$asm = [Mono.Cecil.AssemblyDefinition]::ReadAssembly((Join-Path $interopDir "Assembly-CSharp.dll"))
$index = @{}   # dotted fullname -> TypeDefinition
$byName = @{}  # simple name -> list of dotted fullnames
function Add-TypeToIndex($t) {
    $dotted = $t.FullName.Replace('/', '.')
    $script:index[$dotted] = $t
    if (-not $script:byName.ContainsKey($t.Name)) { $script:byName[$t.Name] = New-Object System.Collections.ArrayList }
    [void]$script:byName[$t.Name].Add($dotted)
    foreach ($n in $t.NestedTypes) { Add-TypeToIndex $n }
}
foreach ($t in $asm.MainModule.Types) { Add-TypeToIndex $t }
Write-Host ("Indexed {0} types from Assembly-CSharp.dll" -f $index.Count)

function Resolve-Target([string]$typeSpec) {
    if ($index.ContainsKey($typeSpec)) { return ,@($typeSpec) }
    $suffix = "." + $typeSpec
    $hits = @($index.Keys | Where-Object { $_.EndsWith($suffix) })
    if ($hits.Count -gt 0) { return ,$hits }
    $simple = $typeSpec.Split('.')[-1]
    if ($byName.ContainsKey($simple)) { return ,@($byName[$simple]) }
    return ,@()
}

$missing = @()
$okCount = 0
foreach ($line in $targets) {
    $parts = $line.Trim().Split('|')
    $mod = $parts[0]; $typeSpec = $parts[1]; $method = $parts[2]
    $candidates = Resolve-Target $typeSpec
    if ($candidates.Count -eq 0) {
        $missing += "MISSING TYPE   $mod : $typeSpec"
        continue
    }
    $found = $false
    foreach ($c in $candidates) {
        $t = $index[$c]
        if (@($t.Methods | Where-Object { $_.Name -eq $method }).Count -gt 0) { $found = $true }
    }
    if ($found) { $okCount++ }
    else { $missing += "MISSING METHOD $mod : $typeSpec :: $method (type found: $($candidates -join ', '))" }
}

Write-Host ("OK: {0} targets resolved" -f $okCount)
Write-Host ""
Write-Host "===== MISSING ====="
if ($missing.Count -eq 0) { Write-Host "(none - every patch target resolved)" }
else { $missing | ForEach-Object { Write-Host $_ } }

# --- rename-candidate hunt for the four members the launch log flagged ---
Write-Host ""
Write-Host "===== RENAME CANDIDATES ====="
$hunts = @(
    @{ Type = "SSSGame.UI.ItemThumbnailPanel";                        Pattern = "vail" },
    @{ Type = "SSSGame.HarvestMarker";                                Pattern = "Radius|Show" },
    @{ Type = "SSSGame.Network.NetworkWorldDataManager";              Pattern = "Fish" },
    @{ Type = "SandSailorStudio.Inventory.AvailabilityProcess";       Pattern = "Life|span|Duration" }
)
foreach ($h in $hunts) {
    Write-Host ""
    Write-Host ("--- {0} methods matching /{1}/ ---" -f $h.Type, $h.Pattern)
    $cands = Resolve-Target $h.Type
    if ($cands.Count -eq 0) { Write-Host "  TYPE MISSING ENTIRELY"; continue }
    foreach ($c in $cands) {
        $t = $index[$c]
        foreach ($m in $t.Methods) {
            if ($m.Name -match $h.Pattern) {
                $params = ($m.Parameters | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ", "
                Write-Host ("  {0} {1}({2})" -f $m.ReturnType.Name, $m.Name, $params)
            }
        }
    }
}
