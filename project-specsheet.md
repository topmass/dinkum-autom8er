# Autom8er Project Specsheet

## Overview
**Version:** 1.4.0
**Plugin ID:** `topmass.autom8er`
**DLL Name:** `topmass.autom8er.dll`
**Target Framework:** net472
**Game:** Dinkum (Steam)
**Dependencies:** BepInEx 6.0.0-pre.1

## Features Summary
1. **Auto Input/Output** - Chests next to machines automatically feed inputs and receive outputs
2. **White Crates = INPUT ONLY** - White crates/chests feed machines but won't receive outputs
3. **Conveyor Belt System** - Black Marble Path (configurable) routes items between distant chests and machines
4. **Silo Auto-Fill** - Silos fill from chests with Animal Food (configurable speed, visual fill effect)
5. **Crab Pot Automation** - Auto-bait loading + harvest output via chests/conveyors (2-tile radius for water)
6. **Harvest Machines** - Bee houses, key cutters, worm farms auto-harvest on day change
7. **Growth Stage Loading** - Incubators and other growth-stage objects accept items from chests/conveyors
8. **Farm Animal Protection** - Incubators and animal spawners are NEVER auto-harvested
9. **Fish Pond Automation** - Auto-feed critters from adjacent chests, extract roe on day change (3-tile radius)
10. **Bug Terrarium Automation** - Auto-feed honey from adjacent chests, extract cocoons on day change (3-tile radius)
11. **Auto Sorter Integration** - Auto Sorters work as automation I/O chests, exempt from KeepOneItem, fire items on deposit
12. **Auto Placer Support** - Auto Placers function as standard automation chests

---

## Key IDs

### TileObject IDs (for `WorldManager.Instance.onTileMap`)
| ID | Item | Purpose |
|----|------|---------|
| 302 | Silo | 2x2 multi-tile, holds 200 Animal Food |
| 417 | White Wooden Crate | INPUT ONLY container |
| 430 | White Wooden Chest | INPUT ONLY container |
| 985 | Egg Incubator | Growth stage object, accepts fertilized eggs |

### Item IDs (for `Inventory.Instance.allItems`)
| ID | Item | Purpose |
|----|------|---------|
| 344 | Animal Food | Silo fill item (hardcoded, ItemSign not on prefab) |
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

