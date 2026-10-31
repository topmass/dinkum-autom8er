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

    [HarmonyPatch(typeof(TileObjectSettings), "onDeathServer")]
    public static class LoggingDropCapturePatch
    {
        static void Prefix(int xPos, int yPos, HouseDetails inside, out HashSet<uint> __state)
        {
            __state = VacuumCrateHelper.BeginLoggingDropCapture(xPos, yPos, inside);
        }

        static void Postfix(int xPos, int yPos, HouseDetails inside, HashSet<uint> __state)
        {
            VacuumCrateHelper.CompleteLoggingDropCapture(xPos, yPos, inside, __state);
        }
    }

    // Falling trunks spawn their remaining drops after the tile's death call returns.
    [HarmonyPatch(typeof(WorldObject), "doDrop")]
    public static class LoggingWorldObjectDropCapturePatch
    {
        static void Prefix(WorldObject __instance, out HashSet<uint> __state)
        {
            __state = null;
            // A hive break is never a trunk. A hive this mod broke gives its honey and bees to its crate;
            // a hive the player broke keeps them in the world, even on a pending trunk's tile.
            if (VacuumCrateHelper.BeginLoggingHiveBreakDrop(__instance) || __instance.spawnBugOnDrop != null)
                return;

            Vector3 source = __instance.transform.position;
            int sourceX = Mathf.RoundToInt(source.x / 2f);
            int sourceY = Mathf.RoundToInt(source.z / 2f);
            __state = VacuumCrateHelper.BeginLoggingDropCapture(sourceX, sourceY, null);
            if (__state != null)
                VacuumCrateHelper.BeginLoggingHiveSpawnCapture(sourceX, sourceY);
        }

        static void Postfix(WorldObject __instance, HashSet<uint> __state)
        {
            Vector3 source = __instance.transform.position;
            VacuumCrateHelper.CompleteLoggingDropCapture(
                Mathf.RoundToInt(source.x / 2f), Mathf.RoundToInt(source.z / 2f), null, __state, worldObjectDrop: true);
        }

        // Runs even when doDrop throws, so a capture never leaks into later spawns.
        static void Finalizer()
        {
            VacuumCrateHelper.EndLoggingWorldObjectDrop();
        }
    }

    // Records a hive that falls from a tracked trunk. Other spawns are ignored.
    [HarmonyPatch(typeof(NetworkMapSharer), "spawnACarryable")]
    public static class LoggingHiveSpawnCapturePatch
    {
        static void Postfix(PickUpAndCarry __result)
        {
            VacuumCrateHelper.RecordLoggingHiveSpawn(__result);
        }
    }

    // Honey from a hive this mod broke goes to its crate. A crate that filled since the break holds it as a saved pickup.
    [HarmonyPatch(typeof(NetworkMapSharer), "spawnAServerDrop", new System.Type[]
    {
        typeof(int), typeof(int), typeof(Vector3), typeof(HouseDetails), typeof(bool), typeof(int)
    })]
    public static class LoggingHiveHoneyCapturePatch
    {
        static bool Prefix(int itemId, int stackAmount, Vector3 position, HouseDetails inside, int xPType)
        {
            return inside != null || !VacuumCrateHelper.TryTakeLoggingHiveOutput(itemId, stackAmount, position, xPType, isBug: false);
        }
    }

    // While the game respawns saved drops and carryables, owned logging outputs match their saved records.
    [HarmonyPatch]
    public static class LoggingRestorePatch
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(DropSaves), "loadDrops");
            yield return AccessTools.Method(typeof(CarrySave), "loadAllCarryable");
        }

        static void Prefix()
        {
            VacuumCrateHelper.BeginLoggingRestore();
        }

        static void Finalizer()
        {
            VacuumCrateHelper.EndLoggingRestore();
        }
    }

    // DropToSave.spawnDrop respawns each saved drop here. The game may stack it onto a drop on the same tile.
    [HarmonyPatch(typeof(NetworkMapSharer), "spawnAServerDropToSave")]
    public static class LoggingRestoreDropPatch
    {
        static void Prefix(int itemId, out Dictionary<DroppedItem, int> __state)
        {
            __state = VacuumCrateHelper.BeginLoggingRestoreDrop(itemId);
        }

        static void Postfix(int itemId, int stackAmount, Vector3 position, HouseDetails inside, Dictionary<DroppedItem, int> __state)
        {
            VacuumCrateHelper.CompleteLoggingRestoreDrop(itemId, stackAmount, position, inside, __state);
        }
    }

    // Bees that hive rolled go to the crate instead of spawning as live bugs.
    [HarmonyPatch(typeof(NetworkNavMesh), "spawnSpecificBug")]
    public static class LoggingHiveBugCapturePatch
    {
        static bool Prefix(int bugId, Vector3 position)
        {
            return !VacuumCrateHelper.TryTakeLoggingHiveOutput(bugId, 1, position, -1, isBug: true);
        }
    }
}
