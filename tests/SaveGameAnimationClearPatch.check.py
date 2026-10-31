"""Run with: uv run --no-project python Autom8er/tests/SaveGameAnimationClearPatch.check.py

Compile the real save-window code with small game substitutes and step it frame by frame
through a save that follows SaveAndLoad.SaveCoroutine: drops and machines on the first frame,
the map over later frames, then chests. Every item must exist at most once across the snapshot
and exactly once in the live world afterward. Copies with the save holds removed must fail.
Requires the .NET 10 SDK, but no game process.
"""

from pathlib import Path
import subprocess
import tempfile


code = Path(__file__).resolve().parents[1] / "code"


def between(name, start, end):
    text = (code / name).read_text()
    first = text.index(start)
    return text[first:text.index(end, first)]


plugin_is_saving = between("Plugin.cs", "        internal static bool IsSaving", "        internal static bool IsDurabilityTransferItem")
update_gate = between("Plugin.cs", "            bool saving = IsSaving;", "            scanTimer += Time.deltaTime;")
reset_for_save = between("RuntimeState.cs", "        public static void ResetForSave(", "        // On load, before")
save_prefix = between("SaveGameAnimationClearPatch.cs", "        static bool Prefix(", "    }\n}")
start_animation = between("ConveyorAnimator.cs", "        public static void StartAnimation(", "        public static void UpdateAnimations()")
clear_all = between("ConveyorAnimator.cs", "        // Set while ClearAllAnimations runs.", "        public static bool HasActiveAnimations()")
has_active = between("ConveyorAnimator.cs", "        public static bool HasActiveAnimations()", "        public static void AnimateTransfer(")
animate_transfer = between("ConveyorAnimator.cs", "        public static void AnimateTransfer(", "        private static GameObject CreateItemVisual(")
phase_fields = between("NewDayHarvestPatch.cs", "        private const float DAY_CHANGE_PHASE_DELAY_SECONDS", "        // Save/load safety net")
phase_runner = between("NewDayHarvestPatch.cs", "        public static System.Collections.IEnumerator RunPhasedHarvestProcessing", "    }\n}")
quarry_break = between("QuarryHelper.cs", "        private static System.Collections.IEnumerator BreakQuarryOutputsAfterDelay", "        private static void BreakQuarryOutputs(")
eject_prefix = between("EjectItemOnCyclePatch.cs", "        static bool Prefix(", "    }\n}")
harvest_start = between("HarvestPatch.cs", "        // During a save the chest snapshot is still to come", "    }\n}")

