"""Run: uv run --no-project python Autom8er/tests/VacuumCrateHelper.check.py

Runs source-extracted logging methods with game substitutes. ES3 is replaced by
JSON file storage here; the check does not validate Unity or ES3 at runtime.
"""
from pathlib import Path
import re
import subprocess
import tempfile

root = Path(__file__).resolve().parents[1]
source = (root / "code/VacuumCrateHelper.cs").read_text()

def extract(name, source=source, kind=r"(?:private|public|internal) static [^\n]*\b"):
    match = re.search(r"^        " + kind + name + r"(?:\(|$)", source, re.M)
    start = match.start()
    end = source.index("{", start) + 1
    depth = 1
    while depth:
        depth += (source[end] == "{") - (source[end] == "}")
        end += 1
    return source[start:end]

names = "PosKey EnsureLoggingCrateInstance IsLiveFarmerChest LoadLoggingRows SaveLoggingRows GetActiveLoggingSeedItemId GetLoggingDirection GetLoggingRowLength TryInferExistingLoggingRow TryGetLoggingRowTile TryGetLoggingTileInDirection FindItemSlot CountItemInChest TryFindNextLoggingAction TryProcessFarmTileCore TryProcessLoggingStep PlantTreeAt ConsumeOneItem PruneExpiredLoggingCollections IsLiveLoggingCrate HasPendingLoggingCollection RunDayChangeFarmerWaves HasImmediateWork ClearVisuals TryTakeLoggingBreak TryProcessFarmTile".split()
methods = "\n".join(extract(name) for name in names)
methods += "\n" + "\n".join(extract(name) for name in "BeginLoggingDropCapture CompleteLoggingDropCapture FindPendingLoggingCollectionAtSource CaptureLoggingDropIds IsCapturedByOtherLoggingCollection FindLoggingDropOwner GetDropTile".split())
# Bee hive logging: real hive methods, the real logging protection, and the real chest deposit.
methods += "\n" + "\n".join(extract(name) for name in "BeginLoggingHiveSpawnCapture EndLoggingWorldObjectDrop RecordLoggingHiveSpawn GetLoggingHiveBreakObject HasBugNet IsBugNetItem TryBreakLoggingHives CountLoggingHiveOutputSlots NeedsNewStackSlot CountEmptySlots HorizontalDistance BeginLoggingHiveBreakDrop TryTakeLoggingHiveOutput".split())
methods += "\n" + "\n".join(extract(name) for name in "CountReservedLoggingHiveSlots CanTakeLoggingOutput IsOwnedLoggingDrop HoldLoggingCollection HoldLoggingOutput IsWorldSaving BuildDropGroups".split())
methods += "\n" + extract("TryVacuumNearbyDrops", kind=r"private static float ").replace("TryVacuumNearbyDrops(", "RealTryVacuumNearbyDrops(", 1)
methods += "\n" + extract("FindLoggingSeedForTreeTile").replace("FindLoggingSeedForTreeTile(", "RealFindLoggingSeedForTreeTile(", 1)
methods += "\n" + extract("GetProtectedAutoFarmerAmount").replace("GetProtectedAutoFarmerAmount(", "RealGetProtectedAutoFarmerAmount(", 1)
methods += "\n" + "\n".join(extract(name, kind=r"private class ") for name in "PendingLoggingCollection LoggingHive LoggingHiveBreak VacuumDropGroup".split())
methods += "\n" + "\n".join(re.findall(r"^        private const (?:float LOGGING_HIVE_|int LOGGING_BREAKS_|float LOGGING_BREAK_)[^\n]*", source, re.M))
# Save and load of held logging outputs: the real records, restore hooks, paused clock, and after-save hive outputs.
methods += "\n" + "\n".join(extract(name) for name in "LoggingSavePath LoggingTime PrepareLoggingForSave IsSavedCarryable AddLoggingSaveRecord AddLoggingHiveRecord BuildLoggingHeldRecords ReadLoggingLoadRecords GetRestoredLoggingCollection TakeLoggingLoadRecord BeginLoggingRestore BeginLoggingRestoreDrop CompleteLoggingRestoreDrop EndLoggingRestore RestoreLoggingCollections RespawnLoggingTrunk TakeLoggingHiveOutputAfterSave UpdateVisuals".split())
held_start = source.index("        // Logging timestamps use this clock.")
methods += "\n" + source[held_start:source.index("\n", source.index("        private static bool loggingRestoreActive;"))]
runtime = (root / "code/RuntimeState.cs").read_text()
after_save = runtime[runtime.index("        // Item moves that start while a save runs"):runtime.index("        // On load, before")]
conveyor = extract("FindSlotForItem", (root / "code/ConveyorHelper.cs").read_text())
deposit = extract("TryDepositHarvestToChest", (root / "code/HarvestHelper.cs").read_text())
plugin = (root / "code/Plugin.cs").read_text()
stacking = extract("ApplyStackabilityOverrides", plugin, kind=r"private void ")
stacking += "\n" + "\n".join(extract(name, plugin, kind=r"internal static bool ") for name in "IsStackedHiveBee IsModStackedCritter".split())
stacking += "\n" + "\n".join(re.findall(r"^        public const int (?:QUEEN_)?BEE_ITEM_ID[^\n]*", plugin, re.M))
# The real daytime round-robin lane, which shares the logging break budget.
stacking += "\n" + "\n".join(extract(name, plugin, kind=r"private [^\n]*\b") for name in "ProcessFarmerCrateBatch CalculateRoundRobinBudget GetNextRoundRobinChest".split()).replace("VacuumCrateHelper.", "Program.")
def patch_source(name):
    text = (root / "code" / name).read_text().split("namespace Autom8er", 1)[1]
    return text[text.index("{") + 1:text.rfind("}")].replace("VacuumCrateHelper.", "Program.").replace("static void", "public static void").replace("static bool", "public static bool").replace("public static class", "internal static class")
# Real chest-lock exception and stack refund patches for bees and critters.
patches = patch_source("LoggingDropCapturePatch.cs") + patch_source("StackedCritterPatches.cs")
assert 'typeof(SaveAndLoad), "SaveChests"' in (root / "code/LoggingRowSavePatch.cs").read_text()
assert "VacuumCrateHelper.SaveLoggingRows();" in (root / "code/LoggingRowSavePatch.cs").read_text()
assert "!IsLiveFarmerChest(chest)" in extract("TryProcessBundledCropReplantStep")
assert 'typeof(SaveAndLoad), "LoadOverFrames"' in (root / "code/LoadGameStateClearPatch.cs").read_text()

