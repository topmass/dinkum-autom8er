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
    [BepInPlugin("topmass.autom8er", "Autom8er", "1.5.0")]
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

        // Default conveyor tile (Black Marble Path)
        public const int DEFAULT_CONVEYOR_TILE_ITEM_ID = 1747;

        // TileObject IDs for INPUT ONLY containers (won't receive machine outputs)
        public const int WHITE_CRATE_TILE_ID = 417;
        public const int WHITE_CHEST_TILE_ID = 430;

        // TileObject IDs for OUTPUT ONLY containers (won't feed machines)
        public const int BLACK_CRATE_TILE_ID = 410;
        public const int BLACK_CHEST_TILE_ID = 421;

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

            ConveyorTileItemId = configConveyorTileItemId.Value;
            KeepOneItem = configKeepOneItem.Value;
            ScanInterval = Mathf.Clamp(configScanInterval.Value, 0.1f, 1.0f);
            SiloFillSpeed = Mathf.Clamp(configSiloFillSpeed.Value, 1, 20);
            AutoFeedPonds = configAutoFeedPonds.Value;
            HoldOutputForBreeding = configHoldOutputForBreeding.Value;
            AnimationEnabled = configAnimationEnabled.Value;
            AnimationSpeed = Mathf.Clamp(configAnimationSpeed.Value, 0.5f, 10f);

            Log.LogInfo("Autom8er v1.5.0 loaded! ConveyorTile=" + ConveyorTileItemId + ", Scan=" + ScanInterval + "s, KeepOne=" + KeepOneItem + ", SiloSpeed=" + SiloFillSpeed + ", FeedPonds=" + AutoFeedPonds + ", BreedHold=" + HoldOutputForBreeding + ", Anim=" + AnimationEnabled + ", AnimSpeed=" + AnimationSpeed);

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

            ConveyorAnimator.UpdateAnimations();

            scanTimer += Time.deltaTime;
            if (scanTimer >= ScanInterval)
            {
                scanTimer = 0f;
                ProcessAllChests();
                // Note: Harvest processing (bee houses, key cutters, worm farms, crab pots)
                // is triggered by NewDayHarvestPatch after day change, not continuously
            }
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

        private void ProcessAllChests()
        {
            List<Chest> chests = ContainerManager.manage.activeChests;
            if (chests == null || chests.Count == 0)
                return;

            foreach (Chest chest in chests)
            {
                if (chest == null)
                    continue;

                if (!ConveyorHelper.ChestHasItems(chest))
                    continue;

                HouseDetails inside = null;
                if (chest.insideX != -1 && chest.insideY != -1)
                {
                    inside = HouseManager.manage.getHouseInfoIfExists(chest.insideX, chest.insideY);
                }

                // Skip output-only containers (black crate/chest) — they receive but never feed
                if (ConveyorHelper.IsOutputOnlyContainer(chest.xPos, chest.yPos, inside))
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
            harmony?.UnpatchSelf();
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
            if (ConveyorHelper.TryDepositToAdjacentChest(xPos, yPos, inside, resultItemId, stackAmount))
            {
                Plugin.Log.LogInfo("Autom8er: Machine output -> " + Inventory.Instance.allItems[resultItemId].itemName);
                return false;
            }

            // Priority 2: Via Black Marble Path conveyor network
            if (ConveyorHelper.TryDepositViaConveyorPath(xPos, yPos, inside, resultItemId, stackAmount))
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

            if (__instance.isCrabPot)
            {
                // Crab pot drops
                int itemId = __instance.getCrabTrapDrop(xPos, yPos);
                if (itemId != -1)
                {
                    int slotCheck = ConveyorHelper.FindSlotForItem(targetChest, itemId);
                    if (slotCheck != -1)
                    {
                        harvestedAny = true;
                        int capturedItemId = itemId;
                        Chest capturedChest = targetChest;
                        HouseDetails capturedInside = inside;
                        System.Action depositHarvest = () =>
                        {
                            if (!HarvestHelper.TryDepositHarvestToChest(capturedChest, capturedInside, capturedItemId, 1))
                                ConveyorHelper.FallbackDepositToAnyChest(capturedChest.xPos, capturedChest.yPos, capturedInside, capturedItemId, 1);
                        };

                        ConveyorAnimator.AnimateTransfer(itemId, 1,
                            new Vector2Int(xPos, yPos),
                            new Vector2Int(targetChest.xPos, targetChest.yPos), inside, depositHarvest);

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
                            int slotCheck = ConveyorHelper.FindSlotForItem(targetChest, itemId);
                            if (slotCheck != -1)
                            {
                                harvestedAny = true;
                                int capturedItemId = itemId;
                                Chest capturedChest = targetChest;
                                HouseDetails capturedInside = inside;
                                System.Action depositHarvest = () =>
                                {
                                    if (!HarvestHelper.TryDepositHarvestToChest(capturedChest, capturedInside, capturedItemId, 1))
                                        ConveyorHelper.FallbackDepositToAnyChest(capturedChest.xPos, capturedChest.yPos, capturedInside, capturedItemId, 1);
                                };

                                ConveyorAnimator.AnimateTransfer(itemId, 1,
                                    new Vector2Int(xPos, yPos),
                                    new Vector2Int(targetChest.xPos, targetChest.yPos), inside, depositHarvest);

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
                                int slotCheck = ConveyorHelper.FindSlotForItem(targetChest, itemId);
                                if (slotCheck != -1)
                                {
                                    harvestedAny = true;
                                    int capturedItemId = itemId;
                                    Chest capturedChest = targetChest;
                                    HouseDetails capturedInside = inside;
                                    System.Action depositHarvest = () =>
                                    {
                                        if (!HarvestHelper.TryDepositHarvestToChest(capturedChest, capturedInside, capturedItemId, 1))
                                            ConveyorHelper.FallbackDepositToAnyChest(capturedChest.xPos, capturedChest.yPos, capturedInside, capturedItemId, 1);
                                    };

                                    ConveyorAnimator.AnimateTransfer(itemId, 1,
                                        new Vector2Int(xPos, yPos),
                                        new Vector2Int(targetChest.xPos, targetChest.yPos), inside, depositHarvest);

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
        }
    }

    // Patch to trigger harvest processing after day change completes
    // This is much more efficient than continuous scanning since growth machines
    // (bee houses, key cutters, worm farms, crab pots) only update on day change
    [HarmonyPatch(typeof(WorldManager), "refreshAllChunksNewDay")]
    public static class NewDayHarvestPatch
    {
        // MoveNext is the actual coroutine method generated by the compiler
        static void Postfix()
        {
            // This runs when the coroutine starts, not when it finishes
            // We need to delay the harvest processing
            if (NetworkMapSharer.Instance.isServer)
            {
                // Use a coroutine to delay harvest processing until chunks are refreshed
                WorldManager.Instance.StartCoroutine(DelayedHarvestProcessing());
            }
        }

        private static System.Collections.IEnumerator DelayedHarvestProcessing()
        {
            // Wait a bit for chunk refreshes to complete
            yield return new UnityEngine.WaitForSeconds(2f);

            Plugin.Log.LogInfo("Autom8er: Processing day-change harvests...");
            HarvestHelper.ProcessHarvestableMachines();
            FishPondHelper.ProcessPondAndTerrariumOutput();
            Plugin.Log.LogInfo("Autom8er: Day-change harvest processing complete");
        }
    }

    public static class HarvestHelper
    {
        private static readonly int[] dx = { -1, 0, 1, 0 };
        private static readonly int[] dy = { 0, -1, 0, 1 };

        // Used to pass inside context from TryAutoHarvestAt to HarvestPatch
        public static HouseDetails CurrentHarvestInside = null;

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

            // BFS along conveyor tiles to find a chest
            int maxSteps = 50;
            while (queue.Count > 0 && maxSteps-- > 0)
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

        public static bool TryDepositHarvestToChest(Chest chest, HouseDetails inside, int itemId, int amount)
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

            return true;
        }

        // Scan for harvestable machines and auto-trigger harvest
        public static void ProcessHarvestableMachines()
        {
            if (!NetworkMapSharer.Instance.isServer)
                return;

            HashSet<long> checkedPositions = new HashSet<long>();

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
                            checkedPositions.Add(key);
                            TryAutoHarvestAt(checkX, checkY, inside);
                        }
                    }
                }

                // Also scan machines adjacent to conveyor tiles connected to this chest
                ScanConveyorPathForHarvestables(chest.xPos, chest.yPos, inside, checkedPositions);
            }
        }

        // Scan along conveyor path and check all adjacent tiles for harvestable machines
        private static void ScanConveyorPathForHarvestables(int startX, int startY, HouseDetails inside, HashSet<long> checkedPositions)
        {
            if (Plugin.ConveyorTileType < 0)
                return;

            // Conveyor paths not supported inside houses
            if (inside != null)
                return;

            HashSet<long> visitedConveyors = new HashSet<long>();
            Queue<(int x, int y)> queue = new Queue<(int x, int y)>();

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

            // BFS along conveyor tiles, checking tiles within 2-tile radius for harvestables
            // (2-tile radius needed for crab pots in water)
            int maxSteps = 100;
            while (queue.Count > 0 && maxSteps-- > 0)
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
                            checkedPositions.Add(machineKey);
                            TryAutoHarvestAt(checkX, checkY, null);
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
        }

        public static void TryAutoHarvestAt(int xPos, int yPos, HouseDetails inside)
        {
            int tileObjectId = inside != null
                ? inside.houseMapOnTile[xPos, yPos]
                : WorldManager.Instance.onTileMap[xPos, yPos];

            if (tileObjectId < 0)
                return;

            TileObject tileObj = WorldManager.Instance.allObjects[tileObjectId];
            if (tileObj == null || tileObj.tileObjectGrowthStages == null)
                return;

            TileObjectGrowthStages growth = tileObj.tileObjectGrowthStages;

            // NEVER auto-harvest incubators or anything that spawns farm animals
            if (growth.spawnsFarmAnimal)
                return;

            // Check if harvestable (not manually harvestable check - we want auto-harvest)
            int currentStatus = inside != null
                ? inside.houseMapOnTileStatus[xPos, yPos]
                : WorldManager.Instance.onTileStatusMap[xPos, yPos];

            // canBeHarvested checks if at final growth stage
            if (!growth.canBeHarvested(currentStatus))
                return;

            // Determine search radius - crab pots need 2-tile radius due to water placement
            int searchRadius = growth.isCrabPot ? 2 : 1;

            // IMPORTANT: Only harvest if we have a valid output chest
            // This prevents items from being lost when no chest is nearby
            Chest outputChest = FindAdjacentChestForHarvest(xPos, yPos, inside, searchRadius);
            if (outputChest == null)
                return;

            // Set the inside context for the HarvestPatch to use
            CurrentHarvestInside = inside;

            try
            {
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
            }
            finally
            {
                // Always clear the context
                CurrentHarvestInside = null;
            }
        }
    }

    public static class ConveyorHelper
    {
        private static readonly int[] dx = { -1, 0, 1, 0 };
        private static readonly int[] dy = { 0, -1, 0, 1 };

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

            // BFS to find all connected path tiles
            int maxPathTiles = 500;
            while (toExplore.Count > 0 && pathNetwork.Count < maxPathTiles)
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
                        }

                        Plugin.Log.LogInfo("Autom8er: Conveyor input -> Machine: " + (isFuelItem ? "1" : amountNeeded.ToString()) + "x " + item.itemName);
                        return true;
                    }
                }
            }

            return false;
        }

        public static bool TryDepositToAdjacentChest(int machineX, int machineY, HouseDetails inside, int itemId, int stackAmount)
        {
            for (int i = 0; i < 4; i++)
            {
                int checkX = machineX + dx[i];
                int checkY = machineY + dy[i];

                if (checkX < 0 || checkY < 0)
                    continue;

                // Skip input-only containers (white crate/chest)
                if (IsInputOnlyContainer(checkX, checkY, inside))
                    continue;

                Chest chest = FindChestAt(checkX, checkY, inside);
                if (chest == null)
                    continue;

                if (IsSpecialContainer(checkX, checkY, inside))
                    continue;

                int slotIndex = FindSlotForItem(chest, itemId);
                if (slotIndex == -1)
                    continue;

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

                return true;
            }

            return false;
        }

        public static bool TryDepositViaConveyorPath(int machineX, int machineY, HouseDetails inside, int itemId, int stackAmount)
        {
            if (Plugin.ConveyorTileType == -1)
                return false;

            // Find all adjacent conveyor tiles
            HashSet<Vector2Int> pathNetwork = new HashSet<Vector2Int>();
            Queue<Vector2Int> toExplore = new Queue<Vector2Int>();

            // Start BFS from tiles adjacent to the machine
            for (int i = 0; i < 4; i++)
            {
                int checkX = machineX + dx[i];
                int checkY = machineY + dy[i];

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

            // BFS to find all connected path tiles (limit to prevent infinite loops)
            int maxPathTiles = 500;
            while (toExplore.Count > 0 && pathNetwork.Count < maxPathTiles)
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

            // Find any chest touching the path network (excluding input-only and special containers)
            foreach (Vector2Int pathTile in pathNetwork)
            {
                for (int i = 0; i < 4; i++)
                {
                    int checkX = pathTile.x + dx[i];
                    int checkY = pathTile.y + dy[i];

                    if (checkX < 0 || checkY < 0)
                        continue;

                    // Skip if this is also a path tile (we want chests, not more path)
                    if (pathNetwork.Contains(new Vector2Int(checkX, checkY)))
                        continue;

                    // Skip if this is the original machine position
                    if (checkX == machineX && checkY == machineY)
                        continue;

                    // Skip input-only containers
                    if (IsInputOnlyContainer(checkX, checkY, inside))
                        continue;

                    Chest chest = FindChestAt(checkX, checkY, inside);
                    if (chest == null)
                        continue;

                    if (IsSpecialContainer(checkX, checkY, inside))
                        continue;

                    int slotIndex = FindSlotForItem(chest, itemId);
                    if (slotIndex == -1)
                        continue;

                    if (Plugin.AnimationEnabled)
                    {
                        Vector2Int machinePos = new Vector2Int(machineX, machineY);
                        Vector2Int chestPos = new Vector2Int(chest.xPos, chest.yPos);
                        List<Vector2Int> path = ConveyorPathfinder.FindPath(machinePos, chestPos, inside);

                        int capturedItemId = itemId;
                        int capturedStack = stackAmount;
                        int capturedChestX = chest.xPos;
                        int capturedChestY = chest.yPos;
                        HouseDetails capturedInside = inside;
                        Chest capturedChest = chest;

                        System.Action onArrival = () =>
                        {
                            int arrivalSlot = FindSlotForItem(capturedChest, capturedItemId);
                            if (arrivalSlot == -1)
                            {
                                if (!FallbackDepositToAnyChest(capturedChestX, capturedChestY, capturedInside, capturedItemId, capturedStack))
                                    Plugin.Log.LogWarning("Autom8er: Target chest full on arrival — no chest available, item lost!");
                                return;
                            }

                            bool stackable = Inventory.Instance.allItems[capturedItemId].checkIfStackable();
                            if (stackable && capturedChest.itemIds[arrivalSlot] == capturedItemId)
                            {
                                ContainerManager.manage.changeSlotInChest(
                                    capturedChestX, capturedChestY, arrivalSlot,
                                    capturedItemId, capturedChest.itemStacks[arrivalSlot] + capturedStack, capturedInside);
                            }
                            else
                            {
                                ContainerManager.manage.changeSlotInChest(
                                    capturedChestX, capturedChestY, arrivalSlot,
                                    capturedItemId, capturedStack, capturedInside);
                            }

                            if (IsAutoSorter(capturedChest))
                                QueueAutoSorterUpdate(capturedChest);
                        };

                        ConveyorAnimator.StartAnimation(itemId, stackAmount, path, onArrival);
                    }
                    else
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
                    }

                    return true;
                }
            }

            return false;
        }

        // Fallback for animation callbacks when the intended destination is unavailable on arrival
        public static bool FallbackDepositToAnyChest(int originX, int originY, HouseDetails inside, int itemId, int stackAmount)
        {
            // First try adjacent chests
            if (TryDepositToAdjacentChest(originX, originY, inside, itemId, stackAmount))
                return true;

            // Then try chests on the conveyor network (instant, no animation)
            if (Plugin.ConveyorTileType == -1)
                return false;

            HashSet<Vector2Int> pathNetwork = new HashSet<Vector2Int>();
            Queue<Vector2Int> toExplore = new Queue<Vector2Int>();

            for (int i = 0; i < 4; i++)
            {
                int checkX = originX + dx[i];
                int checkY = originY + dy[i];
                if (checkX < 0 || checkY < 0) continue;
                if (IsConveyorTile(checkX, checkY, inside))
                {
                    Vector2Int pos = new Vector2Int(checkX, checkY);
                    if (pathNetwork.Add(pos))
                        toExplore.Enqueue(pos);
                }
            }

            int maxPathTiles = 500;
            while (toExplore.Count > 0 && pathNetwork.Count < maxPathTiles)
            {
                Vector2Int current = toExplore.Dequeue();
                for (int i = 0; i < 4; i++)
                {
                    int nx = current.x + dx[i];
                    int ny = current.y + dy[i];
                    if (nx < 0 || ny < 0) continue;
                    Vector2Int nextPos = new Vector2Int(nx, ny);
                    if (pathNetwork.Contains(nextPos)) continue;
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
                    if (checkX < 0 || checkY < 0) continue;
                    if (pathNetwork.Contains(new Vector2Int(checkX, checkY))) continue;

                    Chest chest = FindChestAt(checkX, checkY, inside);
                    if (chest == null) continue;
                    if (IsSpecialContainer(checkX, checkY, inside)) continue;

                    int slotIndex = FindSlotForItem(chest, itemId);
                    if (slotIndex == -1) continue;

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

                    return true;
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

            return tileObjectId == Plugin.BLACK_CRATE_TILE_ID || tileObjectId == Plugin.BLACK_CHEST_TILE_ID;
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
            int maxTiles = 500;
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

    public static class ConveyorAnimator
    {
        private static List<ConveyorAnimation> activeAnimations = new List<ConveyorAnimation>();
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

        // Item travels 10% into the destination tile before vanishing
        private const float FINAL_TILE_FRACTION = 0.1f;

        public static void StartAnimation(int itemId, int stackAmount, List<Vector2Int> path, System.Action onArrival, float delay = 0f)
        {
            if (path == null || path.Count < 2)
            {
                if (onArrival != null)
                    onArrival();
                return;
            }

            GameObject visual = null;
            try
            {
                InventoryItem invItem = Inventory.Instance.allItems[itemId];
                if (invItem != null && invItem.itemPrefab != null)
                {
                    visual = GameObject.Instantiate(invItem.itemPrefab);
                    visual.transform.position = TileToWorld(path[0].x, path[0].y);
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

                    if (delay > 0f)
                        visual.SetActive(false);
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Autom8er: Failed to create animation visual for item " + itemId + ": " + e.Message);
            }

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

        public static void UpdateAnimations()
        {
            if (activeAnimations.Count == 0)
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
            reservedTargets.Clear();
        }

        public static bool HasActiveAnimations()
        {
            return activeAnimations.Count > 0;
        }

        public static void AnimateTransfer(int itemId, int amount, Vector2Int source, Vector2Int dest, HouseDetails inside, System.Action depositCallback)
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

            StartAnimation(itemId, amount, path, depositCallback);
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
            int maxSteps = 100;
            while (queue.Count > 0 && maxSteps-- > 0)
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

            int maxSteps = 100;
            while (queue.Count > 0 && maxSteps-- > 0)
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
            int maxSteps = 100;
            while (queue.Count > 0 && maxSteps-- > 0)
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

            int maxSteps = 100;
            while (queue.Count > 0 && maxSteps-- > 0)
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
        public static void ProcessPondAndTerrariumOutput()
        {
            if (!NetworkMapSharer.Instance.isServer)
                return;

            HashSet<long> checkedPonds = new HashSet<long>();
            List<Chest> chestsCopy = new List<Chest>(ContainerManager.manage.activeChests);

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

                        TryExtractOutputAtPosition(adjX, adjY, chest, checkedPonds);
                    }
                }

                // Also check via conveyor path
                ExtractOutputViaConveyorPath(chest, checkedPonds);
            }
        }

        private static void ExtractOutputViaConveyorPath(Chest targetChest, HashSet<long> checkedPonds)
        {
            if (Plugin.ConveyorTileType < 0)
                return;

            int mapW = WorldManager.Instance.onTileMap.GetLength(0);
            int mapH = WorldManager.Instance.onTileMap.GetLength(1);
            HashSet<long> visitedConveyors = new HashSet<long>();
            Queue<(int x, int y)> queue = new Queue<(int x, int y)>();

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

            int maxSteps = 100;
            while (queue.Count > 0 && maxSteps-- > 0)
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

                        TryExtractOutputAtPosition(adjX, adjY, targetChest, checkedPonds);
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

        private static void TryExtractOutputAtPosition(int checkX, int checkY, Chest targetChest, HashSet<long> checkedPonds)
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
                return;

            // Avoid double-processing the same pond
            long pondKey = ((long)rootX << 32) | (uint)rootY;
            if (checkedPonds.Contains(pondKey))
                return;
            checkedPonds.Add(pondKey);

            TileObject tileObj = WorldManager.Instance.allObjects[tileObjectId];
            if (tileObj == null || tileObj.tileObjectChest == null)
                return;

            ChestPlaceable chestPlaceable = tileObj.tileObjectChest;
            bool isFishPond = chestPlaceable.isFishPond;
            bool isTerrarium = chestPlaceable.isBugTerrarium;

            if (!isFishPond && !isTerrarium)
                return;

            Chest pondChest = ContainerManager.manage.getChestForWindow(rootX, rootY, null);
            if (pondChest == null)
                return;

            // Check if output slot 23 has anything
            int outputId = pondChest.itemIds[23];
            int outputStack = pondChest.itemStacks[23];

            if (outputId < 0 || outputStack <= 0)
                return;

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
                        return; // Not enough to extract yet
                }
                // If full (5 creatures), extract everything
            }

            // Check if chest has room before removing from pond
            int depositSlot = ConveyorHelper.FindSlotForItem(targetChest, outputId);
            if (depositSlot == -1)
                return;

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
            Chest capturedChest = targetChest;
            System.Action depositOutput = () =>
            {
                if (!HarvestHelper.TryDepositHarvestToChest(capturedChest, null, capturedOutputId, capturedExtract))
                {
                    // Chest full on arrival — try any reachable chest
                    ConveyorHelper.FallbackDepositToAnyChest(capturedChest.xPos, capturedChest.yPos, null, capturedOutputId, capturedExtract);
                }
            };

            ConveyorAnimator.AnimateTransfer(outputId, extractAmount,
                new Vector2Int(rootX, rootY),
                new Vector2Int(targetChest.xPos, targetChest.yPos), null, depositOutput);

            string typeName = isFishPond ? "fish pond" : "bug terrarium";
            string itemName = Inventory.Instance.allItems[outputId].itemName;
            Plugin.Log.LogInfo("Autom8er: Extracted " + extractAmount + "x " + itemName + " from " + typeName + " at " + rootX + "," + rootY);
        }
    }
}
