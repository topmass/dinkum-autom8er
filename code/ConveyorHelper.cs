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

    public static class ConveyorHelper
    {
        private static readonly int[] dx = { -1, 0, 1, 0 };
        private static readonly int[] dy = { 0, -1, 0, 1 };
        private static readonly Dictionary<string, int> filterDistributionCursor = new Dictionary<string, int>();
        private static readonly Dictionary<long, float> automationChestRefreshAt = new Dictionary<long, float>();
        private const float AUTOMATION_CHEST_REFRESH_INTERVAL = 2.5f;

        private sealed class DestinationSearchState
        {
            public readonly List<OutputDestination> filterDestinations = new List<OutputDestination>();
            public readonly HashSet<long> seenFilters = new HashSet<long>();
            public readonly HashSet<long> seenChests = new HashSet<long>();
            public OutputDestination firstMatchingDestination;
            public OutputDestination firstEmptyDestination;
        }

        public class OutputDestination
        {
            public Chest chest;
            public int slotIndex;
            public int routeX;
            public int routeY;
        }

        private static long GetAutomationAnchorKey(Chest chest)
        {
            if (chest == null)
                return long.MinValue;

            int insideX = chest.insideX + 1;
            int insideY = chest.insideY + 1;
            return (((long)chest.xPos & 0xFFFFFL) << 44)
                | (((long)chest.yPos & 0xFFFFFL) << 24)
                | (((long)insideX & 0xFFFL) << 12)
                | ((long)insideY & 0xFFFL);
        }

        public static bool TryFeedAdjacentMachine(Chest sourceChest, HouseDetails inside)
        {
            // Hoisted: without any adjacent machine, the slot loop below re-reads the same
            // 4 neighbor tiles once per item slot (up to 96 reads per chest per tick).
            bool anyAdjacentMachine = false;
            for (int i = 0; i < 4; i++)
            {
                int tileId = GetTileObjectId(sourceChest.xPos + dx[i], sourceChest.yPos + dy[i], inside);
                if (tileId >= 0 && WorldManager.Instance.allObjects[tileId] != null &&
                    WorldManager.Instance.allObjects[tileId].tileObjectItemChanger != null)
                {
                    anyAdjacentMachine = true;
                    break;
                }
            }
            if (!anyAdjacentMachine)
                return false;

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

                    if (ShouldBlockBlastFurnaceRefill(tileObjectId, item, machineX, machineY, inside))
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
                        // Exempt sources bypass KeepOneItem; so do non-stackable items
                        // (stack always 1, they would starve forever - spec Learning 8)
                        bool keepOne = Plugin.KeepOneAppliesTo(sourceChest, inside) && item.checkIfStackable();
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

                    Plugin.LogVerbose("Autom8er: Chest -> Machine: " + (isFuelItem ? "1" : amountNeeded.ToString()) + "x " + item.itemName);
                    return true;
                }
            }

            return false;
        }

        // Scratch buffers dedicated to TryFeedMachineViaConveyorPath (per-chest-per-tick hot
        // path). Do NOT share with the per-transfer destination searches - those can nest.
        private static readonly HashSet<Vector2Int> scratchFeedNetwork = new HashSet<Vector2Int>();
        private static readonly Queue<Vector2Int> scratchFeedQueue = new Queue<Vector2Int>();

        public static bool TryFeedMachineViaConveyorPath(Chest sourceChest, HouseDetails inside)
        {
            if (Plugin.ConveyorTileType == -1)
                return false;

            // Find conveyor tiles adjacent to this chest
            scratchFeedNetwork.Clear();
            scratchFeedQueue.Clear();
            HashSet<Vector2Int> pathNetwork = scratchFeedNetwork;
            Queue<Vector2Int> toExplore = scratchFeedQueue;

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

                        if (ShouldBlockBlastFurnaceRefill(tileObjectId, item, machineX, machineY, inside))
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
                            // Exempt sources bypass KeepOneItem
                            // Non-stackable items (stack always 1) can never satisfy "keep one
                            // plus amountNeeded" and would starve forever (spec Learning 8).
                            bool keepOne = Plugin.KeepOneAppliesTo(sourceChest, inside) && item.checkIfStackable();
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
                            // Fuel items carry their durability in the stack field; the fallback
                            // must return the full taken amount, not amountNeeded (spec: Blast
                            // Furnace Rules - same principle as returning full ore batches).
                            int capturedTransferAmount = isFuelItem ? stack : amountNeeded;

                            System.Action onArrival = () =>
                            {
                                // finally: a leaked reservation blacklists this machine for
                                // all automation until the next save/load clears it.
                                try
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
                                    else
                                    {
                                        // Machine occupied on arrival: reroute, or ground-drop as last resort.
                                        DeliverOrDrop(capturedMachineX, capturedMachineY, capturedInside, capturedItemId, capturedTransferAmount);
                                    }
                                }
                                finally
                                {
                                    ConveyorAnimator.UnreserveTarget(capturedMachineX, capturedMachineY);
                                }
                            };

                            ConveyorAnimator.StartAnimation(itemId, isFuelItem ? stack : amountNeeded, path, onArrival, 0f, inside != null);
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

                        Plugin.LogVerbose("Autom8er: Conveyor input -> Machine: " + (isFuelItem ? "1" : amountNeeded.ToString()) + "x " + item.itemName);
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

            FilterCrateHelper.MarkRouteActive(destination.routeX, destination.routeY, inside);
            DepositIntoChest(destination.chest, destination.slotIndex, inside, itemId, stackAmount, onDepositSuccess);
            return true;
        }

        public static bool TryDepositToAdjacentChestLegacy(int machineX, int machineY, HouseDetails inside, int itemId, int stackAmount, System.Action onDepositSuccess = null)
        {
            DestinationSearchState state = new DestinationSearchState();
            OutputDestination firstChestDestination = null;

            for (int i = 0; i < 4; i++)
            {
                int checkX = machineX + dx[i];
                int checkY = machineY + dy[i];

                if (checkX < 0 || checkY < 0)
                    continue;

                TryCollectFilterDestinationAt(checkX, checkY, inside, itemId, state);
                TryGetLegacyChestDestinationAt(checkX, checkY, inside, itemId, ref firstChestDestination);
            }

            OutputDestination filterDestination;
            if (TryChooseBalancedFilterDestination(state.filterDestinations, itemId, inside, advanceCursor: true, out filterDestination))
            {
                FilterCrateHelper.MarkRouteActive(filterDestination.routeX, filterDestination.routeY, inside);
                DepositIntoChest(filterDestination.chest, filterDestination.slotIndex, inside, itemId, stackAmount, onDepositSuccess);
                return true;
            }

            if (firstChestDestination == null)
                return false;

            DepositIntoChest(firstChestDestination.chest, firstChestDestination.slotIndex, inside, itemId, stackAmount, onDepositSuccess);
            return true;
        }

        public static bool TryDepositViaConveyorPath(int machineX, int machineY, HouseDetails inside, int itemId, int stackAmount, System.Action onDepositSuccess = null)
        {
            OutputDestination destination;
            if (!TryFindBestConveyorOutputDestination(machineX, machineY, inside, itemId, out destination))
                return false;

            FilterCrateHelper.MarkRouteActive(destination.routeX, destination.routeY, inside);
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
                    // Chest may have been picked up/replaced mid-animation; a stale object
                    // makes changeSlotInChest silently no-op, so route to fallback instead.
                    bool chestStillLive = ContainerManager.manage.getChestForWindow(capturedChestX, capturedChestY, capturedInside) == capturedChest;
                    int arrivalSlot = chestStillLive ? FindSlotForItem(capturedChest, capturedItemId) : -1;
                    if (arrivalSlot == -1)
                    {
                        DeliverOrDrop(capturedChestX, capturedChestY, capturedInside, capturedItemId, capturedStack, onDepositSuccess);
                        return;
                    }

                    DepositIntoChest(capturedChest, arrivalSlot, capturedInside, capturedItemId, capturedStack, onDepositSuccess);
                };

                ConveyorAnimator.StartAnimation(itemId, stackAmount, path, onArrival, 0f, inside != null);
                return true;
            }

            DepositIntoChest(destination.chest, destination.slotIndex, inside, itemId, stackAmount, onDepositSuccess);
            return true;
        }

        public static bool TryDepositViaConveyorPathLegacy(int machineX, int machineY, HouseDetails inside, int itemId, int stackAmount, System.Action onDepositSuccess = null)
        {
            if (Plugin.ConveyorTileType == -1)
                return false;

            HashSet<Vector2Int> pathNetwork = new HashSet<Vector2Int>();
            Queue<Vector2Int> toExplore = new Queue<Vector2Int>();
            DestinationSearchState state = new DestinationSearchState();
            OutputDestination firstChestDestination = null;

            for (int i = 0; i < 4; i++)
            {
                int checkX = machineX + dx[i];
                int checkY = machineY + dy[i];

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

                    if (checkX == machineX && checkY == machineY)
                        continue;

                    TryCollectFilterDestinationAt(checkX, checkY, inside, itemId, state);
                    TryGetLegacyChestDestinationAt(checkX, checkY, inside, itemId, ref firstChestDestination);
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

            OutputDestination destination;
            if (TryChooseBalancedFilterDestination(state.filterDestinations, itemId, inside, advanceCursor: true, out destination) ||
                (destination = firstChestDestination) != null)
            {
                FilterCrateHelper.MarkRouteActive(destination.routeX, destination.routeY, inside);
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
                        bool chestStillLive = ContainerManager.manage.getChestForWindow(capturedChestX, capturedChestY, capturedInside) == capturedChest;
                        int arrivalSlot = chestStillLive ? FindSlotForItem(capturedChest, capturedItemId) : -1;
                        if (arrivalSlot == -1)
                        {
                            DeliverOrDrop(capturedChestX, capturedChestY, capturedInside, capturedItemId, capturedStack, onDepositSuccess, legacySearch: true);
                            return;
                        }

                        DepositIntoChest(capturedChest, arrivalSlot, capturedInside, capturedItemId, capturedStack, onDepositSuccess);
                    };

                    ConveyorAnimator.StartAnimation(itemId, stackAmount, path, onArrival, 0f, inside != null);
                    return true;
                }

                DepositIntoChest(destination.chest, destination.slotIndex, inside, itemId, stackAmount, onDepositSuccess);
                return true;
            }

            return false;
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

        public static bool FallbackDepositToAnyChestLegacy(int originX, int originY, HouseDetails inside, int itemId, int stackAmount, System.Action onDepositSuccess = null)
        {
            if (TryDepositToAdjacentChestLegacy(originX, originY, inside, itemId, stackAmount, onDepositSuccess))
                return true;

            if (TryDepositViaConveyorPathLegacy(originX, originY, inside, itemId, stackAmount, onDepositSuccess))
                return true;

            return false;
        }

        // GUARANTEED-TERMINAL delivery sink (spec Open Review Finding 3). Every arrival
        // callback whose intended destination can vanish or fill mid-animation must end
        // here: chest search first, then a real networked ground drop at the origin tile.
        // A ground drop is always possible, so items are never silently destroyed.
        // onDepositSuccess (task/XP credit) fires only on a live chest write; the
        // ground-drop tail intentionally does NOT invoke it, matching the old
        // failed-fallback behavior where no credit was granted either.
        public static void DeliverOrDrop(int originX, int originY, HouseDetails inside, int itemId, int stackAmount, System.Action onDepositSuccess = null, bool legacySearch = false)
        {
            if (itemId < 0 || stackAmount <= 0)
                return;

            bool deposited = legacySearch
                ? FallbackDepositToAnyChestLegacy(originX, originY, inside, itemId, stackAmount, onDepositSuccess)
                : FallbackDepositToAnyChest(originX, originY, inside, itemId, stackAmount, onDepositSuccess);
            if (deposited)
                return;

            if (NetworkMapSharer.Instance == null || WorldManager.Instance == null)
            {
                // Should be unreachable on the host; log loudly so it shows up in reports.
                Plugin.LogWarningThrottled("deliver-or-drop-no-server", "Autom8er: DeliverOrDrop had no server to spawn a ground drop, item lost.", 10f);
                return;
            }

            float height = 0.5f;
            if (originX >= 0 && originX < WorldManager.Instance.heightMap.GetLength(0) &&
                originY >= 0 && originY < WorldManager.Instance.heightMap.GetLength(1))
            {
                height = WorldManager.Instance.heightMap[originX, originY];
            }

            // spawnAServerDrop (not a bare dropAnItem) so the drop is network-spawned
            // and real for every client - see VacuumCrateHelper.SpawnFallbackGroundDrop.
            Vector3 dropPos = new Vector3(originX * 2, height, originY * 2);
            NetworkMapSharer.Instance.spawnAServerDrop(itemId, stackAmount, dropPos, inside);
            Plugin.LogWarningThrottled("deliver-or-drop-ground", "Autom8er: No chest had room on arrival, dropped item on the ground instead.", 10f);
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

            DestinationSearchState state = new DestinationSearchState();
            OutputDestination firstChestDestination = null;

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

                        TryCollectFilterDestinationAt(checkX, checkY, inside, itemId, state);
                        TryGetLegacyChestDestinationAt(checkX, checkY, inside, itemId, ref firstChestDestination);
                    }
                }
            }

            if (TryChooseBalancedFilterDestination(state.filterDestinations, itemId, inside, advanceCursor: true, out bestDestination))
                return true;

            if (firstChestDestination != null)
            {
                bestDestination = firstChestDestination;
                return true;
            }

            return TryFindBestConveyorOutputDestinationLegacyFromRadius(originX, originY, inside, itemId, searchRadius, out bestDestination);
        }

        public static bool TryFindBestOutputDestinationFromNetworkAnchor(Chest anchorChest, HouseDetails inside, int itemId, bool includeAnchorChest, bool excludeVacuumCrates, out OutputDestination bestDestination)
        {
            if (anchorChest == null || itemId < 0)
            {
                bestDestination = null;
                return false;
            }

            bestDestination = null;
            DestinationSearchState state = new DestinationSearchState();

            if (includeAnchorChest)
                TryGetBestDestinationAt(anchorChest.xPos, anchorChest.yPos, inside, itemId, excludeVacuumCrates, state);

            if (Plugin.ConveyorTileType == -1)
                return TryFinishDestinationSearch(state, itemId, inside, advanceFilterCursor: true, out bestDestination);

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

                    TryGetBestDestinationAt(checkX, checkY, inside, itemId, excludeVacuumCrates, state);
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

            return TryFinishDestinationSearch(state, itemId, inside, advanceFilterCursor: true, out bestDestination);
        }

        public static bool TryFindBestAdjacentOutputDestination(int originX, int originY, HouseDetails inside, int itemId, out OutputDestination bestDestination)
        {
            bestDestination = null;
            if (itemId < 0)
                return false;

            DestinationSearchState state = new DestinationSearchState();

            for (int i = 0; i < 4; i++)
            {
                int checkX = originX + dx[i];
                int checkY = originY + dy[i];

                if (checkX < 0 || checkY < 0)
                    continue;

                TryGetBestDestinationAt(checkX, checkY, inside, itemId, excludeVacuumCrates: false, state);
            }

            return TryFinishDestinationSearch(state, itemId, inside, advanceFilterCursor: true, out bestDestination);
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

            DestinationSearchState state = new DestinationSearchState();

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

                    TryGetBestDestinationAt(checkX, checkY, inside, itemId, excludeVacuumCrates: false, state);
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

            return TryFinishDestinationSearch(state, itemId, inside, advanceFilterCursor: true, out bestDestination);
        }

        private static bool TryFindBestConveyorOutputDestinationLegacyFromRadius(int originX, int originY, HouseDetails inside, int itemId, int startRadius, out OutputDestination bestDestination)
        {
            bestDestination = null;
            if (itemId < 0 || Plugin.ConveyorTileType == -1)
                return false;

            HashSet<Vector2Int> pathNetwork = new HashSet<Vector2Int>();
            Queue<Vector2Int> toExplore = new Queue<Vector2Int>();
            DestinationSearchState state = new DestinationSearchState();
            OutputDestination firstChestDestination = null;

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

                    TryCollectFilterDestinationAt(checkX, checkY, inside, itemId, state);
                    TryGetLegacyChestDestinationAt(checkX, checkY, inside, itemId, ref firstChestDestination);
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

            if (TryChooseBalancedFilterDestination(state.filterDestinations, itemId, inside, advanceCursor: true, out bestDestination))
                return true;

            if (firstChestDestination != null)
            {
                bestDestination = firstChestDestination;
                return true;
            }

            return false;
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

            DestinationSearchState state = new DestinationSearchState();

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

                    TryGetBestDestinationAt(checkX, checkY, inside, itemId, excludeVacuumCrates: false, state);
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

            return TryFinishDestinationSearch(state, itemId, inside, advanceFilterCursor: true, out bestDestination);
        }

        public static List<OutputDestination> CollectFilterDestinationsFromNetworkAnchor(Chest anchorChest, HouseDetails inside, int itemId, bool includeAnchorChest)
        {
            DestinationSearchState state = new DestinationSearchState();
            if (anchorChest == null || itemId < 0)
                return state.filterDestinations;

            if (includeAnchorChest)
                TryCollectFilterDestinationAt(anchorChest.xPos, anchorChest.yPos, inside, itemId, state);

            if (Plugin.ConveyorTileType == -1)
                return state.filterDestinations;

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

                    TryCollectFilterDestinationAt(checkX, checkY, inside, itemId, state);
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

            return state.filterDestinations;
        }

        public static bool TryChooseBalancedFilterDestination(List<OutputDestination> filterDestinations, int itemId, HouseDetails inside, bool advanceCursor, out OutputDestination destination)
        {
            destination = null;
            if (filterDestinations == null || filterDestinations.Count == 0)
                return false;

            SortFilterDestinations(filterDestinations);

            string key = BuildFilterDistributionKey(filterDestinations, itemId, inside);
            int cursor;
            if (!filterDistributionCursor.TryGetValue(key, out cursor))
                cursor = 0;

            int index = Mathf.Abs(cursor) % filterDestinations.Count;
            destination = filterDestinations[index];

            if (advanceCursor)
                filterDistributionCursor[key] = (index + 1) % filterDestinations.Count;

            return true;
        }

        public static void AdvanceFilterDistributionCursor(List<OutputDestination> filterDestinations, int itemId, HouseDetails inside)
        {
            if (filterDestinations == null || filterDestinations.Count == 0)
                return;

            SortFilterDestinations(filterDestinations);

            string key = BuildFilterDistributionKey(filterDestinations, itemId, inside);
            int cursor;
            if (!filterDistributionCursor.TryGetValue(key, out cursor))
                cursor = 0;

            filterDistributionCursor[key] = (Mathf.Abs(cursor) + 1) % filterDestinations.Count;
        }

        public static void ClearFilterDistributionState()
        {
            filterDistributionCursor.Clear();
        }

        // Full load-time reset for this helper (called via RuntimeState.ResetForLoad).
        public static void ClearRuntimeState()
        {
            ClearPendingSorts();
            ClearFilterDistributionState();
            // Refresh throttle keyed by old-world chest anchors; harmless if stale in
            // gameplay terms but grows unbounded across loads without this.
            automationChestRefreshAt.Clear();
        }

        private static bool TryFinishDestinationSearch(DestinationSearchState state, int itemId, HouseDetails inside, bool advanceFilterCursor, out OutputDestination destination)
        {
            if (TryChooseBalancedFilterDestination(state.filterDestinations, itemId, inside, advanceFilterCursor, out destination))
                return true;

            destination = state.firstMatchingDestination ?? state.firstEmptyDestination;
            return destination != null;
        }

        private static void TryGetLegacyChestDestinationAt(int checkX, int checkY, HouseDetails inside, int itemId, ref OutputDestination firstChestDestination)
        {
            if (firstChestDestination != null)
                return;

            if (IsInputOnlyContainer(checkX, checkY, inside) ||
                IsSpecialContainer(checkX, checkY, inside) ||
                IsFilterCrate(checkX, checkY, inside) ||
                IsVacuumCrate(checkX, checkY, inside))
                return;

            Chest chest = FindChestAt(checkX, checkY, inside);
            if (chest == null)
                return;

            int slotIndex = FindSlotForItem(chest, itemId);
            if (slotIndex == -1)
                return;

            firstChestDestination = new OutputDestination
            {
                chest = chest,
                slotIndex = slotIndex,
                routeX = chest.xPos,
                routeY = chest.yPos
            };
        }

        private static void TryGetBestDestinationAt(int checkX, int checkY, HouseDetails inside, int itemId, bool excludeVacuumCrates, DestinationSearchState state)
        {
            TryCollectFilterDestinationAt(checkX, checkY, inside, itemId, state);

            if (IsInputOnlyContainer(checkX, checkY, inside) ||
                IsSpecialContainer(checkX, checkY, inside) ||
                IsFilterCrate(checkX, checkY, inside) ||
                IsVacuumCrate(checkX, checkY, inside))
                return;

            Chest chest = FindChestAt(checkX, checkY, inside);
            if (chest == null)
                return;

            if (excludeVacuumCrates && IsVacuumCrate(checkX, checkY, inside))
                return;

            long chestKey = (((long)chest.xPos & 0xFFFFFL) << 44)
                | (((long)chest.yPos & 0xFFFFFL) << 24)
                | (((long)(chest.insideX + 1) & 0xFFFL) << 12)
                | ((long)(chest.insideY + 1) & 0xFFFL);

            if (!state.seenChests.Add(chestKey))
                return;

            int slotIndex = FindSlotForItem(chest, itemId);
            if (slotIndex == -1)
                return;

            OutputDestination destination = new OutputDestination
            {
                chest = chest,
                slotIndex = slotIndex,
                routeX = chest.xPos,
                routeY = chest.yPos
            };

            if (ChestHasMatchingStack(chest, itemId))
            {
                if (state.firstMatchingDestination == null)
                    state.firstMatchingDestination = destination;
                return;
            }

            if (state.firstEmptyDestination == null)
                state.firstEmptyDestination = destination;
        }

        private static void TryCollectFilterDestinationAt(int filterX, int filterY, HouseDetails inside, int itemId, DestinationSearchState state)
        {
            OutputDestination filterDestination;
            if (!TryGetFilterDestinationAt(filterX, filterY, inside, itemId, out filterDestination))
                return;

            long filterKey = (((long)filterDestination.routeX & 0xFFFFFL) << 44)
                | (((long)filterDestination.routeY & 0xFFFFFL) << 24)
                | (((long)((inside != null ? inside.xPos : -1) + 1) & 0xFFFL) << 12)
                | ((long)((inside != null ? inside.yPos : -1) + 1) & 0xFFFL);

            if (!state.seenFilters.Add(filterKey))
                return;

            state.filterDestinations.Add(filterDestination);
        }

        private static string BuildFilterDistributionKey(List<OutputDestination> filterDestinations, int itemId, HouseDetails inside)
        {
            List<string> routeParts = new List<string>(filterDestinations.Count);
            for (int i = 0; i < filterDestinations.Count; i++)
            {
                OutputDestination destination = filterDestinations[i];
                routeParts.Add(destination.routeX + "," + destination.routeY);
            }

            routeParts.Sort();

            string insideKey = inside != null ? (inside.xPos + "," + inside.yPos) : "world";
            return insideKey + "|" + itemId + "|" + string.Join(";", routeParts.ToArray());
        }

        private static void SortFilterDestinations(List<OutputDestination> filterDestinations)
        {
            filterDestinations.Sort((a, b) =>
            {
                if (a.routeX != b.routeX)
                    return a.routeX.CompareTo(b.routeX);

                if (a.routeY != b.routeY)
                    return a.routeY.CompareTo(b.routeY);

                if (a.chest.xPos != b.chest.xPos)
                    return a.chest.xPos.CompareTo(b.chest.xPos);

                return a.chest.yPos.CompareTo(b.chest.yPos);
            });
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
            if (IsInputOnlyContainer(x, y, inside) || IsOutputOnlyContainer(x, y, inside) || IsFilterCrate(x, y, inside))
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

                if (IsSpecialContainer(checkX, checkY, inside) || IsVacuumCrate(checkX, checkY, inside))
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

                    if (IsSpecialContainer(checkX, checkY, inside) || IsVacuumCrate(checkX, checkY, inside))
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

            return tileObjectId == Plugin.WHITE_CRATE_TILE_ID;
        }

        public static bool IsOutputOnlyContainer(int x, int y, HouseDetails inside)
        {
            return false;
        }

        public static bool IsFilterCrate(int x, int y, HouseDetails inside)
        {
            return GetTileObjectId(x, y, inside) == Plugin.BLACK_CRATE_TILE_ID;
        }

        public static bool IsVacuumCrate(int x, int y, HouseDetails inside)
        {
            return GetTileObjectId(x, y, inside) == Plugin.GREEN_CRATE_TILE_ID;
        }

        public static bool IsPondOrTerrarium(int x, int y, HouseDetails inside)
        {
            int tileObjectId = GetTileObjectId(x, y, inside);
            if (tileObjectId < 0)
                return false;

            ChestPlaceable chestPlaceable = WorldManager.Instance.allObjects[tileObjectId].tileObjectChest;
            return chestPlaceable != null && (chestPlaceable.isFishPond || chestPlaceable.isBugTerrarium);
        }

        public static bool IsGenericAutomationRootContainer(int x, int y, HouseDetails inside)
        {
            // Read the tile ONCE - this predicate runs for every tile of full-map scans,
            // and the four helper calls it replaced each re-read the same tile.
            int tileObjectId = GetTileObjectId(x, y, inside);
            if (tileObjectId <= 0)
                return false;

            if (tileObjectId == Plugin.WHITE_CRATE_TILE_ID ||
                tileObjectId == Plugin.BLACK_CRATE_TILE_ID ||
                tileObjectId == Plugin.GREEN_CRATE_TILE_ID)
                return false;

            TileObject tileObj = WorldManager.Instance.allObjects[tileObjectId];
            if (tileObj == null || tileObj.tileObjectChest == null)
                return false;

            ChestPlaceable chestPlaceable = tileObj.tileObjectChest;
            if (chestPlaceable.isFishPond || chestPlaceable.isBugTerrarium ||
                chestPlaceable.isAutoPlacer ||
                chestPlaceable.isMannequin || chestPlaceable.isToolRack ||
                chestPlaceable.isDisplayStand)
                return false;

            return FindChestAt(x, y, inside) != null;
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
            if (x < 0 || y < 0)
                return -1;

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

        private static bool ShouldBlockBlastFurnaceRefill(int tileObjectId, InventoryItem item, int xPos, int yPos, HouseDetails inside)
        {
            if (tileObjectId != Plugin.BLAST_FURNACE_TILE_ID || item == null || item.itemChange == null)
                return false;

            if (item.itemChange.getCycles(tileObjectId) <= 1)
                return false;

            return HasActiveChangerAt(xPos, yPos, inside);
        }

        private static bool HasActiveChangerAt(int xPos, int yPos, HouseDetails inside)
        {
            if (WorldManager.Instance == null || WorldManager.Instance.allChangers == null)
                return false;

            for (int i = 0; i < WorldManager.Instance.allChangers.Count; i++)
            {
                CurrentChanger changer = WorldManager.Instance.allChangers[i];
                if (changer == null || changer.xPos != xPos || changer.yPos != yPos)
                    continue;

                if (inside == null)
                {
                    if (changer.houseX == -1 && changer.houseY == -1)
                        return true;
                }
                else if (changer.houseX == inside.xPos && changer.houseY == inside.yPos)
                {
                    return true;
                }
            }

            return false;
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
            // The shared day-change chest collection holds Chest references from the old
            // world; never let it survive into a different save (spec rule 12/14).
            cachedOutdoorRootChests = null;
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

            // Bounds must match the map actually indexed below: houseMapOnTile is 25x25,
            // so clamping against the world map size overruns it for indoor anchors.
            int mapW = inside == null ? WorldManager.Instance.GetMapSize() : inside.houseMapOnTile.GetLength(0);
            int mapH = inside == null ? WorldManager.Instance.GetMapSize() : inside.houseMapOnTile.GetLength(1);

            for (int x = autoSorter.xPos - 10; x <= autoSorter.xPos + 10; x++)
            {
                for (int y = autoSorter.yPos - 10; y <= autoSorter.yPos + 10; y++)
                {
                    if (x < 0 || y < 0 || x >= mapW || y >= mapH)
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
            // finally guarantees the debounce key is released even if the sort throws
            // or the coroutine is destroyed; a stuck key silences that sorter forever.
            try
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
            }
            finally
            {
                pendingSorts.Remove(key);
            }
        }

        public static void EnsureAutomationChestsActive(Chest anchorChest)
        {
            if (anchorChest == null)
                return;

            long key = GetAutomationAnchorKey(anchorChest);
            float now = Time.time;
            float nextRefreshAt;
            if (automationChestRefreshAt.TryGetValue(key, out nextRefreshAt) && now < nextRefreshAt)
                return;

            EnsureNearbyChestsActive(anchorChest);
            automationChestRefreshAt[key] = now + AUTOMATION_CHEST_REFRESH_INTERVAL;
        }

        public static void ActivateAllOutdoorAutomationChests()
        {
            if (WorldManager.Instance == null || ContainerManager.manage == null)
                return;

            int mapSize = WorldManager.Instance.GetMapSize();
            for (int x = 0; x < mapSize; x++)
            {
                for (int y = 0; y < mapSize; y++)
                {
                    int tileId = WorldManager.Instance.onTileMap[x, y];
                    if (tileId <= 0)
                        continue;

                    TileObject tileObj = WorldManager.Instance.allObjects[tileId];
                    if (tileObj == null || !tileObj.tileObjectChest || tileObj.tileObjectItemChanger)
                        continue;

                    ContainerManager.manage.getChestForRecycling(x, y, null);
                }
            }
        }

        private static List<Chest> cachedOutdoorRootChests = null;
        private static float cachedOutdoorRootChestsAt = -999f;

        // The day-change phases (harvest -> ponds -> quarries) all need the same full-map
        // chest collection within a ~2s kickoff window; scan the 1M-tile map once and share.
        // Callers must NOT mutate the returned list. The short TTL means normal gameplay
        // (where players add/remove chests) never sees a stale collection.
        public static List<Chest> CollectOutdoorAutomationRootChestsShared(float maxAgeSeconds = 10f)
        {
            if (cachedOutdoorRootChests != null &&
                Time.realtimeSinceStartup - cachedOutdoorRootChestsAt <= maxAgeSeconds)
                return cachedOutdoorRootChests;

            cachedOutdoorRootChests = CollectOutdoorAutomationRootChests();
            cachedOutdoorRootChestsAt = Time.realtimeSinceStartup;
            return cachedOutdoorRootChests;
        }

        public static List<Chest> CollectOutdoorAutomationRootChests()
        {
            List<Chest> roots = new List<Chest>();
            if (WorldManager.Instance == null || ContainerManager.manage == null)
                return roots;

            int mapW = WorldManager.Instance.onTileMap.GetLength(0);
            int mapH = WorldManager.Instance.onTileMap.GetLength(1);
            HashSet<long> seen = new HashSet<long>();

            for (int x = 0; x < mapW; x++)
            {
                for (int y = 0; y < mapH; y++)
                {
                    if (!IsGenericAutomationRootContainer(x, y, null))
                        continue;

                    Chest chest = FindChestAt(x, y, null);
                    if (chest == null)
                        continue;

                    long key = ((long)chest.xPos << 32) | (uint)chest.yPos;
                    if (!seen.Add(key))
                        continue;

                    roots.Add(chest);
                }
            }

            return roots;
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
}
