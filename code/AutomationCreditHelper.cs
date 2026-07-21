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

    public static class AutomationCreditHelper
    {
        public static void TryGrantMachineInputCredit(int itemId, int tileObjectId)
        {
            if (itemId < 0 || tileObjectId < 0 || Inventory.Instance == null)
                return;

            InventoryItem item = Inventory.Instance.allItems[itemId];
            if (item == null || item.itemChange == null)
                return;

            try
            {
                item.itemChange.checkTask(tileObjectId);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Autom8er: Failed to grant machine credit for item " + itemId + " -> machine " + tileObjectId + ": " + e.Message);
            }
        }

        public static void TryGrantHarvestMilestone(int tileObjectId)
        {
            if (tileObjectId < 0 || WorldManager.Instance == null || DailyTaskGenerator.generate == null)
                return;

            TileObject tileObj = WorldManager.Instance.allObjects[tileObjectId];
            if (tileObj == null || tileObj.tileObjectGrowthStages == null)
                return;

            DailyTaskGenerator.genericTaskType milestone = tileObj.tileObjectGrowthStages.milestoneOnHarvest;
            if (milestone == DailyTaskGenerator.genericTaskType.None)
                return;

            try
            {
                DailyTaskGenerator.generate.doATask(milestone);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Autom8er: Failed to grant harvest milestone for tile object " + tileObjectId + ": " + e.Message);
            }
        }

        public static void TryGrantGrowthHarvestCredit(int tileObjectId)
        {
            if (tileObjectId < 0 || WorldManager.Instance == null)
                return;

            TileObject tileObj = WorldManager.Instance.allObjects[tileObjectId];
            if (tileObj == null || tileObj.tileObjectGrowthStages == null)
                return;

            TileObjectGrowthStages growth = tileObj.tileObjectGrowthStages;

            try
            {
                if (CharLevelManager.manage != null)
                {
                    if (growth.needsTilledSoil || growth.isAPlantSproutFromAFarmPlant(tileObj.tileObjectId))
                    {
                        if (growth.diesOnHarvest)
                        {
                            CharLevelManager.manage.addXp(CharLevelManager.SkillTypes.Farming, Mathf.Clamp(growth.objectStages.Length / 3, 1, 12));
                        }
                        else
                        {
                            CharLevelManager.manage.addXp(CharLevelManager.SkillTypes.Farming, Mathf.Clamp(growth.objectStages.Length / 8, 1, 12));
                        }
                    }
                    else if (growth.mustBeInWater)
                    {
                        CharLevelManager.manage.addXp(CharLevelManager.SkillTypes.Fishing, 3);
                    }
                    else if ((bool)growth.harvestDrop && (!growth.harvestDrop.placeable || growth.harvestDrop.placeable.tileObjectId != tileObj.tileObjectId))
                    {
                        CharLevelManager.manage.addXp(CharLevelManager.SkillTypes.Foraging, 1);
                    }
                }

                if (DailyTaskGenerator.generate == null)
                    return;

                if (growth.needsTilledSoil || growth.isAPlantSproutFromAFarmPlant(tileObj.tileObjectId))
                {
                    DailyTaskGenerator.generate.doATask(DailyTaskGenerator.genericTaskType.HarvestCrops);
                }

                if (growth.isCrabPot)
                    return;

                int amount = 1;
                if (!growth.normalPickUp && !growth.autoPickUpOnHarvest && growth.harvestSpots != null && growth.harvestSpots.Length > 0)
                {
                    amount = growth.harvestSpots.Length;
                }

                DailyTaskGenerator.generate.doATaskTileObject(DailyTaskGenerator.genericTaskType.Harvest, tileObj.tileObjectId, amount);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Autom8er: Failed to grant growth harvest credit for tile object " + tileObjectId + ": " + e.Message);
            }
        }

        public static void TryGrantOutputCredit(int tileObjectId, int itemId, int amount)
        {
            if (tileObjectId < 0 || itemId < 0 || amount <= 0 || WorldManager.Instance == null || CharLevelManager.manage == null)
                return;

            TileObject tileObj = WorldManager.Instance.allObjects[tileObjectId];
            if (tileObj == null)
                return;

            int tallyType = tileObj.getXpTallyType();
            if (tallyType < 0)
                return;

            try
            {
                CharLevelManager.manage.addToDayTally(itemId, amount, tallyType);

                InventoryItem item = Inventory.Instance != null ? Inventory.Instance.allItems[itemId] : null;
                if (item != null && (bool)item.underwaterCreature && PediaManager.manage != null)
                {
                    PediaManager.manage.addCaughtToList(itemId);
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Autom8er: Failed to grant harvest output credit for item " + itemId + ": " + e.Message);
            }
        }

        public static void TryGrantHarvestOutputCredit(int tileObjectId, int itemId, int amount)
        {
            TryGrantOutputCredit(tileObjectId, itemId, amount);
        }

        public static void TryGrantPlantSeedCredit()
        {
            if (DailyTaskGenerator.generate == null)
                return;

            try
            {
                DailyTaskGenerator.generate.doATask(DailyTaskGenerator.genericTaskType.PlantSeeds);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Autom8er: Failed to grant seed planting credit: " + e.Message);
            }
        }

        public static void TryGrantTileObjectBreakCredit(int tileObjectId)
        {
            if (tileObjectId < 0 || WorldManager.Instance == null || CharLevelManager.manage == null)
                return;

            TileObject tileObj = WorldManager.Instance.allObjects[tileObjectId];
            TileObjectSettings settings = WorldManager.Instance.allObjectSettings[tileObjectId];
            if (tileObj == null || settings == null)
                return;

            try
            {
                int xpAmount = 1 + Mathf.RoundToInt(settings.fullHealth / 10f);
                CharLevelManager.manage.addXp(CharLevelManager.SkillTypes.Mining, xpAmount);

                if (DailyTaskGenerator.generate != null)
                    DailyTaskGenerator.generate.doATask(settings.TaskType);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Autom8er: Failed to grant tile break credit for tile object " + tileObjectId + ": " + e.Message);
            }
        }

        public static void TryGrantLoggingBreakCredit(int tileObjectId)
        {
            if (tileObjectId < 0 || WorldManager.Instance == null)
                return;

            TileObjectSettings settings = WorldManager.Instance.allObjectSettings[tileObjectId];
            if (settings == null)
                return;

            try
            {
                if (CharLevelManager.manage != null &&
                    !settings.canBePickedUp &&
                    (!settings.dropsItemOnDeath || !settings.dropsItemOnDeath.placeable ||
                     settings.dropsItemOnDeath.placeable.tileObjectId != tileObjectId))
                {
                    CharLevelManager.manage.addXp(CharLevelManager.SkillTypes.Foraging, 1 + Mathf.RoundToInt(settings.fullHealth / 10f));
                }

                if (DailyTaskGenerator.generate != null)
                    DailyTaskGenerator.generate.doATask(settings.TaskType);
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Autom8er: Failed to grant logging credit for tile object " + tileObjectId + ": " + e.Message);
            }
        }

        public static void TryGrantQuarryOutputCredit(int itemId, int amount)
        {
            if (itemId < 0 || amount <= 0 || CharLevelManager.manage == null)
                return;

            try
            {
                CharLevelManager.manage.addToDayTally(itemId, amount, (int)CharLevelManager.SkillTypes.Mining);

                InventoryItem item = Inventory.Instance != null ? Inventory.Instance.allItems[itemId] : null;
                if (item != null && (bool)item.underwaterCreature && PediaManager.manage != null)
                {
                    PediaManager.manage.addCaughtToList(itemId);
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Autom8er: Failed to grant quarry output credit for item " + itemId + ": " + e.Message);
            }
        }

        public static void TryGrantDroppedItemPickupCredit(int itemId, int amount, int tallyType)
        {
            if (itemId < 0 || amount <= 0 || tallyType < 0 || CharLevelManager.manage == null)
                return;

            try
            {
                CharLevelManager.manage.addToDayTally(itemId, amount, tallyType);

                InventoryItem item = Inventory.Instance != null ? Inventory.Instance.allItems[itemId] : null;
                if (item != null && (bool)item.underwaterCreature && PediaManager.manage != null)
                {
                    PediaManager.manage.addCaughtToList(itemId);
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning("Autom8er: Failed to grant dropped-item pickup credit for item " + itemId + ": " + e.Message);
            }
        }
    }
}
