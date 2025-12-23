# Autom8er v1.2.0

Automation and conveyor belts for Dinkum machines.

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

### White Crates/Chests = INPUT ONLY
White Wooden Crate and White Wooden Chest are special:
- CAN feed materials into machines
- WON'T receive outputs from machines
- Perfect for dedicated input stations

### Black Marble Path = Conveyor Belt
Connect machines to distant chests using Black Marble Path tiles:
- Machines touching the path can send outputs to any chest on the network
- Chests touching the path can feed materials to any machine on the network
- Build complex factory layouts with routed conveyor systems

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
- And more!

## Supported Containers
All standard storage containers:
- Wooden Crate, Wooden Chest
- Iron Chest
- Painted crates/chests (all colors)
- And more!

**Excluded (special containers):**
- Fish Ponds
- Bug Terrariums
- Auto Sorters
- Auto Placers
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
[Conveyor]
## Item ID of the floor/path tile to use as conveyor belt.
## Examples: Black Marble Path (1747), Cobblestone Path (964), Rock Path (346), Iron Path (775), Brick Path (15)
ConveyorTileItemId = 1747

[Performance]
## Maximum number of machines to load per cycle (every 0.5 seconds).
## Default: 1 (original behavior, machines load one at a time - good for most setups)
## Increase to 2-5 if you have large arrays with multiple machine types and want them to load simultaneously.
## Higher values = more parallel loading but slightly more CPU/network usage per cycle.
MaxMachinesPerCycle = 1
```

**Common path Item IDs:**
| Item ID | Path Name |
|---------|-----------|
| 15 | Brick Path |
| 346 | Rock Path |
| 775 | Iron Path |
| 964 | Cobblestone Path |
| 1747 | Black Marble Path (default) |

**MaxMachinesPerCycle:**
- Default `1` = Original behavior, one machine loads at a time (good for most setups)
- Set to `2-5` for large arrays with multiple machine types (furnaces + BBQs + grinders)
- When set higher, prioritizes type diversity (feeds one furnace, one BBQ, one grinder per cycle instead of three furnaces)

## Requirements

- BepInEx 6.0.0-pre.1 installed in Dinkum
