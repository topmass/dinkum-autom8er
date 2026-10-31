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
        // caches, harvest flags, scheduled quarry breaks) alive: the same world keeps
        // running afterward. Plugin.Update pauses the scan and animations until the
        // save ends, so the chest snapshot taken after the map frames stays consistent.
        public static void ResetForSave(bool isServer)
        {
            // Guests and an underground/off-island host must not flush: the host's
            // chest writes would land in nonSavedChests and destroy the items. A client
            // save (isServer false) writes no chests. A save that starts while another
            // is running must not flush into that save's half-written snapshot.
            if (!isServer || Plugin.IsSaving || !Plugin.CanRunServerAutomation())
                return;

            // A queued save can start before Update delivers the previous save's deferred outputs.
            RunQueuedAfterSave();
            ConveyorAnimator.ClearAllAnimations();
            ConveyorHelper.ClearFilterDistributionState();
            FilterCrateHelper.ClearBulkDistributionState();
            VacuumCrateHelper.PrepareLoggingForSave();
        }

        // Item moves that start while a save runs: a chest write then would land in the later chest
        // snapshot after drops and machines were already saved. They run on the first host update
        // after the save, on the surface, in the order they were queued.
        private static readonly List<System.Action> afterSave = new List<System.Action>();

        public static void RunAfterSave(System.Action action)
        {
            afterSave.Add(action);
        }

        internal static void RunQueuedAfterSave()
        {
            if (afterSave.Count == 0)
                return;

            List<System.Action> batch = new List<System.Action>(afterSave);
            afterSave.Clear();
            for (int i = 0; i < batch.Count; i++)
            {
                try { batch[i](); }
                catch (System.Exception e) { Plugin.LogWarningThrottled("after-save-error", "Autom8er: After-save work failed: " + e.Message, 10f); }
            }
        }

        // On load, before the new world streams in. Everything keyed by the old
        // world must go: coroutines owning pendingSorts died with the scene,
        // Mirror netIds collide across sessions, and cached Chest references
        // point into the previous save.
        public static void ResetForLoad()
        {
            afterSave.Clear();
            ConveyorAnimator.ClearAllAnimations();
            ConveyorHelper.ClearRuntimeState();
            FilterCrateHelper.ClearBulkDistributionState();
            QuarryHelper.ClearState();
            VacuumCrateHelper.ClearState();
            FarmEnclosureHelper.ClearState();
            HarvestHelper.ResetRuntimeState();
            NewDayHarvestPatch.ResetRunningFlag();
        }
    }
}
