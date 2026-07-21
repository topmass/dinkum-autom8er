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

    [HarmonyPatch(typeof(CharPickUp), "CmdChangeTileVariation")]
    public static class FreeChestCratePaintingMarkPatch
    {
        private static bool skipNextChestCratePaintConsume = false;
        private static float skipPaintConsumeUntil = 0f;

        static void Prefix(CharPickUp __instance, int newColour, int xPos, int yPos)
        {
            skipNextChestCratePaintConsume = Plugin.FreeChestCratePainting &&
                IsPaintingChestOrCrate(__instance, xPos, yPos);
            skipPaintConsumeUntil = skipNextChestCratePaintConsume ? Time.realtimeSinceStartup + 0.5f : 0f;
        }

        public static bool ShouldSkipNextPaintConsume()
        {
            if (!skipNextChestCratePaintConsume)
                return false;

            skipNextChestCratePaintConsume = false;
            bool stillImmediate = Time.realtimeSinceStartup <= skipPaintConsumeUntil;
            skipPaintConsumeUntil = 0f;
            return stillImmediate;
        }

        private static bool IsPaintingChestOrCrate(CharPickUp picker, int xPos, int yPos)
        {
            if (picker == null || picker.myInteract == null || WorldManager.Instance == null)
                return false;

            HouseDetails inside = picker.myInteract.InsideHouseDetails;
            int tileObjectId = ConveyorHelper.GetTileObjectId(xPos, yPos, inside);
            if (tileObjectId < 0 || tileObjectId >= WorldManager.Instance.allObjects.Length)
                return false;

            TileObject tileObject = WorldManager.Instance.allObjects[tileObjectId];
            return tileObject != null && tileObject.tileObjectChest != null;
        }
    }
}
