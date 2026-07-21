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

    // On game load, clear stale static state from the previous session.
    // Coroutines that would have cleaned pendingSorts are destroyed on scene reload.
    [HarmonyPatch(typeof(SaveLoad), "loadOverFrames")]
    public static class LoadGameStateClearPatch
    {
        static void Prefix()
        {
            ConveyorAnimator.ClearAllAnimations();
            ConveyorHelper.ClearPendingSorts();
            ConveyorHelper.ClearFilterDistributionState();
            FilterCrateHelper.ClearBulkDistributionState();
            QuarryHelper.ClearState();
            VacuumCrateHelper.ClearState();
            HarvestHelper.ResetRuntimeState();
            NewDayHarvestPatch.ResetRunningFlag();
            Plugin.QueueLoadCatchUp();
        }

        static void Postfix()
        {
            Plugin.QueueLoadCatchUp();
        }
    }
}
