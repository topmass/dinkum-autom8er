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

    // Handles auto-feeding fish ponds (critters) and bug terrariums (honey),
    // plus extracting roe and cocoons on day change.
    // Uses Manhattan distance 3 radius because fish ponds are 5x5 multi-tile
    // objects whose visual stone border extends beyond their onTileMap footprint.
    public static class FishPondHelper
    {
        private static readonly int[] dx = { -1, 0, 1, 0 };
        private static readonly int[] dy = { 0, -1, 0, 1 };
        private const int SearchRadius = 3;

        // Scratch buffers reused every scan pass (main-thread only)
        private static readonly HashSet<long> scratchChecked = new HashSet<long>();
        private static readonly HashSet<long> scratchVisited = new HashSet<long>();
        private static readonly Queue<(int x, int y)> scratchQueue = new Queue<(int x, int y)>();

        public static void TryFeedPondsAndTerrariums(Chest sourceChest, HouseDetails inside)
        {
            if (inside != null)
                return;

            // Cheap pre-filter: 24 slot reads beat the 24-tile diamond + conveyor BFS below,
            // and most chests hold no pond food at all.
            if (!ChestHasAnyPondFood(sourceChest))
                return;

            scratchChecked.Clear();
            HashSet<long> checkedPositions = scratchChecked;

            if (TryFeedInRadius(sourceChest, sourceChest.xPos, sourceChest.yPos, checkedPositions))
                return;

            TryFeedViaConveyorPath(sourceChest, checkedPositions);
        }

        private static bool ChestHasAnyPondFood(Chest sourceChest)
        {
            int honeyId = ContainerManager.manage.fishPondManager.honeyItem.getItemId();
            for (int slot = 0; slot < sourceChest.itemIds.Length; slot++)
            {
                int itemId = sourceChest.itemIds[slot];
                if (itemId < 0 || sourceChest.itemStacks[slot] <= 0)
                    continue;

                if (itemId == honeyId)
                    return true;

                InventoryItem item = Inventory.Instance.allItems[itemId];
                if (item != null && (bool)item.underwaterCreature)
                    return true;
            }
            return false;
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
            scratchVisited.Clear();
            scratchQueue.Clear();
            HashSet<long> visitedConveyors = scratchVisited;
            Queue<(int x, int y)> queue = scratchQueue;

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

                // KeepOneItem only applies to stackable items (critters don't stack)
                bool keepOne = Plugin.KeepOneAppliesTo(sourceChest, null);
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
                int capturedSourceX = sourceChest.xPos;
                int capturedSourceY = sourceChest.yPos;
                System.Action depositFood = () =>
                {
                    try
                    {
                        // Re-check on arrival. Two hazards (spec Open Review Finding 1):
                        // the pond may have been picked up/replaced mid-animation (null
                        // chest: writing to the stale coordinates would destroy the food),
                        // or a player loaded food manually (occupied slot 22: overwriting
                        // would destroy their item). Both routes must end in live storage
                        // or a real networked ground drop, never a stale-coordinate write.
                        Chest pondChest = ContainerManager.manage.getChestForWindow(capturedPondX, capturedPondY, null);
                        if (pondChest != null && pondChest.itemIds[22] == -1)
                            ContainerManager.manage.changeSlotInChest(capturedPondX, capturedPondY, 22, capturedItemId, 1, null);
                        else
                            ConveyorHelper.DeliverOrDrop(capturedSourceX, capturedSourceY, null, capturedItemId, 1, null, legacySearch: true);
                    }
                    finally
                    {
                        ConveyorAnimator.UnreserveTarget(capturedPondX, capturedPondY);
                    }
                };

                ConveyorAnimator.AnimateTransfer(itemId, 1,
                    new Vector2Int(sourceChest.xPos, sourceChest.yPos),
                    new Vector2Int(pondX, pondY), null, depositFood);

                string typeName = isFishPond ? "fish pond" : "bug terrarium";
                Plugin.LogVerbose("Autom8er: Fed " + typeName + " at " + pondX + "," + pondY + ": " + Inventory.Instance.allItems[itemId].itemName);
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
            List<Chest> chestsCopy = ConveyorHelper.CollectOutdoorAutomationRootChestsShared();
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

                if (!ConveyorHelper.IsGenericAutomationRootContainer(chest.xPos, chest.yPos, null))
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
            scratchVisited.Clear();
            scratchQueue.Clear();
            HashSet<long> visitedConveyors = scratchVisited;
            Queue<(int x, int y)> queue = scratchQueue;
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
            {
                // No destination is a failure of THIS chest's network, not the pond.
                // Unmark so a later chest in the scan can still claim this pond today.
                checkedPonds.Remove(pondKey);
                return false;
            }

            FilterCrateHelper.MarkRouteActive(depositDestination.routeX, depositDestination.routeY, null);

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
            int capturedRootX = rootX;
            int capturedRootY = rootY;
            System.Action depositOutput = () =>
            {
                if (!HarvestHelper.TryDepositHarvestToChest(capturedChest, null, capturedOutputId, capturedExtract,
                    () => AutomationCreditHelper.TryGrantOutputCredit(tileObjectId, capturedOutputId, capturedExtract)))
                {
                    // Chest full on arrival — try any reachable chest
                    if (!ConveyorHelper.FallbackDepositToAnyChest(capturedChest.xPos, capturedChest.yPos, null, capturedOutputId, capturedExtract,
                        () => AutomationCreditHelper.TryGrantOutputCredit(tileObjectId, capturedOutputId, capturedExtract)))
                    {
                        // Everything full: put the output back on pond slot 23 instead of
                        // destroying it. Add onto whatever the pond produced meanwhile.
                        Chest livePond = ContainerManager.manage.getChestForWindow(capturedRootX, capturedRootY, null);
                        int liveStack = (livePond != null && livePond.itemIds[23] == capturedOutputId) ? livePond.itemStacks[23] : 0;
                        if (livePond != null && (livePond.itemIds[23] == -1 || livePond.itemIds[23] == capturedOutputId))
                        {
                            ContainerManager.manage.changeSlotInChest(capturedRootX, capturedRootY, 23, capturedOutputId, liveStack + capturedExtract, null);
                            Plugin.LogWarningThrottled("pond-extract-returned", "Autom8er: No storage available, returned output to pond/terrarium.");
                        }
                        else
                        {
                            // Pond gone or slot 23 holds a different item: a networked
                            // ground drop beats a stale-coordinate write or silent loss.
                            ConveyorHelper.DeliverOrDrop(capturedRootX, capturedRootY, null, capturedOutputId, capturedExtract);
                        }
                    }
                }
            };

            ConveyorAnimator.AnimateTransfer(outputId, extractAmount,
                new Vector2Int(rootX, rootY),
                new Vector2Int(capturedRouteX, capturedRouteY), null, depositOutput,
                HarvestHelper.GetNextDayChangeAnimationDelay(capturedChest));

            string typeName = isFishPond ? "fish pond" : "bug terrarium";
            string itemName = Inventory.Instance.allItems[outputId].itemName;
            Plugin.LogVerbose("Autom8er: Extracted " + extractAmount + "x " + itemName + " from " + typeName + " at " + rootX + "," + rootY);
            return true;
        }
    }
}
