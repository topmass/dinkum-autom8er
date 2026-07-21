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
            int sourceTileObjectId = inside != null
                ? inside.houseMapOnTile[xPos, yPos]
                : WorldManager.Instance.onTileMap[xPos, yPos];

            System.Action grantOutputCredit = () =>
            {
                AutomationCreditHelper.TryGrantOutputCredit(sourceTileObjectId, resultItemId, stackAmount);
            };

            if (ConveyorHelper.TryDepositToAdjacentChestLegacy(xPos, yPos, inside, resultItemId, stackAmount, grantOutputCredit))
            {
                Plugin.LogVerbose("Autom8er: Machine output -> " + Inventory.Instance.allItems[resultItemId].itemName);
                return false;
            }

            // Priority 2: Via Black Marble Path conveyor network
            if (ConveyorHelper.TryDepositViaConveyorPathLegacy(xPos, yPos, inside, resultItemId, stackAmount, grantOutputCredit))
            {
                Plugin.LogVerbose("Autom8er: Machine output via conveyor -> " + Inventory.Instance.allItems[resultItemId].itemName);
                return false;
            }

            return true;
        }
    }
}
