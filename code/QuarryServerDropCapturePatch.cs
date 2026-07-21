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

    [HarmonyPatch(typeof(NetworkMapSharer), "spawnAServerDrop", new System.Type[]
    {
        typeof(int), typeof(int), typeof(Vector3), typeof(HouseDetails), typeof(bool), typeof(int)
    })]
    public static class QuarryServerDropCapturePatch
    {
        static bool Prefix(int itemId, int stackAmount, Vector3 position, HouseDetails inside, bool tryNotToStack, int xPType)
        {
            return !QuarryHelper.TryCaptureServerDrop(itemId, stackAmount, position);
        }
    }
}