### Plugin.cs Layout (~2470 lines)
```
Autom8er namespace
├── Plugin : BaseUnityPlugin
│   ├── Config: ConveyorTileItemId, KeepOneItem, ScanInterval, SiloFillSpeed,
│   │          AutoFeedPonds, HoldOutputForBreeding
│   ├── Constants: WHITE_CRATE_TILE_ID (417), WHITE_CHEST_TILE_ID (430)
│   ├── Awake() - Init config, apply Harmony patches
│   ├── Update() - Scan timer, process chests at ScanInterval
│   ├── CacheConveyorTileType() - Get placeableTileType from item data
│   └── ProcessAllChests() - Main loop (ORDER MATTERS):
│       1. FishPondHelper.TryFeedPondsAndTerrariums ← MUST BE FIRST (see Rule 7)
│       2. TryFeedAdjacentMachine (furnaces, etc)
│       3. TryFeedMachineViaConveyorPath (furnaces via conveyor)
│       4. CrabPotHelper.TryLoadBaitIntoCrabPots (2-tile radius)
│       5. GrowthStageHelper.TryLoadItemsIntoGrowthStages (incubators, etc)
│       6. SiloHelper.TryLoadFeedIntoSilos (visual fill)
│
├── EjectItemOnCyclePatch [HarmonyPatch]
│   └── Prefix on ItemDepositAndChanger.ejectItemOnCycle()
│       - Intercepts machine output before item drops
│       - Priority 1: Direct adjacent chest (TryDepositToAdjacentChest)
│       - Priority 2: Via conveyor path (TryDepositViaConveyorPath)
│       - Both trigger QueueAutoSorterUpdate if depositing into Auto Sorter
│
├── NewDayHarvestPatch [HarmonyPatch]
│   └── Postfix on WorldManager.refreshAllChunksNewDay()
│       - 2s delay via coroutine (wait for chunk refresh)
│       - Calls HarvestHelper.ProcessHarvestableMachines()
│       - Calls FishPondHelper.ProcessPondAndTerrariumOutput()
│
├── HarvestHelper (static class) — line ~407
│   ├── ProcessHarvestableMachines() - Entry point on day change
│   ├── ScanAndHarvestAllChests() - Scan all chests + conveyor networks
│   ├── ScanConveyorPathForHarvestables() - BFS to find harvestable machines
│   ├── TryAutoHarvestAt() - Harvest a single machine
│   │   └── GUARD: if (growth.spawnsFarmAnimal) return; ← CRITICAL
│   ├── TryDepositHarvestToChest() - Deposit items into chest
│   │   └── Triggers QueueAutoSorterUpdate if target is Auto Sorter
│   ├── FindAdjacentChestForHarvest() - Find output chest nearby
│   └── FindChestViaConveyorPath() - Find chest via conveyor BFS
│       └── Checks ADJACENT tiles to conveyors for chests (not ON them)
│
├── ConveyorHelper (static class) — line ~810
│   ├── TryFeedAdjacentMachine() - Direct chest → machine
│   │   └── KeepOneItem exemption for Auto Sorters
│   ├── TryFeedMachineViaConveyorPath() - Chest → path → machine
│   │   └── KeepOneItem exemption for Auto Sorters
│   ├── TryDepositToAdjacentChest() - Machine → direct chest
│   │   └── Triggers QueueAutoSorterUpdate if target is Auto Sorter
│   ├── TryDepositViaConveyorPath() - Machine → path → chest
│   │   └── Triggers QueueAutoSorterUpdate if target is Auto Sorter
│   ├── IsConveyorTile() - Check if tile is conveyor path
│   ├── IsInputOnlyContainer() - Check for white crate/chest
│   ├── IsSpecialContainer() - Exclude fish ponds, terrariums, auto placers, mannequins, etc.
│   │   └── Auto Sorters REMOVED from this list (they participate in automation)
│   ├── FindChestAt() - Get Chest object at position
│   │   └── Filters out tileObjectItemChanger (gacha machines, etc.)
│   ├── IsAutoSorter() - Check if chest is Auto Sorter via Chest.IsAutoSorter()
│   ├── QueueAutoSorterUpdate() - Debounced trigger for Auto Sorter firing
│   ├── AutoSorterBatchSort() - Coroutine: 1s delay, then fire slots one at a time
│   ├── FindSlotForItem() - Find slot for stacking or empty slot
│   └── ChestHasItems() - Check if chest has any items
│
├── CrabPotHelper (static class) — line ~1440
│   ├── TryLoadBaitIntoCrabPots() - Entry point (outdoor only)
│   ├── TryLoadBaitInRadius() - 2-tile radius search (Manhattan distance)
│   ├── TryLoadBaitViaConveyorPath() - BFS conveyor + 2-tile radius
│   ├── TryLoadBaitAtPosition() - Check for crab pot, needs bait
│   └── TryLoadBaitFromChest() - Match itemsToPlace[], consume from chest
│       └── KeepOneItem exemption for Auto Sorters
│
├── GrowthStageHelper (static class) — line ~1642
│   ├── TryLoadItemsIntoGrowthStages() - Entry point
│   ├── TryLoadInRadius() - 1-tile radius (standard adjacency)
│   ├── TryLoadViaConveyorPath() - BFS conveyor + 1-tile radius
│   ├── TryLoadAtPosition() - Check growth stage, needs loading, SKIP crab pots
│   └── TryLoadFromChest() - Match itemsToPlace[], consume from chest
│       └── KeepOneItem exemption for Auto Sorters
│
├── SiloHelper (static class) — line ~1840
│   ├── TryLoadFeedIntoSilos() - Entry point (outdoor only)
│   ├── TryLoadFeedInRadius() - 1-tile radius
│   ├── TryLoadFeedViaConveyorPath() - BFS conveyor
│   ├── TryLoadFeedAtPosition() - Multi-tile root resolution, isSilo check
│   └── TryLoadFeedFromChest() - Hardcoded item 344 (Animal Food), configurable speed
│       └── KeepOneItem exemption for Auto Sorters
│
└── FishPondHelper (static class) — line ~2047
    ├── TryFeedPondsAndTerrariums() - Entry point (outdoor only, 3-tile radius)
    ├── TryFeedInRadius() - 3-tile Manhattan distance search
    ├── TryFeedViaConveyorPath() - BFS conveyor + 3-tile radius at each tile
    ├── TryFeedAtPosition() - Multi-tile root resolution, identify pond/terrarium
    ├── TryLoadFoodFromChest() - Load critters (pond) or honey (terrarium) into slot 22
    │   └── KeepOneItem exemption for Auto Sorters
    ├── ProcessPondAndTerrariumOutput() - Day change extraction entry point
    ├── ExtractOutputViaConveyorPath() - BFS conveyor for extraction targets
    └── TryExtractOutputAtPosition() - Extract from slot 23 with breeding hold logic
```

