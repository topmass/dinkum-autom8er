# Autom8er

A BepInEx mod for Dinkum that fully automates machines with adjacent chests.

## Quick Install

Just want the mod? Copy `mod/Autom8er.dll` to your `BepInEx/plugins/` folder.

## What It Does

Place any chest or crate next to a machine (furnace, grinder, BBQ, etc.):

1. **Auto-Output:** When the machine finishes, the output goes into the chest (not dropped on ground)
2. **Auto-Input:** If the chest has valid materials, the machine automatically reloads and starts again

This creates a fully automated processing loop! Fill a chest with 50 ore next to a furnace = 10 iron bars, hands-free.

## Folder Structure

```
Autom8er/
├── README.md          # This file
├── mod/
│   ├── Autom8er.dll   # Pre-built mod - just install this
│   └── README.md      # Installation instructions
└── codebase/
    ├── Plugin.cs      # Source code
    ├── Autom8er.csproj
    ├── .gitignore
    └── Libs/
        └── README.md  # Instructions for adding build dependencies
```

## Building From Source

See `codebase/Libs/README.md` for required DLLs, then:

```bash
cd codebase
dotnet build -c Release
```

Output: `codebase/bin/Release/net472/Autom8er.dll`

## Requirements

- Dinkum (Steam)
- BepInEx 6.0.0-pre.1

## License

MIT - Feel free to use, modify, and distribute.
