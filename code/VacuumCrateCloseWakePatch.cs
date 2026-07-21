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

    [HarmonyPatch(typeof(ChestWindow), "closeChestInWindow")]
    public static class VacuumCrateCloseWakePatch
    {
        public static void Prefix(ChestWindow __instance, Chest ___currentlyOpenedChest)
        {
            if (__instance == null || ___currentlyOpenedChest == null)
                return;

            if (__instance.isStash || __instance.isVehicleStorage != null || __instance.isNPCInv != null)
                return;

            if (NetworkMapSharer.Instance == null ||
                NetworkMapSharer.Instance.localChar == null ||
                NetworkMapSharer.Instance.localChar.myInteract == null)
            {
                return;
            }

            HouseDetails inside = NetworkMapSharer.Instance.localChar.myInteract.InsideHouseDetails;
            if (!ConveyorHelper.IsVacuumCrate(___currentlyOpenedChest.xPos, ___currentlyOpenedChest.yPos, inside))
                return;

            VacuumCrateHelper.MarkCrateActive(___currentlyOpenedChest);
        }
    }
}
