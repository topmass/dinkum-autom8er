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

    [HarmonyPatch(typeof(GiveNPC), "tryToBuy")]
    public static class FranklinRepeatBuyPatch
    {
        public class FranklinBuyState
        {
            public ShopBuyDrop Drop;
            public int ItemId;
        }

        static void Prefix(GiveNPC __instance, ref FranklinBuyState __state)
        {
            __state = null;

            if (!Plugin.InfiniteFranklinOrdering || __instance == null || __instance.dropToBuy == null)
                return;

            if (__instance.dropToBuy.insideShop != NPCSchedual.Locations.Craft_Workshop)
                return;

            __state = new FranklinBuyState
            {
                Drop = __instance.dropToBuy,
                ItemId = __instance.dropToBuy.myItemId
            };
        }

        static void Postfix(GiveNPC __instance, FranklinBuyState __state)
        {
            if (!Plugin.InfiniteFranklinOrdering || __instance == null || __state == null || __state.Drop == null || __state.ItemId < 0)
                return;

            if (__instance.dropToBuy != null || __state.Drop.myItemId != -1)
                return;

            __state.Drop.createItem(__state.ItemId);
        }
    }
}
