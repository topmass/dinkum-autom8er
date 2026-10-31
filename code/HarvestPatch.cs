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

            // Find chest within search radius. Some non-farm natural harvestables
            // are explicitly forced into a nearby green VacuFarm crate while still
            // using the vanilla harvest() path.
            Chest targetChest = HarvestHelper.ConsumeForcedHarvestChestAt(xPos, yPos);
            if (targetChest == null)
                targetChest = HarvestHelper.CurrentHarvestForcedChest;
            if (targetChest == null)
                targetChest = HarvestHelper.FindAdjacentChestForHarvest(xPos, yPos, inside, searchRadius);
            if (targetChest == null)
                return true; // No chest, let original harvest drop items on ground

            // Harvest all items to chest
            bool harvestedAny = false;
            // Drops with no chest slot are collected here; if we end up skipping the vanilla
            // harvest they must be ground-dropped like vanilla would, not silently destroyed.
            List<int> unfitDropIds = null;
            int sourceTileObjectId = __instance.GetComponent<TileObject>().tileObjectId;

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
                            if (!HarvestHelper.TryDepositHarvestToChest(capturedChest, capturedInside, capturedItemId, 1,
                                () => AutomationCreditHelper.TryGrantHarvestOutputCredit(sourceTileObjectId, capturedItemId, 1)))
                            {
                                ConveyorHelper.DeliverOrDrop(capturedChest.xPos, capturedChest.yPos, capturedInside, capturedItemId, 1,
                                    () => AutomationCreditHelper.TryGrantHarvestOutputCredit(sourceTileObjectId, capturedItemId, 1), legacySearch: true);
                            }
                        };

                        StartHarvestTransfer(itemId, xPos, yPos, capturedChest, inside, depositHarvest);

                        Plugin.LogVerbose("Autom8er: Crab pot harvest -> chest: " + Inventory.Instance.allItems[itemId].itemName);
                    }
                    else
                    {
                        if (unfitDropIds == null) unfitDropIds = new List<int>();
                        unfitDropIds.Add(itemId);
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
                                    if (!HarvestHelper.TryDepositHarvestToChest(capturedChest, capturedInside, capturedItemId, 1,
                                        () => AutomationCreditHelper.TryGrantHarvestOutputCredit(sourceTileObjectId, capturedItemId, 1)))
                                    {
                                        ConveyorHelper.DeliverOrDrop(capturedChest.xPos, capturedChest.yPos, capturedInside, capturedItemId, 1,
                                            () => AutomationCreditHelper.TryGrantHarvestOutputCredit(sourceTileObjectId, capturedItemId, 1), legacySearch: true);
                                    }
                                };

                                StartHarvestTransfer(itemId, xPos, yPos, capturedChest, inside, depositHarvest);

                                Plugin.LogVerbose("Autom8er: Harvest -> chest: " + Inventory.Instance.allItems[itemId].itemName);
                            }
                            else
                            {
                                if (unfitDropIds == null) unfitDropIds = new List<int>();
                                unfitDropIds.Add(itemId);
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
                                        if (!HarvestHelper.TryDepositHarvestToChest(capturedChest, capturedInside, capturedItemId, 1,
                                            () => AutomationCreditHelper.TryGrantHarvestOutputCredit(sourceTileObjectId, capturedItemId, 1)))
                                        {
                                            ConveyorHelper.DeliverOrDrop(capturedChest.xPos, capturedChest.yPos, capturedInside, capturedItemId, 1,
                                                () => AutomationCreditHelper.TryGrantHarvestOutputCredit(sourceTileObjectId, capturedItemId, 1), legacySearch: true);
                                        }
                                    };

                                    StartHarvestTransfer(itemId, xPos, yPos, capturedChest, inside, depositHarvest);

                                    Plugin.LogVerbose("Autom8er: Harvest (loot) -> chest: " + Inventory.Instance.allItems[itemId].itemName);
                                }
                                else
                                {
                                    if (unfitDropIds == null) unfitDropIds = new List<int>();
                                    unfitDropIds.Add(itemId);
                                }
                            }
                        }
                    }
                }
            }

            if (harvestedAny)
            {
                // Vanilla harvest() is being skipped, so any drop that had no chest slot
                // must still be spawned on the ground like vanilla would have done.
                if (unfitDropIds != null && NetworkMapSharer.Instance != null && WorldManager.Instance != null)
                {
                    Vector3 dropPos = new Vector3(xPos * 2, WorldManager.Instance.heightMap[xPos, yPos], yPos * 2);
                    for (int i = 0; i < unfitDropIds.Count; i++)
                        NetworkMapSharer.Instance.spawnAServerDrop(unfitDropIds[i], 1, dropPos, inside);
                }
                return false; // Skip original (we handled it)
            }

            return true; // Let original run
        }

        // During a save the chest snapshot is still to come, so the transfer starts once the save ends.
        private static void StartHarvestTransfer(int itemId, int xPos, int yPos, Chest chest, HouseDetails inside, System.Action depositHarvest)
        {
            System.Action start = () => ConveyorAnimator.AnimateTransfer(itemId, 1,
                new Vector2Int(xPos, yPos),
                new Vector2Int(chest.xPos, chest.yPos), inside, depositHarvest,
                HarvestHelper.GetNextDayChangeAnimationDelay(chest));

            if (Plugin.IsSaving && Plugin.CanRunServerAutomation())
                RuntimeState.RunAfterSave(start);
            else
                start();
        }
    }
}
