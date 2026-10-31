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

    // Flush conveyor animations before saving so no items are lost in transit.
    // Every live save goes through SaveAndLoad.SaveGame (SaveLoad.SaveGame is unused).
    [HarmonyPatch(typeof(SaveAndLoad), "SaveGame")]
    public static class SaveGameAnimationClearPatch
    {
        static bool Prefix(SaveAndLoad __instance, bool isServer, bool takePhoto, bool endOfDaySave)
        {
            // Both save coroutines would share one isSaving flag, and the first to finish would clear it
            // while the second still writes. A save asked for during a save runs once that save ends.
            if (Plugin.IsSaving)
            {
                __instance.StartCoroutine(SaveAfterCurrentSave(__instance, isServer, takePhoto, endOfDaySave));
                return false;
            }

            RuntimeState.ResetForSave(isServer);
            return true;
        }

        private static IEnumerator SaveAfterCurrentSave(SaveAndLoad saver, bool isServer, bool takePhoto, bool endOfDaySave)
        {
            while (Plugin.IsSaving)
                yield return null;

            saver.SaveGame(isServer, takePhoto, endOfDaySave);
        }
    }
}
