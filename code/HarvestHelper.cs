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

    public static class HarvestHelper
    {
        private static readonly int[] dx = { -1, 0, 1, 0 };
        private static readonly int[] dy = { 0, -1, 0, 1 };
        private const int DAY_CHANGE_ANIMATION_BATCH_SIZE = 100;
        private const float DAY_CHANGE_ANIMATION_BATCH_DELAY = 0.2f;
        private static Dictionary<long, int> dayChangeAnimationCounts = new Dictionary<long, int>();
        private static bool dayChangeMachineHarvestRunning = false;

        // Used to pass inside context from TryAutoHarvestAt to HarvestPatch
        public static HouseDetails CurrentHarvestInside = null;
        public static Chest CurrentHarvestForcedChest = null;

        // Forced routing for RPC-deferred harvests: RpcHarvestObject is a Mirror send-stub,
        // so harvest() runs after the caller's stack (and its finally) has unwound - a
        // transient static can't carry the owning crate across that gap. Keyed by tile.
        private class ForcedChestEntry
        {
            public Chest chest;
            public float expireAt;
        }
        private static readonly Dictionary<long, ForcedChestEntry> forcedHarvestChests = new Dictionary<long, ForcedChestEntry>();

        public static void SetForcedHarvestChestAt(int xPos, int yPos, Chest chest)
        {
            forcedHarvestChests[((long)xPos << 32) | (uint)yPos] = new ForcedChestEntry
            {
                chest = chest,
                expireAt = Time.realtimeSinceStartup + 2f
            };
        }

        public static Chest ConsumeForcedHarvestChestAt(int xPos, int yPos)
        {
            long key = ((long)xPos << 32) | (uint)yPos;
            ForcedChestEntry entry;
            if (!forcedHarvestChests.TryGetValue(key, out entry))
                return null;

            forcedHarvestChests.Remove(key);
            return Time.realtimeSinceStartup <= entry.expireAt ? entry.chest : null;
        }

        public static void ClearForcedHarvestChests()
        {
            forcedHarvestChests.Clear();
        }

        public static void ResetDayChangeAnimationStagger()
        {
            dayChangeAnimationCounts.Clear();
        }

        // Save/load runtime-state reset (spec rule 12): stagger counts keyed by the old
        // world's chests, forced-chest handoffs, and the single-flight harvest flag.
        public static void ResetRuntimeState()
        {
            dayChangeAnimationCounts.Clear();
            dayChangeMachineHarvestRunning = false;
            ClearForcedHarvestChests();
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
                        if (chest != null &&
                            !ConveyorHelper.IsSpecialContainer(checkX, checkY, inside) &&
                            !ConveyorHelper.IsFilterCrate(checkX, checkY, inside) &&
                            !ConveyorHelper.IsVacuumCrate(checkX, checkY, inside))
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
            ConveyorHelper.TryFindBestHarvestOutputDestination(machineX, machineY, inside, itemId, searchRadius, out destination);
            return destination;
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
                    if (chest != null &&
                        !ConveyorHelper.IsSpecialContainer(adjX, adjY, null) &&
                        !ConveyorHelper.IsInputOnlyContainer(adjX, adjY, null) &&
                        !ConveyorHelper.IsFilterCrate(adjX, adjY, null) &&
                        !ConveyorHelper.IsVacuumCrate(adjX, adjY, null))
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
            if (!Plugin.CanRunServerAutomation() ||
                chest == null ||
                itemId < 0 ||
                Inventory.Instance == null ||
                ContainerManager.manage == null)
            {
                return false;
            }

            // A captured chest may have been picked up/replaced during a long animation;
            // changeSlotInChest would then silently no-op while we report success, granting
            // credit and skipping fallbacks for a deposit that never happened.
            if (ContainerManager.manage.getChestForWindow(chest.xPos, chest.yPos, inside) != chest)
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

        // Scan for harvestable machines and auto-trigger harvest.
        // This runs in batches so large hive/key-cutter arrays do not spike the frame.
        public static System.Collections.IEnumerator ProcessHarvestableMachines()
        {
            if (!NetworkMapSharer.Instance.isServer || dayChangeMachineHarvestRunning)
                yield break;

            dayChangeMachineHarvestRunning = true;

            try
            {
                ResetDayChangeAnimationStagger();

                HashSet<long> checkedPositions = new HashSet<long>();

                List<Chest> chestsCopy = ConveyorHelper.CollectOutdoorAutomationRootChestsShared();

                foreach (Chest chest in chestsCopy)
                {
                    if (chest == null)
                        continue;

                    HouseDetails inside = null;
                    if (chest.insideX != -1 && chest.insideY != -1)
                    {
                        inside = HouseManager.manage.getHouseInfoIfExists(chest.insideX, chest.insideY);
                    }

                    if (!ConveyorHelper.IsGenericAutomationRootContainer(chest.xPos, chest.yPos, inside))
                        continue;

                    for (int offsetX = -2; offsetX <= 2; offsetX++)
                    {
                        for (int offsetY = -2; offsetY <= 2; offsetY++)
                        {
                            if (offsetX == 0 && offsetY == 0)
                                continue;

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
                                ScanConnectedHarvestArray(checkX, checkY, inside, checkedPositions);
                            }
                        }
                    }

                    ScanConveyorPathForHarvestables(chest.xPos, chest.yPos, inside, checkedPositions);
                }
            }
            finally
            {
                dayChangeMachineHarvestRunning = false;
            }

            yield break;
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
                            ScanConnectedHarvestArray(checkX, checkY, null, checkedPositions);
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

        private static void ScanConnectedHarvestArray(int seedX, int seedY, HouseDetails inside, HashSet<long> checkedPositions)
        {
            int mapW = inside != null ? inside.houseMapOnTile.GetLength(0) : WorldManager.Instance.onTileMap.GetLength(0);
            int mapH = inside != null ? inside.houseMapOnTile.GetLength(1) : WorldManager.Instance.onTileMap.GetLength(1);

            int rootX;
            int rootY;
            int tileObjectId;
            if (!TryGetHarvestArrayRoot(seedX, seedY, inside, out rootX, out rootY, out tileObjectId))
                return;

            Queue<(int x, int y)> queue = new Queue<(int x, int y)>();
            HashSet<long> queuedRoots = new HashSet<long>();

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
                TryAutoHarvestAt(currentX, currentY, inside);

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
        }

        private static bool TryGetHarvestArrayRoot(int xPos, int yPos, HouseDetails inside, out int rootX, out int rootY, out int tileObjectId)
        {
            rootX = xPos;
            rootY = yPos;
            tileObjectId = -1;

            int[,] tileMap = inside != null ? inside.houseMapOnTile : WorldManager.Instance.onTileMap;
            if (tileMap == null || xPos < 0 || yPos < 0 ||
                xPos >= tileMap.GetLength(0) || yPos >= tileMap.GetLength(1))
                return false;

            tileObjectId = tileMap[xPos, yPos];

            if (tileObjectId < -1)
            {
                Vector2 rootPos = WorldManager.Instance.findMultiTileObjectPos(xPos, yPos);
                rootX = (int)rootPos.x;
                rootY = (int)rootPos.y;
                if (rootX < 0 || rootY < 0 || rootX >= tileMap.GetLength(0) || rootY >= tileMap.GetLength(1))
                    return false;
                tileObjectId = tileMap[rootX, rootY];
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

            bool isFarmCrop = growth.needsTilledSoil || growth.isAPlantSproutFromAFarmPlant(tileObj.tileObjectId);

            // Farm crops are owned by the dedicated VacuFarm day-change path.
            if (isFarmCrop && VacuumCrateHelper.IsOwnedByVacuumCrate(xPos, yPos, inside, tileObj, growth))
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
            Chest forcedHarvestChest = null;
            if (!isFarmCrop && inside == null)
            {
                forcedHarvestChest = VacuumCrateHelper.FindOwningVacuumCrate(xPos, yPos, tileObj, growth);
            }

            Chest outputChest = forcedHarvestChest ?? FindAdjacentChestForHarvest(xPos, yPos, inside, searchRadius);
            if (outputChest == null)
                return false;

            // Set the inside context for the HarvestPatch to use
            CurrentHarvestInside = inside;
            CurrentHarvestForcedChest = forcedHarvestChest;

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

                Plugin.LogVerbose("Autom8er: Auto-harvested machine at " + xPos + "," + yPos);
                return true;
            }
            finally
            {
                // Always clear the context
                CurrentHarvestInside = null;
                CurrentHarvestForcedChest = null;
            }
        }
    }
}
