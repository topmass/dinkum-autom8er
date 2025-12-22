# Required DLLs

Copy these files to this folder before building:

## From `Dinkum/BepInEx/core/`:
- `BepInEx.Core.dll`
- `BepInEx.Unity.dll`
- `0Harmony.dll`

## From `Dinkum/Dinkum_Data/Managed/`:
- `Assembly-CSharp.dll`
- `Assembly-CSharp-firstpass.dll`
- `Mirror.dll`

## Finding Dinkum Folder

**Linux (Steam):**
```
~/.steam/steam/steamapps/common/Dinkum/
```
or
```
~/Steam/steamapps/common/Dinkum/
```

**Windows:**
```
C:\Program Files (x86)\Steam\steamapps\common\Dinkum\
```

## Copy Commands (Linux)

```bash
DINKUM=~/.steam/steam/steamapps/common/Dinkum
cp $DINKUM/BepInEx/core/BepInEx.Core.dll .
cp $DINKUM/BepInEx/core/BepInEx.Unity.dll .
cp $DINKUM/BepInEx/core/0Harmony.dll .
cp $DINKUM/Dinkum_Data/Managed/Assembly-CSharp.dll .
cp $DINKUM/Dinkum_Data/Managed/Assembly-CSharp-firstpass.dll .
cp $DINKUM/Dinkum_Data/Managed/Mirror.dll .
```

These files are not included in the repository due to copyright.
