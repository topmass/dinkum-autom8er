# Autom8er Project Specsheet

## Overview
**Version:** main (post-1.6.1)
**Plugin ID:** `topmass.autom8er`
**DLL Name:** `topmass.autom8er.dll`
**Target Framework:** net472
**Game:** Dinkum (Steam)
**Dependencies:** BepInEx 6.0.0-pre.1

## Features Summary
1. **Auto Input/Output** - Chests next to machines automatically feed inputs and receive outputs
2. **White Crates = INPUT ONLY** - White crates feed machines but won't receive outputs
3. **Conveyor Belt System** - Black Marble Path (configurable) routes items between distant chests and machines
4. **Conveyor Visual Animations** - Items visually slide along conveyor paths; transfer happens on arrival
5. **Silo Auto-Fill** - Silos fill from chests with Animal Food (5 staggered bag animations per tick)
6. **Crab Pot Automation** - Auto-bait loading + harvest output via chests/conveyors (2-tile radius for water)
7. **Harvest Machines** - Bee houses, key cutters, worm farms auto-harvest on day change
8. **Growth Stage Loading** - Incubators and other growth-stage objects accept items from chests/conveyors
9. **Farm Animal Protection** - Incubators and animal spawners are NEVER auto-harvested
10. **Fish Pond Automation** - Auto-feed critters from adjacent chests, extract roe on day change (3-tile radius)
11. **Bug Terrarium Automation** - Auto-feed honey from adjacent chests, extract cocoons on day change (3-tile radius)
12. **Auto Sorter Integration** - Auto Sorters work as automation I/O chests, exempt from KeepOneItem, fire items on deposit
13. **Auto Placer Support** - Auto Placers function as standard automation chests
14. **Stackable Critters** - All underwater creatures become stackable (configurable, default on)
15. **Quarry Auto-Harvest** - Vanilla quarry day-change nodes can be auto-harvested into Autom8er chest/conveyor networks
16. **Green Crates = VacuFarm Crates** - Green crates harvest/vacuum nearby farm outputs, can till/fertilize/plant, and pass outputs into the connected network
17. **Black Crates = Filter Crates** - Black crates hold sample items and route matching network items into a touching storage chest or green VacuFarm crate

### Green Crate Tree Mode
- If a green crate contains a shovel and buried tree/fruit items, tree planting uses a dedicated layout pass that is separate from crop tilling/fertilizing/seed planting.
- Crop tiles still use the original nearest-valid-tile behavior around the crate. Only shovel-based tree planting uses the fixed layout scan.
- Tree planting scans the active 21x21 area in a fixed map-direction order: northwest quadrant first, then northeast, southwest, and southeast.
- Within each tree quadrant, placement starts from that quadrant's outer corner and skips blocked or invalid tiles instead of failing the whole run.
- Decorative blockers are supported. If a tile is occupied by something that cannot be safely replaced, the crate leaves it alone and keeps scanning for the next valid tile.

---

## Key IDs

### TileObject IDs (for `WorldManager.Instance.onTileMap`)
| ID | Item | Purpose |
|----|------|---------|
| 302 | Silo | 2x2 multi-tile, holds 200 Animal Food |
| 417 | White Wooden Crate | INPUT ONLY container |
| 430 | White Wooden Chest | Standard chest |
| 410 | Black Wooden Crate | Filter crate root for routed storage |
| 421 | Black Wooden Chest | Standard chest |
| 412 | Green Wooden Crate | VacuFarm crate |
| 985 | Egg Incubator | Growth stage object, accepts fertilized eggs |
| 190 | Quarry | Vanilla quarry root tile used by quarry auto-harvest |

### Item IDs (for `Inventory.Instance.allItems`)
| ID | Item | Purpose |
|----|------|---------|
| 344 | Animal Food | Silo fill item (hardcoded, ItemSign not on prefab) |
| 383 | Quarry | Inventory item that places the vanilla quarry |
| 1747 | Black Marble Path | Default conveyor tile |
| 964 | Cobblestone Path | Alt conveyor option |
| 346 | Rock Path | Alt conveyor option |
| 775 | Iron Path | Alt conveyor option |
| 15 | Brick Path | Alt conveyor option |
| 2019 | Fertilised Chicken Egg | Incubator input |

### How Tile Types Work
- **`onTileMap[x,y]`** - Stores TileObject ID (chests, machines, objects). Multi-tile objects store negative values (< -1) in non-root tiles
- **`tileTypeMap[x,y]`** - Stores tile type (ground, paths, floors)
- **`onTileStatusMap[x,y]`** - Stores machine status (-2/-1 = empty, >=0 = processing item ID), growth stage, or silo fill level (0-200)
- Path items have `placeableTileType` property that maps to `tileTypeMap` values

### Multi-Tile Objects
Silos, fish ponds, bug terrariums, and other large objects span multiple tiles. Non-root tiles store negative values in `onTileMap`:
```csharp
// Check if tile is part of a multi-tile object
if (onTileMap[x, y] < -1)
{
    // Find the root tile position
    Vector2 rootPos = WorldManager.Instance.findMultiTileObjectPos(x, y);
    int actualTileId = WorldManager.Instance.onTileMap[(int)rootPos.x, (int)rootPos.y];
}
```

---

## Code Structure