code = r'''
using System; using System.Collections.Generic; using System.IO; using System.Text.Json;
class HarmonyPatch : Attribute { public HarmonyPatch(){} public HarmonyPatch(Type type,string method){} public HarmonyPatch(Type type,string method,Type[] args){} }
struct Vector3 { public float x,y,z; public Vector3(float x,float y,float z){this.x=x;this.y=y;this.z=z;} public static Vector3 operator-(Vector3 a,Vector3 b)=>new Vector3(a.x-b.x,a.y-b.y,a.z-b.z); public float sqrMagnitude=>x*x+y*y+z*z; public static float Distance(Vector3 a,Vector3 b)=>(float)Math.Sqrt((a.x-b.x)*(a.x-b.x)+(a.y-b.y)*(a.y-b.y)+(a.z-b.z)*(a.z-b.z)); }
class MethodBase {} static class AccessTools { public static MethodBase Method(Type t,string name)=>null; } class DropSaves {} class CarrySave {}
struct Quaternion { public static Quaternion identity=>new Quaternion(); }
struct Vector2 { public float x,y; public Vector2(float x,float y){this.x=x;this.y=y;} public float magnitude=>(float)Math.Sqrt(x*x+y*y); }
class UObj { public static implicit operator bool(UObj o)=>o!=null; }
class Transform { public Vector3 position; }
// Game: InventoryItemLootTable.getRandomDropFromTable rolls Bee 55% / Queen Bee 45% (BeeHiveBreaks prefab data).
class InventoryItemLootTable { public InventoryItem[] itemsInLootTable; public System.Random rng=new System.Random(7); public int rolls; public int Roll(){rolls++;return rng.Next(100)<55?227:228;} }
class WorldObject { public bool destroyAndDropItemAfterTime=true,destroyAndDropBeforeTime=false; public float destroyTime=2.5f; public Transform transform=new Transform(); public InventoryItemLootTable spawnBugOnDrop; public Transform[] bugPositions=new Transform[0],dropPositions=new Transform[0]; public InventoryItem dropsItemOnDestroyed; }
// Game: a hive's death (health 5, maxHealth 2) destroys it a frame later and spawns its break object.
class Damageable { public int health=5; public GameObject spawnWorldObjectOnDeath; public PickUpAndCarry owner; public void changeHealth(int dif){health=Math.Clamp(health+dif,0,2);if(health<=0)Game.broken.Add(owner);} }
// gameObject is null once the game destroys the hive, a frame after its death.
class PickUpAndCarry { public Transform transform=new Transform(); public uint beingCarriedBy; public bool delivered; public int prefabId=3; public GameObject gameObject=new GameObject(); public bool IsDropOnCurrentLevel()=>true; public Damageable damageable; public T GetComponent<T>() where T:class=>damageable as T; }
class NetworkNavMesh { public static NetworkNavMesh nav=new NetworkNavMesh(); public int GetMyConnectedId(CharMovement c)=>0; public void spawnSpecificBug(int id,Vector3 at){Game.nativeBugs.Add(id);} }
static class Game { public static uint nextNet=90000; public static List<PickUpAndCarry> broken=new List<PickUpAndCarry>(); public static List<int> nativeBugs=new List<int>(),nativeDrops=new List<int>(); public static int credits; }
// A null arrival is a visual-only arc. The test counts it but has nothing to run.
class ConveyorAnimator { public static Vector3 TileToWorld(int x,int y)=>new Vector3(x*2,0,y*2); public static List<Action> arrivals=new List<Action>(); public static void AnimateArcTransfer(int id,int stack,Vector3 from,Vector3 to,float duration,float arc,Action onArrival,float delay=0f){arrivals.Add(onArrival);} public static void AnimateTransfer(int id,int stack,Vector2Int from,Vector2Int to,HouseDetails h,Action onArrival){throw new Exception("Logging used the network route");} }
class FilterCrateHelper { public static void MarkRouteActive(int x,int y,HouseDetails h){} }
class HarvestHelper { DEPOSIT }
class DroppedItem { public uint netId; public int myItemId=-1,stackAmount,endOfDayTallyType=-1; public bool saveDrop; public Vector3 desiredPos; public int NetworkstackAmount{get=>stackAmount;set=>stackAmount=value;} public HouseDetails inside=null; public Vector2Int onTile; public Transform transform=new Transform(); public bool IsDropOnCurrentLevel()=>true; }
struct Vector2Int { public int x,y; public Vector2Int(int x,int y){this.x=x;this.y=y;} public static Vector2Int zero=>new Vector2Int(0,0); }
class Mathf { public static float Clamp(float n,float a,float b)=>Math.Clamp(n,a,b); public static float Max(float a,float b)=>Math.Max(a,b); public static int RoundToInt(float n)=>(int)Math.Round(n); public static float Abs(float n)=>Math.Abs(n); public static int Abs(int n)=>Math.Abs(n); public static int Min(int a,int b)=>Math.Min(a,b); public static int Max(int a,int b)=>Math.Max(a,b); public static int Clamp(int n,int a,int b)=>Math.Clamp(n,a,b); public static int CeilToInt(float n)=>(int)Math.Ceiling(n); }
class Time { public static float time=1; public static int frameCount; }
class WaitForSecondsRealtime { public WaitForSecondsRealtime(float seconds){} }
class GameObject { public static List<GameObject> spawned=new List<GameObject>(); public static GameObject Instantiate(GameObject prefab,Vector3 at,Quaternion r){var g=new GameObject{worldObject=prefab.worldObject,at=at};spawned.Add(g);return g;} public Vector3 at; public Transform transform=new Transform(); public bool activeSelf; public WorldObject worldObject; public T GetComponent<T>() where T:class=>worldObject as T; public static void Destroy(GameObject obj){} }
class HouseDetails {}
class Chest { public int xPos=30,yPos=30,insideX=-1,insideY=-1; public int[] itemIds=new int[24],itemStacks=new int[24]; }
class TileObject : UObj { public int tileObjectId=2; public TileObjectGrowthStages tileObjectGrowthStages; }
class TileObjectGrowthStages { public TileObject changeToWhenGrown; }
class InventoryItem { public enum typeOfAnimation { ShovelAnimation, Pickaxe, Axe, BugNet } public TileObject placeable=new TileObject(); public void checkForTask(){} public string name=""; public bool isStackable=true,isATool,hasFuel; public typeOfAnimation myAnimType; public UObj bug,fish,underwaterCreature; public bool checkIfStackable()=>isStackable&&!isATool&&!hasFuel; }
// Game: InventorySlot UI state. Harmony runs the refund prefix before every refresh.
class InventorySlot { public int itemNo=-1,stack; public bool disabled,selected; public bool isDisabledForGive()=>disabled; public void disableForGive(){disabled=true;} public void clearDisable(){disabled=false;} public bool isSelectedForGive()=>selected; public void updateSlotContentsAndRefresh(int newItemNo,int amount){StackedBugTerrariumRefundPatch.Prefix(this,ref newItemNo,ref amount);itemNo=newItemNo;stack=amount;} }
// Game: ChestWindow.lockBugsAndFishFromChest disables every inventory insect and fish; Harmony then runs the postfix.
class ChestWindow { public bool isStash; public GameObject pondScreen=new GameObject(); public Chest currentlyOpenedChest; public void lockBugsAndFishFromChest(){foreach(var s in Inventory.Instance.invSlots){if(s.itemNo>=0&&((bool)Inventory.Instance.allItems[s.itemNo].bug||(bool)Inventory.Instance.allItems[s.itemNo].fish))s.disableForGive();}ChestHiveBeeUnlockPatch.Postfix(this,currentlyOpenedChest);} }
// Game: CharMovement.UserCode_CmdDropItem releases one live bug for any bug item; other items drop as a pickup.
class Permissions { public bool canPickUp=true; public bool CheckIfCanPickUp()=>canPickUp; } class EquipItemToChar { public Permissions myPermissions=new Permissions(); } class CharInteract { public HouseDetails InsideHouseDetails; }
class CharMovement { public EquipItemToChar myEquip=new EquipItemToChar(); public CharInteract myInteract=new CharInteract(); public void UserCode_CmdDropItem(int itemId,int stackAmount){if(!StackedBugDropPatch.Prefix(this,itemId,stackAmount,new Vector3(),new Vector3()))return;if((bool)Inventory.Instance.allItems[itemId].bug)Game.nativeBugs.Add(itemId);else if(myEquip.myPermissions.CheckIfCanPickUp())NetworkMapSharer.Instance.CharDropsAServerDrop(itemId,stackAmount,new Vector3(),new Vector3(),null,false,0);} } class GiveNPC {}
class Inventory { public static Inventory Instance=new Inventory(); public InventorySlot[] invSlots=new InventorySlot[0]; public int selectedSlot; public InventoryItem[] allItems=new InventoryItem[2200]; public int getInvItemId(InventoryItem item)=>Array.IndexOf(allItems,item); }
class DailyTaskGenerator { public enum genericTaskType { None, ChopDownStump } }
class TileObjectSettings { public GameObject dropsObjectOnDeath; public bool isGrass=false; public int changeToTileObjectOnDeath=-1; public DailyTaskGenerator.genericTaskType TaskType=DailyTaskGenerator.genericTaskType.None; }
class WorldManager { public List<DroppedItem> itemsOnGround=new List<DroppedItem>(); public static WorldManager Instance=new WorldManager(); public int[,] onTileMap=new int[120,70],heightMap=new int[120,70],onTileStatusMap=new int[120,70]; public TileObjectSettings[] allObjectSettings=new TileObjectSettings[413]; }
class ContainerManager {
 public static ContainerManager manage=new ContainerManager(); public List<Chest> activeChests=new List<Chest>();
 public static int lookups; public Chest getChestForWindow(int x,int y,HouseDetails h){lookups++;return activeChests.Find(c=>c.xPos==x&&c.yPos==y);}
 public void changeSlotInChest(int x,int y,int slot,int id,int count,HouseDetails h){ var c=getChestForWindow(x,y,h); if(c!=null){c.itemIds[slot]=id;c.itemStacks[slot]=count;} }
}
class ConveyorHelper {
 public static bool IsVacuumCrate(int x,int y,HouseDetails h)=>x>=0&&y>=0&&x<120&&y<70&&WorldManager.Instance.onTileMap[x,y]==412;
 public static Chest FindChestAt(int x,int y,HouseDetails h)=>ContainerManager.manage.getChestForWindow(x,y,h);
 public static bool IsAutoSorter(Chest c)=>false; public static void QueueAutoSorterUpdate(Chest c){}
 public class OutputDestination { public Chest chest; public int routeX,routeY; }
 public static bool TryFindBestOutputDestinationFromNetworkAnchor(Chest c,HouseDetails h,int id,bool includeAnchorChest,bool excludeVacuumCrates,out OutputDestination d){d=null;return false;}
 public static bool FallbackDepositToAnyChest(int x,int y,HouseDetails h,int id,int amount,Action credit=null)=>false; public static void EnsureAutomationChestsActive(Chest c){}
 CONVEYOR
}
class FarmEnclosureHelper { public static int calls; public static Func<int,int,bool> fenced; public static bool IsInsideEnclosure(Chest c,int x,int y){calls++;return fenced==null||!fenced(x,y);} }
class NetworkMapSharer { public static NetworkMapSharer Instance=new NetworkMapSharer(); public int writes; public void CharDropsAServerDrop(int id,int stack,Vector3 at,Vector3 desired,HouseDetails inside,bool tryNotToStack,int xPType){for(int i=0;i<stack;i++)Game.nativeDrops.Add(id);} public void spawnAServerDrop(int id,int stack,Vector3 at,HouseDetails inside=null,bool tryNotToStack=false,int xPType=-1){for(int i=0;i<stack;i++)Game.nativeDrops.Add(id);} public void spawnAServerDropToSave(int id,int stack,Vector3 at,HouseDetails inside=null,bool tryNotToStack=false,int xPType=-1){Dictionary<DroppedItem,int> st;LoggingRestoreDropPatch.Prefix(id,out st);var tile=new Vector2Int((int)Math.Round(at.x/2),(int)Math.Round(at.z/2));var same=tryNotToStack||Inventory.Instance.allItems[id]?.checkIfStackable()!=true?null:WorldManager.Instance.itemsOnGround.Find(d=>d.myItemId==id&&d.onTile.x==tile.x&&d.onTile.y==tile.y);if(same!=null)same.stackAmount+=stack;else WorldManager.Instance.itemsOnGround.Add(new DroppedItem{netId=Game.nextNet++,myItemId=id,stackAmount=stack,saveDrop=true,endOfDayTallyType=xPType,onTile=tile,desiredPos=at});LoggingRestoreDropPatch.Postfix(id,stack,at,inside,st);} public PickUpAndCarry spawnACarryable(GameObject go,Vector3 pos,bool moveToGroundLevel=true)=>Program.SpawnHive(pos); public float trunkFall; public List<(int x,int y,float at)> trunks=new List<(int,int,float)>(); public List<(int x,int y,int old)> breakLog=new List<(int,int,int)>(); public void RpcUpdateOnTileObject(int id,int x,int y){writes++;if(WorldManager.Instance.onTileMap[x,y]==1||WorldManager.Instance.onTileMap[x,y]==3)breakLog.Add((x,y,WorldManager.Instance.onTileMap[x,y]));if(trunkFall>0&&WorldManager.Instance.onTileMap[x,y]==1)trunks.Add((x,y,Time.time+trunkFall));WorldManager.Instance.onTileMap[x,y]=id;} }
class Log { public int warnings; public List<string> info=new List<string>(); public void LogWarning(string message){warnings++;} public void LogInfo(string message){info.Add(message);} }
class Plugin { public const int WATER_TANK_ITEM_ID=692,TROPICAL_GRASS_TURF_ROLL_ITEM_ID=1445; public static bool allowed=true,StackableCritters=true; public static Log Log=new Log(); public static bool CanRunServerAutomation()=>allowed; public static void LogVerbose(string m){} public static void LogWarningThrottled(string k,string m,float s=5f){Log.warnings++;} bool crittersPatched=false; public void RunStacking()=>ApplyStackabilityOverrides(); public static float ScanInterval=.1f; public void DaytimeLoggingPass(List<Chest> c,ref int cursor)=>ProcessFarmerCrateBatch(c,ref cursor,.45f,4); STACKING }
class SaveAndLoad { public string GetSaveSlotDirectory()=>SaveLoad.saveOrLoad.slot; }
class SaveLoad { public static SaveLoad saveOrLoad=new SaveLoad(); public SaveAndLoad newFileSaver=new SaveAndLoad(); public bool loadingComplete=true,isSaving; public string slot=""; public GameObject[] carryablePrefabs={new GameObject(),new GameObject(),new GameObject(),new GameObject()}; public string saveSlot()=>slot; }
class ES3 {
 public static int writes;
 // ES3 keeps each key beside the others in one file. A file that does not parse throws, as a corrupt ES3 file does.
 static Dictionary<string,JsonElement> Keys(string path)=>File.Exists(path)?JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(File.ReadAllText(path)):new Dictionary<string,JsonElement>();
 public static bool KeyExists(string key,string path)=>Keys(path).ContainsKey(key);
 public static T Load<T>(string key,string path)=>Keys(path)[key].Deserialize<T>();
 public static void Save<T>(string key,T value,string path){Directory.CreateDirectory(Path.GetDirectoryName(path));var keys=Keys(path);keys[key]=JsonSerializer.SerializeToElement(value);File.WriteAllText(path,JsonSerializer.Serialize(keys));writes++;}
}
static class RuntimeState {
AFTER_SAVE
}
class AutomationCreditHelper { public static void TryGrantLoggingBreakCredit(int id){} public static void TryGrantDroppedItemPickupCredit(int id,int amount,int tally){if(tally>=0)Game.credits+=amount;} }
class Program {
 const int LOGGING_DROP_RADIUS_TILES=2,MAX_LOGGING_ROW_LENGTH=24,LOGGING_DIRECTION_COLUMNS=6,FARM_ACTIONS_PER_SCAN=2,DAY_CHANGE_HARVEST_CRATES_PER_WAVE=4;
 const float LOGGING_TRUNK_DROP_GRACE_SECONDS=2,LOGGING_DROP_SETTLE_SECONDS=.08f,LOGGING_COLLECTION_EXPIRY_SECONDS=30,FARM_TILE_RESERVATION_SECONDS=.35f,DAY_CHANGE_HARVEST_STEP_DELAY_SECONDS=.1f,DAY_CHANGE_HARVEST_WAVE_DELAY_SECONDS=.05f;
 static PendingLoggingCollection loggingHiveSpawnOwner; static LoggingHiveBreak activeLoggingHiveBreak; static List<LoggingHiveBreak> pendingLoggingHiveBreaks=new List<LoggingHiveBreak>();
 static void TriggerVacuumVisual(int x,int y,float duration){}
 static Vector3 GetVacuumTargetWorld(int x,int y)=>new Vector3(x*2,1,y*2);
 static bool IsHoeToolItem(int id)=>false; static bool IsShovelToolItem(int id)=>false; static bool IsAxeToolItem(int id)=>id==200;
 static int CountValidLoggingRowSlots(Chest c,int id)=>0; static bool TryFindBestTreePlantTile(Chest c,int id,out FarmTileTarget t){t=null;return false;}
 enum LoggingActionType {None,Chop,Plant}
 class DayChangeFarmerWaveState {public Chest chest; public int wave; public bool finished;}
 class VacuumVisualState {public GameObject visual=null; public float expireAt; public int tileX,tileY;}
 const float VACUUM_VISUAL_HEIGHT=1.6f; static void ClearExpiredFarmReservations(){}
 class FarmTileTarget {}
 static Dictionary<long,PendingLoggingCollection> pendingLoggingCollections=new Dictionary<long,PendingLoggingCollection>();
 static Dictionary<long,int> activeLoggingSeedByCrate=new Dictionary<long,int>(),loggingRowLengthByCrate=new Dictionary<long,int>();
 static Dictionary<long,Vector2Int> loggingDirectionByCrate=new Dictionary<long,Vector2Int>();
 static Dictionary<long,Chest> loggingCrateInstances=new Dictionary<long,Chest>();
 static Dictionary<long,bool> pruneLiveCrates=new Dictionary<long,bool>();
 static bool loggingRowsLoaded;
 static Dictionary<long,VacuumVisualState> activeVisuals=new Dictionary<long,VacuumVisualState>();
 static HashSet<uint> pendingDrops=new HashSet<uint>(); static HashSet<int> harvestCollectingCrates=new HashSet<int>(),pendingFlushStartedAt=new HashSet<int>(),reservedFarmTiles=new HashSet<int>(),pendingFarmActions=new HashSet<int>(),activeCrateUntil=new HashSet<int>(),farmerNetworkScaleCache=new HashSet<int>();
 static int collections; static bool realVacuum; static Dictionary<int,int> loggingSeedByTreeTile; static HashSet<int> seedItems=new HashSet<int>();
 const int VACUUM_PICKUP_RADIUS_TILES=12,MAX_LOGGING_GROWTH_CHAIN=8; const float VACUUM_ITEM_DELAY=.03f;
 static bool IsHarvestCollecting(Chest c)=>false; static bool IsVehicleItem(int id)=>false; static bool IsDropInSameSpace(DroppedItem d,HouseDetails h)=>true;
 static bool CanVacuumDeposit(Chest c,HouseDetails h,int id)=>throw new Exception("Logging used the generic deposit check");
 static bool TryRemoveDropFromWorld(DroppedItem d)=>WorldManager.Instance.itemsOnGround.Remove(d);
 static void SpawnFallbackGroundDrop(int id,int stack,int tally,Vector3 at,HouseDetails h)=>throw new Exception("Logging used the unowned ground fallback");
 static bool IsLoggingTreeObject(int id)=>id==7||id==8||id==9;
 static bool IsLoggingTreePlantingItem(int id)=>id==100||seedItems.Contains(id);
 static int FindLoggingSeedForTreeTile(int id)=>id==1||id==2?100:-1;
 static bool HasAxeTool(Chest c)=>FindItemSlot(c,id=>id==200)>=0;
 static int GetBestAxeSlot(Chest c)=>FindItemSlot(c,id=>id==200);
 static InventoryItem GetBestAxeItem(Chest c)=>HasAxeTool(c)?Inventory.Instance.allItems[200]:null;
 static bool IsLoggingStump(int id,InventoryItem axe)=>id==3;
 static bool IsMatureLoggingTree(int x,int y,int id,InventoryItem axe)=>id==1;
 static bool CanPlantLoggingSeedAt(int seed,int x,int y)=>seed==100&&WorldManager.Instance.onTileMap[x,y]==-1;
 static void ReserveFarmTile(int x,int y,float time){}
 static bool AreFarmerActionsSuspended()=>false;
 static bool TryProcessOneFarmAction(Chest c)=>false;
 static List<uint> collectedDrops=new List<uint>();
 static float TryVacuumNearbyDrops(Chest c,HouseDetails h,PendingLoggingCollection p){collections++;if(realVacuum)return RealTryVacuumNearbyDrops(c,h,p);foreach(var drop in WorldManager.Instance.itemsOnGround.ToArray())if(FindLoggingDropOwner(drop)==p){Check(!collectedDrops.Contains(drop.netId),"Drop collected twice");collectedDrops.Add(drop.netId);WorldManager.Instance.itemsOnGround.Remove(drop);}return 0;}
 static void MarkCrateActive(Chest c){}
 static int TryProcessBundledCropReplantStep(Chest c)=>0;
 static int ProcessPendingFarmActions(bool ignoreSuspension=false)=>0;
 static bool HasPendingFarmActionsForAny(List<Chest> c,int start,int end)=>false;
 static bool HasPendingFarmActionsForChest(Chest c)=>false;
 static bool HasHoeTool(Chest c)=>false;
 static bool HasShovelTool(Chest c)=>false;
 static int GetProtectedAutoFarmerAmount(Chest c,int id)=>CountItemInChest(c,id);
 public static bool TryFlushStoredItemsToNetwork(Chest c,HouseDetails h)=>false;
 static bool IsFertilizerItem(int id)=>false;
 static bool IsGrowthBoosterItem(int id)=>false;
 static bool IsCropSeedItem(int id)=>false;
 static bool IsTreePlantingItem(int id)=>id==100;
 static bool ShouldRetainForAutoFarmer(Chest c,int id)=>false;
 static bool CanFertilizeTile(int x,int y)=>false;
 static bool CanApplyInstaGrowTile(int x,int y)=>false;
 static bool TryFindBestFarmTile(Chest c,Func<int,int,bool> f,out FarmTileTarget t){t=null;return false;}
 METHODS
 // The visible pacing the user accepted: at most 4 native logging breaks per 0.1s step, fixed here so a raised budget fails.
 const int MAX_BREAKS_PER_STEP=4;
 static void Check(bool ok,string label){if(!ok)throw new Exception(label);}
 static Chest AddChest(int seeds=0,int axeSlot=0){var c=new Chest();Array.Fill(c.itemIds,-1);c.itemIds[axeSlot]=200;c.itemStacks[axeSlot]=100;c.itemIds[12]=100;c.itemStacks[12]=seeds;ContainerManager.manage.activeChests.Add(c);WorldManager.Instance.onTileMap[30,30]=412;return c;}
 static void Reset(string slot){ClearVisuals();ContainerManager.manage.activeChests.Clear();for(int x=0;x<120;x++)for(int y=0;y<70;y++)WorldManager.Instance.onTileMap[x,y]=-1;SaveLoad.saveOrLoad.slot=Path.Combine(Environment.CurrentDirectory,slot);Plugin.allowed=true;SaveLoad.saveOrLoad.loadingComplete=true;Time.time=1;collections=0;NetworkMapSharer.Instance.writes=0;}
 static void Main(){
 Inventory.Instance.allItems[100]=new InventoryItem();Inventory.Instance.allItems[200]=new InventoryItem();WorldManager.Instance.allObjectSettings[1]=new TileObjectSettings();
 Reset("axe");var chest=AddChest(1);TryProcessFarmTileCore(chest,true);WorldManager.Instance.onTileMap[30,31]=1;TryProcessFarmTileCore(chest,true);chest.itemIds[0]=-1;
 Check(HasImmediateWork(chest),"Axe removal hid pending work");Time.time=2;bool[] done={false};var wave=RunDayChangeFarmerWaves(new List<Chest>{chest},done);int steps=0;while(wave.MoveNext()&&steps++<4){}Check(done[0]&&collections==1&&pendingLoggingCollections.Count==0,"Axe removal stalled collection");
 Console.WriteLine("PASS: removing axe finishes collection and the wave");
 foreach(bool replace in new[]{false,true}){Reset("carry"+replace);chest=AddChest(2);EnsureLoggingCrateInstance(chest);ContainerManager.manage.activeChests.Clear();WorldManager.Instance.onTileMap[30,30]=-1;if(replace)AddChest(7);pendingLoggingCollections[PosKey(30,31)]=new PendingLoggingCollection{chestX=30,chestY=30,sourceX=30,sourceY=31,expiresAt=40};done=new[]{false};wave=RunDayChangeFarmerWaves(new List<Chest>{chest},done);Check(!wave.MoveNext()&&done[0]&&NetworkMapSharer.Instance.writes==0&&pendingLoggingCollections.Count==0,"Stale source kept acting");Check(chest.itemStacks[12]==2,"Carried inventory changed");}
 Console.WriteLine("PASS: carried/replaced sources cannot act or hold the wave open");
 foreach(int length in new[]{1,5,24}){Reset("row"+length);chest=AddChest(length,11);TryProcessFarmTileCore(chest,true);chest.itemStacks[12]=0;WorldManager.Instance.onTileMap[31,30]=-1;int offset=length==1?1:3;WorldManager.Instance.onTileMap[30+offset,30]=1;SaveLoggingRows();ClearVisuals();ContainerManager.manage.activeChests.Clear();chest=AddChest();LoggingActionType action;int x,y,tile,slot,seed;Check(TryFindNextLoggingAction(chest,out action,out x,out y,out tile,out slot,out seed)&&action==LoggingActionType.Chop&&x==30+offset&&y==30&&seed==100&&GetLoggingRowLength(chest,seed)==length,"Saved row did not recover");WorldManager.Instance.onTileMap[x,y]=-1;Check(GetActiveLoggingSeedItemId(chest)==100,"Empty row lost species");}
 Console.WriteLine("PASS: seedless 1/5/24-tree rows restore direction, length, species, including gaps");
 // Changing save slots and replacing a crate cannot inherit another row.
 Reset("new-slot");chest=AddChest();Check(GetActiveLoggingSeedItemId(chest)==-1,"Save-slot state leaked");
 SaveLoad.saveOrLoad.slot=Path.Combine(Environment.CurrentDirectory,"row1");ClearVisuals();LoadLoggingRows();ContainerManager.manage.activeChests.Clear();chest=AddChest(5);Check(GetActiveLoggingSeedItemId(chest)==100&&GetLoggingRowLength(chest,100)==5,"Replacement inherited saved row length");SaveLoggingRows();ContainerManager.manage.activeChests.Clear();SaveLoggingRows();ClearVisuals();chest=AddChest();Check(GetActiveLoggingSeedItemId(chest)==-1,"Removed row survived save");
 Console.WriteLine("PASS: save-slot isolation and replaced/removed row cleanup");
 Reset("guards");chest=AddChest(1);Plugin.allowed=false;int writes=ES3.writes;Check(TryProcessFarmTileCore(chest,true)==0,"Guest/off-island action");SaveLoggingRows();Check(ES3.writes==writes,"Guest/off-island save");Plugin.allowed=true;SaveLoad.saveOrLoad.loadingComplete=false;LoadLoggingRows();Check(!loggingRowsLoaded,"Loaded rows before world was ready");SaveLoad.saveOrLoad.loadingComplete=true;
 ES3.Save("rows",new Dictionary<long,int[]>{{PosKey(30,30),new[]{100,1,1,100}}},SaveLoad.saveOrLoad.saveSlot()+"/autom8er-logging.es3");LoadLoggingRows();Check(activeLoggingSeedByCrate.Count==0,"Invalid row accepted");
 ClearVisuals();File.WriteAllText(SaveLoad.saveOrLoad.saveSlot()+"/autom8er-logging.es3","broken");int warnings=Plugin.Log.warnings;LoadLoggingRows();Check(Plugin.Log.warnings==warnings+2,"Broken file did not warn for rows and held outputs");
 Console.WriteLine("PASS: host/load guards and malformed saved rows");
 Reset("falling-trunk");chest=AddChest(1);WorldManager.Instance.onTileMap[30,31]=1;
 var trunk=new WorldObject();trunk.transform.position=new Vector3(60,0,62);
 WorldManager.Instance.allObjectSettings[1].dropsObjectOnDeath=new GameObject{worldObject=trunk};
 var oldDrop=new DroppedItem{netId=9,onTile=new Vector2Int(30,31)};WorldManager.Instance.itemsOnGround.Add(oldDrop);
 Check(TryProcessFarmTileCore(chest,true)==1,"Tree was not chopped");
 HashSet<uint> before;LoggingDropCapturePatch.Prefix(30,31,null,out before);
 var immediate=new DroppedItem{netId=10,onTile=new Vector2Int(30,31)};WorldManager.Instance.itemsOnGround.Add(immediate);
 LoggingDropCapturePatch.Postfix(30,31,null,before);
 Time.time=2;Check(TryProcessFarmTileCore(chest,true)==0&&collections==0&&pendingLoggingCollections.Count==1,"Collection ended before trunk fell");
 // A neighboring death must not release this crate's pending collection.
 var neighbor=new WorldObject();neighbor.transform.position=new Vector3(62,0,62);
 LoggingWorldObjectDropCapturePatch.Prefix(neighbor,out before);LoggingWorldObjectDropCapturePatch.Postfix(neighbor,before);
 Check(TryProcessFarmTileCore(chest,true)==0,"Unrelated trunk released collection");
 Time.time=4;LoggingWorldObjectDropCapturePatch.Prefix(trunk,out before);
 var late=new DroppedItem{netId=11,onTile=new Vector2Int(40,40)};WorldManager.Instance.itemsOnGround.Add(late);
 LoggingWorldObjectDropCapturePatch.Postfix(trunk,before);
 var pending=pendingLoggingCollections[PosKey(30,31)];
 Check(pending.spawnedDropIds.SetEquals(new uint[]{10,11})&&FindLoggingDropOwner(late)==pending&&FindLoggingDropOwner(oldDrop)==null,"Delayed or relocated drop ownership failed");
 Check(TryProcessFarmTileCore(chest,true)==0,"Skipped settle time after trunk drop");
 Time.time=4.2f;Check(TryProcessFarmTileCore(chest,true)==2&&collections==1&&pendingLoggingCollections.Count==0&&WorldManager.Instance.onTileMap[30,31]==2,"Trunk drops never reached collection and replant");
 Console.WriteLine("PASS: delayed trunk drops retain ownership, including relocated drops; old and neighboring drops stay separate");
 // Full rows through the real day-change runner: trees become stumps, trunks fall later, stumps clear, saplings replant.
 WorldManager.Instance.allObjectSettings[1].changeToTileObjectOnDeath=3;WorldManager.Instance.allObjectSettings[3]=new TileObjectSettings();
 trunk.destroyTime=2.5f;NetworkMapSharer.Instance.trunkFall=2.5f;
 foreach(int crates in new[]{1,4,5,8,12,50}){ContainerManager.lookups=0;
  Reset("timing"+crates);WorldManager.Instance.itemsOnGround.Clear();collectedDrops.Clear();NetworkMapSharer.Instance.trunks.Clear();WorldManager.Instance.onTileMap[30,30]=-1;
  var rows=new List<Chest>();
  for(int c=0;c<crates;c++){var crate=new Chest{xPos=2+c*2,yPos=5};Array.Fill(crate.itemIds,-1);crate.itemIds[0]=200;crate.itemStacks[0]=1;crate.itemIds[12]=100;crate.itemStacks[12]=24;ContainerManager.manage.activeChests.Add(crate);WorldManager.Instance.onTileMap[crate.xPos,5]=412;for(int y=6;y<30;y++)WorldManager.Instance.onTileMap[crate.xPos,y]=1;rows.Add(crate);}
  uint nextDrop=1000;int trunkDrops=0;float start=Time.time;done=new[]{false};wave=RunDayChangeFarmerWaves(rows,done);int guard=0;
  var breakLog=NetworkMapSharer.Instance.breakLog;breakLog.Clear();var perStep=new List<int>();var lastBreakStep=new Dictionary<int,int>();var counts=new Dictionary<int,int>();int maxGap=0,maxSpread=0,seen=0;
  while(wave.MoveNext()){Check(guard++<20000,"Runner never finished");perStep.Add(breakLog.Count-seen);
   for(;seen<breakLog.Count;seen++){int x=breakLog[seen].x,last;if(lastBreakStep.TryGetValue(x,out last))maxGap=Math.Max(maxGap,perStep.Count-last);lastBreakStep[x]=perStep.Count;counts[x]=counts.GetValueOrDefault(x)+1;}
   if(crates<=4&&counts.Count==crates){int lo=int.MaxValue,hi=0;foreach(int n in counts.Values){lo=Math.Min(lo,n);hi=Math.Max(hi,n);}maxSpread=Math.Max(maxSpread,hi-lo);}
   Time.time+=.1f;
   foreach(var fall in NetworkMapSharer.Instance.trunks.FindAll(t=>t.at<=Time.time)){var falling=new WorldObject();falling.transform.position=new Vector3(fall.x*2,0,fall.y*2);LoggingWorldObjectDropCapturePatch.Prefix(falling,out before);WorldManager.Instance.itemsOnGround.Add(new DroppedItem{netId=nextDrop++,onTile=new Vector2Int(fall.x+1,fall.y+1)});trunkDrops++;LoggingWorldObjectDropCapturePatch.Postfix(falling,before);}
   NetworkMapSharer.Instance.trunks.RemoveAll(t=>t.at<=Time.time);}
  float elapsed=Time.time-start;
  foreach(var crate in rows){for(int y=6;y<30;y++)Check(WorldManager.Instance.onTileMap[crate.xPos,y]==2,"Row tile not replanted");Check(crate.itemStacks[12]==0,"Seeds not used");}
  Check(done[0]&&trunkDrops==24*crates&&collectedDrops.Count==trunkDrops&&WorldManager.Instance.itemsOnGround.Count==0&&pendingLoggingCollections.Count==0,"Drops not collected exactly once");
  // Every tree and stump breaks once. Each row's trees, then its stumps, break from the crate outward.
  Check(breakLog.Count==48*crates,"Break count changed: "+breakLog.Count);
  foreach(var crate in rows)foreach(int kind in new[]{1,3}){int lastY=0;foreach(var b in breakLog)if(b.x==crate.xPos&&b.old==kind){Check(b.y>lastY,"Row broke out of order");lastY=b.y;}}
  // The shared budget caps native breaks per batch: one batch per step, two per pair of steps, a partial plus five per second.
  // One crate breaks one tree per step. The old runner broke 2 per crate per step, 8 or more per step across a wave.
  int maxStep=0,maxPair=0,maxSecond=0;for(int i=0;i<perStep.Count;i++){maxStep=Math.Max(maxStep,perStep[i]);if(i>0)maxPair=Math.Max(maxPair,perStep[i]+perStep[i-1]);int second=0;for(int j=i;j<Math.Min(perStep.Count,i+10);j++)second+=perStep[j];maxSecond=Math.Max(maxSecond,second);}
  Check(maxStep<=(crates==1?1:MAX_BREAKS_PER_STEP)&&maxPair<=2*MAX_BREAKS_PER_STEP&&maxSecond<=6*MAX_BREAKS_PER_STEP,"Logging burst: "+maxStep+" breaks in one step, "+maxPair+" in two, "+maxSecond+" in one second");
  // Rows share the budget fairly: one wave's rows stay within one break of each other, and no row waits long between breaks.
  Check(maxSpread<=1&&maxGap<=30,"Unfair logging: spread "+maxSpread+", longest wait "+maxGap+" steps");
  // The budget stays in use: the run takes its break count at the budget rate, plus the last trunk fall.
  Check(elapsed<3f+1.1f*48*Math.Max(.1f,crates*LOGGING_BREAK_BATCH_SECONDS/LOGGING_BREAKS_PER_BATCH),"Day-change logging too slow: "+elapsed);
  // Pruning checks each crate once, not once per pending trunk. Per-trunk checks made 50 crates take about 606,000 lookups.
  Check(ContainerManager.lookups<crates*1500,"Chest lookups grew with pending trunks: "+ContainerManager.lookups);
  Console.WriteLine("PASS: "+crates+" crate(s) x 24 trees with a 2.5s trunk fall finished in "+elapsed.ToString("0.0")+"s; at most "+maxStep+" breaks per step, "+maxSecond+" per second, longest row wait "+maxGap+" steps; each trunk drop collected once in row order");
 }
 // A game pause (frozen game time, realtime waits still stepping), a 5s slow frame, and a 3s save hold never bank breaks.
 // Taking one row's axe or breaking another row's crate mid-run stops only that row. Every trunk drop exists once.
 {Reset("stall");WorldManager.Instance.itemsOnGround.Clear();collectedDrops.Clear();NetworkMapSharer.Instance.trunks.Clear();WorldManager.Instance.onTileMap[30,30]=-1;
  var rows=LoggingRows(8);var log=NetworkMapSharer.Instance.breakLog;log.Clear();uint firstNet=fallNet;bool[] fin={false};var run=RunDayChangeFarmerWaves(rows,fin);int step=0,seen=0,maxStep=0,pauseBreaks=0,lateBreaks=0;
  while(true){if(step==60)Time.time+=5f;SaveLoad.saveOrLoad.isSaving=step>=70&&step<100;
   if(step==120)rows[1].itemIds[0]=-1;if(step==140){ContainerManager.manage.activeChests.Remove(rows[2]);WorldManager.Instance.onTileMap[rows[2].xPos,5]=-1;}
   if(!run.MoveNext())break;Check(step++<5000,"Stalled runner never finished");
   for(int n=seen;n<log.Count;n++)if((log[n].x==rows[1].xPos&&step>120)||(log[n].x==rows[2].xPos&&step>140))lateBreaks++;
   maxStep=Math.Max(maxStep,log.Count-seen);if(step>20&&step<=50)pauseBreaks+=log.Count-seen;seen=log.Count;
   if(step<=20||step>50)Time.time+=.1f;FallTrunks();}
  SaveLoad.saveOrLoad.isSaving=false;
  Check(maxStep<=MAX_BREAKS_PER_STEP&&pauseBreaks<=MAX_BREAKS_PER_STEP,"Stall banked breaks: "+maxStep+" in one step, "+pauseBreaks+" while paused");
  Check(lateBreaks==0,"A row kept felling after its axe or crate was removed");
  for(int r=0;r<rows.Count;r++)if(r!=1&&r!=2)for(int y=6;y<30;y++)Check(WorldManager.Instance.onTileMap[rows[r].xPos,y]==2,"Row "+r+" not replanted after a stall");
  Check(fin[0]&&collectedDrops.Count+WorldManager.Instance.itemsOnGround.Count==fallNet-firstNet&&pendingLoggingCollections.Count==0,"Trunk drops not kept once through a stall");
  Console.WriteLine("PASS: a pause, a 5s slow frame, and a save hold never bank breaks ("+maxStep+" per step, "+pauseBreaks+" while paused); axe or crate removal stops only that row; every trunk drop kept once");}
 // The daytime lane with the 0.1s minimum scan interval: 24 rows, 4 per pass. A row the budget turns away goes first next pass,
 // so no row starves. Before that, rows on every other pass were turned away each time and never felled.
 {Reset("daytime");WorldManager.Instance.itemsOnGround.Clear();collectedDrops.Clear();NetworkMapSharer.Instance.trunks.Clear();WorldManager.Instance.onTileMap[30,30]=-1;
  var rows=LoggingRows(24);var log=NetworkMapSharer.Instance.breakLog;log.Clear();uint firstNet=fallNet;var plugin=new Plugin();int cursor=0,pass=0,seen=0,maxStep=0,maxGap=0;var last=new Dictionary<int,int>();
  while(true){var active=rows.FindAll(r=>HasImmediateWork(r));if(active.Count==0)break;Check(pass++<5000,"Daytime logging starved a row");plugin.DaytimeLoggingPass(active,ref cursor);maxStep=Math.Max(maxStep,log.Count-seen);
   for(;seen<log.Count;seen++){int l;if(last.TryGetValue(log[seen].x,out l))maxGap=Math.Max(maxGap,pass-l);last[log[seen].x]=pass;}
   Time.time+=.1f;FallTrunks();}
  var counted=new Dictionary<int,int>();foreach(var b in log)counted[b.x]=counted.GetValueOrDefault(b.x)+1;
  foreach(var row in rows){Check(counted.GetValueOrDefault(row.xPos)==48,"Daytime row did not break each tree and stump once");for(int y=6;y<30;y++)Check(WorldManager.Instance.onTileMap[row.xPos,y]==2,"Daytime row not replanted");}
  Check(maxStep<=MAX_BREAKS_PER_STEP&&collectedDrops.Count==fallNet-firstNet&&pendingLoggingCollections.Count==0&&maxGap<=40,"Daytime logging burst, unfair, or lost drops: "+maxStep+" per pass, longest wait "+maxGap+" passes");
  Console.WriteLine("PASS: 24 daytime rows at a 0.1s scan interval all finish in "+pass+" passes, at most "+maxStep+" breaks per pass; longest row wait "+maxGap+" passes; every trunk drop collected once");}
 HiveChecks();
 }
 static List<Chest> LoggingRows(int count){var rows=new List<Chest>();for(int c=0;c<count;c++){var crate=new Chest{xPos=2+c*2,yPos=5};Array.Fill(crate.itemIds,-1);crate.itemIds[0]=200;crate.itemStacks[0]=1;crate.itemIds[12]=100;crate.itemStacks[12]=24;ContainerManager.manage.activeChests.Add(crate);WorldManager.Instance.onTileMap[crate.xPos,5]=412;for(int y=6;y<30;y++)WorldManager.Instance.onTileMap[crate.xPos,y]=1;rows.Add(crate);}return rows;}
 static uint fallNet=50000;
 // Game: each falling trunk drops its wood at its destroy time, through WorldObject.doDrop.
 static void FallTrunks(){foreach(var fall in NetworkMapSharer.Instance.trunks.FindAll(t=>t.at<=Time.time)){var falling=new WorldObject();falling.transform.position=new Vector3(fall.x*2,0,fall.y*2);HashSet<uint> st;LoggingWorldObjectDropCapturePatch.Prefix(falling,out st);WorldManager.Instance.itemsOnGround.Add(new DroppedItem{netId=fallNet++,onTile=new Vector2Int(fall.x+1,fall.y+1)});LoggingWorldObjectDropCapturePatch.Postfix(falling,st);}NetworkMapSharer.Instance.trunks.RemoveAll(t=>t.at<=Time.time);}
 // Bee hives from felled logging trees. Real hive data: honey 225 x1, two bugs rolled Bee 227 55% / Queen Bee 228 45%.
 static InventoryItemLootTable table=new InventoryItemLootTable();
 static WorldObject breakTemplate;
 static uint nextId=5000;
 static List<int> rolled=new List<int>();
 static List<PickUpAndCarry> liveHives=new List<PickUpAndCarry>();
 public static PickUpAndCarry SpawnHive(Vector3 at){var d=new Damageable{spawnWorldObjectOnDeath=new GameObject{worldObject=breakTemplate}};var h=new PickUpAndCarry{damageable=d};d.owner=h;h.transform.position=at;liveHives.Add(h);LoggingHiveSpawnCapturePatch.Postfix(h);return h;}
 // Game: WorldObject.doDrop on the felled trunk spawns wood, then a carryable hive by prefab chance.
 static PickUpAndCarry FellTrunk(int x,int y,Vector3 hiveAt,bool fail=false,int wood=300){var t=new WorldObject();t.transform.position=new Vector3(x*2,0,y*2);HashSet<uint> st;LoggingWorldObjectDropCapturePatch.Prefix(t,out st);PickUpAndCarry h=null;
  try{WorldManager.Instance.itemsOnGround.Add(new DroppedItem{netId=nextId++,myItemId=wood,stackAmount=1,onTile=new Vector2Int(x,y),desiredPos=new Vector3(x*2,0,y*2)});if(fail)throw new InvalidOperationException("doDrop failed");h=SpawnHive(hiveAt);LoggingWorldObjectDropCapturePatch.Postfix(t,st);}finally{LoggingWorldObjectDropCapturePatch.Finalizer();}return h;}
 // Game: a frame after a hive dies its BeeHiveBreaks object starts and runs doDrop at the hive position.
 static void BreakObjectDrop(Vector3 at){var w=new WorldObject{spawnBugOnDrop=table,dropsItemOnDestroyed=breakTemplate.dropsItemOnDestroyed,dropPositions=breakTemplate.dropPositions,bugPositions=breakTemplate.bugPositions};w.transform.position=at;HashSet<uint> st;LoggingWorldObjectDropCapturePatch.Prefix(w,out st);
  try{foreach(var d in w.dropPositions)if(LoggingHiveHoneyCapturePatch.Prefix(225,1,at,null,1))NetworkMapSharer.Instance.spawnAServerDrop(225,1,at,null,true,1);
   foreach(var b in w.bugPositions){int id=table.Roll();rolled.Add(id);if(LoggingHiveBugCapturePatch.Prefix(id,at))NetworkNavMesh.nav.spawnSpecificBug(id,at);}
   LoggingWorldObjectDropCapturePatch.Postfix(w,st);}finally{LoggingWorldObjectDropCapturePatch.Finalizer();}}
 static void HiveFrames(){var broken=Game.broken.ToArray();Game.broken.Clear();foreach(var h in broken){h.gameObject=null;BreakObjectDrop(h.transform.position);}}
 static void Arrive(){var a=ConveyorAnimator.arrivals.ToArray();ConveyorAnimator.arrivals.Clear();foreach(var x in a)x?.Invoke();}
 static void HiveReset(string slot){Reset(slot);WorldManager.Instance.itemsOnGround.Clear();Game.broken.Clear();Game.nativeBugs.Clear();Game.nativeDrops.Clear();Game.credits=0;ConveyorAnimator.arrivals.Clear();rolled.Clear();table.rolls=0;liveHives.Clear();GameObject.spawned.Clear();}
 static Chest NetCrate(int netId,int durability,int x=30){var c=new Chest{xPos=x,yPos=30};Array.Fill(c.itemIds,-1);c.itemIds[0]=200;c.itemStacks[0]=100;c.itemIds[12]=100;c.itemStacks[12]=1;if(netId>=0){c.itemIds[1]=netId;c.itemStacks[1]=durability;}ContainerManager.manage.activeChests.Add(c);WorldManager.Instance.onTileMap[x,30]=412;return c;}
 static void Chop(Chest c,int x,int y){WorldManager.Instance.onTileMap[x,y]=1;Check(TryProcessFarmTileCore(c,true)>=1&&pendingLoggingCollections.ContainsKey(PosKey(x,y)),"Tree not chopped");}
 static void Collect(Chest c){Time.time+=.1f;TryProcessFarmTileCore(c,true);}
 static int Bugs(Chest c){return ChestBugs(c).Count;}
 static List<int> Sorted(IEnumerable<int> ids){var l=new List<int>(ids);l.Sort();return l;}
 // Bees stack, so one slot can hold many. Unstackable bees keep one per slot.
 static List<int> ChestBugs(Chest c){var l=new List<int>();for(int i=0;i<24;i++)if(c.itemIds[i]==227||c.itemIds[i]==228){Check(c.itemStacks[i]==1||Inventory.Instance.allItems[c.itemIds[i]].checkIfStackable(),"Unstackable bee stacked");for(int n=0;n<c.itemStacks[i];n++)l.Add(c.itemIds[i]);}return l;}
 // Every rolled output exists exactly once, in the crate or in the world. Nothing is lost or duplicated.
 static void Conserved(List<Chest> crates,int hives,string label,int preHoney=0,int preBees=0,int preQueens=0){var bugs=new List<int>(Game.nativeBugs);int honey=Game.nativeDrops.FindAll(i=>i==225).Count;
  foreach(var d in WorldManager.Instance.itemsOnGround){if(d.myItemId==225)honey+=d.stackAmount;if(d.myItemId==227||d.myItemId==228)for(int n=0;n<d.stackAmount;n++)bugs.Add(d.myItemId);}foreach(var c in crates){bugs.AddRange(ChestBugs(c));honey+=CountItemInChest(c,225);}
  for(int n=0;n<preBees;n++)Check(bugs.Remove(227),label+": lost a stored Bee");for(int n=0;n<preQueens;n++)Check(bugs.Remove(228),label+": lost a stored Queen Bee");
  Check(table.rolls==2*hives&&rolled.Count==2*hives,label+": rolls "+table.rolls);Check(string.Join(",",Sorted(bugs))==string.Join(",",Sorted(rolled))&&honey-preHoney==hives,label+": outputs not conserved");}
 static void HiveChecks(){
  var honey=new InventoryItem{name="Honey"};var bee=new InventoryItem{name="Bee",isStackable=false,bug=new UObj()};var queen=new InventoryItem{name="Queen Bee",isStackable=false,bug=new UObj()};
  var bugNet=new InventoryItem{name="Bug Net",isATool=true,hasFuel=true,myAnimType=InventoryItem.typeOfAnimation.BugNet};var compNet=new InventoryItem{name="Comp Bug Net",isATool=true,hasFuel=true,myAnimType=InventoryItem.typeOfAnimation.BugNet};
  var pickaxe=new InventoryItem{name="Pickaxe",isATool=true,hasFuel=true,myAnimType=InventoryItem.typeOfAnimation.Pickaxe};
  var allItems=Inventory.Instance.allItems;allItems[225]=honey;allItems[227]=bee;allItems[228]=queen;allItems[2]=bugNet;allItems[1101]=compNet;allItems[3]=pickaxe;
  allItems[200].isATool=true;allItems[200].myAnimType=InventoryItem.typeOfAnimation.Axe;allItems[100].isStackable=true;
  table.itemsInLootTable=new[]{bee,queen};realVacuum=true;allItems[300]=new InventoryItem{name="Wood"};allItems[310]=new InventoryItem{name="Hard Wood"};
  // Plugin.ApplyStackabilityOverrides makes the hive bees stackable before any hive is collected.
  new Plugin().RunStacking();Check(bee.checkIfStackable()&&queen.checkIfStackable(),"Hive bees not stackable");
  breakTemplate=new WorldObject{spawnBugOnDrop=table,dropsItemOnDestroyed=honey,dropPositions=new[]{new Transform()},bugPositions=new[]{new Transform(),new Transform()}};
  WorldManager.Instance.allObjectSettings[1]=new TileObjectSettings{dropsObjectOnDeath=new GameObject{worldObject=new WorldObject()},changeToTileObjectOnDeath=-1};
  NetworkMapSharer.Instance.trunkFall=0;
  // Both net types opt in at full and zero durability. The net is never charged.
  foreach(var net in new[]{(2,150),(2,0),(1101,600),(1101,0)}){HiveReset("hive-net"+net.Item1+"-"+net.Item2);var c=NetCrate(net.Item1,net.Item2);Chop(c,30,31);Time.time+=1;var hive=FellTrunk(30,31,new Vector3(61,0,62));
   Check(pendingLoggingCollections[PosKey(30,31)].hives.Count==1&&hive.damageable.health==5,"Hive not tracked");Collect(c);Check(hive.damageable.health==0&&Game.broken.Count==1&&table.rolls==0,"Hive not broken through its death");
   HiveFrames();Check(ConveyorAnimator.arrivals.Count==4&&Game.nativeBugs.Count==0&&Game.nativeDrops.Count==0&&CountItemInChest(c,300)==1,"Wood or outputs not taken");Arrive();
   Check(CountItemInChest(c,225)==1&&Bugs(c)==2&&c.itemIds[1]==net.Item1&&c.itemStacks[1]==net.Item2&&Game.credits==1,"Net "+net+" crate outputs wrong");Conserved(new List<Chest>{c},1,"net "+net);
   Check(pendingLoggingHiveBreaks.Count==0&&activeLoggingHiveBreak==null&&loggingHiveSpawnOwner==null,"Hive context leaked");}
  Console.WriteLine("PASS: Bug Net and Comp Bug Net at full and zero durability break the hive once; honey and both rolled bees reach the crate; durability unchanged");
  // No net, or a tool that is not a net: the hive stays whole for the player and logging is unchanged.
  foreach(int tool in new[]{-1,3}){HiveReset("hive-off"+tool);var c=NetCrate(tool,10);Chop(c,30,31);Time.time+=1;var hive=FellTrunk(30,31,new Vector3(61,0,62));Collect(c);
   Check(hive.damageable.health==5&&Game.broken.Count==0&&table.rolls==0&&pendingLoggingCollections.Count==0&&collections>=1,"Hive touched without a net");}
  Console.WriteLine("PASS: without a bug net the hive stays whole and the collection finishes as before");
  // Adjacent rows: each crate owns only hives from its own trunks, wherever they land.
  foreach(bool bothNets in new[]{false,true}){HiveReset("hive-adjacent"+bothNets);var a=NetCrate(2,150,30);var b=NetCrate(bothNets?1101:-1,600,32);Chop(a,30,31);Chop(b,32,31);Time.time+=1;
   var hiveA=FellTrunk(30,31,new Vector3(64,0,62));var hiveB=FellTrunk(32,31,new Vector3(60,0,62));
   Check(pendingLoggingCollections[PosKey(30,31)].hives[0].carry==hiveA&&pendingLoggingCollections[PosKey(32,31)].hives[0].carry==hiveB,"Hive owned by the wrong row");
   pendingLoggingCollections[PosKey(32,31)].collectAt=Time.time+10;float bCollectAt=pendingLoggingCollections[PosKey(32,31)].collectAt;
   Collect(a);HiveFrames();Arrive();Check(pendingLoggingCollections[PosKey(32,31)].collectAt==bCollectAt,"Hive break released a neighbor's trunk wait");
   Check(hiveA.damageable.health==0&&hiveB.damageable.health==5&&CountItemInChest(a,225)==1&&Bugs(a)==2&&CountItemInChest(b,225)==0,"Row A took the wrong outputs");
   Time.time+=10;Collect(b);HiveFrames();Arrive();Check(hiveB.damageable.health==(bothNets?0:5)&&CountItemInChest(b,225)==(bothNets?1:0)&&Bugs(b)==(bothNets?2:0)&&CountItemInChest(a,225)==1&&Bugs(a)==2,"Row B outputs wrong");
   Conserved(new List<Chest>{a,b},bothNets?2:1,"adjacent "+bothNets);}
  Console.WriteLine("PASS: adjacent rows keep their own hives; a neighbor's break never releases a pending trunk early");
  // Capacity: honey + 2 bees need 3 empty slots, or 2 when honey is already stacked.
  foreach(var room in new[]{(0,false,false),(2,false,false),(2,true,true),(3,false,true)}){HiveReset("hive-room"+room);var c=NetCrate(2,150);for(int i=2;i<24;i++)if(i!=12){c.itemIds[i]=300;c.itemStacks[i]=1;}
   for(int i=0;i<room.Item1;i++)c.itemIds[23-i]=-1;if(room.Item2){c.itemIds[2]=225;c.itemStacks[2]=4;}allItems[300]=new InventoryItem();
   Chop(c,30,31);Time.time+=1;var hive=FellTrunk(30,31,new Vector3(61,0,62));Collect(c);HiveFrames();Arrive();
   Check(hive.damageable.health==(room.Item3?0:5),"Capacity "+room+" decided wrong");
   if(!room.Item3){Check(table.rolls==0&&pendingLoggingCollections[PosKey(30,31)].waitingForRoom,"Full crate rolled outputs or dropped the hive");Collect(c);Check(hive.damageable.health==5,"Hive broke without room");
    for(int i=0;i<3;i++)c.itemIds[20-i]=-1;Collect(c);HiveFrames();Arrive();Collect(c);Check(hive.damageable.health==0&&pendingLoggingCollections.Count==0,"Waiting hive did not break once room existed");}
   Check(CountItemInChest(c,225)==(room.Item2?5:1)&&Bugs(c)==2,"Honey did not stack");Conserved(new List<Chest>{c},1,"room "+room,room.Item2?4:0);}
  Console.WriteLine("PASS: a full or partly full crate keeps the hive whole until room exists, then breaks it once; existing honey stacks; exact room breaks it");
  // Room lost between the break and the roll: outputs stay saved pickups the crate still owns, never live bees.
  // Outputs that rolled are already stored, so filling the crate later cannot spill them.
  foreach(bool afterRoll in new[]{false,true}){HiveReset("hive-full"+afterRoll);var c=NetCrate(1101,0);Chop(c,30,31);Time.time+=1;var hive=FellTrunk(30,31,new Vector3(61,0,62));Collect(c);
   if(afterRoll)HiveFrames();var freed=new List<int>();for(int i=0;i<24;i++)if(c.itemIds[i]==-1){c.itemIds[i]=301;c.itemStacks[i]=1;freed.Add(i);}allItems[301]=new InventoryItem();if(!afterRoll)HiveFrames();Arrive();
   int held=WorldManager.Instance.itemsOnGround.FindAll(d=>d.saveDrop).Count;
   Check(Game.nativeBugs.Count==0&&Game.nativeDrops.Count==0&&held==(afterRoll?0:3)&&c.itemStacks[1]==0,"Full crate spilled outputs");Conserved(new List<Chest>{c},1,"full "+afterRoll);
   Collect(c);Check(afterRoll?pendingLoggingCollections.Count==0:pendingLoggingCollections[PosKey(30,31)].waitingForRoom,"Held outputs released ownership");
   foreach(int i in freed.GetRange(0,3))c.itemIds[i]=-1;Collect(c);Collect(c);Check(CountItemInChest(c,225)==1&&Bugs(c)==2&&WorldManager.Instance.itemsOnGround.Count==0&&pendingLoggingCollections.Count==0,"Held outputs never reached the crate");Conserved(new List<Chest>{c},1,"full later "+afterRoll);}
  Console.WriteLine("PASS: a crate that fills before the roll holds honey and bees as saved pickups and stores them once room exists; stored outputs never spill");
  // Net removed, crate replaced, or crate carried mid-fall; hive carried or pushed; guest/off-island host.
  string[] cases={"net","replace","carry","hive-carried","hive-moved","guest","crate-gone-after-break","net-gone-after-break"};
  foreach(string change in cases){HiveReset("hive-"+change);var c=NetCrate(2,150);Chop(c,30,31);Time.time+=1;var hive=FellTrunk(30,31,new Vector3(61,0,62));
   if(change=="net")c.itemIds[1]=-1;if(change=="replace"){ContainerManager.manage.activeChests.Clear();NetCrate(2,150);}if(change=="carry"){ContainerManager.manage.activeChests.Clear();WorldManager.Instance.onTileMap[30,30]=-1;}
   if(change=="hive-carried")hive.beingCarriedBy=7;if(change=="hive-moved")hive.transform.position=new Vector3(64,0,62);if(change=="guest")Plugin.allowed=false;
   if(change.EndsWith("after-break")){Collect(c);if(change.StartsWith("crate")){ContainerManager.manage.activeChests.Clear();WorldManager.Instance.onTileMap[30,30]=-1;}else c.itemIds[1]=-1;HiveFrames();Arrive();
    var live=ContainerManager.manage.activeChests;Check(hive.damageable.health==0&&(change.StartsWith("crate")?Game.nativeBugs.Count==2&&Game.nativeDrops.Count==1:Bugs(c)==2),"After-break change "+change);Conserved(live.Count>0?live:new List<Chest>(),1,change);continue;}
   Collect(c);HiveFrames();Arrive();Check(hive.damageable.health==5&&table.rolls==0,"Hive broken after "+change);
   if(change=="guest"){Plugin.allowed=true;activeLoggingHiveBreak=new LoggingHiveBreak{chest=c};Plugin.allowed=false;Check(!TryTakeLoggingHiveOutput(227,1,new Vector3(),-1,true),"Guest took output");activeLoggingHiveBreak=null;Plugin.allowed=true;}}
  Console.WriteLine("PASS: net/crate removal or replacement mid-fall, moved or carried hives, and guests leave the hive whole; changes after the break keep every output");
  // Untracked hives and context cleanup: player hives, hives outside a trunk drop, a throwing doDrop, save/load reset.
  HiveReset("hive-untracked");var crate=NetCrate(2,150);var wild=SpawnHive(new Vector3(61,0,62));BreakObjectDrop(new Vector3(61,0,62));Check(Game.nativeBugs.Count==2&&Game.nativeDrops.Count==1&&Bugs(crate)==0,"Player hive break taken");
  Chop(crate,30,31);Time.time+=1;try{FellTrunk(30,31,new Vector3(61,0,62),fail:true);Check(false,"No throw");}catch(InvalidOperationException){}
  var stray=SpawnHive(new Vector3(61,0,62));Check(pendingLoggingCollections[PosKey(30,31)].hives.Count==0&&loggingHiveSpawnOwner==null,"Context leaked after a throwing doDrop");
  HiveReset("hive-save");crate=NetCrate(2,150);Chop(crate,30,31);Time.time+=1;var saved=FellTrunk(30,31,new Vector3(61,0,62));Collect(crate);ClearVisuals();HiveFrames();Arrive();
  Check(saved.damageable.health==0&&Game.nativeBugs.Count==2&&Game.nativeDrops.Count==1&&Bugs(crate)==0&&pendingLoggingHiveBreaks.Count==0,"Save reset kept a hive break");
  HiveReset("hive-expired");crate=NetCrate(2,150);Chop(crate,30,31);Time.time+=1;FellTrunk(30,31,new Vector3(61,0,62));Collect(crate);Time.time+=LOGGING_HIVE_BREAK_EXPIRY_SECONDS+1;HiveFrames();Arrive();Check(Game.nativeBugs.Count==2&&Bugs(crate)==0,"Expired break taken");
  Console.WriteLine("PASS: untracked hives keep native drops; a throwing doDrop, a save reset, or an expired break leaves no stale capture");
  // A player's hive break on a pending trunk's tile is not a trunk drop: the wait stays and the outputs stay in the world.
  HiveReset("hive-player-on-trunk");crate=NetCrate(2,150);Chop(crate,30,31);float trunkWait=pendingLoggingCollections[PosKey(30,31)].collectAt;BreakObjectDrop(new Vector3(60,0,62));
  Check(pendingLoggingCollections[PosKey(30,31)].collectAt==trunkWait&&Game.nativeDrops.Count==1&&Game.nativeBugs.Count==2&&Bugs(crate)==0,"Player hive break ended a trunk wait");
  Console.WriteLine("PASS: a player's hive break on a pending trunk's tile keeps the trunk wait and its native outputs");
  // Two hives collected in one pass share the crate's room: room for one breaks one and leaves the other whole.
  HiveReset("hive-shared-room");crate=NetCrate(2,150);for(int i=2;i<24;i++)if(i!=12){crate.itemIds[i]=300;crate.itemStacks[i]=1;}for(int i=0;i<3;i++)crate.itemIds[23-i]=-1;allItems[300]=new InventoryItem();
  foreach(int y in new[]{31,32})pendingLoggingCollections[PosKey(30,y)]=new PendingLoggingCollection{chestX=30,chestY=30,sourceX=30,sourceY=y,collectAt=Time.time+5,expiresAt=Time.time+40};
  var first=FellTrunk(30,31,new Vector3(61,0,62));var second=FellTrunk(30,32,new Vector3(61,0,64));Collect(crate);HiveFrames();Arrive();
  Check(first.damageable.health+second.damageable.health==0&&Game.nativeBugs.Count==0&&Game.nativeDrops.Count==0&&Bugs(crate)==4&&CountItemInChest(crate,225)==2&&table.rolls==4,"Two stacking hives did not share three slots");Conserved(new List<Chest>{crate},2,"shared room");
  Check(Slots(crate,225)==1&&Slots(crate,227)<=1&&Slots(crate,228)<=1,"Stacked output used an extra slot");
  Console.WriteLine("PASS: two hives in one pass share honey, Bee, and Queen Bee slots; three empty slots fit both");
  // Two hives in one collection also share claimed types.
  HiveReset("hive-one-collection");crate=RoomCrate(3,new(int,int)[0]);Chop(crate,30,31);Time.time+=1;var one=FellTrunk(30,31,new Vector3(61,0,62));var two=SpawnHive(new Vector3(61,0,64));pendingLoggingCollections[PosKey(30,31)].hives.Add(new LoggingHive{carry=two,spawnedAt=two.transform.position});
  Collect(crate);HiveFrames();Arrive();Check(one.damageable.health+two.damageable.health==0&&Bugs(crate)==4&&CountItemInChest(crate,225)==2,"One collection's hives did not share slots");Conserved(new List<Chest>{crate},2,"one collection");
  // A logging crate keeps its net when exporting; other crates and other items are unchanged.
  HiveReset("hive-protect");crate=NetCrate(2,150);Check(RealGetProtectedAutoFarmerAmount(crate,2)==150&&RealGetProtectedAutoFarmerAmount(crate,225)==0,"Net not kept");crate.itemIds[0]=-1;Check(RealGetProtectedAutoFarmerAmount(crate,2)==0,"Net kept without an axe");
  Console.WriteLine("PASS: an axe crate keeps its bug net out of network exports; honey and bees still export");
  // A full crate that conveyors empty later: wood and hive wait in the world, felling pauses, and the day-change run still ends.
  {HiveReset("hive-backpressure");var c=RoomCrate(0,new(int,int)[0]);Chop(c,30,31);loggingRowLengthByCrate[PosKey(30,30)]=2;Time.time+=1;var hive=FellTrunk(30,31,new Vector3(61,0,62),wood:310);
   WorldManager.Instance.onTileMap[30,32]=1;int writes=NetworkMapSharer.Instance.writes;Collect(c);
   var waiting=pendingLoggingCollections[PosKey(30,31)];var wood=WorldManager.Instance.itemsOnGround.Find(d=>d.myItemId==310);
   Check(waiting.waitingForRoom&&wood!=null&&wood.saveDrop&&hive.damageable.health==5&&NetworkMapSharer.Instance.writes==writes,"Full crate did not hold wood and hive, or kept felling");
   bool[] bfin={false};var brun=RunDayChangeFarmerWaves(new List<Chest>{c},bfin);int steps=0;while(brun.MoveNext()&&steps++<10){}Check(bfin[0]&&NetworkMapSharer.Instance.writes==writes,"Waiting crate held the day-change run open or felled");
   // A later drop beside the waiting stump, such as a player's, stays free.
   var playerDrop=new DroppedItem{netId=Game.nextNet++,myItemId=300,stackAmount=1,onTile=new Vector2Int(30,31)};WorldManager.Instance.itemsOnGround.Add(playerDrop);Check(FindLoggingDropOwner(playerDrop)==null,"Waiting collection claimed a new nearby drop");WorldManager.Instance.itemsOnGround.Remove(playerDrop);
   Time.time+=120;Collect(c);Check(pendingLoggingCollections.ContainsKey(PosKey(30,31))&&FindLoggingDropOwner(wood)==waiting,"Waiting collection expired");
   // Conveyors empty four slots: the wood and the hive's outputs go in, then felling resumes.
   for(int i=0;i<4;i++)c.itemIds[20-i]=-1;Collect(c);HiveFrames();Arrive();Collect(c);
   Check(CountItemInChest(c,310)==1&&hive.damageable.health==0&&WorldManager.Instance.itemsOnGround.Count==0&&pendingLoggingCollections.ContainsKey(PosKey(30,32))&&NetworkMapSharer.Instance.writes>writes,"Room did not resume collection and felling");Conserved(new List<Chest>{c},1,"backpressure");}
  Console.WriteLine("PASS: a full crate holds its wood and hive as saved, owned outputs, pauses felling, lets the day-change run end, and finishes once conveyors free room");
  // Two trunks in one pass, each with a new wood type and a hive. Wood is stored first, so hive room is exact.
  // A second, different wood type cannot take the slots a broken hive still needs; it waits instead.
  allItems[311]=new InventoryItem{name="Bottle Tree Wood"};
  foreach(var room in new[]{(4,2,310),(3,0,310),(4,2,311)}){HiveReset("hive-wood-room"+room.Item1+room.Item3);var c=RoomCrate(room.Item1,new(int,int)[0]);
   foreach(int y in new[]{31,32})pendingLoggingCollections[PosKey(30,y)]=new PendingLoggingCollection{chestX=30,chestY=30,sourceX=30,sourceY=y,collectAt=Time.time+5,expiresAt=Time.time+40};
   var h1=FellTrunk(30,31,new Vector3(61,0,62),wood:310);var h2=FellTrunk(30,32,new Vector3(61,0,64),wood:room.Item3);Collect(c);HiveFrames();Arrive();var ground=WorldManager.Instance.itemsOnGround;int heldWood=room.Item3==311?1:0;
   int broken=(h1.damageable.health==0?1:0)+(h2.damageable.health==0?1:0);
   Check(broken==room.Item2&&CountItemInChest(c,310)+CountItemInChest(c,311)==2-heldWood&&Game.nativeBugs.Count==0&&Game.nativeDrops.Count==0&&ground.Count==heldWood&&(heldWood==0||ground[0].myItemId==311&&ground[0].saveDrop),"Wood took a broken hive's room: "+broken);
   if(heldWood==1){c.itemIds[20]=-1;Collect(c);Check(CountItemInChest(c,311)==1&&ground.Count==0&&pendingLoggingCollections.Count==0,"Held wood never stored");}Conserved(new List<Chest>{c},broken,"wood room "+room.Item1);
   if(broken==0){for(int i=0;i<2;i++)c.itemIds[20-i]=-1;Collect(c);HiveFrames();Arrive();Collect(c);Check(h1.damageable.health+h2.damageable.health==0&&pendingLoggingCollections.Count==0,"Waiting hives never broke");Conserved(new List<Chest>{c},2,"wood room later");}}
  Console.WriteLine("PASS: wood stored in the same pass counts against hive room; two hives share slots; too little room keeps both whole until room exists");
  // While waiting: removing the net leaves the hive whole for the player; removing the crate leaves saved wood and a whole hive.
  foreach(string change in new[]{"net","crate"}){HiveReset("hive-wait-"+change);var c=RoomCrate(0,new(int,int)[0]);Chop(c,30,31);Time.time+=1;var hive=FellTrunk(30,31,new Vector3(61,0,62),wood:310);Collect(c);
   Check(pendingLoggingCollections[PosKey(30,31)].waitingForRoom,"Full crate not waiting");
   if(change=="net"){c.itemIds[1]=300;Collect(c);Check(pendingLoggingCollections[PosKey(30,31)].hives.Count==0,"Netless crate kept the hive");c.itemIds[20]=-1;Collect(c);Check(CountItemInChest(c,310)==1&&pendingLoggingCollections.Count==0,"Wood never stored");}
   else{ContainerManager.manage.activeChests.Clear();WorldManager.Instance.onTileMap[30,30]=-1;PruneExpiredLoggingCollections();var left=WorldManager.Instance.itemsOnGround.Find(d=>d.myItemId==310);Check(pendingLoggingCollections.Count==0&&left!=null&&left.saveDrop,"Removed crate lost its held wood");}
   Check(hive.damageable.health==5&&table.rolls==0,"Hive broke after "+change+" removal");}
  Console.WriteLine("PASS: removing the net or crate while waiting leaves the hive whole and the wood saved in the world");
  // Day-change logging waits while the game saves, then finishes its work.
  {HiveReset("save-wait");var sc=NetCrate(2,150);WorldManager.Instance.onTileMap[30,31]=1;SaveLoad.saveOrLoad.isSaving=true;bool[] sfin={false};var srun=RunDayChangeFarmerWaves(new List<Chest>{sc},sfin);for(int i=0;i<5;i++)srun.MoveNext();
   Check(!sfin[0]&&NetworkMapSharer.Instance.writes==0,"Day-change logging ran during a save");SaveLoad.saveOrLoad.isSaving=false;int sg=0;while(srun.MoveNext()&&sg++<100)Time.time+=.1f;
   Check(sfin[0]&&NetworkMapSharer.Instance.writes>0&&pendingLoggingCollections.Count==0,"Day-change logging abandoned work after a save");}
  Console.WriteLine("PASS: day-change logging waits through a save and then finishes");
  // Seed lookup: the lowest seed item wins each growth stage, as the full item scan did. Load clears the map.
  {seedItems.UnionWith(new[]{150,160});var s7=new TileObject{tileObjectId=7};var s8=new TileObject{tileObjectId=8,tileObjectGrowthStages=new TileObjectGrowthStages{changeToWhenGrown=s7}};var s9=new TileObject{tileObjectId=9,tileObjectGrowthStages=new TileObjectGrowthStages{changeToWhenGrown=s7}};
   allItems[150]=new InventoryItem{placeable=s9};allItems[160]=new InventoryItem{placeable=s8};ClearVisuals();
   Check(RealFindLoggingSeedForTreeTile(7)==150&&RealFindLoggingSeedForTreeTile(8)==160&&RealFindLoggingSeedForTreeTile(9)==150&&RealFindLoggingSeedForTreeTile(1)==-1,"Seed map changed first-match order");
   allItems[150]=new InventoryItem{placeable=s8};ClearVisuals();Check(RealFindLoggingSeedForTreeTile(9)==-1&&RealFindLoggingSeedForTreeTile(8)==150,"Load did not rebuild the seed map");
   seedItems.Clear();allItems[150]=null;allItems[160]=null;ClearVisuals();}
  Console.WriteLine("PASS: the seed lookup keeps first-match order and rebuilds after load");
  // An idle row skips the fence query; a fence still stops felling past it.
  {HiveReset("fence");var fc=NetCrate(-1,0);loggingRowLengthByCrate[PosKey(30,30)]=3;loggingDirectionByCrate[PosKey(30,30)]=new Vector2Int(0,1);for(int y=31;y<=33;y++)WorldManager.Instance.onTileMap[30,y]=5;
   LoggingActionType a;int x,y2,t,sl,sd;FarmEnclosureHelper.calls=0;Check(!TryFindNextLoggingAction(fc,out a,out x,out y2,out t,out sl,out sd)&&FarmEnclosureHelper.calls==0,"Idle row ran the fence query");
   WorldManager.Instance.onTileMap[30,33]=1;FarmEnclosureHelper.fenced=(fx,fy)=>fy>=33;Check(!TryFindNextLoggingAction(fc,out a,out x,out y2,out t,out sl,out sd)&&FarmEnclosureHelper.calls>0,"Felled past the fence");
   FarmEnclosureHelper.fenced=null;Check(TryFindNextLoggingAction(fc,out a,out x,out y2,out t,out sl,out sd)&&a==LoggingActionType.Chop&&y2==33,"Tree inside the fence not felled");}
  Console.WriteLine("PASS: idle logging rows skip the fence query; fences still stop felling");
  // 50 rows x 24 trees, every trunk drops a hive. Hive breaks share the tree and stump budget, and every row still finishes.
  HiveReset("hive-50");var rows=new List<Chest>();for(int r=0;r<50;r++){var row=new Chest{xPos=2+r*2,yPos=5};Array.Fill(row.itemIds,-1);row.itemIds[0]=200;row.itemStacks[0]=1;row.itemIds[1]=2;row.itemStacks[1]=0;row.itemIds[12]=100;row.itemStacks[12]=24;ContainerManager.manage.activeChests.Add(row);WorldManager.Instance.onTileMap[row.xPos,5]=412;for(int y=6;y<30;y++)WorldManager.Instance.onTileMap[row.xPos,y]=1;rows.Add(row);}
  WorldManager.Instance.allObjectSettings[1].changeToTileObjectOnDeath=3;NetworkMapSharer.Instance.trunkFall=2.5f;NetworkMapSharer.Instance.trunks.Clear();var hives=new List<PickUpAndCarry>();bool[] fin={false};var run=RunDayChangeFarmerWaves(rows,fin);float begin=Time.time;int frames=0;var hiveBreakLog=NetworkMapSharer.Instance.breakLog;hiveBreakLog.Clear();int seenBreaks=0,maxHiveStep=0;
  while(run.MoveNext()){Check(frames++<5000,"Hive runner never finished");maxHiveStep=Math.Max(maxHiveStep,hiveBreakLog.Count-seenBreaks+Game.broken.Count);seenBreaks=hiveBreakLog.Count;Time.time+=.1f;HiveFrames();Arrive();foreach(var fall in NetworkMapSharer.Instance.trunks.FindAll(t=>t.at<=Time.time))hives.Add(FellTrunk(fall.x,fall.y,new Vector3(fall.x*2+1,0,fall.y*2)));NetworkMapSharer.Instance.trunks.RemoveAll(t=>t.at<=Time.time);}
  HiveFrames();Arrive();float took=Time.time-begin;int brokenCount=hives.FindAll(h=>h.damageable.health==0).Count;
  foreach(var row in rows)for(int y=6;y<30;y++)Check(WorldManager.Instance.onTileMap[row.xPos,y]==2,"Hive row not replanted");
  Check(fin[0]&&hives.Count==1200&&brokenCount>0&&took<3f+1.1f*(2400+brokenCount)*LOGGING_BREAK_BATCH_SECONDS/LOGGING_BREAKS_PER_BATCH,"Hive rows slowed: "+took);Conserved(rows,brokenCount,"50 rows");
  Check(maxHiveStep<=MAX_BREAKS_PER_STEP,"Tree, stump, and hive breaks burst: "+maxHiveStep+" in one step");
  Console.WriteLine("PASS: 50 rows x 24 hive trees finished in "+took.ToString("0.0")+"s with at most "+maxHiveStep+" tree, stump, or hive breaks per step; "+brokenCount+" hives broken while room lasted, "+(1200-brokenCount)+" left whole; every output kept once");
  StackChecks(honey,bee,queen,bugNet,compNet,pickaxe);
  PersistChecks();
 }
 static Chest RoomCrate(int empty,(int id,int stack)[] stored){var c=NetCrate(2,150);for(int i=2;i<24;i++)if(i!=12){c.itemIds[i]=300;c.itemStacks[i]=1;}for(int i=0;i<empty;i++)c.itemIds[23-i]=-1;for(int i=0;i<stored.Length;i++){c.itemIds[2+i]=stored[i].id;c.itemStacks[2+i]=stored[i].stack;}Inventory.Instance.allItems[300]=new InventoryItem();return c;}
 static int Stored((int id,int stack)[] stored,int id){int n=0;foreach(var s in stored)if(s.id==id)n+=s.stack;return n;}

 // Game save order (SaveAndLoad.SaveCoroutine): RuntimeState.ResetForSave runs PrepareLoggingForSave, then in the same
 // frame CarrySave and DropSaves store live carryables and saveDrop drops. The map follows over frames, then the chests.
 static List<(int id,int stack,Vector3 at)> savedDrops; static List<(int prefab,Vector3 at)> savedCarry; static List<Chest> savedChests;
 static Chest CopyChest(Chest c){var copy=new Chest{xPos=c.xPos,yPos=c.yPos};Array.Copy(c.itemIds,copy.itemIds,24);Array.Copy(c.itemStacks,copy.itemStacks,24);return copy;}
 static void SaveStart(){SaveLoad.saveOrLoad.isSaving=true;PrepareLoggingForSave();
  savedDrops=new List<(int,int,Vector3)>();foreach(var d in WorldManager.Instance.itemsOnGround)if(d.saveDrop)savedDrops.Add((d.myItemId,d.stackAmount,new Vector3((int)d.desiredPos.x,(int)d.desiredPos.y,(int)d.desiredPos.z)));
  savedCarry=new List<(int,Vector3)>();foreach(var h in liveHives)if(h.gameObject!=null&&!h.delivered)savedCarry.Add((h.prefabId,h.transform.position));}
 static void SaveChests(){savedChests=ContainerManager.manage.activeChests.ConvertAll(CopyChest);SaveLoggingRows();}
 // The first host update after the save: the logging clock restarts and work held for the save runs.
 static void SaveEnd(){SaveLoad.saveOrLoad.isSaving=false;UpdateVisuals();RuntimeState.RunQueuedAfterSave();}
 // Load: RuntimeState.ResetForLoad, then NetworkMapSharer.OnStartServer respawns saved drops and carryables, then the first host frame.
 static Chest Reload(string slot=null){ClearVisuals();if(slot!=null)SaveLoad.saveOrLoad.slot=Path.Combine(Environment.CurrentDirectory,slot);
  WorldManager.Instance.itemsOnGround.Clear();liveHives.Clear();Game.broken.Clear();Game.nativeBugs.Clear();Game.nativeDrops.Clear();ConveyorAnimator.arrivals.Clear();GameObject.spawned.Clear();rolled.Clear();table.rolls=0;
  ContainerManager.manage.activeChests.Clear();ContainerManager.manage.activeChests.AddRange(savedChests.ConvertAll(CopyChest));
  LoggingRestorePatch.Prefix();try{foreach(var d in savedDrops)NetworkMapSharer.Instance.spawnAServerDropToSave(d.id,d.stack,d.at);}finally{LoggingRestorePatch.Finalizer();}
  LoggingRestorePatch.Prefix();try{foreach(var h in savedCarry)NetworkMapSharer.Instance.spawnACarryable(SaveLoad.saveOrLoad.carryablePrefabs[h.prefab],h.at,false);}finally{LoggingRestorePatch.Finalizer();}
  UpdateVisuals();return ContainerManager.manage.activeChests[0];}
 static void PersistChecks(){
  // Felled trees leave no stump here, so each scenario has one collection. The 50-row run left stumps on.
  WorldManager.Instance.allObjectSettings[1].changeToTileObjectOnDeath=-1;NetworkMapSharer.Instance.trunkFall=0;
  // Full crate: held wood, a duplicate wood record, and a whole hive survive a long save and a reload. The player's equal
  // wood record on the same spot and the player's own hive stay the player's. Each output is stored exactly once.
  {HiveReset("persist-full");var c=RoomCrate(0,new(int,int)[0]);Chop(c,30,31);Time.time+=1;var hive=FellTrunk(30,31,new Vector3(61,0,62),wood:310);Collect(c);
   var waiting=pendingLoggingCollections[PosKey(30,31)];var wood=WorldManager.Instance.itemsOnGround.Find(d=>d.myItemId==310);
   var player=new DroppedItem{netId=Game.nextNet++,myItemId=310,stackAmount=1,saveDrop=true,onTile=wood.onTile,desiredPos=wood.desiredPos};
   var twin=new DroppedItem{netId=Game.nextNet++,myItemId=310,stackAmount=1,saveDrop=true,onTile=wood.onTile,desiredPos=wood.desiredPos};
   WorldManager.Instance.itemsOnGround.Add(player);WorldManager.Instance.itemsOnGround.Add(twin);waiting.spawnedDropIds.Add(twin.netId);var wild=SpawnHive(new Vector3(70,0,62));
   Check(waiting.waitingForRoom&&hive.damageable.health==5&&FindLoggingDropOwner(player)==null,"Full crate setup wrong");
   SaveStart();Time.time+=90;SaveChests();SaveEnd();
   Check(savedDrops.Count==3&&savedCarry.Count==2&&pendingLoggingCollections.ContainsKey(PosKey(30,31))&&FindLoggingDropOwner(twin)==waiting,"A long save released the held wood or hive");
   for(int i=0;i<4;i++)c.itemIds[20-i]=-1;Collect(c);HiveFrames();Arrive();Collect(c);var ground=WorldManager.Instance.itemsOnGround;
   Check(CountItemInChest(c,310)==2&&ground.Count==1&&ground[0]==player&&wild.damageable.health==5&&pendingLoggingCollections.Count==0,"Live: owned wood not stored once, or the player's wood or hive taken");Conserved(new List<Chest>{c},1,"persist live");
   var r=Reload();var restored=pendingLoggingCollections[PosKey(30,31)];ground=WorldManager.Instance.itemsOnGround;
   // The game stacks the three equal records into one drop as it loads. The crate's two split off; the player's one stays.
   var left=ground.Find(d=>FindLoggingDropOwner(d)==null);var owned=ground.Find(d=>FindLoggingDropOwner(d)==restored);
   Check(restored.waitingForRoom&&ground.Count==2&&left!=null&&left.stackAmount==1&&owned!=null&&owned.stackAmount==2&&owned.saveDrop&&restored.hives.Count==1&&restored.hives[0].carry!=liveHives.Find(h=>h.transform.position.x==70),"Reload did not restore exactly the owned wood and hive");
   var rhive=restored.hives[0].carry;Collect(r);Check(rhive.damageable.health==5&&CountItemInChest(r,310)==0,"Reloaded full crate took outputs without room");
   for(int i=0;i<4;i++)r.itemIds[20-i]=-1;Collect(r);HiveFrames();Arrive();Collect(r);
   Check(CountItemInChest(r,310)==2&&ground.Count==1&&ground[0]==left&&rhive.damageable.health==0&&liveHives.FindAll(h=>h.damageable.health==5).Count==1&&pendingLoggingCollections.Count==0,"Reload: owned outputs not stored once, or the player's taken");Conserved(new List<Chest>{r},1,"persist reload");
   // Another save slot has no held outputs, and this slot's records never reach it.
   Reload("persist-other-slot");Collect(ContainerManager.manage.activeChests[0]);
   Check(pendingLoggingCollections.Count==0&&WorldManager.Instance.itemsOnGround.TrueForAll(d=>FindLoggingDropOwner(d)==null)&&liveHives.TrueForAll(h=>h.damageable.health==5)&&table.rolls==0,"Another slot inherited held outputs");}
  Console.WriteLine("PASS: a full crate's wood and hive survive a 90s save and a reload once; equal player records and hives stay the player's; other slots own nothing");
  // A hive breaks one frame before the save, before or after the game destroys it. Its outputs roll during the save.
  foreach(bool destroyed in new[]{false,true}){HiveReset("persist-break"+destroyed);var c=NetCrate(2,150);Chop(c,30,31);Time.time+=1;var hive=FellTrunk(30,31,new Vector3(61,0,62));Collect(c);
   Check(hive.damageable.health==0&&Game.broken.Count==1&&table.rolls==0,"Hive not broken before the save");if(destroyed)hive.gameObject=null;
   SaveStart();Time.time+=60;HiveFrames();
   Check(table.rolls==2&&Bugs(c)==0&&CountItemInChest(c,225)==0&&Game.nativeBugs.Count==0&&Game.nativeDrops.Count==0,"Hive outputs moved or spilled during the save");
   SaveChests();SaveEnd();Arrive();
   Check(savedCarry.Count==(destroyed?0:1)&&CountItemInChest(savedChests[0],225)==0&&Bugs(savedChests[0])==0,"Snapshot holds the hive and its outputs together");
   Check(CountItemInChest(c,225)==1&&Bugs(c)==2,"Live: outputs held for the save never landed");Conserved(new List<Chest>{c},1,"break live "+destroyed);
   var r=Reload();Check(pendingLoggingCollections[PosKey(30,31)].hives.Count==1&&liveHives.Count==1&&table.rolls==0,"Reload did not bring the broken hive back once");
   Collect(r);HiveFrames();Arrive();Collect(r);
   Check(CountItemInChest(r,225)==1&&Bugs(r)==2&&pendingLoggingCollections.Count==0,"Reload: hive outputs not stored once");Conserved(new List<Chest>{r},1,"break reload "+destroyed);}
  Console.WriteLine("PASS: a hive broken just before the save is saved whole or brought back, never with its outputs; outputs that roll during a 60s save land once afterward");
  // The trunk is still falling when the save starts, and drops during a long save.
  {HiveReset("persist-trunk");var c=NetCrate(2,150);Chop(c,30,31);var waiting=pendingLoggingCollections[PosKey(30,31)];
   SaveStart();Time.time+=60;FellTrunk(30,31,new Vector3(61,0,62));
   Check(waiting.spawnedDropIds.Count==1&&waiting.hives.Count==1&&TryProcessFarmTileCore(c,true)==0&&CountItemInChest(c,300)==0,"Trunk drop during the save lost ownership or was collected");
   SaveChests();SaveEnd();Check(savedDrops.Count==0&&savedCarry.Count==0,"Snapshot holds outputs of a trunk that had not fallen");
   Collect(c);HiveFrames();Arrive();Collect(c);
   Check(CountItemInChest(c,300)==1&&CountItemInChest(c,225)==1&&Bugs(c)==2&&!pendingLoggingCollections.ContainsKey(PosKey(30,31)),"Live: trunk outputs not stored after a long save");Conserved(new List<Chest>{c},1,"trunk live");
   var r=Reload();
   // The save cannot hold a falling trunk, so the game's own trunk falls again at the stump.
   Check(GameObject.spawned.Count==1&&GameObject.spawned[0].at.x==60&&GameObject.spawned[0].at.z==62&&pendingLoggingCollections.ContainsKey(PosKey(30,31))&&CountItemInChest(r,300)==0,"Reload did not respawn the falling trunk");
   Time.time+=2.5f;FellTrunk(30,31,new Vector3(61,0,62));Collect(r);HiveFrames();Arrive();Collect(r);
   Check(CountItemInChest(r,300)==1&&CountItemInChest(r,225)==1&&Bugs(r)==2&&!pendingLoggingCollections.ContainsKey(PosKey(30,31)),"Reload: respawned trunk outputs not stored once");Conserved(new List<Chest>{r},1,"trunk reload");}
  Console.WriteLine("PASS: a trunk still falling at save start keeps its wood and hive through a 60s save, and falls again once after a reload");
  // No net: a saved hive comes back owned, but the crate leaves it whole. The wood is still stored.
  {HiveReset("persist-no-net");var c=NetCrate(-1,0);Chop(c,30,31);Time.time+=1;FellTrunk(30,31,new Vector3(61,0,62));
   SaveStart();SaveChests();SaveEnd();var r=Reload();Collect(r);HiveFrames();Arrive();
   Check(CountItemInChest(r,300)==1&&liveHives.Count==1&&liveHives[0].damageable.health==5&&table.rolls==0&&pendingLoggingCollections.Count==0,"No-net crate broke a restored hive or lost its wood");}
  Console.WriteLine("PASS: without a net a restored hive stays whole after reload; the wood is stored");
  // The crate is replaced while the save runs. The chest snapshot holds the new crate, which owns nothing.
  {HiveReset("persist-replaced");var c=RoomCrate(0,new(int,int)[0]);Chop(c,30,31);Time.time+=1;FellTrunk(30,31,new Vector3(61,0,62),wood:310);Collect(c);
   SaveStart();ContainerManager.manage.activeChests.Clear();NetCrate(2,150);SaveChests();SaveEnd();
   var r=Reload();var wood=WorldManager.Instance.itemsOnGround.Find(d=>d.myItemId==310);Collect(r);HiveFrames();
   Check(pendingLoggingCollections.Count==0&&wood!=null&&wood.saveDrop&&FindLoggingDropOwner(wood)==null&&CountItemInChest(r,310)==0&&liveHives[0].damageable.health==5&&table.rolls==0,"Replacement crate inherited held outputs");}
  Console.WriteLine("PASS: a crate replaced during the save leaves its saved wood and whole hive to the player after reload");
 }
 static int Slots(Chest c,int id){int n=0;for(int i=0;i<24;i++)if(c.itemIds[i]==id)n++;return n;}
 static void StackChecks(InventoryItem honey,InventoryItem bee,InventoryItem queen,InventoryItem bugNet,InventoryItem compNet,InventoryItem pickaxe){
  var allItems=Inventory.Instance.allItems;
  // Real crab pot loot (asset data): 22 river/ocean critters are unstackable underwaterCreature items; Old Key, Crocodile Tooth,
  // Thongs, Jelly, shells, wheelie bin relics, and the Wheelie Bin are already stackable. Ulysses Butterfly 29 and Barramundi 49 are vanilla.
  var crab=new InventoryItem{name="Mud Crab",isStackable=false,underwaterCreature=new UObj()};var butterfly=new InventoryItem{name="Ulysses Butterfly",isStackable=false,bug=new UObj()};var fishItem=new InventoryItem{name="Barramundi",isStackable=false,fish=new UObj()};
  var oldKey=new InventoryItem{name="Old Key"};var turf=new InventoryItem{name="Tropical Grass Turf Roll",isStackable=false,isATool=true};
  allItems[224]=crab;allItems[29]=butterfly;allItems[49]=fishItem;allItems[382]=oldKey;allItems[1445]=turf;
  new Plugin().RunStacking();
  Check(crab.checkIfStackable()&&bee.checkIfStackable()&&queen.checkIfStackable()&&oldKey.checkIfStackable()&&honey.checkIfStackable(),"Crab pot loot or hive bees not stackable");
  Check(!butterfly.isStackable&&!fishItem.isStackable&&!butterfly.checkIfStackable()&&!fishItem.checkIfStackable(),"Other insects or fish became stackable");
  Check(bugNet.isStackable&&!bugNet.checkIfStackable()&&!compNet.checkIfStackable()&&!pickaxe.checkIfStackable()&&turf.isStackable&&!turf.checkIfStackable(),"Tool or turf roll stacking changed");
  Console.WriteLine("PASS: crab pot critters, Bee, and Queen Bee stack; Ulysses Butterfly, Barramundi, nets, tools, and the turf roll keep their behavior");
  // Chest window: bees unlock only in a normal chest. Other insects and fish stay locked everywhere.
  var inv=Inventory.Instance;inv.invSlots=new InventorySlot[6];for(int i=0;i<6;i++)inv.invSlots[i]=new InventorySlot();
  int[] held={227,228,29,49,224,2};for(int i=0;i<6;i++){inv.invSlots[i].itemNo=held[i];inv.invSlots[i].stack=i<2?5:1;}
  foreach(var window in new[]{"chest","stash","vehicle","pond"}){var w=new ChestWindow{currentlyOpenedChest=window=="vehicle"?null:new Chest(),isStash=window=="stash"};w.pondScreen.activeSelf=window=="pond";
   foreach(var slot in inv.invSlots)slot.clearDisable();w.lockBugsAndFishFromChest();bool open=window=="chest";
   Check(inv.invSlots[0].disabled!=open&&inv.invSlots[1].disabled!=open&&inv.invSlots[2].disabled&&inv.invSlots[3].disabled&&!inv.invSlots[4].disabled&&!inv.invSlots[5].disabled,"Chest lock wrong in "+window);}
  Console.WriteLine("PASS: Bee and Queen Bee unlock in normal chests only; insects and fish stay locked; bags, NPCs, vehicles, ponds, and terrariums keep the lock");
  // Terrarium placement: the game places one bug and clears the held slot in the same frame. A stack keeps the rest.
  foreach(var held2 in new[]{(227,5,4),(227,1,0),(29,1,0)}){var slot=inv.invSlots[0];slot.itemNo=held2.Item1;slot.stack=held2.Item2;inv.selectedSlot=0;Time.frameCount++;
   StackedBugTerrariumPatch.Prefix(held2.Item1);slot.updateSlotContentsAndRefresh(-1,0);Check(slot.stack==held2.Item3&&slot.itemNo==(held2.Item3>0?held2.Item1:-1),"Terrarium placement changed "+held2);}
  // A stale pending refund from an earlier frame never touches a later clear.
  var stale=inv.invSlots[0];stale.itemNo=227;stale.stack=5;Time.frameCount++;StackedBugTerrariumPatch.Prefix(227);Time.frameCount++;stale.updateSlotContentsAndRefresh(-1,0);Check(stale.itemNo==-1,"Stale terrarium refund restored items");
  Console.WriteLine("PASS: placing a stacked bee in a terrarium uses one bee; single bugs and later clears are unchanged");
  // Drop: menu drops send the whole stack, the hotbar drop sends one. A bee stack drops as a pickup; one bug still releases.
  var dropper=new CharMovement();foreach(var drop in new[]{(227,5,0,5),(228,2,0,2),(227,1,1,0),(29,1,1,0),(224,6,0,6)}){Game.nativeBugs.Clear();Game.nativeDrops.Clear();dropper.UserCode_CmdDropItem(drop.Item1,drop.Item2);
   Check(Game.nativeBugs.Count==drop.Item3&&Game.nativeDrops.Count==drop.Item4&&(Game.nativeBugs.TrueForAll(i=>i==drop.Item1)&&Game.nativeDrops.TrueForAll(i=>i==drop.Item1)),"Drop lost or changed "+drop);}
  dropper.myEquip.myPermissions.canPickUp=false;Game.nativeDrops.Clear();dropper.UserCode_CmdDropItem(227,5);Check(Game.nativeDrops.Count==0,"Drop ignored permission");Game.nativeBugs.Clear();
  Console.WriteLine("PASS: dropping a Bee or Queen Bee stack leaves the whole stack as a pickup; one bee still releases one live bug");
  // Museum: the game donates the first selected slot of each item and clears that whole slot.
  int[][] museum={new[]{227,5},new[]{227,3},new[]{228,1},new[]{382,4},new[]{224,6},new[]{29,1}};for(int i=0;i<6;i++){inv.invSlots[i].itemNo=museum[i][0];inv.invSlots[i].stack=museum[i][1];inv.invSlots[i].selected=true;}
  List<int[]> state;StackedCritterMuseumPatch.Prefix(out state);var seen=new List<int>();foreach(var slot in inv.invSlots)if(slot.isSelectedForGive()&&!seen.Contains(slot.itemNo)){seen.Add(slot.itemNo);slot.updateSlotContentsAndRefresh(-1,0);}StackedCritterMuseumPatch.Postfix(state);
  Check(inv.invSlots[0].itemNo==227&&inv.invSlots[0].stack==4&&inv.invSlots[1].stack==3&&inv.invSlots[2].itemNo==-1&&inv.invSlots[3].itemNo==-1&&inv.invSlots[4].itemNo==224&&inv.invSlots[4].stack==5&&inv.invSlots[5].itemNo==-1,"Museum donation lost or duplicated a stack");
  foreach(var slot in inv.invSlots)slot.selected=false;
  Console.WriteLine("PASS: donating from a stacked Bee or critter keeps the rest; other slots and vanilla items are unchanged");
  // Capacity with stacks. Rolls happen only when the break object drops, once per bee position.
  var cases=new[]{(3,new(int,int)[0],true),(2,new(int,int)[0],false),(2,new[]{(227,5)},true),(1,new[]{(227,5)},false),(1,new[]{(227,5),(225,2)},true),(0,new[]{(227,5),(225,2)},false),(0,new[]{(227,5),(228,7),(225,2)},true),(0,new[]{(227,999),(228,999),(225,999)},true),(1,new[]{(228,1)},false),(2,new[]{(228,1)},true)};
  foreach(var room in cases){HiveReset("stack-room"+room.Item1+room.Item2.Length+Stored(room.Item2,227));var c=RoomCrate(room.Item1,room.Item2);Chop(c,30,31);Time.time+=1;var hive=FellTrunk(30,31,new Vector3(61,0,62));Collect(c);Check(table.rolls==0,"Rolled before the drop");HiveFrames();Arrive();
   Check(hive.damageable.health==(room.Item3?0:5),"Stack room "+room.Item1+"/"+room.Item2.Length+" decided wrong");
   if(room.Item3){Check(Game.nativeBugs.Count==0&&Game.nativeDrops.Count==0&&Slots(c,225)==1&&Slots(c,227)<=1&&Slots(c,228)<=1,"Stacked outputs split or spilled");Conserved(new List<Chest>{c},1,"stack room",Stored(room.Item2,225),Stored(room.Item2,227),Stored(room.Item2,228));}else Check(table.rolls==0&&pendingLoggingCollections[PosKey(30,31)].hives.Count==1,"Whole hive rolled or was dropped");}
  Console.WriteLine("PASS: honey, Bee, and Queen Bee stack onto partial and very large stacks; missing types need one slot each; too little room keeps the hive whole and pending");
  // Pending breaks: a later collection stacks onto types an earlier unfinished break claimed, and never spends the same slot twice.
  foreach(var pass in new[]{(3,new(int,int)[0],true),(2,new[]{(227,5)},true),(1,new(int,int)[0],false)}){HiveReset("stack-pending"+pass.Item1+pass.Item2.Length);var c=RoomCrate(pass.Item1,pass.Item2);
   foreach(int y in new[]{31,32})pendingLoggingCollections[PosKey(30,y)]=new PendingLoggingCollection{chestX=30,chestY=30,sourceX=30,sourceY=y,collectAt=Time.time+5,expiresAt=Time.time+40};
   // The second trunk falls after the first hive broke but before its break object dropped.
   var a=FellTrunk(30,31,new Vector3(61,0,62));Collect(c);Check(table.rolls==0&&pendingLoggingCollections.ContainsKey(PosKey(30,32)),"Second collection ran early");
   var b=FellTrunk(30,32,new Vector3(61,0,64));Collect(c);int broken=(a.damageable.health==0?1:0)+(b.damageable.health==0?1:0);HiveFrames();Arrive();
   Check(broken==(pass.Item3?2:0)&&Game.nativeBugs.Count==0&&Game.nativeDrops.Count==0,"Pending reservation "+pass.Item1+" broke "+broken);Conserved(new List<Chest>{c},broken,"pending",Stored(pass.Item2,225),Stored(pass.Item2,227),Stored(pass.Item2,228));}
  Console.WriteLine("PASS: a second collection stacks onto an unfinished break's claimed slots; with too little room neither hive breaks yet");
  // Inventory changes after the break: the player swaps out the stored Bee stacks before the roll. Bees that no longer fit wait as saved pickups.
  {HiveReset("stack-changed");var stored=new[]{(227,5),(228,7),(225,2)};var c=RoomCrate(0,stored);Chop(c,30,31);Time.time+=1;var hive=FellTrunk(30,31,new Vector3(61,0,62));Collect(c);Check(hive.damageable.health==0,"Stacked crate left the hive whole");
   for(int i=0;i<24;i++)if(c.itemIds[i]==227||c.itemIds[i]==228){c.itemIds[i]=301;c.itemStacks[i]=1;}allItems[301]=new InventoryItem();HiveFrames();Arrive();
   Check(Game.nativeBugs.Count==0&&CountItemInChest(c,225)==3&&Bugs(c)==0&&Game.nativeDrops.Count==0&&WorldManager.Instance.itemsOnGround.Count==rolled.Count,"Changed crate lost or kept bees");Conserved(new List<Chest>{c},1,"changed",2);}
  Console.WriteLine("PASS: a crate whose bee stacks are removed before the roll keeps the honey and holds the rolled bees as saved pickups, once each");
  // StackableCritters off: bees stay unstackable and locked, and each rolled bee needs its own slot as before.
  Plugin.StackableCritters=false;var vanillaBee=new InventoryItem{name="Bee",isStackable=false,bug=new UObj()};var vanillaQueen=new InventoryItem{name="Queen Bee",isStackable=false,bug=new UObj()};allItems[227]=vanillaBee;allItems[228]=vanillaQueen;table.itemsInLootTable=new[]{vanillaBee,vanillaQueen};
  new Plugin().RunStacking();Check(!vanillaBee.checkIfStackable()&&!vanillaQueen.checkIfStackable(),"Disabled config stacked bees");
  inv.invSlots[0].itemNo=227;inv.invSlots[0].stack=1;inv.invSlots[0].clearDisable();new ChestWindow{currentlyOpenedChest=new Chest()}.lockBugsAndFishFromChest();Check(inv.invSlots[0].disabled,"Disabled config unlocked bees");
  HiveReset("stack-off");var off=RoomCrate(3,new[]{(227,1)});foreach(int y in new[]{31,32})pendingLoggingCollections[PosKey(30,y)]=new PendingLoggingCollection{chestX=30,chestY=30,sourceX=30,sourceY=y,collectAt=Time.time+5,expiresAt=Time.time+40};
  var offA=FellTrunk(30,31,new Vector3(61,0,62));var offB=FellTrunk(30,32,new Vector3(61,0,64));Collect(off);HiveFrames();Arrive();
  Check(offA.damageable.health+offB.damageable.health==5&&Bugs(off)==3,"Unstackable bees shared a slot");Conserved(new List<Chest>{off},1,"stack off",0,1);
  Plugin.StackableCritters=true;allItems[227]=bee;allItems[228]=queen;table.itemsInLootTable=new[]{bee,queen};
  Console.WriteLine("PASS: with StackableCritters off, bees stay one per slot and locked, and two hives cannot share three slots");
  // Three parallel rows of different species, crates one empty tile apart, no conveyors. A trunk falls in any direction
  // and drops wood along its length, onto or past the next row while that row's own trunks are pending.
  {HiveReset("mixed-rows");allItems[302]=new InventoryItem{name="Palm Wood"};var woods=new Dictionary<int,int>{{10,301},{12,302},{14,310}};
   WorldManager.Instance.allObjectSettings[1]=new TileObjectSettings{dropsObjectOnDeath=new GameObject{worldObject=new WorldObject()},changeToTileObjectOnDeath=3};WorldManager.Instance.allObjectSettings[3]=new TileObjectSettings();
   NetworkMapSharer.Instance.trunkFall=2.5f;NetworkMapSharer.Instance.trunks.Clear();WorldManager.Instance.onTileMap[30,30]=-1;
   var rows=new List<Chest>();foreach(int x in woods.Keys){var crate=new Chest{xPos=x,yPos=5};Array.Fill(crate.itemIds,-1);crate.itemIds[0]=200;crate.itemStacks[0]=1;crate.itemIds[12]=100;crate.itemStacks[12]=24;ContainerManager.manage.activeChests.Add(crate);WorldManager.Instance.onTileMap[x,5]=412;for(int y=6;y<30;y++)WorldManager.Instance.onTileMap[x,y]=1;rows.Add(crate);}
   var dirs=new[]{(1,0),(-1,0),(1,1),(-1,-1),(0,1),(-1,1),(1,-1),(0,-1)};int spawned=0,guard=0;bool[] fin={false};var run=RunDayChangeFarmerWaves(rows,fin);
   while(run.MoveNext()){Check(guard++<20000,"Mixed rows never finished");Time.time+=.1f;
    foreach(var fall in NetworkMapSharer.Instance.trunks.FindAll(t=>t.at<=Time.time)){var t=new WorldObject();t.transform.position=new Vector3(fall.x*2,0,fall.y*2);var d=dirs[(fall.x*7+fall.y*3)%8];int wood=woods[fall.x];HashSet<uint> st;LoggingWorldObjectDropCapturePatch.Prefix(t,out st);
     // Game: WorldObject.doDrop spawns one wood per drop position with tryNotToStack; dropAnItem can still stack it onto an equal drop on its landing tile.
     for(int k=1;k<=3;k++){var tile=new Vector2Int(fall.x+d.Item1*k,fall.y+d.Item2*k);var same=WorldManager.Instance.itemsOnGround.Find(g=>g.myItemId==wood&&g.onTile.x==tile.x&&g.onTile.y==tile.y);if(same!=null)same.stackAmount++;else WorldManager.Instance.itemsOnGround.Add(new DroppedItem{netId=fallNet++,myItemId=wood,stackAmount=1,onTile=tile});spawned++;}
     LoggingWorldObjectDropCapturePatch.Postfix(t,st);LoggingWorldObjectDropCapturePatch.Finalizer();}
    NetworkMapSharer.Instance.trunks.RemoveAll(t=>t.at<=Time.time);}
   Check(fin[0]&&spawned==72*3&&WorldManager.Instance.itemsOnGround.Count==0&&pendingLoggingCollections.Count==0,"Mixed rows left wood behind");
   foreach(var crate in rows)foreach(var wood in woods)Check(CountItemInChest(crate,wood.Value)==(wood.Key==crate.xPos?72:0),"Crate "+crate.xPos+" holds "+CountItemInChest(crate,wood.Value)+" of row "+wood.Key+"'s wood");}
  Console.WriteLine("PASS: three adjacent rows of different species with trunks falling across rows: each crate collects only its own row's wood");
 }
}
PATCHES
'''.replace("METHODS", methods).replace("PATCHES", patches).replace("CONVEYOR", conveyor).replace("DEPOSIT", deposit).replace("STACKING", stacking).replace("AFTER_SAVE", after_save)
with tempfile.TemporaryDirectory(prefix="autom8er-logging-check-") as temp:
    directory = Path(temp)
    (directory / "Program.cs").write_text(code)
    project = directory / "Check.csproj"
    project.write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>')
    subprocess.run(["dotnet", "run", "--project", str(project), "-c", "Release"], cwd=directory, check=True)
