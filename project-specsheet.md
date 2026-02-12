# Autom8er Project Specsheet

## Overview
**Version:** 1.3.1
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
Silos and other large objects span multiple tiles. Non-root tiles store negative values in `onTileMap`:
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

### Plugin.cs Layout
```
Autom8er namespace
├── Plugin : BaseUnityPlugin
│   ├── Config: ConveyorTileItemId, KeepOneItem, ScanInterval, SiloFillSpeed
│   ├── Constants: WHITE_CRATE_TILE_ID (417), WHITE_CHEST_TILE_ID (430)
│   ├── Awake() - Init config, apply Harmony patches
│   ├── Update() - Scan timer, process chests at ScanInterval
│   ├── CacheConveyorTileType() - Get placeableTileType from item data
│   └── ProcessAllChests() - Main loop:
│       1. TryFeedAdjacentMachine (furnaces, etc)
│       2. TryFeedMachineViaConveyorPath (furnaces via conveyor)
│       3. CrabPotHelper.TryLoadBaitIntoCrabPots (2-tile radius)
│       4. GrowthStageHelper.TryLoadItemsIntoGrowthStages (incubators, etc)
│       5. SiloHelper.TryLoadFeedIntoSilos (visual fill)
│
├── EjectItemOnCyclePatch [HarmonyPatch]
│   └── Prefix on ItemDepositAndChanger.ejectItemOnCycle()
│       - Intercepts machine output before item drops
│       - Priority 1: Direct adjacent chest
│       - Priority 2: Via conveyor path network
│
├── NewDayHarvestPatch [HarmonyPatch]
│   └── Postfix on WorldManager.manageObject() (day change)
│       - Scans for harvestable growth stage machines
│       - SKIPS objects with spawnsFarmAnimal=true (incubators!)
│       - Deposits harvest to nearby chests
│
├── ConveyorHelper (static class)
│   ├── TryFeedAdjacentMachine() - Direct chest → machine
│   ├── TryFeedMachineViaConveyorPath() - Chest → path → machine
│   ├── TryDepositToAdjacentChest() - Machine → direct chest
│   ├── TryDepositViaConveyorPath() - Machine → path → chest
│   ├── IsConveyorTile() - Check if tile is conveyor path
│   ├── IsInputOnlyContainer() - Check for white crate/chest
│   ├── IsSpecialContainer() - Exclude fish ponds, terrariums, etc.
│   ├── FindChestAt() - Get Chest object at position
│   ├── FindSlotForItem() - Find slot for stacking or empty slot
│   └── ChestHasItems() - Check if chest has any items
│
├── HarvestHelper (static class)
│   ├── ScanAndHarvestAllChests() - Entry point on day change
│   ├── ScanConveyorPathForHarvestables() - BFS to find harvestable machines
│   ├── TryAutoHarvestAt() - Harvest a single machine
│   │   └── GUARD: if (growth.spawnsFarmAnimal) return; ← CRITICAL
│   ├── FindAdjacentChestForHarvest() - Find output chest nearby
│   └── FindChestViaConveyorPath() - Find chest via conveyor BFS
│       └── Checks ADJACENT tiles to conveyors for chests (not ON them)
│
├── CrabPotHelper (static class)
│   ├── TryLoadBaitIntoCrabPots() - Entry point (outdoor only)
│   ├── TryLoadBaitInRadius() - 2-tile radius search (Manhattan distance)
│   ├── TryLoadBaitViaConveyorPath() - BFS conveyor + 2-tile radius
│   ├── TryLoadBaitAtPosition() - Check for crab pot, needs bait
│   └── TryLoadBaitFromChest() - Match itemsToPlace[], consume from chest
│
├── GrowthStageHelper (static class)
│   ├── TryLoadItemsIntoGrowthStages() - Entry point
│   ├── TryLoadInRadius() - 1-tile radius (standard adjacency)
│   ├── TryLoadViaConveyorPath() - BFS conveyor + 1-tile radius
│   ├── TryLoadAtPosition() - Check growth stage, needs loading, SKIP crab pots
│   └── TryLoadFromChest() - Match itemsToPlace[], consume from chest
│
└── SiloHelper (static class)
    ├── TryLoadFeedIntoSilos() - Entry point (outdoor only)
    ├── TryLoadFeedInRadius() - 1-tile radius
    ├── TryLoadFeedViaConveyorPath() - BFS conveyor
    ├── TryLoadFeedAtPosition() - Multi-tile root resolution, isSilo check
    └── TryLoadFeedFromChest() - Hardcoded item 344 (Animal Food), configurable speed
```

---

## How Systems Work

### Auto Input (Chest → Machine) — ItemDepositAndChanger machines
1. `Update()` runs at `ScanInterval` (default 0.3s) on server
2. Iterates all `ContainerManager.manage.activeChests`
3. For each chest with items:
   - `TryFeedAdjacentMachine(chest, inside)` - Check 4 adjacent tiles first
   - If no adjacent machine, `TryFeedMachineViaConveyorPath(chest, inside)` - BFS along conveyor
   - One machine fed per chest per cycle (speed controlled by ScanInterval)