### Plugin.cs Layout (~3400 lines)
```
Autom8er namespace
├── Plugin : BaseUnityPlugin
│   ├── Config: ConveyorTileItemId, KeepOneItem, ScanInterval, SiloFillSpeed,
│   │          AutoFeedPonds, HoldOutputForBreeding, AnimationEnabled, AnimationSpeed,
│   │          StackableCritters
│   ├── Constants: WHITE_CRATE_TILE_ID (417), WHITE_CHEST_TILE_ID (430),
│   │          BLACK_CRATE_TILE_ID (410), GREEN_CRATE_TILE_ID (412)
│   ├── Awake() - Init config, apply Harmony patches
│   ├── Update() - ConveyorAnimator.UpdateAnimations() + scan timer + ProcessAllChests
│   ├── OnDestroy() - ConveyorAnimator.ClearAllAnimations() + harmony.UnpatchSelf()
│   ├── CacheConveyorTileType() - Get placeableTileType from item data
│   ├── ApplyStackableCritters() - One-time: sets isStackable=true on all underwaterCreature items
│   └── ProcessAllChests() - Main loop (ORDER MATTERS):
│       1. FilterCrateHelper.TryPullFilteredItemsFromNetwork for black crates
│       2. VacuumCrateHelper.TryVacuumNearbyDrops / TryFlushStoredItemsToNetwork / TryProcessFarmTile for green crates
│       3. FishPondHelper.TryFeedPondsAndTerrariums ← MUST BE FIRST among chest-fed systems (see Rule 7)
│       4. TryFeedAdjacentMachine (furnaces, etc)
│       5. TryFeedMachineViaConveyorPath (furnaces via conveyor)
│       6. CrabPotHelper.TryLoadBaitIntoCrabPots (2-tile radius)
│       7. GrowthStageHelper.TryLoadItemsIntoGrowthStages (incubators, etc)
│       8. SiloHelper.TryLoadFeedIntoSilos (staggered bag animations)
│
├── QuarryServerDropCapturePatch / QuarryDirectedServerDropCapturePatch [HarmonyPatch]
│   └── Prefix on both NetworkMapSharer.spawnAServerDrop overloads
│       - Active only during an automated quarry node break
│       - Captures the node's real vanilla item drops synchronously before they become ground items
│
├── QuarryObjectDropSuppressPatch [HarmonyPatch]
│   └── Prefix on NetworkMapSharer.RpcSpawnATileObjectDrop()
│       - Suppresses the extra quarry object-drop visual while an automated quarry node is being harvested
│
├── EjectItemOnCyclePatch [HarmonyPatch]
│   └── Prefix on ItemDepositAndChanger.ejectItemOnCycle()
│       - Intercepts machine output before item drops
│       - Priority 1: Direct adjacent chest (TryDepositToAdjacentChest)
│       - Priority 2: Via conveyor path (TryDepositViaConveyorPath)
│       - Both trigger QueueAutoSorterUpdate if depositing into Auto Sorter
│       - Both animated when via conveyor path
│
├── NewDayHarvestPatch [HarmonyPatch]
│   └── Postfix on WorldManager.refreshAllChunksNewDay()
│       - 2s delay via coroutine (wait for chunk refresh)
│       - Runs phased day-change kickoff with existing phase gaps:
│         harvest machines → 0.5s → Auto Farmers → 0.5s → ponds/terrariums → 0.5s → quarries
│       - Phase gaps only delay the start of each system; they do not wait for full completion
│       - Single-flight guard prevents load catch-up and day-change runs from overlapping
│       - Calls HarvestHelper.ProcessHarvestableMachines() as a coroutine kickoff
│       - Calls VacuumCrateHelper.ProcessDayChangeVacuumHarvests() as a coroutine kickoff
│       - Calls FishPondHelper.ProcessPondAndTerrariumOutput()
│       - Calls QuarryHelper.ProcessDayChangeQuarries()
│
├── SaveGameAnimationClearPatch [HarmonyPatch]
│   └── Prefix on SaveLoad.SaveGame()
│       - Calls ConveyorAnimator.ClearAllAnimations()
│       - Executes all pending deposit callbacks (no item loss)
│
├── LoadGameStateClearPatch [HarmonyPatch]
│   └── Prefix on SaveLoad.loadOverFrames()
│       - Clears pendingSorts (stale coroutine refs from previous session)
│       - Clears all animations
│       - Clears filter distribution cursors
│       - Clears quarry runtime state
│
├── QuarryHelper (static class)
│   ├── ProcessDayChangeQuarries() - Entry point after vanilla new-day quarry nodes spawn
│   ├── ScanChestForQuarries() - Finds nearby/connected quarries from chest + conveyor networks
│   ├── TryHarvestQuarryOutputsAt() - Collects spawned adjacent quarry nodes for a reachable quarry
│   ├── Quarry break stagger helpers - Batch 5 quarries per chest, add 0.2s per batch before breaking
│   ├── TryCaptureServerDrop() - Captures real vanilla quarry item drops during onDeathServer()
│   ├── ShouldSuppressQuarryObjectDrop() - Blocks extra object-drop visuals during automated quarry harvest
│   ├── HarvestQuarryNodeToStorage() - Plays death particles, runs vanilla onDeathServer(), clears tile, queues conveyor deposit
│   └── CollectKnownQuarryNodeIds() - Builds the set of vanilla quarry node/barrel/bin tile IDs
│
├── HarvestHelper (static class)
│   ├── ProcessHarvestableMachines() - Entry point on day change
│   ├── ScanAndHarvestAllChests() - Scan all chests + conveyor networks
│   ├── ScanConveyorPathForHarvestables() - BFS to find harvestable machines
│   ├── ScanConnectedHarvestArray() - Flood-fill same-machine arrays from a chest-discovered seed
│   ├── TryGetHarvestArrayRoot() - Resolve multi-tile roots before array traversal
│   ├── TryAutoHarvestAt() - Harvest a single machine
│   │   └── GUARD: if (growth.spawnsFarmAnimal) return; ← CRITICAL
│   ├── TryDepositHarvestToChest() - Deposit items into chest
│   │   └── Triggers QueueAutoSorterUpdate if target is Auto Sorter
│   ├── FindAdjacentChestForHarvest() - Find output chest nearby
│   ├── FindChestViaConveyorPath() - Find chest via conveyor BFS
│   └── Day-change harvest batching:
│       - Classic machine arrays process in 100-machine waves with a 0.2s coroutine pause between waves
│       - Conveyor animation launch stagger is also 100 outputs per chest with a 0.2s delay per batch
│       - No hard connected-network scan cap and no hard total-output cap
│
├── ConveyorHelper (static class)
│   ├── TryFeedAdjacentMachine() - Direct chest → machine
│   │   └── Checks ConveyorAnimator.IsTargetReserved() to avoid double-feeding
│   ├── TryFeedMachineViaConveyorPath() - Chest → path → machine (animated)
│   │   └── ReserveTarget, animate, deposit+UnreserveTarget on arrival
│   ├── OutputDestination - Routed destination info: storage chest + conveyor arrival tile
│   ├── TryDepositToAdjacentChest() - Machine → direct routed destination (instant)
│   ├── TryDepositViaConveyorPath() - Machine → path → routed destination (animated)
│   │   └── Arrival callback: deposit or FallbackDepositToAnyChest if full
│   ├── FallbackDepositToAnyChest() - BFS fallback when intended dest unavailable
│   ├── CollectFilterDestinationsFromNetworkAnchor() - Gathers all same-item black filter destinations on a connected network
│   ├── TryChooseBalancedFilterDestination() - Stable round-robin selection across a filter group
│   ├── ClearFilterDistributionState() - Reset splitter cursors on save/load
│   ├── IsConveyorTile() - Check if tile is conveyor path
│   ├── IsInputOnlyContainer() - Check for white crates only
│   ├── IsFilterCrate() - Check for black crates only
│   ├── IsVacuumCrate() - Check for green crates only
│   ├── IsSpecialContainer() - Exclude fish ponds, terrariums, auto placers, etc.
│   ├── TryFindBestOutputDestinationFromNetworkAnchor() - Balanced filter route, then matching stack, then empty chest
│   │   └── Generic destination searches must skip green VacuFarm crates unless a black filter crate is explicitly routing into them
│   ├── TryFindFilterDestinationFromCrate() - Resolve a black crate's touching storage chest
│   ├── FindChestAt() - Get Chest object at position
│   │   └── Filters out tileObjectItemChanger (gacha machines, etc.)
│   ├── IsAutoSorter() - Check if chest is Auto Sorter
│   ├── QueueAutoSorterUpdate() - Debounced trigger for Auto Sorter firing
│   ├── AutoSorterBatchSort() - Coroutine: 1s delay, activate nearby chests, then fire slots
│   ├── EnsureNearbyChestsActive() - Load destination chests into activeChests from save data
│   ├── EnsureAutoSorterStatusClean() - Fix stale onTileStatusMap after save/load
│   ├── FindSlotForItem() - Find slot for stacking or empty slot
│   └── ChestHasItems() - Check if chest has any items
│
├── ConveyorPathfinder (static class)
│   ├── IsPartOfObject() - Check if tile belongs to a multi-tile object at root
│   └── FindPath(start, end, inside) - BFS with parent tracking
│       - Phase 1: Walk conveyor tiles, check distance-1 neighbors for destination
│       - Phase 2: If not found, check distance-2 (outer-row crab pots)
│       - Multi-tile aware: scans radius around start for all object tiles
│       - Large-array safe: traverses the full connected conveyor run with no arbitrary 500-tile cutoff
│       - Returns ordered List<Vector2Int> path including start and end
│
├── ConveyorAnimator (static class)
│   ├── ConveyorAnimation (inner class)
│   │   └── Fields: visual, path, currentSegment, segmentProgress, onArrival, itemId, startDelay
│   ├── ArcAnimation (inner class)
│   │   └── Used by quarry item-pop visuals before conveyor deposit
│   ├── FINAL_TILE_FRACTION = 0.3f — item travels 30% into destination tile
│   ├── TileToWorld(x, y) - Convert tile coords to world position
│   │   └── new Vector3(x*2, heightMap[x,y]+0.5, y*2) — NO centering offset
│   ├── StartAnimation(itemId, amount, path, onArrival, delay=0)
│   │   └── Instantiates itemPrefab, scale 0.75, disables physics/collider
│   │   └── Removes DroppedItem/DroppedItemBounce, applies SetItemTexture
│   │   └── Hides visual if delay > 0 (shown when delay elapses)
│   ├── UpdateAnimations() - Called every frame from Plugin.Update()
│   │   └── Handles startDelay countdown (visual hidden until ready)
│   │   └── Lerps position along path segments
│   │   └── FINAL_TILE_FRACTION on last segment
│   │   └── Executes onArrival callback when animation completes
│   ├── AnimateTransfer(itemId, amount, source, dest, inside, callback, delay=0)
│   │   └── General-purpose: FindPath + StartAnimation, instant fallback if disabled/no path
│   ├── ReserveTarget/UnreserveTarget/IsTargetReserved
│   │   └── HashSet<long> keyed by (x<<32|y), prevents duplicate transfers during animation
│   ├── ClearAllAnimations() - Executes ALL pending callbacks, destroys visuals, clears reservations
│   └── HasActiveAnimations() - Check if any animations running
│
├── CrabPotHelper (static class)
│   ├── TryLoadBaitIntoCrabPots() - Entry point (outdoor only)
│   ├── TryLoadBaitInRadius() - 2-tile Manhattan distance search
│   ├── TryLoadBaitViaConveyorPath() - BFS conveyor + 2-tile radius
│   ├── TryLoadBaitAtPosition() - Check for crab pot, needs bait, IsTargetReserved check
│   └── TryLoadBaitFromChest() - ReserveTarget, animate, deposit+UnreserveTarget on arrival
│
├── GrowthStageHelper (static class)
│   ├── TryLoadItemsIntoGrowthStages() - Entry point
│   ├── TryLoadInRadius() - 1-tile radius
│   ├── TryLoadViaConveyorPath() - BFS conveyor + 1-tile radius
│   ├── TryLoadAtPosition() - Check growth stage, SKIP crab pots, IsTargetReserved check
│   └── TryLoadFromChest() - ReserveTarget, animate, deposit+UnreserveTarget on arrival
│
├── SiloHelper (static class)
│   ├── TryLoadFeedIntoSilos() - Entry point (outdoor only)
│   ├── TryLoadFeedInRadius() - 1-tile radius
│   ├── TryLoadFeedViaConveyorPath() - BFS conveyor
│   ├── TryLoadFeedAtPosition() - Multi-tile root resolution, isSilo check, IsTargetReserved
│   └── TryLoadFeedFromChest() - Staggered bag animations:
│       - toTransfer = SiloFillSpeed (default 10 = 5% of 200)
│       - numBags = toTransfer/2 (5 visible bags)
│       - Each bag deposits 2 items on arrival
│       - Stagger interval = 0.5 tiles / AnimationSpeed
│       - Only last bag calls UnreserveTarget
│
├── VacuumCrateHelper (static class)
│   ├── Green crates only
│   ├── ProcessDayChangeVacuumHarvests() - Day-change crop/tree harvest wave runner
│   ├── TryVacuumNearbyDrops() - Suck nearby drops into the crate, then route them onward
│   ├── TryProcessFarmTile() - Till/fertilize/plant nearby farm tiles during normal scan passes
│   ├── TryFlushStoredItemsToNetwork() - Push one grouped chunk back out of the green crate per scan pass
│   └── Uses routed output destinations, so filtered black-crate routes outrank generic storage, but generic network destinations must not treat green crates as normal storage
│
├── FilterCrateHelper (static class)
│   ├── Black crates only
│   ├── TryPullFilteredItemsFromNetwork() - Keeps one sample item, pulls one grouped chunk from connected source chests per scan pass
│   ├── FindSourcePull() - BFS the connected conveyor network for a valid source chest
│   ├── Uses the black crate tile as the conveyor arrival point
│   └── Deposits into the regular chest touching the black crate, using the shared filter splitter
│
└── FishPondHelper (static class)
    ├── TryFeedPondsAndTerrariums() - Entry point (outdoor only, 3-tile radius)
    ├── TryFeedInRadius() - 3-tile Manhattan distance search
    ├── TryFeedViaConveyorPath() - BFS conveyor + 3-tile radius at each tile
    ├── TryFeedAtPosition() - Multi-tile root resolution, IsTargetReserved check
    ├── TryLoadFoodFromChest() - ReserveTarget, animate, deposit+UnreserveTarget
    ├── ProcessPondAndTerrariumOutput() - Day change extraction entry point
    ├── ExtractOutputViaConveyorPath() - BFS conveyor for extraction targets
    └── TryExtractOutputAtPosition() - Animate output from pond/terrarium to chest
```

