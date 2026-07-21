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

    // Handles loading items into TileObjectGrowthStages objects (incubators, etc)
    // Excludes crab pots (handled by CrabPotHelper with 2-tile radius)
    public static class GrowthStageHelper
    {
        private static readonly int[] dx = { -1, 0, 1, 0 };
        private static readonly int[] dy = { 0, -1, 0, 1 };

        // Scratch buffers reused every scan pass (main-thread only)
        private static readonly HashSet<long> scratchChecked = new HashSet<long>();
        private static readonly HashSet<long> scratchVisited = new HashSet<long>();
        private static readonly Queue<(int x, int y)> scratchQueue = new Queue<(int x, int y)>();

        public static void TryLoadItemsIntoGrowthStages(Chest sourceChest, HouseDetails inside)
        {
            scratchChecked.Clear();
            HashSet<long> checkedPositions = scratchChecked;

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

            scratchVisited.Clear();
            scratchQueue.Clear();
            HashSet<long> visitedConveyors = scratchVisited;
            Queue<(int x, int y)> queue = scratchQueue;

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

                // Check whether KeepOneItem applies to this source
                bool keepOne = Plugin.KeepOneAppliesTo(chest, inside);
                int minRequired = keepOne ? 2 : 1;
                if (stack < minRequired)
                    continue;

                int remaining = stack - 1;
                // Passing null here for an indoor chest would let changeSlotInChest match a
                // same-coordinate chest in a DIFFERENT house (house==null matches everything).
                if (remaining <= 0)
                    ContainerManager.manage.changeSlotInChest(chest.xPos, chest.yPos, slot, -1, 0, inside);
                else
                    ContainerManager.manage.changeSlotInChest(chest.xPos, chest.yPos, slot, itemId, remaining, inside);

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
                        // The plain RPC writes the WORLD map on clients - indoor targets must
                        // use the Inside variant or clients corrupt tiles near the map origin.
                        NetworkMapSharer.Instance.RpcGiveOnTileStatusInside(newStatus, capturedTargetX, capturedTargetY, capturedInside.xPos, capturedInside.yPos);
                    }
                    else
                    {
                        WorldManager.Instance.onTileStatusMap[capturedTargetX, capturedTargetY] = newStatus;
                        NetworkMapSharer.Instance.RpcGiveOnTileStatus(newStatus, capturedTargetX, capturedTargetY);
                    }
                    ConveyorAnimator.UnreserveTarget(capturedTargetX, capturedTargetY);
                };

                ConveyorAnimator.AnimateTransfer(itemId, 1,
                    new Vector2Int(chest.xPos, chest.yPos),
                    new Vector2Int(targetX, targetY), inside, depositItem);

                Plugin.LogVerbose("Autom8er: Loaded " + item.itemName + " into growth stage object at " + targetX + "," + targetY);
                return true;
            }

            return false;
        }
    }
}
