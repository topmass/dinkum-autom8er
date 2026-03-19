using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Mirror;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Autom8er
{
    [BepInPlugin("topmass.autom8er", "Autom8er", "1.6.1")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        private Harmony harmony;
        private float scanTimer = 0f;

        // Config
        private ConfigEntry<int> configConveyorTileItemId;
        private ConfigEntry<bool> configKeepOneItem;
        private ConfigEntry<float> configScanInterval;
        private ConfigEntry<int> configSiloFillSpeed;
        private ConfigEntry<bool> configAutoFeedPonds;
        private ConfigEntry<bool> configHoldOutputForBreeding;
        private ConfigEntry<bool> configAnimationEnabled;
        private ConfigEntry<float> configAnimationSpeed;
        private ConfigEntry<bool> configStackableCritters;

        // Default conveyor tile (Black Marble Path)
        public const int DEFAULT_CONVEYOR_TILE_ITEM_ID = 1747;

        // TileObject IDs for INPUT ONLY containers (won't receive machine outputs)
        public const int WHITE_CRATE_TILE_ID = 417;
        public const int WHITE_CHEST_TILE_ID = 430;

        // TileObject IDs for legacy output-only containers
        public const int BLACK_CRATE_TILE_ID = 410;
        public const int BLACK_CHEST_TILE_ID = 421;

        // TileObject IDs for colored crate-only feature blocks
        public const int GREEN_CRATE_TILE_ID = 412;

        // Item IDs
        public const int HAR_VAC_ITEM_ID = 1728;
        public const int TROPICAL_GRASS_TURF_ROLL_ITEM_ID = 1445;

        // Cached at runtime from item data
        public static int ConveyorTileType = -1;
        public static int ConveyorTileItemId = DEFAULT_CONVEYOR_TILE_ITEM_ID;
        public static bool KeepOneItem = false;
        public static float ScanInterval = 0.3f;
        public static int SiloFillSpeed = 10;
        public static bool AutoFeedPonds = true;
        public static bool HoldOutputForBreeding = true;
        public static bool AnimationEnabled = true;
        public static float AnimationSpeed = 2f;
        public static bool StackableCritters = true;
        private static bool loadCatchUpPending = false;
        private static bool loadCatchUpCompletedForCurrentLoad = false;
        private static float loadCatchUpNextAttemptAt = 0f;
        private static int loadCatchUpAttempts = 0;
        private bool crittersPatched = false;

        public static void QueueLoadCatchUp()
        {
            loadCatchUpPending = true;
            loadCatchUpCompletedForCurrentLoad = false;
            loadCatchUpAttempts = 0;
            loadCatchUpNextAttemptAt = Time.realtimeSinceStartup + 8f;

            if (Log != null)
                Log.LogInfo("Autom8er: Queued load catch-up.");
        }

        private void Awake()
        {
            Log = Logger;

            // Setup config
            configConveyorTileItemId = Config.Bind(
                "Conveyor",
                "ConveyorTileItemId",
                DEFAULT_CONVEYOR_TILE_ITEM_ID,
                "Item ID of the floor/path tile to use as conveyor belt.\n" +
                "Examples: Black Marble Path (1747), Cobblestone Path (964), Rock Path (346), Iron Path (775), Brick Path (15)"
            );

            configScanInterval = Config.Bind(
                "Performance",
                "ScanInterval",
                0.3f,
                "How often to scan chests and feed machines (in seconds).\n" +
                "Default: 0.3 (about 3 times per second)\n" +
                "Lower = faster automation but more CPU usage. Range: 0.1 to 1.0\n" +
                "Recommended: 0.2-0.3 for fast setups, 0.5 for relaxed."
            );

            configKeepOneItem = Config.Bind(
                "Automation",
                "KeepOneItem",
                false,
                "Keep one item in chest slots to maintain placeholders for easy stacking.\n" +
                "When enabled, requires 1 extra item beyond what the machine needs before it will take from a slot.\n" +
                "Example: Furnace needs 5 ore - will only take from stacks of 6+, leaving 1 behind.\n" +
                "This helps items stack into existing slots when you return from gathering."
            );

            configSiloFillSpeed = Config.Bind(
                "Automation",
                "SiloFillSpeed",
                10,
                "How many Animal Food items to load into silos per tick.\n" +
                "Default: 10 (5% of silo capacity). Each item animates individually on the conveyor.\n" +
                "Range: 1-20. Higher = faster filling."
            );

            configAutoFeedPonds = Config.Bind(
                "Fish Pond & Terrarium",
                "AutoFeedPondsAndTerrariums",
                true,
                "Automatically feed fish ponds with critters and bug terrariums with honey\n" +
                "from adjacent chests or via conveyor paths. Also extracts roe and cocoons\n" +
                "into adjacent chests on day change."
            );

            configHoldOutputForBreeding = Config.Bind(
                "Fish Pond & Terrarium",
                "HoldOutputForBreeding",
                true,
                "When enabled, holds 15 roe in fish ponds and 10 cocoons in terrariums\n" +
                "to allow breeding until they reach 5 creatures. Once full (5 fish/bugs),\n" +
                "all output is extracted immediately since breeding cannot occur.\n" +
                "Disable to always extract all output regardless of creature count."
            );

            configAnimationEnabled = Config.Bind(
                "Conveyor Animation",
                "AnimationEnabled",
                true,
                "Show items visually moving along conveyor paths.\n" +
                "Disable to transfer items instantly without animation (may improve performance)."
            );

            configAnimationSpeed = Config.Bind(
                "Conveyor Animation",
                "AnimationSpeed",
                2f,
                "Speed of conveyor animations in tiles per second.\n" +
                "Default: 2 (smooth, visible movement)\n" +
                "Range: 0.5 to 10. Higher = faster animations."
            );

            configStackableCritters = Config.Bind(
                "Quality of Life",
                "StackableCritters",
                true,
                "Makes all critters (underwater creatures) stackable in inventory and chests.\n" +
                "Helps with automation and storage management.\n" +
                "Disable to keep vanilla critter stacking behavior (1 per slot)."
            );

            ConveyorTileItemId = configConveyorTileItemId.Value;
            KeepOneItem = configKeepOneItem.Value;
            ScanInterval = Mathf.Clamp(configScanInterval.Value, 0.1f, 1.0f);
            SiloFillSpeed = Mathf.Clamp(configSiloFillSpeed.Value, 1, 20);
            AutoFeedPonds = configAutoFeedPonds.Value;
            HoldOutputForBreeding = configHoldOutputForBreeding.Value;
            AnimationEnabled = configAnimationEnabled.Value;
            AnimationSpeed = Mathf.Clamp(configAnimationSpeed.Value, 0.5f, 10f);
            StackableCritters = configStackableCritters.Value;

            Log.LogInfo("Autom8er v1.6.1 loaded! ConveyorTile=" + ConveyorTileItemId + ", Scan=" + ScanInterval + "s, KeepOne=" + KeepOneItem + ", SiloSpeed=" + SiloFillSpeed + ", FeedPonds=" + AutoFeedPonds + ", BreedHold=" + HoldOutputForBreeding + ", Anim=" + AnimationEnabled + ", AnimSpeed=" + AnimationSpeed + ", StackCritters=" + StackableCritters);

            harmony = new Harmony("topmass.autom8er");
            harmony.PatchAll();

            Log.LogInfo("Harmony patches applied.");
        }

        private void Update()
        {
            if (!NetworkServer.active)
                return;

            if (Inventory.Instance == null || WorldManager.Instance == null || ContainerManager.manage == null)
                return;

            // Cache the conveyor tile type once
            if (ConveyorTileType == -1)
            {
                CacheConveyorTileType();
            }

            if (!crittersPatched)
            {
                ApplyStackabilityOverrides();
            }

            ConveyorAnimator.UpdateAnimations();
            VacuumCrateHelper.UpdateVisuals();

            scanTimer += Time.deltaTime;
            if (scanTimer >= ScanInterval)
            {
                scanTimer = 0f;
                ProcessAllChests();
                // Note: Harvest processing (bee houses, key cutters, worm farms, crab pots)
                // is triggered by NewDayHarvestPatch after day change, not continuously
            }

            if (!loadCatchUpCompletedForCurrentLoad &&
                SaveLoad.saveOrLoad != null &&
                SaveLoad.saveOrLoad.loadingComplete &&
                NetworkMapSharer.Instance != null &&
                NetworkMapSharer.Instance.localChar != null &&
                !loadCatchUpPending)
            {
                loadCatchUpPending = true;
                loadCatchUpNextAttemptAt = Time.realtimeSinceStartup + 2f;
                loadCatchUpAttempts = 0;
                Log.LogInfo("Autom8er: Load complete detected in Update, queuing catch-up.");
            }

            if (loadCatchUpPending)
            {
                TryStartQueuedLoadCatchUp();
            }
        }

        private void TryStartQueuedLoadCatchUp()
        {
            if (Time.realtimeSinceStartup < loadCatchUpNextAttemptAt)
                return;

            if (SaveLoad.saveOrLoad == null || !SaveLoad.saveOrLoad.loadingComplete)
            {
                loadCatchUpNextAttemptAt = Time.realtimeSinceStartup + 2f;
                return;
            }

            if (NetworkMapSharer.Instance == null || !NetworkMapSharer.Instance.isServer || NetworkMapSharer.Instance.localChar == null)
            {
                loadCatchUpNextAttemptAt = Time.realtimeSinceStartup + 2f;
                return;
            }

            if (ContainerManager.manage == null)
            {
                loadCatchUpNextAttemptAt = Time.realtimeSinceStartup + 2f;
                return;
            }

            if (ContainerManager.manage.activeChests.Count == 0 && loadCatchUpAttempts < 5)
            {
                loadCatchUpAttempts++;
                loadCatchUpNextAttemptAt = Time.realtimeSinceStartup + 2f;
                Log.LogInfo("Autom8er: Load catch-up waiting for active chests, retry " + loadCatchUpAttempts + ".");
                return;
            }

            loadCatchUpPending = false;
            loadCatchUpCompletedForCurrentLoad = true;
            Log.LogInfo("Autom8er: Starting load catch-up harvest processing.");
            StartCoroutine(NewDayHarvestPatch.RunPhasedHarvestProcessing(0f, "Autom8er: Processing load catch-up harvests...", "Autom8er: Load catch-up harvest processing complete"));
        }

        private void CacheConveyorTileType()
        {
            if (ConveyorTileItemId < Inventory.Instance.allItems.Length)
            {
                InventoryItem pathItem = Inventory.Instance.allItems[ConveyorTileItemId];
                if (pathItem != null && pathItem.placeableTileType > -1)
                {
                    ConveyorTileType = pathItem.placeableTileType;
                    Log.LogInfo("Autom8er: Conveyor tile '" + pathItem.itemName + "' (ID " + ConveyorTileItemId + ") -> tileType = " + ConveyorTileType);
                }
                else
                {
                    Log.LogWarning("Autom8er: Item ID " + ConveyorTileItemId + " is not a valid path/floor tile!");
                }
            }
            else
            {
                Log.LogWarning("Autom8er: Invalid conveyor tile Item ID: " + ConveyorTileItemId);
            }
        }

        private void ApplyStackabilityOverrides()
        {
            crittersPatched = true;
            int critterCount = 0;
            for (int i = 0; i < Inventory.Instance.allItems.Length; i++)
            {
                InventoryItem item = Inventory.Instance.allItems[i];
                if (item == null)
                    continue;

                if (StackableCritters && (bool)item.underwaterCreature && !item.isStackable)
                {
                    item.isStackable = true;
                    critterCount++;
                }
            }

            InventoryItem tropicalGrassRoll = Inventory.Instance.allItems.Length > TROPICAL_GRASS_TURF_ROLL_ITEM_ID
                ? Inventory.Instance.allItems[TROPICAL_GRASS_TURF_ROLL_ITEM_ID]
                : null;

            bool tropicalGrassPatched = false;
            if (tropicalGrassRoll != null && !tropicalGrassRoll.isStackable)
            {
                tropicalGrassRoll.isStackable = true;
                tropicalGrassPatched = true;
            }

            if (critterCount > 0)
                Log.LogInfo("Autom8er: Made " + critterCount + " critters stackable");

            if (tropicalGrassPatched)
                Log.LogInfo("Autom8er: Made Tropical Grass Turf Roll stackable");
        }

        private void ProcessAllChests()
        {
            List<Chest> activeChests = ContainerManager.manage.activeChests;
            if (activeChests == null || activeChests.Count == 0)
                return;

            List<Chest> chests = new List<Chest>(activeChests);

            foreach (Chest chest in chests)
            {
                if (chest == null)
                    continue;

                HouseDetails inside = null;
                if (chest.insideX != -1 && chest.insideY != -1)
                {
                    inside = HouseManager.manage.getHouseInfoIfExists(chest.insideX, chest.insideY);
                }

                if (ConveyorHelper.IsFilterCrate(chest.xPos, chest.yPos, inside))
                {
                    FilterCrateHelper.TryPullFilteredItemsFromNetwork(chest, inside);
                    continue;
                }

                // Skip legacy output-only chests.
                if (ConveyorHelper.IsOutputOnlyContainer(chest.xPos, chest.yPos, inside))
                    continue;

                VacuumCrateHelper.TryVacuumNearbyDrops(chest, inside);
                VacuumCrateHelper.TryFlushStoredItemsToNetwork(chest, inside);

                if (!ConveyorHelper.ChestHasItems(chest))
                    continue;

                // Try to feed fish ponds and bug terrariums (before machine checks,
                // since critters have itemChange and would get caught by TryFeedAdjacentMachine)
                if (AutoFeedPonds)
                    FishPondHelper.TryFeedPondsAndTerrariums(chest, inside);

                // Try to feed one machine per chest per cycle
                if (ConveyorHelper.TryFeedAdjacentMachine(chest, inside))
                    continue;

                if (ConveyorHelper.TryFeedMachineViaConveyorPath(chest, inside))
                    continue;

                // Try to load bait into nearby crab pots (2-tile radius due to water)
                CrabPotHelper.TryLoadBaitIntoCrabPots(chest, inside);

                // Try to load items into growth stage objects (incubators, etc)
                GrowthStageHelper.TryLoadItemsIntoGrowthStages(chest, inside);

                // Try to load feed into nearby silos (visual fill, one item per tick)
                SiloHelper.TryLoadFeedIntoSilos(chest, inside);
            }
        }

        private void OnDestroy()
        {
            ConveyorAnimator.ClearAllAnimations();
            VacuumCrateHelper.ClearVisuals();
            harmony?.UnpatchSelf();
        }
    }

    public static class AutomationCreditHelper
    {
        public static void TryGrantMachineInputCredit(int itemId, int tileObjectId)
        {
            if (itemId < 0 || tileObjectId < 0 || Inventory.Instance == null)
                return;

            InventoryItem item = Inventory.Instance.allItems[itemId];
            if (item == null || item.itemChange == null)
                return;

            try
            {
                item.itemChange.checkTask(tileObjectId);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Autom8er: Failed to grant machine credit for item " + itemId + " -> machine " + tileObjectId + ": " + e.Message);
            }
        }

        public static void TryGrantHarvestMilestone(int tileObjectId)
        {
            if (tileObjectId < 0 || WorldManager.Instance == null || DailyTaskGenerator.generate == null)
                return;

            TileObject tileObj = WorldManager.Instance.allObjects[tileObjectId];
            if (tileObj == null || tileObj.tileObjectGrowthStages == null)
                return;

            DailyTaskGenerator.genericTaskType milestone = tileObj.tileObjectGrowthStages.milestoneOnHarvest;
            if (milestone == DailyTaskGenerator.genericTaskType.None)
                return;

            try
            {
                DailyTaskGenerator.generate.doATask(milestone);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Autom8er: Failed to grant harvest milestone for tile object " + tileObjectId + ": " + e.Message);
            }
        }

        public static void TryGrantGrowthHarvestCredit(int tileObjectId)
        {
            if (tileObjectId < 0 || WorldManager.Instance == null)
                return;

            TileObject tileObj = WorldManager.Instance.allObjects[tileObjectId];
            if (tileObj == null || tileObj.tileObjectGrowthStages == null)
                return;

            TileObjectGrowthStages growth = tileObj.tileObjectGrowthStages;

            try
            {
                if (CharLevelManager.manage != null)
                {
                    if (growth.needsTilledSoil || growth.isAPlantSproutFromAFarmPlant(tileObj.tileObjectId))
                    {
                        if (growth.diesOnHarvest)
                        {
                            CharLevelManager.manage.addXp(CharLevelManager.SkillTypes.Farming, Mathf.Clamp(growth.objectStages.Length / 3, 1, 12));
                        }
                        else
                        {
                            CharLevelManager.manage.addXp(CharLevelManager.SkillTypes.Farming, Mathf.Clamp(growth.objectStages.Length / 8, 1, 12));
                        }
                    }
                    else if (growth.mustBeInWater)
                    {
                        CharLevelManager.manage.addXp(CharLevelManager.SkillTypes.Fishing, 3);
                    }
                    else if ((bool)growth.harvestDrop && (!growth.harvestDrop.placeable || growth.harvestDrop.placeable.tileObjectId != tileObj.tileObjectId))
                    {
                        CharLevelManager.manage.addXp(CharLevelManager.SkillTypes.Foraging, 1);
                    }
                }

                if (DailyTaskGenerator.generate == null)
                    return;

                if (growth.needsTilledSoil || growth.isAPlantSproutFromAFarmPlant(tileObj.tileObjectId))
                {
                    DailyTaskGenerator.generate.doATask(DailyTaskGenerator.genericTaskType.HarvestCrops);
                }

                if (growth.isCrabPot)
                    return;

                int amount = 1;
                if (!growth.normalPickUp && !growth.autoPickUpOnHarvest && growth.harvestSpots != null && growth.harvestSpots.Length > 0)
                {
                    amount = growth.harvestSpots.Length;
                }

                DailyTaskGenerator.generate.doATaskTileObject(DailyTaskGenerator.genericTaskType.Harvest, tileObj.tileObjectId, amount);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Autom8er: Failed to grant growth harvest credit for tile object " + tileObjectId + ": " + e.Message);
            }
        }

        public static void TryGrantOutputCredit(int tileObjectId, int itemId, int amount)
        {
            if (tileObjectId < 0 || itemId < 0 || amount <= 0 || WorldManager.Instance == null || CharLevelManager.manage == null)
                return;

            TileObject tileObj = WorldManager.Instance.allObjects[tileObjectId];
            if (tileObj == null)
                return;

            int tallyType = tileObj.getXpTallyType();
            if (tallyType < 0)
                return;

            try
            {
                CharLevelManager.manage.addToDayTally(itemId, amount, tallyType);

                InventoryItem item = Inventory.Instance != null ? Inventory.Instance.allItems[itemId] : null;
                if (item != null && (bool)item.underwaterCreature && PediaManager.manage != null)
                {
                    PediaManager.manage.addCaughtToList(itemId);
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Autom8er: Failed to grant harvest output credit for item " + itemId + ": " + e.Message);
            }
        }

        public static void TryGrantHarvestOutputCredit(int tileObjectId, int itemId, int amount)
        {
            TryGrantOutputCredit(tileObjectId, itemId, amount);
        }

        public static void TryGrantTileObjectBreakCredit(int tileObjectId)
        {
            if (tileObjectId < 0 || WorldManager.Instance == null || CharLevelManager.manage == null)
                return;

            TileObject tileObj = WorldManager.Instance.allObjects[tileObjectId];
            TileObjectSettings settings = WorldManager.Instance.allObjectSettings[tileObjectId];
            if (tileObj == null || settings == null)
                return;

            try
            {
                int xpAmount = 1 + Mathf.RoundToInt(settings.fullHealth / 10f);
                CharLevelManager.manage.addXp(CharLevelManager.SkillTypes.Mining, xpAmount);

                if (DailyTaskGenerator.generate != null)
                    DailyTaskGenerator.generate.doATask(settings.TaskType);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Autom8er: Failed to grant tile break credit for tile object " + tileObjectId + ": " + e.Message);
            }
        }

        public static void TryGrantQuarryOutputCredit(int itemId, int amount)
        {
            if (itemId < 0 || amount <= 0 || CharLevelManager.manage == null)
                return;

            try
            {
                CharLevelManager.manage.addToDayTally(itemId, amount, (int)CharLevelManager.SkillTypes.Mining);

                InventoryItem item = Inventory.Instance != null ? Inventory.Instance.allItems[itemId] : null;
                if (item != null && (bool)item.underwaterCreature && PediaManager.manage != null)
                {
                    PediaManager.manage.addCaughtToList(itemId);
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Autom8er: Failed to grant quarry output credit for item " + itemId + ": " + e.Message);
            }
        }

        public static void TryGrantDroppedItemPickupCredit(int itemId, int amount, int tallyType)
        {
            if (itemId < 0 || amount <= 0 || tallyType < 0 || CharLevelManager.manage == null)
                return;

            try
            {
                CharLevelManager.manage.addToDayTally(itemId, amount, tallyType);

                InventoryItem item = Inventory.Instance != null ? Inventory.Instance.allItems[itemId] : null;
                if (item != null && (bool)item.underwaterCreature && PediaManager.manage != null)
                {
                    PediaManager.manage.addCaughtToList(itemId);
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Autom8er: Failed to grant dropped-item pickup credit for item " + itemId + ": " + e.Message);
            }
        }
    }

    [HarmonyPatch(typeof(ItemDepositAndChanger), "ejectItemOnCycle")]
    public static class EjectItemOnCyclePatch
    {
        static bool Prefix(ItemDepositAndChanger __instance, int xPos, int yPos, HouseDetails inside)
        {
            int resultItemId = -1;
            int stackAmount = 1;

            if (inside != null)
            {
                if (inside.houseMapOnTileStatus[xPos, yPos] < 0 ||
                    !Inventory.Instance.allItems[inside.houseMapOnTileStatus[xPos, yPos]].itemChange)
                {
                    return true;
                }
                resultItemId = Inventory.Instance.allItems[inside.houseMapOnTileStatus[xPos, yPos]]
                    .itemChange.getChangerResultId(inside.houseMapOnTile[xPos, yPos]);
            }
            else
            {
                if (WorldManager.Instance.onTileStatusMap[xPos, yPos] < 0)
                {
                    return true;
                }
                if (!Inventory.Instance.allItems[WorldManager.Instance.onTileStatusMap[xPos, yPos]].itemChange)
                {
                    return true;
                }
                resultItemId = Inventory.Instance.allItems[WorldManager.Instance.onTileStatusMap[xPos, yPos]]
                    .itemChange.getChangerResultId(WorldManager.Instance.onTileMap[xPos, yPos]);
            }

            if (resultItemId == -1)
            {
                return true;
            }

            if (Inventory.Instance.allItems[resultItemId].hasFuel)
            {
                stackAmount = Inventory.Instance.allItems[resultItemId].fuelMax;
            }

            // Priority 1: Direct adjacent chest (not white/input-only)
            int sourceTileObjectId = inside != null
                ? inside.houseMapOnTile[xPos, yPos]
                : WorldManager.Instance.onTileMap[xPos, yPos];

            System.Action grantOutputCredit = () =>
            {
                AutomationCreditHelper.TryGrantOutputCredit(sourceTileObjectId, resultItemId, stackAmount);
            };

            if (ConveyorHelper.TryDepositToAdjacentChest(xPos, yPos, inside, resultItemId, stackAmount, grantOutputCredit))
            {
                Plugin.Log.LogInfo("Autom8er: Machine output -> " + Inventory.Instance.allItems[resultItemId].itemName);
                return false;
            }

            // Priority 2: Via Black Marble Path conveyor network
            if (ConveyorHelper.TryDepositViaConveyorPath(xPos, yPos, inside, resultItemId, stackAmount, grantOutputCredit))
            {
                Plugin.Log.LogInfo("Autom8er: Machine output via conveyor -> " + Inventory.Instance.allItems[resultItemId].itemName);
                return false;
            }

            return true;
        }
    }

    // Patch for TileObjectGrowthStages harvest (bee houses, key cutters, worm farms, crab pots)
    [HarmonyPatch(typeof(TileObjectGrowthStages), "harvest")]
    public static class HarvestPatch
    {
        static bool Prefix(TileObjectGrowthStages __instance, int xPos, int yPos)
        {
            // Only run on server
            if (!NetworkMapSharer.Instance.isServer)
                return true;

            // Determine if we're inside a house based on current player state
            // Note: harvest is called from our TryAutoHarvestAt which tracks the inside context
            HouseDetails inside = HarvestHelper.CurrentHarvestInside;

            // For crab pots, use 2-tile radius since they're placed in water
            int searchRadius = __instance.isCrabPot ? 2 : 1;

            // Find chest within search radius
            Chest targetChest = HarvestHelper.FindAdjacentChestForHarvest(xPos, yPos, inside, searchRadius);
            if (targetChest == null)
                return true; // No chest, let original harvest drop items on ground

            // Harvest all items to chest
            bool harvestedAny = false;
            int sourceTileObjectId = __instance.GetComponent<TileObject>().tileObjectId;

            if (__instance.isCrabPot)
            {
                // Crab pot drops
                int itemId = __instance.getCrabTrapDrop(xPos, yPos);
                if (itemId != -1)
                {
                    ConveyorHelper.OutputDestination itemDestination = HarvestHelper.FindBestDestinationForHarvestOutput(xPos, yPos, inside, itemId, searchRadius);
                    if (itemDestination != null)
                    {
                        harvestedAny = true;
                        int capturedItemId = itemId;
                        Chest capturedChest = itemDestination.chest;
                        int capturedRouteX = itemDestination.routeX;
                        int capturedRouteY = itemDestination.routeY;
                        HouseDetails capturedInside = inside;
                        System.Action depositHarvest = () =>
                        {
                            if (!HarvestHelper.TryDepositHarvestToChest(capturedChest, capturedInside, capturedItemId, 1,
                                () => AutomationCreditHelper.TryGrantHarvestOutputCredit(sourceTileObjectId, capturedItemId, 1)))
                            {
                                ConveyorHelper.FallbackDepositToAnyChest(capturedChest.xPos, capturedChest.yPos, capturedInside, capturedItemId, 1,
                                    () => AutomationCreditHelper.TryGrantHarvestOutputCredit(sourceTileObjectId, capturedItemId, 1));
                            }
                        };

                        ConveyorAnimator.AnimateTransfer(itemId, 1,
                            new Vector2Int(xPos, yPos),
                            new Vector2Int(capturedRouteX, capturedRouteY), inside, depositHarvest,
                            HarvestHelper.GetNextDayChangeAnimationDelay(capturedChest));

                        Plugin.Log.LogInfo("Autom8er: Crab pot harvest -> chest: " + Inventory.Instance.allItems[itemId].itemName);
                    }
                }
            }
            else
            {
                // Regular harvest drops (bee houses, key cutters, worm farms, etc.)
                // Handle harvestDrop
                if (__instance.harvestDrop != null)
                {
                    int itemId = Inventory.Instance.getInvItemId(__instance.harvestDrop);
                    if (itemId != -1)
                    {
                        // Multiple harvest spots = multiple items
                        int numSpots = __instance.harvestSpots != null ? __instance.harvestSpots.Length : 1;
                        if (numSpots < 1) numSpots = 1;

                        for (int i = 0; i < numSpots; i++)
                        {
                            ConveyorHelper.OutputDestination itemDestination = HarvestHelper.FindBestDestinationForHarvestOutput(xPos, yPos, inside, itemId, searchRadius);
                            if (itemDestination != null)
                            {
                                harvestedAny = true;
                                int capturedItemId = itemId;
                                Chest capturedChest = itemDestination.chest;
                                int capturedRouteX = itemDestination.routeX;
                                int capturedRouteY = itemDestination.routeY;
                                HouseDetails capturedInside = inside;
                                System.Action depositHarvest = () =>
                                {
                                    if (!HarvestHelper.TryDepositHarvestToChest(capturedChest, capturedInside, capturedItemId, 1,
                                        () => AutomationCreditHelper.TryGrantHarvestOutputCredit(sourceTileObjectId, capturedItemId, 1)))
                                    {
                                        ConveyorHelper.FallbackDepositToAnyChest(capturedChest.xPos, capturedChest.yPos, capturedInside, capturedItemId, 1,
                                            () => AutomationCreditHelper.TryGrantHarvestOutputCredit(sourceTileObjectId, capturedItemId, 1));
                                    }
                                };

                                ConveyorAnimator.AnimateTransfer(itemId, 1,
                                    new Vector2Int(xPos, yPos),
                                    new Vector2Int(capturedRouteX, capturedRouteY), inside, depositHarvest,
                                    HarvestHelper.GetNextDayChangeAnimationDelay(capturedChest));

                                Plugin.Log.LogInfo("Autom8er: Harvest -> chest: " + Inventory.Instance.allItems[itemId].itemName);
                            }
                        }
                    }
                }

                // Handle loot table drops
                if (__instance.dropsFromLootTable != null)
                {
                    int numSpots = __instance.harvestSpots != null ? __instance.harvestSpots.Length : 1;
                    if (numSpots < 1) numSpots = 1;

                    for (int i = 0; i < numSpots; i++)
                    {
                        InventoryItem dropItem = __instance.dropsFromLootTable.getRandomDropFromTable();
                        if (dropItem != null)
                        {
                            int itemId = Inventory.Instance.getInvItemId(dropItem);
                            if (itemId != -1)
                            {
                                ConveyorHelper.OutputDestination itemDestination = HarvestHelper.FindBestDestinationForHarvestOutput(xPos, yPos, inside, itemId, searchRadius);
                                if (itemDestination != null)
                                {
                                    harvestedAny = true;
                                    int capturedItemId = itemId;
                                    Chest capturedChest = itemDestination.chest;
                                    int capturedRouteX = itemDestination.routeX;
                                    int capturedRouteY = itemDestination.routeY;
                                    HouseDetails capturedInside = inside;
                                    System.Action depositHarvest = () =>
                                    {
                                        if (!HarvestHelper.TryDepositHarvestToChest(capturedChest, capturedInside, capturedItemId, 1,
                                            () => AutomationCreditHelper.TryGrantHarvestOutputCredit(sourceTileObjectId, capturedItemId, 1)))
                                        {
                                            ConveyorHelper.FallbackDepositToAnyChest(capturedChest.xPos, capturedChest.yPos, capturedInside, capturedItemId, 1,
                                                () => AutomationCreditHelper.TryGrantHarvestOutputCredit(sourceTileObjectId, capturedItemId, 1));
                                        }
                                    };

                                    ConveyorAnimator.AnimateTransfer(itemId, 1,
                                        new Vector2Int(xPos, yPos),
                                        new Vector2Int(capturedRouteX, capturedRouteY), inside, depositHarvest,
                                        HarvestHelper.GetNextDayChangeAnimationDelay(capturedChest));

                                    Plugin.Log.LogInfo("Autom8er: Harvest (loot) -> chest: " + Inventory.Instance.allItems[itemId].itemName);
                                }
                            }
                        }
                    }
                }
            }

            if (harvestedAny)
            {
                return false; // Skip original (we handled it)
            }

            return true; // Let original run
        }
    }

    // Clear conveyor animations before saving so no items are lost in transit
    [HarmonyPatch(typeof(SaveLoad), "SaveGame")]
    public static class SaveGameAnimationClearPatch
    {
        static void Prefix()
        {
            ConveyorAnimator.ClearAllAnimations();
            QuarryHelper.ClearState();
        }
    }

    // On game load, clear stale static state from the previous session.
    // Coroutines that would have cleaned pendingSorts are destroyed on scene reload.
    [HarmonyPatch(typeof(SaveLoad), "loadOverFrames")]
    public static class LoadGameStateClearPatch
    {
        static void Prefix()
        {
            ConveyorAnimator.ClearAllAnimations();
            ConveyorHelper.ClearPendingSorts();
            QuarryHelper.ClearState();
            Plugin.QueueLoadCatchUp();
        }

        static void Postfix()
        {
            Plugin.QueueLoadCatchUp();
        }
    }

    // Patch to trigger harvest processing after day change completes
    // This is much more efficient than continuous scanning since growth machines
    // (bee houses, key cutters, worm farms, crab pots) only update on day change
    [HarmonyPatch(typeof(WorldManager), "refreshAllChunksNewDay")]
    public static class NewDayHarvestPatch
    {
        private const float DAY_CHANGE_PHASE_DELAY_SECONDS = 1f;
        private const float POST_QUARRY_PHASE_DELAY_SECONDS = 0.5f;

        // MoveNext is the actual coroutine method generated by the compiler
        static void Postfix()
        {
            // This runs when the coroutine starts, not when it finishes
            // We need to delay the harvest processing
            if (NetworkMapSharer.Instance.isServer)
            {
                // Use a coroutine to delay harvest processing until chunks are refreshed
                WorldManager.Instance.StartCoroutine(RunPhasedHarvestProcessing(2f, "Autom8er: Processing day-change harvests...", "Autom8er: Day-change harvest processing complete"));
            }
        }

        public static System.Collections.IEnumerator RunPhasedHarvestProcessing(float initialDelay, string startLog, string endLog)
        {
            if (initialDelay > 0f)
                yield return new UnityEngine.WaitForSeconds(initialDelay);

            if (NetworkMapSharer.Instance == null || !NetworkMapSharer.Instance.isServer || WorldManager.Instance == null)
                yield break;

            Plugin.Log.LogInfo(startLog);
            HarvestHelper.ProcessHarvestableMachines();
            yield return new WaitForSeconds(DAY_CHANGE_PHASE_DELAY_SECONDS);
            FishPondHelper.ProcessPondAndTerrariumOutput();
            yield return new WaitForSeconds(DAY_CHANGE_PHASE_DELAY_SECONDS);
            QuarryHelper.ProcessDayChangeQuarries();
            yield return new WaitForSeconds(POST_QUARRY_PHASE_DELAY_SECONDS);
            yield return WorldManager.Instance.StartCoroutine(VacuumCrateHelper.ProcessDayChangeVacuumHarvests());
            Plugin.Log.LogInfo(endLog);
        }
    }

    [HarmonyPatch(typeof(NetworkMapSharer), "spawnAServerDrop", new System.Type[]
    {
        typeof(int), typeof(int), typeof(Vector3), typeof(HouseDetails), typeof(bool), typeof(int)
    })]
    public static class QuarryServerDropCapturePatch
    {
        static bool Prefix(int itemId, int stackAmount, Vector3 position, HouseDetails inside, bool tryNotToStack, int xPType)
        {
            return !QuarryHelper.TryCaptureServerDrop(itemId, stackAmount, position);
        }
    }

    [HarmonyPatch(typeof(NetworkMapSharer), "spawnAServerDrop", new System.Type[]
    {
        typeof(int), typeof(int), typeof(Vector3), typeof(Vector3), typeof(HouseDetails), typeof(bool), typeof(int)
    })]
    public static class QuarryDirectedServerDropCapturePatch
    {
        static bool Prefix(int itemId, int stackAmount, Vector3 position, Vector3 desiredPos, HouseDetails inside, bool tryNotToStack, int xPType)
        {
            return !QuarryHelper.TryCaptureServerDrop(itemId, stackAmount, position);
        }
    }

    [HarmonyPatch(typeof(NetworkMapSharer), "RpcSpawnATileObjectDrop")]
    public static class QuarryObjectDropSuppressPatch
    {
        static bool Prefix(int tileObjectToSpawnFrom, int xPos, int yPos, int tileStatus)
        {
            return !QuarryHelper.ShouldSuppressQuarryObjectDrop(xPos, yPos);
        }
    }

    public static class HarvestHelper
    {
        private static readonly int[] dx = { -1, 0, 1, 0 };
        private static readonly int[] dy = { 0, -1, 0, 1 };
        private const int DAY_CHANGE_ANIMATION_BATCH_SIZE = 100;
        private const float DAY_CHANGE_ANIMATION_BATCH_DELAY = 0.2f;
        private static Dictionary<long, int> dayChangeAnimationCounts = new Dictionary<long, int>();

        // Used to pass inside context from TryAutoHarvestAt to HarvestPatch
        public static HouseDetails CurrentHarvestInside = null;

        public static void ResetDayChangeAnimationStagger()
        {
            dayChangeAnimationCounts.Clear();
        }

        public static float GetNextDayChangeAnimationDelay(Chest chest)
        {
            if (chest == null)
                return 0f;

            long chestKey = (((long)chest.xPos & 0xFFFFFL) << 44)
                | (((long)chest.yPos & 0xFFFFFL) << 24)
                | (((long)(chest.insideX + 1) & 0xFFFL) << 12)
                | ((long)(chest.insideY + 1) & 0xFFFL);

            int currentCount = 0;
            dayChangeAnimationCounts.TryGetValue(chestKey, out currentCount);
            dayChangeAnimationCounts[chestKey] = currentCount + 1;
            return (currentCount / DAY_CHANGE_ANIMATION_BATCH_SIZE) * DAY_CHANGE_ANIMATION_BATCH_DELAY;
        }

        public static Chest FindAdjacentChestForHarvest(int machineX, int machineY, HouseDetails inside, int searchRadius = 1)
        {
            // Check tiles in expanding radius (1-tile first, then 2-tile if specified)
            for (int radius = 1; radius <= searchRadius; radius++)
            {
                // Check all tiles within this radius
                for (int offsetX = -radius; offsetX <= radius; offsetX++)
                {
                    for (int offsetY = -radius; offsetY <= radius; offsetY++)
                    {
                        // Skip the center tile (the machine itself)
                        if (offsetX == 0 && offsetY == 0)
                            continue;

                        // Only check tiles at exactly this radius distance (Manhattan distance)
                        // For radius 1: adjacent tiles. For radius 2: tiles 2 away in any direction
                        int manhattanDist = Mathf.Abs(offsetX) + Mathf.Abs(offsetY);
                        if (manhattanDist > radius)
                            continue;

                        // For radius > 1, skip tiles we already checked in previous radius
                        if (radius > 1 && manhattanDist < radius)
                            continue;

                        int checkX = machineX + offsetX;
                        int checkY = machineY + offsetY;

                        if (checkX < 0 || checkY < 0)
                            continue;

                        // Skip input-only containers (white crate)
                        if (ConveyorHelper.IsInputOnlyContainer(checkX, checkY, inside))
                            continue;

                        Chest chest = ConveyorHelper.FindChestAt(checkX, checkY, inside);
                        if (chest != null && !ConveyorHelper.IsSpecialContainer(checkX, checkY, inside))
                        {
                            return chest;
                        }
                    }
                }
            }

            // Also check via conveyor path
            return FindChestViaConveyorPath(machineX, machineY, inside);
        }

        public static ConveyorHelper.OutputDestination FindBestDestinationForHarvestOutput(int machineX, int machineY, HouseDetails inside, int itemId, int searchRadius = 1)
        {
            ConveyorHelper.OutputDestination destination;
            if (ConveyorHelper.TryFindBestHarvestOutputDestination(machineX, machineY, inside, itemId, searchRadius, out destination))
                return destination;

            return null;
        }

        public static Chest FindBestChestForHarvestOutput(int machineX, int machineY, HouseDetails inside, int itemId, int searchRadius = 1)
        {
            ConveyorHelper.OutputDestination destination = FindBestDestinationForHarvestOutput(machineX, machineY, inside, itemId, searchRadius);
            return destination != null ? destination.chest : null;
        }

        public static Chest FindChestViaConveyorPath(int startX, int startY, HouseDetails inside)
        {
            if (Plugin.ConveyorTileType < 0)
                return null;

            // Conveyor paths not supported inside houses
            if (inside != null)
                return null;

            HashSet<long> visited = new HashSet<long>();
            Queue<(int x, int y)> queue = new Queue<(int x, int y)>();

            // Start by checking conveyor tiles within 2-tile radius (for crab pots in water)
            for (int offsetX = -2; offsetX <= 2; offsetX++)
            {
                for (int offsetY = -2; offsetY <= 2; offsetY++)
                {
                    if (offsetX == 0 && offsetY == 0)
                        continue;

                    if (Mathf.Abs(offsetX) + Mathf.Abs(offsetY) > 2)
                        continue;

                    int nx = startX + offsetX;
                    int ny = startY + offsetY;

                    if (nx < 0 || ny < 0)
                        continue;

                    if (ConveyorHelper.IsConveyorTile(nx, ny, null))
                    {
                        long key = ((long)nx << 32) | (uint)ny;
                        if (!visited.Contains(key))
                        {
                            queue.Enqueue((nx, ny));
                            visited.Add(key);
                        }
                    }
                }
            }

            // BFS along conveyor tiles to find a chest. visited prevents infinite loops.
            while (queue.Count > 0)
            {
                var (cx, cy) = queue.Dequeue();

                // Check for chest ADJACENT to this conveyor tile (chests are next to conveyors, not on them)
                for (int i = 0; i < 4; i++)
                {
                    int adjX = cx + dx[i];
                    int adjY = cy + dy[i];

                    if (adjX < 0 || adjY < 0)
                        continue;

                    Chest chest = ConveyorHelper.FindChestAt(adjX, adjY, null);
                    if (chest != null && !ConveyorHelper.IsSpecialContainer(adjX, adjY, null) && !ConveyorHelper.IsInputOnlyContainer(adjX, adjY, null))
                    {
                        return chest;
                    }
                }

                // Expand to adjacent conveyor tiles
                for (int i = 0; i < 4; i++)
                {
                    int nx = cx + dx[i];
                    int ny = cy + dy[i];

                    if (nx < 0 || ny < 0)
                        continue;

                    long key = ((long)nx << 32) | (uint)ny;
                    if (visited.Contains(key))
                        continue;

                    if (ConveyorHelper.IsConveyorTile(nx, ny, null))
                    {
                        visited.Add(key);
                        queue.Enqueue((nx, ny));
                    }
                }
            }

            return null;
        }

        public static bool TryDepositHarvestToChest(Chest chest, HouseDetails inside, int itemId, int amount, System.Action onDepositSuccess = null)
        {
            if (chest == null || itemId < 0)
                return false;

            int slotIndex = ConveyorHelper.FindSlotForItem(chest, itemId);
            if (slotIndex == -1)
                return false;

            bool isStackable = Inventory.Instance.allItems[itemId].checkIfStackable();

            if (isStackable && chest.itemIds[slotIndex] == itemId)
            {
                ContainerManager.manage.changeSlotInChest(
                    chest.xPos, chest.yPos, slotIndex,
                    itemId, chest.itemStacks[slotIndex] + amount, inside);
            }
            else
            {
                ContainerManager.manage.changeSlotInChest(
                    chest.xPos, chest.yPos, slotIndex,
                    itemId, amount, inside);
            }

            // Queue AutoSorter to batch-fire items to nearby chests
            if (ConveyorHelper.IsAutoSorter(chest))
                ConveyorHelper.QueueAutoSorterUpdate(chest);

            if (onDepositSuccess != null)
                onDepositSuccess();

            return true;
        }

        // Scan for harvestable machines and auto-trigger harvest
        public static int ProcessHarvestableMachines()
        {
            if (!NetworkMapSharer.Instance.isServer)
                return 0;

            ResetDayChangeAnimationStagger();

            HashSet<long> checkedPositions = new HashSet<long>();
            int harvestedCount = 0;

            // Create a copy of the list to avoid "Collection was modified" exception
            List<Chest> chestsCopy = new List<Chest>(ContainerManager.manage.activeChests);

            // Scan machines adjacent to chests and conveyor paths
            foreach (Chest chest in chestsCopy)
            {
                if (chest == null)
                    continue;

                // Skip input-only containers
                HouseDetails inside = null;
                if (chest.insideX != -1 && chest.insideY != -1)
                {
                    inside = HouseManager.manage.getHouseInfoIfExists(chest.insideX, chest.insideY);
                }

                if (ConveyorHelper.IsInputOnlyContainer(chest.xPos, chest.yPos, inside))
                    continue;

                // Check tiles in 2-tile radius for harvestable machines
                // This is important for crab pots which are in water (1-2 tiles from shore/chest)
                for (int offsetX = -2; offsetX <= 2; offsetX++)
                {
                    for (int offsetY = -2; offsetY <= 2; offsetY++)
                    {
                        // Skip the chest tile itself
                        if (offsetX == 0 && offsetY == 0)
                            continue;

                        // Only check tiles within Manhattan distance of 2
                        int manhattanDist = Mathf.Abs(offsetX) + Mathf.Abs(offsetY);
                        if (manhattanDist > 2)
                            continue;

                        int checkX = chest.xPos + offsetX;
                        int checkY = chest.yPos + offsetY;

                        if (checkX < 0 || checkY < 0)
                            continue;

                        long key = ((long)checkX << 32) | (uint)checkY;
                        if (!checkedPositions.Contains(key))
                        {
                            harvestedCount += ScanConnectedHarvestArray(checkX, checkY, inside, checkedPositions);
                        }
                    }
                }

                // Also scan machines adjacent to conveyor tiles connected to this chest
                harvestedCount += ScanConveyorPathForHarvestables(chest.xPos, chest.yPos, inside, checkedPositions);
            }

            return harvestedCount;
        }

        // Scan along conveyor path and check all adjacent tiles for harvestable machines
        private static int ScanConveyorPathForHarvestables(int startX, int startY, HouseDetails inside, HashSet<long> checkedPositions)
        {
            if (Plugin.ConveyorTileType < 0)
                return 0;

            // Conveyor paths not supported inside houses
            if (inside != null)
                return 0;

            HashSet<long> visitedConveyors = new HashSet<long>();
            Queue<(int x, int y)> queue = new Queue<(int x, int y)>();
            int harvestedCount = 0;

            // Start by checking adjacent conveyor tiles from chest
            for (int i = 0; i < 4; i++)
            {
                int nx = startX + dx[i];
                int ny = startY + dy[i];

                if (nx < 0 || ny < 0)
                    continue;

                if (ConveyorHelper.IsConveyorTile(nx, ny, null))
                {
                    long key = ((long)nx << 32) | (uint)ny;
                    if (!visitedConveyors.Contains(key))
                    {
                        visitedConveyors.Add(key);
                        queue.Enqueue((nx, ny));
                    }
                }
            }

            // BFS along conveyor tiles, checking tiles within 2-tile radius for harvestables.
            // visitedConveyors prevents infinite loops.
            while (queue.Count > 0)
            {
                var (cx, cy) = queue.Dequeue();

                // Check 2-tile radius around each conveyor tile for harvestable machines
                for (int offsetX = -2; offsetX <= 2; offsetX++)
                {
                    for (int offsetY = -2; offsetY <= 2; offsetY++)
                    {
                        if (offsetX == 0 && offsetY == 0)
                            continue;

                        // Manhattan distance for 2-tile radius
                        if (Mathf.Abs(offsetX) + Mathf.Abs(offsetY) > 2)
                            continue;

                        int checkX = cx + offsetX;
                        int checkY = cy + offsetY;

                        if (checkX < 0 || checkY < 0)
                            continue;

                        long machineKey = ((long)checkX << 32) | (uint)checkY;
                        if (!checkedPositions.Contains(machineKey))
                        {
                            harvestedCount += ScanConnectedHarvestArray(checkX, checkY, null, checkedPositions);
                        }
                    }
                }

                // Expand to adjacent conveyor tiles
                for (int i = 0; i < 4; i++)
                {
                    int nx = cx + dx[i];
                    int ny = cy + dy[i];

                    if (nx < 0 || ny < 0)
                        continue;

                    long key = ((long)nx << 32) | (uint)ny;
                    if (visitedConveyors.Contains(key))
                        continue;

                    if (ConveyorHelper.IsConveyorTile(nx, ny, null))
                    {
                        visitedConveyors.Add(key);
                        queue.Enqueue((nx, ny));
                    }
                }
            }

            return harvestedCount;
        }

        private static int ScanConnectedHarvestArray(int seedX, int seedY, HouseDetails inside, HashSet<long> checkedPositions)
        {
            int mapW = inside != null ? inside.houseMapOnTile.GetLength(0) : WorldManager.Instance.onTileMap.GetLength(0);
            int mapH = inside != null ? inside.houseMapOnTile.GetLength(1) : WorldManager.Instance.onTileMap.GetLength(1);

            int rootX;
            int rootY;
            int tileObjectId;
            if (!TryGetHarvestArrayRoot(seedX, seedY, inside, out rootX, out rootY, out tileObjectId))
                return 0;

            Queue<(int x, int y)> queue = new Queue<(int x, int y)>();
            HashSet<long> queuedRoots = new HashSet<long>();
            int harvestedCount = 0;

            long seedKey = ((long)rootX << 32) | (uint)rootY;
            queue.Enqueue((rootX, rootY));
            queuedRoots.Add(seedKey);

            while (queue.Count > 0)
            {
                var (currentX, currentY) = queue.Dequeue();
                long currentKey = ((long)currentX << 32) | (uint)currentY;
                if (checkedPositions.Contains(currentKey))
                    continue;

                checkedPositions.Add(currentKey);
                if (TryAutoHarvestAt(currentX, currentY, inside))
                    harvestedCount++;

                TileObject currentTileObj = WorldManager.Instance.allObjects[tileObjectId];
                TileObjectGrowthStages currentGrowth = currentTileObj != null ? currentTileObj.tileObjectGrowthStages : null;
                int searchRadius = currentGrowth != null && currentGrowth.isCrabPot ? 2 : 1;

                for (int offsetX = -searchRadius; offsetX <= searchRadius; offsetX++)
                {
                    for (int offsetY = -searchRadius; offsetY <= searchRadius; offsetY++)
                    {
                        if (offsetX == 0 && offsetY == 0)
                            continue;

                        if (Mathf.Abs(offsetX) + Mathf.Abs(offsetY) > searchRadius)
                            continue;

                        int nextX = currentX + offsetX;
                        int nextY = currentY + offsetY;
                        if (nextX < 0 || nextY < 0 || nextX >= mapW || nextY >= mapH)
                            continue;

                        int nextRootX;
                        int nextRootY;
                        int nextTileObjectId;
                        if (!TryGetHarvestArrayRoot(nextX, nextY, inside, out nextRootX, out nextRootY, out nextTileObjectId))
                            continue;

                        if (nextTileObjectId != tileObjectId)
                            continue;

                        long nextKey = ((long)nextRootX << 32) | (uint)nextRootY;
                        if (queuedRoots.Add(nextKey))
                            queue.Enqueue((nextRootX, nextRootY));
                    }
                }
            }

            return harvestedCount;
        }

        private static bool TryGetHarvestArrayRoot(int xPos, int yPos, HouseDetails inside, out int rootX, out int rootY, out int tileObjectId)
        {
            rootX = xPos;
            rootY = yPos;
            tileObjectId = inside != null ? inside.houseMapOnTile[xPos, yPos] : WorldManager.Instance.onTileMap[xPos, yPos];

            if (tileObjectId < -1)
            {
                Vector2 rootPos = WorldManager.Instance.findMultiTileObjectPos(xPos, yPos);
                rootX = (int)rootPos.x;
                rootY = (int)rootPos.y;
                tileObjectId = inside != null ? inside.houseMapOnTile[rootX, rootY] : WorldManager.Instance.onTileMap[rootX, rootY];
            }

            if (tileObjectId < 0)
                return false;

            TileObject tileObj = WorldManager.Instance.allObjects[tileObjectId];
            if (tileObj == null || tileObj.tileObjectGrowthStages == null)
                return false;

            TileObjectGrowthStages growth = tileObj.tileObjectGrowthStages;
            if (growth.spawnsFarmAnimal)
                return false;

            return true;
        }

        public static bool TryAutoHarvestAt(int xPos, int yPos, HouseDetails inside)
        {
            int tileObjectId = inside != null
                ? inside.houseMapOnTile[xPos, yPos]
                : WorldManager.Instance.onTileMap[xPos, yPos];

            if (tileObjectId < 0)
                return false;

            TileObject tileObj = WorldManager.Instance.allObjects[tileObjectId];
            if (tileObj == null || tileObj.tileObjectGrowthStages == null)
                return false;

            TileObjectGrowthStages growth = tileObj.tileObjectGrowthStages;

            // NEVER auto-harvest incubators or anything that spawns farm animals
            if (growth.spawnsFarmAnimal)
                return false;

            // Check if harvestable (not manually harvestable check - we want auto-harvest)
            int currentStatus = inside != null
                ? inside.houseMapOnTileStatus[xPos, yPos]
                : WorldManager.Instance.onTileStatusMap[xPos, yPos];

            // canBeHarvested checks if at final growth stage
            if (!growth.canBeHarvested(currentStatus))
                return false;

            // Determine search radius - crab pots need 2-tile radius due to water placement
            int searchRadius = growth.isCrabPot ? 2 : 1;

            // IMPORTANT: Only harvest if we have a valid output chest
            // This prevents items from being lost when no chest is nearby
            Chest outputChest = FindAdjacentChestForHarvest(xPos, yPos, inside, searchRadius);
            if (outputChest == null)
                return false;

            // Set the inside context for the HarvestPatch to use
            CurrentHarvestInside = inside;

            try
            {
                AutomationCreditHelper.TryGrantGrowthHarvestCredit(tileObjectId);
                AutomationCreditHelper.TryGrantHarvestMilestone(tileObjectId);

                // This machine is ready to harvest - trigger harvest
                // The Harmony patch will redirect output to chest
                growth.harvest(xPos, yPos);

                // Update tile status after harvest
                if (growth.diesOnHarvest)
                {
                    // Machine dies after harvest (some plants)
                    NetworkMapSharer.Instance.RpcClearOnTileObjectNoDrop(xPos, yPos);
                }
                else if (growth.takeOrAddFromStateOnHarvest != 0)
                {
                    // Reset to earlier growth stage for re-harvest
                    int newStatus = currentStatus + growth.takeOrAddFromStateOnHarvest;
                    if (newStatus < 0) newStatus = 0;

                    if (inside != null)
                    {
                        inside.houseMapOnTileStatus[xPos, yPos] = newStatus;
                    }
                    else
                    {
                        WorldManager.Instance.onTileStatusMap[xPos, yPos] = newStatus;
                    }
                    NetworkMapSharer.Instance.RpcGiveOnTileStatus(newStatus, xPos, yPos);
                }

                Plugin.Log.LogInfo("Autom8er: Auto-harvested machine at " + xPos + "," + yPos);
                return true;
            }
            finally
            {
                // Always clear the context
                CurrentHarvestInside = null;
            }
        }
    }

    public static class QuarryHelper
    {
        private class QuarryNodeHarvest
        {
            public int x;
            public int y;
            public int tileObjectId;
            public float itemAnimationDelay;
        }

        private class QuarryCapturedDrop
        {
            public int itemId;
            public int stackAmount;
            public Vector3 startWorld;
        }

        private class QuarryCaptureContext
        {
            public int nodeX;
            public int nodeY;
            public List<QuarryCapturedDrop> drops = new List<QuarryCapturedDrop>();
        }

        private static readonly int[] dx = { -1, 0, 1, 0 };
        private static readonly int[] dy = { 0, -1, 0, 1 };
        private const int QUARRY_TILE_OBJECT_ID = 190;
        private const float QUARRY_ITEM_POP_DURATION = 0.2f;
        private const float QUARRY_ITEM_POP_ARC_HEIGHT = 0.9f;
        private const int QUARRY_BREAK_BATCH_SIZE = 5;
        private const float QUARRY_BREAK_BATCH_DELAY = 0.2f;
        private const float QUARRY_INITIAL_BREAK_DELAY = 1f;

        private static QuarryCaptureContext activeCapture = null;
        private static Dictionary<long, int> quarryBreakCounts = new Dictionary<long, int>();
        private static int quarryRunGeneration = 0;

        public static void ClearState()
        {
            activeCapture = null;
            quarryBreakCounts.Clear();
            quarryRunGeneration++;
        }

        public static int ProcessDayChangeQuarries()
        {
            if (!NetworkMapSharer.Instance.isServer || ContainerManager.manage == null)
                return 0;

            HashSet<int> quarryNodeIds = CollectKnownQuarryNodeIds();
            if (quarryNodeIds.Count == 0)
                return 0;

            List<Chest> chestsCopy = new List<Chest>(ContainerManager.manage.activeChests);
            if (chestsCopy.Count == 0)
                return 0;

            ResetQuarryBreakStagger();

            int runGeneration = ++quarryRunGeneration;
            HashSet<long> processedQuarries = new HashSet<long>();
            int scheduledQuarryCount = 0;

            foreach (Chest chest in chestsCopy)
            {
                if (chest == null)
                    continue;

                HouseDetails inside = null;
                if (chest.insideX != -1 && chest.insideY != -1)
                    inside = HouseManager.manage.getHouseInfoIfExists(chest.insideX, chest.insideY);

                if (inside != null)
                    continue;

                if (ConveyorHelper.IsInputOnlyContainer(chest.xPos, chest.yPos, null))
                    continue;

                if (ConveyorHelper.IsSpecialContainer(chest.xPos, chest.yPos, null))
                    continue;

                scheduledQuarryCount += ScanChestForQuarries(chest, processedQuarries, quarryNodeIds, runGeneration);
            }

            return scheduledQuarryCount;
        }

        public static bool TryCaptureServerDrop(int itemId, int stackAmount, Vector3 position)
        {
            QuarryCaptureContext capture = activeCapture;
            if (capture == null)
                return false;

            if (itemId >= 0 && stackAmount > 0)
            {
                capture.drops.Add(new QuarryCapturedDrop
                {
                    itemId = itemId,
                    stackAmount = stackAmount,
                    startWorld = position
                });
            }

            return true;
        }

        private static List<QuarryCapturedDrop> BuildGroupedCapturedDrops(List<QuarryCapturedDrop> drops)
        {
            List<QuarryCapturedDrop> groupedDrops = new List<QuarryCapturedDrop>();
            if (drops == null || drops.Count == 0)
                return groupedDrops;

            Dictionary<int, QuarryCapturedDrop> stackableGroups = new Dictionary<int, QuarryCapturedDrop>();

            for (int i = 0; i < drops.Count; i++)
            {
                QuarryCapturedDrop drop = drops[i];
                if (drop == null || drop.itemId < 0 || drop.stackAmount <= 0)
                    continue;

                InventoryItem item = Inventory.Instance != null ? Inventory.Instance.allItems[drop.itemId] : null;
                bool isStackable = item != null && item.checkIfStackable();

                if (!isStackable)
                {
                    groupedDrops.Add(drop);
                    continue;
                }

                if (!stackableGroups.TryGetValue(drop.itemId, out QuarryCapturedDrop grouped))
                {
                    grouped = new QuarryCapturedDrop
                    {
                        itemId = drop.itemId,
                        stackAmount = 0,
                        startWorld = drop.startWorld
                    };
                    stackableGroups[drop.itemId] = grouped;
                    groupedDrops.Add(grouped);
                }

                grouped.stackAmount += drop.stackAmount;
            }

            return groupedDrops;
        }

        public static bool ShouldSuppressQuarryObjectDrop(int xPos, int yPos)
        {
            return activeCapture != null && activeCapture.nodeX == xPos && activeCapture.nodeY == yPos;
        }

        private static int ScanChestForQuarries(Chest chest, HashSet<long> processedQuarries, HashSet<int> quarryNodeIds, int runGeneration)
        {
            int scheduledQuarryCount = 0;

            for (int i = 0; i < 4; i++)
            {
                int checkX = chest.xPos + dx[i];
                int checkY = chest.yPos + dy[i];
                if (TryHarvestQuarryOutputsAt(checkX, checkY, chest, processedQuarries, quarryNodeIds, runGeneration))
                    scheduledQuarryCount++;
            }

            if (Plugin.ConveyorTileType == -1)
                return scheduledQuarryCount;

            HashSet<long> visitedConveyors = new HashSet<long>();
            Queue<Vector2Int> queue = new Queue<Vector2Int>();

            for (int i = 0; i < 4; i++)
            {
                int nx = chest.xPos + dx[i];
                int ny = chest.yPos + dy[i];
                if (nx < 0 || ny < 0)
                    continue;

                if (!ConveyorHelper.IsConveyorTile(nx, ny, null))
                    continue;

                long key = PosKey(nx, ny);
                if (visitedConveyors.Add(key))
                    queue.Enqueue(new Vector2Int(nx, ny));
            }

            while (queue.Count > 0)
            {
                Vector2Int current = queue.Dequeue();

                for (int i = 0; i < 4; i++)
                {
                    int quarryX = current.x + dx[i];
                    int quarryY = current.y + dy[i];
                    if (TryHarvestQuarryOutputsAt(quarryX, quarryY, chest, processedQuarries, quarryNodeIds, runGeneration))
                        scheduledQuarryCount++;
                }

                for (int i = 0; i < 4; i++)
                {
                    int nx = current.x + dx[i];
                    int ny = current.y + dy[i];
                    if (nx < 0 || ny < 0)
                        continue;

                    if (!ConveyorHelper.IsConveyorTile(nx, ny, null))
                        continue;

                    long key = PosKey(nx, ny);
                    if (visitedConveyors.Add(key))
                        queue.Enqueue(new Vector2Int(nx, ny));
                }
            }

            return scheduledQuarryCount;
        }

        private static bool TryHarvestQuarryOutputsAt(int quarryX, int quarryY, Chest sourceChest, HashSet<long> processedQuarries, HashSet<int> quarryNodeIds, int runGeneration)
        {
            if (!WorldManager.Instance.isPositionOnMap(quarryX, quarryY))
                return false;

            if (WorldManager.Instance.onTileMap[quarryX, quarryY] != QUARRY_TILE_OBJECT_ID)
                return false;

            long quarryKey = PosKey(quarryX, quarryY);
            if (processedQuarries.Contains(quarryKey))
                return false;

            List<QuarryNodeHarvest> nodesToHarvest = new List<QuarryNodeHarvest>();

            for (int i = 0; i < 4; i++)
            {
                int nodeX = quarryX + dx[i];
                int nodeY = quarryY + dy[i];
                if (!WorldManager.Instance.isPositionOnMap(nodeX, nodeY))
                    continue;

                if (WorldManager.Instance.heightMap[nodeX, nodeY] != WorldManager.Instance.heightMap[quarryX, quarryY])
                    continue;

                int nodeTileObjectId = WorldManager.Instance.onTileMap[nodeX, nodeY];
                if (!IsLikelyQuarryNode(nodeTileObjectId, quarryNodeIds))
                    continue;

                nodesToHarvest.Add(new QuarryNodeHarvest
                {
                    x = nodeX,
                    y = nodeY,
                    tileObjectId = nodeTileObjectId,
                    itemAnimationDelay = HarvestHelper.GetNextDayChangeAnimationDelay(sourceChest)
                });
            }

            if (nodesToHarvest.Count == 0)
                return false;

            processedQuarries.Add(quarryKey);

            float breakDelay = GetNextQuarryBreakDelay(sourceChest);
            if (breakDelay <= 0f)
            {
                BreakQuarryOutputs(runGeneration, quarryX, quarryY, nodesToHarvest);
                return true;
            }

            WorldManager.Instance.StartCoroutine(BreakQuarryOutputsAfterDelay(runGeneration, quarryX, quarryY, nodesToHarvest, breakDelay));
            return true;
        }

        private static System.Collections.IEnumerator BreakQuarryOutputsAfterDelay(int runGeneration, int quarryX, int quarryY, List<QuarryNodeHarvest> nodesToHarvest, float breakDelay)
        {
            yield return new WaitForSeconds(breakDelay);

            if (runGeneration != quarryRunGeneration)
                yield break;

            BreakQuarryOutputs(runGeneration, quarryX, quarryY, nodesToHarvest);
        }

        private static void BreakQuarryOutputs(int runGeneration, int quarryX, int quarryY, List<QuarryNodeHarvest> nodesToHarvest)
        {
            if (runGeneration != quarryRunGeneration)
                return;

            for (int i = 0; i < nodesToHarvest.Count; i++)
            {
                QuarryNodeHarvest node = nodesToHarvest[i];
                if (!WorldManager.Instance.isPositionOnMap(node.x, node.y))
                    continue;

                if (WorldManager.Instance.onTileMap[node.x, node.y] != node.tileObjectId)
                    continue;

                HarvestQuarryNodeToStorage(node.x, node.y, quarryX, quarryY, node.tileObjectId, node.itemAnimationDelay);
            }
        }

        private static void HarvestQuarryNodeToStorage(int nodeX, int nodeY, int quarryX, int quarryY, int tileObjectId, float animationDelay)
        {
            TileObject liveTileObject = WorldManager.Instance.findTileObjectInUse(nodeX, nodeY);
            if (liveTileObject != null)
                liveTileObject.onDeath();

            AutomationCreditHelper.TryGrantTileObjectBreakCredit(tileObjectId);

            Vector3 worldPos = new Vector3(nodeX * 2, WorldManager.Instance.heightMap[nodeX, nodeY], nodeY * 2);
            TileObject tileObjectForServerDrop = WorldManager.Instance.getTileObjectForServerDrop(tileObjectId, worldPos);
            QuarryCaptureContext capture = new QuarryCaptureContext
            {
                nodeX = nodeX,
                nodeY = nodeY
            };

            activeCapture = capture;
            try
            {
                tileObjectForServerDrop.onDeathServer(nodeX, nodeY);
            }
            finally
            {
                activeCapture = null;
                WorldManager.Instance.returnTileObject(tileObjectForServerDrop);
            }

            WorldManager.Instance.onTileMap[nodeX, nodeY] = -1;
            WorldManager.Instance.onTileStatusMap[nodeX, nodeY] = -1;
            WorldManager.Instance.refreshTileObjectsOnChunksInUse(nodeX, nodeY);
            WorldManager.Instance.onTileChunkHasChanged(nodeX, nodeY);
            NetworkNavMesh.nav.updateChunkInUse();
            NetworkMapSharer.Instance.RpcClearOnTileObjectNoDrop(nodeX, nodeY);

            List<QuarryCapturedDrop> groupedDrops = BuildGroupedCapturedDrops(capture.drops);
            for (int i = 0; i < groupedDrops.Count; i++)
            {
                QuarryCapturedDrop drop = groupedDrops[i];
                QueueQuarryDropToStorage(quarryX, quarryY, tileObjectId, drop.itemId, drop.stackAmount, drop.startWorld, animationDelay + i * 0.05f);
            }
        }

        private static void ResetQuarryBreakStagger()
        {
            quarryBreakCounts.Clear();
        }

        private static float GetNextQuarryBreakDelay(Chest chest)
        {
            if (chest == null)
                return 0f;

            long chestKey = PosKey(chest.xPos, chest.yPos);
            int currentCount = 0;
            quarryBreakCounts.TryGetValue(chestKey, out currentCount);
            quarryBreakCounts[chestKey] = currentCount + 1;
            return QUARRY_INITIAL_BREAK_DELAY + (currentCount / QUARRY_BREAK_BATCH_SIZE) * QUARRY_BREAK_BATCH_DELAY;
        }

        private static void QueueQuarryDropToStorage(int quarryX, int quarryY, int sourceTileObjectId, int itemId, int stackAmount, Vector3 startWorld, float animationDelay)
        {
            System.Action depositToStorage = () =>
            {
                System.Action onDepositSuccess = () =>
                    AutomationCreditHelper.TryGrantQuarryOutputCredit(itemId, stackAmount);

                if (ConveyorHelper.TryDepositToAdjacentChest(quarryX, quarryY, null, itemId, stackAmount, onDepositSuccess))
                    return;

                if (ConveyorHelper.TryDepositViaConveyorPath(quarryX, quarryY, null, itemId, stackAmount, onDepositSuccess))
                    return;

                if (!ConveyorHelper.FallbackDepositToAnyChest(quarryX, quarryY, null, itemId, stackAmount, onDepositSuccess))
                {
                    Vector3 fallbackDropPos = ConveyorAnimator.TileToWorld(quarryX, quarryY);
                    NetworkMapSharer.Instance.spawnAServerDrop(itemId, stackAmount, fallbackDropPos, null, tryNotToStack: true, (int)CharLevelManager.SkillTypes.Mining);
                    Plugin.Log.LogWarning("Autom8er: Quarry drop fell back to a real ground drop because no valid chest space remained for item " + itemId + ".");
                }
            };

            if (!Plugin.AnimationEnabled)
            {
                depositToStorage();
                return;
            }

            Vector3 quarryWorld = ConveyorAnimator.TileToWorld(quarryX, quarryY) + Vector3.up * 0.2f;
            ConveyorAnimator.AnimateArcTransfer(itemId, stackAmount, startWorld, quarryWorld, QUARRY_ITEM_POP_DURATION, QUARRY_ITEM_POP_ARC_HEIGHT, depositToStorage, animationDelay);
        }

        private static bool IsLikelyQuarryNode(int tileObjectId, HashSet<int> quarryNodeIds)
        {
            if (tileObjectId < 0)
                return false;

            if (quarryNodeIds.Contains(tileObjectId))
                return true;

            TileObjectSettings settings = WorldManager.Instance.allObjectSettings[tileObjectId];
            if (settings == null)
                return false;

            if (settings.isStone || settings.isHardStone)
                return true;

            if (BuriedManager.manage != null)
            {
                if (BuriedManager.manage.oldBarrel != null && BuriedManager.manage.oldBarrel.tileObjectId == tileObjectId)
                    return true;

                if (BuriedManager.manage.wheelieBin != null && BuriedManager.manage.wheelieBin.tileObjectId == tileObjectId)
                    return true;
            }

            return false;
        }

        private static HashSet<int> CollectKnownQuarryNodeIds()
        {
            HashSet<int> quarryNodeIds = new HashSet<int>();

            if (GenerateMap.generate != null)
            {
                AddNodeIdsFromTable(quarryNodeIds, GenerateMap.generate.quaryGrowBack0);
                AddNodeIdsFromTable(quarryNodeIds, GenerateMap.generate.quaryGrowBack1);
                AddNodeIdsFromTable(quarryNodeIds, GenerateMap.generate.quaryGrowBack2);
            }

            if (BuriedManager.manage != null)
            {
                if (BuriedManager.manage.oldBarrel != null)
                    quarryNodeIds.Add(BuriedManager.manage.oldBarrel.tileObjectId);

                if (BuriedManager.manage.wheelieBin != null)
                    quarryNodeIds.Add(BuriedManager.manage.wheelieBin.tileObjectId);
            }

            AddFallbackRockNodeId(quarryNodeIds, 456);
            AddFallbackRockNodeId(quarryNodeIds, 105);
            AddFallbackRockNodeId(quarryNodeIds, 106);
            AddFallbackRockNodeId(quarryNodeIds, 107);
            AddFallbackRockNodeId(quarryNodeIds, 104);

            return quarryNodeIds;
        }

        private static void AddNodeIdsFromTable(HashSet<int> quarryNodeIds, BiomSpawnTable table)
        {
            if (table == null || table.objectsInBiom == null)
                return;

            for (int i = 0; i < table.objectsInBiom.Length; i++)
            {
                TileObject tileObj = table.objectsInBiom[i];
                if (tileObj != null)
                    quarryNodeIds.Add(tileObj.tileObjectId);
            }
        }

        private static void AddFallbackRockNodeId(HashSet<int> quarryNodeIds, int dropItemId)
        {
            int tileObjectId = FindStoneNodeByDrop(dropItemId);
            if (tileObjectId >= 0)
                quarryNodeIds.Add(tileObjectId);
        }

        private static long PosKey(int x, int y)
        {
            return ((long)x << 32) | (uint)y;
        }

        private static int FindStoneNodeByDrop(int dropItemId)
        {
            if (WorldManager.Instance == null || Inventory.Instance == null)
                return -1;

            int bestTileObjectId = -1;
            float bestHealth = float.MaxValue;

            for (int i = 0; i < WorldManager.Instance.allObjectSettings.Length; i++)
            {
                TileObjectSettings settings = WorldManager.Instance.allObjectSettings[i];
                TileObject tileObj = WorldManager.Instance.allObjects[i];
                if (settings == null || tileObj == null)
                    continue;

                if (!settings.isStone && !settings.isHardStone)
                    continue;

                if (settings.canBePickedUp || settings.dropsItemOnDeath == null)
                    continue;

                int itemId = Inventory.Instance.getInvItemId(settings.dropsItemOnDeath);
                if (itemId != dropItemId)
                    continue;

                if (settings.fullHealth < bestHealth)
                {
                    bestHealth = settings.fullHealth;
                    bestTileObjectId = i;
                }
            }

            return bestTileObjectId;
        }
    }

    public static class ConveyorHelper
    {
        private static readonly int[] dx = { -1, 0, 1, 0 };
        private static readonly int[] dy = { 0, -1, 0, 1 };

        public class OutputDestination
        {
            public Chest chest;
            public int slotIndex;
            public int routeX;
            public int routeY;
        }

        public static bool TryFeedAdjacentMachine(Chest sourceChest, HouseDetails inside)
        {
            for (int slot = 0; slot < sourceChest.itemIds.Length; slot++)
            {
                int itemId = sourceChest.itemIds[slot];
                int stack = sourceChest.itemStacks[slot];

                if (itemId < 0 || stack <= 0)
                    continue;

                InventoryItem item = Inventory.Instance.allItems[itemId];
                if (item == null || item.itemChange == null)
                    continue;

                for (int i = 0; i < 4; i++)
                {
                    int machineX = sourceChest.xPos + dx[i];
                    int machineY = sourceChest.yPos + dy[i];

                    if (machineX < 0 || machineY < 0)
                        continue;

                    int tileObjectId = GetTileObjectId(machineX, machineY, inside);
                    if (tileObjectId < 0)
                        continue;

                    TileObject tileObj = WorldManager.Instance.allObjects[tileObjectId];
                    if (tileObj == null || tileObj.tileObjectItemChanger == null)
                        continue;

                    if (!IsMachineEmpty(machineX, machineY, inside))
                        continue;

                    if (ConveyorAnimator.IsTargetReserved(machineX, machineY))
                        continue;

                    if (!item.itemChange.checkIfCanBeDepositedServer(tileObjectId))
                        continue;

                    int amountNeeded = item.itemChange.getAmountNeeded(tileObjectId);

                    // Items with hasFuel (tools/durability) store durability in stack, not count
                    // For these items, we always take 1 item and clear the slot
                    bool isFuelItem = item.hasFuel;

                    if (isFuelItem)
                    {
                        // Fuel items: just need 1 item present (stack > 0 means item exists)
                        if (stack <= 0)
                            continue;

                        // Skip fully charged items (prevents infinite charging station loop)
                        // When tool is at max durability, don't re-load it
                        if (stack >= item.fuelMax)
                            continue;
                    }
                    else
                    {
                        // Regular items: need amountNeeded (plus 1 extra if KeepOneItem enabled)
                        // AutoSorters are exempt from KeepOneItem
                        bool keepOne = Plugin.KeepOneItem && !IsAutoSorter(sourceChest);
                        int minRequired = keepOne ? amountNeeded + 1 : amountNeeded;
                        if (stack < minRequired)
                            continue;
                    }

                    if (inside != null)
                    {
                        NetworkMapSharer.Instance.RpcDepositItemIntoChangerInside(itemId, machineX, machineY, inside.xPos, inside.yPos);
                    }
                    else
                    {
                        NetworkMapSharer.Instance.RpcDepositItemIntoChanger(itemId, machineX, machineY);
                    }
                    NetworkMapSharer.Instance.startTileTimerOnServer(itemId, machineX, machineY, inside);
                    AutomationCreditHelper.TryGrantMachineInputCredit(itemId, tileObjectId);

                    // Update chest slot
                    if (isFuelItem)
                    {
                        // Fuel items: always clear the slot (we're taking the whole item)
                        ContainerManager.manage.changeSlotInChest(sourceChest.xPos, sourceChest.yPos, slot, -1, 0, inside);
                    }
                    else
                    {
                        // Regular items: subtract amountNeeded from stack
                        int remaining = stack - amountNeeded;
                        if (remaining <= 0)
                        {
                            ContainerManager.manage.changeSlotInChest(sourceChest.xPos, sourceChest.yPos, slot, -1, 0, inside);
                        }
                        else
                        {
                            ContainerManager.manage.changeSlotInChest(sourceChest.xPos, sourceChest.yPos, slot, itemId, remaining, inside);
                        }
                    }

                    Plugin.Log.LogInfo("Autom8er: Chest -> Machine: " + (isFuelItem ? "1" : amountNeeded.ToString()) + "x " + item.itemName);
                    return true;
                }
            }

            return false;
        }

        public static bool TryFeedMachineViaConveyorPath(Chest sourceChest, HouseDetails inside)
        {
            if (Plugin.ConveyorTileType == -1)
                return false;

            // Find conveyor tiles adjacent to this chest
            HashSet<Vector2Int> pathNetwork = new HashSet<Vector2Int>();
            Queue<Vector2Int> toExplore = new Queue<Vector2Int>();

            for (int i = 0; i < 4; i++)
            {
                int checkX = sourceChest.xPos + dx[i];
                int checkY = sourceChest.yPos + dy[i];

                if (checkX < 0 || checkY < 0)
                    continue;

                if (IsConveyorTile(checkX, checkY, inside))
                {
                    Vector2Int pos = new Vector2Int(checkX, checkY);
                    if (!pathNetwork.Contains(pos))
                    {
                        pathNetwork.Add(pos);
                        toExplore.Enqueue(pos);
                    }
                }
            }

            if (pathNetwork.Count == 0)
                return false;

            // BFS to find all connected path tiles.
            while (toExplore.Count > 0)
            {
                Vector2Int current = toExplore.Dequeue();

                for (int i = 0; i < 4; i++)
                {
                    int nextX = current.x + dx[i];
                    int nextY = current.y + dy[i];

                    if (nextX < 0 || nextY < 0)
                        continue;

                    Vector2Int nextPos = new Vector2Int(nextX, nextY);
                    if (pathNetwork.Contains(nextPos))
                        continue;

                    if (IsConveyorTile(nextX, nextY, inside))
                    {
                        pathNetwork.Add(nextPos);
                        toExplore.Enqueue(nextPos);
                    }
                }
            }

            // Find machines touching the path network
            foreach (Vector2Int pathTile in pathNetwork)
            {
                for (int i = 0; i < 4; i++)
                {
                    int machineX = pathTile.x + dx[i];
                    int machineY = pathTile.y + dy[i];

                    if (machineX < 0 || machineY < 0)
                        continue;

                    // Skip if this is also a path tile
                    if (pathNetwork.Contains(new Vector2Int(machineX, machineY)))
                        continue;

                    // Skip if this is the source chest position
                    if (machineX == sourceChest.xPos && machineY == sourceChest.yPos)
                        continue;

                    int tileObjectId = GetTileObjectId(machineX, machineY, inside);
                    if (tileObjectId < 0)
                        continue;

                    TileObject tileObj = WorldManager.Instance.allObjects[tileObjectId];
                    if (tileObj == null || tileObj.tileObjectItemChanger == null)
                        continue;

                    if (!IsMachineEmpty(machineX, machineY, inside))
                        continue;

                    if (Plugin.AnimationEnabled && ConveyorAnimator.IsTargetReserved(machineX, machineY))
                        continue;

                    // Try to feed this machine from the chest
                    for (int slot = 0; slot < sourceChest.itemIds.Length; slot++)
                    {
                        int itemId = sourceChest.itemIds[slot];
                        int stack = sourceChest.itemStacks[slot];

                        if (itemId < 0 || stack <= 0)
                            continue;

                        InventoryItem item = Inventory.Instance.allItems[itemId];
                        if (item == null || item.itemChange == null)
                            continue;

                        if (!item.itemChange.checkIfCanBeDepositedServer(tileObjectId))
                            continue;

                        int amountNeeded = item.itemChange.getAmountNeeded(tileObjectId);

                        // Items with hasFuel (tools/durability) store durability in stack, not count
                        bool isFuelItem = item.hasFuel;

                        if (isFuelItem)
                        {
                            // Fuel items: just need 1 item present
                            if (stack <= 0)
                                continue;

                            // Skip fully charged items (prevents infinite charging station loop)
                            if (stack >= item.fuelMax)
                                continue;
                        }
                        else
                        {
                            // Regular items: need amountNeeded (plus 1 extra if KeepOneItem enabled)
                            // AutoSorters are exempt from KeepOneItem
                            bool keepOne = Plugin.KeepOneItem && !IsAutoSorter(sourceChest);
                            int minRequired = keepOne ? amountNeeded + 1 : amountNeeded;
                            if (stack < minRequired)
                                continue;
                        }

                        // Remove item from chest immediately (keeps inventory counts in sync)
                        if (isFuelItem)
                        {
                            ContainerManager.manage.changeSlotInChest(sourceChest.xPos, sourceChest.yPos, slot, -1, 0, inside);
                        }
                        else
                        {
                            int remaining = stack - amountNeeded;
                            if (remaining <= 0)
                            {
                                ContainerManager.manage.changeSlotInChest(sourceChest.xPos, sourceChest.yPos, slot, -1, 0, inside);
                            }
                            else
                            {
                                ContainerManager.manage.changeSlotInChest(sourceChest.xPos, sourceChest.yPos, slot, itemId, remaining, inside);
                            }
                        }

                        if (Plugin.AnimationEnabled)
                        {
                            ConveyorAnimator.ReserveTarget(machineX, machineY);

                            Vector2Int chestPos = new Vector2Int(sourceChest.xPos, sourceChest.yPos);
                            Vector2Int machPos = new Vector2Int(machineX, machineY);
                            List<Vector2Int> path = ConveyorPathfinder.FindPath(chestPos, machPos, inside);

                            int capturedItemId = itemId;
                            int capturedMachineX = machineX;
                            int capturedMachineY = machineY;
                            HouseDetails capturedInside = inside;

                            System.Action onArrival = () =>
                            {
                                if (IsMachineEmpty(capturedMachineX, capturedMachineY, capturedInside))
                                {
                                    if (capturedInside != null)
                                        NetworkMapSharer.Instance.RpcDepositItemIntoChangerInside(capturedItemId, capturedMachineX, capturedMachineY, capturedInside.xPos, capturedInside.yPos);
                                    else
                                        NetworkMapSharer.Instance.RpcDepositItemIntoChanger(capturedItemId, capturedMachineX, capturedMachineY);
                                    NetworkMapSharer.Instance.startTileTimerOnServer(capturedItemId, capturedMachineX, capturedMachineY, capturedInside);
                                    AutomationCreditHelper.TryGrantMachineInputCredit(capturedItemId, tileObjectId);
                                }
                                else if (!FallbackDepositToAnyChest(capturedMachineX, capturedMachineY, capturedInside, capturedItemId, 1))
                                {
                                    Plugin.Log.LogWarning("Autom8er: Machine occupied on arrival — no chest available, item lost!");
                                }
                                ConveyorAnimator.UnreserveTarget(capturedMachineX, capturedMachineY);
                            };

                            ConveyorAnimator.StartAnimation(itemId, isFuelItem ? stack : amountNeeded, path, onArrival);
                        }
                        else
                        {
                            if (inside != null)
                            {
                                NetworkMapSharer.Instance.RpcDepositItemIntoChangerInside(itemId, machineX, machineY, inside.xPos, inside.yPos);
                            }
                            else
                            {
                                NetworkMapSharer.Instance.RpcDepositItemIntoChanger(itemId, machineX, machineY);
                            }
                            NetworkMapSharer.Instance.startTileTimerOnServer(itemId, machineX, machineY, inside);
                            AutomationCreditHelper.TryGrantMachineInputCredit(itemId, tileObjectId);
                        }

                        Plugin.Log.LogInfo("Autom8er: Conveyor input -> Machine: " + (isFuelItem ? "1" : amountNeeded.ToString()) + "x " + item.itemName);
                        return true;
                    }
                }
            }

            return false;
        }

        public static bool TryDepositToAdjacentChest(int machineX, int machineY, HouseDetails inside, int itemId, int stackAmount, System.Action onDepositSuccess = null)
        {
            OutputDestination destination;
            if (!TryFindBestAdjacentOutputDestination(machineX, machineY, inside, itemId, out destination))
                return false;

            DepositIntoChest(destination.chest, destination.slotIndex, inside, itemId, stackAmount, onDepositSuccess);
            return true;
        }

        public static bool TryDepositViaConveyorPath(int machineX, int machineY, HouseDetails inside, int itemId, int stackAmount, System.Action onDepositSuccess = null)
        {
            OutputDestination destination;
            if (!TryFindBestConveyorOutputDestination(machineX, machineY, inside, itemId, out destination))
                return false;

            if (Plugin.AnimationEnabled)
            {
                Vector2Int machinePos = new Vector2Int(machineX, machineY);
                Vector2Int chestPos = new Vector2Int(destination.routeX, destination.routeY);
                List<Vector2Int> path = ConveyorPathfinder.FindPath(machinePos, chestPos, inside);

                int capturedItemId = itemId;
                int capturedStack = stackAmount;
                int capturedChestX = destination.chest.xPos;
                int capturedChestY = destination.chest.yPos;
                HouseDetails capturedInside = inside;
                Chest capturedChest = destination.chest;

                System.Action onArrival = () =>
                {
                    int arrivalSlot = FindSlotForItem(capturedChest, capturedItemId);
                    if (arrivalSlot == -1)
                    {
                        if (!FallbackDepositToAnyChest(capturedChestX, capturedChestY, capturedInside, capturedItemId, capturedStack, onDepositSuccess))
                            Plugin.Log.LogWarning("Autom8er: Target chest full on arrival — no chest available, item lost!");
                        return;
                    }

                    DepositIntoChest(capturedChest, arrivalSlot, capturedInside, capturedItemId, capturedStack, onDepositSuccess);
                };

                ConveyorAnimator.StartAnimation(itemId, stackAmount, path, onArrival);
                return true;
            }

            DepositIntoChest(destination.chest, destination.slotIndex, inside, itemId, stackAmount, onDepositSuccess);
            return true;
        }

        // Fallback for animation callbacks when the intended destination is unavailable on arrival
        public static bool FallbackDepositToAnyChest(int originX, int originY, HouseDetails inside, int itemId, int stackAmount, System.Action onDepositSuccess = null)
        {
            // First try adjacent chests
            if (TryDepositToAdjacentChest(originX, originY, inside, itemId, stackAmount, onDepositSuccess))
                return true;

            OutputDestination destination;
            if (!TryFindBestConveyorOutputDestination(originX, originY, inside, itemId, out destination))
                return false;

            DepositIntoChest(destination.chest, destination.slotIndex, inside, itemId, stackAmount, onDepositSuccess);
            return true;
        }

        public static bool TryFindBestChestViaConveyorPathForOutput(int originX, int originY, HouseDetails inside, int itemId, out Chest bestChest, out int bestSlotIndex)
        {
            OutputDestination destination;
            bool found = TryFindBestConveyorOutputDestination(originX, originY, inside, itemId, out destination);
            bestChest = found ? destination.chest : null;
            bestSlotIndex = found ? destination.slotIndex : -1;
            return found;
        }

        public static bool TryFindBestChestFromNetworkAnchor(Chest anchorChest, HouseDetails inside, int itemId, out Chest bestChest, out int bestSlotIndex)
        {
            OutputDestination destination;
            bool found = TryFindBestOutputDestinationFromNetworkAnchor(anchorChest, inside, itemId, includeAnchorChest: true, excludeVacuumCrates: false, out destination);
            bestChest = found ? destination.chest : null;
            bestSlotIndex = found ? destination.slotIndex : -1;
            return found;
        }

        public static bool TryFindBestExternalChestFromNetworkAnchor(Chest anchorChest, HouseDetails inside, int itemId, out Chest bestChest, out int bestSlotIndex)
        {
            OutputDestination destination;
            bool found = TryFindBestOutputDestinationFromNetworkAnchor(anchorChest, inside, itemId, includeAnchorChest: false, excludeVacuumCrates: false, out destination);
            bestChest = found ? destination.chest : null;
            bestSlotIndex = found ? destination.slotIndex : -1;
            return found;
        }

        public static bool TryFindBestNonVacuumExternalChestFromNetworkAnchor(Chest anchorChest, HouseDetails inside, int itemId, out Chest bestChest, out int bestSlotIndex)
        {
            OutputDestination destination;
            bool found = TryFindBestOutputDestinationFromNetworkAnchor(anchorChest, inside, itemId, includeAnchorChest: false, excludeVacuumCrates: true, out destination);
            bestChest = found ? destination.chest : null;
            bestSlotIndex = found ? destination.slotIndex : -1;
            return found;
        }

        public static bool TryFindFilterDestinationFromCrate(Chest filterChest, HouseDetails inside, int itemId, out OutputDestination destination)
        {
            destination = null;
            if (filterChest == null)
                return false;

            return TryGetFilterDestinationAt(filterChest.xPos, filterChest.yPos, inside, itemId, out destination);
        }

        public static bool TryFindBestHarvestOutputDestination(int originX, int originY, HouseDetails inside, int itemId, int searchRadius, out OutputDestination bestDestination)
        {
            bestDestination = null;
            if (itemId < 0)
                return false;

            OutputDestination firstMatchingDestination = null;
            OutputDestination firstEmptyDestination = null;
            HashSet<long> seenChests = new HashSet<long>();

            for (int radius = 1; radius <= searchRadius; radius++)
            {
                for (int offsetX = -radius; offsetX <= radius; offsetX++)
                {
                    for (int offsetY = -radius; offsetY <= radius; offsetY++)
                    {
                        if (offsetX == 0 && offsetY == 0)
                            continue;

                        int manhattanDist = Mathf.Abs(offsetX) + Mathf.Abs(offsetY);
                        if (manhattanDist > radius)
                            continue;

                        if (radius > 1 && manhattanDist < radius)
                            continue;

                        int checkX = originX + offsetX;
                        int checkY = originY + offsetY;
                        if (checkX < 0 || checkY < 0)
                            continue;

                        if (TryGetBestDestinationAt(checkX, checkY, inside, itemId, excludeVacuumCrates: false, seenChests, ref firstMatchingDestination, ref firstEmptyDestination, out bestDestination))
                            return true;
                    }
                }
            }

            if (firstMatchingDestination != null || firstEmptyDestination != null)
            {
                bestDestination = firstMatchingDestination ?? firstEmptyDestination;
                return true;
            }

            return TryFindBestConveyorOutputDestinationFromRadius(originX, originY, inside, itemId, Mathf.Max(1, searchRadius), out bestDestination);
        }

        public static bool TryFindBestOutputDestinationFromNetworkAnchor(Chest anchorChest, HouseDetails inside, int itemId, bool includeAnchorChest, bool excludeVacuumCrates, out OutputDestination bestDestination)
        {
            if (anchorChest == null || itemId < 0)
            {
                bestDestination = null;
                return false;
            }

            bestDestination = null;
            OutputDestination firstMatchingDestination = null;
            OutputDestination firstEmptyDestination = null;
            HashSet<long> seenChests = new HashSet<long>();

            if (includeAnchorChest && TryGetBestDestinationAt(anchorChest.xPos, anchorChest.yPos, inside, itemId, excludeVacuumCrates, seenChests, ref firstMatchingDestination, ref firstEmptyDestination, out bestDestination))
                return true;

            if (Plugin.ConveyorTileType == -1)
            {
                bestDestination = firstMatchingDestination ?? firstEmptyDestination;
                return bestDestination != null;
            }

            HashSet<Vector2Int> pathNetwork = new HashSet<Vector2Int>();
            Queue<Vector2Int> toExplore = new Queue<Vector2Int>();

            for (int i = 0; i < 4; i++)
            {
                int checkX = anchorChest.xPos + dx[i];
                int checkY = anchorChest.yPos + dy[i];

                if (checkX < 0 || checkY < 0)
                    continue;

                if (!IsConveyorTile(checkX, checkY, inside))
                    continue;

                Vector2Int pos = new Vector2Int(checkX, checkY);
                if (pathNetwork.Add(pos))
                    toExplore.Enqueue(pos);
            }

            while (toExplore.Count > 0)
            {
                Vector2Int current = toExplore.Dequeue();

                for (int i = 0; i < 4; i++)
                {
                    int checkX = current.x + dx[i];
                    int checkY = current.y + dy[i];

                    if (checkX < 0 || checkY < 0)
                        continue;

                    Vector2Int checkPos = new Vector2Int(checkX, checkY);
                    if (pathNetwork.Contains(checkPos))
                        continue;

                    if (TryGetBestDestinationAt(checkX, checkY, inside, itemId, excludeVacuumCrates, seenChests, ref firstMatchingDestination, ref firstEmptyDestination, out bestDestination))
                        return true;
                }

                for (int i = 0; i < 4; i++)
                {
                    int nextX = current.x + dx[i];
                    int nextY = current.y + dy[i];

                    if (nextX < 0 || nextY < 0)
                        continue;

                    Vector2Int nextPos = new Vector2Int(nextX, nextY);
                    if (pathNetwork.Contains(nextPos))
                        continue;

                    if (IsConveyorTile(nextX, nextY, inside))
                    {
                        pathNetwork.Add(nextPos);
                        toExplore.Enqueue(nextPos);
                    }
                }
            }

            bestDestination = firstMatchingDestination ?? firstEmptyDestination;
            return bestDestination != null;
        }

        public static bool TryFindBestAdjacentOutputDestination(int originX, int originY, HouseDetails inside, int itemId, out OutputDestination bestDestination)
        {
            bestDestination = null;
            if (itemId < 0)
                return false;

            OutputDestination firstMatchingDestination = null;
            OutputDestination firstEmptyDestination = null;
            HashSet<long> seenChests = new HashSet<long>();

            for (int i = 0; i < 4; i++)
            {
                int checkX = originX + dx[i];
                int checkY = originY + dy[i];

                if (checkX < 0 || checkY < 0)
                    continue;

                if (TryGetBestDestinationAt(checkX, checkY, inside, itemId, excludeVacuumCrates: false, seenChests, ref firstMatchingDestination, ref firstEmptyDestination, out bestDestination))
                    return true;
            }

            bestDestination = firstMatchingDestination ?? firstEmptyDestination;
            return bestDestination != null;
        }

        public static bool TryFindBestConveyorOutputDestination(int originX, int originY, HouseDetails inside, int itemId, out OutputDestination bestDestination)
        {
            bestDestination = null;
            if (itemId < 0)
                return false;

            if (Plugin.ConveyorTileType == -1)
                return false;

            HashSet<Vector2Int> pathNetwork = new HashSet<Vector2Int>();
            Queue<Vector2Int> toExplore = new Queue<Vector2Int>();

            for (int i = 0; i < 4; i++)
            {
                int checkX = originX + dx[i];
                int checkY = originY + dy[i];

                if (checkX < 0 || checkY < 0)
                    continue;

                if (!IsConveyorTile(checkX, checkY, inside))
                    continue;

                Vector2Int pos = new Vector2Int(checkX, checkY);
                if (pathNetwork.Add(pos))
                    toExplore.Enqueue(pos);
            }

            if (pathNetwork.Count == 0)
                return false;

            OutputDestination firstMatchingDestination = null;
            OutputDestination firstEmptyDestination = null;
            HashSet<long> seenChests = new HashSet<long>();

            while (toExplore.Count > 0)
            {
                Vector2Int current = toExplore.Dequeue();

                for (int i = 0; i < 4; i++)
                {
                    int checkX = current.x + dx[i];
                    int checkY = current.y + dy[i];

                    if (checkX < 0 || checkY < 0)
                        continue;

                    Vector2Int checkPos = new Vector2Int(checkX, checkY);
                    if (pathNetwork.Contains(checkPos) || (checkX == originX && checkY == originY))
                        continue;

                    if (TryGetBestDestinationAt(checkX, checkY, inside, itemId, excludeVacuumCrates: false, seenChests, ref firstMatchingDestination, ref firstEmptyDestination, out bestDestination))
                        return true;
                }

                for (int i = 0; i < 4; i++)
                {
                    int nextX = current.x + dx[i];
                    int nextY = current.y + dy[i];

                    if (nextX < 0 || nextY < 0)
                        continue;

                    Vector2Int nextPos = new Vector2Int(nextX, nextY);
                    if (pathNetwork.Contains(nextPos))
                        continue;

                    if (IsConveyorTile(nextX, nextY, inside))
                    {
                        pathNetwork.Add(nextPos);
                        toExplore.Enqueue(nextPos);
                    }
                }
            }

            bestDestination = firstMatchingDestination ?? firstEmptyDestination;
            return bestDestination != null;
        }

        private static bool TryFindBestConveyorOutputDestinationFromRadius(int originX, int originY, HouseDetails inside, int itemId, int startRadius, out OutputDestination bestDestination)
        {
            bestDestination = null;
            if (itemId < 0 || Plugin.ConveyorTileType == -1)
                return false;

            HashSet<Vector2Int> pathNetwork = new HashSet<Vector2Int>();
            Queue<Vector2Int> toExplore = new Queue<Vector2Int>();

            for (int offsetX = -startRadius; offsetX <= startRadius; offsetX++)
            {
                for (int offsetY = -startRadius; offsetY <= startRadius; offsetY++)
                {
                    if (offsetX == 0 && offsetY == 0)
                        continue;

                    if (Mathf.Abs(offsetX) + Mathf.Abs(offsetY) > startRadius)
                        continue;

                    int checkX = originX + offsetX;
                    int checkY = originY + offsetY;
                    if (checkX < 0 || checkY < 0)
                        continue;

                    if (!IsConveyorTile(checkX, checkY, inside))
                        continue;

                    Vector2Int pos = new Vector2Int(checkX, checkY);
                    if (pathNetwork.Add(pos))
                        toExplore.Enqueue(pos);
                }
            }

            if (pathNetwork.Count == 0)
                return false;

            OutputDestination firstMatchingDestination = null;
            OutputDestination firstEmptyDestination = null;
            HashSet<long> seenChests = new HashSet<long>();

            while (toExplore.Count > 0)
            {
                Vector2Int current = toExplore.Dequeue();

                for (int i = 0; i < 4; i++)
                {
                    int checkX = current.x + dx[i];
                    int checkY = current.y + dy[i];
                    if (checkX < 0 || checkY < 0)
                        continue;

                    Vector2Int checkPos = new Vector2Int(checkX, checkY);
                    if (pathNetwork.Contains(checkPos) || (checkX == originX && checkY == originY))
                        continue;

                    if (TryGetBestDestinationAt(checkX, checkY, inside, itemId, excludeVacuumCrates: false, seenChests, ref firstMatchingDestination, ref firstEmptyDestination, out bestDestination))
                        return true;
                }

                for (int i = 0; i < 4; i++)
                {
                    int nextX = current.x + dx[i];
                    int nextY = current.y + dy[i];
                    if (nextX < 0 || nextY < 0)
                        continue;

                    Vector2Int nextPos = new Vector2Int(nextX, nextY);
                    if (pathNetwork.Contains(nextPos))
                        continue;

                    if (IsConveyorTile(nextX, nextY, inside))
                    {
                        pathNetwork.Add(nextPos);
                        toExplore.Enqueue(nextPos);
                    }
                }
            }

            bestDestination = firstMatchingDestination ?? firstEmptyDestination;
            return bestDestination != null;
        }

        private static bool TryGetBestDestinationAt(int checkX, int checkY, HouseDetails inside, int itemId, bool excludeVacuumCrates, HashSet<long> seenChests, ref OutputDestination firstMatchingDestination, ref OutputDestination firstEmptyDestination, out OutputDestination immediateFilterDestination)
        {
            immediateFilterDestination = null;

            OutputDestination filterDestination;
            if (TryGetFilterDestinationAt(checkX, checkY, inside, itemId, out filterDestination))
            {
                immediateFilterDestination = filterDestination;
                return true;
            }

            if (IsInputOnlyContainer(checkX, checkY, inside) || IsSpecialContainer(checkX, checkY, inside) || IsFilterCrate(checkX, checkY, inside))
                return false;

            Chest chest = FindChestAt(checkX, checkY, inside);
            if (chest == null)
                return false;

            if (excludeVacuumCrates && IsVacuumCrate(checkX, checkY, inside))
                return false;

            long chestKey = (((long)chest.xPos & 0xFFFFFL) << 44)
                | (((long)chest.yPos & 0xFFFFFL) << 24)
                | (((long)(chest.insideX + 1) & 0xFFFL) << 12)
                | ((long)(chest.insideY + 1) & 0xFFFL);

            if (!seenChests.Add(chestKey))
                return false;

            int slotIndex = FindSlotForItem(chest, itemId);
            if (slotIndex == -1)
                return false;

            OutputDestination destination = new OutputDestination
            {
                chest = chest,
                slotIndex = slotIndex,
                routeX = chest.xPos,
                routeY = chest.yPos
            };

            if (ChestHasMatchingStack(chest, itemId))
            {
                if (firstMatchingDestination == null)
                    firstMatchingDestination = destination;
                return false;
            }

            if (firstEmptyDestination == null)
                firstEmptyDestination = destination;

            return false;
        }

        private static bool TryGetFilterDestinationAt(int filterX, int filterY, HouseDetails inside, int itemId, out OutputDestination destination)
        {
            destination = null;

            if (itemId < 0 || !IsFilterCrate(filterX, filterY, inside))
                return false;

            Chest filterChest = FindChestAt(filterX, filterY, inside);
            if (filterChest == null || !ChestHasMatchingStack(filterChest, itemId))
                return false;

            OutputDestination firstEmptyDestination = null;
            bool isStackable = Inventory.Instance.allItems[itemId].checkIfStackable();

            for (int i = 0; i < 4; i++)
            {
                int chestX = filterX + dx[i];
                int chestY = filterY + dy[i];

                if (chestX < 0 || chestY < 0)
                    continue;

                if (!IsValidFilterStorageChest(chestX, chestY, inside))
                    continue;

                Chest storageChest = FindChestAt(chestX, chestY, inside);
                if (storageChest == null)
                    continue;

                int slotIndex = FindSlotForItem(storageChest, itemId);
                if (slotIndex == -1)
                    continue;

                OutputDestination candidate = new OutputDestination
                {
                    chest = storageChest,
                    slotIndex = slotIndex,
                    routeX = filterX,
                    routeY = filterY
                };

                if (isStackable && ChestHasMatchingStack(storageChest, itemId))
                {
                    destination = candidate;
                    return true;
                }

                if (firstEmptyDestination == null)
                    firstEmptyDestination = candidate;
            }

            destination = firstEmptyDestination;
            return destination != null;
        }

        private static bool IsValidFilterStorageChest(int x, int y, HouseDetails inside)
        {
            if (IsInputOnlyContainer(x, y, inside) || IsOutputOnlyContainer(x, y, inside) || IsFilterCrate(x, y, inside) || IsVacuumCrate(x, y, inside))
                return false;

            if (IsSpecialContainer(x, y, inside))
                return false;

            Chest chest = FindChestAt(x, y, inside);
            if (chest == null)
                return false;

            return true;
        }

        public static bool ChestHasMatchingStack(Chest chest, int itemId)
        {
            if (chest == null || itemId < 0)
                return false;

            for (int i = 0; i < chest.itemIds.Length; i++)
            {
                if (chest.itemIds[i] == itemId && chest.itemStacks[i] > 0)
                    return true;
            }

            return false;
        }

        private static void DepositIntoChest(Chest chest, int slotIndex, HouseDetails inside, int itemId, int stackAmount, System.Action onDepositSuccess)
        {
            bool isStackable = Inventory.Instance.allItems[itemId].checkIfStackable();

            if (isStackable && chest.itemIds[slotIndex] == itemId)
            {
                ContainerManager.manage.changeSlotInChest(
                    chest.xPos, chest.yPos, slotIndex,
                    itemId, chest.itemStacks[slotIndex] + stackAmount, inside);
            }
            else
            {
                ContainerManager.manage.changeSlotInChest(
                    chest.xPos, chest.yPos, slotIndex,
                    itemId, stackAmount, inside);
            }

            if (IsAutoSorter(chest))
                QueueAutoSorterUpdate(chest);

            if (onDepositSuccess != null)
                onDepositSuccess();
        }

        public static bool CanDepositFromPosition(int originX, int originY, HouseDetails inside, int itemId)
        {
            if (itemId < 0)
                return false;

            if (CanDepositToAdjacentChest(originX, originY, inside, itemId))
                return true;

            return CanDepositViaConveyorPath(originX, originY, inside, itemId, requireEmptySlot: false);
        }

        public static bool CanDepositUnknownFromPosition(int originX, int originY, HouseDetails inside)
        {
            if (CanDepositToAdjacentChest(originX, originY, inside, -1))
                return true;

            return CanDepositViaConveyorPath(originX, originY, inside, -1, requireEmptySlot: true);
        }

        private static bool CanDepositToAdjacentChest(int originX, int originY, HouseDetails inside, int itemId)
        {
            if (itemId >= 0)
            {
                OutputDestination bestDestination;
                return TryFindBestAdjacentOutputDestination(originX, originY, inside, itemId, out bestDestination);
            }

            for (int i = 0; i < 4; i++)
            {
                int checkX = originX + dx[i];
                int checkY = originY + dy[i];
                if (checkX < 0 || checkY < 0)
                    continue;

                if (IsInputOnlyContainer(checkX, checkY, inside))
                    continue;

                if (IsSpecialContainer(checkX, checkY, inside))
                    continue;

                Chest chest = FindChestAt(checkX, checkY, inside);
                if (chest == null)
                    continue;

                if (itemId >= 0)
                {
                    if (FindSlotForItem(chest, itemId) != -1)
                        return true;
                }
                else if (FindFirstEmptySlot(chest) != -1)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool CanDepositViaConveyorPath(int originX, int originY, HouseDetails inside, int itemId, bool requireEmptySlot)
        {
            if (!requireEmptySlot && itemId >= 0)
            {
                OutputDestination bestDestination;
                return TryFindBestConveyorOutputDestination(originX, originY, inside, itemId, out bestDestination);
            }

            if (Plugin.ConveyorTileType == -1)
                return false;

            HashSet<Vector2Int> pathNetwork = new HashSet<Vector2Int>();
            Queue<Vector2Int> toExplore = new Queue<Vector2Int>();

            for (int i = 0; i < 4; i++)
            {
                int checkX = originX + dx[i];
                int checkY = originY + dy[i];
                if (checkX < 0 || checkY < 0)
                    continue;

                if (!IsConveyorTile(checkX, checkY, inside))
                    continue;

                Vector2Int pos = new Vector2Int(checkX, checkY);
                if (pathNetwork.Add(pos))
                    toExplore.Enqueue(pos);
            }

            while (toExplore.Count > 0)
            {
                Vector2Int current = toExplore.Dequeue();

                for (int i = 0; i < 4; i++)
                {
                    int nx = current.x + dx[i];
                    int ny = current.y + dy[i];
                    if (nx < 0 || ny < 0)
                        continue;

                    Vector2Int nextPos = new Vector2Int(nx, ny);
                    if (pathNetwork.Contains(nextPos))
                        continue;

                    if (IsConveyorTile(nx, ny, inside))
                    {
                        pathNetwork.Add(nextPos);
                        toExplore.Enqueue(nextPos);
                    }
                }
            }

            foreach (Vector2Int pathTile in pathNetwork)
            {
                for (int i = 0; i < 4; i++)
                {
                    int checkX = pathTile.x + dx[i];
                    int checkY = pathTile.y + dy[i];
                    if (checkX < 0 || checkY < 0)
                        continue;

                    if (pathNetwork.Contains(new Vector2Int(checkX, checkY)))
                        continue;

                    if (IsInputOnlyContainer(checkX, checkY, inside))
                        continue;

                    if (IsSpecialContainer(checkX, checkY, inside))
                        continue;

                    Chest chest = FindChestAt(checkX, checkY, inside);
                    if (chest == null)
                        continue;

                    if (requireEmptySlot)
                    {
                        if (FindFirstEmptySlot(chest) != -1)
                            return true;
                    }
                    else if (FindSlotForItem(chest, itemId) != -1)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public static bool IsConveyorTile(int x, int y, HouseDetails inside)
        {
            if (Plugin.ConveyorTileType == -1)
                return false;

            int tileType;
            if (inside != null)
            {
                if (x >= inside.houseMapOnTile.GetLength(0) || y >= inside.houseMapOnTile.GetLength(1))
                    return false;
                // Inside houses, paths are still on tileTypeMap but we need house's version
                // Actually, houses might not have tileTypeMap - skip for now
                return false;
            }
            else
            {
                if (x >= WorldManager.Instance.tileTypeMap.GetLength(0) ||
                    y >= WorldManager.Instance.tileTypeMap.GetLength(1))
                    return false;
                tileType = WorldManager.Instance.tileTypeMap[x, y];
            }

            return tileType == Plugin.ConveyorTileType;
        }

        public static bool IsInputOnlyContainer(int x, int y, HouseDetails inside)
        {
            int tileObjectId = GetTileObjectId(x, y, inside);
            if (tileObjectId < 0)
                return false;

            return tileObjectId == Plugin.WHITE_CRATE_TILE_ID || tileObjectId == Plugin.WHITE_CHEST_TILE_ID;
        }

        public static bool IsOutputOnlyContainer(int x, int y, HouseDetails inside)
        {
            int tileObjectId = GetTileObjectId(x, y, inside);
            if (tileObjectId < 0)
                return false;

            return tileObjectId == Plugin.BLACK_CHEST_TILE_ID;
        }

        public static bool IsFilterCrate(int x, int y, HouseDetails inside)
        {
            return GetTileObjectId(x, y, inside) == Plugin.BLACK_CRATE_TILE_ID;
        }

        public static bool IsVacuumCrate(int x, int y, HouseDetails inside)
        {
            return GetTileObjectId(x, y, inside) == Plugin.GREEN_CRATE_TILE_ID;
        }

        public static bool ChestHasItems(Chest chest)
        {
            for (int i = 0; i < chest.itemIds.Length; i++)
            {
                if (chest.itemIds[i] >= 0 && chest.itemStacks[i] > 0)
                    return true;
            }
            return false;
        }

        public static int GetTileObjectId(int x, int y, HouseDetails inside)
        {
            if (inside != null)
            {
                if (x >= inside.houseMapOnTile.GetLength(0) || y >= inside.houseMapOnTile.GetLength(1))
                    return -1;
                return inside.houseMapOnTile[x, y];
            }
            else
            {
                if (x >= WorldManager.Instance.onTileMap.GetLength(0) ||
                    y >= WorldManager.Instance.onTileMap.GetLength(1))
                    return -1;
                return WorldManager.Instance.onTileMap[x, y];
            }
        }

        public static bool IsMachineEmpty(int x, int y, HouseDetails inside)
        {
            int status;
            if (inside != null)
            {
                status = inside.houseMapOnTileStatus[x, y];
            }
            else
            {
                status = WorldManager.Instance.onTileStatusMap[x, y];
            }
            return status == -2 || status == -1;
        }

        public static Chest FindChestAt(int x, int y, HouseDetails inside)
        {
            int tileObjectId = GetTileObjectId(x, y, inside);
            if (tileObjectId < 0)
                return null;

            TileObject tileObj = WorldManager.Instance.allObjects[tileObjectId];
            if (tileObj == null || tileObj.tileObjectChest == null)
                return null;

            // Skip machines with internal inventory (gacha machines, etc.)
            if (tileObj.tileObjectItemChanger != null)
                return null;

            Chest chest = ContainerManager.manage.getChestForWindow(x, y, inside);

            if (chest == null)
            {
                chest = ContainerManager.manage.getChestForRecycling(x, y, inside);
            }

            return chest;
        }

        public static bool IsSpecialContainer(int x, int y, HouseDetails inside)
        {
            int tileObjectId = GetTileObjectId(x, y, inside);
            if (tileObjectId < 0)
                return true;

            ChestPlaceable chestPlaceable = WorldManager.Instance.allObjects[tileObjectId].tileObjectChest;
            if (chestPlaceable == null)
                return true;

            if (chestPlaceable.isFishPond || chestPlaceable.isBugTerrarium ||
                chestPlaceable.isAutoPlacer ||
                chestPlaceable.isMannequin || chestPlaceable.isToolRack ||
                chestPlaceable.isDisplayStand)
            {
                return true;
            }

            return false;
        }

        public static bool IsAutoSorter(Chest chest)
        {
            return chest != null && chest.IsAutoSorter();
        }

        // Debounced AutoSorter triggering — batches rapid deposits into full-stack fires
        private static HashSet<long> pendingSorts = new HashSet<long>();

        public static void ClearPendingSorts()
        {
            pendingSorts.Clear();
        }

        public static void QueueAutoSorterUpdate(Chest chest)
        {
            long key = ((long)chest.xPos << 32) | (uint)chest.yPos;
            if (pendingSorts.Add(key))
            {
                ContainerManager.manage.StartCoroutine(AutoSorterBatchSort(chest, key));
            }
        }

        // The game's AutoSortItemsIntoNearbyChests silently aborts when
        // onTileStatusMap != 0 (thinks a player has the chest UI open).
        // After save/load, playingLookingInside resets to 0 but onTileStatusMap
        // can retain a stale non-zero value. Fix this before each sort attempt.
        private static void EnsureAutoSorterStatusClean(Chest chest)
        {
            if (chest.insideX == -1 && chest.insideY == -1)
            {
                if (chest.playingLookingInside <= 0 &&
                    WorldManager.Instance.onTileStatusMap[chest.xPos, chest.yPos] != 0)
                {
                    WorldManager.Instance.onTileStatusMap[chest.xPos, chest.yPos] = 0;
                    NetworkMapSharer.Instance.RpcGiveOnTileStatus(0, chest.xPos, chest.yPos);
                }
            }
        }

        // The game's AutoSortItemsIntoNearbyChests iterates activeChests to find
        // destinations. On first game load, chests aren't in activeChests until
        // something activates them. Load nearby chests from save data so auto sort
        // can find valid destinations.
        private static void EnsureNearbyChestsActive(Chest autoSorter)
        {
            HouseDetails inside = null;
            if (autoSorter.insideX != -1 && autoSorter.insideY != -1)
                inside = HouseManager.manage.getHouseInfoIfExists(autoSorter.insideX, autoSorter.insideY);

            int mapSize = WorldManager.Instance.GetMapSize();

            for (int x = autoSorter.xPos - 10; x <= autoSorter.xPos + 10; x++)
            {
                for (int y = autoSorter.yPos - 10; y <= autoSorter.yPos + 10; y++)
                {
                    if (x < 0 || y < 0 || x >= mapSize || y >= mapSize)
                        continue;

                    bool hasChest;
                    bool hasMachineInventory;

                    if (inside == null)
                    {
                        int tileId = WorldManager.Instance.onTileMap[x, y];
                        if (tileId <= 0) continue;
                        TileObject tileObj = WorldManager.Instance.allObjects[tileId];
                        hasChest = tileObj.tileObjectChest;
                        hasMachineInventory = tileObj.tileObjectItemChanger;
                    }
                    else
                    {
                        int tileId = inside.houseMapOnTile[x, y];
                        if (tileId <= 0) continue;
                        TileObject tileObj = WorldManager.Instance.allObjects[tileId];
                        hasChest = tileObj.tileObjectChest;
                        hasMachineInventory = tileObj.tileObjectItemChanger;
                    }

                    if (!hasChest || hasMachineInventory)
                        continue;

                    ContainerManager.manage.getChestForRecycling(x, y, inside);
                }
            }
        }

        private static IEnumerator AutoSorterBatchSort(Chest chest, long key)
        {
            yield return new WaitForSeconds(1.0f);

            EnsureNearbyChestsActive(chest);

            int maxCycles = 24;
            while (maxCycles-- > 0)
            {
                bool hasItems = false;
                for (int i = 0; i < chest.itemIds.Length; i++)
                {
                    if (chest.itemIds[i] != -1) { hasItems = true; break; }
                }
                if (!hasItems) break;

                EnsureAutoSorterStatusClean(chest);

                yield return ContainerManager.manage.StartCoroutine(
                    ContainerManager.manage.AutoSortItemsIntoNearbyChests(chest));

                yield return new WaitForSeconds(0.5f);
            }

            pendingSorts.Remove(key);
        }

        public static void EnsureAutomationChestsActive(Chest anchorChest)
        {
            if (anchorChest == null)
                return;

            EnsureNearbyChestsActive(anchorChest);
        }

        public static int FindSlotForItem(Chest chest, int itemId)
        {
            bool isStackable = Inventory.Instance.allItems[itemId].checkIfStackable();

            if (isStackable)
            {
                for (int i = 0; i < chest.itemIds.Length; i++)
                {
                    if (chest.itemIds[i] == itemId)
                    {
                        return i;
                    }
                }
            }

            for (int i = 0; i < chest.itemIds.Length; i++)
            {
                if (chest.itemIds[i] == -1)
                {
                    return i;
                }
            }

            return -1;
        }

        public static int FindFirstEmptySlot(Chest chest)
        {
            for (int i = 0; i < chest.itemIds.Length; i++)
            {
                if (chest.itemIds[i] == -1)
                    return i;
            }

            return -1;
        }
    }

    public static class ConveyorPathfinder
    {
        private static readonly int[] dx = { -1, 0, 1, 0 };
        private static readonly int[] dy = { 0, -1, 0, 1 };

        private static bool IsPartOfObject(int x, int y, Vector2Int rootPos, HouseDetails inside)
        {
            if (x == rootPos.x && y == rootPos.y)
                return true;

            int[,] tileMap = inside != null ? inside.houseMapOnTile : WorldManager.Instance.onTileMap;
            int mapW = tileMap.GetLength(0);
            int mapH = tileMap.GetLength(1);
            if (x < 0 || y < 0 || x >= mapW || y >= mapH)
                return false;

            if (tileMap[x, y] < -1)
            {
                Vector2 resolved = WorldManager.Instance.findMultiTileObjectPos(x, y, inside);
                return (int)resolved.x == rootPos.x && (int)resolved.y == rootPos.y;
            }

            return false;
        }

        // BFS with parent tracking. Handles multi-tile objects (silos, fish ponds)
        public static List<Vector2Int> FindPath(Vector2Int start, Vector2Int end, HouseDetails inside)
        {
            Dictionary<Vector2Int, Vector2Int> cameFrom = new Dictionary<Vector2Int, Vector2Int>();
            Queue<Vector2Int> queue = new Queue<Vector2Int>();

            int[,] tileMap = inside != null ? inside.houseMapOnTile : WorldManager.Instance.onTileMap;
            int mapW = tileMap.GetLength(0);
            int mapH = tileMap.GetLength(1);

            // Scan radius 5 around start to find all tiles of multi-tile source object
            int scanRadius = 5;
            int minX = Mathf.Max(0, start.x - scanRadius);
            int maxX = Mathf.Min(mapW - 1, start.x + scanRadius);
            int minY = Mathf.Max(0, start.y - scanRadius);
            int maxY = Mathf.Min(mapH - 1, start.y + scanRadius);

            for (int sx = minX; sx <= maxX; sx++)
            {
                for (int sy = minY; sy <= maxY; sy++)
                {
                    if (!IsPartOfObject(sx, sy, start, inside))
                        continue;

                    for (int i = 0; i < 4; i++)
                    {
                        int nx = sx + dx[i];
                        int ny = sy + dy[i];
                        if (nx < 0 || ny < 0 || nx >= mapW || ny >= mapH)
                            continue;

                        Vector2Int neighbor = new Vector2Int(nx, ny);
                        if (ConveyorHelper.IsConveyorTile(nx, ny, inside) && !cameFrom.ContainsKey(neighbor))
                        {
                            cameFrom[neighbor] = start;
                            queue.Enqueue(neighbor);
                        }
                    }
                }
            }

            Vector2Int effectiveEnd = end;
            bool foundEnd = false;
            // Allow very large conveyor runs so single-chest mega arrays still animate end-to-end.
            // Revisit protection comes from cameFrom, so this won't loop infinitely.
            int maxTiles = mapW * mapH;
            int visited = 0;

            while (queue.Count > 0 && visited < maxTiles)
            {
                Vector2Int current = queue.Dequeue();
                visited++;

                for (int i = 0; i < 4; i++)
                {
                    int nx = current.x + dx[i];
                    int ny = current.y + dy[i];
                    if (nx < 0 || ny < 0 || nx >= mapW || ny >= mapH)
                        continue;

                    Vector2Int neighbor = new Vector2Int(nx, ny);

                    if (IsPartOfObject(nx, ny, end, inside))
                    {
                        effectiveEnd = neighbor;
                        cameFrom[effectiveEnd] = current;
                        foundEnd = true;
                        break;
                    }

                    if (cameFrom.ContainsKey(neighbor))
                        continue;

                    if (ConveyorHelper.IsConveyorTile(nx, ny, inside))
                    {
                        cameFrom[neighbor] = current;
                        queue.Enqueue(neighbor);
                    }
                }

                if (foundEnd)
                    break;
            }

            // Phase 2: check distance 2 (outer-row crab pots are 2 tiles from conveyor)
            if (!foundEnd)
            {
                for (int i = 0; i < 4; i++)
                {
                    int adjX = end.x + dx[i];
                    int adjY = end.y + dy[i];
                    if (adjX < 0 || adjY < 0 || adjX >= mapW || adjY >= mapH)
                        continue;

                    Vector2Int adjTile = new Vector2Int(adjX, adjY);

                    for (int j = 0; j < 4; j++)
                    {
                        int convX = adjX + dx[j];
                        int convY = adjY + dy[j];
                        if (convX < 0 || convY < 0 || convX >= mapW || convY >= mapH)
                            continue;

                        Vector2Int convTile = new Vector2Int(convX, convY);
                        if (cameFrom.ContainsKey(convTile) && !(convTile.x == start.x && convTile.y == start.y))
                        {
                            effectiveEnd = adjTile;
                            cameFrom[effectiveEnd] = convTile;
                            foundEnd = true;
                            break;
                        }
                    }
                    if (foundEnd) break;
                }
            }

            if (!foundEnd)
                return null;

            List<Vector2Int> path = new List<Vector2Int>();
            Vector2Int step = effectiveEnd;
            while (step.x != start.x || step.y != start.y)
            {
                path.Add(step);
                step = cameFrom[step];
            }
            path.Add(start);
            path.Reverse();

            return path;
        }
    }

    public static class VacuumCrateHelper
    {
        private const int VACUUM_HARVEST_RADIUS_TILES = 10;
        private const int VACUUM_PICKUP_RADIUS_TILES = 12;
        private const float VACUUM_ITEM_DELAY = 0.03f;
        private const float VACUUM_VISUAL_HEIGHT = 1.6f;
        private const int VACUUM_HARVEST_BATCH_SIZE = 5;
        private const float VACUUM_HARVEST_BATCH_DELAY = 0.2f;
        private const float VACUUM_NETWORK_FLUSH_DELAY = 0.03f;

        private class VacuumVisualState
        {
            public GameObject visual;
            public float expireAt;
            public int tileX;
            public int tileY;
        }

        private class HarvestTarget
        {
            public int xPos;
            public int yPos;
            public TileObject tileObj;
            public TileObjectGrowthStages growth;
        }

        private class VacuumDropGroup
        {
            public int itemId;
            public int totalStackAmount;
            public int tallyType;
            public Vector2Int dropTile;
            public Vector3 startWorld;
            public List<DroppedItem> drops = new List<DroppedItem>();
            public List<uint> dropIds = new List<uint>();
        }

        private static readonly Dictionary<long, VacuumVisualState> activeVisuals = new Dictionary<long, VacuumVisualState>();
        private static readonly HashSet<uint> pendingDrops = new HashSet<uint>();

        private static long PosKey(int x, int y)
        {
            return ((long)x << 32) | (uint)y;
        }

        public static System.Collections.IEnumerator ProcessDayChangeVacuumHarvests()
        {
            if (WorldManager.Instance == null || ContainerManager.manage == null || !NetworkMapSharer.Instance.isServer)
                yield break;

            ActivateAllVacuumCrates();

            List<Chest> chestsCopy = new List<Chest>(ContainerManager.manage.activeChests);
            if (chestsCopy.Count == 0)
                yield break;

            int harvested = 0;

            for (int i = 0; i < chestsCopy.Count; i++)
            {
                Chest chest = chestsCopy[i];
                if (chest == null || chest.insideX != -1 || chest.insideY != -1)
                    continue;

                if (!ConveyorHelper.IsVacuumCrate(chest.xPos, chest.yPos, null))
                    continue;

                List<HarvestTarget> harvestTargets = CollectNearbyHarvestTargets(chest, null);
                for (int harvestIndex = 0; harvestIndex < harvestTargets.Count; harvestIndex++)
                {
                    HarvestTarget target = harvestTargets[harvestIndex];
                    HarvestPlantAt(target.xPos, target.yPos, target.tileObj, target.growth);
                    harvested++;

                    bool batchComplete = ((harvestIndex + 1) % VACUUM_HARVEST_BATCH_SIZE == 0) || harvestIndex == harvestTargets.Count - 1;
                    if (batchComplete)
                    {
                        TryVacuumNearbyDrops(chest, null);
                    }

                    if ((harvestIndex + 1) % VACUUM_HARVEST_BATCH_SIZE == 0 && harvestIndex + 1 < harvestTargets.Count)
                        yield return new WaitForSeconds(VACUUM_HARVEST_BATCH_DELAY);
                }

                if (harvestTargets.Count == 0)
                    TryVacuumNearbyDrops(chest, null);
            }

            if (harvested > 0)
            {
                Plugin.Log.LogInfo("Autom8er: Vacuum crate day-change harvest collected " + harvested + " crop/tree tile(s).");
            }
        }

        public static void TryVacuumNearbyDrops(Chest chest, HouseDetails inside)
        {
            if (chest == null || !ConveyorHelper.IsVacuumCrate(chest.xPos, chest.yPos, inside))
                return;

            if (WorldManager.Instance == null || Inventory.Instance == null || WorldManager.Instance.itemsOnGround == null)
                return;

            ConveyorHelper.EnsureAutomationChestsActive(chest);

            List<DroppedItem> candidates = new List<DroppedItem>();
            List<DroppedItem> itemsOnGround = WorldManager.Instance.itemsOnGround;

            for (int i = 0; i < itemsOnGround.Count; i++)
            {
                DroppedItem drop = itemsOnGround[i];
                if (drop == null || drop.myItemId < 0 || drop.stackAmount <= 0)
                    continue;

                if (!drop.IsDropOnCurrentLevel() || pendingDrops.Contains(drop.netId))
                    continue;

                if (!IsDropInSameSpace(drop, inside))
                    continue;

                Vector2Int dropTile = GetDropTile(drop);
                if (Mathf.Abs(dropTile.x - chest.xPos) > VACUUM_PICKUP_RADIUS_TILES ||
                    Mathf.Abs(dropTile.y - chest.yPos) > VACUUM_PICKUP_RADIUS_TILES)
                {
                    continue;
                }

                if (!CanVacuumDeposit(chest, inside, drop.myItemId))
                    continue;

                candidates.Add(drop);
            }

            List<VacuumDropGroup> groups = BuildDropGroups(candidates);
            if (groups.Count == 0)
                return;

            Vector3 chestWorld = GetVacuumTargetWorld(chest.xPos, chest.yPos);
            groups.Sort((a, b) =>
            {
                float distA = (a.startWorld - chestWorld).sqrMagnitude;
                float distB = (b.startWorld - chestWorld).sqrMagnitude;
                return distA.CompareTo(distB);
            });

            float delay = 0f;
            float longestAnimation = 0f;
            int collected = 0;

            for (int i = 0; i < groups.Count; i++)
            {
                VacuumDropGroup group = groups[i];
                if (group == null || group.totalStackAmount <= 0 || group.itemId < 0)
                    continue;

                int removedStackAmount = 0;
                List<uint> removedDropIds = new List<uint>();
                for (int dropIndex = 0; dropIndex < group.drops.Count; dropIndex++)
                {
                    DroppedItem drop = group.drops[dropIndex];
                    if (drop == null || !pendingDrops.Add(drop.netId))
                        continue;

                    if (!TryRemoveDropFromWorld(drop))
                    {
                        pendingDrops.Remove(drop.netId);
                        continue;
                    }

                    removedStackAmount += drop.stackAmount;
                    removedDropIds.Add(drop.netId);
                }

                if (removedStackAmount <= 0)
                    continue;

                int itemId = group.itemId;
                int stackAmount = removedStackAmount;
                int tallyType = group.tallyType;
                Vector3 startWorld = group.startWorld;

                float distance = Vector3.Distance(startWorld, chestWorld);
                float duration = Mathf.Clamp(0.15f + distance * 0.03f, 0.18f, 0.55f);
                float arcHeight = Mathf.Clamp(0.3f + distance * 0.06f, 0.35f, 1.4f);

                System.Action onArrival = () =>
                {
                    for (int removedIndex = 0; removedIndex < removedDropIds.Count; removedIndex++)
                    {
                        pendingDrops.Remove(removedDropIds[removedIndex]);
                    }

                    System.Action grantCredit = () =>
                    {
                        AutomationCreditHelper.TryGrantDroppedItemPickupCredit(itemId, stackAmount, tallyType);
                    };

                    ConveyorHelper.OutputDestination networkDestination;
                    if (ConveyorHelper.TryFindBestOutputDestinationFromNetworkAnchor(chest, inside, itemId, includeAnchorChest: false, excludeVacuumCrates: true, out networkDestination))
                    {
                        Chest capturedSourceChest = chest;
                        Chest capturedNetworkChest = networkDestination.chest;
                        int capturedRouteX = networkDestination.routeX;
                        int capturedRouteY = networkDestination.routeY;
                        System.Action depositToNetwork = () =>
                        {
                            if (HarvestHelper.TryDepositHarvestToChest(capturedNetworkChest, inside, itemId, stackAmount, grantCredit))
                                return;

                            if (ConveyorHelper.FallbackDepositToAnyChest(capturedNetworkChest.xPos, capturedNetworkChest.yPos, inside, itemId, stackAmount, grantCredit))
                                return;

                            if (HarvestHelper.TryDepositHarvestToChest(capturedSourceChest, inside, itemId, stackAmount, grantCredit))
                                return;

                            SpawnFallbackGroundDrop(itemId, stackAmount, tallyType, chestWorld, inside);
                        };

                        ConveyorAnimator.AnimateTransfer(itemId, stackAmount,
                            new Vector2Int(chest.xPos, chest.yPos),
                            new Vector2Int(capturedRouteX, capturedRouteY),
                            inside, depositToNetwork);
                        return;
                    }

                    if (HarvestHelper.TryDepositHarvestToChest(chest, inside, itemId, stackAmount, grantCredit))
                        return;

                    if (ConveyorHelper.FallbackDepositToAnyChest(chest.xPos, chest.yPos, inside, itemId, stackAmount, grantCredit))
                        return;

                    SpawnFallbackGroundDrop(itemId, stackAmount, tallyType, chestWorld, inside);
                };

                ConveyorAnimator.AnimateArcTransfer(itemId, stackAmount, startWorld, chestWorld, duration, arcHeight, onArrival, delay);
                longestAnimation = Mathf.Max(longestAnimation, delay + duration);
                delay += VACUUM_ITEM_DELAY;
                collected++;
            }

            if (collected > 0)
            {
                TriggerVacuumVisual(chest.xPos, chest.yPos, longestAnimation + 0.2f);
                Plugin.Log.LogInfo("Autom8er: Vacuum crate at " + chest.xPos + "," + chest.yPos + " collected " + collected + " drop(s).");
            }
        }

        public static void TryFlushStoredItemsToNetwork(Chest chest, HouseDetails inside)
        {
            if (chest == null || inside != null || !ConveyorHelper.IsVacuumCrate(chest.xPos, chest.yPos, inside))
                return;

            ConveyorHelper.EnsureAutomationChestsActive(chest);

            float delay = 0f;
            bool movedAny = false;

            for (int slot = 0; slot < chest.itemIds.Length; slot++)
            {
                int itemId = chest.itemIds[slot];
                int stackAmount = chest.itemStacks[slot];

                if (itemId < 0 || stackAmount <= 0)
                    continue;

                ConveyorHelper.OutputDestination networkDestination;
                if (!ConveyorHelper.TryFindBestOutputDestinationFromNetworkAnchor(chest, inside, itemId, includeAnchorChest: false, excludeVacuumCrates: true, out networkDestination))
                    continue;

                int sourceSlot = slot;
                int capturedItemId = itemId;
                int capturedStackAmount = stackAmount;
                Chest capturedSourceChest = chest;
                Chest capturedNetworkChest = networkDestination.chest;
                int capturedRouteX = networkDestination.routeX;
                int capturedRouteY = networkDestination.routeY;

                ContainerManager.manage.changeSlotInChest(chest.xPos, chest.yPos, sourceSlot, -1, 0, inside);
                movedAny = true;

                System.Action depositToNetwork = () =>
                {
                    if (HarvestHelper.TryDepositHarvestToChest(capturedNetworkChest, inside, capturedItemId, capturedStackAmount))
                        return;

                    ConveyorHelper.OutputDestination fallbackDestination;
                    if (ConveyorHelper.TryFindBestOutputDestinationFromNetworkAnchor(capturedSourceChest, inside, capturedItemId, includeAnchorChest: false, excludeVacuumCrates: true, out fallbackDestination) &&
                        fallbackDestination != null &&
                        !(fallbackDestination.chest.xPos == capturedNetworkChest.xPos && fallbackDestination.chest.yPos == capturedNetworkChest.yPos))
                    {
                        if (HarvestHelper.TryDepositHarvestToChest(fallbackDestination.chest, inside, capturedItemId, capturedStackAmount))
                            return;
                    }

                    HarvestHelper.TryDepositHarvestToChest(capturedSourceChest, inside, capturedItemId, capturedStackAmount);
                };

                ConveyorAnimator.AnimateTransfer(capturedItemId, capturedStackAmount,
                    new Vector2Int(chest.xPos, chest.yPos),
                    new Vector2Int(capturedRouteX, capturedRouteY),
                    inside, depositToNetwork, delay);

                delay += VACUUM_NETWORK_FLUSH_DELAY;
            }

            if (movedAny)
                TriggerVacuumVisual(chest.xPos, chest.yPos, delay + 0.2f);
        }

        private static List<HarvestTarget> CollectNearbyHarvestTargets(Chest chest, HouseDetails inside)
        {
            if (chest == null || inside != null || !ConveyorHelper.IsVacuumCrate(chest.xPos, chest.yPos, inside))
                return new List<HarvestTarget>();

            if (WorldManager.Instance == null || NetworkMapSharer.Instance == null)
                return new List<HarvestTarget>();

            int[,] tileMap = WorldManager.Instance.onTileMap;
            int[,] statusMap = WorldManager.Instance.onTileStatusMap;
            int mapW = tileMap.GetLength(0);
            int mapH = tileMap.GetLength(1);

            int minX = Mathf.Max(0, chest.xPos - VACUUM_HARVEST_RADIUS_TILES);
            int maxX = Mathf.Min(mapW - 1, chest.xPos + VACUUM_HARVEST_RADIUS_TILES);
            int minY = Mathf.Max(0, chest.yPos - VACUUM_HARVEST_RADIUS_TILES);
            int maxY = Mathf.Min(mapH - 1, chest.yPos + VACUUM_HARVEST_RADIUS_TILES);

            List<HarvestTarget> targets = new List<HarvestTarget>();

            for (int x = minX; x <= maxX; x++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    int tileObjectId = tileMap[x, y];
                    if (tileObjectId < 0)
                        continue;

                    TileObject tileObj = WorldManager.Instance.allObjects[tileObjectId];
                    if (tileObj == null || tileObj.tileObjectGrowthStages == null)
                        continue;

                    TileObjectGrowthStages growth = tileObj.tileObjectGrowthStages;
                    if (!IsVacuumHarvestCandidate(tileObj, growth, statusMap[x, y]))
                        continue;

                    targets.Add(new HarvestTarget
                    {
                        xPos = x,
                        yPos = y,
                        tileObj = tileObj,
                        growth = growth
                    });
                }
            }

            targets.Sort((a, b) =>
            {
                int distA = Mathf.Abs(a.xPos - chest.xPos) + Mathf.Abs(a.yPos - chest.yPos);
                int distB = Mathf.Abs(b.xPos - chest.xPos) + Mathf.Abs(b.yPos - chest.yPos);
                if (distA != distB)
                    return distA.CompareTo(distB);

                if (a.yPos != b.yPos)
                    return a.yPos.CompareTo(b.yPos);

                return a.xPos.CompareTo(b.xPos);
            });

            return targets;
        }

        public static void UpdateVisuals()
        {
            if (activeVisuals.Count == 0)
                return;

            List<long> expired = null;

            foreach (KeyValuePair<long, VacuumVisualState> entry in activeVisuals)
            {
                VacuumVisualState state = entry.Value;
                if (state.visual == null || Time.time >= state.expireAt)
                {
                    if (expired == null)
                        expired = new List<long>();
                    expired.Add(entry.Key);
                    continue;
                }

                Vector3 basePos = ConveyorAnimator.TileToWorld(state.tileX, state.tileY);
                state.visual.transform.position = new Vector3(basePos.x, basePos.y + VACUUM_VISUAL_HEIGHT, basePos.z);
            }

            if (expired == null)
                return;

            for (int i = 0; i < expired.Count; i++)
            {
                long key = expired[i];
                if (!activeVisuals.TryGetValue(key, out VacuumVisualState state))
                    continue;

                if (state.visual != null)
                    GameObject.Destroy(state.visual);

                activeVisuals.Remove(key);
            }
        }

        public static void ClearVisuals()
        {
            foreach (KeyValuePair<long, VacuumVisualState> entry in activeVisuals)
            {
                if (entry.Value.visual != null)
                    GameObject.Destroy(entry.Value.visual);
            }

            activeVisuals.Clear();
            pendingDrops.Clear();
        }

        private static bool IsDropInSameSpace(DroppedItem drop, HouseDetails inside)
        {
            if (inside == null)
                return drop.inside == null;

            if (drop.inside == null)
                return false;

            return drop.inside.xPos == inside.xPos && drop.inside.yPos == inside.yPos;
        }

        private static Vector2Int GetDropTile(DroppedItem drop)
        {
            if (drop == null)
                return Vector2Int.zero;

            int tileX = Mathf.RoundToInt(drop.onTile.x);
            int tileY = Mathf.RoundToInt(drop.onTile.y);

            if (tileX == 0 && tileY == 0)
            {
                tileX = Mathf.RoundToInt(drop.transform.position.x / 2f);
                tileY = Mathf.RoundToInt(drop.transform.position.z / 2f);
            }

            return new Vector2Int(tileX, tileY);
        }

        private static bool CanVacuumDeposit(Chest chest, HouseDetails inside, int itemId)
        {
            if (chest == null || itemId < 0)
                return false;

            if (ConveyorHelper.FindSlotForItem(chest, itemId) != -1)
                return true;

            return ConveyorHelper.CanDepositFromPosition(chest.xPos, chest.yPos, inside, itemId);
        }

        private static List<VacuumDropGroup> BuildDropGroups(List<DroppedItem> candidates)
        {
            List<VacuumDropGroup> groups = new List<VacuumDropGroup>();
            if (candidates == null || candidates.Count == 0)
                return groups;

            Dictionary<string, VacuumDropGroup> grouped = new Dictionary<string, VacuumDropGroup>();

            for (int i = 0; i < candidates.Count; i++)
            {
                DroppedItem drop = candidates[i];
                if (drop == null || drop.myItemId < 0 || drop.stackAmount <= 0)
                    continue;

                InventoryItem invItem = Inventory.Instance.allItems[drop.myItemId];
                bool isStackable = invItem != null && invItem.checkIfStackable();
                Vector2Int dropTile = GetDropTile(drop);
                string key = isStackable
                    ? dropTile.x + ":" + dropTile.y + ":" + drop.myItemId + ":" + drop.endOfDayTallyType
                    : drop.netId.ToString();

                if (!grouped.TryGetValue(key, out VacuumDropGroup group))
                {
                    group = new VacuumDropGroup
                    {
                        itemId = drop.myItemId,
                        totalStackAmount = 0,
                        tallyType = drop.endOfDayTallyType,
                        dropTile = dropTile,
                        startWorld = drop.transform.position
                    };
                    grouped[key] = group;
                    groups.Add(group);
                }

                group.totalStackAmount += drop.stackAmount;
                group.drops.Add(drop);
                group.dropIds.Add(drop.netId);
            }

            return groups;
        }

        private static void ActivateAllVacuumCrates()
        {
            int mapSize = WorldManager.Instance.GetMapSize();

            for (int y = 0; y < mapSize; y++)
            {
                for (int x = 0; x < mapSize; x++)
                {
                    if (WorldManager.Instance.onTileMap[x, y] != Plugin.GREEN_CRATE_TILE_ID)
                        continue;

                    ContainerManager.manage.getChestForRecycling(x, y, null);
                }
            }
        }

        private static bool IsVacuumHarvestCandidate(TileObject tileObj, TileObjectGrowthStages growth, int stage)
        {
            if (tileObj == null || growth == null)
                return false;

            bool isFarmCrop = growth.needsTilledSoil || growth.isAPlantSproutFromAFarmPlant(tileObj.tileObjectId);
            bool canHarvest = isFarmCrop
                ? growth.canBeHarvested(stage, deathCheck: true)
                : growth.canBeHarvested(stage);

            if (!canHarvest || growth.isCrabPot || growth.mustBeInWater)
                return false;

            bool isTreeFruitStyle = growth.harvestableByHand &&
                                    ((bool)growth.harvestDrop || (bool)growth.dropsFromLootTable) &&
                                    tileObj.tileObjectChest == null &&
                                    tileObj.tileObjectItemChanger == null;

            return isFarmCrop || isTreeFruitStyle;
        }

        private static void HarvestPlantAt(int xPos, int yPos, TileObject tileObj, TileObjectGrowthStages growth)
        {
            int newStatus = WorldManager.Instance.onTileStatusMap[xPos, yPos] + growth.takeOrAddFromStateOnHarvest;
            if (newStatus < 0)
                newStatus = 0;

            AutomationCreditHelper.TryGrantHarvestMilestone(tileObj.tileObjectId);
            AutomationCreditHelper.TryGrantGrowthHarvestCredit(tileObj.tileObjectId);

            if (growth.diesOnHarvest)
            {
                NetworkMapSharer.Instance.RpcHarvestObject(-1, xPos, yPos, spawnDrop: true);
            }
            else
            {
                NetworkMapSharer.Instance.RpcHarvestObject(newStatus, xPos, yPos, spawnDrop: true);
                WorldManager.Instance.onTileStatusMap[xPos, yPos] = newStatus;
            }

            WorldManager.Instance.onTileChunkHasChanged(xPos, yPos);
        }

        private static bool TryRemoveDropFromWorld(DroppedItem drop)
        {
            if (drop == null)
                return false;

            try
            {
                WorldManager.Instance.itemsOnGround.Remove(drop);

                Collider collider = drop.GetComponent<Collider>();
                if (collider != null)
                    collider.enabled = false;

                Renderer[] renderers = drop.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                {
                    renderers[i].enabled = false;
                }

                if (NetworkServer.active && drop.netIdentity != null)
                    NetworkServer.Destroy(drop.gameObject);
                else
                    GameObject.Destroy(drop.gameObject);

                return true;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Autom8er: Failed to remove drop for vacuum crate: " + e.Message);
                return false;
            }
        }

        private static void SpawnFallbackGroundDrop(int itemId, int stackAmount, int tallyType, Vector3 chestWorld, HouseDetails inside)
        {
            GameObject dropped = WorldManager.Instance.dropAnItem(itemId, stackAmount, chestWorld, inside, tryNotToStack: false);
            if (dropped == null)
                return;

            DroppedItem droppedItem = dropped.GetComponent<DroppedItem>();
            if (droppedItem != null && tallyType > -1)
            {
                droppedItem.NetworkendOfDayTallyType = tallyType;
            }
        }

        private static void TriggerVacuumVisual(int tileX, int tileY, float duration)
        {
            long key = PosKey(tileX, tileY);
            float expireAt = Time.time + Mathf.Max(0.25f, duration);

            if (activeVisuals.TryGetValue(key, out VacuumVisualState state))
            {
                state.expireAt = Mathf.Max(state.expireAt, expireAt);
                return;
            }

            GameObject visual = CreateHarVacVisual(tileX, tileY);
            if (visual == null)
                return;

            activeVisuals[key] = new VacuumVisualState
            {
                visual = visual,
                expireAt = expireAt,
                tileX = tileX,
                tileY = tileY
            };
        }

        private static GameObject CreateHarVacVisual(int tileX, int tileY)
        {
            try
            {
                InventoryItem harVac = Inventory.Instance.allItems[Plugin.HAR_VAC_ITEM_ID];
                if (harVac == null || harVac.itemPrefab == null)
                    return null;

                GameObject visual = GameObject.Instantiate(harVac.itemPrefab);
                Vector3 basePos = ConveyorAnimator.TileToWorld(tileX, tileY);
                visual.transform.position = new Vector3(basePos.x, basePos.y + VACUUM_VISUAL_HEIGHT, basePos.z);
                visual.transform.localScale = Vector3.one;

                Rigidbody rb = visual.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.isKinematic = true;
                    rb.detectCollisions = false;
                }

                Collider[] colliders = visual.GetComponentsInChildren<Collider>(true);
                for (int i = 0; i < colliders.Length; i++)
                {
                    colliders[i].enabled = false;
                }

                Animator animator = visual.GetComponent<Animator>();
                if (animator != null)
                    animator.enabled = false;

                DroppedItem droppedItem = visual.GetComponent<DroppedItem>();
                if (droppedItem != null)
                    GameObject.Destroy(droppedItem);

                DroppedItemBounce bounce = visual.GetComponent<DroppedItemBounce>();
                if (bounce != null)
                    GameObject.Destroy(bounce);

                Renderer[] renderers = visual.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                {
                    if (!(renderers[i] is ParticleSystemRenderer))
                        renderers[i].enabled = false;
                }

                VacuumMachine vacuumMachine = visual.GetComponentInChildren<VacuumMachine>(true);
                if (vacuumMachine != null)
                {
                    Transform target = new GameObject("Autom8erVacTarget").transform;
                    target.SetParent(visual.transform, worldPositionStays: false);
                    target.localPosition = new Vector3(0f, -0.35f, 0f);
                    vacuumMachine.targetTransform = target;

                    if (vacuumMachine.particleSystem1 != null)
                    {
                        vacuumMachine.particleSystem1.Clear(withChildren: true);
                        vacuumMachine.particleSystem1.Play(withChildren: true);
                    }

                    if (vacuumMachine.particleSystem2 != null)
                    {
                        vacuumMachine.particleSystem2.Clear(withChildren: true);
                        vacuumMachine.particleSystem2.Play(withChildren: true);
                    }
                }
                else
                {
                    GameObject.Destroy(visual);
                    return null;
                }

                return visual;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Autom8er: Failed to create Har-Vac visual: " + e.Message);
                return null;
            }
        }

        private static Vector3 GetVacuumTargetWorld(int tileX, int tileY)
        {
            Vector3 basePos = ConveyorAnimator.TileToWorld(tileX, tileY);
            return new Vector3(basePos.x, basePos.y + 0.95f, basePos.z);
        }
    }

    public static class FilterCrateHelper
    {
        private static readonly int[] dx = { -1, 0, 1, 0 };
        private static readonly int[] dy = { 0, -1, 0, 1 };

        private class SourcePull
        {
            public Chest sourceChest;
            public int sourceSlot;
            public int itemId;
            public int moveAmount;
        }

        public static void TryPullFilteredItemsFromNetwork(Chest filterChest, HouseDetails inside)
        {
            if (filterChest == null || inside != null || !ConveyorHelper.IsFilterCrate(filterChest.xPos, filterChest.yPos, inside))
                return;

            ConveyorHelper.EnsureAutomationChestsActive(filterChest);

            HashSet<int> processedItemIds = new HashSet<int>();

            for (int slot = 0; slot < filterChest.itemIds.Length; slot++)
            {
                int itemId = filterChest.itemIds[slot];
                int stackAmount = filterChest.itemStacks[slot];

                if (itemId < 0 || stackAmount <= 0 || !processedItemIds.Add(itemId))
                    continue;

                InventoryItem filterItem = Inventory.Instance.allItems[itemId];
                bool isFuelItem = filterItem != null && filterItem.hasFuel;

                ConveyorHelper.OutputDestination destination;
                if (!ConveyorHelper.TryFindFilterDestinationFromCrate(filterChest, inside, itemId, out destination))
                    continue;

                if (!isFuelItem && stackAmount > 1)
                {
                    int extraAmount = stackAmount - 1;
                    if (HarvestHelper.TryDepositHarvestToChest(destination.chest, inside, itemId, extraAmount))
                    {
                        ContainerManager.manage.changeSlotInChest(filterChest.xPos, filterChest.yPos, slot, itemId, 1, inside);
                    }
                }

                SourcePull pull = FindSourcePull(filterChest, inside, itemId, destination.chest);
                if (pull == null)
                    continue;

                RemoveFromSourceChest(pull.sourceChest, pull.sourceSlot, pull.itemId, pull.moveAmount, inside);

                Chest capturedSourceChest = pull.sourceChest;
                int capturedItemId = pull.itemId;
                int capturedAmount = pull.moveAmount;
                Chest capturedDestinationChest = destination.chest;
                int capturedRouteX = destination.routeX;
                int capturedRouteY = destination.routeY;

                System.Action depositFiltered = () =>
                {
                    if (HarvestHelper.TryDepositHarvestToChest(capturedDestinationChest, inside, capturedItemId, capturedAmount))
                        return;

                    if (ConveyorHelper.FallbackDepositToAnyChest(capturedDestinationChest.xPos, capturedDestinationChest.yPos, inside, capturedItemId, capturedAmount))
                        return;

                    HarvestHelper.TryDepositHarvestToChest(capturedSourceChest, inside, capturedItemId, capturedAmount);
                };

                ConveyorAnimator.AnimateTransfer(capturedItemId, capturedAmount,
                    new Vector2Int(capturedSourceChest.xPos, capturedSourceChest.yPos),
                    new Vector2Int(capturedRouteX, capturedRouteY),
                    inside, depositFiltered);

                return;
            }
        }

        private static SourcePull FindSourcePull(Chest filterChest, HouseDetails inside, int itemId, Chest destinationChest)
        {
            if (Plugin.ConveyorTileType == -1)
                return null;

            HashSet<Vector2Int> pathNetwork = new HashSet<Vector2Int>();
            Queue<Vector2Int> toExplore = new Queue<Vector2Int>();
            HashSet<long> seenChests = new HashSet<long>();

            for (int i = 0; i < 4; i++)
            {
                int checkX = filterChest.xPos + dx[i];
                int checkY = filterChest.yPos + dy[i];

                if (checkX < 0 || checkY < 0)
                    continue;

                if (!ConveyorHelper.IsConveyorTile(checkX, checkY, inside))
                    continue;

                Vector2Int pos = new Vector2Int(checkX, checkY);
                if (pathNetwork.Add(pos))
                    toExplore.Enqueue(pos);
            }

            while (toExplore.Count > 0)
            {
                Vector2Int current = toExplore.Dequeue();

                for (int i = 0; i < 4; i++)
                {
                    int checkX = current.x + dx[i];
                    int checkY = current.y + dy[i];

                    if (checkX < 0 || checkY < 0)
                        continue;

                    Vector2Int checkPos = new Vector2Int(checkX, checkY);
                    if (pathNetwork.Contains(checkPos))
                        continue;

                    Chest chest = ConveyorHelper.FindChestAt(checkX, checkY, inside);
                    if (chest == null)
                        continue;

                    if (chest.xPos == filterChest.xPos && chest.yPos == filterChest.yPos)
                        continue;

                    if (destinationChest != null &&
                        chest.xPos == destinationChest.xPos &&
                        chest.yPos == destinationChest.yPos)
                        continue;

                    if (ConveyorHelper.IsOutputOnlyContainer(checkX, checkY, inside) ||
                        ConveyorHelper.IsFilterCrate(checkX, checkY, inside) ||
                        ConveyorHelper.IsVacuumCrate(checkX, checkY, inside) ||
                        ConveyorHelper.IsSpecialContainer(checkX, checkY, inside))
                        continue;

                    long chestKey = (((long)chest.xPos & 0xFFFFFL) << 44)
                        | (((long)chest.yPos & 0xFFFFFL) << 24)
                        | (((long)(chest.insideX + 1) & 0xFFFL) << 12)
                        | ((long)(chest.insideY + 1) & 0xFFFL);

                    if (!seenChests.Add(chestKey))
                        continue;

                    for (int slot = 0; slot < chest.itemIds.Length; slot++)
                    {
                        if (chest.itemIds[slot] != itemId || chest.itemStacks[slot] <= 0)
                            continue;

                        int moveAmount = GetTransferAmount(chest, slot, itemId);
                        if (moveAmount <= 0)
                            continue;

                        return new SourcePull
                        {
                            sourceChest = chest,
                            sourceSlot = slot,
                            itemId = itemId,
                            moveAmount = moveAmount
                        };
                    }
                }

                for (int i = 0; i < 4; i++)
                {
                    int nextX = current.x + dx[i];
                    int nextY = current.y + dy[i];

                    if (nextX < 0 || nextY < 0)
                        continue;

                    Vector2Int nextPos = new Vector2Int(nextX, nextY);
                    if (pathNetwork.Contains(nextPos))
                        continue;

                    if (ConveyorHelper.IsConveyorTile(nextX, nextY, inside))
                    {
                        pathNetwork.Add(nextPos);
                        toExplore.Enqueue(nextPos);
                    }
                }
            }

            return null;
        }

        private static int GetTransferAmount(Chest sourceChest, int slot, int itemId)
        {
            int stack = sourceChest.itemStacks[slot];
            if (stack <= 0)
                return 0;

            InventoryItem item = Inventory.Instance.allItems[itemId];
            if (item == null)
                return 0;

            bool keepOne = Plugin.KeepOneItem && !ConveyorHelper.IsAutoSorter(sourceChest) && item.checkIfStackable();
            return keepOne ? stack - 1 : stack;
        }

        private static void RemoveFromSourceChest(Chest sourceChest, int slot, int itemId, int moveAmount, HouseDetails inside)
        {
            int remaining = sourceChest.itemStacks[slot] - moveAmount;
            if (remaining <= 0)
            {
                ContainerManager.manage.changeSlotInChest(sourceChest.xPos, sourceChest.yPos, slot, -1, 0, inside);
                return;
            }

            ContainerManager.manage.changeSlotInChest(sourceChest.xPos, sourceChest.yPos, slot, itemId, remaining, inside);
        }
    }

    public static class ConveyorAnimator
    {
        private static List<ConveyorAnimation> activeAnimations = new List<ConveyorAnimation>();
        private static List<ArcAnimation> activeArcAnimations = new List<ArcAnimation>();
        private static HashSet<long> reservedTargets = new HashSet<long>();

        private class ConveyorAnimation
        {
            public GameObject visual;
            public List<Vector2Int> path;
            public int currentSegment;
            public float segmentProgress;
            public System.Action onArrival;
            public int itemId;
            public float startDelay;
        }

        private class ArcAnimation
        {
            public GameObject visual;
            public Vector3 startWorld;
            public Vector3 endWorld;
            public float duration;
            public float elapsed;
            public float arcHeight;
            public System.Action onArrival;
            public float startDelay;
        }

        private static long PosKey(int x, int y)
        {
            return ((long)x << 32) | (uint)y;
        }

        public static void ReserveTarget(int x, int y)
        {
            reservedTargets.Add(PosKey(x, y));
        }

        public static void UnreserveTarget(int x, int y)
        {
            reservedTargets.Remove(PosKey(x, y));
        }

        public static bool IsTargetReserved(int x, int y)
        {
            return reservedTargets.Contains(PosKey(x, y));
        }

        public static Vector3 TileToWorld(int tileX, int tileY)
        {
            // Game places tile objects at (x*2, height, y*2) — no centering offset
            float worldX = tileX * 2f;
            float worldY = 0.5f;
            if (WorldManager.Instance != null &&
                tileX >= 0 && tileX < WorldManager.Instance.heightMap.GetLength(0) &&
                tileY >= 0 && tileY < WorldManager.Instance.heightMap.GetLength(1))
            {
                worldY = WorldManager.Instance.heightMap[tileX, tileY] + 0.5f;
            }
            float worldZ = tileY * 2f;
            return new Vector3(worldX, worldY, worldZ);
        }

        // Item travels 30% into the destination tile before vanishing
        private const float FINAL_TILE_FRACTION = 0.3f;

        public static void StartAnimation(int itemId, int stackAmount, List<Vector2Int> path, System.Action onArrival, float delay = 0f)
        {
            if (path == null || path.Count < 2)
            {
                if (onArrival != null)
                    onArrival();
                return;
            }

            GameObject visual = CreateItemVisual(itemId, TileToWorld(path[0].x, path[0].y), delay > 0f);

            if (visual == null)
            {
                if (onArrival != null)
                    onArrival();
                return;
            }

            ConveyorAnimation anim = new ConveyorAnimation
            {
                visual = visual,
                path = path,
                currentSegment = 0,
                segmentProgress = 0f,
                onArrival = onArrival,
                itemId = itemId,
                startDelay = delay
            };

            activeAnimations.Add(anim);
        }

        public static void AnimateArcTransfer(int itemId, int stackAmount, Vector3 startWorld, Vector3 endWorld, float duration, float arcHeight, System.Action onArrival, float delay = 0f)
        {
            if (!Plugin.AnimationEnabled)
            {
                if (onArrival != null)
                    onArrival();
                return;
            }

            GameObject visual = CreateItemVisual(itemId, startWorld, delay > 0f);
            if (visual == null)
            {
                if (onArrival != null)
                    onArrival();
                return;
            }

            activeArcAnimations.Add(new ArcAnimation
            {
                visual = visual,
                startWorld = startWorld,
                endWorld = endWorld,
                duration = Mathf.Max(0.01f, duration),
                elapsed = 0f,
                arcHeight = arcHeight,
                onArrival = onArrival,
                startDelay = delay
            });
        }

        public static void UpdateAnimations()
        {
            if (activeAnimations.Count == 0 && activeArcAnimations.Count == 0)
                return;

            float speed = Plugin.AnimationSpeed;

            for (int i = activeAnimations.Count - 1; i >= 0; i--)
            {
                ConveyorAnimation anim = activeAnimations[i];

                if (anim.visual == null)
                {
                    if (anim.onArrival != null)
                    {
                        try { anim.onArrival(); }
                        catch (System.Exception e) { Plugin.Log.LogWarning("Autom8er: Animation callback error: " + e.Message); }
                    }
                    activeAnimations.RemoveAt(i);
                    continue;
                }

                if (anim.startDelay > 0f)
                {
                    anim.startDelay -= Time.deltaTime;
                    if (anim.visual.activeSelf)
                        anim.visual.SetActive(false);
                    continue;
                }
                else if (!anim.visual.activeSelf)
                {
                    anim.visual.SetActive(true);
                }

                anim.segmentProgress += Time.deltaTime * speed;

                while (anim.segmentProgress >= 1f && anim.currentSegment < anim.path.Count - 2)
                {
                    anim.segmentProgress -= 1f;
                    anim.currentSegment++;
                }

                bool isLastSegment = (anim.currentSegment >= anim.path.Count - 2);
                float arrivalThreshold = isLastSegment ? FINAL_TILE_FRACTION : 1f;

                if (isLastSegment && anim.segmentProgress >= arrivalThreshold)
                {
                    if (anim.onArrival != null)
                    {
                        try { anim.onArrival(); }
                        catch (System.Exception e) { Plugin.Log.LogWarning("Autom8er: Animation callback error: " + e.Message); }
                    }
                    GameObject.Destroy(anim.visual);
                    activeAnimations.RemoveAt(i);
                    continue;
                }

                Vector2Int from = anim.path[anim.currentSegment];
                Vector2Int to = anim.path[anim.currentSegment + 1];
                Vector3 fromPos = TileToWorld(from.x, from.y);
                Vector3 toPos = TileToWorld(to.x, to.y);
                float t = Mathf.Clamp01(anim.segmentProgress);
                anim.visual.transform.position = Vector3.Lerp(fromPos, toPos, t);
            }

            for (int i = activeArcAnimations.Count - 1; i >= 0; i--)
            {
                ArcAnimation anim = activeArcAnimations[i];

                if (anim.visual == null)
                {
                    if (anim.onArrival != null)
                    {
                        try { anim.onArrival(); }
                        catch (System.Exception e) { Plugin.Log.LogWarning("Autom8er: Arc animation callback error: " + e.Message); }
                    }
                    activeArcAnimations.RemoveAt(i);
                    continue;
                }

                if (anim.startDelay > 0f)
                {
                    anim.startDelay -= Time.deltaTime;
                    if (anim.visual.activeSelf)
                        anim.visual.SetActive(false);
                    continue;
                }
                else if (!anim.visual.activeSelf)
                {
                    anim.visual.SetActive(true);
                }

                anim.elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(anim.elapsed / anim.duration);
                Vector3 pos = Vector3.Lerp(anim.startWorld, anim.endWorld, t);
                pos.y += Mathf.Sin(t * Mathf.PI) * anim.arcHeight;
                anim.visual.transform.position = pos;

                if (t >= 1f)
                {
                    if (anim.onArrival != null)
                    {
                        try { anim.onArrival(); }
                        catch (System.Exception e) { Plugin.Log.LogWarning("Autom8er: Arc animation callback error: " + e.Message); }
                    }
                    GameObject.Destroy(anim.visual);
                    activeArcAnimations.RemoveAt(i);
                }
            }
        }

        public static void ClearAllAnimations()
        {
            for (int i = activeAnimations.Count - 1; i >= 0; i--)
            {
                ConveyorAnimation anim = activeAnimations[i];
                if (anim.onArrival != null)
                {
                    try { anim.onArrival(); }
                    catch (System.Exception e) { Plugin.Log.LogWarning("Autom8er: ClearAll callback error: " + e.Message); }
                }
                if (anim.visual != null)
                    GameObject.Destroy(anim.visual);
            }
            activeAnimations.Clear();

            for (int i = activeArcAnimations.Count - 1; i >= 0; i--)
            {
                ArcAnimation anim = activeArcAnimations[i];
                if (anim.onArrival != null)
                {
                    try { anim.onArrival(); }
                    catch (System.Exception e) { Plugin.Log.LogWarning("Autom8er: ClearAll arc callback error: " + e.Message); }
                }
                if (anim.visual != null)
                    GameObject.Destroy(anim.visual);
            }
            activeArcAnimations.Clear();

            reservedTargets.Clear();
        }

        public static bool HasActiveAnimations()
        {
            return activeAnimations.Count > 0 || activeArcAnimations.Count > 0;
        }

        public static void AnimateTransfer(int itemId, int amount, Vector2Int source, Vector2Int dest, HouseDetails inside, System.Action depositCallback, float delay = 0f)
        {
            if (!Plugin.AnimationEnabled)
            {
                depositCallback();
                return;
            }

            List<Vector2Int> path = ConveyorPathfinder.FindPath(source, dest, inside);
            if (path == null || path.Count < 2)
            {
                depositCallback();
                return;
            }

            StartAnimation(itemId, amount, path, depositCallback, delay);
        }

        private static GameObject CreateItemVisual(int itemId, Vector3 startWorld, bool startHidden)
        {
            GameObject visual = null;
            try
            {
                InventoryItem invItem = Inventory.Instance.allItems[itemId];
                if (invItem != null && invItem.itemPrefab != null)
                {
                    visual = GameObject.Instantiate(invItem.itemPrefab);
                    visual.transform.position = startWorld;
                    visual.transform.localScale = Vector3.one * 0.75f;

                    Rigidbody rb = visual.GetComponent<Rigidbody>();
                    if (rb != null)
                    {
                        rb.isKinematic = true;
                        rb.detectCollisions = false;
                    }
                    Collider col = visual.GetComponent<Collider>();
                    if (col != null)
                        col.enabled = false;

                    DroppedItem droppedItem = visual.GetComponent<DroppedItem>();
                    if (droppedItem != null)
                        GameObject.Destroy(droppedItem);
                    DroppedItemBounce bounce = visual.GetComponent<DroppedItemBounce>();
                    if (bounce != null)
                        GameObject.Destroy(bounce);

                    SetItemTexture setTex = visual.GetComponent<SetItemTexture>();
                    if (setTex != null)
                        setTex.setTexture(invItem);

                    if (startHidden)
                        visual.SetActive(false);
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Autom8er: Failed to create animation visual for item " + itemId + ": " + e.Message);
            }

            return visual;
        }
    }

    public static class CrabPotHelper
    {
        private static readonly int[] dx = { -1, 0, 1, 0 };
        private static readonly int[] dy = { 0, -1, 0, 1 };

        // Try to load bait from chest into nearby crab pots (within 2-tile radius or via conveyor)
        public static void TryLoadBaitIntoCrabPots(Chest sourceChest, HouseDetails inside)
        {
            // Crab pots are only placed outside (in water), so skip indoor chests
            if (inside != null)
                return;

            HashSet<long> checkedPositions = new HashSet<long>();

            // Check 2-tile radius around chest for crab pots
            if (TryLoadBaitInRadius(sourceChest, sourceChest.xPos, sourceChest.yPos, checkedPositions))
                return;

            // Also check via conveyor path
            TryLoadBaitViaConveyorPath(sourceChest, checkedPositions);
        }

        private static bool TryLoadBaitInRadius(Chest sourceChest, int centerX, int centerY, HashSet<long> checkedPositions)
        {
            for (int offsetX = -2; offsetX <= 2; offsetX++)
            {
                for (int offsetY = -2; offsetY <= 2; offsetY++)
                {
                    if (offsetX == 0 && offsetY == 0)
                        continue;

                    // Manhattan distance check for 2-tile radius
                    if (Mathf.Abs(offsetX) + Mathf.Abs(offsetY) > 2)
                        continue;

                    int checkX = centerX + offsetX;
                    int checkY = centerY + offsetY;

                    if (checkX < 0 || checkY < 0)
                        continue;

                    long key = ((long)checkX << 32) | (uint)checkY;
                    if (checkedPositions.Contains(key))
                        continue;
                    checkedPositions.Add(key);

                    if (checkX >= WorldManager.Instance.onTileMap.GetLength(0) ||
                        checkY >= WorldManager.Instance.onTileMap.GetLength(1))
                        continue;

                    if (TryLoadBaitAtPosition(sourceChest, checkX, checkY))
                        return true;
                }
            }
            return false;
        }

        private static void TryLoadBaitViaConveyorPath(Chest sourceChest, HashSet<long> checkedPositions)
        {
            if (Plugin.ConveyorTileType < 0)
                return;

            HashSet<long> visitedConveyors = new HashSet<long>();
            Queue<(int x, int y)> queue = new Queue<(int x, int y)>();

            // Start by checking adjacent conveyor tiles from chest
            for (int i = 0; i < 4; i++)
            {
                int nx = sourceChest.xPos + dx[i];
                int ny = sourceChest.yPos + dy[i];

                if (nx < 0 || ny < 0)
                    continue;

                if (ConveyorHelper.IsConveyorTile(nx, ny, null))
                {
                    long key = ((long)nx << 32) | (uint)ny;
                    if (!visitedConveyors.Contains(key))
                    {
                        visitedConveyors.Add(key);
                        queue.Enqueue((nx, ny));
                    }
                }
            }

            // BFS along conveyor tiles, checking 2-tile radius around each for crab pots
            while (queue.Count > 0)
            {
                var (cx, cy) = queue.Dequeue();

                // Check 2-tile radius around this conveyor tile for crab pots
                if (TryLoadBaitInRadius(sourceChest, cx, cy, checkedPositions))
                    return;

                // Expand to adjacent conveyor tiles
                for (int i = 0; i < 4; i++)
                {
                    int nx = cx + dx[i];
                    int ny = cy + dy[i];

                    if (nx < 0 || ny < 0)
                        continue;

                    long key = ((long)nx << 32) | (uint)ny;
                    if (visitedConveyors.Contains(key))
                        continue;

                    if (ConveyorHelper.IsConveyorTile(nx, ny, null))
                    {
                        visitedConveyors.Add(key);
                        queue.Enqueue((nx, ny));
                    }
                }
            }
        }

        private static bool TryLoadBaitAtPosition(Chest sourceChest, int checkX, int checkY)
        {
            int tileObjectId = WorldManager.Instance.onTileMap[checkX, checkY];
            if (tileObjectId < 0)
                return false;

            TileObject tileObj = WorldManager.Instance.allObjects[tileObjectId];
            if (tileObj == null || tileObj.tileObjectGrowthStages == null)
                return false;

            TileObjectGrowthStages growth = tileObj.tileObjectGrowthStages;
            if (!growth.isCrabPot)
                return false;

            // Check if crab pot needs bait
            int currentStatus = WorldManager.Instance.onTileStatusMap[checkX, checkY];
            if (currentStatus >= growth.maxStageToReachByPlacing)
                return false; // Already baited

            if (ConveyorAnimator.IsTargetReserved(checkX, checkY))
                return false;

            // Try to find valid bait in chest
            return TryLoadBaitFromChest(sourceChest, checkX, checkY, growth);
        }

        private static bool TryLoadBaitFromChest(Chest chest, int crabPotX, int crabPotY, TileObjectGrowthStages growth)
        {
            if (growth.itemsToPlace == null || growth.itemsToPlace.Length == 0)
                return false;

            // Search chest for valid bait items
            for (int slot = 0; slot < chest.itemIds.Length; slot++)
            {
                int itemId = chest.itemIds[slot];
                int stack = chest.itemStacks[slot];

                if (itemId < 0 || stack <= 0)
                    continue;

                // Check if this item is valid bait
                InventoryItem item = Inventory.Instance.allItems[itemId];
                bool isValidBait = false;

                for (int i = 0; i < growth.itemsToPlace.Length; i++)
                {
                    if (growth.itemsToPlace[i] == item)
                    {
                        isValidBait = true;
                        break;
                    }
                }

                if (!isValidBait)
                    continue;

                // Check KeepOneItem setting (AutoSorters exempt)
                bool keepOne = Plugin.KeepOneItem && !ConveyorHelper.IsAutoSorter(chest);
                int minRequired = keepOne ? 2 : 1;
                if (stack < minRequired)
                    continue;

                int remaining = stack - 1;
                if (remaining <= 0)
                    ContainerManager.manage.changeSlotInChest(chest.xPos, chest.yPos, slot, -1, 0, null);
                else
                    ContainerManager.manage.changeSlotInChest(chest.xPos, chest.yPos, slot, itemId, remaining, null);

                ConveyorAnimator.ReserveTarget(crabPotX, crabPotY);

                int capturedPotX = crabPotX;
                int capturedPotY = crabPotY;
                System.Action depositBait = () =>
                {
                    int newStatus = WorldManager.Instance.onTileStatusMap[capturedPotX, capturedPotY] + 1;
                    WorldManager.Instance.onTileStatusMap[capturedPotX, capturedPotY] = newStatus;
                    NetworkMapSharer.Instance.RpcGiveOnTileStatus(newStatus, capturedPotX, capturedPotY);
                    ConveyorAnimator.UnreserveTarget(capturedPotX, capturedPotY);
                };

                ConveyorAnimator.AnimateTransfer(itemId, 1,
                    new Vector2Int(chest.xPos, chest.yPos),
                    new Vector2Int(crabPotX, crabPotY), null, depositBait);

                Plugin.Log.LogInfo("Autom8er: Loaded bait into crab pot at " + crabPotX + "," + crabPotY + ": " + item.itemName);
                return true;
            }

            return false;
        }
    }

    // Handles loading items into TileObjectGrowthStages objects (incubators, etc)
    // Excludes crab pots (handled by CrabPotHelper with 2-tile radius)
    public static class GrowthStageHelper
    {
        private static readonly int[] dx = { -1, 0, 1, 0 };
        private static readonly int[] dy = { 0, -1, 0, 1 };

        public static void TryLoadItemsIntoGrowthStages(Chest sourceChest, HouseDetails inside)
        {
            HashSet<long> checkedPositions = new HashSet<long>();

            // Check 1-tile radius around chest
            if (TryLoadInRadius(sourceChest, sourceChest.xPos, sourceChest.yPos, inside, checkedPositions))
                return;

            // Also check via conveyor path
            TryLoadViaConveyorPath(sourceChest, inside, checkedPositions);
        }

        private static bool TryLoadInRadius(Chest sourceChest, int centerX, int centerY, HouseDetails inside, HashSet<long> checkedPositions)
        {
            for (int i = 0; i < 4; i++)
            {
                int checkX = centerX + dx[i];
                int checkY = centerY + dy[i];

                if (checkX < 0 || checkY < 0)
                    continue;

                long key = ((long)checkX << 32) | (uint)checkY;
                if (checkedPositions.Contains(key))
                    continue;
                checkedPositions.Add(key);

                if (checkX >= WorldManager.Instance.onTileMap.GetLength(0) ||
                    checkY >= WorldManager.Instance.onTileMap.GetLength(1))
                    continue;

                if (TryLoadAtPosition(sourceChest, checkX, checkY, inside))
                    return true;
            }
            return false;
        }

        private static void TryLoadViaConveyorPath(Chest sourceChest, HouseDetails inside, HashSet<long> checkedPositions)
        {
            if (Plugin.ConveyorTileType < 0)
                return;

            HashSet<long> visitedConveyors = new HashSet<long>();
            Queue<(int x, int y)> queue = new Queue<(int x, int y)>();

            for (int i = 0; i < 4; i++)
            {
                int nx = sourceChest.xPos + dx[i];
                int ny = sourceChest.yPos + dy[i];

                if (nx < 0 || ny < 0)
                    continue;

                if (ConveyorHelper.IsConveyorTile(nx, ny, inside))
                {
                    long key = ((long)nx << 32) | (uint)ny;
                    if (!visitedConveyors.Contains(key))
                    {
                        visitedConveyors.Add(key);
                        queue.Enqueue((nx, ny));
                    }
                }
            }

            while (queue.Count > 0)
            {
                var (cx, cy) = queue.Dequeue();

                if (TryLoadInRadius(sourceChest, cx, cy, inside, checkedPositions))
                    return;

                for (int i = 0; i < 4; i++)
                {
                    int nx = cx + dx[i];
                    int ny = cy + dy[i];

                    if (nx < 0 || ny < 0)
                        continue;

                    long key = ((long)nx << 32) | (uint)ny;
                    if (visitedConveyors.Contains(key))
                        continue;

                    if (ConveyorHelper.IsConveyorTile(nx, ny, inside))
                    {
                        visitedConveyors.Add(key);
                        queue.Enqueue((nx, ny));
                    }
                }
            }
        }

        private static bool TryLoadAtPosition(Chest sourceChest, int checkX, int checkY, HouseDetails inside)
        {
            int tileObjectId = inside != null
                ? inside.houseMapOnTile[checkX, checkY]
                : WorldManager.Instance.onTileMap[checkX, checkY];

            if (tileObjectId < 0)
                return false;

            TileObject tileObj = WorldManager.Instance.allObjects[tileObjectId];
            if (tileObj == null || tileObj.tileObjectGrowthStages == null)
                return false;

            TileObjectGrowthStages growth = tileObj.tileObjectGrowthStages;

            // Skip crab pots (handled by CrabPotHelper with 2-tile radius)
            if (growth.isCrabPot)
                return false;

            // Must have items it accepts
            if (growth.itemsToPlace == null || growth.itemsToPlace.Length == 0)
                return false;

            // Check if it needs loading (not yet at max stage for placing)
            int currentStatus = inside != null
                ? inside.houseMapOnTileStatus[checkX, checkY]
                : WorldManager.Instance.onTileStatusMap[checkX, checkY];

            if (currentStatus >= growth.maxStageToReachByPlacing)
                return false; // Already loaded

            if (ConveyorAnimator.IsTargetReserved(checkX, checkY))
                return false;

            return TryLoadFromChest(sourceChest, checkX, checkY, inside, growth);
        }

        private static bool TryLoadFromChest(Chest chest, int targetX, int targetY, HouseDetails inside, TileObjectGrowthStages growth)
        {
            for (int slot = 0; slot < chest.itemIds.Length; slot++)
            {
                int itemId = chest.itemIds[slot];
                int stack = chest.itemStacks[slot];

                if (itemId < 0 || stack <= 0)
                    continue;

                // Check if this item is valid for the growth stage
                InventoryItem item = Inventory.Instance.allItems[itemId];
                bool isValid = false;

                for (int i = 0; i < growth.itemsToPlace.Length; i++)
                {
                    if (growth.itemsToPlace[i] == item)
                    {
                        isValid = true;
                        break;
                    }
                }

                if (!isValid)
                    continue;

                // Check KeepOneItem setting (AutoSorters exempt)
                bool keepOne = Plugin.KeepOneItem && !ConveyorHelper.IsAutoSorter(chest);
                int minRequired = keepOne ? 2 : 1;
                if (stack < minRequired)
                    continue;

                int remaining = stack - 1;
                if (remaining <= 0)
                    ContainerManager.manage.changeSlotInChest(chest.xPos, chest.yPos, slot, -1, 0, null);
                else
                    ContainerManager.manage.changeSlotInChest(chest.xPos, chest.yPos, slot, itemId, remaining, null);

                ConveyorAnimator.ReserveTarget(targetX, targetY);

                int capturedTargetX = targetX;
                int capturedTargetY = targetY;
                HouseDetails capturedInside = inside;
                System.Action depositItem = () =>
                {
                    int newStatus = (capturedInside != null
                        ? capturedInside.houseMapOnTileStatus[capturedTargetX, capturedTargetY]
                        : WorldManager.Instance.onTileStatusMap[capturedTargetX, capturedTargetY]) + 1;

                    if (capturedInside != null)
                    {
                        capturedInside.houseMapOnTileStatus[capturedTargetX, capturedTargetY] = newStatus;
                    }
                    else
                    {
                        WorldManager.Instance.onTileStatusMap[capturedTargetX, capturedTargetY] = newStatus;
                    }
                    NetworkMapSharer.Instance.RpcGiveOnTileStatus(newStatus, capturedTargetX, capturedTargetY);
                    ConveyorAnimator.UnreserveTarget(capturedTargetX, capturedTargetY);
                };

                ConveyorAnimator.AnimateTransfer(itemId, 1,
                    new Vector2Int(chest.xPos, chest.yPos),
                    new Vector2Int(targetX, targetY), inside, depositItem);

                Plugin.Log.LogInfo("Autom8er: Loaded " + item.itemName + " into growth stage object at " + targetX + "," + targetY);
                return true;
            }

            return false;
        }
    }

    public static class SiloHelper
    {
        private static readonly int[] dx = { -1, 0, 1, 0 };
        private static readonly int[] dy = { 0, -1, 0, 1 };

        // Try to load animal feed from chest into nearby silos (within 1-tile radius or via conveyor)
        // Loads one item per tick for visual fill effect
        public static void TryLoadFeedIntoSilos(Chest sourceChest, HouseDetails inside)
        {
            // Silos are only placed outside, skip indoor chests
            if (inside != null)
                return;

            HashSet<long> checkedPositions = new HashSet<long>();

            // Check 1-tile radius around chest for silos (silos are on land, not in water)
            if (TryLoadFeedInRadius(sourceChest, sourceChest.xPos, sourceChest.yPos, checkedPositions))
                return;

            // Also check via conveyor path
            TryLoadFeedViaConveyorPath(sourceChest, checkedPositions);
        }

        private static bool TryLoadFeedInRadius(Chest sourceChest, int centerX, int centerY, HashSet<long> checkedPositions)
        {
            // Check 1-tile radius (silos are on land, adjacent to chests)
            for (int i = 0; i < 4; i++)
            {
                int checkX = centerX + dx[i];
                int checkY = centerY + dy[i];

                if (checkX < 0 || checkY < 0)
                    continue;

                long key = ((long)checkX << 32) | (uint)checkY;
                if (checkedPositions.Contains(key))
                    continue;
                checkedPositions.Add(key);

                if (checkX >= WorldManager.Instance.onTileMap.GetLength(0) ||
                    checkY >= WorldManager.Instance.onTileMap.GetLength(1))
                    continue;

                if (TryLoadFeedAtPosition(sourceChest, checkX, checkY))
                    return true;
            }
            return false;
        }

        private static void TryLoadFeedViaConveyorPath(Chest sourceChest, HashSet<long> checkedPositions)
        {
            if (Plugin.ConveyorTileType < 0)
                return;

            HashSet<long> visitedConveyors = new HashSet<long>();
            Queue<(int x, int y)> queue = new Queue<(int x, int y)>();

            // Start by checking adjacent conveyor tiles from chest
            for (int i = 0; i < 4; i++)
            {
                int nx = sourceChest.xPos + dx[i];
                int ny = sourceChest.yPos + dy[i];

                if (nx < 0 || ny < 0)
                    continue;

                if (ConveyorHelper.IsConveyorTile(nx, ny, null))
                {
                    long key = ((long)nx << 32) | (uint)ny;
                    if (!visitedConveyors.Contains(key))
                    {
                        visitedConveyors.Add(key);
                        queue.Enqueue((nx, ny));
                    }
                }
            }

            // BFS along conveyor tiles, checking adjacent tiles for silos
            while (queue.Count > 0)
            {
                var (cx, cy) = queue.Dequeue();

                // Check adjacent tiles to this conveyor for silos
                if (TryLoadFeedInRadius(sourceChest, cx, cy, checkedPositions))
                    return;

                // Expand to adjacent conveyor tiles
                for (int i = 0; i < 4; i++)
                {
                    int nx = cx + dx[i];
                    int ny = cy + dy[i];

                    if (nx < 0 || ny < 0)
                        continue;

                    long key = ((long)nx << 32) | (uint)ny;
                    if (visitedConveyors.Contains(key))
                        continue;

                    if (ConveyorHelper.IsConveyorTile(nx, ny, null))
                    {
                        visitedConveyors.Add(key);
                        queue.Enqueue((nx, ny));
                    }
                }
            }
        }

        private static bool TryLoadFeedAtPosition(Chest sourceChest, int checkX, int checkY)
        {
            int tileObjectId = WorldManager.Instance.onTileMap[checkX, checkY];

            // For multi-tile objects (silos are 2x2), non-root tiles store negative values
            // Need to find the root tile position to get the actual TileObject ID
            int siloX = checkX;
            int siloY = checkY;

            if (tileObjectId < -1)
            {
                // This is part of a multi-tile object, find the root tile
                Vector2 rootPos = WorldManager.Instance.findMultiTileObjectPos(checkX, checkY);
                siloX = (int)rootPos.x;
                siloY = (int)rootPos.y;
                tileObjectId = WorldManager.Instance.onTileMap[siloX, siloY];
            }

            if (tileObjectId < 0)
                return false;

            TileObject tileObj = WorldManager.Instance.allObjects[tileObjectId];
            if (tileObj == null || tileObj.sprinklerTile == null)
                return false;

            SprinklerTile sprinkler = tileObj.sprinklerTile;
            if (!sprinkler.isSilo)
                return false;

            // Check if silo has room (max 200) - use root tile position for status
            int currentStatus = WorldManager.Instance.onTileStatusMap[siloX, siloY];

            if (currentStatus >= 200)
                return false; // Silo is full

            if (ConveyorAnimator.IsTargetReserved(siloX, siloY))
                return false;

            // Silos only accept Animal Food (item ID 344)
            // The ItemSign component is on the world instance, not the prefab, so we hardcode this
            const int ANIMAL_FOOD_ITEM_ID = 344;

            // Try to find Animal Food in chest - use root tile position
            return TryLoadFeedFromChest(sourceChest, siloX, siloY, ANIMAL_FOOD_ITEM_ID, "Animal Food");
        }

        private static bool TryLoadFeedFromChest(Chest chest, int siloX, int siloY, int validItemId, string itemName)
        {
            int currentStatus = WorldManager.Instance.onTileStatusMap[siloX, siloY];
            int siloRoom = 200 - currentStatus;
            if (siloRoom <= 0)
                return false;

            // Search chest for valid feed item
            for (int slot = 0; slot < chest.itemIds.Length; slot++)
            {
                int itemId = chest.itemIds[slot];
                int stack = chest.itemStacks[slot];

                if (itemId != validItemId || stack <= 0)
                    continue;

                // Check KeepOneItem setting - reserve 1 if enabled (AutoSorters exempt)
                bool keepOne = Plugin.KeepOneItem && !ConveyorHelper.IsAutoSorter(chest);
                int reserve = keepOne ? 1 : 0;
                int available = stack - reserve;
                if (available <= 0)
                    continue;

                // Calculate how many to transfer (min of: fill speed, available, room in silo)
                int toTransfer = Mathf.Min(Plugin.SiloFillSpeed, available, siloRoom);
                if (toTransfer <= 0)
                    continue;

                int remaining = stack - toTransfer;
                if (remaining <= 0)
                    ContainerManager.manage.changeSlotInChest(chest.xPos, chest.yPos, slot, -1, 0, null);
                else
                    ContainerManager.manage.changeSlotInChest(chest.xPos, chest.yPos, slot, itemId, remaining, null);

                ConveyorAnimator.ReserveTarget(siloX, siloY);

                // Show half as many bags, each carrying double
                int numBags = Mathf.Max(1, toTransfer / 2);
                int itemsPerBag = toTransfer / numBags;
                int remainder = toTransfer - (itemsPerBag * numBags);

                Vector2Int source = new Vector2Int(chest.xPos, chest.yPos);
                Vector2Int dest = new Vector2Int(siloX, siloY);
                List<Vector2Int> path = ConveyorPathfinder.FindPath(source, dest, null);
                float staggerInterval = 0.5f / Plugin.AnimationSpeed;

                for (int b = 0; b < numBags; b++)
                {
                    int capturedSiloX = siloX;
                    int capturedSiloY = siloY;
                    bool isLastBag = (b == numBags - 1);
                    int bagAmount = isLastBag ? itemsPerBag + remainder : itemsPerBag;
                    int capturedAmount = bagAmount;

                    System.Action depositBag = () =>
                    {
                        int newStatus = Mathf.Min(WorldManager.Instance.onTileStatusMap[capturedSiloX, capturedSiloY] + capturedAmount, 200);
                        WorldManager.Instance.onTileStatusMap[capturedSiloX, capturedSiloY] = newStatus;
                        NetworkMapSharer.Instance.RpcGiveOnTileStatus(newStatus, capturedSiloX, capturedSiloY);
                        if (isLastBag)
                            ConveyorAnimator.UnreserveTarget(capturedSiloX, capturedSiloY);
                    };

                    if (Plugin.AnimationEnabled && path != null && path.Count >= 2)
                    {
                        ConveyorAnimator.StartAnimation(itemId, bagAmount, path, depositBag, b * staggerInterval);
                    }
                    else
                    {
                        depositBag();
                    }
                }

                return true;
            }

            return false;
        }
    }

    // Handles auto-feeding fish ponds (critters) and bug terrariums (honey),
    // plus extracting roe and cocoons on day change.
    // Uses Manhattan distance 3 radius because fish ponds are 5x5 multi-tile
    // objects whose visual stone border extends beyond their onTileMap footprint.
    public static class FishPondHelper
    {
        private static readonly int[] dx = { -1, 0, 1, 0 };
        private static readonly int[] dy = { 0, -1, 0, 1 };
        private const int SearchRadius = 3;

        public static void TryFeedPondsAndTerrariums(Chest sourceChest, HouseDetails inside)
        {
            if (inside != null)
                return;

            HashSet<long> checkedPositions = new HashSet<long>();

            if (TryFeedInRadius(sourceChest, sourceChest.xPos, sourceChest.yPos, checkedPositions))
                return;

            TryFeedViaConveyorPath(sourceChest, checkedPositions);
        }

        private static bool TryFeedInRadius(Chest sourceChest, int centerX, int centerY, HashSet<long> checkedPositions)
        {
            int mapW = WorldManager.Instance.onTileMap.GetLength(0);
            int mapH = WorldManager.Instance.onTileMap.GetLength(1);

            for (int ox = -SearchRadius; ox <= SearchRadius; ox++)
            {
                for (int oy = -SearchRadius; oy <= SearchRadius; oy++)
                {
                    if (ox == 0 && oy == 0)
                        continue;

                    if (Mathf.Abs(ox) + Mathf.Abs(oy) > SearchRadius)
                        continue;

                    int checkX = centerX + ox;
                    int checkY = centerY + oy;

                    if (checkX < 0 || checkY < 0 || checkX >= mapW || checkY >= mapH)
                        continue;

                    long key = ((long)checkX << 32) | (uint)checkY;
                    if (checkedPositions.Contains(key))
                        continue;
                    checkedPositions.Add(key);

                    if (TryFeedAtPosition(sourceChest, checkX, checkY))
                        return true;
                }
            }
            return false;
        }

        private static void TryFeedViaConveyorPath(Chest sourceChest, HashSet<long> checkedPositions)
        {
            if (Plugin.ConveyorTileType < 0)
                return;

            int mapW = WorldManager.Instance.onTileMap.GetLength(0);
            int mapH = WorldManager.Instance.onTileMap.GetLength(1);
            HashSet<long> visitedConveyors = new HashSet<long>();
            Queue<(int x, int y)> queue = new Queue<(int x, int y)>();

            for (int i = 0; i < 4; i++)
            {
                int nx = sourceChest.xPos + dx[i];
                int ny = sourceChest.yPos + dy[i];
                if (nx < 0 || ny < 0) continue;

                if (ConveyorHelper.IsConveyorTile(nx, ny, null))
                {
                    long key = ((long)nx << 32) | (uint)ny;
                    if (visitedConveyors.Add(key))
                        queue.Enqueue((nx, ny));
                }
            }

            while (queue.Count > 0)
            {
                var (cx, cy) = queue.Dequeue();

                // Check radius around each conveyor tile
                for (int ox = -SearchRadius; ox <= SearchRadius; ox++)
                {
                    for (int oy = -SearchRadius; oy <= SearchRadius; oy++)
                    {
                        if (Mathf.Abs(ox) + Mathf.Abs(oy) > SearchRadius)
                            continue;

                        int adjX = cx + ox;
                        int adjY = cy + oy;
                        if (adjX < 0 || adjY < 0 || adjX >= mapW || adjY >= mapH)
                            continue;

                        long adjKey = ((long)adjX << 32) | (uint)adjY;
                        if (checkedPositions.Add(adjKey))
                        {
                            if (TryFeedAtPosition(sourceChest, adjX, adjY))
                                return;
                        }
                    }
                }

                for (int i = 0; i < 4; i++)
                {
                    int nx = cx + dx[i];
                    int ny = cy + dy[i];
                    if (nx < 0 || ny < 0) continue;

                    long key = ((long)nx << 32) | (uint)ny;
                    if (visitedConveyors.Contains(key)) continue;

                    if (ConveyorHelper.IsConveyorTile(nx, ny, null))
                    {
                        visitedConveyors.Add(key);
                        queue.Enqueue((nx, ny));
                    }
                }
            }
        }

        private static bool TryFeedAtPosition(Chest sourceChest, int checkX, int checkY)
        {
            int tileObjectId = WorldManager.Instance.onTileMap[checkX, checkY];

            int rootX = checkX;
            int rootY = checkY;

            if (tileObjectId < -1)
            {
                Vector2 rootPos = WorldManager.Instance.findMultiTileObjectPos(checkX, checkY);
                rootX = (int)rootPos.x;
                rootY = (int)rootPos.y;
                tileObjectId = WorldManager.Instance.onTileMap[rootX, rootY];
            }

            if (tileObjectId < 0)
                return false;

            TileObject tileObj = WorldManager.Instance.allObjects[tileObjectId];
            if (tileObj == null || tileObj.tileObjectChest == null)
                return false;

            ChestPlaceable chestPlaceable = tileObj.tileObjectChest;
            bool isFishPond = chestPlaceable.isFishPond;
            bool isTerrarium = chestPlaceable.isBugTerrarium;

            if (!isFishPond && !isTerrarium)
                return false;

            Chest pondChest = ContainerManager.manage.getChestForWindow(rootX, rootY, null);
            if (pondChest == null)
                return false;

            if (pondChest.itemIds[22] != -1)
                return false;

            if (ConveyorAnimator.IsTargetReserved(rootX, rootY))
                return false;

            return TryLoadFoodFromChest(sourceChest, rootX, rootY, isFishPond);
        }

        private static bool TryLoadFoodFromChest(Chest sourceChest, int pondX, int pondY, bool isFishPond)
        {
            int honeyId = ContainerManager.manage.fishPondManager.honeyItem.getItemId();

            for (int slot = 0; slot < sourceChest.itemIds.Length; slot++)
            {
                int itemId = sourceChest.itemIds[slot];
                int stack = sourceChest.itemStacks[slot];

                if (itemId < 0 || stack <= 0)
                    continue;

                bool isValidFood = false;

                if (isFishPond)
                {
                    // Fish ponds accept underwater creatures (critters)
                    InventoryItem item = Inventory.Instance.allItems[itemId];
                    if (item != null && (bool)item.underwaterCreature)
                        isValidFood = true;
                }
                else
                {
                    // Bug terrariums accept honey
                    if (itemId == honeyId)
                        isValidFood = true;
                }

                if (!isValidFood)
                    continue;

                // KeepOneItem only applies to stackable items (critters don't stack); AutoSorters exempt
                bool keepOne = Plugin.KeepOneItem && !ConveyorHelper.IsAutoSorter(sourceChest);
                if (keepOne && Inventory.Instance.allItems[itemId].checkIfStackable() && stack < 2)
                    continue;

                int remaining = stack - 1;
                if (remaining <= 0)
                    ContainerManager.manage.changeSlotInChest(sourceChest.xPos, sourceChest.yPos, slot, -1, 0, null);
                else
                    ContainerManager.manage.changeSlotInChest(sourceChest.xPos, sourceChest.yPos, slot, itemId, remaining, null);

                ConveyorAnimator.ReserveTarget(pondX, pondY);

                int capturedPondX = pondX;
                int capturedPondY = pondY;
                int capturedItemId = itemId;
                System.Action depositFood = () =>
                {
                    ContainerManager.manage.changeSlotInChest(capturedPondX, capturedPondY, 22, capturedItemId, 1, null);
                    ConveyorAnimator.UnreserveTarget(capturedPondX, capturedPondY);
                };

                ConveyorAnimator.AnimateTransfer(itemId, 1,
                    new Vector2Int(sourceChest.xPos, sourceChest.yPos),
                    new Vector2Int(pondX, pondY), null, depositFood);

                string typeName = isFishPond ? "fish pond" : "bug terrarium";
                Plugin.Log.LogInfo("Autom8er: Fed " + typeName + " at " + pondX + "," + pondY + ": " + Inventory.Instance.allItems[itemId].itemName);
                return true;
            }

            return false;
        }

        // Called on day change to extract roe/cocoons from slot 23 into adjacent chests
        public static int ProcessPondAndTerrariumOutput()
        {
            if (!NetworkMapSharer.Instance.isServer)
                return 0;

            HarvestHelper.ResetDayChangeAnimationStagger();

            HashSet<long> checkedPonds = new HashSet<long>();
            List<Chest> chestsCopy = new List<Chest>(ContainerManager.manage.activeChests);
            int extractedCount = 0;

            foreach (Chest chest in chestsCopy)
            {
                if (chest == null)
                    continue;

                // Skip special containers and input-only containers
                HouseDetails inside = null;
                if (chest.insideX != -1 && chest.insideY != -1)
                    inside = HouseManager.manage.getHouseInfoIfExists(chest.insideX, chest.insideY);

                // Ponds/terrariums are outdoor only
                if (inside != null)
                    continue;

                if (ConveyorHelper.IsSpecialContainer(chest.xPos, chest.yPos, null))
                    continue;

                if (ConveyorHelper.IsInputOnlyContainer(chest.xPos, chest.yPos, null))
                    continue;

                // Check radius around chest for ponds/terrariums
                int mapW = WorldManager.Instance.onTileMap.GetLength(0);
                int mapH = WorldManager.Instance.onTileMap.GetLength(1);
                for (int ox = -SearchRadius; ox <= SearchRadius; ox++)
                {
                    for (int oy = -SearchRadius; oy <= SearchRadius; oy++)
                    {
                        if (ox == 0 && oy == 0) continue;
                        if (Mathf.Abs(ox) + Mathf.Abs(oy) > SearchRadius) continue;

                        int adjX = chest.xPos + ox;
                        int adjY = chest.yPos + oy;
                        if (adjX < 0 || adjY < 0 || adjX >= mapW || adjY >= mapH) continue;

                        if (TryExtractOutputAtPosition(adjX, adjY, chest, checkedPonds))
                            extractedCount++;
                    }
                }

                // Also check via conveyor path
                extractedCount += ExtractOutputViaConveyorPath(chest, checkedPonds);
            }

            return extractedCount;
        }

        private static int ExtractOutputViaConveyorPath(Chest targetChest, HashSet<long> checkedPonds)
        {
            if (Plugin.ConveyorTileType < 0)
                return 0;

            int mapW = WorldManager.Instance.onTileMap.GetLength(0);
            int mapH = WorldManager.Instance.onTileMap.GetLength(1);
            HashSet<long> visitedConveyors = new HashSet<long>();
            Queue<(int x, int y)> queue = new Queue<(int x, int y)>();
            int extractedCount = 0;

            for (int i = 0; i < 4; i++)
            {
                int nx = targetChest.xPos + dx[i];
                int ny = targetChest.yPos + dy[i];
                if (nx < 0 || ny < 0) continue;

                if (ConveyorHelper.IsConveyorTile(nx, ny, null))
                {
                    long key = ((long)nx << 32) | (uint)ny;
                    if (visitedConveyors.Add(key))
                        queue.Enqueue((nx, ny));
                }
            }

            while (queue.Count > 0)
            {
                var (cx, cy) = queue.Dequeue();

                for (int ox = -SearchRadius; ox <= SearchRadius; ox++)
                {
                    for (int oy = -SearchRadius; oy <= SearchRadius; oy++)
                    {
                        if (Mathf.Abs(ox) + Mathf.Abs(oy) > SearchRadius) continue;

                        int adjX = cx + ox;
                        int adjY = cy + oy;
                        if (adjX < 0 || adjY < 0 || adjX >= mapW || adjY >= mapH) continue;

                        if (TryExtractOutputAtPosition(adjX, adjY, targetChest, checkedPonds))
                            extractedCount++;
                    }
                }

                for (int i = 0; i < 4; i++)
                {
                    int nx = cx + dx[i];
                    int ny = cy + dy[i];
                    if (nx < 0 || ny < 0) continue;

                    long key = ((long)nx << 32) | (uint)ny;
                    if (visitedConveyors.Contains(key)) continue;

                    if (ConveyorHelper.IsConveyorTile(nx, ny, null))
                    {
                        visitedConveyors.Add(key);
                        queue.Enqueue((nx, ny));
                    }
                }
            }

            return extractedCount;
        }

        private static bool TryExtractOutputAtPosition(int checkX, int checkY, Chest targetChest, HashSet<long> checkedPonds)
        {
            int tileObjectId = WorldManager.Instance.onTileMap[checkX, checkY];

            // Handle multi-tile
            int rootX = checkX;
            int rootY = checkY;

            if (tileObjectId < -1)
            {
                Vector2 rootPos = WorldManager.Instance.findMultiTileObjectPos(checkX, checkY);
                rootX = (int)rootPos.x;
                rootY = (int)rootPos.y;
                tileObjectId = WorldManager.Instance.onTileMap[rootX, rootY];
            }

            if (tileObjectId < 0)
                return false;

            // Avoid double-processing the same pond
            long pondKey = ((long)rootX << 32) | (uint)rootY;
            if (checkedPonds.Contains(pondKey))
                return false;
            checkedPonds.Add(pondKey);

            TileObject tileObj = WorldManager.Instance.allObjects[tileObjectId];
            if (tileObj == null || tileObj.tileObjectChest == null)
                return false;

            ChestPlaceable chestPlaceable = tileObj.tileObjectChest;
            bool isFishPond = chestPlaceable.isFishPond;
            bool isTerrarium = chestPlaceable.isBugTerrarium;

            if (!isFishPond && !isTerrarium)
                return false;

            Chest pondChest = ContainerManager.manage.getChestForWindow(rootX, rootY, null);
            if (pondChest == null)
                return false;

            // Check if output slot 23 has anything
            int outputId = pondChest.itemIds[23];
            int outputStack = pondChest.itemStacks[23];

            if (outputId < 0 || outputStack <= 0)
                return false;

            // Determine how much to extract
            int extractAmount = outputStack;

            if (Plugin.HoldOutputForBreeding)
            {
                // Count creatures in slots 0-4
                int creatureCount = 0;
                for (int s = 0; s < 5; s++)
                {
                    if (pondChest.itemIds[s] != -1)
                        creatureCount++;
                }

                if (creatureCount < 5)
                {
                    // Hold roe/cocoons for breeding
                    int holdAmount = isFishPond ? 15 : 10;
                    extractAmount = outputStack - holdAmount;

                    if (extractAmount <= 0)
                        return false; // Not enough to extract yet
                }
                // If full (5 creatures), extract everything
            }

            ConveyorHelper.OutputDestination depositDestination;
            if (!ConveyorHelper.TryFindBestOutputDestinationFromNetworkAnchor(targetChest, null, outputId, includeAnchorChest: true, excludeVacuumCrates: false, out depositDestination))
                return false;

            // Remove from pond slot 23 immediately
            int remaining = outputStack - extractAmount;
            if (remaining <= 0)
            {
                ContainerManager.manage.changeSlotInChest(rootX, rootY, 23, -1, 0, null);
            }
            else
            {
                ContainerManager.manage.changeSlotInChest(rootX, rootY, 23, outputId, remaining, null);
            }

            // Animate output traveling to chest, deposit on arrival
            int capturedOutputId = outputId;
            int capturedExtract = extractAmount;
            Chest capturedChest = depositDestination.chest;
            int capturedRouteX = depositDestination.routeX;
            int capturedRouteY = depositDestination.routeY;
            System.Action depositOutput = () =>
            {
                if (!HarvestHelper.TryDepositHarvestToChest(capturedChest, null, capturedOutputId, capturedExtract,
                    () => AutomationCreditHelper.TryGrantOutputCredit(tileObjectId, capturedOutputId, capturedExtract)))
                {
                    // Chest full on arrival — try any reachable chest
                    ConveyorHelper.FallbackDepositToAnyChest(capturedChest.xPos, capturedChest.yPos, null, capturedOutputId, capturedExtract,
                        () => AutomationCreditHelper.TryGrantOutputCredit(tileObjectId, capturedOutputId, capturedExtract));
                }
            };

            ConveyorAnimator.AnimateTransfer(outputId, extractAmount,
                new Vector2Int(rootX, rootY),
                new Vector2Int(capturedRouteX, capturedRouteY), null, depositOutput,
                HarvestHelper.GetNextDayChangeAnimationDelay(capturedChest));

            string typeName = isFishPond ? "fish pond" : "bug terrarium";
            string itemName = Inventory.Instance.allItems[outputId].itemName;
            Plugin.Log.LogInfo("Autom8er: Extracted " + extractAmount + "x " + itemName + " from " + typeName + " at " + rootX + "," + rootY);
            return true;
        }
    }
}