---

## How Systems Work

### Conveyor Visual Animations (v1.5.0)

All conveyor transfers now show items visually sliding along the path. The system has three layers:

**ConveyorPathfinder** — Finds the tile-by-tile path between source and destination:
1. BFS from source, walking only conveyor tiles, tracking parent pointers
2. Multi-tile aware: scans radius 5 around source to find all tiles of multi-tile objects (silos, fish ponds)
3. Destination check: matches the exact tile OR any extension tile of a multi-tile object
4. Phase 2 fallback: if destination not found at distance 1, checks distance 2 (outer-row crab pots)
5. Large-array safe: pathfinding traverses the full connected conveyor run, so long single-chest conveyor runs still get a real path
6. Returns ordered `List<Vector2Int>` from source to destination (or nearest reachable tile)

**ConveyorAnimator** — Manages visual item models sliding along paths:
1. `StartAnimation()` instantiates the item's 3D prefab at 0.75 scale, disables physics
2. `UpdateAnimations()` runs every frame — lerps position along path segments at `AnimationSpeed` tiles/sec
3. `FINAL_TILE_FRACTION` (0.3) — item travels 30% into the final destination tile before vanishing
4. `startDelay` field — visual hidden until delay elapses (used for staggered silo bags and day-change mega-array batching)
5. On arrival: executes deposit callback, destroys visual model

**Reservation system** — Prevents duplicate transfers during animation:
- `ReserveTarget(x,y)` called before animation starts
- `IsTargetReserved(x,y)` checked before starting new transfers to same target
- `UnreserveTarget(x,y)` called in deposit callback when animation arrives
- Used by: machines, crab pots, silos, fish ponds, terrariums, growth stages

**Key constants:**
- `FINAL_TILE_FRACTION = 0.3f` — 30% into destination tile
- Item scale: `0.75f` (smaller than dropped items)
- Tile-to-world: `new Vector3(x*2, heightMap[x,y]+0.5, y*2)` — matches game's `NetworkMapSharer` placement

**When animation is disabled** (`AnimationEnabled = false`):
- `AnimateTransfer()` fires the deposit callback immediately (same frame)
- Functionally identical to v1.4.0 behavior
- Items removed from source, deposited to destination instantly

### Day-Change Mega Arrays (single chest/crate support)

### Quarry Auto-Harvest

Autom8er now leaves vanilla quarry spawning alone and only automates the harvest/output part.

How it works:
1. Dinkum spawns the normal quarry outputs on sleep/day change.
2. `NewDayHarvestPatch` waits for chunk refresh, then calls `QuarryHelper.ProcessDayChangeQuarries()`.
3. The scan starts from normal output chests and connected conveyor networks. It does not do a global quarry scan.
4. If a reachable quarry has spawned a valid vanilla quarry node on one of its 4 adjacent same-height tiles, Autom8er queues that node for harvest.
5. Quarry break starts are staggered per destination chest: every 5 connected quarries add `+0.2s` before their break begins, with a `1.0s` initial delay before the first break wave.
6. At break time, `HarvestQuarryNodeToStorage()` runs the node's real vanilla `onDeathServer()` path while temporary Harmony capture patches intercept the resulting `spawnAServerDrop()` calls.
7. Each captured drop first plays a short arc from the broken node into the quarry tile, then uses the normal adjacent-chest or conveyor deposit logic.
8. The quarry tile itself is the deposit origin, so any side of the quarry can connect to the chest/conveyor network.

Rules that matter:
- This system only harvests already-spawned vanilla quarry outputs. Autom8er does not create custom timed quarry spawns anymore.
- The quarry must be reachable from a normal output chest directly or through a connected conveyor run.
- Quarry nodes are only auto-harvested when the node tile is on the same elevation as the quarry tile, matching vanilla spawn rules.
- Quarry break staggering is separate from item-transfer staggering. The break wave is throttled every 5 quarries to reduce block-break spikes on very large arrays.
- Hidden treasure / detector `X` outcomes are not part of the auto-harvest node set and are left alone.
- Quarry drops still use the normal storage routing rules: adjacent chest first, then conveyor path, then fallback chest search.
- Layout rule: each quarry in an array needs its own valid adjacent spawn tiles. If two quarries are built so close that they compete for the same orthogonal spawn space, vanilla quarry spawning becomes ambiguous and that layout is not considered a supported automation pattern.
- Safe layout rule: mirrored rows with a dedicated conveyor lane and separate spawn lanes per row work correctly. Opposing quarries that share a single middle spawn lane do not.

