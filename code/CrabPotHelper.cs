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

    public static class CrabPotHelper
    {
        private static readonly int[] dx = { -1, 0, 1, 0 };
        private static readonly int[] dy = { 0, -1, 0, 1 };

        // Scratch buffers reused every scan pass (main-thread only): per-chest-per-tick
        // allocations here were a top source of GC hitches on large bases.
        private static readonly HashSet<long> scratchChecked = new HashSet<long>();
        private static readonly HashSet<long> scratchVisited = new HashSet<long>();
        private static readonly Queue<(int x, int y)> scratchQueue = new Queue<(int x, int y)>();

        // Try to load bait from chest into nearby crab pots (within 2-tile radius or via conveyor)
        public static void TryLoadBaitIntoCrabPots(Chest sourceChest, HouseDetails inside)
        {
            // Crab pots are only placed outside (in water), so skip indoor chests
            if (inside != null)
                return;

            if (!GrowthStageHelper.ChestHasPlaceableInput(sourceChest, true))
                return;

            scratchChecked.Clear();
            HashSet<long> checkedPositions = scratchChecked;

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

            scratchVisited.Clear();
            scratchQueue.Clear();
            HashSet<long> visitedConveyors = scratchVisited;
            Queue<(int x, int y)> queue = scratchQueue;

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
            return TryLoadBaitFromChest(sourceChest, checkX, checkY, tileObjectId, growth);
        }

        private static bool TryLoadBaitFromChest(Chest chest, int crabPotX, int crabPotY, int tileObjectId, TileObjectGrowthStages growth)
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

                // Check whether KeepOneItem applies to this source
                bool keepOne = Plugin.KeepOneAppliesTo(chest, null);
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
                int capturedSourceX = chest.xPos;
                int capturedSourceY = chest.yPos;
                int capturedItemId = itemId;
                int capturedTileObjectId = tileObjectId;
                int capturedMaxStage = growth.maxStageToReachByPlacing;
                System.Action depositBait = () =>
                {
                    try
                    {
                        int currentStatus = WorldManager.Instance.onTileStatusMap[capturedPotX, capturedPotY];
                        if (ConveyorHelper.GetTileObjectId(capturedPotX, capturedPotY, null) != capturedTileObjectId ||
                            currentStatus >= capturedMaxStage)
                        {
                            ConveyorHelper.DeliverOrDrop(capturedSourceX, capturedSourceY, null, capturedItemId, 1);
                            return;
                        }

                        int newStatus = currentStatus + 1;
                        WorldManager.Instance.onTileStatusMap[capturedPotX, capturedPotY] = newStatus;
                        NetworkMapSharer.Instance.RpcGiveOnTileStatus(newStatus, capturedPotX, capturedPotY);
                    }
                    finally
                    {
                        ConveyorAnimator.UnreserveTarget(capturedPotX, capturedPotY);
                    }
                };

                ConveyorAnimator.AnimateTransfer(itemId, 1,
                    new Vector2Int(chest.xPos, chest.yPos),
                    new Vector2Int(crabPotX, crabPotY), null, depositBait);

                Plugin.LogVerbose("Autom8er: Loaded bait into crab pot at " + crabPotX + "," + crabPotY + ": " + item.itemName);
                return true;
            }

            return false;
        }
    }
}
