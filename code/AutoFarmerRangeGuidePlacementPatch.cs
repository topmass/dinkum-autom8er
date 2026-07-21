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

    [HarmonyPatch(typeof(CharInteract), "ChangeTile")]
    public static class AutoFarmerRangeGuidePlacementPatch
    {
        public static void Prefix(int newOnTile)
        {
            AutoFarmerRangeGuide.NotifyGreenCratePlaced(newOnTile);
        }
    }
}
