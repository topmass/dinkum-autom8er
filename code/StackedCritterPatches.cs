using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;

namespace Autom8er
{
    // Bees from automated hives can go back into a normal chest by hand. Other insects and fish stay locked.
    [HarmonyPatch(typeof(ChestWindow), "lockBugsAndFishFromChest")]
    public static class ChestHiveBeeUnlockPatch
    {
        static void Postfix(ChestWindow __instance, Chest ___currentlyOpenedChest)
        {
            // Normal chests only. Bags and NPC inventories are stashes, vehicles have no chest,
            // and fish ponds and terrariums show the pond screen.
            if (!Plugin.StackableCritters || __instance.isStash || ___currentlyOpenedChest == null || __instance.pondScreen.activeSelf)
                return;

            InventorySlot[] slots = Inventory.Instance.invSlots;
            for (int i = 0; i < slots.Length; i++)
            {
                if (slots[i].isDisabledForGive() && Plugin.IsStackedHiveBee(slots[i].itemNo))
                    slots[i].clearDisable();
            }
        }
    }

    // A bug drop releases one bug but the caller already removed the whole stack. Drop a stack as a pickup instead.
    // Any bug stack comes from this mod, so this also keeps old bee stacks safe when StackableCritters is off.
    [HarmonyPatch(typeof(CharMovement), "UserCode_CmdDropItem")]
    public static class StackedBugDropPatch
    {
        static bool Prefix(CharMovement __instance, int itemId, int stackAmount, Vector3 dropPos, Vector3 desirePos)
        {
            if (stackAmount <= 1 || !Inventory.Instance.allItems[itemId].bug)
                return true;

            // Same as the game's drop for every other item.
            if (__instance.myEquip.myPermissions.CheckIfCanPickUp())
                NetworkMapSharer.Instance.CharDropsAServerDrop(itemId, stackAmount, dropPos, desirePos, __instance.myInteract.InsideHouseDetails, false, NetworkNavMesh.nav.GetMyConnectedId(__instance));
            return false;
        }
    }

    // Placing a held bug in a terrarium clears the whole slot. For a stack, keep the rest in the slot.
    [HarmonyPatch(typeof(CharMovement), "CmdPlaceBugInTerrarium")]
    public static class StackedBugTerrariumPatch
    {
        internal static InventorySlot refundSlot;
        internal static int refundItemId;
        internal static int refundStack;
        internal static int refundFrame;

        static void Prefix(int itemId)
        {
            InventorySlot slot = Inventory.Instance.invSlots[Inventory.Instance.selectedSlot];
            if (slot.itemNo != itemId || slot.stack <= 1 || !Plugin.IsModStackedCritter(itemId))
                return;

            refundSlot = slot;
            refundItemId = itemId;
            refundStack = slot.stack - 1;
            refundFrame = Time.frameCount;
        }
    }

    // The terrarium code clears the held slot right after its command, in the same frame.
    [HarmonyPatch(typeof(InventorySlot), "updateSlotContentsAndRefresh")]
    public static class StackedBugTerrariumRefundPatch
    {
        static void Prefix(InventorySlot __instance, ref int newItemNo, ref int amount)
        {
            if (StackedBugTerrariumPatch.refundSlot == null)
                return;

            if (Time.frameCount != StackedBugTerrariumPatch.refundFrame)
            {
                StackedBugTerrariumPatch.refundSlot = null;
                return;
            }

            if (!object.ReferenceEquals(__instance, StackedBugTerrariumPatch.refundSlot))
                return;

            StackedBugTerrariumPatch.refundSlot = null;
            if (newItemNo != -1)
                return;

            newItemNo = StackedBugTerrariumPatch.refundItemId;
            amount = StackedBugTerrariumPatch.refundStack;
        }
    }

    // A museum donation clears the whole donated slot. For a stack, give back all but the donated one.
    [HarmonyPatch(typeof(GiveNPC), "donateItemToMuseum")]
    public static class StackedCritterMuseumPatch
    {
        static void Prefix(out List<int[]> __state)
        {
            __state = null;
            List<int> donated = new List<int>();
            InventorySlot[] slots = Inventory.Instance.invSlots;
            for (int i = 0; i < slots.Length; i++)
            {
                // The game donates only the first selected slot of each item.
                if (!slots[i].isSelectedForGive() || donated.Contains(slots[i].itemNo))
                    continue;

                donated.Add(slots[i].itemNo);
                if (slots[i].stack <= 1 || !Plugin.IsModStackedCritter(slots[i].itemNo))
                    continue;

                if (__state == null)
                    __state = new List<int[]>();
                __state.Add(new int[] { i, slots[i].itemNo, slots[i].stack - 1 });
            }
        }

        static void Postfix(List<int[]> __state)
        {
            if (__state == null)
                return;

            for (int i = 0; i < __state.Count; i++)
            {
                InventorySlot slot = Inventory.Instance.invSlots[__state[i][0]];
                if (slot.itemNo == -1)
                    slot.updateSlotContentsAndRefresh(__state[i][1], __state[i][2]);
            }
        }
    }
}
