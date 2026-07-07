# Autom8er

A BepInEx mod for Dinkum that fully automates machines, fish ponds, bug terrariums, crab pots, silos, quarries, and farming with chests, conveyor paths, and special crates.

## Quick Install

Copy `mod/topmass.autom8er.dll` to your `BepInEx/plugins/` folder.

## What It Does

Place any chest or crate next to a machine (furnace, grinder, BBQ, etc.):

1. **Auto-Output:** When the machine finishes, the output goes into the chest (not dropped on ground)
2. **Auto-Input:** If the chest has valid materials, the machine automatically reloads and starts again

### Conveyor Belts
Path tiles (Black Marble Path by default, configurable) act as conveyor belts connecting chests and machines across any distance. Items visually slide along the path. In multiplayer, guests who also run the mod see the belt animations too.

### Special Crates
- **White Crates** = input only: they feed machines but never receive outputs (white *chests* are normal storage)
- **Black Crates** = filter crates: hold sample items and route matching items into a touching storage chest
- **Green Crates** = AutoFarmer/vacuum crates: harvest nearby crops and fruit, till/fertilize/plant when holding tools, and forward everything into the connected network

### Fish Ponds & Bug Terrariums
Place a chest within 3 tiles of a fish pond or bug terrarium:
- **Fish Ponds:** Auto-loads critters from chest into food slot, extracts roe on day change
- **Bug Terrariums:** Auto-loads honey from chest, extracts cocoons on day change
- **Smart Breeding Hold:** Keeps roe/cocoons in the pond when creatures < 5 to allow breeding

### Crab Pots
Place a chest within 2 tiles of crab pots:
- Auto-loads bait from chest into crab pots
- Auto-harvests catches into adjacent chests on day change

### Silos
Place a chest next to a silo to auto-fill it with feed.

### Auto Sorters
Auto Sorters work as input/output chests and are **exempt from KeepOneItem** — items deposited by automation trigger the sorter to fire items to nearby matching chests automatically.

### Auto Placers
Auto Placers can feed automation but are excluded from receiving machine outputs (they are special containers, not storage).

## Folder Structure

```
Autom8er/
├── README.md              # This file
├── project-specsheet.md   # Living spec: features, code structure, rules
├── mod/
│   └── topmass.autom8er.dll   # Pre-built mod - just install this
└── code/
    ├── Plugin.cs      # Source code
    ├── Autom8er.csproj
    ├── .gitignore
    └── Libs/
        └── README.md  # Instructions for adding build dependencies
```

## Building From Source

See `code/Libs/README.md` for required DLLs, then:

```bash
cd code
dotnet build -c Release
```

Output: `code/bin/Release/net472/topmass.autom8er.dll`

## Requirements

- Dinkum (Steam)
- BepInEx 6.0.0-pre.1

## License

MIT - Feel free to use, modify, and distribute.