---

## How Systems Work

### Auto Input (Chest → Machine) — ItemDepositAndChanger machines
1. `Update()` runs at `ScanInterval` (default 0.3s) on server
2. Iterates all `ContainerManager.manage.activeChests`
3. For each chest with items:
   - `FishPondHelper.TryFeedPondsAndTerrariums(chest, inside)` - FIRST (see Rule 7)
   - `TryFeedAdjacentMachine(chest, inside)` - Check 4 adjacent tiles
   - If no adjacent machine, `TryFeedMachineViaConveyorPath(chest, inside)` - BFS along conveyor
   - One machine fed per chest per cycle (speed controlled by ScanInterval)
4. Validates: machine empty, item can be deposited, enough quantity
5. If `KeepOneItem` enabled (and source is NOT Auto Sorter), requires `amountNeeded + 1` items before taking
6. Calls `NetworkMapSharer.Instance.RpcDepositItemIntoChanger()` to deposit
7. Calls `NetworkMapSharer.Instance.startTileTimerOnServer()` to start processing
8. Updates chest slot via `ContainerManager.manage.changeSlotInChest()`

### Auto Output (Machine → Chest)
1. Harmony Prefix on `ItemDepositAndChanger.ejectItemOnCycle()`
2. Gets result item ID from `itemChange.getChangerResultId()`
3. Priority 1: `TryDepositToAdjacentChest()` - Direct adjacent (skips white crates + special containers)
4. Priority 2: `TryDepositViaConveyorPath()` - BFS along conveyor to find chest
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
   - `AutoSorterBatchSort`: 1s initial delay (accumulation window), then loops up to 24 cycles calling game's `AutoSortItemsIntoNearbyChests` with 0.5s pauses between each, exits when empty, removes key from set

### Fish Pond Automation
**Chest layout:** 24 slots total — slots 0-4 = creatures, slot 22 = food input, slot 23 = output (roe)

**Feeding (continuous, in ProcessAllChests):**
1. Runs BEFORE machine feeding (critters have `itemChange` — see Rule 7)
2. Scans 3-tile Manhattan distance radius around source chest
3. Also searches via conveyor BFS + 3-tile radius at each conveyor tile
4. For each found fish pond (multi-tile root resolved):
   - Gets pond chest via `getChestForWindow(rootX, rootY, null)`
   - Checks slot 22 — if empty, searches source chest for items with `underwaterCreature` set
   - Loads 1 critter into slot 22 via `changeSlotInChest()`
   - Removes 1 from source chest

**Extraction (day change, via ProcessPondAndTerrariumOutput):**
1. Copies `activeChests`, iterates each regular outdoor chest
2. Scans 3-tile radius + conveyor for adjacent fish ponds
3. For each pond with output in slot 23:
   - Counts creatures in slots 0-4
   - **5 creatures (full):** Extract ALL roe
   - **< 5 creatures AND HoldOutputForBreeding:** Only extract above 15 roe
   - **HoldOutputForBreeding disabled:** Extract ALL
4. Deposits via `TryDepositHarvestToChest()`, updates slot 23

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
6. Fills `SiloFillSpeed` items per tick (default 2, configurable 1-5)
7. Visual fill updates automatically via `ShowObjectOnStatusChange.toScale` scaling Y by status/200
8. Max capacity: 200 (stored in `onTileStatusMap`)

### Crab Pot Automation
1. **Bait Loading** (continuous, in `ProcessAllChests`):
   - 2-tile Manhattan distance radius (crab pots are in water, 1 tile from shore)
   - Uses `TileObjectGrowthStages.itemsToPlace[]` to match valid bait items dynamically
   - Checks `currentStatus < maxStageToReachByPlacing` to know if bait needed
   - Conveyor support: BFS along conveyors, checking 2-tile radius at each conveyor tile
