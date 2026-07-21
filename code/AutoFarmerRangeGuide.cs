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

    public static class AutoFarmerRangeGuide
    {
        private static bool heldGuideActive = false;
        private static int heldGuideX = int.MinValue;
        private static int heldGuideY = int.MinValue;
        private static bool activeGuidePinned = false;
        private static bool suppressHeldGuideUntilReleased = false;
        private static readonly FieldInfo objectAttackingField = AccessTools.Field(typeof(CharInteract), "_objectAttacking");
        private static readonly FieldInfo tapeMeasureFirstDrawField = AccessTools.Field(typeof(TapeMeasureManager), "_isFirstDraw");

        public static void Update()
        {
            if (TapeMeasureManager.manage == null || WorldManager.Instance == null || NetworkMapSharer.Instance == null || NetworkMapSharer.Instance.localChar == null)
            {
                ClearHeldGuide();
                return;
            }

            CharMovement localChar = NetworkMapSharer.Instance.localChar;
            bool hasHeldTarget = IsHoldingGreenCrate(localChar);
            if (!hasHeldTarget)
                suppressHeldGuideUntilReleased = false;

            if (localChar.myInteract == null || localChar.myInteract.InsideHouseDetails != null || !hasHeldTarget || suppressHeldGuideUntilReleased)
            {
                if (!activeGuidePinned)
                    ClearHeldGuide();
                return;
            }

            int xPos = Mathf.RoundToInt(localChar.myInteract.selectedTile.x);
            int yPos = Mathf.RoundToInt(localChar.myInteract.selectedTile.y);
            if (!IsInMap(xPos, yPos))
            {
                ClearHeldGuide();
                return;
            }

            if (heldGuideActive && heldGuideX == xPos && heldGuideY == yPos)
                return;

            ShowGuideAt(xPos, yPos);
            heldGuideActive = true;
            heldGuideX = xPos;
            heldGuideY = yPos;
            activeGuidePinned = false;
        }

        public static bool TryHandleTapeUse(TapeMeasureManager manager)
        {
            if (IsRangeReviewInstalled())
                return false;

            if (manager == null || WorldManager.Instance == null || NetworkMapSharer.Instance == null || NetworkMapSharer.Instance.localChar == null)
                return false;

            CharMovement localChar = NetworkMapSharer.Instance.localChar;
            if (localChar.myInteract == null || localChar.myInteract.InsideHouseDetails != null || manager.isCurrentlyMeasuring())
                return false;

            RefreshCurrentSelectionIfGreenCrate(localChar);

            int xPos;
            int yPos;
            if (!TryResolveGreenCrateTarget(localChar, out xPos, out yPos))
            {
                if (heldGuideActive)
                    ClearActiveGuide();
                return false;
            }

            if (activeGuidePinned && heldGuideActive && heldGuideX == xPos && heldGuideY == yPos)
            {
                ClearActiveGuide();
                return true;
            }

            ShowGuideAt(xPos, yPos);
            heldGuideActive = true;
            heldGuideX = xPos;
            heldGuideY = yPos;
            activeGuidePinned = true;
            return true;
        }

        private static bool IsRangeReviewInstalled()
        {
            Assembly[] assemblies = System.AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                AssemblyName name = assemblies[i].GetName();
                if (name != null && name.Name == "topmass.rangereview")
                    return true;
            }

            return false;
        }

        private static void RefreshCurrentSelectionIfGreenCrate(CharMovement localChar)
        {
            int xPos = Mathf.RoundToInt((localChar.transform.position.x + localChar.transform.forward.x * 2f) / 2f);
            int yPos = Mathf.RoundToInt((localChar.transform.position.z + localChar.transform.forward.z * 2f) / 2f);

            int rootX;
            int rootY;
            if (!TryGetGreenCrateAt(xPos, yPos, out rootX, out rootY))
                return;

            localChar.myInteract.RefreshTileSelection(rootX, rootY);
        }

        public static void NotifyGreenCratePlaced(int tileObjectId)
        {
            if (tileObjectId != Plugin.GREEN_CRATE_TILE_ID)
                return;

            suppressHeldGuideUntilReleased = true;
            ClearActiveGuide();
        }

        public static void ClearAll()
        {
            ClearActiveGuide();
        }

        private static void ClearActiveGuide()
        {
            heldGuideActive = false;
            heldGuideX = int.MinValue;
            heldGuideY = int.MinValue;
            activeGuidePinned = false;

            if (TapeMeasureManager.manage != null)
                TapeMeasureManager.manage.clearTapeMeasure();
        }

        private static void ClearHeldGuide()
        {
            if (!heldGuideActive)
                return;

            heldGuideActive = false;
            heldGuideX = int.MinValue;
            heldGuideY = int.MinValue;

            if (TapeMeasureManager.manage != null)
                TapeMeasureManager.manage.clearTapeMeasure();
        }

        private static bool IsHoldingGreenCrate(CharMovement localChar)
        {
            if (localChar == null || localChar.myEquip == null || localChar.myEquip.itemCurrentlyHolding == null)
                return false;

            InventoryItem item = localChar.myEquip.itemCurrentlyHolding;
            return item.placeable != null && item.placeable.tileObjectId == Plugin.GREEN_CRATE_TILE_ID;
        }

        private static bool IsInMap(int xPos, int yPos)
        {
            if (WorldManager.Instance == null || WorldManager.Instance.onTileMap == null)
                return false;

            return xPos >= 0 && yPos >= 0 &&
                xPos < WorldManager.Instance.onTileMap.GetLength(0) &&
                yPos < WorldManager.Instance.onTileMap.GetLength(1);
        }

        private static void ShowGuideAt(int xPos, int yPos)
        {
            if (TapeMeasureManager.manage == null)
                return;

            ResetTapeMeasureDrawCache(TapeMeasureManager.manage);
            Vector3 tileObjectPosition = new Vector3(xPos * 2f, 0f, yPos * 2f);
            TapeMeasureManager.manage.ShowTileObjectDistance(tileObjectPosition, Plugin.AUTO_FARMER_RANGE_TILES, Plugin.GREEN_CRATE_TILE_ID);
        }

        private static void ResetTapeMeasureDrawCache(TapeMeasureManager manager)
        {
            if (tapeMeasureFirstDrawField != null)
                tapeMeasureFirstDrawField.SetValue(manager, true);
        }

        private static bool TryResolveGreenCrateTarget(CharMovement localChar, out int targetX, out int targetY)
        {
            targetX = int.MinValue;
            targetY = int.MinValue;
            int xPos = Mathf.RoundToInt(localChar.myInteract.selectedTile.x);
            int yPos = Mathf.RoundToInt(localChar.myInteract.selectedTile.y);
            if (TryGetGreenCrateAt(xPos, yPos, out targetX, out targetY))
                return true;

            if (localChar.myInteract.tileHighlighter != null)
            {
                int highlighterX = Mathf.RoundToInt(localChar.myInteract.tileHighlighter.position.x / 2f);
                int highlighterY = Mathf.RoundToInt(localChar.myInteract.tileHighlighter.position.z / 2f);
                if (TryGetGreenCrateAt(highlighterX, highlighterY, out targetX, out targetY))
                    return true;
            }

            int forwardX = Mathf.RoundToInt((localChar.transform.position.x + localChar.transform.forward.x * 2f) / 2f);
            int forwardY = Mathf.RoundToInt((localChar.transform.position.z + localChar.transform.forward.z * 2f) / 2f);
            if (TryGetGreenCrateAt(forwardX, forwardY, out targetX, out targetY))
                return true;

            TileObject objectAttacking = GetObjectAttacking(localChar.myInteract);
            if (objectAttacking != null && TryGetGreenCrateAt(objectAttacking.xPos, objectAttacking.yPos, out targetX, out targetY))
                return true;

            int attackX = Mathf.RoundToInt(localChar.myInteract.currentlyAttackingPos.x);
            int attackY = Mathf.RoundToInt(localChar.myInteract.currentlyAttackingPos.y);
            if (TryGetGreenCrateAt(attackX, attackY, out targetX, out targetY))
                return true;

            int bestX = int.MinValue;
            int bestY = int.MinValue;
            int bestDistance = int.MaxValue;
            for (int y = yPos - 1; y <= yPos + 1; y++)
            {
                for (int x = xPos - 1; x <= xPos + 1; x++)
                {
                    int nearbyX;
                    int nearbyY;
                    if (!TryGetGreenCrateAt(x, y, out nearbyX, out nearbyY))
                        continue;

                    int distance = Mathf.Abs(x - xPos) + Mathf.Abs(y - yPos);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestX = nearbyX;
                        bestY = nearbyY;
                    }
                }
            }

            if (bestDistance == int.MaxValue)
                return false;

            targetX = bestX;
            targetY = bestY;
            return true;
        }

        private static bool TryGetGreenCrateAt(int xPos, int yPos, out int rootX, out int rootY)
        {
            rootX = xPos;
            rootY = yPos;
            int tileObjectId;
            if (!TryGetRootTileObjectAt(xPos, yPos, out rootX, out rootY, out tileObjectId))
                return false;

            return tileObjectId == Plugin.GREEN_CRATE_TILE_ID;
        }

        private static bool TryGetRootTileObjectAt(int xPos, int yPos, out int rootX, out int rootY, out int tileObjectId)
        {
            rootX = xPos;
            rootY = yPos;
            tileObjectId = -1;
            if (!IsInMap(xPos, yPos))
                return false;

            tileObjectId = WorldManager.Instance.onTileMap[xPos, yPos];
            if (tileObjectId < -1)
            {
                Vector2 rootPos = WorldManager.Instance.findMultiTileObjectPos(xPos, yPos);
                rootX = Mathf.RoundToInt(rootPos.x);
                rootY = Mathf.RoundToInt(rootPos.y);
                if (!IsInMap(rootX, rootY))
                    return false;

                tileObjectId = WorldManager.Instance.onTileMap[rootX, rootY];
            }

            return tileObjectId >= 0;
        }

        private static TileObject GetObjectAttacking(CharInteract interact)
        {
            if (interact == null || objectAttackingField == null)
                return null;

            return objectAttackingField.GetValue(interact) as TileObject;
        }
    }
}
