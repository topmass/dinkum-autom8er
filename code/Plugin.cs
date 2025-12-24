using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Mirror;
using System.Collections.Generic;
using UnityEngine;

namespace Autom8er
{
    [BepInPlugin("topmass.autom8er", "Autom8er", "1.2.0")]
    public class Plugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        private Harmony harmony;
        private float scanTimer = 0f;

        // Config
        private ConfigEntry<int> configConveyorTileItemId;
        private ConfigEntry<bool> configKeepOneItem;
        private ConfigEntry<float> configScanInterval;

        // Default conveyor tile (Black Marble Path)
        public const int DEFAULT_CONVEYOR_TILE_ITEM_ID = 1747;

        // TileObject IDs for INPUT ONLY containers (won't receive machine outputs)
        public const int WHITE_CRATE_TILE_ID = 417;
        public const int WHITE_CHEST_TILE_ID = 430;

        // Cached at runtime from item data
        public static int ConveyorTileType = -1;
        public static int ConveyorTileItemId = DEFAULT_CONVEYOR_TILE_ITEM_ID;
        public static bool KeepOneItem = false;
        public static float ScanInterval = 0.3f;

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

            ConveyorTileItemId = configConveyorTileItemId.Value;
            KeepOneItem = configKeepOneItem.Value;
            ScanInterval = Mathf.Clamp(configScanInterval.Value, 0.1f, 1.0f);

            Log.LogInfo("Autom8er v1.2.0 loaded!");
            Log.LogInfo("- All machines supported (any with ItemChanger)");
            Log.LogInfo("- All chests/crates supported (except special containers)");
            Log.LogInfo("- White crates/chests = INPUT ONLY");
            Log.LogInfo("- Conveyor tile Item ID: " + ConveyorTileItemId);
            Log.LogInfo("- Scan interval: " + ScanInterval + "s");
            Log.LogInfo("- Keep one item: " + KeepOneItem);

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

            scanTimer += Time.deltaTime;
            if (scanTimer >= ScanInterval)
            {
                scanTimer = 0f;
                ProcessAllChests();
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

                // Try to feed one machine per chest per cycle
                if (ConveyorHelper.TryFeedAdjacentMachine(chest, inside))
                    continue;

                ConveyorHelper.TryFeedMachineViaConveyorPath(chest, inside);
            }
        }

        private void OnDestroy()
        {
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

                    if (!item.itemChange.checkIfCanBeDepositedServer(tileObjectId))
                        continue;

                    int amountNeeded = item.itemChange.getAmountNeeded(tileObjectId);
                    int minRequired = Plugin.KeepOneItem ? amountNeeded + 1 : amountNeeded;
                    if (stack < minRequired)
                        continue;

                    if (inside != null)
                    {
                        NetworkMapSharer.Instance.RpcDepositItemIntoChangerInside(itemId, machineX, machineY, inside.xPos, inside.yPos);
                    }
                    else
                    {
                        NetworkMapSharer.Instance.RpcDepositItemIntoChanger(itemId, machineX, machineY);
                    }
                    NetworkMapSharer.Instance.startTileTimerOnServer(itemId, machineX, machineY, inside);

                    int remaining = stack - amountNeeded;
                    if (remaining <= 0)
                    {
                        ContainerManager.manage.changeSlotInChest(sourceChest.xPos, sourceChest.yPos, slot, -1, 0, inside);
                    }
                    else
                    {
                        ContainerManager.manage.changeSlotInChest(sourceChest.xPos, sourceChest.yPos, slot, itemId, remaining, inside);
                    }

                    Plugin.Log.LogInfo("Autom8er: Chest -> Machine: " + amountNeeded + "x " + item.itemName);
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
                        int minRequired = Plugin.KeepOneItem ? amountNeeded + 1 : amountNeeded;
                        if (stack < minRequired)
                            continue;

                        if (inside != null)
                        {
                            NetworkMapSharer.Instance.RpcDepositItemIntoChangerInside(itemId, machineX, machineY, inside.xPos, inside.yPos);
                        }
                        else
                        {
                            NetworkMapSharer.Instance.RpcDepositItemIntoChanger(itemId, machineX, machineY);
                        }
                        NetworkMapSharer.Instance.startTileTimerOnServer(itemId, machineX, machineY, inside);

                        int remaining = stack - amountNeeded;
                        if (remaining <= 0)
                        {
                            ContainerManager.manage.changeSlotInChest(sourceChest.xPos, sourceChest.yPos, slot, -1, 0, inside);
                        }
                        else
                        {
                            ContainerManager.manage.changeSlotInChest(sourceChest.xPos, sourceChest.yPos, slot, itemId, remaining, inside);
                        }

                        Plugin.Log.LogInfo("Autom8er: Conveyor input -> Machine: " + amountNeeded + "x " + item.itemName);
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
                chestPlaceable.isAutoSorter || chestPlaceable.isAutoPlacer ||
                chestPlaceable.isMannequin || chestPlaceable.isToolRack ||
                chestPlaceable.isDisplayStand)
            {
                return true;
            }

            return false;
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
}
