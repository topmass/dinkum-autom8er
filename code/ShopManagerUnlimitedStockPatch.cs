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

    [HarmonyPatch(typeof(ShopManager), "sellStall")]
    public static class ShopManagerUnlimitedStockPatch
    {
        static bool Prefix(int type, int id)
        {
            return !Plugin.InfiniteToolRebuy;
        }
    }
}
