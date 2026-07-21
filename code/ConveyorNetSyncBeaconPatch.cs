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

    [HarmonyPatch(typeof(NetworkMapSharer), "UserCode_RpcFeedFishSound")]
    public static class ConveyorNetSyncBeaconPatch
    {
        static bool Prefix(Vector3 fishPos)
        {
            if (fishPos == ConveyorNetSync.BeaconPosition)
            {
                ConveyorNetSync.NotifyBeaconReceived();
                return false; // beacon is not a real fish-feed sound; skip vanilla playback
            }
            return true;
        }
    }
}
