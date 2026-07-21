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

    [HarmonyPatch(typeof(ContainerManager), "RemoveAmountOfItemsInChestForTable")]
    public static class CraftingTableBlackCrateRemovePatch
    {
        static bool Prefix(int itemId, int removeAmount, int xPos, int yPos, int houseX, int houseY)
        {
            HouseDetails inside = null;
            if (houseX != -1 || houseY != -1)
            {
                if (HouseManager.manage == null)
                    return true;

                inside = HouseManager.manage.getHouseInfoIfExists(houseX, houseY);
                if (inside == null)
                    return true;
            }

            return !ConveyorHelper.IsFilterCrate(xPos, yPos, inside);
        }
    }
}