source = r'''
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace UnityEngine {
    class WaitForSeconds { public float seconds; public WaitForSeconds(float s) { seconds = s; } }
    struct Vector3 { public float x, y, z; public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; } }
    struct Vector2Int { public int x, y; public Vector2Int(int x, int y) { this.x = x; this.y = y; } }
    class Transform { public Vector3 position; }
    static class Mathf { public static float Max(float a, float b) => Math.Max(a, b); }
    class GameObject { public static int destroyed; public static void Destroy(GameObject g) { destroyed++; } }
    class MonoBehaviour {
        class Routine { public IEnumerator body; public float resumeAt; }
        static readonly List<Routine> routines = new List<Routine>();
        public static float now;
        public void StartCoroutine(IEnumerator body) { var r = new Routine { body = body }; if (Step(r)) routines.Add(r); }
        static bool Step(Routine r) {
            if (!r.body.MoveNext()) return false;
            var wait = r.body.Current as WaitForSeconds;
            r.resumeAt = wait != null ? now + wait.seconds : now;
            return true;
        }
        public static void Tick() {
            foreach (var r in routines.ToArray())
                if (now >= r.resumeAt && !Step(r)) routines.Remove(r);
        }
        public static bool Idle => routines.Count == 0;
    }
}

class SaveLoad { public static SaveLoad saveOrLoad = new SaveLoad(); public bool isSaving; }
class NetworkServer { public static bool active = true; }
class HouseDetails { public int[,] houseMapOnTileStatus, houseMapOnTile; }
class Chest { public int xPos, yPos; }
class NetworkMapSharer {
    public static NetworkMapSharer Instance = new NetworkMapSharer(); public bool isServer = true;
    // Native machine drop: not a saved drop once the save stored drops on its first frame.
    public void spawnAServerDropToSave(int itemId, int stack, Vector3 at, HouseDetails inside) { World.groundBars++; }
}
class WorldManager : MonoBehaviour { public static WorldManager Instance = new WorldManager(); public int[,] onTileStatusMap = new int[4, 4], onTileMap = new int[4, 4]; }
class ItemChange { public int getChangerResultId(int machineTileObjectId) => 2; public static implicit operator bool(ItemChange c) => c != null; }
class InventoryItem { public ItemChange itemChange; public bool hasFuel; public int fuelMax; public string itemName = ""; }
class Inventory { public static Inventory Instance = new Inventory(); public InventoryItem[] allItems = { new InventoryItem(), new InventoryItem { itemChange = new ItemChange() }, new InventoryItem() }; }
class ItemDepositAndChanger { public Transform ejectPos = new Transform(); }
class Logger { public void LogInfo(string m) {} }
class QuarryNodeHarvest {}
class ConveyorPathfinder { public static List<Vector2Int> FindPath(Vector2Int a, Vector2Int b, HouseDetails h) => new List<Vector2Int> { a, b }; }
class ConveyorNetSync { public static void BroadcastAnimation(int id, int amount, List<Vector2Int> path, float delay) {} }
class AutomationCreditHelper { public static void TryGrantOutputCredit(int tile, int id, int amount) {} }

static class World {
    // Live state. The map is written over frames, so each source tile sits in a chunk.
    public static bool hiveFull = true, quarryNode = true, berryRipe = true;
    public static int pondRoe = 1, ore, groundBars;
    public static Dictionary<string, int> chests = new Dictionary<string, int>();
    public static int Chest(string id) { int n; chests.TryGetValue(id, out n); return n; }
    public static void Deposit(string id) { chests[id] = Chest(id) + 1; }
}

static class Plugin {
    public static Logger Log = new Logger();
    public static bool surface = true, AnimationEnabled = true;
    public static int warnings;
    public static void LogWarningThrottled(string key, string message, float delay) { warnings++; }
    public static void LogVerbose(string m) {}
    public static bool CanRunServerAutomation() => NetworkServer.active && surface;
    IS_SAVING
    public static int scans, animationUpdates, vacuumVisualUpdates;
    public static void UpdateTick() {
UPDATE_GATE
        scans++;
    }
}

static class VacuumCrateHelper {
    public static int logSnapshots;
    public static void UpdateVisuals() { Plugin.vacuumVisualUpdates++; }
    public static void PrepareLoggingForSave() { logSnapshots++; }
    public static IEnumerator ProcessDayChangeVacuumHarvests() { yield break; }
}

static class ConveyorAnimator {
    public class ConveyorAnimation { public GameObject visual; public List<Vector2Int> path; public int currentSegment; public float segmentProgress; public Action onArrival; public int itemId; public float startDelay; public int framesLeft = 3; }
    public class ArcAnimation { public GameObject visual; public Vector3 startWorld, endWorld; public float duration, elapsed, arcHeight; public Action onArrival; public float startDelay; public int framesLeft = 3; }
    static List<ConveyorAnimation> activeAnimations = new List<ConveyorAnimation>();
    static List<ArcAnimation> activeArcAnimations = new List<ArcAnimation>();
    static HashSet<long> reservedTargets = new HashSet<long>();
    static GameObject CreateItemVisual(int itemId, Vector3 at, bool hidden) => new GameObject();
    static Vector3 TileToWorld(int x, int y) => new Vector3(x * 2, 0, y * 2);
    public static int InFlight => activeAnimations.Count + activeArcAnimations.Count;
    // Real start paths: a belt leg through AnimateTransfer, a vacuum or quarry pop through AnimateArcTransfer.
    public static void Belt(Action arrive) { AnimateTransfer(1, 1, new Vector2Int(0, 0), new Vector2Int(1, 0), null, arrive); }
    public static void Arc(Action arrive) { AnimateArcTransfer(1, 1, new Vector3(), new Vector3(2, 0, 0), 0.3f, 1f, arrive); }
    public static void UpdateAnimations() {
        Plugin.animationUpdates++;
        foreach (var a in activeAnimations.ToArray()) if (--a.framesLeft <= 0) { activeAnimations.Remove(a); a.onArrival(); }
        foreach (var a in activeArcAnimations.ToArray()) if (--a.framesLeft <= 0) { activeArcAnimations.Remove(a); a.onArrival(); }
    }
START_ANIMATION
CLEAR_ALL
HAS_ACTIVE
ANIMATE_TRANSFER
}

static class ConveyorHelper {
    public static int flushes;
    public static void ClearFilterDistributionState() { flushes++; }
    public static void ActivateAllOutdoorAutomationChests() {}
    // The furnace's output chest is adjacent, so a deposit lands at once.
    public static bool TryDepositToAdjacentChestLegacy(int x, int y, HouseDetails inside, int id, int stack, Action credit) { World.Deposit("bar"); return true; }
    public static bool TryDepositViaConveyorPathLegacy(int x, int y, HouseDetails inside, int id, int stack, Action credit) => false;
}
static class FilterCrateHelper { public static void ClearBulkDistributionState() {} }

static class RuntimeState {
RESET_FOR_SAVE
}
class SaveAndLoad : MonoBehaviour {
    public static int started;
    public void SaveGame(bool isServer, bool takePhoto = true, bool endOfDaySave = true) {
        if (SaveGameAnimationClearPatch.Call(this, isServer, takePhoto, endOfDaySave)) { started++; StartCoroutine(Program.SaveCoroutine()); }
    }
}
static class SaveGameAnimationClearPatch {
SAVE_PREFIX
    public static bool Call(SaveAndLoad saver, bool isServer, bool takePhoto, bool endOfDaySave) => Prefix(saver, isServer, takePhoto, endOfDaySave);
}
static class EjectItemOnCyclePatch {
EJECT_PREFIX
    public static bool Call(ItemDepositAndChanger changer, int x, int y) => Prefix(changer, x, y, null);
}
static class HarvestPatch {
HARVEST_START
    public static void Call(Action deposit) { StartHarvestTransfer(5, 2, 2, new Chest { xPos = 3, yPos = 2 }, null, deposit); }
}

static class HarvestHelper {
    public static float GetNextDayChangeAnimationDelay(Chest chest) => 0f;
    // An adjacent hive output has no belt path, so the deposit lands at once.
    public static IEnumerator ProcessHarvestableMachines() {
        if (World.hiveFull) { World.hiveFull = false; World.Deposit("honey"); }
        yield break;
    }
}
static class FishPondHelper {
    public static int ProcessPondAndTerrariumOutput() {
        if (World.pondRoe > 0) { World.pondRoe--; ConveyorAnimator.Belt(() => World.Deposit("roe")); }
        return 0;
    }
}
static class QuarryHelper {
    static int quarryRunGeneration = 1;
    public static int ProcessDayChangeQuarries() {
        WorldManager.Instance.StartCoroutine(BreakQuarryOutputsAfterDelay(1, 0, 0, new List<QuarryNodeHarvest>(), 1f));
        return 1;
    }
    static void BreakQuarryOutputs(int gen, int x, int y, List<QuarryNodeHarvest> nodes) {
        if (!World.quarryNode) return;
        World.quarryNode = false;
        ConveyorAnimator.Arc(() => ConveyorAnimator.Belt(() => World.Deposit("stone")));
    }
QUARRY_BREAK
}

static class NewDayHarvestPatch {
PHASE_FIELDS
    public static void Start(float delay) {
        phasedHarvestRunning = true;
        WorldManager.Instance.StartCoroutine(RunPhasedHarvestProcessing(delay, "", ""));
    }
PHASE_RUNNER
}

class Program {
    static void Check(bool ok, string what) { if (!ok) throw new Exception("FAIL: " + what); }

    // Snapshot parts, written in SaveAndLoad.SaveCoroutine order.
    static bool savedHiveFull, savedQuarryNode, savedFurnacePending, savedBerryRipe;
    static int savedPondRoe, snapshots, running, mostRunning; static Dictionary<string, int> savedChests;
    static bool furnacePending = true;
    const int MAP_FRAMES = 80;

    public static IEnumerator SaveCoroutine() {
        SaveLoad.saveOrLoad.isSaving = true;
        running++; mostRunning = Math.Max(mostRunning, running);
        for (int frame = 0; frame < MAP_FRAMES; frame++) {
            if (frame == 0) { savedHiveFull = World.hiveFull; savedFurnacePending = furnacePending; }
            if (frame == MAP_FRAMES - 1) { savedQuarryNode = World.quarryNode; savedBerryRipe = World.berryRipe; }
            yield return null;
        }
        savedPondRoe = World.pondRoe;
        savedChests = new Dictionary<string, int>(World.chests);
        snapshots++;
        yield return new WaitForSeconds(1f);
        yield return new WaitForSeconds(1f);
        running--;
        SaveLoad.saveOrLoad.isSaving = false;
    }

    // Game: WorldManager.countDownPos ejects a finished furnace once, then clears it. The save stored it as
    // unfinished on its first frame, so a reload of that save ejects it again.
    static IEnumerator FurnaceCountdown(float doneAt) {
        while (MonoBehaviour.now < doneAt) yield return null;
        WorldManager.Instance.onTileStatusMap[1, 1] = 1;
        if (EjectItemOnCyclePatch.Call(new ItemDepositAndChanger(), 1, 1)) World.groundBars++;
        WorldManager.Instance.onTileStatusMap[1, 1] = -2;
        furnacePending = false;
    }

    static int Saved(string id) { int n; savedChests.TryGetValue(id, out n); return n; }

    static void Main() {
        var saver = new SaveAndLoad();
        // Guards: a client save and an underground host never flush.
        ConveyorAnimator.Belt(() => World.ore++);
        RuntimeState.ResetForSave(false);
        Plugin.surface = false; RuntimeState.ResetForSave(true); Plugin.surface = true;
        Check(ConveyorAnimator.InFlight == 1 && World.ore == 0 && ConveyorHelper.flushes == 0 && VacuumCrateHelper.logSnapshots == 0, "save guards");

        // One flush delivers a whole chain: arc, belt, belt. No callback is dropped and nothing is left in flight.
        ConveyorAnimator.Arc(() => ConveyorAnimator.Belt(() => ConveyorAnimator.Belt(() => World.ore++)));
        RuntimeState.ResetForSave(true);
        Check(ConveyorAnimator.InFlight == 0 && World.ore == 2 && Plugin.warnings == 0 && VacuumCrateHelper.logSnapshots == 1, "chained flush");

        // The day-change kickoff starts 2s after the chunk refresh; the save starts during that delay.
        NewDayHarvestPatch.Start(2f);
        WorldManager.Instance.StartCoroutine(FurnaceCountdown(2.5f));
        bool started = false, asked = false, picked = false, firstChecked = false;
        for (int frame = 0; frame < 600; frame++) {
            MonoBehaviour.now = frame * 0.05f;
            if (!started && MonoBehaviour.now >= 1.5f) {
                // An item-owning arc, like a vacuum pickup or quarry pop, about to continue onto the belt.
                ConveyorAnimator.Arc(() => ConveyorAnimator.Belt(() => World.Deposit("ore")));
                saver.SaveGame(true);
                started = true;
                Check(World.Chest("ore") == 1 && ConveyorAnimator.InFlight == 0, "prefix drains arc into belt into chest");
            }
            if (started && !asked && MonoBehaviour.now >= 2f) {
                // The host asks for a second save while the first still writes. It runs after the first ends.
                saver.SaveGame(true);
                asked = true;
                Check(SaveAndLoad.started == 1, "second save waits");
            }
            if (started && !picked && MonoBehaviour.now >= 2.2f) {
                // The player picks a ripe berry bush next to a chest during the save.
                World.berryRipe = false;
                HarvestPatch.Call(() => World.Deposit("berry"));
                picked = true;
                Check(World.Chest("berry") == 0 && ConveyorAnimator.InFlight == 0, "harvest transfer waits for the save");
            }
            int scansBefore = Plugin.scans, updatesBefore = Plugin.animationUpdates;
            bool saving = SaveLoad.saveOrLoad.isSaving;
            Plugin.UpdateTick();
            if (saving) Check(Plugin.scans == scansBefore && Plugin.animationUpdates == updatesBefore, "update paused while saving");
            MonoBehaviour.Tick();
            if (snapshots == 1 && !firstChecked) {
                // One honey, one roe, one stone, one bar: each is in the first snapshot at most once.
                Check((savedHiveFull ? 1 : 0) + Saved("honey") == 1, "snapshot conservation: hive");
                Check(savedPondRoe + Saved("roe") == 1, "snapshot conservation: pond");
                Check((savedQuarryNode ? 1 : 0) + Saved("stone") == 1, "snapshot conservation: quarry");
                Check(Saved("ore") == 1, "snapshot conservation: flushed transfer");
                Check((savedFurnacePending ? 1 : 0) + Saved("bar") == 1, "snapshot conservation: furnace");
                // The harvest changed the map during the save; like the game's own unsaved drops, the berry is not saved twice.
                Check((savedBerryRipe ? 1 : 0) + Saved("berry") <= 1, "snapshot conservation: berry");
                firstChecked = true;
            }
        }
        Check(SaveAndLoad.started == 2 && mostRunning == 1 && !SaveLoad.saveOrLoad.isSaving && Plugin.vacuumVisualUpdates == 600, "saves ran one after the other");
        Check(VacuumCrateHelper.logSnapshots == 3 && ConveyorHelper.flushes == 3, "each save flushed and recorded logging once");

        // The held work resumed after the save, so nothing was dropped or left on the ground.
        Check(!World.hiveFull && World.pondRoe == 0 && !World.quarryNode, "held phases resumed");
        Check(World.Chest("honey") == 1 && World.Chest("roe") == 1 && World.Chest("stone") == 1, "live delivery");
        Check(World.Chest("bar") == 1 && World.groundBars == 0 && World.Chest("berry") == 1, "machine and harvest outputs delivered normally after the save");
        Check(ConveyorAnimator.InFlight == 0 && MonoBehaviour.Idle && !NewDayHarvestPatch.Running, "everything settled");
        // A queued save may resume before the next host Update drains the previous save's outputs.
        RuntimeState.RunAfterSave(() => ConveyorAnimator.Arc(() => World.Deposit("deferred")));
        RuntimeState.ResetForSave(true);
        Check(World.Chest("deferred") == 1 && ConveyorAnimator.InFlight == 0, "queued save flushes deferred output before snapshot");
        Console.WriteLine("PASS: save guards, chained flush, serialized saves, update pause, held day-change, quarry, machine, and harvest work");
    }
}
'''

