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

    [HarmonyPatch(typeof(ContainerManager), "GetAmountOfItemsInChestForTable")]
    public static class CraftingTableBlackCrateAmountPatch
    {
        static bool Prefix(int itemId, int xPos, int yPos, ref int __result)
        {
            HouseDetails inside = null;
            if (NetworkMapSharer.Instance != null &&
                NetworkMapSharer.Instance.localChar != null &&
                NetworkMapSharer.Instance.localChar.myInteract != null)
            {
                inside = NetworkMapSharer.Instance.localChar.myInteract.InsideHouseDetails;
            }

            if (ConveyorHelper.IsFilterCrate(xPos, yPos, inside))
            {
                __result = 0;
                return false;
            }

            return true;
        }
    }
}