Large bee hive / key cutter / worm farm / crab pot / pond / terrarium setups can now be driven from a single valid output chest:
1. The scan still starts from chests, not from every machine on the map
2. If a chest discovers one harvestable machine, `ScanConnectedHarvestArray()` continues through adjacent machines of the same `tileObjectId`
3. This lets one chest reach a long contiguous machine block instead of requiring multiple chests spaced through the array
4. Day-change outputs are staggered per destination chest:
   - Outputs `0-99` start immediately
   - Outputs `100-199` get `+0.2s`
   - Outputs `200-299` get `+0.4s`
   - and so on
5. There is no hard cap on day-change animation count; the stagger only spreads the start cost across batches
6. There is no arbitrary chest-scan cutoff for connected mega arrays; traversal continues until the reachable connected machine/conveyor network ends
7. Long conveyor routes still animate because pathfinding now traverses the full connected conveyor run instead of cutting off after 500 visited tiles

### Auto Input (Chest → Machine) — ItemDepositAndChanger machines
1. `Update()` runs at `ScanInterval` (default 0.3s) on server
2. Iterates all `ContainerManager.manage.activeChests`
3. For each chest with items:
   - `FishPondHelper.TryFeedPondsAndTerrariums(chest, inside)` - FIRST (see Rule 7)
   - `TryFeedAdjacentMachine(chest, inside)` - Check 4 adjacent tiles (respects reservations)
   - If no adjacent machine, `TryFeedMachineViaConveyorPath(chest, inside)` - BFS along conveyor
   - One machine fed per chest per cycle (speed controlled by ScanInterval)
4. Validates: machine empty, item can be deposited, enough quantity
5. If `KeepOneItem` enabled (and source is NOT Auto Sorter), requires `amountNeeded + 1` items before taking
6. Item removed from chest immediately; machine feed happens on animation arrival
7. Adjacent transfers (no conveyor) are always instant

### Auto Output (Machine → Chest)
1. Harmony Prefix on `ItemDepositAndChanger.ejectItemOnCycle()`
2. Gets result item ID from `itemChange.getChangerResultId()`
3. Priority 1: `TryDepositToAdjacentChest()` - Direct adjacent (instant, skips white crates + special containers)
4. Priority 2: `TryDepositViaConveyorPath()` - BFS along conveyor, animated to chest
5. If deposited into Auto Sorter, calls `QueueAutoSorterUpdate()` to trigger firing
6. If deposited, returns `false` to skip original eject (no ground drop)
7. If no chest found, returns `true` to let item drop normally

### Auto Sorter Integration
**How Auto Sorters work in vanilla:** The game's `AutoSortItemsIntoNearbyChests` coroutine scans `activeChests` within 10 tiles for any chest that already has at least 1 of the same item (`GetAmountOfItemInside >= 1`). It fires ONE full stack per invocation, then yield breaks. The vanilla trigger is the player opening and closing the Auto Sorter.

