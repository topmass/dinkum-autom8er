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

    [HarmonyPatch(typeof(ShopBuyDrop), "sold", new System.Type[] { typeof(bool) })]
    public static class ShopBuyDropUnlimitedStockPatch
    {
        static bool Prefix(ShopBuyDrop __instance)
        {
            if (__instance == null)
                return true;

            if (!Plugin.InfiniteToolRebuy)
                return true;

            return __instance.shopStallNo < 0;
        }
    }
}