replacements = {
    "IS_SAVING": plugin_is_saving,
    "UPDATE_GATE": update_gate,
    "RESET_FOR_SAVE": reset_for_save,
    "SAVE_PREFIX": save_prefix,
    "START_ANIMATION": start_animation,
    "CLEAR_ALL": clear_all,
    "HAS_ACTIVE": has_active,
    "ANIMATE_TRANSFER": animate_transfer,
    "PHASE_FIELDS": phase_fields + "        public static bool Running => phasedHarvestRunning;\n",
    "PHASE_RUNNER": phase_runner,
    "QUARRY_BREAK": quarry_break,
    "EJECT_PREFIX": eject_prefix,
    "HARVEST_START": harvest_start,
}
for key, value in replacements.items():
    assert key in source
    source = source.replace(key, value)

hold = "while (Plugin.IsSaving)\n                    yield return null;\n"
assert phase_runner.count(hold) == 4 and "Plugin.IsSaving || " in quarry_break
eject_hold = "if (Plugin.IsSaving && Plugin.CanRunServerAutomation())\n            {\n                Vector3 ejectPosition"
serialize = "            if (Plugin.IsSaving)\n            {\n                __instance.StartCoroutine("
assert eject_hold in eject_prefix and serialize in save_prefix and "flushing = true;" in clear_all
variants = (
    ("held", source, None),
    ("deferred_unflushed", source.replace("            RunQueuedAfterSave();\n", ""), "FAIL: queued save flushes deferred output before snapshot"),
    ("unheld", source.replace(hold, "").replace(quarry_break, quarry_break.replace("Plugin.IsSaving || ", "")), "FAIL: snapshot conservation"),
    ("no_inline_drain", source.replace("flushing = true;", "flushing = false;"), "FAIL: chained flush"),
    ("eject_unheld", source.replace(eject_hold, eject_hold.replace("Plugin.IsSaving && ", "false && ")), "FAIL: snapshot conservation: furnace"),
    ("eject_native", source.replace(eject_hold, eject_hold.replace("if (Plugin.IsSaving && Plugin.CanRunServerAutomation())\n            {", "if (Plugin.IsSaving) return true;\n            if (false)\n            {")), "FAIL: machine and harvest outputs"),
    ("unserialized", source.replace(serialize, serialize.replace("if (Plugin.IsSaving)", "if (false)")), "FAIL: second save waits"),
    ("harvest_unheld", source.replace("            if (Plugin.IsSaving && Plugin.CanRunServerAutomation())\n                RuntimeState.RunAfterSave(start);", "            if (false)\n                RuntimeState.RunAfterSave(start);"), "FAIL: harvest transfer waits for the save"),
)

with tempfile.TemporaryDirectory(prefix="autom8er-save-check-") as temp:
    for name, text, expected in variants:
        directory = Path(temp) / name
        directory.mkdir()
        (directory / "Program.cs").write_text(text)
        (directory / "Check.csproj").write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>disable</Nullable><NoWarn>CS0414;CS0162;CS0649</NoWarn></PropertyGroup></Project>')
        result = subprocess.run(["dotnet", "run", "--project", str(directory / "Check.csproj"), "-c", "Release"], capture_output=True, text=True)
        output = result.stdout + result.stderr
        if expected is None:
            assert result.returncode == 0, output
            print(output.strip().splitlines()[-1])
        else:
            assert result.returncode != 0 and expected in output, name + "\n" + output
            print("PASS: the " + name + " copy fails with " + expected)
