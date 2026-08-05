# TerraformAuditMod (Mod 30) — terraforming flag probe

**Status: DEV TOOL v0.3.0, NOT for Nexus. Both vanilla hoe and bulldozer confirmed to set
heightmap-modified flag, confirmed in-game 2026-08-05.**

**GOAL:** determine which terrain-leveling routes set the game's per-cell
heightmap-modified flag, so that a planned TreeRespawnMod feature can suppress
gatherable respawn on terraformed ground.

## Why it exists

TreeRespawnMod's gather respawn feature respawns gather nodes (berries, reeds, etc.) on any ground
the player didn't manually clear, but it cannot yet distinguish between "player left it alone" and
"player terraformed it flat". The game maintains this distinction internally via a per-cell flag in
`TerraformingMap`, so read-access to that flag — and verification of which write paths set it —
enables the suppression logic.

## How it works

**Hotkey F4** dumps, at the player's position, the terraforming-map state: the sample under the
player, a 3x3 neighbourhood around it, and a whole-chunk count of heightmap-modified samples.

**Registry:** Captures terrain chunks via a Harmony postfix on `StreamingTerrainVS.Awake()`. Each
chunk is a `StreamingTerrainVS` instance keyed by its world position.

**Map resolution (v0.3.0):** Three routes in order to get the `TerraformingMap` from a chunk:
1. The chunk's own `_terraformingMap` property (dead end — reads null; see architecture.md)
2. The host `TerrainChunk`'s `DataHandler`
3. `WorldDataManager.OpenData` + `GetDataHandler`

The mod logs which one works. Per-sample reading at the player is the primary result; chunk identity
is printed before map resolution.

## Config reference (`com.askamods.terraformaudit.cfg`)

| Key | Default | Meaning |
|---|---|---|
| `AuditHotkey` | `F4` | Terraforming-map state dump key. |
| `EnableDiagnostics` | `true` | Log chunk registration and map resolution attempts. |

## Design notes

- **Chunk capture point:** `StreamingTerrainVS.Awake()` is a plain Unity lifecycle method (safe to
  patch, not a Fusion state-sync method). Each streamed chunk instantiates one `StreamingTerrainVS`.
- **Why not `Init()`:** patching `StreamingTerrainVS.Init()` breaks world load entirely — 4,542
  `NullReferenceException` frames named `DMD<StreamingTerrainVS::Init>` in one session (confirmed
  2026-08-05). The fault is inside the patched method, not the postfix body. Use `Awake()` instead.
- **Registry hardening (v0.2.0 onward):** stale pooled wrappers are filtered out; only chunks with
  live `TerrainChunk` components survive the read pass.

## Version history

- **v0.3.0 (2026-08-05)** — three-route `TerraformingMap` resolution via chunk property,
  terrain chunk DataHandler, and WorldDataManager path; per-sample reading at the player
  promoted to primary result; chunk identity logged before map resolution. **Confirmed
  in-game:** both TerrainLevelerMod's bulldozer AND the vanilla terraforming hoe set the
  heightmap-modified flag on all affected terrain samples (verified 2026-08-05). Probe
  has answered its question completely.
- **v0.2.0 (2026-08-05)** — `Init` patch removed after it broke world load; registry
  hardened against stale pooled wrappers.
- **v0.1.0 (2026-08-05)** — first build, patched both `StreamingTerrainVS.Awake` and
  `.Init`.

## Archive

The probe has answered its design question (which terrain-leveling routes set the
heightmap-modified flag) completely. It is retained for re-checking terrain questions as
needed, not for ongoing use.
