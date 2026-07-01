# Terrain Leveler Mod - Drag Crash Handoff (2026-07-01)

Claude, the user is handing this back to you. The goal is to restore the stable drag behavior where dragging the terraforming grid too far turns it RED (unplaceable) instead of crashing, while keeping the new obstacle-clearing `HeightmapTool` logic intact.

## The Context
We successfully implemented the one-hit obstacle-clearing blast (`HeightmapTool.LEVEL` + `AOESpell`). However, after doing this, the user reported that dragging the grid too far causes the game to violently crash, and they want the old behavior back where it just turned red.

## The "Stable" Working Reference
In commit `72970ed`, when the config `BypassHeightLimits` was TRUE, the mod was perfectly stable. The user could drag the grid until it turned red, and they could "bite into big hills to create steep vertical cliffs." 

I extracted the code from `72970ed` and saved it to `TerrainLevelerMod/Plugin_WorkingReference.txt` so you can analyze it. In that stable version, when `BypassHeightLimits` was true:
1. `_ValidateFootprint` always returned `true` (preventing the `OverlapBoxNonAlloc` buffer overflow crash on dense forests).
2. `CheckLevelDifference` always returned `true` (bypassing native steepness rejections).
3. `maxHeightDifference` was hardcoded to `10000f`.
4. `_OnBuildingDimensionsChanged` clamped the horizontal 2D distance between markers to `20f`, and strictly clamped the vertical stretch to `15f`.
5. `_OnSnap` and `TerraformingGrid.OnDynamicBuildingDimensionsChanged` both aborted if the grid exceeded 512 tiles.

## What Failed
In **v1.3.15**, I tried to restore this exact logic but parameterize it using the user's config:
- I clamped the max distance to `Math.Min(MaxDragRange.Value, 22.5f)`.
- I clamped the vertical stretch to `Math.Min(MaxHeightDifference.Value, 15f)`.
- I kept the `_ValidateFootprint` bypass.
- The user reported: *"it still didnt turn red, and let me run into crashing."*

## The Suspects for the Crash
If the 1.3.15 logic still crashes, the crash MUST be bypassing our clamps, or there is another engine limitation we hit:
1. **The Native `maxRange` Raycast:** If we clamp the marker physically to 22.5m, the native tool still does a raycast to the mouse. If the mouse exceeds 22.5m, does the game natively turn it red? Or does it crash trying to evaluate the mouse position? 
2. **The 512 NetworkArray Limit:** A 22.5m radius yields a grid of about 506 tiles natively (using `GetCellsCount()`). If my math is slightly off, 22.5m might barely exceed 512 tiles on certain slopes, causing a `Fusion NetworkArray` overflow crash. We might need to lower the distance clamp back to `20f` (which is exactly what the stable reference used).
3. **The Y-Clamp / NaN Normal Crash:** If the terrain is perfectly vertical, the engine crashes generating normals. The 15m Y-Clamp prevents this, but maybe the Raycast in `_OnBuildingDimensionsChanged` is picking up bad terrain?

## Next Steps for Claude
1. Analyze `TerrainLevelerMod/Plugin_WorkingReference.txt`.
2. Compare it with the current `TerrainLevelerMod/Plugin.cs`.
3. Figure out exactly why the hardcoded `20f` and `15f` in `Plugin_WorkingReference.txt` caused the grid to turn red safely, whereas the logic in 1.3.15 let it "run green until it crashed".
4. Re-implement the drag/clamp logic so it perfectly mirrors the stable reference while keeping the new `TerraformingFieldInteraction_Use_Postfix` (the bomb/clear pass) from 1.3.15.
