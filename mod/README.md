# Autom8er v1.4.0

Automation and conveyor belts for Dinkum machines, fish ponds, bug terrariums, crab pots, silos, and more.

## Installation

Copy `topmass.autom8er.dll` to your Dinkum `BepInEx/plugins/` folder.

**Linux:**
```bash
cp topmass.autom8er.dll ~/.steam/steam/steamapps/common/Dinkum/BepInEx/plugins/
```

**Windows:**
```
Copy to: C:\Program Files (x86)\Steam\steamapps\common\Dinkum\BepInEx\plugins\
```

## Features

### Auto Input/Output (All Chests)
Place any chest or crate next to a machine:
- **Auto-Output:** Machine outputs go into adjacent chest (not dropped on ground)
- **Auto-Input:** If chest has valid materials, machine auto-reloads and starts again

### Fish Ponds
Place a chest within 3 tiles of a fish pond:
- Auto-loads critters (underwater creatures) from chest into food slot
- Extracts roe into adjacent chests on day change
- Smart breeding hold: keeps 15 roe when < 5 fish to allow breeding (configurable)

### Bug Terrariums
Place a chest within 3 tiles of a bug terrarium:
- Auto-loads honey from chest into food slot
- Extracts cocoons into adjacent chests on day change
- Smart breeding hold: keeps 10 cocoons when < 5 bugs to allow breeding (configurable)

### Crab Pots
Place a chest within 2 tiles of crab pots:
- Auto-loads bait from chest into crab pots
- Auto-harvests catches into adjacent chests on day change

### Silos
Place a chest next to a silo to auto-fill with feed.

### Auto Sorters
Auto Sorters work as automation input/output chests:
- Exempt from KeepOneItem (empties completely)
- Items deposited by automation trigger the sorter to fire to nearby matching chests
- Use as machine output to auto-distribute products to sorted storage

### Auto Placers
Auto Placers function as standard automation chests.

### White Crates/Chests = INPUT ONLY
White Wooden Crate and White Wooden Chest are special:
- CAN feed materials into machines
- WON'T receive outputs from machines

### Black Crates/Chests = OUTPUT ONLY
Black Wooden Crate and Black Wooden Chest are special:
- CAN receive outputs from machines
- WON'T feed materials into machines

### Black Marble Path = Conveyor Belt
Connect machines to distant chests using Black Marble Path tiles:
- Machines touching the path can send outputs to any chest on the network
- Chests touching the path can feed materials to any machine on the network

**Example Layout:**
```
[White Crate] --- Black Marble Path --- [Furnace] --- Black Marble Path --- [Output Chest]
   (input)                                               (output)
```

## Supported Machines
All machines with ItemChanger component:
- Furnaces (Smelting)
- Stone Grinder, Grain Mill (Grinding)
- BBQ, Camp Oven, Camp Fire (Cooking)
- Keg (Brewing)
- Table Saw (Sawing)
- Charging Station (Tools)
- And more!

## Supported Containers
All standard storage containers plus Auto Sorters and Auto Placers.

**Excluded (special containers):**
- Mannequins
- Tool Racks
- Display Stands

## Configuration

After first run, a config file is created at:
```
BepInEx/config/topmass.autom8er.cfg
```

**Settings:**
```ini
[Automation]
## Keep one item in chest slots to maintain placeholders.
## Auto Sorters are always exempt from this setting.
KeepOneItem = false

## Auto-feed fish ponds and bug terrariums from adjacent chests.
AutoFeedPondsAndTerrariums = true

## Hold roe/cocoons for breeding when creatures < 5.
HoldOutputForBreeding = true

## Silo fill speed per tick (1-10).
SiloFillSpeed = 1

[Conveyor]
## Item ID of the floor/path tile to use as conveyor belt.
ConveyorTileItemId = 1747

[Performance]
## How often to scan chests and feed machines (in seconds). Range: 0.1 to 1.0
ScanInterval = 0.3
```

**Common path Item IDs:**
| Item ID | Path Name |
|---------|-----------|
| 15 | Brick Path |
| 346 | Rock Path |
| 775 | Iron Path |
| 964 | Cobblestone Path |
| 1747 | Black Marble Path (default) |

## Requirements

- BepInEx 6.0.0-pre.1 installed in Dinkum
