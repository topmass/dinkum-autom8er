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
}
