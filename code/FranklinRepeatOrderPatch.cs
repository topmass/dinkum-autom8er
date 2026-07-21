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

    [HarmonyPatch(typeof(CharMovement), "UserCode_CmdAgreeToCraftsmanCrafting")]
    public static class FranklinRepeatOrderPatch
    {
        static bool Prefix()
        {
            return !Plugin.InfiniteFranklinOrdering;
        }
    }
}
