# Autom8er Project Specsheet

## Overview
**Version:** 1.2.0
**Plugin ID:** `topmass.autom8er`
**DLL Name:** `topmass.autom8er.dll`
**Target Framework:** net472
**Game:** Dinkum (Steam)
**Dependencies:** BepInEx 6.0.0-pre.1

## Features Summary
1. **Auto Input/Output** - Any chest next to a machine automatically feeds inputs and receives outputs
2. **White Crates = INPUT ONLY** - White crates/chests can feed machines but won't receive outputs
3. **Conveyor Belt System** - Black Marble Path (configurable) routes items between distant chests and machines

---

## Key IDs

### TileObject IDs (for `WorldManager.Instance.onTileMap`)
| ID | Item | Purpose |
|----|------|---------|
| 417 | White Wooden Crate | INPUT ONLY container |
| 430 | White Wooden Chest | INPUT ONLY container |

### Item IDs (for `Inventory.Instance.allItems`)
| ID | Item | Default Use |
|----|------|-------------|
| 1747 | Black Marble Path | Default conveyor tile |
| 964 | Cobblestone Path | Alt conveyor option |
| 346 | Rock Path | Alt conveyor option |
| 775 | Iron Path | Alt conveyor option |
| 15 | Brick Path | Alt conveyor option |

### How Tile Types Work
- **`onTileMap[x,y]`** - Stores TileObject ID (chests, machines, objects)
- **`tileTypeMap[x,y]`** - Stores tile type (ground, paths, floors)
- **`onTileStatusMap[x,y]`** - Stores machine status (-2/-1 = empty, >=0 = processing item ID)
- Path items have `placeableTileType` property that maps to `tileTypeMap` values

---

## Code Structure

### Plugin.cs Layout
```
Autom8er namespace
├── Plugin : BaseUnityPlugin
│   ├── Config: configConveyorTileItemId, configKeepOneItem, configScanInterval
│   ├── Constants: WHITE_CRATE_TILE_ID, WHITE_CHEST_TILE_ID, DEFAULT_CONVEYOR_TILE_ITEM_ID
│   ├── Runtime: ConveyorTileType, ConveyorTileItemId, KeepOneItem, ScanInterval
│   ├── Awake() - Init config, apply Harmony patches
│   ├── Update() - Scan timer, process chests at ScanInterval (default 0.3s)
│   ├── CacheConveyorTileType() - Get placeableTileType from item data
│   └── ProcessAllChests() - Main loop, one machine per chest per cycle
│
├── EjectItemOnCyclePatch [HarmonyPatch]
│   └── Prefix on ItemDepositAndChanger.ejectItemOnCycle()
│       - Intercepts machine output before item drops
│       - Priority 1: Direct adjacent chest
│       - Priority 2: Via conveyor path network
│
└── ConveyorHelper (static class)
    ├── TryFeedAdjacentMachine() - Direct chest → machine (returns bool)
    ├── TryFeedMachineViaConveyorPath() - Chest → path → machine (returns bool)
    ├── TryDepositToAdjacentChest() - Machine → direct chest
    ├── TryDepositViaConveyorPath() - Machine → path → chest
    ├── IsConveyorTile() - Check if tile is conveyor path
    ├── IsInputOnlyContainer() - Check for white crate/chest
    ├── IsSpecialContainer() - Exclude fish ponds, terrariums, etc.
    ├── FindChestAt() - Get Chest object at position
    ├── FindSlotForItem() - Find slot for stacking or empty slot
    ├── GetTileObjectId() - Get onTileMap value
    ├── IsMachineEmpty() - Check onTileStatusMap for -1/-2
    └── ChestHasItems() - Check if chest has any items
```

---

## How Systems Work

### Auto Input (Chest → Machine)
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

### Conveyor Path BFS Algorithm
1. Start from machine/chest position
2. Check 4 adjacent tiles for conveyor tile type
3. Add matching tiles to `HashSet<Vector2Int> pathNetwork`
4. Use `Queue<Vector2Int>` for BFS exploration
5. Limit to 500 tiles to prevent infinite loops
6. After BFS, scan all path tiles for adjacent chests/machines
7. Skip: path tiles, source position, input-only containers, special containers

### Input-Only Logic
- `IsInputOnlyContainer()` checks TileObject ID against WHITE_CRATE_TILE_ID (417) and WHITE_CHEST_TILE_ID (430)
- Called in `TryDepositToAdjacentChest()` and `TryDepositViaConveyorPath()` to skip white containers
- White containers CAN still feed machines (not blocked in input functions)

---

## Configuration

### BepInEx Config System
```csharp
configConveyorTileItemId = Config.Bind(
    "Conveyor",                           // Section
    "ConveyorTileItemId",                 // Key
    DEFAULT_CONVEYOR_TILE_ITEM_ID,        // Default (1747)
    "Description with examples..."        // Description
);
```

### Config File Location
```
BepInEx/config/topmass.autom8er.cfg
```

### Runtime Tile Type Caching
```csharp
InventoryItem pathItem = Inventory.Instance.allItems[ConveyorTileItemId];
ConveyorTileType = pathItem.placeableTileType;
// Then compare: WorldManager.Instance.tileTypeMap[x, y] == ConveyorTileType
```

---

## Build & Deploy

### Required DLLs in `codebase/Libs/`
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
cd codebase
dotnet build -c Release
```

### Output
```
codebase/bin/Release/net472/topmass.autom8er.dll
```

### Install Locations
```bash
# Mod repository
cp bin/Release/net472/topmass.autom8er.dll ../mod/

# Game plugins
cp bin/Release/net472/topmass.autom8er.dll ~/.steam/steam/steamapps/common/Dinkum/BepInEx/plugins/
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

// Update chest slot
ContainerManager.manage.changeSlotInChest(x, y, slot, itemId, stack, inside);

// Get chest at position
ContainerManager.manage.getChestForWindow(x, y, inside);
ContainerManager.manage.getChestForRecycling(x, y, inside);

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
3. Conveyor only works outdoors (not inside buildings currently)

### Items Not Depositing
1. Check if destination is a special container (excluded)
2. Check if destination is white crate (input-only, no outputs)
3. Check if chest has space (FindSlotForItem returns -1 if full)

### Machine Not Auto-Loading
1. Check if machine is empty (status -1 or -2)
2. Check if item can be deposited in that machine type
3. Check if chest has enough quantity (e.g., 5 ore for furnace)

---

## Version History
- **1.2.0** - Added ScanInterval config (default 0.3s), KeepOneItem config for placeholder preservation
- **1.1.0** - Added configurable conveyor tile, path examples in config
- **1.0.0** - Initial release with auto I/O, white crate input-only, conveyor system