2. **Harvest Output** (day change, via `NewDayHarvestPatch`):
   - Scans for crab pots at harvestable state
   - 2-tile radius search for output chest
   - `FindChestViaConveyorPath` checks ADJACENT tiles to conveyors for chests
   - Fish/critters deposited 1 per slot (non-stackable)

### Growth Stage Loading (Incubators, etc.)
1. Runs every tick in `ProcessAllChests()` after crab pot loading
2. Handles ANY `TileObjectGrowthStages` with `itemsToPlace[]` EXCEPT crab pots
3. Crab pots excluded because CrabPotHelper handles them with 2-tile radius
4. Uses standard 1-tile adjacency + conveyor BFS
5. Matches items via `growth.itemsToPlace[]` array (dynamic, not hardcoded)
6. Checks `currentStatus < maxStageToReachByPlacing` to know if loading needed
7. Works for: Egg Incubators (fertilized eggs), any future growth-stage objects

### Harvest Day Change (Bee Houses, Key Cutters, Worm Farms)
1. Harmony Postfix on day change event
2. Scans all chests and their conveyor networks for harvestable machines
3. **CRITICAL GUARD: `if (growth.spawnsFarmAnimal) return;`** — Never harvests incubators or animal spawners
4. Only harvests when output chest is available (prevents item loss)
5. Resets growth stage via `takeOrAddFromStateOnHarvest`

### Conveyor Path BFS Algorithm
1. Start from machine/chest position
2. Check adjacent tiles for conveyor tile type
3. Add matching tiles to queue, track visited in `HashSet<long>`
4. Limit to 50-100 steps to prevent runaway
5. At each conveyor tile, check adjacent positions for targets
6. For crab pots/fish ponds: extended radius at each conveyor tile
7. For chests in `FindChestViaConveyorPath`: check ADJACENT to conveyor tiles (not ON them)

### Input-Only Logic
- `IsInputOnlyContainer()` checks TileObject ID against WHITE_CRATE_TILE_ID (417) and WHITE_CHEST_TILE_ID (430)
- Called in output functions to skip white containers
- White containers CAN still feed machines (not blocked in input functions)

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
| `SiloFillSpeed` | Automation | 2 | 1-5 | Animal Food items loaded per tick |
| `AutoFeedPondsAndTerrariums` | Fish Pond & Terrarium | true | true/false | Auto-feed ponds/terrariums + extract on day change |
| `HoldOutputForBreeding` | Fish Pond & Terrarium | true | true/false | Hold 15 roe / 10 cocoons when < 5 creatures |

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
Crab pots are placed in water, 1 tile from shore. Chests and conveyors are on land. Need 2-tile Manhattan distance for detection.

### 5. Conveyor Chest Detection: Adjacent, Not On
`FindChestViaConveyorPath` must check tiles ADJACENT to conveyor tiles for chests, not ON them. Chests sit next to conveyors, not on top of them.

### 6. Copy Active Chests Before Iterating
When iterating `ContainerManager.manage.activeChests`, operations that modify chests can trigger collection modification exceptions. Copy the list first if needed (day-change processing uses `new List<Chest>(activeChests)`).

### 7. FishPondHelper MUST Run Before Machine Feeding
In `ProcessAllChests()`, fish pond feeding MUST happen before `TryFeedAdjacentMachine()`. Critters (underwater creatures) have `itemChange` set on them, which means the machine-feeding code would try to deposit them into furnaces/grinders. By feeding ponds first, critters get consumed for their intended purpose.

### 8. Auto Sorters Are Exempt from KeepOneItem
All 6 KeepOneItem check locations use `!IsAutoSorter(chest)` to exempt Auto Sorters. This is essential because Auto Sorters are intermediary — they need to fully empty to fire items to their final destinations. Regular chests with KeepOneItem keep 1 item per slot so the Auto Sorter always has a target to match against.

