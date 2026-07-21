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

    [HarmonyPatch(typeof(SprinklerTile), "waterTiles")]
    public static class AutoFarmerWaterTankSprinklerPatch
    {
        public static void Postfix(SprinklerTile __instance, int xPos, int yPos)
        {
            // Vanilla waterTiles runs on every machine, but this check depends on chest
            // contents only the server knows. Server-only; clients get the wet tiles via
            // the server's sprinkerContinuesToWater RPCs.
            if (!Plugin.CanRunServerAutomation())
                return;

            if (__instance == null || __instance.isTank || __instance.isSilo || WorldManager.Instance == null)
                return;

            if (!IsPositionOnMap(xPos, yPos))
                return;

            if (WorldManager.Instance.onTileStatusMap[xPos, yPos] != 0)
                return;

            if (!HasAutoFarmerWaterTankInRange(xPos, yPos))
                return;

            WaterSprinklerTiles(__instance, xPos, yPos);
        }

        private static bool HasAutoFarmerWaterTankInRange(int xPos, int yPos)
        {
            int minX = Mathf.Max(0, xPos - Plugin.AUTO_FARMER_RANGE_TILES);
            int maxX = Mathf.Min(WorldManager.Instance.onTileMap.GetLength(0) - 1, xPos + Plugin.AUTO_FARMER_RANGE_TILES);
            int minY = Mathf.Max(0, yPos - Plugin.AUTO_FARMER_RANGE_TILES);
            int maxY = Mathf.Min(WorldManager.Instance.onTileMap.GetLength(1) - 1, yPos + Plugin.AUTO_FARMER_RANGE_TILES);

            for (int x = minX; x <= maxX; x++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    if (!ConveyorHelper.IsVacuumCrate(x, y, null))
                        continue;

                    Chest chest = ConveyorHelper.FindChestAt(x, y, null);
                    if (chest != null && chest.GetAmountOfItemInside(Plugin.WATER_TANK_ITEM_ID) > 0)
                        return true;
                }
            }

            return false;
        }

        private static void WaterSprinklerTiles(SprinklerTile sprinkler, int xPos, int yPos)
        {
            WorldManager.Instance.onTileStatusMap[xPos, yPos] = 1;

            for (int offsetX = -sprinkler.horizontalSize; offsetX <= sprinkler.horizontalSize; offsetX++)
            {
                for (int offsetY = -sprinkler.verticlSize; offsetY <= sprinkler.verticlSize; offsetY++)
                {
                    int tileX = xPos + offsetX;
                    int tileY = yPos + offsetY;
                    if (!IsPositionOnMap(tileX, tileY))
                        continue;

                    int wetVersion = WorldManager.Instance.tileTypes[WorldManager.Instance.tileTypeMap[tileX, tileY]].wetVersion;
                    if (wetVersion == -1)
                        continue;

                    WorldManager.Instance.tileTypeMap[tileX, tileY] = wetVersion;
                    MarkChunkChanged(tileX, tileY);
                }
            }

            if (NetworkMapSharer.Instance != null && NetworkMapSharer.Instance.isServer)
                WorldManager.Instance.sprinkerContinuesToWater(xPos, yPos);
        }

        private static bool IsPositionOnMap(int xPos, int yPos)
        {
            return WorldManager.Instance != null &&
                WorldManager.Instance.onTileMap != null &&
                xPos >= 0 &&
                yPos >= 0 &&
                xPos < WorldManager.Instance.onTileMap.GetLength(0) &&
                yPos < WorldManager.Instance.onTileMap.GetLength(1);
        }

        private static void MarkChunkChanged(int xPos, int yPos)
        {
            if (WorldManager.Instance.chunkHasChangedToday == null)
                return;

            int chunkX = Mathf.RoundToInt(xPos / 10);
            int chunkY = Mathf.RoundToInt(yPos / 10);
            if (chunkX < 0 || chunkY < 0 ||
                chunkX >= WorldManager.Instance.chunkHasChangedToday.GetLength(0) ||
                chunkY >= WorldManager.Instance.chunkHasChangedToday.GetLength(1))
            {
                return;
            }

            WorldManager.Instance.chunkHasChangedToday[chunkX, chunkY] = true;
        }
    }
}
