"""Run with: uv run --no-project python Autom8er/tests/ConveyorHelper.check.py

Compile the actual fallback method, range lookup, and belt machine feed with small game substitutes.
Requires the sibling RangeReview source and .NET 10 SDK, but no game process.
"""

from pathlib import Path
import re
import subprocess
import tempfile


root = Path(__file__).resolve().parents[2]
conveyor = (root / "Autom8er/code/ConveyorHelper.cs").read_text()
start = conveyor.index("        public static void DeliverOrDrop(")
end = conveyor.index("        public static bool TryFindFilterDestinationFromCrate", start)
delivery_method = conveyor[start:end]
range_source = (root / "RangeReview/code/Plugin.cs").read_text()
range_lookup = re.search(r"int farmRange = .*?;", range_source, re.S).group()
assert "tileObjectId, farmRange)" in range_source

source = r'''
using System;
struct Vector3 {
    public float x, y, z;
    public Vector3(float x, float y, float z) { this.x=x; this.y=y; this.z=z; }
    public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x+b.x,a.y+b.y,a.z+b.z);
}
class HouseDetails { public int xPos=10, yPos=20; }
class Transform { public Vector3 position; }
class Display {
    public Transform origin;
    public Transform getStartingPosTransform() => origin;
}
class HouseManager {
    public static HouseManager manage = new HouseManager();
    public Display findHousesOnDisplay(int x, int y) => new Display {
        origin = new Transform { position = new Vector3(x*100,100,y*100) }
    };
}
class WorldManager {
    public static WorldManager Instance = new WorldManager();
    public int[,] heightMap = new int[30,30];
}
class NetworkMapSharer {
    public static NetworkMapSharer Instance = new NetworkMapSharer();
    public int drops, item, amount; public Vector3 pos; public HouseDetails house;
    public void spawnAServerDropToSave(int id, int stack, Vector3 at, HouseDetails inside) {
        drops++; item=id; amount=stack; pos=at; house=inside;
    }
}
class Plugin { public static void LogWarningThrottled(string key,string message,float delay) {} }
class Program {
    const int GREEN_CRATE_RANGE_TILES=10;
    static bool storageAvailable;
    static bool FallbackDepositToAnyChest(int x,int y,HouseDetails h,int id,int amount,Action credit) => storageAvailable;
    static bool FallbackDepositToAnyChestLegacy(int x,int y,HouseDetails h,int id,int amount,Action credit) => storageAvailable;
    static void Check(bool ok) { if (!ok) throw new Exception("Regression check failed"); }
    static int GuideRange() { RANGE_LOOKUP return farmRange; }
    DELIVERY_METHOD
    static void Main() {
        RANGE_CHECKS
        var network=NetworkMapSharer.Instance;
        WorldManager.Instance.heightMap[3,4]=7;
        DeliverOrDrop(3,4,null,344,2);
        Check(network.drops==1 && network.pos.x==6 && network.pos.y==7 && network.pos.z==8);
        foreach (var house in new[]{new HouseDetails(),new HouseDetails{xPos=30,yPos=40}}) {
            DeliverOrDrop(3,4,house,344,2);
            Check(network.pos.x==house.xPos*100+6 && network.pos.y==100 && network.pos.z==house.yPos*100+8);
            Check(network.house==house && network.item==344 && network.amount==2);
        }
        storageAvailable=true;
        DeliverOrDrop(3,4,new HouseDetails(),344,2);
        Check(network.drops==3);
        Console.WriteLine("PASS: guide radius, outdoor drop, two indoor houses, storage success");
    }
}
'''.replace("RANGE_LOOKUP", range_lookup).replace("DELIVERY_METHOD", delivery_method)

