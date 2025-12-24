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
[Automation]
## Keep one item in chest slots to maintain placeholders for easy stacking.
## When enabled, requires 1 extra item beyond what the machine needs before it will take from a slot.
## Example: Furnace needs 5 ore - will only take from stacks of 6+, leaving 1 behind.
KeepOneItem = false

[Conveyor]
## Item ID of the floor/path tile to use as conveyor belt.
## Examples: Black Marble Path (1747), Cobblestone Path (964), Rock Path (346), Iron Path (775), Brick Path (15)
ConveyorTileItemId = 1747

[Performance]
## How often to scan chests and feed machines (in seconds).
## Default: 0.3 (about 3 times per second)
## Lower = faster automation but more CPU usage. Range: 0.1 to 1.0
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

**KeepOneItem:**
- Default `false` = Takes all items needed, may empty slots
- Set to `true` = Always leaves 1 item behind in each slot
- Useful for keeping placeholder items so returning loot auto-stacks into existing slots

**ScanInterval:**
- Default `0.3` = Scans about 3 times per second (fast and responsive)
- Set to `0.2` for faster automation
- Set to `0.5` for more relaxed/lower CPU usage

## Requirements

- BepInEx 6.0.0-pre.1 installed in Dinkum