### 9. Filter tileObjectItemChanger in FindChestAt
`FindChestAt()` skips any TileObject that has `tileObjectItemChanger != null`. This prevents ghost chest creation from machines like gacha machines that have `tileObjectChest` but are not legitimate storage containers. See Learnings section for details.

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
cp code/bin/Release/net472/topmass.autom8er.dll ~/.steam/steam/steamapps/common/Dinkum/BepInEx/plugins/
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
3. Multi-tile: conveyor can touch any of 4 silo tiles
4. ItemSign is NOT on prefab — item 344 is hardcoded

### Fish Pond Not Feeding
1. Chest must be within 3 tiles (Manhattan distance) or connected via conveyor
2. Slot 22 must be empty for food to load
3. Source chest must contain items with `underwaterCreature` set (critters)
4. Ponds are outdoor only — `inside` must be null
5. Fish ponds are multi-tile (5x5) — ensure root tile resolution is working

### Auto Sorter Not Firing
1. Items only fire to chests that already have ≥1 of the same item (game's vanilla matching logic)
2. Target chests must be within 10 tiles
3. Check that debounce coroutine is running (1s initial delay before first fire)
4. Auto Sorter fires ONE slot per cycle with 0.5s pauses between — large inventories take time

### Incubator Not Loading
1. Uses `TileObjectGrowthStages.itemsToPlace[]` — same system as crab pots
2. Handled by `GrowthStageHelper` (1-tile radius, standard adjacency)
3. Crab pots excluded from this (handled separately with 2-tile radius)
4. Check that incubator is empty (status < maxStageToReachByPlacing)

### Animals Not Hatching (Incubator Bug)
If animals aren't spawning from incubators near conveyors, check that `spawnsFarmAnimal` guard exists in `TryAutoHarvestAt()`. Without it, the harvest system consumes eggs.

---

## Version History
- **1.4.0** - Fish pond automation (feed critters + extract roe), bug terrarium automation (feed honey + extract cocoons), smart breeding hold (configurable), Auto Sorter as I/O chest (KeepOneItem exempt, auto-trigger on deposit, debounce batch sort), gacha machine ghost chest fix (tileObjectItemChanger filter in FindChestAt), Auto Placer support
- **1.3.1** - Fix incubator bug (spawnsFarmAnimal guard), add GrowthStageHelper for incubator loading via conveyors
- **1.3.0** - Silos (auto-fill, configurable speed), crab pots (bait + harvest via conveyor), bee houses/key cutters/worm farms (day change harvest), multi-tile object support, SiloFillSpeed config
- **1.2.0** - Added ScanInterval config (default 0.3s), KeepOneItem config for placeholder preservation
- **1.1.0** - Added configurable conveyor tile, path examples in config
- **1.0.0** - Initial release with auto I/O, white crate input-only, conveyor system

---

## Learnings — Game Mechanics & Gotchas

Things discovered during development that are important for future work on this mod.

### 1. `underwaterCreature` is a Reference Type (Unity Implicit Bool)
`InventoryItem.underwaterCreature` is NOT a boolean — it's a reference to a Unity object. In C#/Unity, checking `if (item.underwaterCreature)` works because Unity overrides the implicit bool operator for UnityEngine.Object. If the reference is null/destroyed, it's falsy; if it exists, it's truthy. Do NOT compare it to `true`/`false` directly — use the implicit bool pattern.

### 2. Critters Have `itemChange` Set
Underwater creatures (critters used as fish pond food) have `itemChange` on them. This means the generic machine-feeding code (`TryFeedAdjacentMachine`) will try to deposit critters into furnaces, grinders, etc. This is why `FishPondHelper.TryFeedPondsAndTerrariums()` MUST run before machine feeding in `ProcessAllChests()`. If you move it after, critters will be consumed by machines instead of ponds.

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
The `NewDayHarvestPatch` hooks into `refreshAllChunksNewDay()`, but chunk data isn't immediately available. A 2-second `WaitForSeconds` delay in the `DelayedHarvestProcessing` coroutine ensures tile data is loaded before scanning for harvestable machines and extractable pond/terrarium outputs.

### 12. Dev Commands Can Cause Unexpected State
Using dev commands to force day changes (skip to next day) can cause machines like gacha machines to not process properly — they may retain stale chest data. The `tileObjectItemChanger` filter in `FindChestAt()` protects against this, but be aware during testing that dev commands can create edge cases that won't appear in normal gameplay.
