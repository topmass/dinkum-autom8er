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

    [HarmonyPatch(typeof(Inventory), "consumeItemInHand")]
    public static class FreeChestCratePaintingConsumePatch
    {
        static bool Prefix()
        {
            if (!Plugin.FreeChestCratePainting ||
                Inventory.Instance == null ||
                Inventory.Instance.invSlots == null ||
                Inventory.Instance.allItems == null)
            {
                return true;
            }

            int selectedSlot = Inventory.Instance.selectedSlot;
            if (selectedSlot < 0 || selectedSlot >= Inventory.Instance.invSlots.Length)
                return true;

            int itemId = Inventory.Instance.invSlots[selectedSlot].itemNo;
            if (itemId < 0 || itemId >= Inventory.Instance.allItems.Length)
                return true;

            InventoryItem item = Inventory.Instance.allItems[itemId];
            if (item == null || item.GetComponent<PaintCan>() == null)
                return true;

            return !FreeChestCratePaintingMarkPatch.ShouldSkipNextPaintConsume();
        }
    }
}
