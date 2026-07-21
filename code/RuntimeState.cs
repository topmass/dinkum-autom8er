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

    // Central save/load runtime-state lifecycle (spec rule 12).
    //
    // CONTRACT: any mutable static state that references a live world - chest
    // coordinates, Chest/GameObject instances, Mirror netIds, running flags,
    // cursors, reservations, caches, quota plans - MUST be reset from here.
    // When adding a new helper with runtime state, give it one Clear method and
    // call it below. Never add a Clear call directly to a Harmony patch; the
    // save patch and the load patch must stay single-line dispatchers so the
    // two paths cannot drift apart again.
    public static class RuntimeState
    {
        // Before SaveGame writes the world. Animations are flushed through their
        // arrival callbacks first so no in-flight item is dropped from the save.
        // This intentionally keeps per-session state (vacuum visuals, logging row
        // caches, harvest flags) alive: the same world keeps running afterward.
        public static void ResetForSave()
        {
            ConveyorAnimator.ClearAllAnimations();
            ConveyorHelper.ClearFilterDistributionState();
            FilterCrateHelper.ClearBulkDistributionState();
            QuarryHelper.ClearState();
        }

        // On load, before the new world streams in. Everything keyed by the old
        // world must go: coroutines owning pendingSorts died with the scene,
        // Mirror netIds collide across sessions, and cached Chest references
        // point into the previous save.
        public static void ResetForLoad()
        {
            ConveyorAnimator.ClearAllAnimations();
            ConveyorHelper.ClearRuntimeState();
            FilterCrateHelper.ClearBulkDistributionState();
            QuarryHelper.ClearState();
            VacuumCrateHelper.ClearState();
            HarvestHelper.ResetRuntimeState();
            NewDayHarvestPatch.ResetRunningFlag();
        }
    }
}