4. Validates: machine empty, item can be deposited, enough quantity
5. If `KeepOneItem` enabled, requires `amountNeeded + 1` items before taking
6. Calls `NetworkMapSharer.Instance.RpcDepositItemIntoChanger()` to deposit
7. Calls `NetworkMapSharer.Instance.startTileTimerOnServer()` to start processing
8. Updates chest slot via `ContainerManager.manage.changeSlotInChest()`

### Auto Output (Machine → Chest)
1. Harmony Prefix on `ItemDepositAndChanger.ejectItemOnCycle()`
2. Gets result item ID from `itemChange.getChangerResultId()`
3. Priority 1: `TryDepositToAdjacentChest()` - Direct adjacent (skips white crates)
4. Priority 2: `TryDepositViaConveyorPath()` - BFS along conveyor to find chest
5. If deposited, returns `false` to skip original eject (no ground drop)
6. If no chest found, returns `true` to let item drop normally

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
6. For crab pots: 2-tile radius at each conveyor tile
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
| `KeepOneItem` | Automation | false | true/false | Reserve 1 item in slots as placeholder |
| `SiloFillSpeed` | Automation | 2 | 1-5 | Animal Food items loaded per tick |

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
Silos and other multi-tile objects store negative values in non-root tiles. Always resolve to root before reading/writing status:
```csharp
if (tileObjectId < -1)
{
    Vector2 rootPos = WorldManager.Instance.findMultiTileObjectPos(x, y);
    // Use rootPos for all status operations
}
```

### 4. Crab Pots Need 2-Tile Radius
Crab pots are placed in water, 1 tile from shore. Chests and conveyors are on land. Need 2-tile Manhattan distance for detection.

### 5. Conveyor Chest Detection: Adjacent, Not On
`FindChestViaConveyorPath` must check tiles ADJACENT to conveyor tiles for chests, not ON them. Chests sit next to conveyors, not on top of them.

### 6. Copy Active Chests Before Iterating
When iterating `ContainerManager.manage.activeChests`, operations that modify chests can trigger collection modification exceptions. Copy the list first if needed.

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
cd code
dotnet build
```

### Output
```
code/bin/Debug/net472/topmass.autom8er.dll
```

### Install Locations
```bash
# Mod repository
cp code/bin/Debug/net472/topmass.autom8er.dll mod/

# Game plugins
cp code/bin/Debug/net472/topmass.autom8er.dll ~/Steam/steamapps/common/Dinkum/BepInEx/plugins/
```

---

## Special Containers (Excluded)
These are checked in `IsSpecialContainer()` and won't receive/send items:
- Fish Ponds (`isFishPond`)
- Bug Terrariums (`isBugTerrarium`)
- Auto Sorters (`isAutoSorter`)
- Auto Placers (`isAutoPlacer`)
- Mannequins (`isMannequin`)
- Tool Racks (`isToolRack`)
- Display Stands (`isDisplayStand`)

---

## Game API Reference

### Key Classes
- `ContainerManager.manage` - Chest management singleton
- `WorldManager.Instance` - World tile data singleton
- `Inventory.Instance` - Item database singleton
- `NetworkMapSharer.Instance` - Network RPC calls
- `HouseManager.manage` - Interior/house management

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

// Get chest at position
ContainerManager.manage.getChestForWindow(x, y, inside);

// Find multi-tile root
WorldManager.Instance.findMultiTileObjectPos(x, y);

// Check if item can go in machine
item.itemChange.checkIfCanBeDepositedServer(tileObjectId);
item.itemChange.getAmountNeeded(tileObjectId);
item.itemChange.getChangerResultId(tileObjectId);
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

### Incubator Not Loading
1. Uses `TileObjectGrowthStages.itemsToPlace[]` — same system as crab pots
2. Handled by `GrowthStageHelper` (1-tile radius, standard adjacency)
3. Crab pots excluded from this (handled separately with 2-tile radius)
4. Check that incubator is empty (status < maxStageToReachByPlacing)

### Animals Not Hatching (Incubator Bug)
If animals aren't spawning from incubators near conveyors, check that `spawnsFarmAnimal` guard exists in `TryAutoHarvestAt()`. Without it, the harvest system consumes eggs.

---

## Version History
- **1.3.1** - Fix incubator bug (spawnsFarmAnimal guard), add GrowthStageHelper for incubator loading via conveyors
- **1.3.0** - Silos (auto-fill, configurable speed), crab pots (bait + harvest via conveyor), bee houses/key cutters/worm farms (day change harvest), multi-tile object support, SiloFillSpeed config
- **1.2.0** - Added ScanInterval config (default 0.3s), KeepOneItem config for placeholder preservation
- **1.1.0** - Added configurable conveyor tile, path examples in config
- **1.0.0** - Initial release with auto I/O, white crate input-only, conveyor system
