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

    [HarmonyPatch(typeof(TapeMeasureManager), "useTapeMeasure")]
    public static class TapeMeasureAutoFarmerRangePatch
    {
        public static bool Prefix(TapeMeasureManager __instance)
        {
            return !AutoFarmerRangeGuide.TryHandleTapeUse(__instance);
        }
    }
}
