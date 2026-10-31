using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Mirror;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace Autom8er
{
    [BepInPlugin("topmass.autom8er", "Autom8er", "1.8.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        private const bool VerboseItemLogging = false;
        private Harmony harmony;
        private float scanTimer = 0f;
        private bool conveyorTileLookupFailed = false;

        // One cursor per batch list: sharing a cursor across two lists makes the modulo
        // position leak between them and can permanently starve specific crates.
        private int nextFarmerCrateIndex = 0;
        private int nextLoggingCrateIndex = 0;
        private int nextVacuumCrateIndex = 0;
        private int nextIdleVacuumCrateIndex = 0;
        private int nextInteractingFilterCrateIndex = 0;
        private int nextFilterCrateIndex = 0;
        private int nextIdleFilterCrateIndex = 0;

        // Config
        private ConfigEntry<int> configConveyorTileItemId;
        private ConfigEntry<bool> configKeepOneItem;
        private ConfigEntry<bool> configKeepOneWhiteCratesOnly;
        private ConfigEntry<float> configScanInterval;
        private ConfigEntry<int> configSiloFillSpeed;
        private ConfigEntry<bool> configAutoFeedPonds;
        private ConfigEntry<bool> configHoldOutputForBreeding;
        private ConfigEntry<bool> configAnimationEnabled;
        private ConfigEntry<float> configAnimationSpeed;
        private ConfigEntry<bool> configStackableCritters;
        private ConfigEntry<bool> configInfiniteToolRebuy;
        private ConfigEntry<bool> configInfiniteFranklinOrdering;
        private ConfigEntry<bool> configFreeChestCratePainting;
        private ConfigEntry<int> configAutoFarmerRangeTiles;

        // Default conveyor tile (Black Marble Path)
        public const int DEFAULT_CONVEYOR_TILE_ITEM_ID = 1747;

        // TileObject IDs for INPUT ONLY containers (crate only)
        public const int WHITE_CRATE_TILE_ID = 417;
        public const int WHITE_CHEST_TILE_ID = 430;

        // TileObject IDs for filter crate / standard chest color variants
        public const int BLACK_CRATE_TILE_ID = 410;
        public const int BLACK_CHEST_TILE_ID = 421;

        // TileObject IDs for colored crate-only feature blocks
        public const int GREEN_CRATE_TILE_ID = 412;
        public const int BLAST_FURNACE_TILE_ID = 581;
        public const int MAX_AUTO_FARMER_RANGE_TILES = 10;
        public static int AUTO_FARMER_RANGE_TILES = MAX_AUTO_FARMER_RANGE_TILES;

        // Item IDs
        public const int HAR_VAC_ITEM_ID = 1728;
        public const int TROPICAL_GRASS_TURF_ROLL_ITEM_ID = 1445;
        public const int WATER_TANK_ITEM_ID = 692;
        public const int BEE_ITEM_ID = 227;
        public const int QUEEN_BEE_ITEM_ID = 228;

        // Cached at runtime from item data
        public static int ConveyorTileType = -1;
        public static int ConveyorTileItemId = DEFAULT_CONVEYOR_TILE_ITEM_ID;
        public static bool KeepOneItem = false;
        public static bool KeepOneWhiteCratesOnly = false;
        public static float ScanInterval = 0.3f;
        public static int SiloFillSpeed = 10;
        public static bool AutoFeedPonds = true;
        public static bool HoldOutputForBreeding = true;
        public static bool AnimationEnabled = true;
        public static float AnimationSpeed = 2f;
        public static bool StackableCritters = true;
        public static bool InfiniteToolRebuy = false;
        public static bool InfiniteFranklinOrdering = true;
        public static bool FreeChestCratePainting = true;
        internal const int ACTIVE_FARMER_CRATES_PER_STEP = 4;
        internal const int FARMER_ACTIONS_PER_STEP = 2;
        internal const float FARMER_STEP_DELAY_SECONDS = 0.1f;
        internal const float DAY_CHANGE_FINAL_COLLECTION_SETTLE_SECONDS = 0.12f;
        private const float VACUUM_ACTIVE_SCAN_INTERVAL = 0.45f;
        private const float VACUUM_IDLE_SCAN_INTERVAL = 10f;
        private const float FILTER_ACTIVE_SCAN_INTERVAL = 0.45f;
        private const float FILTER_IDLE_SCAN_INTERVAL = 8f;
        private const int MAX_ACTIVE_FARMER_CRATES_PER_PASS = ACTIVE_FARMER_CRATES_PER_STEP;
        private const int MAX_ACTIVE_VACUUM_CRATES_PER_PASS = 4;
        private const int MAX_IDLE_VACUUM_CRATES_PER_PASS = 1;
        private const int MAX_INTERACTING_FILTER_CRATES_PER_PASS = 2;
        private const int MAX_ACTIVE_FILTER_CRATES_PER_PASS = 8;
        private const int MAX_IDLE_FILTER_CRATES_PER_PASS = 2;
        private static bool loadCatchUpPending = false;
        private static bool loadCatchUpCompletedForCurrentLoad = false;
        private static float loadCatchUpNextAttemptAt = 0f;
        private static int loadCatchUpAttempts = 0;
        private static readonly Dictionary<string, float> throttledLogUntilByKey = new Dictionary<string, float>();
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

        // Conditional strips every call site - including its string concatenation, which
        // otherwise allocates on every transfer even with logging off - from the build
        // unless AUTOM8ER_VERBOSE_LOG is defined. To get verbose logs: add the symbol to
        // <DefineConstants> in the csproj and set VerboseItemLogging = true.
        [System.Diagnostics.Conditional("AUTOM8ER_VERBOSE_LOG")]
        internal static void LogVerbose(string message)
        {
            if (VerboseItemLogging && Log != null)
                Log.LogInfo(message);
        }

        internal static void LogWarningThrottled(string key, string message, float throttleSeconds = 5f)
        {
            if (Log == null || string.IsNullOrEmpty(key))
                return;

            float now = Time.realtimeSinceStartup;
            if (throttledLogUntilByKey.TryGetValue(key, out float nextAllowedAt) && now < nextAllowedAt)
                return;

            throttledLogUntilByKey[key] = now + Mathf.Max(0.1f, throttleSeconds);
            Log.LogWarning(message);
        }

        internal static bool CanRunServerAutomation()
        {
            // While the host is underground/off-island the game redirects changeSlotInChest
            // to nonSavedChests: surface chest writes silently no-op server-side but still
            // RPC clients, causing free items and guest desync. Pause automation instead.
            return NetworkServer.active &&
                NetworkMapSharer.Instance != null &&
                NetworkMapSharer.Instance.isServer &&
                RealWorldTimeLight.time != null &&
                !RealWorldTimeLight.time.underGround &&
                !RealWorldTimeLight.time.offIsland;
        }

        // SaveAndLoad saves drops and machines on its first frame, writes the map over
        // later frames, then saves chests. Automation that moves items in that window
        // splits one item across two snapshots, so it waits until the save ends.
        internal static bool IsSaving
        {
            get { return SaveLoad.saveOrLoad != null && SaveLoad.saveOrLoad.isSaving; }
        }

        internal static bool IsDurabilityTransferItem(int itemId)
        {
            if (itemId < 0 || Inventory.Instance == null || itemId >= Inventory.Instance.allItems.Length)
                return false;

            InventoryItem item = Inventory.Instance.allItems[itemId];
            return item != null && item.hasFuel && !item.checkIfStackable();
        }
        // Hive bees are the only insects Autom8er collects, so only they stack and go into chests by hand.
        internal static bool IsStackedHiveBee(int itemId)
        {
            if (!StackableCritters || (itemId != BEE_ITEM_ID && itemId != QUEEN_BEE_ITEM_ID) ||
                Inventory.Instance == null || itemId >= Inventory.Instance.allItems.Length)
                return false;

            InventoryItem item = Inventory.Instance.allItems[itemId];
            return item != null && (bool)item.bug;
        }

        // Items this mod made stackable. Vanilla code that expects one per slot must not clear a stack of them.
        internal static bool IsModStackedCritter(int itemId)
        {
            if (!StackableCritters || itemId < 0 || Inventory.Instance == null || itemId >= Inventory.Instance.allItems.Length)
                return false;

            InventoryItem item = Inventory.Instance.allItems[itemId];
            return item != null && ((bool)item.underwaterCreature || IsStackedHiveBee(itemId));
        }

        internal static bool KeepOneAppliesTo(Chest sourceChest, HouseDetails inside)
        {
            if (!KeepOneItem || sourceChest == null ||
                ConveyorHelper.IsAutoSorter(sourceChest) ||
                ConveyorHelper.IsVacuumCrate(sourceChest.xPos, sourceChest.yPos, inside) ||
                VacuumCrateHelper.IsFarmerCrate(sourceChest) ||
                ConveyorHelper.IsFilterCrate(sourceChest.xPos, sourceChest.yPos, inside))
                return false;

            return !KeepOneWhiteCratesOnly || ConveyorHelper.IsInputOnlyContainer(sourceChest.xPos, sourceChest.yPos, inside);
        }

        internal static int GetTransferableAmountFromChest(Chest sourceChest, int slot, int itemId, bool allowKeepOne, HouseDetails inside)
        {
            if (sourceChest == null || slot < 0 || slot >= sourceChest.itemStacks.Length)
                return 0;

            int stackAmount = sourceChest.itemStacks[slot];
            if (stackAmount <= 0)
                return 0;

            if (Plugin.IsDurabilityTransferItem(itemId))
                return stackAmount;

            bool keepOne = allowKeepOne &&
                KeepOneAppliesTo(sourceChest, inside) &&
                Inventory.Instance.allItems[itemId].checkIfStackable();

            return keepOne ? stackAmount - 1 : stackAmount;
        }

        internal static void RemoveTransferredItemFromChest(Chest sourceChest, int slot, int itemId, int moveAmount, HouseDetails inside)
        {
            if (!CanRunServerAutomation() || sourceChest == null || slot < 0 || slot >= sourceChest.itemStacks.Length)
                return;

            if (Plugin.IsDurabilityTransferItem(itemId))
            {
                ContainerManager.manage.changeSlotInChest(sourceChest.xPos, sourceChest.yPos, slot, -1, 0, inside);
                return;
            }

            int remaining = sourceChest.itemStacks[slot] - moveAmount;
            if (remaining <= 0)
            {
                ContainerManager.manage.changeSlotInChest(sourceChest.xPos, sourceChest.yPos, slot, -1, 0, inside);
                return;
            }

            ContainerManager.manage.changeSlotInChest(sourceChest.xPos, sourceChest.yPos, slot, itemId, remaining, inside);
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

            configKeepOneWhiteCratesOnly = Config.Bind(
                "Automation",
                "KeepOneWhiteCratesOnly",
                false,
                "Only has an effect when KeepOneItem is true and restricts keep-one to white input crates only."
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
                "Makes all critters (underwater creatures) and hive bees (Bee, Queen Bee) stackable in inventory and chests.\n" +
                "Bees can also go back into normal chests by hand. Other insects and fish keep vanilla behavior.\n" +
                "Disable to keep vanilla critter and bee behavior (1 per slot)."
            );

            configInfiniteToolRebuy = Config.Bind(
                "Quality of Life",
                "InfiniteToolRebuy",
                false,
                "Allow real shop/tool displays to be bought repeatedly in the same day instead of marking sold out."
            );

            configInfiniteFranklinOrdering = Config.Bind(
                "Quality of Life",
                "InfiniteFranklinOrdering",
                true,
                "Allow Franklin crafting orders to be placed repeatedly in the same day while keeping normal mail delivery."
            );

            configFreeChestCratePainting = Config.Bind(
                "Quality of Life",
                "FreeChestCratePainting",
                true,
                "Painting chests and crates changes their colour without consuming the paint can. Vehicle painting is unchanged."
            );

            configAutoFarmerRangeTiles = Config.Bind(
                "Auto Farmer",
                "AutoFarmerRangeTiles",
                MAX_AUTO_FARMER_RANGE_TILES,
                "Green crate farm radius in tiles. Default 10 keeps the current 21 x 21 area. Range: 1 to 10."
            );

            ConveyorTileItemId = configConveyorTileItemId.Value;
            KeepOneItem = configKeepOneItem.Value;
            KeepOneWhiteCratesOnly = configKeepOneWhiteCratesOnly.Value;
            ScanInterval = Mathf.Clamp(configScanInterval.Value, 0.1f, 1.0f);
            SiloFillSpeed = Mathf.Clamp(configSiloFillSpeed.Value, 1, 20);
            AutoFeedPonds = configAutoFeedPonds.Value;
            HoldOutputForBreeding = configHoldOutputForBreeding.Value;
            AnimationEnabled = configAnimationEnabled.Value;
            AnimationSpeed = Mathf.Clamp(configAnimationSpeed.Value, 0.5f, 10f);
            StackableCritters = configStackableCritters.Value;
            InfiniteToolRebuy = configInfiniteToolRebuy.Value;
            InfiniteFranklinOrdering = configInfiniteFranklinOrdering.Value;
            FreeChestCratePainting = configFreeChestCratePainting.Value;
            AUTO_FARMER_RANGE_TILES = Mathf.Clamp(configAutoFarmerRangeTiles.Value, 1, MAX_AUTO_FARMER_RANGE_TILES);

            Log.LogInfo("Autom8er v1.8.0 loaded! ConveyorTile=" + ConveyorTileItemId + ", Scan=" + ScanInterval + "s, KeepOne=" + KeepOneItem + ", KeepOneWhiteCratesOnly=" + KeepOneWhiteCratesOnly + ", SiloSpeed=" + SiloFillSpeed + ", FeedPonds=" + AutoFeedPonds + ", BreedHold=" + HoldOutputForBreeding + ", Anim=" + AnimationEnabled + ", AnimSpeed=" + AnimationSpeed + ", StackCritters=" + StackableCritters + ", InfiniteToolRebuy=" + InfiniteToolRebuy + ", FranklinOrdering=" + InfiniteFranklinOrdering + ", FreeChestCratePainting=" + FreeChestCratePainting + ", FarmRadius=" + AUTO_FARMER_RANGE_TILES);

            harmony = new Harmony("topmass.autom8er");
            harmony.PatchAll();

            Log.LogInfo("Harmony patches applied.");
        }

        private void Update()
        {
            AutoFarmerRangeGuide.Update();
            ConveyorNetSync.ClientUpdate();

            // Item-data overrides are purely local and must apply on GUESTS too - otherwise
            // a modded client's stacking UI disagrees with the host's stacked chest data.
            if (!crittersPatched && Inventory.Instance != null)
            {
                ApplyStackabilityOverrides();
            }

            if (!NetworkServer.active)
                return;

            if (Inventory.Instance == null || WorldManager.Instance == null || ContainerManager.manage == null)
                return;

            // Cache the conveyor tile type once; a failed lookup is permanent for the
            // session (bad config value), so don't retry and re-warn every frame
            if (ConveyorTileType == -1 && !conveyorTileLookupFailed)
            {
                CacheConveyorTileType();
            }

            // While the host is underground/off-island the game redirects surface chest writes
            // to nonSavedChests (silent server-side no-ops that still RPC clients). Pause the
            // scan AND freeze in-flight animations; do NOT clear them - clearing executes their
            // deposit callbacks into the no-op path and destroys the items. Frozen transfers
            // resume and deposit correctly when the host returns to the surface.
            if (RealWorldTimeLight.time != null &&
                (RealWorldTimeLight.time.underGround || RealWorldTimeLight.time.offIsland))
            {
                return;
            }

            ConveyorNetSync.ServerUpdate();

            // The save patch flushes animations first; new scan transfers or arrivals
            // before the chest snapshot would lose or duplicate items.
            bool saving = IsSaving;
            if (!saving)
                ConveyorAnimator.UpdateAnimations();
            VacuumCrateHelper.UpdateVisuals();
            if (saving)
                return;

            RuntimeState.RunQueuedAfterSave();

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

            ConveyorHelper.ActivateAllOutdoorAutomationChests();

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
            NewDayHarvestPatch.TryStartPhasedHarvest(this, 0f, "Autom8er: Processing load catch-up harvests...", "Autom8er: Load catch-up harvest kickoff complete");
        }

        private void CacheConveyorTileType()
        {
            if (ConveyorTileItemId >= 0 && ConveyorTileItemId < Inventory.Instance.allItems.Length)
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
                    conveyorTileLookupFailed = true;
                }
            }
            else
            {
                Log.LogWarning("Autom8er: Invalid conveyor tile Item ID: " + ConveyorTileItemId);
                conveyorTileLookupFailed = true;
            }
        }

        private void ApplyStackabilityOverrides()
        {
            crittersPatched = true;
            int critterCount = 0;
            int beeCount = 0;
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

                if (IsStackedHiveBee(i) && !item.isStackable)
                {
                    item.isStackable = true;
                    beeCount++;
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

            if (beeCount > 0)
                Log.LogInfo("Autom8er: Made " + beeCount + " hive bees stackable");

            if (tropicalGrassPatched)
                Log.LogInfo("Autom8er: Made Tropical Grass Turf Roll stackable");
        }

        private void ProcessAllChests()
        {
            List<Chest> activeChests = ContainerManager.manage.activeChests;
            if (activeChests == null || activeChests.Count == 0)
                return;

            List<Chest> chests = new List<Chest>(activeChests);
            List<Chest> vacuumCrates = new List<Chest>();
            List<Chest> filterCrates = new List<Chest>();

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
                    if (inside == null)
                        filterCrates.Add(chest);
                    continue;
                }

                // Skip legacy output-only chests.
                if (ConveyorHelper.IsOutputOnlyContainer(chest.xPos, chest.yPos, inside))
                    continue;

                if (ConveyorHelper.IsVacuumCrate(chest.xPos, chest.yPos, inside))
                {
                    if (inside == null)
                        vacuumCrates.Add(chest);
                    continue;
                }

                if (ConveyorHelper.IsPondOrTerrarium(chest.xPos, chest.yPos, inside) ||
                    ConveyorHelper.IsDisplayContainer(chest.xPos, chest.yPos, inside))
                    continue;

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

            ProcessScheduledVacuumCrates(vacuumCrates);
            ProcessScheduledFilterCrates(filterCrates);
        }

        private void ProcessScheduledVacuumCrates(List<Chest> vacuumCrates)
        {
            if (vacuumCrates == null || vacuumCrates.Count == 0)
                return;

            List<Chest> activeFarmerCrates = new List<Chest>();
            List<Chest> activeLoggingCrates = new List<Chest>();
            List<Chest> activeVacuumCrates = new List<Chest>();
            List<Chest> idleVacuumCrates = new List<Chest>();

            for (int i = 0; i < vacuumCrates.Count; i++)
            {
                Chest chest = vacuumCrates[i];
                if (VacuumCrateHelper.IsFarmerCrate(chest))
                {
                    if (VacuumCrateHelper.ShouldUseActiveScanRate(chest))
                    {
                        if (VacuumCrateHelper.IsLoggingCrate(chest))
                            activeLoggingCrates.Add(chest);
                        else
                            activeFarmerCrates.Add(chest);
                    }
                    continue;
                }

                if (VacuumCrateHelper.ShouldUseActiveScanRate(chest))
                    activeVacuumCrates.Add(chest);
                else
                    idleVacuumCrates.Add(chest);
            }

            ProcessFarmerCrateBatch(activeFarmerCrates, ref nextFarmerCrateIndex, VACUUM_ACTIVE_SCAN_INTERVAL, MAX_ACTIVE_FARMER_CRATES_PER_PASS);
            ProcessFarmerCrateBatch(activeLoggingCrates, ref nextLoggingCrateIndex, VACUUM_ACTIVE_SCAN_INTERVAL, MAX_ACTIVE_FARMER_CRATES_PER_PASS);
            ProcessVacuumCrateBatch(activeVacuumCrates, ref nextVacuumCrateIndex, VACUUM_ACTIVE_SCAN_INTERVAL, MAX_ACTIVE_VACUUM_CRATES_PER_PASS);
            ProcessVacuumCrateBatch(idleVacuumCrates, ref nextIdleVacuumCrateIndex, VACUUM_IDLE_SCAN_INTERVAL, MAX_IDLE_VACUUM_CRATES_PER_PASS);
        }

        private void ProcessFarmerCrateBatch(List<Chest> chests, ref int cursor, float targetInterval, int maxPerPass)
        {
            int budget = CalculateRoundRobinBudget(chests, targetInterval, maxPerPass);
            int deferredCursor = -1;
            for (int i = 0; i < budget; i++)
            {
                int chestCursor = cursor;
                Chest chest = GetNextRoundRobinChest(chests, ref cursor);
                if (chest == null)
                    break;

                VacuumCrateHelper.TryProcessFarmTile(chest, null);
                // A crate the shared logging break budget turned away goes first next pass.
                if (deferredCursor < 0 && VacuumCrateHelper.LoggingBreakDeferred)
                    deferredCursor = chestCursor;
                VacuumCrateHelper.TryFlushStoredItemsToNetwork(chest, null);
            }

            if (deferredCursor >= 0)
                cursor = deferredCursor;
        }

        private void ProcessVacuumCrateBatch(List<Chest> chests, ref int cursor, float targetInterval, int maxPerPass)
        {
            int budget = CalculateRoundRobinBudget(chests, targetInterval, maxPerPass);
            for (int i = 0; i < budget; i++)
            {
                Chest chest = GetNextRoundRobinChest(chests, ref cursor);
                if (chest == null)
                    break;

                VacuumCrateHelper.TryVacuumNearbyDrops(chest, null);
                VacuumCrateHelper.TryProcessFarmTile(chest, null);
                VacuumCrateHelper.TryFlushStoredItemsToNetwork(chest, null);
            }
        }

        private void ProcessScheduledFilterCrates(List<Chest> filterCrates)
        {
            if (filterCrates == null || filterCrates.Count == 0)
                return;

            List<Chest> interactingFilterCrates = new List<Chest>();
            List<Chest> activeFilterCrates = new List<Chest>();
            List<Chest> idleFilterCrates = new List<Chest>();

            for (int i = 0; i < filterCrates.Count; i++)
            {
                Chest chest = filterCrates[i];
                if (FilterCrateHelper.IsPlayerInteracting(chest))
                    interactingFilterCrates.Add(chest);
                else if (FilterCrateHelper.ShouldUseActiveScanRate(chest))
                    activeFilterCrates.Add(chest);
                else
                    idleFilterCrates.Add(chest);
            }

            ProcessFilterCrateBatch(interactingFilterCrates, ref nextInteractingFilterCrateIndex, ScanInterval, MAX_INTERACTING_FILTER_CRATES_PER_PASS);
            ProcessFilterCrateBatch(activeFilterCrates, ref nextFilterCrateIndex, FILTER_ACTIVE_SCAN_INTERVAL, MAX_ACTIVE_FILTER_CRATES_PER_PASS);
            ProcessFilterCrateBatch(idleFilterCrates, ref nextIdleFilterCrateIndex, FILTER_IDLE_SCAN_INTERVAL, MAX_IDLE_FILTER_CRATES_PER_PASS);
        }

        private void ProcessFilterCrateBatch(List<Chest> chests, ref int cursor, float targetInterval, int maxPerPass)
        {
            int budget = CalculateRoundRobinBudget(chests, targetInterval, maxPerPass);
            for (int i = 0; i < budget; i++)
            {
                Chest chest = GetNextRoundRobinChest(chests, ref cursor);
                if (chest == null)
                    break;

                FilterCrateHelper.TryPullFilteredItemsFromNetwork(chest, null);
            }
        }

        private int CalculateRoundRobinBudget(List<Chest> chests, float targetInterval, int maxPerPass)
        {
            if (chests == null || chests.Count == 0)
                return 0;

            float clampedTargetInterval = Mathf.Max(targetInterval, ScanInterval);
            int budget = Mathf.CeilToInt((chests.Count * ScanInterval) / clampedTargetInterval);
            return Mathf.Clamp(budget, 1, Mathf.Min(chests.Count, Mathf.Max(1, maxPerPass)));
        }

        private Chest GetNextRoundRobinChest(List<Chest> chests, ref int cursor)
        {
            if (chests == null || chests.Count == 0)
                return null;

            if (cursor < 0)
                cursor = 0;

            int index = cursor % chests.Count;
            cursor = (index + 1) % chests.Count;
            return chests[index];
        }

        private void OnDestroy()
        {
            AutoFarmerRangeGuide.ClearAll();
            ConveyorAnimator.ClearAllAnimations();
            VacuumCrateHelper.ClearVisuals();
            harmony?.UnpatchSelf();
        }
    }
}
