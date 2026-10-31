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

        private static long PosKey(int x, int y)
        {
            return ((long)x << 32) | (uint)y;
        }

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

            List<Chest> chestsCopy = ConveyorHelper.CollectOutdoorAutomationRootChestsShared();
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

                if (!ConveyorHelper.IsGenericAutomationRootContainer(chest.xPos, chest.yPos, null))
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

            // A break clears the node from the map and fills chests; a save in progress
            // would keep one of them and lose or duplicate the other. An underground or
            // off-island host would write the chests to nonSavedChests, so wait that out too.
            while (Plugin.IsSaving || (NetworkServer.active && !Plugin.CanRunServerAutomation()))
                yield return null;

            if (runGeneration != quarryRunGeneration || !Plugin.CanRunServerAutomation())
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
                    // ToSave: the node is already gone from the map, so an unsaved drop is lost on reload.
                    NetworkMapSharer.Instance.spawnAServerDropToSave(itemId, stackAmount, fallbackDropPos, null, tryNotToStack: true, (int)CharLevelManager.SkillTypes.Mining);
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
}
