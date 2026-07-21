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

    public static class ConveyorAnimator
    {
        private static List<ConveyorAnimation> activeAnimations = new List<ConveyorAnimation>();
        private static List<ArcAnimation> activeArcAnimations = new List<ArcAnimation>();
        private static HashSet<long> reservedTargets = new HashSet<long>();

        private class ConveyorAnimation
        {
            public GameObject visual;
            public List<Vector2Int> path;
            public int currentSegment;
            public float segmentProgress;
            public System.Action onArrival;
            public int itemId;
            public float startDelay;
        }

        private class ArcAnimation
        {
            public GameObject visual;
            public Vector3 startWorld;
            public Vector3 endWorld;
            public float duration;
            public float elapsed;
            public float arcHeight;
            public System.Action onArrival;
            public float startDelay;
        }

        private static long PosKey(int x, int y)
        {
            return ((long)x << 32) | (uint)y;
        }

        public static void ReserveTarget(int x, int y)
        {
            reservedTargets.Add(PosKey(x, y));
        }

        public static void UnreserveTarget(int x, int y)
        {
            reservedTargets.Remove(PosKey(x, y));
        }

        public static bool IsTargetReserved(int x, int y)
        {
            return reservedTargets.Contains(PosKey(x, y));
        }

        public static Vector3 TileToWorld(int tileX, int tileY)
        {
            // Game places tile objects at (x*2, height, y*2) — no centering offset
            float worldX = tileX * 2f;
            float worldY = 0.5f;
            if (WorldManager.Instance != null &&
                tileX >= 0 && tileX < WorldManager.Instance.heightMap.GetLength(0) &&
                tileY >= 0 && tileY < WorldManager.Instance.heightMap.GetLength(1))
            {
                worldY = WorldManager.Instance.heightMap[tileX, tileY] + 0.5f;
            }
            float worldZ = tileY * 2f;
            return new Vector3(worldX, worldY, worldZ);
        }

        // Item travels 30% into the destination tile before vanishing
        private const float FINAL_TILE_FRACTION = 0.3f;

        public static void StartAnimation(int itemId, int stackAmount, List<Vector2Int> path, System.Action onArrival, float delay = 0f, bool indoor = false)
        {
            if (path == null || path.Count < 2)
            {
                if (onArrival != null)
                    onArrival();
                return;
            }

            // Indoor paths use house-local coords that would render at the wrong
            // world tiles on guests, so only outdoor animations are broadcast.
            if (!indoor)
                ConveyorNetSync.BroadcastAnimation(itemId, stackAmount, path, delay);

            GameObject visual = CreateItemVisual(itemId, TileToWorld(path[0].x, path[0].y), delay > 0f);

            if (visual == null)
            {
                if (onArrival != null)
                    onArrival();
                return;
            }

            ConveyorAnimation anim = new ConveyorAnimation
            {
                visual = visual,
                path = path,
                currentSegment = 0,
                segmentProgress = 0f,
                onArrival = onArrival,
                itemId = itemId,
                startDelay = delay
            };

            activeAnimations.Add(anim);
        }

        public static void AnimateArcTransfer(int itemId, int stackAmount, Vector3 startWorld, Vector3 endWorld, float duration, float arcHeight, System.Action onArrival, float delay = 0f)
        {
            if (!Plugin.AnimationEnabled)
            {
                if (onArrival != null)
                    onArrival();
                return;
            }

            GameObject visual = CreateItemVisual(itemId, startWorld, delay > 0f);
            if (visual == null)
            {
                if (onArrival != null)
                    onArrival();
                return;
            }

            activeArcAnimations.Add(new ArcAnimation
            {
                visual = visual,
                startWorld = startWorld,
                endWorld = endWorld,
                duration = Mathf.Max(0.01f, duration),
                elapsed = 0f,
                arcHeight = arcHeight,
                onArrival = onArrival,
                startDelay = delay
            });
        }

        public static void UpdateAnimations()
        {
            if (activeAnimations.Count == 0 && activeArcAnimations.Count == 0)
                return;

            float speed = Plugin.AnimationSpeed;

            for (int i = activeAnimations.Count - 1; i >= 0; i--)
            {
                ConveyorAnimation anim = activeAnimations[i];

                if (anim.visual == null)
                {
                    if (anim.onArrival != null)
                    {
                        try { anim.onArrival(); }
                        catch (System.Exception e) { Plugin.LogWarningThrottled("animation-callback-error", "Autom8er: Animation callback error: " + e.Message, 10f); }
                    }
                    activeAnimations.RemoveAt(i);
                    continue;
                }

                if (anim.startDelay > 0f)
                {
                    anim.startDelay -= Time.deltaTime;
                    if (anim.visual.activeSelf)
                        anim.visual.SetActive(false);
                    continue;
                }
                else if (!anim.visual.activeSelf)
                {
                    anim.visual.SetActive(true);
                }

                anim.segmentProgress += Time.deltaTime * speed;

                while (anim.segmentProgress >= 1f && anim.currentSegment < anim.path.Count - 2)
                {
                    anim.segmentProgress -= 1f;
                    anim.currentSegment++;
                }

                bool isLastSegment = (anim.currentSegment >= anim.path.Count - 2);
                float arrivalThreshold = isLastSegment ? FINAL_TILE_FRACTION : 1f;

                if (isLastSegment && anim.segmentProgress >= arrivalThreshold)
                {
                    if (anim.onArrival != null)
                    {
                        try { anim.onArrival(); }
                        catch (System.Exception e) { Plugin.LogWarningThrottled("animation-callback-error", "Autom8er: Animation callback error: " + e.Message, 10f); }
                    }
                    GameObject.Destroy(anim.visual);
                    activeAnimations.RemoveAt(i);
                    continue;
                }

                Vector2Int from = anim.path[anim.currentSegment];
                Vector2Int to = anim.path[anim.currentSegment + 1];
                Vector3 fromPos = TileToWorld(from.x, from.y);
                Vector3 toPos = TileToWorld(to.x, to.y);
                float t = Mathf.Clamp01(anim.segmentProgress);
                anim.visual.transform.position = Vector3.Lerp(fromPos, toPos, t);
            }

            for (int i = activeArcAnimations.Count - 1; i >= 0; i--)
            {
                ArcAnimation anim = activeArcAnimations[i];

                if (anim.visual == null)
                {
                    if (anim.onArrival != null)
                    {
                        try { anim.onArrival(); }
                        catch (System.Exception e) { Plugin.LogWarningThrottled("arc-animation-callback-error", "Autom8er: Arc animation callback error: " + e.Message, 10f); }
                    }
                    activeArcAnimations.RemoveAt(i);
                    continue;
                }

                if (anim.startDelay > 0f)
                {
                    anim.startDelay -= Time.deltaTime;
                    if (anim.visual.activeSelf)
                        anim.visual.SetActive(false);
                    continue;
                }
                else if (!anim.visual.activeSelf)
                {
                    anim.visual.SetActive(true);
                }

                anim.elapsed += Time.deltaTime;
                float t = Mathf.Clamp01(anim.elapsed / anim.duration);
                Vector3 pos = Vector3.Lerp(anim.startWorld, anim.endWorld, t);
                pos.y += Mathf.Sin(t * Mathf.PI) * anim.arcHeight;
                anim.visual.transform.position = pos;

                if (t >= 1f)
                {
                    if (anim.onArrival != null)
                    {
                        try { anim.onArrival(); }
                        catch (System.Exception e) { Plugin.LogWarningThrottled("arc-animation-callback-error", "Autom8er: Arc animation callback error: " + e.Message, 10f); }
                    }
                    GameObject.Destroy(anim.visual);
                    activeArcAnimations.RemoveAt(i);
                }
            }
        }

        public static void ClearAllAnimations()
        {
            for (int i = activeAnimations.Count - 1; i >= 0; i--)
            {
                ConveyorAnimation anim = activeAnimations[i];
                if (anim.onArrival != null)
                {
                    try { anim.onArrival(); }
                    catch (System.Exception e) { Plugin.LogWarningThrottled("clearall-callback-error", "Autom8er: ClearAll callback error: " + e.Message, 10f); }
                }
                if (anim.visual != null)
                    GameObject.Destroy(anim.visual);
            }
            activeAnimations.Clear();

            for (int i = activeArcAnimations.Count - 1; i >= 0; i--)
            {
                ArcAnimation anim = activeArcAnimations[i];
                if (anim.onArrival != null)
                {
                    try { anim.onArrival(); }
                    catch (System.Exception e) { Plugin.LogWarningThrottled("clearall-arc-callback-error", "Autom8er: ClearAll arc callback error: " + e.Message, 10f); }
                }
                if (anim.visual != null)
                    GameObject.Destroy(anim.visual);
            }
            activeArcAnimations.Clear();

            reservedTargets.Clear();
        }

        public static bool HasActiveAnimations()
        {
            return activeAnimations.Count > 0 || activeArcAnimations.Count > 0;
        }

        public static void AnimateTransfer(int itemId, int amount, Vector2Int source, Vector2Int dest, HouseDetails inside, System.Action depositCallback, float delay = 0f)
        {
            if (!Plugin.AnimationEnabled)
            {
                depositCallback();
                return;
            }

            List<Vector2Int> path = ConveyorPathfinder.FindPath(source, dest, inside);
            if (path == null || path.Count < 2)
            {
                depositCallback();
                return;
            }

            StartAnimation(itemId, amount, path, depositCallback, delay, inside != null);
        }

        private static GameObject CreateItemVisual(int itemId, Vector3 startWorld, bool startHidden)
        {
            GameObject visual = null;
            try
            {
                InventoryItem invItem = Inventory.Instance.allItems[itemId];
                if (invItem != null && invItem.itemPrefab != null)
                {
                    visual = GameObject.Instantiate(invItem.itemPrefab);
                    visual.transform.position = startWorld;
                    visual.transform.localScale = Vector3.one * 0.75f;

                    Rigidbody rb = visual.GetComponent<Rigidbody>();
                    if (rb != null)
                    {
                        rb.isKinematic = true;
                        rb.detectCollisions = false;
                    }
                    Collider col = visual.GetComponent<Collider>();
                    if (col != null)
                        col.enabled = false;

                    DroppedItem droppedItem = visual.GetComponent<DroppedItem>();
                    if (droppedItem != null)
                        GameObject.Destroy(droppedItem);
                    DroppedItemBounce bounce = visual.GetComponent<DroppedItemBounce>();
                    if (bounce != null)
                        GameObject.Destroy(bounce);

                    SetItemTexture setTex = visual.GetComponent<SetItemTexture>();
                    if (setTex != null)
                        setTex.setTexture(invItem);

                    if (startHidden)
                        visual.SetActive(false);
                }
            }
            catch (System.Exception e)
            {
                Plugin.LogWarningThrottled("animation-visual-create-failed-" + itemId, "Autom8er: Failed to create animation visual for item " + itemId + ": " + e.Message, 10f);
            }

            return visual;
        }
    }
}