with tempfile.TemporaryDirectory(prefix="autom8er-fallback-check-") as temp:
    for installed in (False, True):
        directory = Path(temp) / str(installed)
        directory.mkdir()
        assembly = "topmass.autom8er" if installed else "ReviewWithoutAutom8er"
        fixture = "namespace Autom8er { public class Plugin { public static int AUTO_FARMER_RANGE_TILES; } }" if installed else ""
        checks = "foreach(int radius in new[]{1,5,10}) { Autom8er.Plugin.AUTO_FARMER_RANGE_TILES=radius; Check(GuideRange()==radius); }" if installed else "Check(GuideRange()==10);"
        (directory / "Program.cs").write_text(source.replace("RANGE_CHECKS", checks) + fixture)
        project = directory / "Check.csproj"
        project.write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><AssemblyName>{assembly}</AssemblyName></PropertyGroup></Project>')
        subprocess.run(["dotnet", "run", "--project", str(project), "-c", "Release"], check=True)

start = conveyor.index("        private static readonly HashSet<Vector2Int> scratchFeedNetwork")
end = conveyor.index("        public static bool TryDepositToAdjacentChest(", start)
feed_method = conveyor[start:end]

feed_source = r'''
using System;
using System.Collections.Generic;
record struct Vector2Int(int x, int y);
class HouseDetails { public int xPos, yPos; }
class Time { public static int frameCount; }
class ItemChange {
    public int machine;
    public bool checkIfCanBeDepositedServer(int id) => id == machine;
    public int getAmountNeeded(int id) => 1;
}
class InventoryItem {
    public ItemChange itemChange; public bool hasFuel; public int fuelMax; public string itemName = "item";
    public bool checkIfStackable() => true;
}
class Inventory {
    public static Inventory Instance = new Inventory();
    public InventoryItem[] allItems = {
        new InventoryItem(),
        new InventoryItem { itemChange = new ItemChange { machine = 2 } },
        new InventoryItem { itemChange = new ItemChange { machine = 3 } } };
}
class TileObject { public object tileObjectItemChanger; }
class WorldManager {
    public static WorldManager Instance = new WorldManager();
    public TileObject[] allObjects = { new TileObject(), new TileObject(),
        new TileObject { tileObjectItemChanger = new object() }, new TileObject { tileObjectItemChanger = new object() } };
}
class Chest { public int xPos, yPos; public int[] itemIds = { -1 }; public int[] itemStacks = { 0 }; }
class ContainerManager {
    public static ContainerManager manage = new ContainerManager();
    public void changeSlotInChest(int x, int y, int slot, int id, int stack, HouseDetails inside) {}
}
class NetworkMapSharer {
    public static NetworkMapSharer Instance = new NetworkMapSharer();
    public void RpcDepositItemIntoChanger(int id, int x, int y) { Program.status[x, y] = id; }
    public void RpcDepositItemIntoChangerInside(int id, int x, int y, int hx, int hy) {}
    public void startTileTimerOnServer(int id, int x, int y, HouseDetails inside) {}
}
class ConveyorAnimator {
    public static bool IsTargetReserved(int x, int y, HouseDetails inside) => false;
    public static void ReserveTarget(int x, int y, HouseDetails inside) {}
    public static void UnreserveTarget(int x, int y, HouseDetails inside) {}
    public static void StartAnimation(int id, int amount, List<Vector2Int> path, Action arrival, float delay, bool indoor) {}
}
class ConveyorPathfinder { public static List<Vector2Int> FindPath(Vector2Int a, Vector2Int b, HouseDetails inside) => null; }
class AutomationCreditHelper { public static void TryGrantMachineInputCredit(int id, int machine) {} }
class Plugin {
    public static int ConveyorTileType = 1;
    public static bool AnimationEnabled = false;
    public static bool KeepOneAppliesTo(Chest chest, HouseDetails inside) => false;
    public static void LogVerbose(string message) {}
}
class Program {
    // Row y=1, x=1..8 is belt. Chests sit at (0,1) and (9,1). Furnaces (tile 2) sit at (3,0) and (5,0).
    public static int[,] tiles = new int[10, 3], status = new int[10, 3];
    public static int beltReads;
    static readonly int[] dx = { -1, 0, 1, 0 };
    static readonly int[] dy = { 0, -1, 0, 1 };
    static bool IsConveyorTile(int x, int y, HouseDetails inside) { beltReads++; return y == 1 && x >= 1 && x <= 8; }
    static int GetTileObjectId(int x, int y, HouseDetails inside) => x < 10 && y < 3 ? tiles[x, y] : -1;
    static bool IsMachineEmpty(int x, int y, HouseDetails inside) => status[x, y] < 0;
    static bool ShouldBlockBlastFurnaceRefill(int id, InventoryItem item, int x, int y, HouseDetails inside) => false;
    static void DeliverOrDrop(int x, int y, HouseDetails inside, int id, int amount) {}
    static void Check(bool ok, string what) { if (!ok) throw new Exception("Regression check failed: " + what); }
    static Chest Holding(int x, int itemId) => new Chest { xPos = x, yPos = 1, itemIds = new[] { itemId }, itemStacks = new[] { 5 } };
    FEED_METHOD
    static void Main() {
        for (int x = 0; x < 10; x++) for (int y = 0; y < 3; y++) { tiles[x, y] = -1; status[x, y] = 0; }
        tiles[0, 1] = tiles[9, 1] = 1; tiles[3, 0] = tiles[5, 0] = 2;
        Chest a = Holding(0, 1), b = Holding(9, 1);

        Time.frameCount = 1;
        Check(!TryFeedMachineViaConveyorPath(a, null), "busy network feeds nothing");
        beltReads = 0;
        Check(!TryFeedMachineViaConveyorPath(b, null) && beltReads <= 4, "same-frame chest skips a busy network");

        Time.frameCount = 2; status[3, 0] = -2;
        Check(TryFeedMachineViaConveyorPath(b, null) && status[3, 0] == 1, "next frame rescans and feeds a freed furnace");

        // A chest that cannot use the open furnace must not mark the network busy for others.
        Time.frameCount = 3; status[5, 0] = -2;
        Check(!TryFeedMachineViaConveyorPath(Holding(0, 2), null), "wrong item feeds nothing");
        Check(TryFeedMachineViaConveyorPath(b, null) && status[5, 0] == 1, "other contents still feed the open furnace");

        // A gacha-style source tile is skipped by its own walk, so it cannot vouch for others.
        Time.frameCount = 4; tiles[0, 1] = 2; status[0, 1] = -2;
        Check(!TryFeedMachineViaConveyorPath(a, null), "source skips its own tile");
        Check(TryFeedMachineViaConveyorPath(b, null) && status[0, 1] == 1, "other chest feeds the machine at the source tile");
        Console.WriteLine("PASS: busy-network skip is frame-scoped and contents-independent");
    }
}
'''.replace("FEED_METHOD", feed_method)

