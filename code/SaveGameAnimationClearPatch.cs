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

    // Clear conveyor animations before saving so no items are lost in transit
    [HarmonyPatch(typeof(SaveLoad), "SaveGame")]
    public static class SaveGameAnimationClearPatch
    {
        static void Prefix()
        {
            ConveyorAnimator.ClearAllAnimations();
            ConveyorHelper.ClearFilterDistributionState();
            FilterCrateHelper.ClearBulkDistributionState();
            QuarryHelper.ClearState();
        }
    }
}
