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

    public static class SiloHelper
    {
        private static readonly int[] dx = { -1, 0, 1, 0 };
        private static readonly int[] dy = { 0, -1, 0, 1 };

        // Scratch buffers reused every scan pass (main-thread only)
        private static readonly HashSet<long> scratchChecked = new HashSet<long>();
        private static readonly HashSet<long> scratchVisited = new HashSet<long>();
        private static readonly Queue<(int x, int y)> scratchQueue = new Queue<(int x, int y)>();

        // Try to load animal feed from chest into nearby silos (within 1-tile radius or via conveyor)
        // Loads one item per tick for visual fill effect
        public static void TryLoadFeedIntoSilos(Chest sourceChest, HouseDetails inside)
        {
            // Silos are only placed outside, skip indoor chests
            if (inside != null)
                return;

            // Cheap pre-filter: silos only ever take Animal Food (344), so a chest without
            // it can skip the radius and conveyor scans entirely.
            bool hasFeed = false;
            for (int slot = 0; slot < sourceChest.itemIds.Length; slot++)
            {
                if (sourceChest.itemIds[slot] == 344 && sourceChest.itemStacks[slot] > 0)
                {
                    hasFeed = true;
                    break;
                }
            }
            if (!hasFeed)
                return;

            scratchChecked.Clear();
            HashSet<long> checkedPositions = scratchChecked;

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
            return TryLoadFeedFromChest(sourceChest, siloX, siloY, tileObjectId, ANIMAL_FOOD_ITEM_ID);
        }

        private static bool TryLoadFeedFromChest(Chest chest, int siloX, int siloY, int tileObjectId, int validItemId)
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

                // Reserve 1 when KeepOneItem applies to this source
                bool keepOne = Plugin.KeepOneAppliesTo(chest, null);
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
                List<Vector2Int> path = Plugin.AnimationEnabled ? ConveyorPathfinder.FindPath(source, dest, null) : null;
                float staggerInterval = Plugin.AnimationEnabled ? 0.5f / Plugin.AnimationSpeed : 0f;
                int capturedSourceX = chest.xPos;
                int capturedSourceY = chest.yPos;
                int capturedItemId = itemId;
                int capturedTileObjectId = tileObjectId;

                for (int b = 0; b < numBags; b++)
                {
                    int capturedSiloX = siloX;
                    int capturedSiloY = siloY;
                    bool isLastBag = (b == numBags - 1);
                    int bagAmount = isLastBag ? itemsPerBag + remainder : itemsPerBag;
                    int capturedAmount = bagAmount;

                    System.Action depositBag = () =>
                    {
                        try
                        {
                            int acceptedAmount = 0;
                            if (ConveyorHelper.GetTileObjectId(capturedSiloX, capturedSiloY, null) == capturedTileObjectId)
                            {
                                int liveStatus = WorldManager.Instance.onTileStatusMap[capturedSiloX, capturedSiloY];
                                acceptedAmount = Mathf.Min(capturedAmount, Mathf.Max(0, 200 - liveStatus));
                                if (acceptedAmount > 0)
                                {
                                    int newStatus = liveStatus + acceptedAmount;
                                    WorldManager.Instance.onTileStatusMap[capturedSiloX, capturedSiloY] = newStatus;
                                    NetworkMapSharer.Instance.RpcGiveOnTileStatus(newStatus, capturedSiloX, capturedSiloY);
                                }
                            }

                            if (acceptedAmount < capturedAmount)
                                ConveyorHelper.DeliverOrDrop(capturedSourceX, capturedSourceY, null, capturedItemId, capturedAmount - acceptedAmount);
                        }
                        finally
                        {
                            if (isLastBag)
                                ConveyorAnimator.UnreserveTarget(capturedSiloX, capturedSiloY);
                        }
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
}