with tempfile.TemporaryDirectory(prefix="autom8er-feed-check-") as temp:
    (Path(temp) / "Program.cs").write_text(feed_source)
    project = Path(temp) / "Check.csproj"
    project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>disable</Nullable></PropertyGroup></Project>')
    subprocess.run(["dotnet", "run", "--project", str(project), "-c", "Release"], check=True)

# Black filter -> white input crate routing, plus the source-pull drain guard.
start = conveyor.index("        private static void TryGetBestDestinationAt(")
end = conveyor.index("        private static string BuildFilterDistributionKey(", start)
best_methods = conveyor[start:end]
start = conveyor.index("        private static bool TryGetFilterDestinationAt(")
end = conveyor.index("        private static void DepositIntoChest(", start)
filter_methods = conveyor[start:end]
start = conveyor.index("        public static int FindSlotForItem(")
end = conveyor.index("        public static int FindFirstEmptySlot(", start)
slot_method = conveyor[start:end]
filter_crate = (root / "Autom8er/code/FilterCrateHelper.cs").read_text()
start = filter_crate.index("        private static bool IsAnyFilterDestination(")
end = filter_crate.index("        private static int GetTransferAmount(", start)
pull_methods = filter_crate[start:end]

white_source = r'''
using System;
using System.Collections.Generic;
record struct Vector2Int(int x, int y);
class HouseDetails { public int xPos, yPos; }
class Mathf { public static int Abs(int v) => Math.Abs(v); }
class InventoryItem { public bool stackable; public bool checkIfStackable() => stackable; }
class Inventory {
    public static Inventory Instance = new Inventory();
    // 0 = log (stackable), 1 = plank (stackable), 2 = tool (not stackable)
    public InventoryItem[] allItems = { new InventoryItem { stackable = true }, new InventoryItem { stackable = true }, new InventoryItem() };
}
class Chest { public int xPos, yPos, insideX = -1, insideY = -1; public int[] itemIds = { -1, -1 }; public int[] itemStacks = { 0, 0 }; }
class Plugin { public static int ConveyorTileType = 1; }
static class ConveyorHelper {
    public const int WHITE = 417, BLACK = 410, GREEN = 412, NORMAL = 5;
    public static int[,] tiles = new int[12, 12];
    public static Dictionary<(int, int), Chest> chests = new();
    static readonly int[] dx = { -1, 0, 1, 0 };
    static readonly int[] dy = { 0, -1, 0, 1 };
    public class OutputDestination { public Chest chest; public int slotIndex, routeX, routeY; }
    public class DestinationSearchState {
        public readonly List<OutputDestination> filterDestinations = new();
        public readonly HashSet<long> seenFilters = new(), seenChests = new();
        public OutputDestination firstMatchingDestination, firstEmptyDestination;
    }
    public static int GetTileObjectId(int x, int y, HouseDetails h) => x >= 0 && y >= 0 && x < 12 && y < 12 ? tiles[x, y] : -1;
    public static bool IsInputOnlyContainer(int x, int y, HouseDetails h) => GetTileObjectId(x, y, h) == WHITE;
    public static bool IsOutputOnlyContainer(int x, int y, HouseDetails h) => false;
    public static bool IsFilterCrate(int x, int y, HouseDetails h) => GetTileObjectId(x, y, h) == BLACK;
    public static bool IsVacuumCrate(int x, int y, HouseDetails h) => GetTileObjectId(x, y, h) == GREEN;
    public static bool IsSpecialContainer(int x, int y, HouseDetails h) => GetTileObjectId(x, y, h) < 0;
    public static bool IsConveyorTile(int x, int y, HouseDetails h) => y == 3 && x >= 0 && x < 12;
    public static Chest FindChestAt(int x, int y, HouseDetails h) => GetTileObjectId(x, y, h) >= 0 && chests.TryGetValue((x, y), out var c) ? c : null;
    BEST_METHODS
    FILTER_METHODS
    SLOT_METHOD
    public static bool Find(int x, int y, int item, out OutputDestination d) => TryGetFilterDestinationAt(x, y, null, item, out d);
    public static DestinationSearchState Best(int x, int y, int item) { var s = new DestinationSearchState(); TryGetBestDestinationAt(x, y, null, item, false, s); return s; }
}
static class FilterCrateHelper {
    static readonly int[] dx = { -1, 0, 1, 0 };
    static readonly int[] dy = { 0, -1, 0, 1 };
    class SourcePull { public Chest sourceChest; public int sourceSlot, moveAmount; public bool advanceCursorOnly; }
    static int GetTransferAmount(Chest c, int slot, int item, HouseDetails h) => c.itemStacks[slot];
    static int GetPreferredMoveAmount(Chest c, int slot, int item, ConveyorHelper.OutputDestination d, List<ConveyorHelper.OutputDestination> all, int amount, out bool cursorOnly) { cursorOnly = false; return amount; }
    PULL_METHODS
    public static Chest Pull(Chest filter, int item, ConveyorHelper.OutputDestination d, List<ConveyorHelper.OutputDestination> all) => FindSourcePull(filter, null, item, d, all)?.sourceChest;
}
class Program {
    static void Check(bool ok, string what) { if (!ok) throw new Exception("Regression check failed: " + what); }
    static Chest Place(int x, int y, int tile, params int[] contents) {
        ConveyorHelper.tiles[x, y] = tile;
        var c = new Chest { xPos = x, yPos = y };
        for (int i = 0; i < contents.Length; i += 2) { c.itemIds[i / 2] = contents[i]; c.itemStacks[i / 2] = contents[i + 1]; }
        return ConveyorHelper.chests[(x, y)] = c;
    }
    static void Reset() {
        for (int x = 0; x < 12; x++) for (int y = 0; y < 12; y++) ConveyorHelper.tiles[x, y] = -1;
        ConveyorHelper.chests.Clear();
    }
    const int W = ConveyorHelper.WHITE, B = ConveyorHelper.BLACK, G = ConveyorHelper.GREEN, N = ConveyorHelper.NORMAL;
    static void Main() {
        ConveyorHelper.OutputDestination d;
        // Filter F at (5,6) holds a log sample. Neighbors: (4,6) (6,6) (5,5) (5,7).
        Reset(); Place(5, 6, B, 0, 1); Chest white = Place(6, 6, W);
        Check(ConveyorHelper.Find(5, 6, 0, out d) && d.chest == white && d.routeX == 5 && d.routeY == 6, "white-only filter routes into the white crate");
        Check(!ConveyorHelper.Find(5, 6, 1, out d), "filter without a matching sample routes nothing");

        Chest normal = Place(4, 6, N, 1, 9, 2, 1);
        Check(!ConveyorHelper.Find(5, 6, 0, out d), "full normal storage blocks diversion into white");
        normal.itemIds[1] = -1; normal.itemStacks[1] = 0;
        Check(ConveyorHelper.Find(5, 6, 0, out d) && d.chest == normal, "normal storage with room beats an empty white crate");
        white.itemIds[0] = 0; white.itemStacks[0] = 4;
        Check(ConveyorHelper.Find(5, 6, 0, out d) && d.chest == normal, "normal storage beats a white crate with a matching stack");
        ConveyorHelper.tiles[4, 6] = G;
        Check(ConveyorHelper.Find(5, 6, 0, out d) && d.chest == normal, "green storage counts as normal storage");
        ConveyorHelper.tiles[4, 6] = -1;
        Check(ConveyorHelper.Find(5, 6, 0, out d) && d.chest == white, "removing the normal chest re-enables the white route live");

        Chest white2 = Place(5, 5, W);
        white.itemIds[0] = -1; white.itemStacks[0] = 0;
        white2.itemIds[1] = 0; white2.itemStacks[1] = 3;
        Check(ConveyorHelper.Find(5, 6, 0, out d) && d.chest == white2 && d.slotIndex == 1, "matching white stack preferred over an empty white crate");
        white2.itemIds[0] = 1; white2.itemStacks[0] = 5;
        Check(ConveyorHelper.Find(5, 6, 2, out d) == false, "tool without a sample routes nothing");
        ConveyorHelper.chests[(5, 6)].itemIds[1] = 2; ConveyorHelper.chests[(5, 6)].itemStacks[1] = 1;
        Check(ConveyorHelper.Find(5, 6, 2, out d) && d.chest == white && d.slotIndex == 0, "non-stackable item takes an empty white slot");

        ConveyorHelper.tiles[5, 5] = -1;
        white.itemIds = new[] { 1, 1 }; white.itemStacks = new[] { 5, 5 };
        Check(!ConveyorHelper.Find(5, 6, 0, out d), "full white crate stops the route");
        white.itemIds = new[] { 0, 1 }; white.itemStacks = new[] { 5, 5 };
        Check(ConveyorHelper.Find(5, 6, 0, out d) && d.chest == white && d.slotIndex == 0, "white crate with a matching stack still takes logs");
        ConveyorHelper.tiles[6, 6] = -1;
        Check(!ConveyorHelper.Find(5, 6, 0, out d), "removed white crate is not a destination");
        ConveyorHelper.tiles[6, 6] = N;
        Check(ConveyorHelper.Find(5, 6, 0, out d) && d.chest == white, "white crate repainted to a normal chest is normal storage");
        ConveyorHelper.tiles[6, 6] = B;
        Check(!ConveyorHelper.Find(5, 6, 0, out d), "white crate repainted black is not a destination");

        // Direct (unfiltered) output never lands in a white crate.
        ConveyorHelper.tiles[6, 6] = W; white.itemIds = new[] { -1, -1 }; white.itemStacks = new[] { 0, 0 };
        var s = ConveyorHelper.Best(6, 6, 0);
        Check(s.firstMatchingDestination == null && s.firstEmptyDestination == null && s.filterDestinations.Count == 0, "direct output skips the white crate");
        s = ConveyorHelper.Best(5, 6, 0);
        Check(s.filterDestinations.Count == 1 && s.filterDestinations[0].chest == white, "filtered output reaches the white crate through its filter");

        // Belt row y=3. F1 (2,4) tool filter feeds white W (3,4), which also touches the belt.
        // F2 (7,4) tool filter stores into N2 (7,5). Only W holds tools. A stackable stack never
        // fills (FindSlotForItem has no cap), so only non-stackable items drop W from the list.
        Reset();
        Chest f1 = Place(2, 4, B, 2, 1); Chest w = Place(3, 4, W, 2, 1, 2, 1);
        Chest f2 = Place(7, 4, B, 2, 1); Chest n2 = Place(7, 5, N);
        var toN2 = new ConveyorHelper.OutputDestination { chest = n2, routeX = 7, routeY = 4 };
        Check(!ConveyorHelper.Find(2, 4, 2, out d), "full white crate is out of the filter list");
        Check(FilterCrateHelper.Pull(f2, 2, toN2, new() { toN2 }) == null, "other same-item filter cannot drain a full filter-fed white crate");
        Chest n1 = Place(2, 5, N, 1, 1, 1, 1);
        Check(FilterCrateHelper.Pull(f2, 2, toN2, new() { toN2 }) == w, "normal storage beside the supplying filter preserves the old white-source behavior even when full");
        ConveyorHelper.tiles[2, 5] = -1;
        Check(FilterCrateHelper.Pull(f2, 2, toN2, new() { toN2 }) == null, "removing normal storage immediately protects the eligible white input again");
        w.itemIds[1] = -1; w.itemStacks[1] = 0;
        Check(ConveyorHelper.Find(2, 4, 2, out d) && d.chest == w, "white crate with room is back in the filter list");
        Check(FilterCrateHelper.Pull(f2, 2, toN2, new() { d, toN2 }) == null, "other filter cannot drain a white crate with room");
        w.itemIds[1] = 2; w.itemStacks[1] = 1; f1.itemIds[0] = 1;
        Check(FilterCrateHelper.Pull(f2, 2, toN2, new() { toN2 }) == w, "white crate without a same-item filter keeps its old pull behavior");
        f1.itemIds[0] = 2;
        Chest source = Place(5, 4, N, 2, 1);
        Check(FilterCrateHelper.Pull(f2, 2, toN2, new() { toN2 }) == source, "ordinary sources still feed the other filter");
        Console.WriteLine("PASS: filter white-crate routing, normal-storage priority, live tile changes, drain guard");
    }
}
'''.replace("BEST_METHODS", best_methods).replace("FILTER_METHODS", filter_methods).replace("SLOT_METHOD", slot_method).replace("PULL_METHODS", pull_methods)

with tempfile.TemporaryDirectory(prefix="autom8er-white-filter-check-") as temp:
    (Path(temp) / "Program.cs").write_text(white_source)
    project = Path(temp) / "Check.csproj"
    project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>disable</Nullable></PropertyGroup></Project>')
    subprocess.run(["dotnet", "run", "--project", str(project), "-c", "Release"], check=True)
