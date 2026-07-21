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

    [HarmonyPatch(typeof(NetworkMapSharer), "RpcSpawnATileObjectDrop")]
    public static class QuarryObjectDropSuppressPatch
    {
        static bool Prefix(int tileObjectToSpawnFrom, int xPos, int yPos, int tileStatus)
        {
            return !QuarryHelper.ShouldSuppressQuarryObjectDrop(xPos, yPos);
        }
    }
}
