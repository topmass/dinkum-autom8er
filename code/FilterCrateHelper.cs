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

    public static class FilterCrateHelper
    {
        private static readonly int[] dx = { -1, 0, 1, 0 };
        private static readonly int[] dy = { 0, -1, 0, 1 };
        private const int TRANSFER_STACK_CHUNK_SIZE = 10;
        private const int BULK_TRANSFER_STACK_THRESHOLD = 1000;
        private const int BULK_TRANSFER_CHUNK_SIZE = 100;
        private const int BULK_DUMP_EQUALIZE_MIN_FILTERS = 2;
        private const float BULK_DUMP_PLAN_TTL_SECONDS = 15f;
        private const float FILTER_ACTIVE_WINDOW_SECONDS = 5f;
        private static readonly Dictionary<long, float> activeCrateUntil = new Dictionary<long, float>();
        private static readonly Dictionary<long, float> networkWakeSpreadCooldownUntil = new Dictionary<long, float>();
        private static readonly Dictionary<string, BulkDistributionPlan> bulkDistributionPlans = new Dictionary<string, BulkDistributionPlan>();
        private const float NETWORK_WAKE_SPREAD_COOLDOWN_SECONDS = 1.5f;

        private class SourcePull
        {
            public Chest sourceChest;
            public int sourceSlot;
            public int moveAmount;
            public bool advanceCursorOnly;
        }

        private class BulkDistributionPlan
        {
            public string planKey;
            public Dictionary<long, int> movedByDestination = new Dictionary<long, int>();
            public int baseShare;
            public int remainder;
            public int destinationCount;
            public float lastTouchedAt;
        }

        private static long PosKey(int x, int y)
        {
            return ((long)x << 32) | (uint)y;
        }

        public static void MarkCrateActive(Chest chest)
        {
            if (chest == null)
                return;

            activeCrateUntil[PosKey(chest.xPos, chest.yPos)] = Time.time + FILTER_ACTIVE_WINDOW_SECONDS;
        }

        public static void ClearBulkDistributionState()
        {
            bulkDistributionPlans.Clear();
            activeCrateUntil.Clear();
            networkWakeSpreadCooldownUntil.Clear();
        }

        public static bool IsPlayerInteracting(Chest chest)
        {
            return chest != null && chest.playingLookingInside > 0;
        }

        public static void MarkRouteActive(int routeX, int routeY, HouseDetails inside)
        {
            if (!ConveyorHelper.IsFilterCrate(routeX, routeY, inside))
                return;

            Chest routeChest = ConveyorHelper.FindChestAt(routeX, routeY, inside);
            if (routeChest == null)
                return;

            MarkNetworkActive(routeChest, inside);
        }

        public static bool ShouldUseActiveScanRate(Chest chest)
        {
            if (chest == null)
                return false;

            if (IsPlayerInteracting(chest))
                return true;

            float activeUntil;
            return activeCrateUntil.TryGetValue(PosKey(chest.xPos, chest.yPos), out activeUntil) && Time.time < activeUntil;
        }

        private static void MarkNetworkActive(Chest filterChest, HouseDetails inside)
        {
            if (filterChest == null)
                return;

            long rootKey = PosKey(filterChest.xPos, filterChest.yPos);
            MarkCrateActive(filterChest);

            float now = Time.time;
            float cooldownUntil;
            if (networkWakeSpreadCooldownUntil.TryGetValue(rootKey, out cooldownUntil) && now < cooldownUntil)
                return;

            networkWakeSpreadCooldownUntil[rootKey] = now + NETWORK_WAKE_SPREAD_COOLDOWN_SECONDS;

            if (Plugin.ConveyorTileType == -1)
                return;

            HashSet<Vector2Int> pathNetwork = new HashSet<Vector2Int>();
            Queue<Vector2Int> toExplore = new Queue<Vector2Int>();
            HashSet<long> seenFilters = new HashSet<long>();

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
                    int filterX = current.x + dx[i];
                    int filterY = current.y + dy[i];
                    if (filterX < 0 || filterY < 0)
                        continue;

                    if (!ConveyorHelper.IsFilterCrate(filterX, filterY, inside))
                        continue;

                    long filterKey = PosKey(filterX, filterY);
                    if (!seenFilters.Add(filterKey))
                        continue;

                    Chest connectedFilterChest = ConveyorHelper.FindChestAt(filterX, filterY, inside);
                    if (connectedFilterChest != null)
                        MarkCrateActive(connectedFilterChest);
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

                bool isFuelItem = Inventory.Instance.allItems[itemId].hasFuel;
                List<ConveyorHelper.OutputDestination> filterDestinations = ConveyorHelper.CollectFilterDestinationsFromNetworkAnchor(filterChest, inside, itemId, includeAnchorChest: true);

                ConveyorHelper.OutputDestination destination;
                if (!ConveyorHelper.TryChooseBalancedFilterDestination(filterDestinations, itemId, inside, advanceCursor: false, out destination))
                    continue;

                if (destination.routeX != filterChest.xPos || destination.routeY != filterChest.yPos)
                    continue;

                if (!isFuelItem && stackAmount > 1)
                {
                    int extraAmount = stackAmount - 1;
                    if (HarvestHelper.TryDepositHarvestToChest(destination.chest, inside, itemId, extraAmount))
                    {
                        ContainerManager.manage.changeSlotInChest(filterChest.xPos, filterChest.yPos, slot, itemId, 1, inside);
                        MarkNetworkActive(filterChest, inside);
                    }
                }

                SourcePull pull = FindSourcePull(filterChest, inside, itemId, destination, filterDestinations);
                if (pull == null)
                    continue;

                if (pull.advanceCursorOnly)
                {
                    ConveyorHelper.AdvanceFilterDistributionCursor(filterDestinations, itemId, inside);
                    return;
                }

                RemoveFromSourceChest(pull.sourceChest, pull.sourceSlot, itemId, pull.moveAmount, inside);

                Chest capturedSourceChest = pull.sourceChest;
                int capturedItemId = itemId;
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

                    if (HarvestHelper.TryDepositHarvestToChest(capturedSourceChest, inside, capturedItemId, capturedAmount))
                        return;

                    // Source filled/removed mid-flight: networked ground drop beats silent loss.
                    if (NetworkMapSharer.Instance != null && WorldManager.Instance != null)
                    {
                        Vector3 sourceWorld = new Vector3(capturedSourceChest.xPos * 2, WorldManager.Instance.heightMap[capturedSourceChest.xPos, capturedSourceChest.yPos], capturedSourceChest.yPos * 2);
                        NetworkMapSharer.Instance.spawnAServerDropToSave(capturedItemId, capturedAmount, sourceWorld, inside);
                    }
                };

                ConveyorAnimator.AnimateTransfer(capturedItemId, capturedAmount,
                    new Vector2Int(capturedSourceChest.xPos, capturedSourceChest.yPos),
                    new Vector2Int(capturedRouteX, capturedRouteY),
                    inside, depositFiltered);

                MarkNetworkActive(filterChest, inside);
                ConveyorHelper.AdvanceFilterDistributionCursor(filterDestinations, itemId, inside);
                return;
            }
        }

        private static bool IsAnyFilterDestination(Chest chest, List<ConveyorHelper.OutputDestination> filterDestinations)
        {
            if (filterDestinations == null)
                return false;

            for (int i = 0; i < filterDestinations.Count; i++)
            {
                ConveyorHelper.OutputDestination dest = filterDestinations[i];
                if (dest != null && dest.chest != null &&
                    dest.chest.xPos == chest.xPos && dest.chest.yPos == chest.yPos)
                    return true;
            }
            return false;
        }

        private static bool TouchesSameItemFilter(int x, int y, HouseDetails inside, int itemId)
        {
            for (int i = 0; i < 4; i++)
            {
                int filterX = x + dx[i];
                int filterY = y + dy[i];
                if (ConveyorHelper.IsFilterCrate(filterX, filterY, inside) &&
                    ConveyorHelper.ChestHasMatchingStack(ConveyorHelper.FindChestAt(filterX, filterY, inside), itemId))
                {
                    bool hasNormalStorage = false;
                    for (int j = 0; j < 4; j++)
                    {
                        if (ConveyorHelper.IsValidFilterStorageChest(filterX + dx[j], filterY + dy[j], inside))
                        {
                            hasNormalStorage = true;
                            break;
                        }
                    }
                    if (!hasNormalStorage)
                        return true;
                }
            }
            return false;
        }

        private static SourcePull FindSourcePull(Chest filterChest, HouseDetails inside, int itemId, ConveyorHelper.OutputDestination destination, List<ConveyorHelper.OutputDestination> filterDestinations)
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

                    if (destination != null && destination.chest != null &&
                        chest.xPos == destination.chest.xPos &&
                        chest.yPos == destination.chest.yPos)
                        continue;

                    // Never pull from ANY sibling filter's destination chest - two same-item
                    // filters stealing from each other's storage ping-pongs items forever.
                    if (IsAnyFilterDestination(chest, filterDestinations))
                        continue;

                    // Never pull from a chest touching this filter crate: any of its up-to-4
                    // touching chests can become its destination once the current one fills,
                    // and shuttling between its own touching chests never ends.
                    if (Mathf.Abs(chest.xPos - filterChest.xPos) + Mathf.Abs(chest.yPos - filterChest.yPos) == 1)
                        continue;

                    // A white input crate fed by a same-item filter drops out of filterDestinations
                    // once full; another filter must still not drain the machine's input stock.
                    if (ConveyorHelper.IsInputOnlyContainer(checkX, checkY, inside) && TouchesSameItemFilter(checkX, checkY, inside, itemId))
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

                        int transferableAmount = GetTransferAmount(chest, slot, itemId, inside);
                        bool advanceCursorOnly;
                        int moveAmount = GetPreferredMoveAmount(chest, slot, itemId, destination, filterDestinations, transferableAmount, out advanceCursorOnly);
                        if (advanceCursorOnly)
                        {
                            return new SourcePull
                            {
                                advanceCursorOnly = true
                            };
                        }

                        if (moveAmount <= 0)
                            continue;

                        return new SourcePull
                        {
                            sourceChest = chest,
                            sourceSlot = slot,
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

        private static int GetTransferAmount(Chest sourceChest, int slot, int itemId, HouseDetails inside)
        {
            return Plugin.GetTransferableAmountFromChest(sourceChest, slot, itemId, allowKeepOne: true, inside: inside);
        }

        private static int GetPreferredMoveAmount(Chest sourceChest, int slot, int itemId, ConveyorHelper.OutputDestination destination, List<ConveyorHelper.OutputDestination> filterDestinations, int transferableAmount, out bool advanceCursorOnly)
        {
            advanceCursorOnly = false;

            if (Plugin.IsDurabilityTransferItem(itemId))
                return transferableAmount;

            if (!ShouldUseBulkDumpEqualization(sourceChest, itemId, filterDestinations, transferableAmount))
                return Mathf.Min(GetTransferChunkSize(itemId, transferableAmount), transferableAmount);

            BulkDistributionPlan plan = GetOrCreateBulkDistributionPlan(sourceChest, itemId, filterDestinations, transferableAmount);
            if (plan == null || destination == null)
                return Mathf.Min(GetTransferChunkSize(itemId, transferableAmount), transferableAmount);

            PruneExpiredBulkDistributionPlans();

            long destinationKey = BuildDestinationRouteKey(destination);
            int targetShare = GetTargetShareForDestination(plan, filterDestinations, destination);
            if (targetShare <= 0)
                return Mathf.Min(GetTransferChunkSize(itemId, transferableAmount), transferableAmount);

            int movedAmount;
            if (!plan.movedByDestination.TryGetValue(destinationKey, out movedAmount))
                movedAmount = 0;

            int remainingShare = targetShare - movedAmount;
            plan.lastTouchedAt = Time.time;

            if (remainingShare <= 0)
            {
                if (IsBulkDistributionPlanSatisfied(plan, filterDestinations))
                    bulkDistributionPlans.Remove(plan.planKey);
                else
                    advanceCursorOnly = true;

                return 0;
            }

            int moveAmount = Mathf.Min(remainingShare, transferableAmount);
            if (moveAmount > 0)
                plan.movedByDestination[destinationKey] = movedAmount + moveAmount;

            if (IsBulkDistributionPlanSatisfied(plan, filterDestinations))
                bulkDistributionPlans.Remove(plan.planKey);

            return moveAmount;
        }

        private static bool ShouldUseBulkDumpEqualization(Chest sourceChest, int itemId, List<ConveyorHelper.OutputDestination> filterDestinations, int transferableAmount)
        {
            if (sourceChest == null || itemId < 0 || transferableAmount <= 0 || filterDestinations == null)
                return false;

            if (!Inventory.Instance.allItems[itemId].checkIfStackable())
                return false;

            if (filterDestinations.Count < BULK_DUMP_EQUALIZE_MIN_FILTERS)
                return false;

            if (ConveyorHelper.IsAutoSorter(sourceChest) ||
                ConveyorHelper.IsFilterCrate(sourceChest.xPos, sourceChest.yPos, null) ||
                ConveyorHelper.IsVacuumCrate(sourceChest.xPos, sourceChest.yPos, null) ||
                ConveyorHelper.IsSpecialContainer(sourceChest.xPos, sourceChest.yPos, null))
            {
                return false;
            }

            if (HasActiveBulkDistributionPlan(sourceChest, itemId, filterDestinations))
                return true;

            int minLargeDump = Mathf.Max(BULK_TRANSFER_STACK_THRESHOLD, filterDestinations.Count * TRANSFER_STACK_CHUNK_SIZE);
            return transferableAmount >= minLargeDump;
        }

        private static BulkDistributionPlan GetOrCreateBulkDistributionPlan(Chest sourceChest, int itemId, List<ConveyorHelper.OutputDestination> filterDestinations, int transferableAmount)
        {
            if (sourceChest == null || filterDestinations == null || filterDestinations.Count == 0)
                return null;

            List<ConveyorHelper.OutputDestination> destinationsCopy = new List<ConveyorHelper.OutputDestination>(filterDestinations);
            SortDestinationsByRoute(destinationsCopy);

            string networkKey = BuildLocalFilterDistributionKey(destinationsCopy, itemId);
            string planKey = BuildBulkDistributionPlanKey(sourceChest, itemId, networkKey);

            BulkDistributionPlan plan;
            if (bulkDistributionPlans.TryGetValue(planKey, out plan))
            {
                plan.lastTouchedAt = Time.time;
                return plan;
            }

            plan = new BulkDistributionPlan
            {
                planKey = planKey,
                destinationCount = destinationsCopy.Count,
                baseShare = transferableAmount / destinationsCopy.Count,
                remainder = transferableAmount % destinationsCopy.Count,
                lastTouchedAt = Time.time
            };

            bulkDistributionPlans[planKey] = plan;
            return plan;
        }

        private static string BuildBulkDistributionPlanKey(Chest sourceChest, int itemId, string networkKey)
        {
            string sourceKey = sourceChest.xPos + "," + sourceChest.yPos + "," + sourceChest.insideX + "," + sourceChest.insideY;
            return sourceKey + "|" + itemId + "|" + networkKey;
        }

        private static int GetTargetShareForDestination(BulkDistributionPlan plan, List<ConveyorHelper.OutputDestination> filterDestinations, ConveyorHelper.OutputDestination destination)
        {
            if (plan == null || filterDestinations == null || destination == null)
                return 0;

            List<ConveyorHelper.OutputDestination> destinationsCopy = new List<ConveyorHelper.OutputDestination>(filterDestinations);
            SortDestinationsByRoute(destinationsCopy);

            for (int i = 0; i < destinationsCopy.Count; i++)
            {
                ConveyorHelper.OutputDestination candidate = destinationsCopy[i];
                if (candidate.routeX != destination.routeX || candidate.routeY != destination.routeY)
                    continue;

                return plan.baseShare + (i < plan.remainder ? 1 : 0);
            }

            return 0;
        }

        private static bool IsBulkDistributionPlanSatisfied(BulkDistributionPlan plan, List<ConveyorHelper.OutputDestination> filterDestinations)
        {
            if (plan == null || filterDestinations == null)
                return true;

            List<ConveyorHelper.OutputDestination> destinationsCopy = new List<ConveyorHelper.OutputDestination>(filterDestinations);
            SortDestinationsByRoute(destinationsCopy);

            for (int i = 0; i < destinationsCopy.Count; i++)
            {
                ConveyorHelper.OutputDestination destination = destinationsCopy[i];
                long destinationKey = BuildDestinationRouteKey(destination);
                int movedAmount;
                if (!plan.movedByDestination.TryGetValue(destinationKey, out movedAmount))
                    movedAmount = 0;

                int targetShare = plan.baseShare + (i < plan.remainder ? 1 : 0);
                if (movedAmount < targetShare)
                    return false;
            }

            return true;
        }

        private static long BuildDestinationRouteKey(ConveyorHelper.OutputDestination destination)
        {
            if (destination == null)
                return 0L;

            return (((long)destination.routeX & 0xFFFFFL) << 20) | ((long)destination.routeY & 0xFFFFFL);
        }

        private static void SortDestinationsByRoute(List<ConveyorHelper.OutputDestination> filterDestinations)
        {
            filterDestinations.Sort((a, b) =>
            {
                if (a.routeX != b.routeX)
                    return a.routeX.CompareTo(b.routeX);

                return a.routeY.CompareTo(b.routeY);
            });
        }

        private static string BuildLocalFilterDistributionKey(List<ConveyorHelper.OutputDestination> filterDestinations, int itemId)
        {
            List<string> routeParts = new List<string>(filterDestinations.Count);
            for (int i = 0; i < filterDestinations.Count; i++)
            {
                ConveyorHelper.OutputDestination destination = filterDestinations[i];
                routeParts.Add(destination.routeX + "," + destination.routeY);
            }

            routeParts.Sort();
            return "world|" + itemId + "|" + string.Join(";", routeParts.ToArray());
        }

        private static void PruneExpiredBulkDistributionPlans()
        {
            if (bulkDistributionPlans.Count == 0)
                return;

            List<string> expiredKeys = null;
            float now = Time.time;

            foreach (KeyValuePair<string, BulkDistributionPlan> entry in bulkDistributionPlans)
            {
                if (now - entry.Value.lastTouchedAt <= BULK_DUMP_PLAN_TTL_SECONDS)
                    continue;

                if (expiredKeys == null)
                    expiredKeys = new List<string>();
                expiredKeys.Add(entry.Key);
            }

            if (expiredKeys == null)
                return;

            for (int i = 0; i < expiredKeys.Count; i++)
            {
                bulkDistributionPlans.Remove(expiredKeys[i]);
            }
        }

        private static int GetTransferChunkSize(int itemId, int stackAmount)
        {
            if (!Inventory.Instance.allItems[itemId].checkIfStackable())
                return 1;

            return stackAmount >= BULK_TRANSFER_STACK_THRESHOLD ? BULK_TRANSFER_CHUNK_SIZE : TRANSFER_STACK_CHUNK_SIZE;
        }

        private static void RemoveFromSourceChest(Chest sourceChest, int slot, int itemId, int moveAmount, HouseDetails inside)
        {
            Plugin.RemoveTransferredItemFromChest(sourceChest, slot, itemId, moveAmount, inside);
        }

        private static bool HasActiveBulkDistributionPlan(Chest sourceChest, int itemId, List<ConveyorHelper.OutputDestination> filterDestinations)
        {
            if (sourceChest == null || itemId < 0 || filterDestinations == null || filterDestinations.Count == 0)
                return false;

            List<ConveyorHelper.OutputDestination> destinationsCopy = new List<ConveyorHelper.OutputDestination>(filterDestinations);
            SortDestinationsByRoute(destinationsCopy);

            string networkKey = BuildLocalFilterDistributionKey(destinationsCopy, itemId);
            string planKey = BuildBulkDistributionPlanKey(sourceChest, itemId, networkKey);

            BulkDistributionPlan plan;
            if (!bulkDistributionPlans.TryGetValue(planKey, out plan) || plan == null)
                return false;

            if (Time.time - plan.lastTouchedAt > BULK_DUMP_PLAN_TTL_SECONDS)
            {
                bulkDistributionPlans.Remove(planKey);
                return false;
            }

            return !IsBulkDistributionPlanSatisfied(plan, destinationsCopy);
        }
    }
}