**What Autom8er adds:**
1. **Removed from `IsSpecialContainer()`** — Auto Sorters participate as normal I/O chests for automation
2. **Exempt from KeepOneItem** — All 6 KeepOneItem check locations skip the filter for Auto Sorters via `IsAutoSorter(chest)`. This lets Auto Sorters empty completely (they're intermediary, not storage)
3. **Triggered on deposit** — Three deposit paths trigger `QueueAutoSorterUpdate()`:
   - `TryDepositToAdjacentChest()` (machine output → adjacent Auto Sorter)
   - `TryDepositViaConveyorPath()` (machine output → conveyor → Auto Sorter)
   - `TryDepositHarvestToChest()` (day-change harvest → Auto Sorter)
4. **Debounce system** — Prevents lag from rapid deposits:
   - `QueueAutoSorterUpdate(chest)` uses `HashSet<long> pendingSorts` keyed by `(xPos << 32 | yPos)`
   - If key is new: starts `AutoSorterBatchSort` coroutine, adds key to set
   - If key exists: skips (coroutine already running for this sorter)
   - `AutoSorterBatchSort`: 1s initial delay (accumulation window), `EnsureNearbyChestsActive()` once, then loops up to 24 cycles calling game's `AutoSortItemsIntoNearbyChests` with 0.5s pauses between each, exits when empty, removes key from set
5. **First-load activation** — `EnsureNearbyChestsActive()` scans 10-tile radius for chest tile objects and loads them from save data into `activeChests` via `getChestForRecycling()`. Runs once per sort batch, no-op if chests already active
6. **Stale status cleanup** — `EnsureAutoSorterStatusClean()` resets `onTileStatusMap` to 0 if `playingLookingInside` is 0 but status was non-zero (stale from save data)

### Fish Pond Automation
**Chest layout:** 24 slots total — slots 0-4 = creatures, slot 22 = food input, slot 23 = output (roe)

**Feeding (continuous, in ProcessAllChests):**
1. Runs BEFORE machine feeding (critters have `itemChange` — see Rule 7)
2. Scans 3-tile Manhattan distance radius around source chest
3. Also searches via conveyor BFS + 3-tile radius at each conveyor tile
4. For each found fish pond (multi-tile root resolved):
   - Checks IsTargetReserved — skips if food already in transit
   - Gets pond chest via `getChestForWindow(rootX, rootY, null)`
   - Checks slot 22 — if empty, searches source chest for items with `underwaterCreature` set
   - ReserveTarget, animate, deposit on arrival + UnreserveTarget

**Extraction (day change, via ProcessPondAndTerrariumOutput):**
1. Copies `activeChests`, iterates each regular outdoor chest
2. Scans 3-tile radius + conveyor for adjacent fish ponds
3. For each pond with output in slot 23:
   - Counts creatures in slots 0-4
   - **5 creatures (full):** Extract ALL roe
   - **< 5 creatures AND HoldOutputForBreeding:** Only extract above 15 roe
   - **HoldOutputForBreeding disabled:** Extract ALL
4. Animated transfer from pond to chest; FallbackDepositToAnyChest if dest full on arrival

### Bug Terrarium Automation
Same architecture as fish ponds but:
- Food = honey (`fishPondManager.honeyItem.getItemId()`)
- Output = cocoons (`fishPondManager.silkItem.getItemId()`)
- Breeding hold threshold = 10 cocoons (vs 15 roe for fish ponds)
- Food check: matches honey item ID directly (not `underwaterCreature`)

### Silo Auto-Fill
1. Runs every tick in `ProcessAllChests()` after machine feeding
2. Finds silos via `SprinklerTile.isSilo` on TileObject
3. Multi-tile resolution: silos are 2x2, non-root tiles have `onTileMap < -1`
4. Uses `findMultiTileObjectPos()` to get root tile for status read/write
5. **Animal Food hardcoded as item 344** — `ItemSign.itemCanPlaceIn` is NOT on the prefab, only on world instances
6. Fills `SiloFillSpeed` items per tick (default 10 = 5% of 200 capacity)
7. **Staggered bag animations:** `numBags = toTransfer / 2` (5 bags), each depositing 2 items
   - Bags staggered by `0.5 / AnimationSpeed` seconds apart
   - Only last bag unreserves the silo
8. Visual fill updates automatically via `ShowObjectOnStatusChange.toScale` scaling Y by status/200
9. Max capacity: 200 (stored in `onTileStatusMap`)

### Crab Pot Automation
1. **Bait Loading** (continuous, in `ProcessAllChests`):
   - 2-tile Manhattan distance radius (crab pots are in water, 1 tile from shore)
   - Uses `TileObjectGrowthStages.itemsToPlace[]` to match valid bait items dynamically
   - Checks `currentStatus < maxStageToReachByPlacing` to know if bait needed
   - IsTargetReserved check prevents duplicate feeding during animation
   - Conveyor support: BFS along conveyors, checking 2-tile radius at each conveyor tile
   - Pathfinder distance-2 support: outer-row pots (2 tiles from conveyor) animate to the intermediate tile
2. **Harvest Output** (day change, via `NewDayHarvestPatch`):
   - Scans for crab pots at harvestable state
   - 2-tile radius search for output chest
   - Animated transfer from pot to chest

### Growth Stage Loading (Incubators, etc.)
1. Runs every tick in `ProcessAllChests()` after crab pot loading
2. Handles ANY `TileObjectGrowthStages` with `itemsToPlace[]` EXCEPT crab pots
3. Crab pots excluded because CrabPotHelper handles them with 2-tile radius
4. Uses standard 1-tile adjacency + conveyor BFS
5. IsTargetReserved + ReserveTarget/UnreserveTarget for animation safety
6. Matches items via `growth.itemsToPlace[]` array (dynamic, not hardcoded)

### Harvest Day Change (Bee Houses, Key Cutters, Worm Farms)
1. Harmony Postfix on day change event
2. Scans all chests and their conveyor networks for harvestable machines
3. **CRITICAL GUARD: `if (growth.spawnsFarmAnimal) return;`** — Never harvests incubators or animal spawners
4. Animated transfer from machine to chest; FallbackDepositToAnyChest if dest full
5. Resets growth stage via `takeOrAddFromStateOnHarvest`

### Conveyor Path BFS Algorithm
1. Start from machine/chest position (multi-tile: scan all object tiles)
2. Check adjacent tiles for conveyor tile type
3. Add matching tiles to queue, track visited in `HashSet<long>`
4. Limit to 50-500 steps depending on context
5. At each conveyor tile, check adjacent positions for targets
6. Phase 2: if destination not found at distance 1, check distance 2 from all visited conveyors
7. For crab pots/fish ponds: extended radius at each conveyor tile
8. For chests in `FindChestViaConveyorPath`: check ADJACENT to conveyor tiles (not ON them)

### Input-Only Logic
- `IsInputOnlyContainer()` checks TileObject ID against WHITE_CRATE_TILE_ID (417) and WHITE_CHEST_TILE_ID (430)
- Called in output functions to skip white containers
- White containers CAN still feed machines (not blocked in input functions)

### Filter Crates
- `Black Wooden Crate` is no longer treated as a normal storage chest or legacy output-only crate
- A black crate holds one or more sample items that define filter keys
- Matching items first route to a black crate filter destination before ordinary matching-stack chests
- The real storage is the regular chest touching that black crate
- Conveyor visuals end on the black crate tile, then the item is deposited into the touching storage chest
- If multiple black crates on the same connected network filter the same item, Autom8er round-robins across them instead of always picking the first one found
- That splitter applies both to normal routed outputs and to the black crate's own active pull behavior, so trickle inputs and large dumps both distribute across the same filter group
- Filter pulls now use the same cadence model as the rest of Autom8er belts: one grouped chunk per scan pass instead of burst-scheduling a whole source stack
- Stackable filter pulls still animate in `10`-item chunks, so the belts stay readable without changing final totals
- The black crate never pulls back out of its own touching destination chest
- Durability items in a black crate are filter keys only. Their durability value is preserved and not normalized

### Vacuum Crates
- `Green Wooden Crate` keeps a `21 x 21` harvest area and a `25 x 25` pickup area
- Day-change harvests break in waves of 5 tiles with a 0.2s delay between waves
- Multi-yield crop/tree drops are grouped so only one conveyor visual is shown per harvested tile/item group
- Vacuumed items route through the connected network using the same destination priority as other outputs, so filter crates can sort vacuum harvests too
- Vacuum crate exports now use the scan loop as pacing: one grouped chunk per scan pass, with a small short-stack buffer before flushing
- This keeps mixed-item orchard or farm harvests from flooding the belt with same-frame bursts

### Stackable Critters (v1.5.0)
On first `Update()` tick, `ApplyStackableCritters()` iterates all `Inventory.Instance.allItems` and sets `isStackable = true` on any item with `underwaterCreature` set. This directly modifies the item data — no Harmony patch needed. Works everywhere including the 2 places in `Inventory.cs` that check the `isStackable` field directly (UI stacking logic). Config `StackableCritters = false` skips the modification entirely (vanilla behavior). Fish pond feeding is unaffected — our code already takes exactly 1 critter per feeding cycle regardless of stack size.

---

## Configuration

### Config File Location
```
BepInEx/config/topmass.autom8er.cfg
```

### Config Options
| Setting | Section | Default | Range | Description |
|---------|---------|---------|-------|-------------|
| `ConveyorTileItemId` | Conveyor | 1747 | Any valid path item ID | Floor tile used as conveyor belt |
| `ScanInterval` | Performance | 0.3 | 0.1-1.0 | Seconds between automation scans |
| `KeepOneItem` | Automation | false | true/false | Reserve 1 item in slots as placeholder (Auto Sorters always exempt) |
| `SiloFillSpeed` | Automation | 10 | 1-20 | Animal Food items loaded per tick (5 bags shown, each carries 2) |
| `AutoFeedPondsAndTerrariums` | Fish Pond & Terrarium | true | true/false | Auto-feed ponds/terrariums + extract on day change |
| `HoldOutputForBreeding` | Fish Pond & Terrarium | true | true/false | Hold 15 roe / 10 cocoons when < 5 creatures |
| `AnimationEnabled` | Conveyor Animation | true | true/false | Show items visually moving along conveyors. Disable for instant transfers |
| `AnimationSpeed` | Conveyor Animation | 2 | 0.5-10 | Speed of conveyor animations in tiles per second |
| `StackableCritters` | Quality of Life | true | true/false | Make all critters stackable in inventory and chests |

### Runtime Tile Type Caching
```csharp
InventoryItem pathItem = Inventory.Instance.allItems[ConveyorTileItemId];
ConveyorTileType = pathItem.placeableTileType;
// Then compare: WorldManager.Instance.tileTypeMap[x, y] == ConveyorTileType
```

---

## Critical Rules

### 1. Never Auto-Harvest Animal Spawners
```csharp
// In TryAutoHarvestAt():
if (growth.spawnsFarmAnimal)
    return;  // NEVER touch incubators or animal spawners
```
Without this guard, incubators near conveyors will have their eggs consumed without spawning animals.

### 2. Silo ItemSign is NOT on Prefab
The `ItemSign` component (which holds `itemCanPlaceIn`) exists on the world-instantiated object, NOT on the prefab in `WorldManager.Instance.allObjects[]`. Animal Food item ID 344 is hardcoded in `SiloHelper`.

### 3. Multi-Tile Object Root Resolution
Silos, fish ponds, terrariums, and other multi-tile objects store negative values in non-root tiles. Always resolve to root before reading/writing status:
```csharp
if (tileObjectId < -1)
{
    Vector2 rootPos = WorldManager.Instance.findMultiTileObjectPos(x, y);
    // Use rootPos for all status operations and ContainerManager calls
}
```

### 4. Crab Pots Need 2-Tile Radius
Crab pots are placed in water, 1 tile from shore. Chests and conveyors are on land. Need 2-tile Manhattan distance for detection. ConveyorPathfinder has Phase 2 fallback for outer-row pots at distance 2 from conveyor.

### 5. Conveyor Chest Detection: Adjacent, Not On
`FindChestViaConveyorPath` must check tiles ADJACENT to conveyor tiles for chests, not ON them. Chests sit next to conveyors, not on top of them.

### 6. Copy Active Chests Before Iterating
When iterating `ContainerManager.manage.activeChests`, operations that modify chests can trigger collection modification exceptions. Copy the list first if needed (day-change processing uses `new List<Chest>(activeChests)`).

### 7. FishPondHelper MUST Run Before Machine Feeding
In `ProcessAllChests()`, fish pond feeding MUST happen before `TryFeedAdjacentMachine()`. Critters (underwater creatures) have `itemChange` set on them, which means the machine-feeding code would try to deposit them into furnaces/grinders. By feeding ponds first, critters get consumed for their intended purpose.

### 8. Auto Sorters Are Exempt from KeepOneItem
All 6 KeepOneItem check locations use `!IsAutoSorter(chest)` to exempt Auto Sorters. This is essential because Auto Sorters are intermediary — they need to fully empty to fire items to their final destinations.

### 9. Filter tileObjectItemChanger in FindChestAt
`FindChestAt()` skips any TileObject that has `tileObjectItemChanger != null`. This prevents ghost chest creation from machines like gacha machines that have `tileObjectChest` but are not legitimate storage containers.

### 10. Tile-to-World Conversion: NO Centering Offset
Game places tile objects at `new Vector3(x*2, heightMap[x,y], y*2)` — confirmed in `NetworkMapSharer.cs:1561`. Animation uses the same formula plus 0.5 Y offset to float items above ground. Do NOT add +1 centering offset.

### 11. Reserve Targets During Animation
Every animated transfer MUST: (1) ReserveTarget before animation, (2) UnreserveTarget in deposit callback. Without this, the scan loop sees the target as still needing items and sends duplicates, causing flooding (items flying out everywhere).

### 12. No dropAnItem in Callbacks
Never use `WorldManager.Instance.dropAnItem()` in animation callbacks — it creates ghost items (visual-only, can't be picked up) when called from Update context. Use `FallbackDepositToAnyChest()` instead, which BFS-searches for any valid chest on the conveyor network.

### 13. activeChests Is Lazily Populated
`ContainerManager.manage.activeChests` is NOT pre-populated on game load. Chests are only added when explicitly loaded via `getChestForRecycling()` or `getChestSaveOrCreateNewOne()`. On first game load, many chests exist in save data but aren't in `activeChests` until something touches them. Any code that iterates `activeChests` to find targets (like `AutoSortItemsIntoNearbyChests`) will find nothing if destination chests haven't been activated yet. Use `EnsureNearbyChestsActive()` pattern to pre-load chests from save data before scanning.

---

## Special Containers

### Excluded via IsSpecialContainer() (won't receive machine outputs):
- Fish Ponds (`isFishPond`)
- Bug Terrariums (`isBugTerrarium`)
- Auto Placers (`isAutoPlacer`)
- Mannequins (`isMannequin`)
- Tool Racks (`isToolRack`)
- Display Stands (`isDisplayStand`)

### NOT in IsSpecialContainer (participates in automation):
- **Auto Sorters** — Removed from exclusion list in v1.4.0. Acts as I/O chest, fires items to nearby sorted storage.

### ChestPlaceable Flags (complete list from game code):
`isStash`, `isFishPond`, `isBugTerrarium`, `isAutoSorter`, `isAutoPlacer`, `isMannequin`, `isToolRack`, `isDisplayStand`

---

## Build & Deploy

### Required DLLs in `code/Libs/`
From `BepInEx/core/`:
- BepInEx.Core.dll
- BepInEx.Unity.dll
- 0Harmony.dll

From `Dinkum_Data/Managed/`:
- Assembly-CSharp.dll
- Assembly-CSharp-firstpass.dll
- Mirror.dll

### Build Command
```bash
cd /home/topmass/Code/dinkum-mods/Autom8er && dotnet build code/Autom8er.csproj -c Release
```

### Output
```
code/bin/Release/net472/topmass.autom8er.dll
```

### Install Locations
```bash
# Mod repository
cp code/bin/Release/net472/topmass.autom8er.dll mod/

# Game plugins
cp code/bin/Release/net472/topmass.autom8er.dll /home/topmass/Steam/steamapps/common/Dinkum/BepInEx/plugins/
```

---

## Game API Reference

### Key Classes
- `ContainerManager.manage` - Chest management singleton
- `WorldManager.Instance` - World tile data singleton
- `Inventory.Instance` - Item database singleton
- `NetworkMapSharer.Instance` - Network RPC calls
- `HouseManager.manage` - Interior/house management
- `fishPondManager` - Fish pond item references (roe, honey, silk/cocoons)

### Key Methods
```csharp
// Deposit item into machine
NetworkMapSharer.Instance.RpcDepositItemIntoChanger(itemId, x, y);
NetworkMapSharer.Instance.RpcDepositItemIntoChangerInside(itemId, x, y, houseX, houseY);

// Start machine processing
NetworkMapSharer.Instance.startTileTimerOnServer(itemId, x, y, inside);

// Update tile status (silos, growth stages)
NetworkMapSharer.Instance.RpcGiveOnTileStatus(newStatus, x, y);

// Update chest slot
ContainerManager.manage.changeSlotInChest(x, y, slot, itemId, stack, inside);

// Get chest at position (returns null if not loaded)
ContainerManager.manage.getChestForWindow(x, y, inside);

// Get chest at position (loads from save if needed — CAN CREATE GHOST CHESTS)
ContainerManager.manage.getChestForRecycling(x, y, inside);

// Find multi-tile root
WorldManager.Instance.findMultiTileObjectPos(x, y);

// Check if item can go in machine
item.itemChange.checkIfCanBeDepositedServer(tileObjectId);
item.itemChange.getAmountNeeded(tileObjectId);
item.itemChange.getChangerResultId(tileObjectId);

// Auto Sorter firing (vanilla game)
ContainerManager.manage.AutoSortItemsIntoNearbyChests(chest);  // coroutine

// Fish pond items
fishPondManager.honeyItem.getItemId();    // terrarium food
fishPondManager.fishRoe.getItemId();      // pond output
fishPondManager.silkItem.getItemId();     // terrarium output
Inventory.Instance.allItems[id].underwaterCreature  // pond food check (reference type, Unity implicit bool)
```

### HouseDetails (Inside Buildings)
- `inside.houseMapOnTile[x, y]` - TileObject ID inside building
- `inside.houseMapOnTileStatus[x, y]` - Machine status inside building
- `chest.insideX / chest.insideY` - If != -1, chest is inside a building
- `HouseManager.manage.getHouseInfoIfExists(x, y)` - Get HouseDetails

---

## Troubleshooting

### Conveyor Not Working
1. Check console for "Conveyor tile 'X' (ID Y) -> tileType = Z"
2. If tileType = -1, the configured item isn't a valid path
3. Conveyor works outdoors; indoor support varies

### Items Not Depositing
1. Check if destination is a special container (excluded)
2. Check if destination is white crate (input-only, no outputs)
3. Check if chest has space (FindSlotForItem returns -1 if full)

### Machine Not Auto-Loading
1. Check if machine is empty (status -1 or -2)
2. Check if item can be deposited in that machine type
3. Check if chest has enough quantity (e.g., 5 ore for furnace)

### Silo Not Filling
1. Silo uses `SprinklerTile.isSilo` — verify TileObject ID 302
2. Must contain Animal Food (item 344) in chest
3. Multi-tile: conveyor can touch any of 4 silo tiles (pathfinder handles multi-tile)
4. ItemSign is NOT on prefab — item 344 is hardcoded
5. Check config `SiloFillSpeed` — old configs may still have value 2

### Animation Not Playing
1. Check `AnimationEnabled` in config (default true)
2. If only some targets miss animation: pathfinder may not find path (check multi-tile adjacency)
3. For outer-row crab pots: Phase 2 distance-2 check handles these
4. If animation disabled mid-flight: running animations complete normally, only new ones skip

### Fish Pond Not Feeding
1. Chest must be within 3 tiles (Manhattan distance) or connected via conveyor
2. Slot 22 must be empty for food to load
3. Source chest must contain items with `underwaterCreature` set (critters)
4. Ponds are outdoor only — `inside` must be null
5. Fish ponds are multi-tile (5x5) — pathfinder handles all extension tiles

### Auto Sorter Not Firing
1. Items only fire to chests that already have ≥1 of the same item (game's vanilla matching logic)
2. Target chests must be within 10 tiles
3. Check that debounce coroutine is running (1s initial delay before first fire)
4. Auto Sorter fires ONE slot per cycle with 0.5s pauses between — large inventories take time
5. On first game load, destination chests must be in `activeChests` — `EnsureNearbyChestsActive()` handles this

### Incubator Not Loading
1. Uses `TileObjectGrowthStages.itemsToPlace[]` — same system as crab pots
2. Handled by `GrowthStageHelper` (1-tile radius, standard adjacency)
3. Crab pots excluded from this (handled separately with 2-tile radius)
4. Check that incubator is empty (status < maxStageToReachByPlacing)

### Animals Not Hatching (Incubator Bug)
If animals aren't spawning from incubators near conveyors, check that `spawnsFarmAnimal` guard exists in `TryAutoHarvestAt()`. Without it, the harvest system consumes eggs.

---

## Version History
- **Current `main` (unversioned after 1.6.1)** - Green Auto Farmer crates harvest farm crops and natural foraging plants like bush lime style fruit/shrub outputs, vacuum nearby drops, till/fertilize/plant farm tiles, and route outputs back into conveyor networks. Farm crops stay on the dedicated Auto Farmer day-change harvest path. Natural foraging plants use the same vanilla `RpcHarvestObject(...)` / `TileObjectGrowthStages.harvest(...)` chain that manual right-click harvest uses, but when a green crate owns the tile their output is forced into that green crate. Black crates act as filter heads with touching storage chests or green Auto Farmer crates, support durability-item filter keys, round-robin split matching items across same-item filter groups, and reuse normal belt cadence for active pulls. Vacuum and filter exports now emit grouped chunks per scan pass instead of local burst scheduling. Classic day-change machine harvestables remain on the simpler stable chest-first path, are explicitly not Auto Farmer-owned, and now process in 100-machine / 0.2s waves to smooth heavy honey days. Day-change systems use kickoff delays only, not full completion waits.
- **1.6.1** - Fixed load-in catch-up processing so existing day-change outputs now run after loading into a save once the world, player, and chests are ready. Added fixed 1 second phasing between day-change harvest systems, quarry mining credit, and a subtle conveyor animation polish so items travel 30% into the destination tile before vanishing.
- **1.5.2** - Large single-chest day-change arrays now scan through the full connected harvest network with no arbitrary connected-array/path scan cutoffs. Day-change conveyor launches are staggered in 100-item / 0.2s batches per destination chest so 1000+ machine arrays stay visual while reducing the launch spike.
- **1.5.1** - Fixed player credit across all automation paths. Automated machine outputs now properly count for player progression when deposited into storage. Bee houses, key cutters, worm farms, crab pots, fish ponds, and bug terrariums also grant proper automation credit. Improved stability for large machine arrays.
- **1.5.0** - Conveyor visual animations (items slide along paths, transfer on arrival), ConveyorPathfinder (BFS with parent tracking, multi-tile aware, distance-2 fallback), ConveyorAnimator (visual management, stagger/delay, reservation system), staggered silo bags (5 visible bags each carrying 2 items), FallbackDepositToAnyChest safety, SaveGameAnimationClearPatch, AnimationEnabled/AnimationSpeed config, SiloFillSpeed bumped to 10, stackable critters QoL (configurable), auto sorter first-load activation fix
- **1.4.0** - Fish pond automation (feed critters + extract roe), bug terrarium automation (feed honey + extract cocoons), smart breeding hold (configurable), Auto Sorter as I/O chest (KeepOneItem exempt, auto-trigger on deposit, debounce batch sort), gacha machine ghost chest fix (tileObjectItemChanger filter in FindChestAt), Auto Placer support
- **1.3.1** - Fix incubator bug (spawnsFarmAnimal guard), add GrowthStageHelper for incubator loading via conveyors
- **1.3.0** - Silos (auto-fill, configurable speed), crab pots (bait + harvest via conveyor), bee houses/key cutters/worm farms (day change harvest), multi-tile object support, SiloFillSpeed config
- **1.2.0** - Added ScanInterval config (default 0.3s), KeepOneItem config for placeholder preservation
- **1.1.0** - Added configurable conveyor tile, path examples in config
- **1.0.0** - Initial release with auto I/O, white crate input-only, conveyor system

---

## Learnings

### Use One Belt Cadence Everywhere
Whenever a new feature pushes items onto conveyors, the safest visual rule is to reuse the main Autom8er scan cadence instead of inventing a local burst loop.

Rules:
1. Prefer one grouped transfer per scan pass over scheduling many same-frame animations.
2. Group stackable transfers before animating them so totals stay correct but the belt remains readable.
3. If a system feels visually worse than chest-to-machine loading, it is probably bypassing the scan-loop cadence.

### Splitters Need One Shared Stable Order
Round-robin splitting only works if every caller sees the same destination order for the same filter group.

Rules:
1. Collect all matching filter destinations on the connected network before choosing one.
2. Sort the group into one stable order before applying the shared cursor.
3. Clear splitter cursors on save/load so stale routing order does not leak across sessions.

### Filter Crates Are Routing Heads, Not Bulk Storage
Black crates should define routing, not become another storage layer that accumulates bulk items.

Rules:
1. The black crate keeps the sample item only.
2. The touching regular chest is the real storage destination.
3. The filter crate must never pull back out of its own touching destination chest.
4. Durability items in the black crate are filter keys only and must never be normalized or duplicated.

### Special Crate Behaviors Apply To Crates Only
Colored specialty behavior is crate-only. Colored chests must stay ordinary storage unless the user explicitly asks for a chest-specific feature.

Rules:
1. White crates are input-only. White chests are standard chests.
2. Black crates are filter crates. Black chests are standard chests.
3. Green crates are Auto Farmer crates. Green chests are standard chests.

### Green Auto Farmer Crates Are Not Generic Storage Destinations
Green crates may receive items through explicit filter routing and their own vacuum/farmer flows, but they must not act like ordinary conveyor destination chests.

Rules:
1. Generic machine and harvest output searches must skip green crates.
2. Generic fallback chest searches must skip green crates.
3. The only normal path that should intentionally put items into a green crate is black filter routing for farmer-managed inputs like seeds and fertilizer.
4. If a green crate receives a harvest output through a generic path, that is a bug and usually means a destination helper forgot to exclude vacuum crates.

### Farm Crops Are Never Generic Machine Harvests
Farm crops and natural foraging plant outputs belong to the Auto Farmer system, not the generic day-change machine harvest path.

Rules:
1. Generic `TryAutoHarvestAt()` must never own crop/tree harvests.
2. Classic machine harvest routing is for hives, key cutters, worm farms, crab pots, and similar machine-style harvestables.
3. If a crop output reaches a black crate through the classic machine path, the ownership boundary is broken.
4. Auto Farmer candidate detection must include natural foraging plants, but it must still exclude crafted machine harvestables like bee houses and key cutters.
5. Farm crops are always Auto Farmer-owned. Natural foraging plants should only be treated as Auto Farmer-owned when a green crate is actually within the Auto Farmer harvest radius.
6. Do not use a "distinct harvest output" check for natural foraging plants. Bush Lime and similar fruit items are placeable and can share the same tile object ID as the harvested plant, which makes that heuristic reject valid fruit trees.
7. The working Auto Farmer tree/shrub rule is: `harvestableByHand` + (`harvestDrop` or `dropsFromLootTable`) + no chest/item changer, while explicitly excluding the classic machine harvest tile IDs (bee house, worm farm, key cutter, crab pot).
8. Natural foraging plants must still use the vanilla right-click harvest chain (`RpcHarvestObject(...)` with `spawnDrop: true`, which then calls `TileObjectGrowthStages.harvest(...)`) so the live tile updates and the fruit drop path stays vanilla-correct.

### Day-Change Buffers Must Smooth The Heavy Systems
Large day-change arrays are safe when the work itself is staggered, not just the conveyor visuals.

Rules:
1. Keep phase gaps between the major day-change systems. The current working flow is harvest machines → 0.5s → Auto Farmers → 0.5s → ponds/terrariums → 0.5s → quarries.
2. Phase gaps are kickoff delays only. Do not wait for the previous system to finish animating/exporting before starting the next.
3. Classic machine harvest arrays like bee houses must batch the actual harvest work, not only the animation launch delay.
4. The current working batch for classic day-change machine harvests is 100 machines with a 0.2s pause between waves.
5. If honey days lag again, check the harvest coroutine wave size before touching the stable chest-first scan model.

### Automation Credit Must Be Granted On Successful Deposit
Vanilla standard machines often get visible progression through a dropped-item path: machine output becomes a world drop, then the player picks it up, and the pickup applies end-of-day tally. Autom8er bypasses that whenever it routes output directly into storage. For reliable credit, award progression when the automated deposit actually succeeds, including fallback chest reroutes.

Rules:
1. Standard machine input automation still needs `itemChange.checkTask(...)` on insert.
2. Standard machine output automation must grant output credit on successful chest deposit, not only on the original machine trigger.
3. Any fallback deposit path must trigger the same credit callback or large automation arrays can lose progression silently.
4. Harvest-style automation must only grant output credit after the item is successfully stored.
5. Any new automated Autom8er feature must preserve the same player credit vanilla would normally award for that action, if vanilla awards any.
6. If vanilla does not award credit for an action, Autom8er should not invent new credit for it.

### Single-Chest Mega Arrays Need Two Separate Fixes
Supporting a 1000+ contiguous day-change array from one chest required solving two different problems:
1. Discovery: chest-first scanning must continue through connected harvest machines of the same type instead of stopping at the initial chest radius
2. Visual launch cost: day-change outputs must be staggered per destination chest so one chest does not try to start every conveyor visual in the same frame window

Rules:
1. Keep chest-first scanning. Do not switch to global machine scans.
2. Continue array traversal only through adjacent machines with the same `tileObjectId`.
3. Do not cap day-change output count; stagger start times instead.
4. Do not add arbitrary connected-array scan limits for mega arrays; traversal should end naturally when the connected network ends.
5. If far rows deposit correctly but show no animation, check conveyor pathfinding first before changing harvest scan logic.

### Long Conveyor Visuals Fail If Pathfinding Caps Too Early
The old `ConveyorPathfinder.FindPath()` limit of 500 visited tiles, plus older capped BFS loops in other conveyor/harvest scans, were enough for normal builds but silently broke mega arrays: the deposit still happened, but `AnimateTransfer()` fell back to instant deposit because no path was returned or the traversal stopped early.

Rules:
1. Pathfinding should only walk connected conveyor tiles, not the full map blindly.
2. Connected-network traversal should stop because there are no more connected conveyor/machine tiles, not because an arbitrary step counter expired.
3. Missing visuals with correct final item totals usually means pathfinding failed and the instant deposit fallback ran.

### 1. `underwaterCreature` is a Reference Type (Unity Implicit Bool)
`InventoryItem.underwaterCreature` is NOT a boolean — it's a reference to a Unity object. In C#/Unity, checking `if (item.underwaterCreature)` works because Unity overrides the implicit bool operator for UnityEngine.Object. If the reference is null/destroyed, it's falsy; if it exists, it's truthy. Do NOT compare it to `true`/`false` directly — use the implicit bool pattern.

### 2. Critters Have `itemChange` Set
Underwater creatures (critters used as fish pond food) have `itemChange` on them. This means the generic machine-feeding code (`TryFeedAdjacentMachine`) will try to deposit critters into furnaces/grinders. This is why `FishPondHelper.TryFeedPondsAndTerrariums()` MUST run before machine feeding in `ProcessAllChests()`. If you move it after, critters will be consumed by machines instead of ponds.

### 3. `getChestForRecycling` Creates Ghost Chests
`ContainerManager.manage.getChestForRecycling(x, y, inside)` calls `getChestSaveOrCreateNewOne()`, which loads chest data from the save file at `/Chests/chest{x}+{y}.dat` and adds it to `activeChests`. If a machine (like a gacha machine) was previously at position `(x, y)` and had chest data saved, calling `getChestForRecycling` on that tile will create a phantom chest in memory that doesn't correspond to any real storage container. Items deposited there vanish.

**Fix:** `FindChestAt()` first tries `getChestForWindow()` (returns null if not loaded), then falls back to `getChestForRecycling()`. But before either, it checks `tileObj.tileObjectItemChanger != null` to filter out machines with internal inventory.

### 4. Gacha Machines Have `tileObjectChest` but Are NOT Storage
Gacha machines have both `tileObjectChest` and `tileObjectItemChanger` on their TileObject. They have chest-like internal inventory but can't be opened by the player as storage — they take dinks and process items. No legitimate storage container (regular chests, Auto Sorters, Auto Placers, fish ponds, terrariums) has `tileObjectItemChanger`. This is the distinguishing filter used in `FindChestAt()`.

### 5. Game's `AutoSortItemsIntoNearbyChests` Fires ONE Slot Per Invocation
The vanilla coroutine has a 0.5s initial `WaitForSeconds`, then iterates slots looking for an item with a matching target chest. When it finds one, it fires that full stack and `yield break`s. It does NOT process all slots in one call. This is why the debounce system loops up to 24 times — one invocation per slot that needs firing.

### 6. Game's Auto Sorter Target Matching
`AutoSortItemsIntoNearbyChests` scans `activeChests` (not just physical proximity) for chests within 10 tiles that have `GetAmountOfItemInside(itemId) >= 1`. It excludes `IsAutoSorter`, `IsFishPond`, and `IsBugTerrarium` as targets. So Auto Sorters cannot fire into other Auto Sorters, fish ponds, or terrariums — only into regular chests/crates.

### 7. Fish Pond/Terrarium Chest Layout
Both use a 24-slot chest:
- **Slots 0-4:** Creatures (fish or bugs)
- **Slot 22:** Food input (critters for ponds, honey for terrariums)
- **Slot 23:** Output (roe for ponds, cocoons for terrariums)

Access via `ContainerManager.manage.getChestForWindow(rootX, rootY, null)` using the root tile position.

### 8. `checkIfStackable()` for Non-Stackable Items
Some items (like critters) always have a stack of 1 and are not stackable. When `KeepOneItem` is enabled, a non-stackable item with stack=1 would never be taken (since keeping 1 means 0 available). The fix: skip `KeepOneItem` for non-stackable items: `if (keepOne && item.checkIfStackable() && stack < 2) continue;`

### 9. Fish Ponds Are 5x5, Terrariums Are 3x3
Both are multi-tile objects. The search radius of 3 tiles (Manhattan distance) from the chest position was chosen to cover all extension tiles of these objects. When a chest is adjacent to any edge tile, the 3-tile radius ensures the root tile is found.

### 10. `onTileMap` Value Meanings
- **`-1`**: Empty tile (nothing placed)
- **`< -1`** (e.g., -2, -3): Extension tile of a multi-tile object. Use `findMultiTileObjectPos()` to get root
- **`>= 0`**: TileObject ID (index into `WorldManager.Instance.allObjects[]`)

### 11. Day-Change Processing Needs Delay
The `NewDayHarvestPatch` hooks into `refreshAllChunksNewDay()`, but chunk data isn't immediately available. A 2-second `WaitForSeconds` delay in the `DelayedHarvestProcessing` coroutine ensures tile data is loaded before scanning for harvestable machines, pond/terrarium outputs, and vanilla quarry outputs.

### 12. dropAnItem Creates Ghost Items From Update Context
`WorldManager.Instance.dropAnItem()` creates physical items with DroppedItem/DroppedItemBounce components. When called from animation callbacks (which fire during Update), the resulting items can become ghost objects — visible but not properly networked, causing stuck items players can't pick up. Always use `FallbackDepositToAnyChest()` instead.

### 13. Animation Tile-to-World Must Match Game Placement
The game places objects at `new Vector3(xPos * 2, heightMap[xPos, yPos], yPos * 2)` — confirmed in `NetworkMapSharer.cs:1561`. Initially the animation used `x*2+1, y*2+1` which caused items to slide along the edge of tiles instead of the center. The correct formula has NO centering offset.

### 14. activeChests Lazy Loading Causes Silent Auto Sort Failure
On first game load from main menu, `loadOverFrames()` loads all save data but does NOT add chests to `activeChests`. Chests are only activated on-demand. The game's `AutoSortItemsIntoNearbyChests` iterates `activeChests` to find destination chests within 10 tiles. If destinations aren't active, the sort silently does nothing — no error, no log. Fix: scan the 10-tile radius for chest tile objects on `onTileMap` and call `getChestForRecycling()` for each before sorting. This is safe to call repeatedly — it returns the existing active chest if already loaded.

### 15. onTileStatusMap Persists Across Save/Load but playingLookingInside Doesn't
`WorldManager.Instance.onTileStatusMap` is saved/loaded from disk. `Chest.playingLookingInside` defaults to 0 on creation (not saved). After loading, a chest can have `onTileStatusMap != 0` (stale from previous session where a player had it open) but `playingLookingInside == 0`. The game's `AutoSortItemsIntoNearbyChests` checks `onTileStatusMap != 0` as a guard and silently aborts. Only furniture tile statuses are zeroed before save (SaveLoad.cs:3316-3319) — auto sorters are NOT furniture.
