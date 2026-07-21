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

    public static class VacuumCrateHelper
    {
        private static readonly int[] dx = { -1, 0, 1, 0 };
        private static readonly int[] dy = { 0, -1, 0, 1 };
        private const int FERTILIZER_ITEM_ID = 275;
        private const int SEASON_ALL_ITEM_ID = 1367;
        private const int SEASON_ALL_DRY_TILE_TYPE = 43;
        private const int SEASON_ALL_WET_TILE_TYPE = 44;
        private const int INSTA_GROW_ITEM_ID = 1671;
        private const int VACUUM_HARVEST_RADIUS_TILES = 10;
        private const int VACUUM_PICKUP_RADIUS_TILES = 12;
        private const int MAX_LOGGING_ROW_LENGTH = 24;
        private const int MAX_LOGGING_GROWTH_CHAIN = 8;
        private const int LOGGING_DIRECTION_COLUMNS = 6;
        private const int LOGGING_DROP_RADIUS_TILES = 2;
        private const float LOGGING_DROP_SETTLE_SECONDS = 0.08f;
        // Ownership must outlive the worst-case scheduler rotation: 30s covers ~100 active
        // logging crates at the max 1.0s ScanInterval (~25s rotation).
        private const float LOGGING_COLLECTION_EXPIRY_SECONDS = 30f;
        private const int FARM_ACTIONS_PER_SCAN = Plugin.FARMER_ACTIONS_PER_STEP;
        private const float FARM_FOLLOW_UP_DELAY_SECONDS = 0.1f;
        private const float FARM_TILE_RESERVATION_SECONDS = 0.35f;
        private const float POST_DAY_CHANGE_HARVEST_FARM_DELAY_SECONDS = 0.3f;
        private const float VACUUM_ITEM_DELAY = 0.03f;
        private const float VACUUM_VISUAL_HEIGHT = 1.6f;
        private const int DAY_CHANGE_HARVEST_CRATES_PER_WAVE = Plugin.ACTIVE_FARMER_CRATES_PER_STEP;
        private const int DAY_CHANGE_HARVEST_TILES_PER_STEP = Plugin.FARMER_ACTIONS_PER_STEP;
        private const float DAY_CHANGE_HARVEST_STEP_DELAY_SECONDS = Plugin.FARMER_STEP_DELAY_SECONDS;
        private const float DAY_CHANGE_HARVEST_WAVE_DELAY_SECONDS = 0.05f;
        private const float VACUUM_STACK_BUFFER_SECONDS = 0.3f;
        private const int TRANSFER_STACK_CHUNK_SIZE = 10;
        private const int BULK_TRANSFER_STACK_THRESHOLD = 1000;
        private const int BULK_TRANSFER_CHUNK_SIZE = 100;
        private const int MULTI_FARMER_VISUAL_CHUNK_THRESHOLD = 100;
        private const float FARMER_NETWORK_SCALE_CACHE_SECONDS = 2f;
        private const float VACUUM_ACTIVE_WINDOW_SECONDS = 2.5f;

        private class VacuumVisualState
        {
            public GameObject visual;
            public float expireAt;
            public int tileX;
            public int tileY;
        }

        private class HarvestTarget
        {
            public int xPos;
            public int yPos;
            public TileObject tileObj;
            public TileObjectGrowthStages growth;
        }

        private class FarmTileTarget
        {
            public int xPos;
            public int yPos;
            public int distance;
        }

        private class VacuumDropGroup
        {
            public int itemId;
            public int totalStackAmount;
            public int tallyType;
            public Vector2Int dropTile;
            public Vector3 startWorld;
            public List<DroppedItem> drops = new List<DroppedItem>();
            public List<uint> dropIds = new List<uint>();
        }

        private enum PendingFarmActionType
        {
            Fertilize,
            PlantSeed
        }

        private class PendingFarmAction
        {
            public int chestX;
            public int chestY;
            public int tileX;
            public int tileY;
            public PendingFarmActionType actionType;
            public int itemId;
            public float executeAt;
        }

        private class PendingLoggingCollection
        {
            public int chestX;
            public int chestY;
            public int sourceX;
            public int sourceY;
            public float collectAt;
            public float expiresAt;
            public HashSet<uint> existingDropIds = new HashSet<uint>();
            public HashSet<uint> spawnedDropIds = new HashSet<uint>();
        }

        private enum LoggingActionType
        {
            None,
            Chop,
            Plant
        }

        private class FarmerNetworkScaleState
        {
            public bool useLargeChunking;
            public float expireAt;
        }

        private class DayChangeHarvestWaveState
        {
            public Chest chest;
            public List<HarvestTarget> harvestTargets = new List<HarvestTarget>();
            public int nextHarvestIndex;
            public float readyToFlushAt;
            public bool flushStarted;
            public bool pendingFinalCollectionSweep;
        }

        private class DayChangeFarmerWaveState
        {
            public Chest chest;
        }

        private class ReplantTilePlan
        {
            public int xPos;
            public int yPos;
            public int distance;
            public bool shouldHoe;
            public bool shouldFertilize;
            public int seedItemId = -1;
            public int priorityScore;
        }

        private static readonly Dictionary<long, VacuumVisualState> activeVisuals = new Dictionary<long, VacuumVisualState>();
        private static readonly HashSet<uint> pendingDrops = new HashSet<uint>();
        private static readonly HashSet<long> harvestCollectingCrates = new HashSet<long>();
        private static readonly Dictionary<long, float> pendingFlushStartedAt = new Dictionary<long, float>();
        private static readonly Dictionary<long, float> reservedFarmTiles = new Dictionary<long, float>();
        private static readonly List<PendingFarmAction> pendingFarmActions = new List<PendingFarmAction>();
        private static readonly Dictionary<long, PendingLoggingCollection> pendingLoggingCollections = new Dictionary<long, PendingLoggingCollection>();
        private static readonly Dictionary<long, int> activeLoggingSeedByCrate = new Dictionary<long, int>();
        private static readonly Dictionary<long, Vector2Int> loggingDirectionByCrate = new Dictionary<long, Vector2Int>();
        private static readonly Dictionary<long, int> loggingRowLengthByCrate = new Dictionary<long, int>();
        private static readonly Dictionary<long, Chest> loggingCrateInstances = new Dictionary<long, Chest>();
        private static readonly Dictionary<long, float> activeCrateUntil = new Dictionary<long, float>();
        private static readonly Dictionary<long, FarmerNetworkScaleState> farmerNetworkScaleCache = new Dictionary<long, FarmerNetworkScaleState>();
        private static bool dayChangeVacuumHarvestRunning = false;
        private static float farmerActionsBlockedUntil = 0f;

        private static long PosKey(int x, int y)
        {
            return ((long)x << 32) | (uint)y;
        }

        private static void EnsureLoggingCrateInstance(Chest chest)
        {
            if (chest == null)
                return;

            long chestKey = PosKey(chest.xPos, chest.yPos);
            Chest previousChest;
            if (loggingCrateInstances.TryGetValue(chestKey, out previousChest) && !object.ReferenceEquals(previousChest, chest))
            {
                pendingLoggingCollections.Remove(chestKey);
                activeLoggingSeedByCrate.Remove(chestKey);
                loggingDirectionByCrate.Remove(chestKey);
                loggingRowLengthByCrate.Remove(chestKey);
            }

            loggingCrateInstances[chestKey] = chest;
        }

        public static void MarkCrateActive(Chest chest)
        {
            if (chest == null)
                return;

            activeCrateUntil[PosKey(chest.xPos, chest.yPos)] = Time.time + VACUUM_ACTIVE_WINDOW_SECONDS;
        }

        public static bool IsFarmerCrate(Chest chest)
        {
            return chest != null && (HasHoeTool(chest) || HasShovelTool(chest) || HasAxeTool(chest));
        }

        public static bool IsLoggingCrate(Chest chest)
        {
            return chest != null && HasAxeTool(chest);
        }

        public static bool ShouldUseActiveScanRate(Chest chest)
        {
            if (chest == null)
                return false;

            if (HasImmediateWork(chest))
                return true;

            float activeUntil;
            return activeCrateUntil.TryGetValue(PosKey(chest.xPos, chest.yPos), out activeUntil) && Time.time < activeUntil;
        }

        private static void BeginHarvestCollection(Chest chest)
        {
            if (chest == null)
                return;

            harvestCollectingCrates.Add(PosKey(chest.xPos, chest.yPos));
        }

        private static void EndHarvestCollection(Chest chest)
        {
            if (chest == null)
                return;

            harvestCollectingCrates.Remove(PosKey(chest.xPos, chest.yPos));
        }

        private static bool IsHarvestCollecting(Chest chest)
        {
            return chest != null && harvestCollectingCrates.Contains(PosKey(chest.xPos, chest.yPos));
        }

        public static System.Collections.IEnumerator ProcessDayChangeVacuumHarvests()
        {
            if (WorldManager.Instance == null || ContainerManager.manage == null || !NetworkMapSharer.Instance.isServer)
                yield break;

            BeginDayChangeHarvestPhase();
            // finally releases the harvest locks even if this coroutine is destroyed mid-run
            // (exit to menu): a stale dayChangeVacuumHarvestRunning would suspend ALL farmer
            // crates in the next loaded save until a full day-change run completes.
            try
            {
                System.Collections.IEnumerator core = ProcessDayChangeVacuumHarvestsCore();
                while (core.MoveNext())
                    yield return core.Current;
            }
            finally
            {
                EndDayChangeHarvestPhase();
            }
        }

        private static System.Collections.IEnumerator ProcessDayChangeVacuumHarvestsCore()
        {
            ActivateAllVacuumCrates();

            List<Chest> chestsCopy = new List<Chest>(ContainerManager.manage.activeChests);
            if (chestsCopy.Count == 0)
            {
                yield break;
            }

            chestsCopy.Sort((a, b) =>
            {
                if (a == null && b == null)
                    return 0;
                if (a == null)
                    return 1;
                if (b == null)
                    return -1;
                if (a.yPos != b.yPos)
                    return a.yPos.CompareTo(b.yPos);
                return a.xPos.CompareTo(b.xPos);
            });

            int harvested = 0;
            HashSet<long> claimedHarvestTiles = new HashSet<long>();
            List<Chest> outdoorVacuumCrates = new List<Chest>();

            for (int i = 0; i < chestsCopy.Count; i++)
            {
                Chest chest = chestsCopy[i];
                if (chest == null || chest.insideX != -1 || chest.insideY != -1)
                    continue;

                if (!ConveyorHelper.IsVacuumCrate(chest.xPos, chest.yPos, null))
                    continue;

                outdoorVacuumCrates.Add(chest);
            }

            for (int waveStart = 0; waveStart < outdoorVacuumCrates.Count; waveStart += DAY_CHANGE_HARVEST_CRATES_PER_WAVE)
            {
                int waveEnd = Mathf.Min(outdoorVacuumCrates.Count, waveStart + DAY_CHANGE_HARVEST_CRATES_PER_WAVE);
                List<DayChangeHarvestWaveState> waveStates = new List<DayChangeHarvestWaveState>(waveEnd - waveStart);

                for (int crateIndex = waveStart; crateIndex < waveEnd; crateIndex++)
                {
                    Chest chest = outdoorVacuumCrates[crateIndex];
                    BeginHarvestCollection(chest);
                    waveStates.Add(new DayChangeHarvestWaveState
                    {
                        chest = chest,
                        harvestTargets = CollectNearbyHarvestTargets(chest, null, claimedHarvestTiles),
                        nextHarvestIndex = 0,
                        readyToFlushAt = Time.time,
                        flushStarted = false
                    });
                }

                while (true)
                {
                    bool hasPendingCrates = false;
                    bool hasRemainingHarvest = false;
                    float nextFlushDelay = float.MaxValue;

                    for (int crateIndex = 0; crateIndex < waveStates.Count; crateIndex++)
                    {
                        DayChangeHarvestWaveState state = waveStates[crateIndex];
                        if (state == null || state.flushStarted || state.chest == null)
                            continue;

                        hasPendingCrates = true;

                        if (state.nextHarvestIndex < state.harvestTargets.Count)
                        {
                            hasRemainingHarvest = true;
                            int processedThisStep = 0;

                            while (processedThisStep < DAY_CHANGE_HARVEST_TILES_PER_STEP && state.nextHarvestIndex < state.harvestTargets.Count)
                            {
                                HarvestTarget target = state.harvestTargets[state.nextHarvestIndex++];
                                HarvestVacuumTargetAt(state.chest, target);
                                harvested++;
                                processedThisStep++;
                            }

                            float gatherDelay = TryVacuumNearbyDrops(state.chest, null);
                            float stepReadyAt = Time.time + Mathf.Max(gatherDelay, DAY_CHANGE_HARVEST_STEP_DELAY_SECONDS);
                            if (stepReadyAt > state.readyToFlushAt)
                                state.readyToFlushAt = stepReadyAt;

                            if (state.nextHarvestIndex >= state.harvestTargets.Count)
                            {
                                state.pendingFinalCollectionSweep = true;
                                state.readyToFlushAt = Mathf.Max(state.readyToFlushAt, Time.time + Plugin.DAY_CHANGE_FINAL_COLLECTION_SETTLE_SECONDS);
                            }
                        }
                        else if (Time.time >= state.readyToFlushAt)
                        {
                            if (state.pendingFinalCollectionSweep)
                            {
                                state.pendingFinalCollectionSweep = false;
                                float finalGatherDelay = TryVacuumNearbyDrops(state.chest, null);
                                if (finalGatherDelay > 0f)
                                {
                                    state.readyToFlushAt = Time.time + Mathf.Max(finalGatherDelay, Plugin.DAY_CHANGE_FINAL_COLLECTION_SETTLE_SECONDS);
                                    continue;
                                }
                            }

                            float trailingGatherDelay = TryVacuumNearbyDrops(state.chest, null);
                            if (trailingGatherDelay > 0f)
                            {
                                state.readyToFlushAt = Time.time + Mathf.Max(trailingGatherDelay, Plugin.DAY_CHANGE_FINAL_COLLECTION_SETTLE_SECONDS);
                                continue;
                            }

                            EndHarvestCollection(state.chest);
                            bool hasPendingFlush;
                            bool didFlush = TryFlushStoredItemsToNetwork(state.chest, null, out hasPendingFlush);
                            if (didFlush || hasPendingFlush)
                            {
                                state.readyToFlushAt = Time.time + (didFlush
                                    ? Mathf.Max(DAY_CHANGE_HARVEST_STEP_DELAY_SECONDS, Plugin.ScanInterval)
                                    : VACUUM_STACK_BUFFER_SECONDS);
                                continue;
                            }

                            state.flushStarted = true;
                            continue;
                        }

                        if (!state.flushStarted && state.nextHarvestIndex >= state.harvestTargets.Count)
                            nextFlushDelay = Mathf.Min(nextFlushDelay, Mathf.Max(0.01f, state.readyToFlushAt - Time.time));
                    }

                    if (!hasPendingCrates)
                        break;

                    float waitSeconds = hasRemainingHarvest
                        ? DAY_CHANGE_HARVEST_STEP_DELAY_SECONDS
                        : (nextFlushDelay == float.MaxValue ? DAY_CHANGE_HARVEST_STEP_DELAY_SECONDS : nextFlushDelay);

                    yield return new WaitForSecondsRealtime(waitSeconds);
                }

                if (waveEnd < outdoorVacuumCrates.Count && DAY_CHANGE_HARVEST_WAVE_DELAY_SECONDS > 0f)
                    yield return new WaitForSecondsRealtime(DAY_CHANGE_HARVEST_WAVE_DELAY_SECONDS);
            }

            if (Plugin.DAY_CHANGE_FINAL_COLLECTION_SETTLE_SECONDS > 0f)
                yield return new WaitForSecondsRealtime(Plugin.DAY_CHANGE_FINAL_COLLECTION_SETTLE_SECONDS);

            List<Chest> outdoorCropFarmerCrates = new List<Chest>();
            List<Chest> outdoorLoggingCrates = new List<Chest>();
            for (int i = 0; i < outdoorVacuumCrates.Count; i++)
            {
                Chest chest = outdoorVacuumCrates[i];
                if (chest == null)
                    continue;

                if (IsLoggingCrate(chest))
                    outdoorLoggingCrates.Add(chest);
                else if (IsFarmerCrate(chest))
                    outdoorCropFarmerCrates.Add(chest);
            }

            bool[] loggingWavesDone = new bool[1];
            WorldManager.Instance.StartCoroutine(RunDayChangeFarmerWaves(outdoorLoggingCrates, loggingWavesDone));
            yield return RunDayChangeFarmerWaves(outdoorCropFarmerCrates, new bool[1]);

            while (!loggingWavesDone[0])
                yield return null;

            if (harvested > 0)
            {
                Plugin.Log.LogInfo("Autom8er: Vacuum crate day-change harvest collected " + harvested + " crop/tree tile(s).");
            }
        }

        private static System.Collections.IEnumerator RunDayChangeFarmerWaves(List<Chest> crates, bool[] doneFlag)
        {
            try
            {
                for (int waveStart = 0; waveStart < crates.Count; waveStart += DAY_CHANGE_HARVEST_CRATES_PER_WAVE)
                {
                    int waveEnd = Mathf.Min(crates.Count, waveStart + DAY_CHANGE_HARVEST_CRATES_PER_WAVE);
                    List<DayChangeFarmerWaveState> waveStates = new List<DayChangeFarmerWaveState>(waveEnd - waveStart);

                    for (int crateIndex = waveStart; crateIndex < waveEnd; crateIndex++)
                    {
                        waveStates.Add(new DayChangeFarmerWaveState
                        {
                            chest = crates[crateIndex]
                        });
                    }

                    while (true)
                    {
                        bool didAnyWork = false;

                        for (int crateIndex = 0; crateIndex < waveStates.Count; crateIndex++)
                        {
                            DayChangeFarmerWaveState state = waveStates[crateIndex];
                            if (state == null || state.chest == null)
                                continue;

                            int workDone = TryProcessBundledCropReplantStep(state.chest);
                            if (workDone <= 0)
                                workDone = TryProcessFarmTileCore(state.chest, ignoreSuspension: true);

                            if (workDone > 0)
                                didAnyWork = true;
                        }

                        if (ProcessPendingFarmActions(ignoreSuspension: true) > 0)
                            didAnyWork = true;

                        if (!didAnyWork &&
                            !HasPendingFarmActionsForAny(crates, waveStart, waveEnd) &&
                            !HasPendingLoggingCollectionForAny(crates, waveStart, waveEnd))
                        {
                            break;
                        }

                        yield return new WaitForSecondsRealtime(DAY_CHANGE_HARVEST_STEP_DELAY_SECONDS);
                    }

                    if (waveEnd < crates.Count && DAY_CHANGE_HARVEST_WAVE_DELAY_SECONDS > 0f)
                        yield return new WaitForSecondsRealtime(DAY_CHANGE_HARVEST_WAVE_DELAY_SECONDS);
                }
            }
            finally
            {
                doneFlag[0] = true;
            }
        }

        public static bool IsOwnedByVacuumCrate(int xPos, int yPos, HouseDetails inside, TileObject tileObj, TileObjectGrowthStages growth)
        {
            if (inside != null || tileObj == null || growth == null || WorldManager.Instance == null)
                return false;

            if (IsFarmCropTile(xPos, yPos, tileObj, growth))
                return true;

            if (!IsVacuumHarvestCandidate(xPos, yPos, tileObj, growth, WorldManager.Instance.onTileStatusMap[xPos, yPos]))
                return false;

            return HasVacuumCrateInHarvestRange(xPos, yPos);
        }

        public static Chest FindOwningVacuumCrate(int xPos, int yPos, TileObject tileObj, TileObjectGrowthStages growth)
        {
            if (tileObj == null || growth == null || WorldManager.Instance == null)
                return null;

            if (!IsVacuumHarvestCandidate(xPos, yPos, tileObj, growth, WorldManager.Instance.onTileStatusMap[xPos, yPos]))
                return null;

            int mapW = WorldManager.Instance.onTileMap.GetLength(0);
            int mapH = WorldManager.Instance.onTileMap.GetLength(1);

            int minX = Mathf.Max(0, xPos - VACUUM_HARVEST_RADIUS_TILES);
            int maxX = Mathf.Min(mapW - 1, xPos + VACUUM_HARVEST_RADIUS_TILES);
            int minY = Mathf.Max(0, yPos - VACUUM_HARVEST_RADIUS_TILES);
            int maxY = Mathf.Min(mapH - 1, yPos + VACUUM_HARVEST_RADIUS_TILES);

            Chest bestChest = null;
            int bestDistance = int.MaxValue;

            for (int x = minX; x <= maxX; x++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    if (WorldManager.Instance.onTileMap[x, y] != Plugin.GREEN_CRATE_TILE_ID)
                        continue;

                    Chest chest = ConveyorHelper.FindChestAt(x, y, null);
                    if (chest == null)
                        continue;

                    int distance = Mathf.Abs(x - xPos) + Mathf.Abs(y - yPos);
                    if (bestChest == null || distance < bestDistance ||
                        (distance == bestDistance && (y < bestChest.yPos || (y == bestChest.yPos && x < bestChest.xPos))))
                    {
                        bestChest = chest;
                        bestDistance = distance;
                    }
                }
            }

            return bestChest;
        }

        private static bool HasVacuumCrateInHarvestRange(int xPos, int yPos)
        {
            int mapW = WorldManager.Instance.onTileMap.GetLength(0);
            int mapH = WorldManager.Instance.onTileMap.GetLength(1);

            int minX = Mathf.Max(0, xPos - VACUUM_HARVEST_RADIUS_TILES);
            int maxX = Mathf.Min(mapW - 1, xPos + VACUUM_HARVEST_RADIUS_TILES);
            int minY = Mathf.Max(0, yPos - VACUUM_HARVEST_RADIUS_TILES);
            int maxY = Mathf.Min(mapH - 1, yPos + VACUUM_HARVEST_RADIUS_TILES);

            for (int x = minX; x <= maxX; x++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    if (WorldManager.Instance.onTileMap[x, y] == Plugin.GREEN_CRATE_TILE_ID)
                        return true;
                }
            }

            return false;
        }

        public static float TryVacuumNearbyDrops(Chest chest, HouseDetails inside)
        {
            return TryVacuumNearbyDrops(chest, inside, null);
        }

        private static float TryVacuumNearbyDrops(Chest chest, HouseDetails inside, PendingLoggingCollection loggingCollection)
        {
            if (chest == null || !ConveyorHelper.IsVacuumCrate(chest.xPos, chest.yPos, inside))
                return 0f;

            if (WorldManager.Instance == null || WorldManager.Instance.itemsOnGround == null)
                return 0f;

            ConveyorHelper.EnsureAutomationChestsActive(chest);
            bool collectToChestOnly = IsHarvestCollecting(chest);

            List<DroppedItem> candidates = new List<DroppedItem>();
            List<DroppedItem> itemsOnGround = WorldManager.Instance.itemsOnGround;

            for (int i = 0; i < itemsOnGround.Count; i++)
            {
                DroppedItem drop = itemsOnGround[i];
                if (drop == null || drop.myItemId < 0 || drop.stackAmount <= 0)
                    continue;

                if (IsVehicleItem(drop.myItemId))
                    continue;

                if (!drop.IsDropOnCurrentLevel() || pendingDrops.Contains(drop.netId))
                    continue;

                if (!IsDropInSameSpace(drop, inside))
                    continue;

                PendingLoggingCollection loggingOwner = FindLoggingDropOwner(drop);
                if (loggingCollection == null)
                {
                    if (loggingOwner != null)
                        continue;
                }
                else if (loggingOwner != loggingCollection)
                {
                    continue;
                }

                Vector2Int dropTile = GetDropTile(drop);
                if (loggingCollection == null &&
                    (Mathf.Abs(dropTile.x - chest.xPos) > VACUUM_PICKUP_RADIUS_TILES ||
                     Mathf.Abs(dropTile.y - chest.yPos) > VACUUM_PICKUP_RADIUS_TILES))
                {
                    continue;
                }

                if (!CanVacuumDeposit(chest, inside, drop.myItemId))
                    continue;

                candidates.Add(drop);
            }

            List<VacuumDropGroup> groups = BuildDropGroups(candidates);
            if (groups.Count == 0)
                return 0f;

            Vector3 chestWorld = GetVacuumTargetWorld(chest.xPos, chest.yPos);
            groups.Sort((a, b) =>
            {
                float distA = (a.startWorld - chestWorld).sqrMagnitude;
                float distB = (b.startWorld - chestWorld).sqrMagnitude;
                return distA.CompareTo(distB);
            });

            float delay = 0f;
            float longestAnimation = 0f;
            int collected = 0;

            for (int i = 0; i < groups.Count; i++)
            {
                VacuumDropGroup group = groups[i];
                if (group == null || group.totalStackAmount <= 0 || group.itemId < 0)
                    continue;

                int removedStackAmount = 0;
                List<uint> removedDropIds = new List<uint>();
                for (int dropIndex = 0; dropIndex < group.drops.Count; dropIndex++)
                {
                    DroppedItem drop = group.drops[dropIndex];
                    if (drop == null || !pendingDrops.Add(drop.netId))
                        continue;

                    if (!TryRemoveDropFromWorld(drop))
                    {
                        pendingDrops.Remove(drop.netId);
                        continue;
                    }

                    removedStackAmount += drop.stackAmount;
                    removedDropIds.Add(drop.netId);
                }

                if (removedStackAmount <= 0)
                    continue;

                int itemId = group.itemId;
                int stackAmount = removedStackAmount;
                int tallyType = group.tallyType;
                Vector3 startWorld = group.startWorld;

                float distance = Vector3.Distance(startWorld, chestWorld);
                float duration = Mathf.Clamp(0.15f + distance * 0.03f, 0.18f, 0.55f);
                float arcHeight = Mathf.Clamp(0.3f + distance * 0.06f, 0.35f, 1.4f);

                System.Action onArrival = () =>
                {
                    for (int removedIndex = 0; removedIndex < removedDropIds.Count; removedIndex++)
                    {
                        pendingDrops.Remove(removedDropIds[removedIndex]);
                    }

                    System.Action grantCredit = () =>
                    {
                        AutomationCreditHelper.TryGrantDroppedItemPickupCredit(itemId, stackAmount, tallyType);
                    };

                    if (HarvestHelper.TryDepositHarvestToChest(chest, inside, itemId, stackAmount, grantCredit))
                        return;

                    if (collectToChestOnly)
                    {
                        SpawnFallbackGroundDrop(itemId, stackAmount, tallyType, chestWorld, inside);
                        return;
                    }

                    ConveyorHelper.OutputDestination networkDestination;
                    if (ConveyorHelper.TryFindBestOutputDestinationFromNetworkAnchor(chest, inside, itemId, includeAnchorChest: false, excludeVacuumCrates: true, out networkDestination))
                    {
                        FilterCrateHelper.MarkRouteActive(networkDestination.routeX, networkDestination.routeY, inside);
                        Chest capturedSourceChest = chest;
                        Chest capturedNetworkChest = networkDestination.chest;
                        int capturedRouteX = networkDestination.routeX;
                        int capturedRouteY = networkDestination.routeY;
                        System.Action depositToNetwork = () =>
                        {
                            if (HarvestHelper.TryDepositHarvestToChest(capturedNetworkChest, inside, itemId, stackAmount, grantCredit))
                                return;

                            if (ConveyorHelper.FallbackDepositToAnyChest(capturedNetworkChest.xPos, capturedNetworkChest.yPos, inside, itemId, stackAmount, grantCredit))
                                return;

                            if (HarvestHelper.TryDepositHarvestToChest(capturedSourceChest, inside, itemId, stackAmount, grantCredit))
                                return;

                            SpawnFallbackGroundDrop(itemId, stackAmount, tallyType, chestWorld, inside);
                        };

                        ConveyorAnimator.AnimateTransfer(itemId, stackAmount,
                            new Vector2Int(chest.xPos, chest.yPos),
                            new Vector2Int(capturedRouteX, capturedRouteY),
                            inside, depositToNetwork);
                        return;
                    }

                    if (ConveyorHelper.FallbackDepositToAnyChest(chest.xPos, chest.yPos, inside, itemId, stackAmount, grantCredit))
                        return;

                    SpawnFallbackGroundDrop(itemId, stackAmount, tallyType, chestWorld, inside);
                };

                ConveyorAnimator.AnimateArcTransfer(itemId, stackAmount, startWorld, chestWorld, duration, arcHeight, onArrival, delay);
                longestAnimation = Mathf.Max(longestAnimation, delay + duration);
                delay += VACUUM_ITEM_DELAY;
                collected++;
            }

            if (collected > 0)
            {
                MarkCrateActive(chest);
                TriggerVacuumVisual(chest.xPos, chest.yPos, longestAnimation + 0.2f);
                Plugin.LogVerbose("Autom8er: Vacuum crate at " + chest.xPos + "," + chest.yPos + " collected " + collected + " drop(s).");
            }

            return longestAnimation;
        }

        public static bool TryFlushStoredItemsToNetwork(Chest chest, HouseDetails inside)
        {
            bool hasPendingFlush;
            return TryFlushStoredItemsToNetwork(chest, inside, out hasPendingFlush);
        }

        private static bool TryFlushStoredItemsToNetwork(Chest chest, HouseDetails inside, out bool hasPendingFlush)
        {
            hasPendingFlush = false;

            if (chest == null || inside != null || !ConveyorHelper.IsVacuumCrate(chest.xPos, chest.yPos, inside))
                return false;

            if (IsHarvestCollecting(chest))
                return false;

            ConveyorHelper.EnsureAutomationChestsActive(chest);

            for (int slot = 0; slot < chest.itemIds.Length; slot++)
            {
                int itemId = chest.itemIds[slot];
                int stackAmount = chest.itemStacks[slot];

                if (itemId < 0 || stackAmount <= 0)
                    continue;

                int protectedAmount = GetProtectedAutoFarmerAmount(chest, itemId);
                int transferableAmount = CountItemInChest(chest, itemId) - protectedAmount;
                if (transferableAmount <= 0)
                    continue;

                ConveyorHelper.OutputDestination networkDestination;
                if (!ConveyorHelper.TryFindBestOutputDestinationFromNetworkAnchor(chest, inside, itemId, includeAnchorChest: false, excludeVacuumCrates: true, out networkDestination))
                    continue;

                hasPendingFlush = true;
                if (!ShouldFlushStackNow(chest, itemId, transferableAmount))
                    continue;

                FilterCrateHelper.MarkRouteActive(networkDestination.routeX, networkDestination.routeY, inside);
                bool isDurabilityTransfer = Plugin.IsDurabilityTransferItem(itemId);
                int moveAmount = isDurabilityTransfer
                    ? stackAmount
                    : Mathf.Min(GetTransferChunkSize(chest, itemId, transferableAmount), Mathf.Min(stackAmount, transferableAmount));
                int sourceSlot = slot;
                int capturedItemId = itemId;
                int capturedMoveAmount = moveAmount;
                Chest capturedSourceChest = chest;
                Chest capturedNetworkChest = networkDestination.chest;
                int capturedRouteX = networkDestination.routeX;
                int capturedRouteY = networkDestination.routeY;

                Plugin.RemoveTransferredItemFromChest(chest, sourceSlot, itemId, moveAmount, inside);

                ClearPendingFlush(chest, capturedItemId);

                System.Action depositToNetwork = () =>
                {
                    if (HarvestHelper.TryDepositHarvestToChest(capturedNetworkChest, inside, capturedItemId, capturedMoveAmount))
                        return;

                    ConveyorHelper.OutputDestination fallbackDestination;
                    if (ConveyorHelper.TryFindBestOutputDestinationFromNetworkAnchor(capturedSourceChest, inside, capturedItemId, includeAnchorChest: false, excludeVacuumCrates: true, out fallbackDestination) &&
                        fallbackDestination != null &&
                        !(fallbackDestination.chest.xPos == capturedNetworkChest.xPos && fallbackDestination.chest.yPos == capturedNetworkChest.yPos))
                    {
                        if (HarvestHelper.TryDepositHarvestToChest(fallbackDestination.chest, inside, capturedItemId, capturedMoveAmount))
                            return;
                    }

                    if (HarvestHelper.TryDepositHarvestToChest(capturedSourceChest, inside, capturedItemId, capturedMoveAmount))
                        return;

                    // Terminal fallback: any reachable chest, then a real networked ground
                    // drop - a full network must never silently destroy a flushed chunk.
                    if (!ConveyorHelper.FallbackDepositToAnyChest(capturedSourceChest.xPos, capturedSourceChest.yPos, inside, capturedItemId, capturedMoveAmount))
                    {
                        Vector3 crateWorld = new Vector3(capturedSourceChest.xPos * 2, WorldManager.Instance.heightMap[capturedSourceChest.xPos, capturedSourceChest.yPos], capturedSourceChest.yPos * 2);
                        SpawnFallbackGroundDrop(capturedItemId, capturedMoveAmount, -1, crateWorld, inside);
                    }
                };

                ConveyorAnimator.AnimateTransfer(capturedItemId, capturedMoveAmount,
                    new Vector2Int(chest.xPos, chest.yPos),
                    new Vector2Int(capturedRouteX, capturedRouteY),
                    inside, depositToNetwork);

                MarkCrateActive(chest);
                return true;
            }

            return false;
        }

        public static void TryProcessFarmTile(Chest chest, HouseDetails inside)
        {
            if (chest == null || inside != null || !ConveyorHelper.IsVacuumCrate(chest.xPos, chest.yPos, inside))
                return;

            if (WorldManager.Instance == null || NetworkMapSharer.Instance == null)
                return;

            if (AreFarmerActionsSuspended())
                return;

            TryProcessFarmTileCore(chest, ignoreSuspension: false);
        }

        private static int TryProcessFarmTileCore(Chest chest, bool ignoreSuspension)
        {
            if (chest == null)
                return 0;

            if (!ignoreSuspension && AreFarmerActionsSuspended())
                return 0;

            if (HasAxeTool(chest))
                return TryProcessLoggingStep(chest);

            int actionsCompleted = 0;
            for (int action = 0; action < FARM_ACTIONS_PER_SCAN; action++)
            {
                if (!TryProcessOneFarmAction(chest))
                    break;

                actionsCompleted++;
            }

            return actionsCompleted;
        }

        private static int TryProcessLoggingStep(Chest chest)
        {
            EnsureLoggingCrateInstance(chest);
            PruneExpiredLoggingCollections();

            long chestKey = PosKey(chest.xPos, chest.yPos);
            PendingLoggingCollection pendingCollection;
            if (pendingLoggingCollections.TryGetValue(chestKey, out pendingCollection))
            {
                if (Time.time < pendingCollection.collectAt)
                    return 0;

                CaptureLoggingDropIds(pendingCollection, pendingCollection.spawnedDropIds, pendingCollection.existingDropIds);
                TryVacuumNearbyDrops(chest, null, pendingCollection);
                pendingLoggingCollections.Remove(chestKey);
                MarkCrateActive(chest);
                return 1;
            }

            LoggingActionType actionType;
            int tileX;
            int tileY;
            int tileObjectId;
            int seedSlot;
            int seedItemId;
            if (!TryFindNextLoggingAction(chest, out actionType, out tileX, out tileY, out tileObjectId, out seedSlot, out seedItemId))
                return 0;

            if (actionType == LoggingActionType.Plant)
            {
                PlantTreeAt(chest, seedSlot, seedItemId, tileX, tileY);
                MarkCrateActive(chest);
                return 1;
            }

            if (actionType != LoggingActionType.Chop || tileObjectId < 0 ||
                NetworkMapSharer.Instance == null || WorldManager.Instance == null)
            {
                return 0;
            }

            TileObjectSettings settings = WorldManager.Instance.allObjectSettings[tileObjectId];
            if (settings == null)
                return 0;

            pendingCollection = new PendingLoggingCollection
            {
                chestX = chest.xPos,
                chestY = chest.yPos,
                sourceX = tileX,
                sourceY = tileY,
                collectAt = Time.time + LOGGING_DROP_SETTLE_SECONDS,
                expiresAt = Time.time + LOGGING_COLLECTION_EXPIRY_SECONDS
            };

            CaptureLoggingDropIds(pendingCollection, pendingCollection.existingDropIds, null);
            pendingLoggingCollections[chestKey] = pendingCollection;
            NetworkMapSharer.Instance.RpcUpdateOnTileObject(settings.changeToTileObjectOnDeath, tileX, tileY);
            CaptureLoggingDropIds(pendingCollection, pendingCollection.spawnedDropIds, pendingCollection.existingDropIds);

            AutomationCreditHelper.TryGrantLoggingBreakCredit(tileObjectId);
            MarkCrateActive(chest);
            return 1;
        }

        private static bool TryFindNextLoggingAction(Chest chest, out LoggingActionType actionType,
            out int tileX, out int tileY, out int tileObjectId, out int seedSlot, out int seedItemId)
        {
            actionType = LoggingActionType.None;
            tileX = 0;
            tileY = 0;
            tileObjectId = -1;
            seedSlot = -1;
            seedItemId = GetActiveLoggingSeedItemId(chest);

            InventoryItem axe = GetBestAxeItem(chest);
            if (axe == null || WorldManager.Instance == null)
                return false;

            if (seedItemId >= 0)
            {
                int activeSeedItemId = seedItemId;
                seedSlot = FindItemSlot(chest, itemId => itemId == activeSeedItemId);
            }

            int rowLength = GetLoggingRowLength(chest, seedItemId);
            for (int rowIndex = 0; rowIndex < rowLength; rowIndex++)
            {
                int checkX;
                int checkY;
                if (!TryGetLoggingRowTile(chest, seedItemId, rowIndex, out checkX, out checkY))
                    continue;

                int checkTileObjectId = WorldManager.Instance.onTileMap[checkX, checkY];
                if (checkTileObjectId >= 0 && checkTileObjectId != 30)
                {
                    if (IsLoggingStump(checkTileObjectId, axe) || IsMatureLoggingTree(checkX, checkY, checkTileObjectId, axe))
                    {
                        actionType = LoggingActionType.Chop;
                        tileX = checkX;
                        tileY = checkY;
                        tileObjectId = checkTileObjectId;
                        return true;
                    }

                    if (seedSlot != -1 && CanPlantLoggingSeedAt(seedItemId, checkX, checkY))
                    {
                        actionType = LoggingActionType.Plant;
                        tileX = checkX;
                        tileY = checkY;
                        return true;
                    }

                    continue;
                }

                if (seedSlot != -1 && CanPlantLoggingSeedAt(seedItemId, checkX, checkY))
                {
                    actionType = LoggingActionType.Plant;
                    tileX = checkX;
                    tileY = checkY;
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetLoggingRowTile(Chest chest, int seedItemId, int rowIndex, out int tileX, out int tileY)
        {
            return TryGetLoggingTileInDirection(chest, GetLoggingDirection(chest, seedItemId), rowIndex, out tileX, out tileY);
        }

        private static bool TryGetLoggingTileInDirection(Chest chest, Vector2Int direction, int rowIndex, out int tileX, out int tileY)
        {
            tileX = 0;
            tileY = 0;
            if (chest == null || WorldManager.Instance == null || rowIndex < 0 || rowIndex >= MAX_LOGGING_ROW_LENGTH)
                return false;

            tileX = chest.xPos + direction.x * (rowIndex + 1);
            tileY = chest.yPos + direction.y * (rowIndex + 1);

            int[,] onTileMap = WorldManager.Instance.onTileMap;
            int[,] heightMap = WorldManager.Instance.heightMap;
            if (onTileMap == null || heightMap == null || tileX < 0 || tileY < 0 ||
                tileX >= onTileMap.GetLength(0) || tileY >= onTileMap.GetLength(1) ||
                tileX >= heightMap.GetLength(0) || tileY >= heightMap.GetLength(1))
            {
                return false;
            }

            return Mathf.Abs(heightMap[tileX, tileY] - heightMap[chest.xPos, chest.yPos]) <= 1;
        }

        private static Vector2Int GetLoggingDirection(Chest chest, int preferredSeedItemId)
        {
            long chestKey = PosKey(chest.xPos, chest.yPos);
            Vector2Int direction;
            if (loggingDirectionByCrate.TryGetValue(chestKey, out direction))
                return direction;

            int inferredSeedItemId;
            int inferredRowLength;
            if (TryInferExistingLoggingRow(chest, preferredSeedItemId, out direction, out inferredSeedItemId, out inferredRowLength))
            {
                loggingDirectionByCrate[chestKey] = direction;
                activeLoggingSeedByCrate[chestKey] = inferredSeedItemId;
                loggingRowLengthByCrate[chestKey] = inferredRowLength;
                return direction;
            }

            int axeSlot = GetBestAxeSlot(chest);
            if (axeSlot >= 0 && axeSlot < LOGGING_DIRECTION_COLUMNS)
                direction = new Vector2Int(0, 1);
            else if (axeSlot >= chest.itemIds.Length - LOGGING_DIRECTION_COLUMNS)
                direction = new Vector2Int(0, -1);
            else if (axeSlot >= 0 && axeSlot % LOGGING_DIRECTION_COLUMNS == 0)
                direction = new Vector2Int(-1, 0);
            else if (axeSlot >= 0 && axeSlot % LOGGING_DIRECTION_COLUMNS == LOGGING_DIRECTION_COLUMNS - 1)
                direction = new Vector2Int(1, 0);
            else
                direction = new Vector2Int(0, 1);

            loggingDirectionByCrate[chestKey] = direction;
            return direction;
        }

        private static int GetLoggingRowLength(Chest chest, int seedItemId)
        {
            if (chest == null || seedItemId < 0)
                return 0;

            long chestKey = PosKey(chest.xPos, chest.yPos);
            int rowLength;
            if (loggingRowLengthByCrate.TryGetValue(chestKey, out rowLength))
                return rowLength;

            rowLength = CountItemInChest(chest, seedItemId);
            rowLength = Mathf.Clamp(rowLength, 1, MAX_LOGGING_ROW_LENGTH);
            loggingRowLengthByCrate[chestKey] = rowLength;
            return rowLength;
        }

        private static bool TryInferExistingLoggingRow(Chest chest, int preferredSeedItemId, out Vector2Int bestDirection, out int bestSeedItemId, out int bestRowLength)
        {
            bestDirection = Vector2Int.zero;
            bestSeedItemId = -1;
            bestRowLength = 0;
            int bestScore = 0;
            Vector2Int[] directions =
            {
                new Vector2Int(0, 1),
                new Vector2Int(0, -1),
                new Vector2Int(-1, 0),
                new Vector2Int(1, 0)
            };

            for (int directionIndex = 0; directionIndex < directions.Length; directionIndex++)
            {
                int directionSeedItemId = -1;
                int matchingTrees = 0;
                int stumps = 0;
                int directionRowLength = 0;
                int firstMatchingTreeIndex = -1;
                int secondMatchingTreeIndex = -1;

                for (int rowIndex = 0; rowIndex < MAX_LOGGING_ROW_LENGTH; rowIndex++)
                {
                    int tileX;
                    int tileY;
                    if (!TryGetLoggingTileInDirection(chest, directions[directionIndex], rowIndex, out tileX, out tileY))
                        continue;

                    int tileObjectId = WorldManager.Instance.onTileMap[tileX, tileY];
                    int seedItemId = FindLoggingSeedForTreeTile(tileObjectId);
                    if (preferredSeedItemId >= 0 && seedItemId != preferredSeedItemId)
                        continue;

                    if (seedItemId >= 0 && (directionSeedItemId < 0 || seedItemId == directionSeedItemId))
                    {
                        directionSeedItemId = seedItemId;
                        matchingTrees++;
                        if (firstMatchingTreeIndex == -1)
                            firstMatchingTreeIndex = rowIndex;
                        else if (secondMatchingTreeIndex == -1)
                            secondMatchingTreeIndex = rowIndex;
                        directionRowLength = rowIndex + 1;
                        continue;
                    }

                    if (tileObjectId >= 0 && tileObjectId < WorldManager.Instance.allObjectSettings.Length)
                    {
                        TileObjectSettings settings = WorldManager.Instance.allObjectSettings[tileObjectId];
                        if (settings != null && settings.TaskType == DailyTaskGenerator.genericTaskType.ChopDownStump)
                        {
                            stumps++;
                            directionRowLength = rowIndex + 1;
                        }
                    }
                }

                if (matchingTrees < 2 || firstMatchingTreeIndex > 1 || secondMatchingTreeIndex > 3)
                    continue;

                int score = matchingTrees * 10 + stumps;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestDirection = directions[directionIndex];
                    bestSeedItemId = directionSeedItemId;
                    bestRowLength = directionRowLength;
                }
            }

            return bestScore > 0;
        }

        private static int FindLoggingSeedForTreeTile(int tileObjectId)
        {
            if (!IsLoggingTreeObject(tileObjectId) || Inventory.Instance == null)
                return -1;

            for (int itemId = 0; itemId < Inventory.Instance.allItems.Length; itemId++)
            {
                if (!IsLoggingTreePlantingItem(itemId))
                    continue;

                if (LoggingSeedProducesTreeTile(itemId, tileObjectId))
                    return itemId;
            }

            return -1;
        }

        private static bool LoggingSeedProducesTreeTile(int seedItemId, int tileObjectId)
        {
            if (!IsLoggingTreePlantingItem(seedItemId))
                return false;

            TileObject treeObject = Inventory.Instance.allItems[seedItemId].placeable;
            for (int stage = 0; stage < MAX_LOGGING_GROWTH_CHAIN && treeObject != null; stage++)
            {
                if (treeObject.tileObjectId == tileObjectId)
                    return true;

                TileObjectGrowthStages growth = treeObject.tileObjectGrowthStages;
                if (growth == null || !growth.changeToWhenGrown || growth.changeToWhenGrown == treeObject)
                    break;

                treeObject = growth.changeToWhenGrown;
            }

            return false;
        }

        private static void CaptureLoggingDropIds(PendingLoggingCollection collection, HashSet<uint> destination, HashSet<uint> excluded)
        {
            if (collection == null || destination == null || WorldManager.Instance == null || WorldManager.Instance.itemsOnGround == null)
                return;

            List<DroppedItem> drops = WorldManager.Instance.itemsOnGround;
            for (int i = 0; i < drops.Count; i++)
            {
                DroppedItem drop = drops[i];
                if (drop == null || drop.netId == 0 || !drop.IsDropOnCurrentLevel() || drop.inside != null ||
                    (excluded != null && excluded.Contains(drop.netId)))
                    continue;

                Vector2Int dropTile = GetDropTile(drop);
                if (Mathf.Abs(dropTile.x - collection.sourceX) <= LOGGING_DROP_RADIUS_TILES &&
                    Mathf.Abs(dropTile.y - collection.sourceY) <= LOGGING_DROP_RADIUS_TILES)
                {
                    destination.Add(drop.netId);
                }
            }
        }

        internal static HashSet<uint> BeginLoggingDropCapture(int sourceX, int sourceY, HouseDetails inside)
        {
            if (inside != null || FindPendingLoggingCollectionAtSource(sourceX, sourceY) == null ||
                WorldManager.Instance == null || WorldManager.Instance.itemsOnGround == null)
            {
                return null;
            }

            HashSet<uint> existingDropIds = new HashSet<uint>();
            List<DroppedItem> drops = WorldManager.Instance.itemsOnGround;
            for (int i = 0; i < drops.Count; i++)
            {
                DroppedItem drop = drops[i];
                if (drop != null && drop.netId != 0 && drop.inside == null && drop.IsDropOnCurrentLevel())
                    existingDropIds.Add(drop.netId);
            }

            return existingDropIds;
        }

        internal static void CompleteLoggingDropCapture(int sourceX, int sourceY, HouseDetails inside, HashSet<uint> existingDropIds)
        {
            if (inside != null || existingDropIds == null || WorldManager.Instance == null || WorldManager.Instance.itemsOnGround == null)
                return;

            PendingLoggingCollection collection = FindPendingLoggingCollectionAtSource(sourceX, sourceY);
            if (collection == null)
                return;

            List<DroppedItem> drops = WorldManager.Instance.itemsOnGround;
            for (int i = 0; i < drops.Count; i++)
            {
                DroppedItem drop = drops[i];
                if (drop != null && drop.netId != 0 && drop.inside == null && drop.IsDropOnCurrentLevel() &&
                    !existingDropIds.Contains(drop.netId))
                {
                    collection.spawnedDropIds.Add(drop.netId);
                }
            }
        }

        private static PendingLoggingCollection FindPendingLoggingCollectionAtSource(int sourceX, int sourceY)
        {
            foreach (KeyValuePair<long, PendingLoggingCollection> entry in pendingLoggingCollections)
            {
                PendingLoggingCollection collection = entry.Value;
                if (collection != null && collection.sourceX == sourceX && collection.sourceY == sourceY &&
                    Time.time <= collection.expiresAt)
                {
                    return collection;
                }
            }

            return null;
        }

        private static bool TryProcessOneFarmAction(Chest chest)
        {
            bool hasHoeTool = HasHoeTool(chest);
            int instaGrowSlot = FindItemSlot(chest, IsGrowthBoosterItem);
            if (instaGrowSlot != -1 && TryFindBestFarmTile(chest, CanApplyInstaGrowTile, out FarmTileTarget growTarget))
            {
                ApplyInstaGrowAt(chest, instaGrowSlot, growTarget.xPos, growTarget.yPos);
                MarkCrateActive(chest);
                return true;
            }

            int fertilizerSlot = FindFertilizerSlot(chest);
            bool hasFertilizer = fertilizerSlot != -1;

            if (hasHoeTool && hasFertilizer && TryFindBestFarmTile(chest, CanFertilizeTile, out FarmTileTarget fertilizeTarget))
            {
                ApplyFertilizerAt(chest, fertilizerSlot, fertilizeTarget.xPos, fertilizeTarget.yPos);
                MarkCrateActive(chest);
                int seedItemId = GetFirstCropSeedItemId(chest);
                if (seedItemId >= 0)
                    QueueFollowUpFarmActions(chest, fertilizeTarget.xPos, fertilizeTarget.yPos, queueFertilizer: false, seedItemId);
                return true;
            }

            if (hasHoeTool)
            {
                for (int slot = 0; slot < chest.itemIds.Length; slot++)
                {
                    int itemId = chest.itemIds[slot];
                    if (!IsCropSeedItem(itemId))
                        continue;

                    if (!TryFindBestFarmTile(chest, (xPos, yPos) => CanPlantSeedAt(itemId, xPos, yPos), out FarmTileTarget plantTarget))
                        continue;

                    PlantSeedAt(chest, slot, itemId, plantTarget.xPos, plantTarget.yPos);
                    MarkCrateActive(chest);
                    return true;
                }
            }

            bool shouldTill = hasHoeTool && (hasFertilizer || HasAnyCropSeed(chest));
            if (shouldTill && TryFindBestFarmTile(chest, CanHoeTile, out FarmTileTarget hoeTarget))
            {
                HoeTileAt(hoeTarget.xPos, hoeTarget.yPos);
                MarkCrateActive(chest);
                int seedItemId = GetFirstCropSeedItemId(chest);
                QueueFollowUpFarmActions(chest, hoeTarget.xPos, hoeTarget.yPos, hasFertilizer, seedItemId);
                return true;
            }

            if (!HasShovelTool(chest))
                return false;

            for (int slot = 0; slot < chest.itemIds.Length; slot++)
            {
                int itemId = chest.itemIds[slot];
                if (!IsTreePlantingItem(itemId))
                    continue;

                if (!TryFindBestTreePlantTile(chest, itemId, out FarmTileTarget treeTarget))
                    continue;

                PlantTreeAt(chest, slot, itemId, treeTarget.xPos, treeTarget.yPos);
                MarkCrateActive(chest);
                return true;
            }

            return false;
        }

        private static int TryProcessBundledCropReplantStep(Chest chest)
        {
            if (chest == null || !HasHoeTool(chest) || HasAxeTool(chest))
                return 0;

            int processedTiles = 0;

            for (int action = 0; action < FARM_ACTIONS_PER_SCAN; action++)
            {
                int seedItemId = GetFirstCropSeedItemId(chest);
                bool hasSeed = seedItemId >= 0;
                bool hasFertilizer = FindFertilizerSlot(chest) != -1;

                if (!hasSeed && !hasFertilizer)
                    break;

                if (!TryFindBestBundledReplantTile(chest, seedItemId, hasFertilizer, out ReplantTilePlan plan))
                    break;

                bool didWork = false;

                if (plan.shouldHoe)
                {
                    HoeTileAt(plan.xPos, plan.yPos);
                    didWork = true;
                }

                if (plan.shouldFertilize)
                {
                    int fertilizerSlot = FindFertilizerSlot(chest);
                    if (fertilizerSlot != -1 && CanFertilizeEmptyTile(plan.xPos, plan.yPos))
                    {
                        ApplyFertilizerAt(chest, fertilizerSlot, plan.xPos, plan.yPos);
                        didWork = true;
                    }
                }

                if (plan.seedItemId >= 0)
                {
                    int seedSlot = FindItemSlot(chest, itemId => itemId == plan.seedItemId);
                    bool canPlantSeed = (plan.shouldHoe || plan.shouldFertilize)
                        ? CanPlantSeedAtNow(plan.seedItemId, plan.xPos, plan.yPos)
                        : CanPlantSeedAt(plan.seedItemId, plan.xPos, plan.yPos);
                    if (seedSlot != -1 && canPlantSeed)
                    {
                        PlantSeedAt(chest, seedSlot, plan.seedItemId, plan.xPos, plan.yPos);
                        didWork = true;
                    }
                }

                if (!didWork)
                    break;

                MarkCrateActive(chest);
                processedTiles++;
            }

            return processedTiles;
        }

        private static bool TryFindBestBundledReplantTile(Chest chest, int seedItemId, bool hasFertilizer, out ReplantTilePlan bestPlan)
        {
            bestPlan = null;
            if (chest == null || WorldManager.Instance == null)
                return false;

            bool hasSeed = seedItemId >= 0;
            if (!hasSeed && !hasFertilizer)
                return false;

            int[,] onTileMap = WorldManager.Instance.onTileMap;
            int[,] tileTypeMap = WorldManager.Instance.tileTypeMap;
            if (onTileMap == null || tileTypeMap == null)
                return false;

            int mapW = Mathf.Min(onTileMap.GetLength(0), tileTypeMap.GetLength(0));
            int mapH = Mathf.Min(onTileMap.GetLength(1), tileTypeMap.GetLength(1));
            int minX = Mathf.Max(0, chest.xPos - VACUUM_HARVEST_RADIUS_TILES);
            int maxX = Mathf.Min(mapW - 1, chest.xPos + VACUUM_HARVEST_RADIUS_TILES);
            int minY = Mathf.Max(0, chest.yPos - VACUUM_HARVEST_RADIUS_TILES);
            int maxY = Mathf.Min(mapH - 1, chest.yPos + VACUUM_HARVEST_RADIUS_TILES);

            for (int x = minX; x <= maxX; x++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    if (!TryBuildBundledReplantPlan(chest, seedItemId, hasFertilizer, x, y, out ReplantTilePlan candidate))
                        continue;

                    if (bestPlan == null ||
                        candidate.priorityScore > bestPlan.priorityScore ||
                        (candidate.priorityScore == bestPlan.priorityScore && candidate.distance < bestPlan.distance) ||
                        (candidate.priorityScore == bestPlan.priorityScore && candidate.distance == bestPlan.distance &&
                            (y < bestPlan.yPos || (y == bestPlan.yPos && x < bestPlan.xPos))))
                    {
                        bestPlan = candidate;
                    }
                }
            }

            return bestPlan != null;
        }

        private static bool TryBuildBundledReplantPlan(Chest chest, int seedItemId, bool hasFertilizer, int xPos, int yPos, out ReplantTilePlan plan)
        {
            plan = null;
            if (chest == null || !IsOutdoorFarmTileInRange(xPos, yPos))
                return false;

            bool hasSeed = seedItemId >= 0;
            bool canHoe = CanHoeTile(xPos, yPos);
            bool canFertilize = hasFertilizer && CanFertilizeEmptyTile(xPos, yPos);
            bool canPlant = hasSeed && CanPlantSeedAt(seedItemId, xPos, yPos);

            bool canHoeThenFertilize = false;
            bool canHoeThenPlant = false;

            if (canHoe)
            {
                int tilledTileType = WeatherManager.Instance != null && WeatherManager.Instance.IsRaining ? 8 : 7;
                canHoeThenFertilize = hasFertilizer && IsEmptyFarmObject(xPos, yPos) && (tilledTileType == 7 || tilledTileType == 8);
                canHoeThenPlant = CanHoeThenPlantSeedAt(seedItemId, xPos, yPos);
            }

            bool shouldHoe = false;
            bool shouldFertilize = false;
            int plannedSeedItemId = -1;

            if (canHoe && (canHoeThenFertilize || canHoeThenPlant || (!hasSeed && !hasFertilizer)))
            {
                shouldHoe = true;
                shouldFertilize = canHoeThenFertilize;
                if (canHoeThenPlant)
                    plannedSeedItemId = seedItemId;
            }
            else if (canFertilize || canPlant)
            {
                shouldFertilize = canFertilize;
                if (canPlant)
                    plannedSeedItemId = seedItemId;
            }

            if (!shouldHoe && !shouldFertilize && plannedSeedItemId < 0)
                return false;

            int priority = 0;
            if (shouldHoe)
                priority += 1;
            if (shouldFertilize)
                priority += 2;
            if (plannedSeedItemId >= 0)
                priority += 4;

            plan = new ReplantTilePlan
            {
                xPos = xPos,
                yPos = yPos,
                distance = Mathf.Abs(xPos - chest.xPos) + Mathf.Abs(yPos - chest.yPos),
                shouldHoe = shouldHoe,
                shouldFertilize = shouldFertilize,
                seedItemId = plannedSeedItemId,
                priorityScore = priority
            };
            return true;
        }

        private static List<HarvestTarget> CollectNearbyHarvestTargets(Chest chest, HouseDetails inside, HashSet<long> claimedHarvestTiles)
        {
            if (chest == null || inside != null || !ConveyorHelper.IsVacuumCrate(chest.xPos, chest.yPos, inside))
                return new List<HarvestTarget>();

            if (WorldManager.Instance == null || NetworkMapSharer.Instance == null)
                return new List<HarvestTarget>();

            int[,] tileMap = WorldManager.Instance.onTileMap;
            int[,] statusMap = WorldManager.Instance.onTileStatusMap;
            int mapW = tileMap.GetLength(0);
            int mapH = tileMap.GetLength(1);

            int minX = Mathf.Max(0, chest.xPos - VACUUM_HARVEST_RADIUS_TILES);
            int maxX = Mathf.Min(mapW - 1, chest.xPos + VACUUM_HARVEST_RADIUS_TILES);
            int minY = Mathf.Max(0, chest.yPos - VACUUM_HARVEST_RADIUS_TILES);
            int maxY = Mathf.Min(mapH - 1, chest.yPos + VACUUM_HARVEST_RADIUS_TILES);

            List<HarvestTarget> targets = new List<HarvestTarget>();

            for (int x = minX; x <= maxX; x++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    int tileObjectId = tileMap[x, y];
                    if (tileObjectId < 0)
                        continue;

                    TileObject tileObj = WorldManager.Instance.allObjects[tileObjectId];
                    if (tileObj == null || tileObj.tileObjectGrowthStages == null)
                        continue;

                    TileObjectGrowthStages growth = tileObj.tileObjectGrowthStages;
                    if (!IsVacuumHarvestCandidate(x, y, tileObj, growth, statusMap[x, y]))
                        continue;

                    long tileKey = PosKey(x, y);
                    if (claimedHarvestTiles != null && !claimedHarvestTiles.Add(tileKey))
                        continue;

                    targets.Add(new HarvestTarget
                    {
                        xPos = x,
                        yPos = y,
                        tileObj = tileObj,
                        growth = growth
                    });
                }
            }

            targets.Sort((a, b) => CompareHarvestTargets(chest, a, b));

            return targets;
        }

        private static int CompareHarvestTargets(Chest chest, HarvestTarget a, HarvestTarget b)
        {
            if (chest == null)
                return 0;

            int absDxA = Mathf.Abs(a.xPos - chest.xPos);
            int absDyA = Mathf.Abs(a.yPos - chest.yPos);
            int absDxB = Mathf.Abs(b.xPos - chest.xPos);
            int absDyB = Mathf.Abs(b.yPos - chest.yPos);

            int ringA = Mathf.Max(absDxA, absDyA);
            int ringB = Mathf.Max(absDxB, absDyB);
            if (ringA != ringB)
                return ringA.CompareTo(ringB);

            bool axisAlignedA = absDxA == 0 || absDyA == 0;
            bool axisAlignedB = absDxB == 0 || absDyB == 0;
            if (axisAlignedA != axisAlignedB)
                return axisAlignedA ? -1 : 1;

            int distA = absDxA + absDyA;
            int distB = absDxB + absDyB;
            if (distA != distB)
                return distA.CompareTo(distB);

            if (a.yPos != b.yPos)
                return a.yPos.CompareTo(b.yPos);

            return a.xPos.CompareTo(b.xPos);
        }

        public static void UpdateVisuals()
        {
            ProcessPendingFarmActions();
            ClearExpiredFarmReservations();

            if (activeVisuals.Count == 0)
                return;

            List<long> expired = null;

            foreach (KeyValuePair<long, VacuumVisualState> entry in activeVisuals)
            {
                VacuumVisualState state = entry.Value;
                if (state.visual == null || Time.time >= state.expireAt)
                {
                    if (expired == null)
                        expired = new List<long>();
                    expired.Add(entry.Key);
                    continue;
                }

                Vector3 basePos = ConveyorAnimator.TileToWorld(state.tileX, state.tileY);
                state.visual.transform.position = new Vector3(basePos.x, basePos.y + VACUUM_VISUAL_HEIGHT, basePos.z);
            }

            if (expired == null)
                return;

            for (int i = 0; i < expired.Count; i++)
            {
                long key = expired[i];
                if (!activeVisuals.TryGetValue(key, out VacuumVisualState state))
                    continue;

                if (state.visual != null)
                    GameObject.Destroy(state.visual);

                activeVisuals.Remove(key);
            }
        }

        public static void ClearVisuals()
        {
            foreach (KeyValuePair<long, VacuumVisualState> entry in activeVisuals)
            {
                if (entry.Value.visual != null)
                    GameObject.Destroy(entry.Value.visual);
            }

            activeVisuals.Clear();
            pendingDrops.Clear();
            harvestCollectingCrates.Clear();
            pendingFlushStartedAt.Clear();
            reservedFarmTiles.Clear();
            pendingFarmActions.Clear();
            pendingLoggingCollections.Clear();
            activeLoggingSeedByCrate.Clear();
            loggingDirectionByCrate.Clear();
            loggingRowLengthByCrate.Clear();
            loggingCrateInstances.Clear();
            activeCrateUntil.Clear();
            farmerNetworkScaleCache.Clear();
        }

        // Full runtime-state reset for save/load (spec rule 12): pendingDrops holds Mirror
        // netIds that collide across sessions, pendingFarmActions reference the old world's
        // tiles, and a stale running flag suspends all farmer crates in the next save.
        public static void ClearState()
        {
            ClearVisuals();
            dayChangeVacuumHarvestRunning = false;
        }

        private static bool IsDropInSameSpace(DroppedItem drop, HouseDetails inside)
        {
            if (inside == null)
                return drop.inside == null;

            if (drop.inside == null)
                return false;

            return drop.inside.xPos == inside.xPos && drop.inside.yPos == inside.yPos;
        }

        private static Vector2Int GetDropTile(DroppedItem drop)
        {
            if (drop == null)
                return Vector2Int.zero;

            int tileX = Mathf.RoundToInt(drop.onTile.x);
            int tileY = Mathf.RoundToInt(drop.onTile.y);

            if (tileX == 0 && tileY == 0)
            {
                tileX = Mathf.RoundToInt(drop.transform.position.x / 2f);
                tileY = Mathf.RoundToInt(drop.transform.position.z / 2f);
            }

            return new Vector2Int(tileX, tileY);
        }

        private static PendingLoggingCollection FindLoggingDropOwner(DroppedItem drop)
        {
            if (drop == null || !drop.IsDropOnCurrentLevel() || drop.inside != null || pendingLoggingCollections.Count == 0)
                return null;

            Vector2Int dropTile = GetDropTile(drop);
            PendingLoggingCollection bestOwner = null;
            int bestDistance = int.MaxValue;
            bool bestWasCaptured = false;

            foreach (KeyValuePair<long, PendingLoggingCollection> entry in pendingLoggingCollections)
            {
                PendingLoggingCollection candidate = entry.Value;
                if (candidate == null || Time.time > candidate.expiresAt || candidate.existingDropIds.Contains(drop.netId))
                    continue;

                int deltaX = Mathf.Abs(dropTile.x - candidate.sourceX);
                int deltaY = Mathf.Abs(dropTile.y - candidate.sourceY);
                bool wasCaptured = candidate.spawnedDropIds.Contains(drop.netId);
                if (!wasCaptured && (deltaX > LOGGING_DROP_RADIUS_TILES || deltaY > LOGGING_DROP_RADIUS_TILES))
                    continue;

                int distance = deltaX * deltaX + deltaY * deltaY;
                if (bestOwner == null ||
                    (wasCaptured && !bestWasCaptured) ||
                    (wasCaptured == bestWasCaptured && distance < bestDistance) ||
                    (wasCaptured == bestWasCaptured && distance == bestDistance &&
                        (candidate.chestY < bestOwner.chestY ||
                         (candidate.chestY == bestOwner.chestY && candidate.chestX < bestOwner.chestX))))
                {
                    bestOwner = candidate;
                    bestDistance = distance;
                    bestWasCaptured = wasCaptured;
                }
            }

            return bestOwner;
        }

        private static void PruneExpiredLoggingCollections()
        {
            if (pendingLoggingCollections.Count == 0)
                return;

            List<long> expiredKeys = null;
            foreach (KeyValuePair<long, PendingLoggingCollection> entry in pendingLoggingCollections)
            {
                if (entry.Value != null && Time.time <= entry.Value.expiresAt)
                    continue;

                if (expiredKeys == null)
                    expiredKeys = new List<long>();
                expiredKeys.Add(entry.Key);
            }

            if (expiredKeys == null)
                return;

            for (int i = 0; i < expiredKeys.Count; i++)
                pendingLoggingCollections.Remove(expiredKeys[i]);
        }

        private static bool CanVacuumDeposit(Chest chest, HouseDetails inside, int itemId)
        {
            if (chest == null || itemId < 0)
                return false;

            if (ConveyorHelper.FindSlotForItem(chest, itemId) != -1)
                return true;

            if (IsHarvestCollecting(chest))
                return false;

            return ConveyorHelper.CanDepositFromPosition(chest.xPos, chest.yPos, inside, itemId);
        }

        public static bool IsVehicleItem(int itemId)
        {
            if (itemId < 0 || Inventory.Instance == null || Inventory.Instance.allItems == null || itemId >= Inventory.Instance.allItems.Length)
                return false;

            InventoryItem item = Inventory.Instance.allItems[itemId];
            return item != null &&
                item.spawnPlaceable != null &&
                item.spawnPlaceable.GetComponent<Vehicle>() != null;
        }

        private static bool HasAnyCropSeed(Chest chest)
        {
            return FindItemSlot(chest, IsCropSeedItem) != -1;
        }

        private static int GetFirstCropSeedItemId(Chest chest)
        {
            int slot = FindItemSlot(chest, IsCropSeedItem);
            return slot == -1 ? -1 : chest.itemIds[slot];
        }

        private static bool HasHoeTool(Chest chest)
        {
            return FindItemSlot(chest, IsHoeToolItem) != -1;
        }

        private static bool HasShovelTool(Chest chest)
        {
            return FindItemSlot(chest, IsShovelToolItem) != -1;
        }

        private static bool HasAxeTool(Chest chest)
        {
            return FindItemSlot(chest, IsAxeToolItem) != -1;
        }

        private static InventoryItem GetBestAxeItem(Chest chest)
        {
            int slot = GetBestAxeSlot(chest);
            return slot == -1 ? null : Inventory.Instance.allItems[chest.itemIds[slot]];
        }

        private static int GetBestAxeSlot(Chest chest)
        {
            int bestSlot = -1;
            if (chest == null || Inventory.Instance == null)
                return -1;

            for (int slot = 0; slot < chest.itemIds.Length; slot++)
            {
                int itemId = chest.itemIds[slot];
                if (itemId < 0 || chest.itemStacks[slot] <= 0 || !IsAxeToolItem(itemId))
                    continue;

                InventoryItem axe = Inventory.Instance.allItems[itemId];
                if (bestSlot == -1 ||
                    (!Inventory.Instance.allItems[chest.itemIds[bestSlot]].damageHardWood && axe.damageHardWood))
                {
                    bestSlot = slot;
                }
            }

            return bestSlot;
        }

        private static int FindItemSlot(Chest chest, System.Func<int, bool> predicate)
        {
            if (chest == null || predicate == null)
                return -1;

            for (int slot = 0; slot < chest.itemIds.Length; slot++)
            {
                int itemId = chest.itemIds[slot];
                if (itemId < 0 || chest.itemStacks[slot] <= 0)
                    continue;

                if (predicate(itemId))
                    return slot;
            }

            return -1;
        }

        private static int FindFertilizerSlot(Chest chest)
        {
            int seasonAllSlot = FindItemSlot(chest, itemId => itemId == SEASON_ALL_ITEM_ID);
            return seasonAllSlot != -1 ? seasonAllSlot : FindItemSlot(chest, itemId => itemId == FERTILIZER_ITEM_ID);
        }

        private static bool ShouldRetainForAutoFarmer(Chest chest, int itemId)
        {
            return GetProtectedAutoFarmerAmount(chest, itemId) > 0;
        }

        private static int GetProtectedAutoFarmerAmount(Chest chest, int itemId)
        {
            if (chest == null || itemId < 0)
                return 0;

            int totalAmount = CountItemInChest(chest, itemId);
            if (itemId == Plugin.WATER_TANK_ITEM_ID ||
                IsHoeToolItem(itemId) || IsShovelToolItem(itemId) || IsAxeToolItem(itemId) ||
                IsFertilizerItem(itemId) || IsGrowthBoosterItem(itemId) || IsCropSeedItem(itemId))
            {
                return totalAmount;
            }

            if (HasAxeTool(chest) && IsLoggingTreePlantingItem(itemId))
            {
                int activeSeedItemId = GetActiveLoggingSeedItemId(chest);
                return itemId == activeSeedItemId ? Mathf.Min(totalAmount, CountValidLoggingRowSlots(chest, itemId)) : 0;
            }

            if (!IsTreePlantingItem(itemId) || !HasShovelTool(chest))
                return 0;

            FarmTileTarget target;
            return TryFindBestTreePlantTile(chest, itemId, out target) ? totalAmount : 0;
        }

        private static int CountItemInChest(Chest chest, int itemId)
        {
            if (chest == null || itemId < 0)
                return 0;

            int totalAmount = 0;
            for (int slot = 0; slot < chest.itemIds.Length; slot++)
            {
                if (chest.itemIds[slot] == itemId && chest.itemStacks[slot] > 0)
                    totalAmount += chest.itemStacks[slot];
            }

            return totalAmount;
        }

        private static int CountValidLoggingRowSlots(Chest chest, int seedItemId)
        {
            if (chest == null || !IsLoggingTreePlantingItem(seedItemId) || WorldManager.Instance == null)
                return 0;

            int validSlots = 0;
            int rowLength = GetLoggingRowLength(chest, seedItemId);
            for (int rowIndex = 0; rowIndex < rowLength; rowIndex++)
            {
                int tileX;
                int tileY;
                if (!TryGetLoggingRowTile(chest, seedItemId, rowIndex, out tileX, out tileY))
                    continue;

                int tileObjectId = WorldManager.Instance.onTileMap[tileX, tileY];
                if (tileObjectId >= 0 && tileObjectId != 30 && tileObjectId < WorldManager.Instance.allObjectSettings.Length)
                {
                    TileObjectSettings settings = WorldManager.Instance.allObjectSettings[tileObjectId];
                    if (settings != null &&
                        (IsLoggingTreeObject(tileObjectId) ||
                         (settings.TaskType == DailyTaskGenerator.genericTaskType.ChopDownStump &&
                          (settings.isWood || settings.isHardWood))))
                    {
                        validSlots++;
                        continue;
                    }
                }

                if (CanPlantLoggingSeedAt(seedItemId, tileX, tileY, ignoreReservation: true))
                    validSlots++;
            }

            return validSlots;
        }

        private static bool HasImmediateWork(Chest chest)
        {
            if (chest == null)
                return false;

            EnsureLoggingCrateInstance(chest);

            if (HasPendingFarmActionsForChest(chest))
                return true;

            bool hasHoeTool = HasHoeTool(chest);
            bool hasShovelTool = HasShovelTool(chest);
            bool hasAxeTool = HasAxeTool(chest);

            if (hasAxeTool)
            {
                PruneExpiredLoggingCollections();
                if (pendingLoggingCollections.ContainsKey(PosKey(chest.xPos, chest.yPos)))
                    return true;

                LoggingActionType loggingAction;
                int loggingX;
                int loggingY;
                int loggingTileObjectId;
                int loggingSeedSlot;
                int loggingSeedItemId;
                if (TryFindNextLoggingAction(chest, out loggingAction, out loggingX, out loggingY,
                    out loggingTileObjectId, out loggingSeedSlot, out loggingSeedItemId))
                {
                    return true;
                }
            }

            for (int slot = 0; slot < chest.itemIds.Length; slot++)
            {
                int itemId = chest.itemIds[slot];
                int stackAmount = chest.itemStacks[slot];
                if (itemId < 0 || stackAmount <= 0)
                    continue;

                if (hasAxeTool)
                {
                    if (CountItemInChest(chest, itemId) > GetProtectedAutoFarmerAmount(chest, itemId))
                        return true;
                    continue;
                }

                if (hasHoeTool && IsFertilizerItem(itemId) && TryFindBestFarmTile(chest, CanFertilizeTile, out FarmTileTarget _))
                    return true;

                if (IsGrowthBoosterItem(itemId) && TryFindBestFarmTile(chest, CanApplyInstaGrowTile, out FarmTileTarget _))
                    return true;

                if (hasHoeTool && IsCropSeedItem(itemId))
                    return true;

                if (hasShovelTool && IsTreePlantingItem(itemId) && ShouldRetainForAutoFarmer(chest, itemId))
                    return true;

                if (CountItemInChest(chest, itemId) > GetProtectedAutoFarmerAmount(chest, itemId))
                    return true;
            }

            return false;
        }

        private static bool HasPendingFarmActionsForChest(Chest chest)
        {
            if (chest == null)
                return false;

            for (int i = 0; i < pendingFarmActions.Count; i++)
            {
                PendingFarmAction action = pendingFarmActions[i];
                if (action.chestX == chest.xPos && action.chestY == chest.yPos)
                    return true;
            }

            return false;
        }

        private static bool IsFertilizerItem(int itemId)
        {
            return itemId == FERTILIZER_ITEM_ID || itemId == SEASON_ALL_ITEM_ID;
        }

        private static bool IsGrowthBoosterItem(int itemId)
        {
            return itemId == INSTA_GROW_ITEM_ID;
        }

        private static bool IsHoeToolItem(int itemId)
        {
            if (itemId < 0 || itemId >= Inventory.Instance.allItems.Length)
                return false;

            InventoryItem item = Inventory.Instance.allItems[itemId];
            if (item == null || !item.isATool || !item.hasFuel || string.IsNullOrEmpty(item.itemName))
                return false;

            return item.itemName.IndexOf("Hoe", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsShovelToolItem(int itemId)
        {
            if (itemId < 0 || itemId >= Inventory.Instance.allItems.Length)
                return false;

            InventoryItem item = Inventory.Instance.allItems[itemId];
            if (item == null || !item.isATool || !item.hasFuel || string.IsNullOrEmpty(item.itemName))
                return false;

            return item.itemName.IndexOf("Shovel", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool IsAxeToolItem(int itemId)
        {
            if (itemId < 0 || Inventory.Instance == null || itemId >= Inventory.Instance.allItems.Length)
                return false;

            InventoryItem item = Inventory.Instance.allItems[itemId];
            return item != null && item.isATool && item.myAnimType == InventoryItem.typeOfAnimation.Axe && item.damageWood;
        }

        private static bool IsCropSeedItem(int itemId)
        {
            if (itemId < 0 || itemId >= Inventory.Instance.allItems.Length)
                return false;

            InventoryItem item = Inventory.Instance.allItems[itemId];
            if (item == null || item.placeable == null || item.placeable.tileObjectGrowthStages == null)
                return false;

            return item.placeable.tileObjectGrowthStages.needsTilledSoil;
        }

        private static bool IsTreePlantingItem(int itemId)
        {
            if (itemId < 0 || itemId >= Inventory.Instance.allItems.Length)
                return false;

            InventoryItem item = Inventory.Instance.allItems[itemId];
            if (item == null || !item.burriedPlaceable || item.placeable == null || item.placeable.tileObjectGrowthStages == null)
                return false;

            return !item.placeable.tileObjectGrowthStages.needsTilledSoil;
        }

        private static bool IsLoggingTreePlantingItem(int itemId)
        {
            if (!IsTreePlantingItem(itemId) || WorldManager.Instance == null)
                return false;

            InventoryItem item = Inventory.Instance.allItems[itemId];
            TileObject treeObject = item.placeable;
            for (int stage = 0; stage < MAX_LOGGING_GROWTH_CHAIN && treeObject != null; stage++)
            {
                TileObjectGrowthStages growth = treeObject.tileObjectGrowthStages;
                if (growth == null)
                    break;

                if (growth.harvestableByHand && ((bool)growth.harvestDrop || (bool)growth.dropsFromLootTable))
                    return false;

                if (!growth.changeToWhenGrown || growth.changeToWhenGrown == treeObject)
                    break;

                treeObject = growth.changeToWhenGrown;
            }

            if (treeObject == null)
                return false;

            int tileObjectId = treeObject.tileObjectId;
            if (tileObjectId < 0 || tileObjectId >= WorldManager.Instance.allObjectSettings.Length)
                return false;

            TileObjectSettings settings = WorldManager.Instance.allObjectSettings[tileObjectId];
            return settings != null &&
                (settings.isWood || settings.isHardWood) &&
                settings.TaskType == DailyTaskGenerator.genericTaskType.ChopDownTree;
        }

        private static int GetActiveLoggingSeedItemId(Chest chest)
        {
            if (chest == null)
                return -1;

            long chestKey = PosKey(chest.xPos, chest.yPos);
            int activeSeedItemId;
            bool hadActiveSeed = activeLoggingSeedByCrate.TryGetValue(chestKey, out activeSeedItemId);
            if (hadActiveSeed &&
                FindItemSlot(chest, itemId => itemId == activeSeedItemId) != -1)
            {
                return activeSeedItemId;
            }

            int seedSlot = FindItemSlot(chest, IsLoggingTreePlantingItem);
            if (seedSlot != -1)
            {
                activeSeedItemId = chest.itemIds[seedSlot];
                activeLoggingSeedByCrate[chestKey] = activeSeedItemId;
                return activeSeedItemId;
            }

            if (hadActiveSeed && CachedLoggingRowContainsSeedType(chest, activeSeedItemId))
                return activeSeedItemId;

            Vector2Int inferredDirection;
            int inferredSeedItemId;
            int inferredRowLength;
            if (TryInferExistingLoggingRow(chest, -1, out inferredDirection, out inferredSeedItemId, out inferredRowLength))
            {
                loggingDirectionByCrate[chestKey] = inferredDirection;
                activeLoggingSeedByCrate[chestKey] = inferredSeedItemId;
                loggingRowLengthByCrate[chestKey] = inferredRowLength;
                return inferredSeedItemId;
            }

            activeLoggingSeedByCrate.Remove(chestKey);
            return -1;
        }

        private static bool CachedLoggingRowContainsSeedType(Chest chest, int seedItemId)
        {
            Vector2Int direction;
            if (chest == null || !loggingDirectionByCrate.TryGetValue(PosKey(chest.xPos, chest.yPos), out direction))
                return false;

            int rowLength;
            if (!loggingRowLengthByCrate.TryGetValue(PosKey(chest.xPos, chest.yPos), out rowLength))
                rowLength = MAX_LOGGING_ROW_LENGTH;

            for (int rowIndex = 0; rowIndex < rowLength; rowIndex++)
            {
                int tileX;
                int tileY;
                if (!TryGetLoggingTileInDirection(chest, direction, rowIndex, out tileX, out tileY))
                    continue;

                if (LoggingSeedProducesTreeTile(seedItemId, WorldManager.Instance.onTileMap[tileX, tileY]))
                    return true;
            }

            return false;
        }

        private static bool IsLoggingStump(int tileObjectId, InventoryItem axe)
        {
            if (axe == null || tileObjectId < 0 || tileObjectId >= WorldManager.Instance.allObjectSettings.Length)
                return false;

            TileObjectSettings settings = WorldManager.Instance.allObjectSettings[tileObjectId];
            return settings != null &&
                settings.TaskType == DailyTaskGenerator.genericTaskType.ChopDownStump &&
                CanAxeDamageLoggingObject(axe, settings);
        }

        private static bool IsMatureLoggingTree(int xPos, int yPos, int tileObjectId, InventoryItem axe)
        {
            if (axe == null || tileObjectId < 0 || tileObjectId >= WorldManager.Instance.allObjectSettings.Length)
                return false;

            TileObjectSettings settings = WorldManager.Instance.allObjectSettings[tileObjectId];
            if (!IsLoggingTreeObject(tileObjectId) || !CanAxeDamageLoggingObject(axe, settings))
            {
                return false;
            }

            TileObject tileObject = WorldManager.Instance.allObjects[tileObjectId];
            TileObjectGrowthStages growth = tileObject != null ? tileObject.tileObjectGrowthStages : null;
            if (growth == null)
                return true;

            return growth.objectStages != null && growth.objectStages.Length > 0 &&
                WorldManager.Instance.onTileStatusMap[xPos, yPos] >= growth.objectStages.Length - 1;
        }

        private static bool IsLoggingTreeObject(int tileObjectId)
        {
            if (tileObjectId < 0 || tileObjectId >= WorldManager.Instance.allObjectSettings.Length)
                return false;

            TileObjectSettings settings = WorldManager.Instance.allObjectSettings[tileObjectId];
            if (settings == null || settings.TaskType != DailyTaskGenerator.genericTaskType.ChopDownTree ||
                (!settings.isWood && !settings.isHardWood))
            {
                return false;
            }

            TileObject tileObject = WorldManager.Instance.allObjects[tileObjectId];
            TileObjectGrowthStages growth = tileObject != null ? tileObject.tileObjectGrowthStages : null;
            return growth == null || !growth.harvestableByHand || (!growth.harvestDrop && !growth.dropsFromLootTable);
        }

        private static bool CanAxeDamageLoggingObject(InventoryItem axe, TileObjectSettings settings)
        {
            if (axe == null || settings == null)
                return false;

            if (settings.isHardWood)
                return axe.damageHardWood;

            return settings.isWood && axe.damageWood;
        }

        private static bool CanPlantLoggingSeedAt(int itemId, int xPos, int yPos, bool ignoreReservation = false)
        {
            return IsLoggingTreePlantingItem(itemId) && CanPlantTreeAt(itemId, xPos, yPos, ignoreReservation);
        }

        private static bool TryFindBestFarmTile(Chest chest, System.Func<int, int, bool> validator, out FarmTileTarget bestTarget)
        {
            bestTarget = null;
            if (chest == null || validator == null || WorldManager.Instance == null)
                return false;

            int[,] onTileMap = WorldManager.Instance.onTileMap;
            int[,] tileTypeMap = WorldManager.Instance.tileTypeMap;
            if (onTileMap == null || tileTypeMap == null)
                return false;

            int mapW = Mathf.Min(onTileMap.GetLength(0), tileTypeMap.GetLength(0));
            int mapH = Mathf.Min(onTileMap.GetLength(1), tileTypeMap.GetLength(1));
            int minX = Mathf.Max(0, chest.xPos - VACUUM_HARVEST_RADIUS_TILES);
            int maxX = Mathf.Min(mapW - 1, chest.xPos + VACUUM_HARVEST_RADIUS_TILES);
            int minY = Mathf.Max(0, chest.yPos - VACUUM_HARVEST_RADIUS_TILES);
            int maxY = Mathf.Min(mapH - 1, chest.yPos + VACUUM_HARVEST_RADIUS_TILES);

            for (int x = minX; x <= maxX; x++)
            {
                for (int y = minY; y <= maxY; y++)
                {
                    if (!validator(x, y))
                        continue;

                    int distance = Mathf.Abs(x - chest.xPos) + Mathf.Abs(y - chest.yPos);
                    if (bestTarget == null ||
                        distance < bestTarget.distance ||
                        (distance == bestTarget.distance && (y < bestTarget.yPos || (y == bestTarget.yPos && x < bestTarget.xPos))))
                    {
                        bestTarget = new FarmTileTarget
                        {
                            xPos = x,
                            yPos = y,
                            distance = distance
                        };
                    }
                }
            }

            return bestTarget != null;
        }

        private static bool TryFindBestTreePlantTile(Chest chest, int itemId, out FarmTileTarget bestTarget)
        {
            bestTarget = null;
            if (chest == null || !IsTreePlantingItem(itemId) || WorldManager.Instance == null)
                return false;

            int[,] onTileMap = WorldManager.Instance.onTileMap;
            int[,] tileTypeMap = WorldManager.Instance.tileTypeMap;
            if (onTileMap == null || tileTypeMap == null)
                return false;

            int mapW = Mathf.Min(onTileMap.GetLength(0), tileTypeMap.GetLength(0));
            int mapH = Mathf.Min(onTileMap.GetLength(1), tileTypeMap.GetLength(1));

            FarmTileTarget foundTarget = null;
            bool found = VisitFarmTilesInPriorityOrder(chest, mapW, mapH, (x, y) =>
            {
                if (!CanPlantTreeAt(itemId, x, y))
                    return false;

                foundTarget = new FarmTileTarget
                {
                    xPos = x,
                    yPos = y
                };
                return true;
            });

            if (found)
            {
                bestTarget = foundTarget;
                return true;
            }

            return TryFindBestFarmTile(chest, (xPos, yPos) => CanPlantTreeAt(itemId, xPos, yPos), out bestTarget);
        }

        private static bool VisitFarmTilesInPriorityOrder(Chest chest, int mapW, int mapH, System.Func<int, int, bool> visitor)
        {
            if (chest == null || visitor == null || mapW <= 0 || mapH <= 0)
                return false;

            int minX = Mathf.Max(0, chest.xPos - VACUUM_HARVEST_RADIUS_TILES);
            int maxX = Mathf.Min(mapW - 1, chest.xPos + VACUUM_HARVEST_RADIUS_TILES);
            int minY = Mathf.Max(0, chest.yPos - VACUUM_HARVEST_RADIUS_TILES);
            int maxY = Mathf.Min(mapH - 1, chest.yPos + VACUUM_HARVEST_RADIUS_TILES);

            int westEndX = Mathf.Min(chest.xPos, maxX);
            int eastStartX = Mathf.Max(chest.xPos + 1, minX);
            int northEndY = Mathf.Max(chest.yPos, minY);
            int southEndY = Mathf.Min(chest.yPos - 1, maxY);

            // Always scan the active 21x21 plot from map-northwest first so the
            // crate keeps a predictable planting order regardless of how it was moved.
            return VisitFarmQuadrant(minX, westEndX, 1, maxY, northEndY, -1, visitor) ||
                   VisitFarmQuadrant(maxX, eastStartX, -1, maxY, northEndY, -1, visitor) ||
                   VisitFarmQuadrant(minX, westEndX, 1, minY, southEndY, 1, visitor) ||
                   VisitFarmQuadrant(maxX, eastStartX, -1, minY, southEndY, 1, visitor);
        }

        private static bool VisitFarmQuadrant(int startX, int endX, int stepX, int startY, int endY, int stepY, System.Func<int, int, bool> visitor)
        {
            if (visitor == null || stepX == 0 || stepY == 0)
                return false;

            bool validXRange = stepX > 0 ? startX <= endX : startX >= endX;
            bool validYRange = stepY > 0 ? startY <= endY : startY >= endY;
            if (!validXRange || !validYRange)
                return false;

            for (int y = startY; stepY > 0 ? y <= endY : y >= endY; y += stepY)
            {
                for (int x = startX; stepX > 0 ? x <= endX : x >= endX; x += stepX)
                {
                    if (visitor(x, y))
                        return true;
                }
            }

            return false;
        }

        private static bool CanHoeTile(int xPos, int yPos)
        {
            if (!IsOutdoorFarmTileInRange(xPos, yPos))
                return false;

            int tileObjectId = WorldManager.Instance.onTileMap[xPos, yPos];
            if (tileObjectId > -1)
            {
                if (tileObjectId >= WorldManager.Instance.allObjectSettings.Length)
                    return false;

                TileObjectSettings settings = WorldManager.Instance.allObjectSettings[tileObjectId];
                if (settings == null || (!settings.isGrass && tileObjectId != 30))
                    return false;
            }

            int tileType = WorldManager.Instance.tileTypeMap[xPos, yPos];
            if (tileType == 7 || tileType == 8 || tileType == 12 || tileType == 13 || tileType == SEASON_ALL_DRY_TILE_TYPE || tileType == SEASON_ALL_WET_TILE_TYPE)
                return false;

            var tileInfo = WorldManager.Instance.tileTypes[tileType];
            if (tileInfo != null && tileInfo.dropOnChange != null)
                return false;

            return true;
        }

        private static bool CanFertilizeTile(int xPos, int yPos)
        {
            if (!IsOutdoorFarmTileInRange(xPos, yPos))
                return false;

            int tileType = WorldManager.Instance.tileTypeMap[xPos, yPos];
            return tileType == 7 || tileType == 8;
        }

        private static bool CanFertilizeEmptyTile(int xPos, int yPos)
        {
            return CanFertilizeTile(xPos, yPos) && IsEmptyFarmObject(xPos, yPos);
        }

        private static bool CanPlantSeedAt(int itemId, int xPos, int yPos)
        {
            if (!IsCropSeedItem(itemId) || !IsOutdoorFarmTileInRange(xPos, yPos))
                return false;

            int tileType = WorldManager.Instance.tileTypeMap[xPos, yPos];
            if (tileType != 7 && tileType != 8 && tileType != 12 && tileType != 13 && tileType != SEASON_ALL_DRY_TILE_TYPE && tileType != SEASON_ALL_WET_TILE_TYPE)
                return false;

            int tileObjectId = WorldManager.Instance.onTileMap[xPos, yPos];
            return tileObjectId == -1 || tileObjectId == 30;
        }

        private static bool CanHoeThenPlantSeedAt(int itemId, int xPos, int yPos)
        {
            if (!CanHoeTile(xPos, yPos))
                return false;

            int tilledTileType = WeatherManager.Instance != null && WeatherManager.Instance.IsRaining ? 8 : 7;
            return IsEmptyFarmObjectAfterHoe(xPos, yPos) && SeedCanPlantOnTilledTile(itemId, tilledTileType);
        }

        private static bool SeedCanPlantOnTilledTile(int itemId, int tileType)
        {
            if (!IsCropSeedItem(itemId))
                return false;

            return tileType == 7 || tileType == 8 || tileType == 12 || tileType == 13 || tileType == SEASON_ALL_DRY_TILE_TYPE || tileType == SEASON_ALL_WET_TILE_TYPE;
        }

        private static bool IsEmptyFarmObject(int xPos, int yPos)
        {
            int tileObjectId = WorldManager.Instance.onTileMap[xPos, yPos];
            return tileObjectId == -1 || tileObjectId == 30;
        }

        private static bool IsEmptyFarmObjectAfterHoe(int xPos, int yPos)
        {
            int tileObjectId = WorldManager.Instance.onTileMap[xPos, yPos];
            if (tileObjectId == -1 || tileObjectId == 30)
                return true;

            if (tileObjectId < 0 || tileObjectId >= WorldManager.Instance.allObjectSettings.Length)
                return false;

            TileObjectSettings settings = WorldManager.Instance.allObjectSettings[tileObjectId];
            return settings != null && settings.isGrass;
        }

        private static bool CanPlantTreeAt(int itemId, int xPos, int yPos, bool ignoreReservation = false)
        {
            if (!IsTreePlantingItem(itemId) || !IsOutdoorFarmTileInRange(xPos, yPos, ignoreReservation))
                return false;

            if (ConveyorHelper.IsConveyorTile(xPos, yPos, null))
                return false;

            InventoryItem item = Inventory.Instance.allItems[itemId];
            int tileType = WorldManager.Instance.tileTypeMap[xPos, yPos];
            if (item.canBePlacedOntoTileType != null && item.canBePlacedOntoTileType.Length > 0)
            {
                bool validTileType = false;
                for (int i = 0; i < item.canBePlacedOntoTileType.Length; i++)
                {
                    if (item.canBePlacedOntoTileType[i] == tileType)
                    {
                        validTileType = true;
                        break;
                    }
                }

                if (!validTileType)
                    return false;
            }

            int tileObjectId = WorldManager.Instance.onTileMap[xPos, yPos];
            if (tileObjectId == -1 || tileObjectId == 30)
                return true;

            if (tileObjectId < 0)
                return false;

            if (tileObjectId >= WorldManager.Instance.allObjectSettings.Length)
                return false;

            TileObjectSettings settings = WorldManager.Instance.allObjectSettings[tileObjectId];
            return settings != null && settings.isGrass;
        }

        private static bool CanApplyInstaGrowTile(int xPos, int yPos)
        {
            if (!IsOutdoorFarmTileInRange(xPos, yPos))
                return false;

            int tileObjectId = WorldManager.Instance.onTileMap[xPos, yPos];
            if (tileObjectId < 0 || tileObjectId >= WorldManager.Instance.allObjects.Length)
                return false;

            TileObjectGrowthStages growth = WorldManager.Instance.allObjects[tileObjectId].tileObjectGrowthStages;
            if (growth == null || !growth.needsTilledSoil || growth.objectStages == null || growth.objectStages.Length == 0)
                return false;

            return WorldManager.Instance.onTileStatusMap[xPos, yPos] < growth.objectStages.Length - 1;
        }

        private static bool IsOutdoorFarmTileInRange(int xPos, int yPos, bool ignoreReservation = false)
        {
            if (WorldManager.Instance == null || NetworkMapSharer.Instance == null)
                return false;

            int[,] onTileMap = WorldManager.Instance.onTileMap;
            int[,] tileTypeMap = WorldManager.Instance.tileTypeMap;
            if (onTileMap == null || tileTypeMap == null)
                return false;

            if (xPos < 0 || yPos < 0 ||
                xPos >= onTileMap.GetLength(0) || yPos >= onTileMap.GetLength(1) ||
                xPos >= tileTypeMap.GetLength(0) || yPos >= tileTypeMap.GetLength(1))
                return false;

            if (WorldManager.Instance.CheckTileClientLock(xPos, yPos))
                return false;

            if (!ignoreReservation && IsFarmTileReserved(xPos, yPos))
                return false;

            return true;
        }

        private static void HoeTileAt(int xPos, int yPos)
        {
            ReserveFarmTile(xPos, yPos, Time.time + FARM_TILE_RESERVATION_SECONDS);
            int tilledTileType = WeatherManager.Instance != null && WeatherManager.Instance.IsRaining ? 8 : 7;
            NetworkMapSharer.Instance.RpcUpdateTileType(tilledTileType, xPos, yPos);
            WorldManager.Instance.tileTypeMap[xPos, yPos] = tilledTileType;

            int tileObjectId = WorldManager.Instance.onTileMap[xPos, yPos];
            if (tileObjectId > -1 &&
                tileObjectId < WorldManager.Instance.allObjectSettings.Length &&
                WorldManager.Instance.allObjectSettings[tileObjectId] != null &&
                WorldManager.Instance.allObjectSettings[tileObjectId].isGrass)
            {
                NetworkMapSharer.Instance.RpcUpdateOnTileObject(-1, xPos, yPos);
                WorldManager.Instance.onTileMap[xPos, yPos] = -1;
            }
        }

        private static void ApplyFertilizerAt(Chest chest, int slot, int xPos, int yPos)
        {
            int fertilizerItemId = chest != null && slot >= 0 && slot < chest.itemIds.Length
                ? chest.itemIds[slot]
                : FERTILIZER_ITEM_ID;
            InventoryItem fertilizer = Inventory.Instance.allItems[fertilizerItemId];
            if (fertilizer == null)
                return;

            ReserveFarmTile(xPos, yPos, Time.time + FARM_TILE_RESERVATION_SECONDS);
            int currentTileType = WorldManager.Instance.tileTypeMap[xPos, yPos];
            int newTileType = fertilizerItemId == SEASON_ALL_ITEM_ID
                ? GetSeasonAllResultTileType(fertilizer, currentTileType)
                : fertilizer.getResultingPlaceableTileType(currentTileType);
            NetworkMapSharer.Instance.RpcUpdateTileType(newTileType, xPos, yPos);
            WorldManager.Instance.tileTypeMap[xPos, yPos] = newTileType;
            ConsumeOneItem(chest, slot);
        }

        private static int GetSeasonAllResultTileType(InventoryItem seasonAll, int currentTileType)
        {
            if (seasonAll != null)
            {
                int metadataResult = seasonAll.getResultingPlaceableTileType(currentTileType);
                if (metadataResult > 0)
                    return metadataResult;
            }

            return currentTileType == 8 || currentTileType == 13 || currentTileType == SEASON_ALL_WET_TILE_TYPE
                ? SEASON_ALL_WET_TILE_TYPE
                : SEASON_ALL_DRY_TILE_TYPE;
        }

        private static void ApplyInstaGrowAt(Chest chest, int slot, int xPos, int yPos)
        {
            int tileObjectId = WorldManager.Instance.onTileMap[xPos, yPos];
            TileObjectGrowthStages growth = tileObjectId >= 0 && tileObjectId < WorldManager.Instance.allObjects.Length
                ? WorldManager.Instance.allObjects[tileObjectId].tileObjectGrowthStages
                : null;

            if (growth == null || growth.objectStages == null || growth.objectStages.Length == 0)
                return;

            ReserveFarmTile(xPos, yPos, Time.time + FARM_TILE_RESERVATION_SECONDS);
            int newStatus = Mathf.Clamp(WorldManager.Instance.onTileStatusMap[xPos, yPos] + Random.Range(1, 4), 0, growth.objectStages.Length - 1);
            WorldManager.Instance.onTileStatusMap[xPos, yPos] = newStatus;
            NetworkMapSharer.Instance.RpcGiveOnTileStatus(newStatus, xPos, yPos);
            NetworkMapSharer.Instance.RpcUseInstagrow(xPos, yPos);
            ConsumeOneItem(chest, slot);
        }

        private static void PlantSeedAt(Chest chest, int slot, int itemId, int xPos, int yPos)
        {
            InventoryItem seed = Inventory.Instance.allItems[itemId];
            if (seed == null || seed.placeable == null)
                return;

            ReserveFarmTile(xPos, yPos, Time.time + FARM_TILE_RESERVATION_SECONDS);
            NetworkMapSharer.Instance.RpcUpdateOnTileObject(seed.placeable.tileObjectId, xPos, yPos);
            WorldManager.Instance.onTileMap[xPos, yPos] = seed.placeable.tileObjectId;
            WorldManager.Instance.onTileStatusMap[xPos, yPos] = 0;
            ConsumeOneItem(chest, slot);
            AutomationCreditHelper.TryGrantPlantSeedCredit();
        }

        private static void PlantTreeAt(Chest chest, int slot, int itemId, int xPos, int yPos)
        {
            InventoryItem plant = Inventory.Instance.allItems[itemId];
            if (plant == null || plant.placeable == null)
                return;

            ReserveFarmTile(xPos, yPos, Time.time + FARM_TILE_RESERVATION_SECONDS);

            int tileObjectId = WorldManager.Instance.onTileMap[xPos, yPos];
            if (tileObjectId > -1 &&
                tileObjectId < WorldManager.Instance.allObjectSettings.Length &&
                WorldManager.Instance.allObjectSettings[tileObjectId] != null &&
                WorldManager.Instance.allObjectSettings[tileObjectId].isGrass)
            {
                NetworkMapSharer.Instance.RpcUpdateOnTileObject(-1, xPos, yPos);
            }

            NetworkMapSharer.Instance.RpcUpdateOnTileObject(plant.placeable.tileObjectId, xPos, yPos);
            ConsumeOneItem(chest, slot);
            plant.checkForTask();
        }

        private static void QueueFollowUpFarmActions(Chest chest, int tileX, int tileY, bool queueFertilizer, int seedItemId)
        {
            if (chest == null)
                return;

            float nextAt = Time.time + FARM_FOLLOW_UP_DELAY_SECONDS;

            if (queueFertilizer)
            {
                pendingFarmActions.Add(new PendingFarmAction
                {
                    chestX = chest.xPos,
                    chestY = chest.yPos,
                    tileX = tileX,
                    tileY = tileY,
                    actionType = PendingFarmActionType.Fertilize,
                    itemId = FERTILIZER_ITEM_ID,
                    executeAt = nextAt
                });
                nextAt += FARM_FOLLOW_UP_DELAY_SECONDS;
            }

            if (seedItemId >= 0)
            {
                pendingFarmActions.Add(new PendingFarmAction
                {
                    chestX = chest.xPos,
                    chestY = chest.yPos,
                    tileX = tileX,
                    tileY = tileY,
                    actionType = PendingFarmActionType.PlantSeed,
                    itemId = seedItemId,
                    executeAt = nextAt
                });
                nextAt += FARM_FOLLOW_UP_DELAY_SECONDS;
            }

            if (nextAt > Time.time)
                ReserveFarmTile(tileX, tileY, nextAt + FARM_TILE_RESERVATION_SECONDS);
        }

        private static int ProcessPendingFarmActions(bool ignoreSuspension = false)
        {
            if (!ignoreSuspension && AreFarmerActionsSuspended())
                return 0;

            if (pendingFarmActions.Count == 0)
                return 0;

            int processed = 0;

            for (int i = pendingFarmActions.Count - 1; i >= 0; i--)
            {
                PendingFarmAction action = pendingFarmActions[i];
                if (action == null)
                {
                    pendingFarmActions.RemoveAt(i);
                    continue;
                }

                if (Time.time < action.executeAt)
                    continue;

                Chest chest = ConveyorHelper.FindChestAt(action.chestX, action.chestY, null);
                if (chest == null || !ConveyorHelper.IsVacuumCrate(chest.xPos, chest.yPos, null))
                {
                    pendingFarmActions.RemoveAt(i);
                    continue;
                }

                switch (action.actionType)
                {
                    case PendingFarmActionType.Fertilize:
                    {
                        int slot = FindFertilizerSlot(chest);
                        if (slot != -1 && CanFertilizeTileNow(action.tileX, action.tileY))
                        {
                            ApplyFertilizerAt(chest, slot, action.tileX, action.tileY);
                            MarkCrateActive(chest);
                            processed++;
                        }
                        break;
                    }
                    case PendingFarmActionType.PlantSeed:
                    {
                        int slot = FindItemSlot(chest, itemId => itemId == action.itemId);
                        if (slot != -1 && CanPlantSeedAtNow(action.itemId, action.tileX, action.tileY))
                        {
                            PlantSeedAt(chest, slot, action.itemId, action.tileX, action.tileY);
                            MarkCrateActive(chest);
                            processed++;
                        }
                        break;
                    }
                }

                pendingFarmActions.RemoveAt(i);
            }

            return processed;
        }

        private static void BeginDayChangeHarvestPhase()
        {
            dayChangeVacuumHarvestRunning = true;
            harvestCollectingCrates.Clear();
            pendingFarmActions.Clear();
            farmerActionsBlockedUntil = Mathf.Max(farmerActionsBlockedUntil, Time.time + POST_DAY_CHANGE_HARVEST_FARM_DELAY_SECONDS);
        }

        private static void EndDayChangeHarvestPhase()
        {
            dayChangeVacuumHarvestRunning = false;
            harvestCollectingCrates.Clear();
            pendingFarmActions.Clear();
            farmerActionsBlockedUntil = Mathf.Max(farmerActionsBlockedUntil, Time.time + POST_DAY_CHANGE_HARVEST_FARM_DELAY_SECONDS);
        }

        private static bool AreFarmerActionsSuspended()
        {
            return dayChangeVacuumHarvestRunning || Time.time < farmerActionsBlockedUntil;
        }

        private static bool HasPendingFarmActionsForAny(List<Chest> chests, int startIndex, int endIndex)
        {
            if (chests == null || pendingFarmActions.Count == 0)
                return false;

            HashSet<long> waveChestKeys = new HashSet<long>();
            for (int i = startIndex; i < endIndex && i < chests.Count; i++)
            {
                Chest chest = chests[i];
                if (chest == null)
                    continue;

                waveChestKeys.Add(PosKey(chest.xPos, chest.yPos));
            }

            if (waveChestKeys.Count == 0)
                return false;

            for (int i = 0; i < pendingFarmActions.Count; i++)
            {
                PendingFarmAction action = pendingFarmActions[i];
                if (action == null)
                    continue;

                if (waveChestKeys.Contains(PosKey(action.chestX, action.chestY)))
                    return true;
            }

            return false;
        }

        private static bool HasPendingLoggingCollectionForAny(List<Chest> chests, int startIndex, int endIndex)
        {
            if (chests == null || pendingLoggingCollections.Count == 0)
                return false;

            for (int i = startIndex; i < endIndex && i < chests.Count; i++)
            {
                Chest chest = chests[i];
                if (chest != null && pendingLoggingCollections.ContainsKey(PosKey(chest.xPos, chest.yPos)))
                    return true;
            }

            return false;
        }

        private static bool CanFertilizeTileNow(int xPos, int yPos)
        {
            if (!IsOutdoorFarmTileInRange(xPos, yPos, ignoreReservation: true))
                return false;

            int tileType = WorldManager.Instance.tileTypeMap[xPos, yPos];
            return tileType == 7 || tileType == 8;
        }

        private static bool CanPlantSeedAtNow(int itemId, int xPos, int yPos)
        {
            if (!IsCropSeedItem(itemId) || !IsOutdoorFarmTileInRange(xPos, yPos, ignoreReservation: true))
                return false;

            int tileType = WorldManager.Instance.tileTypeMap[xPos, yPos];
            if (tileType != 7 && tileType != 8 && tileType != 12 && tileType != 13 && tileType != SEASON_ALL_DRY_TILE_TYPE && tileType != SEASON_ALL_WET_TILE_TYPE)
                return false;

            int tileObjectId = WorldManager.Instance.onTileMap[xPos, yPos];
            return tileObjectId == -1 || tileObjectId == 30;
        }

        private static bool IsFarmTileReserved(int xPos, int yPos)
        {
            long key = PosKey(xPos, yPos);
            if (!reservedFarmTiles.TryGetValue(key, out float reservedUntil))
                return false;

            return reservedUntil > Time.time;
        }

        private static void ReserveFarmTile(int xPos, int yPos, float until)
        {
            long key = PosKey(xPos, yPos);
            if (reservedFarmTiles.TryGetValue(key, out float existingUntil))
            {
                if (existingUntil >= until)
                    return;
            }

            reservedFarmTiles[key] = until;
        }

        private static void ClearExpiredFarmReservations()
        {
            if (reservedFarmTiles.Count == 0)
                return;

            List<long> expired = null;
            foreach (KeyValuePair<long, float> entry in reservedFarmTiles)
            {
                if (entry.Value > Time.time)
                    continue;

                if (expired == null)
                    expired = new List<long>();
                expired.Add(entry.Key);
            }

            if (expired == null)
                return;

            for (int i = 0; i < expired.Count; i++)
                reservedFarmTiles.Remove(expired[i]);
        }

        private static void ConsumeOneItem(Chest chest, int slot)
        {
            int itemId = chest.itemIds[slot];
            int remaining = chest.itemStacks[slot] - 1;

            if (remaining > 0)
            {
                ContainerManager.manage.changeSlotInChest(chest.xPos, chest.yPos, slot, itemId, remaining, null);
            }
            else
            {
                ContainerManager.manage.changeSlotInChest(chest.xPos, chest.yPos, slot, -1, 0, null);
            }
        }

        private static bool ShouldFlushStackNow(Chest chest, int itemId, int stackAmount)
        {
            if (!Inventory.Instance.allItems[itemId].checkIfStackable())
            {
                ClearPendingFlush(chest, itemId);
                return true;
            }

            if (stackAmount >= TRANSFER_STACK_CHUNK_SIZE)
            {
                ClearPendingFlush(chest, itemId);
                return true;
            }

            long key = GetFlushKey(chest, itemId);
            float startedAt;
            if (!pendingFlushStartedAt.TryGetValue(key, out startedAt))
            {
                pendingFlushStartedAt[key] = Time.time;
                return false;
            }

            if (Time.time - startedAt < VACUUM_STACK_BUFFER_SECONDS)
                return false;

            pendingFlushStartedAt.Remove(key);
            return true;
        }

        private static void ClearPendingFlush(Chest chest, int itemId)
        {
            pendingFlushStartedAt.Remove(GetFlushKey(chest, itemId));
        }

        private static long GetFlushKey(Chest chest, int itemId)
        {
            // Green crates are outdoor-only, so (xPos, yPos, itemId) identifies the buffer.
            // Do NOT shift the packed key further: shifting xPos off the top of the long
            // made all same-row crates share flush timers for the same item.
            return (((long)chest.xPos & 0xFFFFFL) << 44)
                | (((long)chest.yPos & 0xFFFFFL) << 24)
                | (uint)itemId;
        }

        private static int GetTransferChunkSize(Chest chest, int itemId, int stackAmount)
        {
            if (!Inventory.Instance.allItems[itemId].checkIfStackable())
                return 1;

            if (ShouldUseMultiFarmerChunking(chest))
            {
                if (stackAmount >= MULTI_FARMER_VISUAL_CHUNK_THRESHOLD)
                    return BULK_TRANSFER_CHUNK_SIZE;

                return TRANSFER_STACK_CHUNK_SIZE;
            }

            return stackAmount >= BULK_TRANSFER_STACK_THRESHOLD ? BULK_TRANSFER_CHUNK_SIZE : TRANSFER_STACK_CHUNK_SIZE;
        }

        private static bool ShouldUseMultiFarmerChunking(Chest chest)
        {
            if (chest == null || !ConveyorHelper.IsVacuumCrate(chest.xPos, chest.yPos, null))
                return false;

            long key = PosKey(chest.xPos, chest.yPos);
            FarmerNetworkScaleState cachedState;
            if (farmerNetworkScaleCache.TryGetValue(key, out cachedState) && cachedState != null && Time.time < cachedState.expireAt)
                return cachedState.useLargeChunking;

            bool useLargeChunking = HasMultipleFarmerCratesOnNetwork(chest);
            farmerNetworkScaleCache[key] = new FarmerNetworkScaleState
            {
                useLargeChunking = useLargeChunking,
                expireAt = Time.time + FARMER_NETWORK_SCALE_CACHE_SECONDS
            };

            return useLargeChunking;
        }

        private static bool HasMultipleFarmerCratesOnNetwork(Chest anchorChest)
        {
            if (anchorChest == null || WorldManager.Instance == null)
                return false;

            int greenCrateCount = 1;

            if (Plugin.ConveyorTileType == -1)
                return false;

            HashSet<Vector2Int> visitedConveyors = new HashSet<Vector2Int>();
            Queue<Vector2Int> queue = new Queue<Vector2Int>();
            HashSet<long> seenGreenCrates = new HashSet<long>();
            seenGreenCrates.Add(PosKey(anchorChest.xPos, anchorChest.yPos));

            for (int i = 0; i < 4; i++)
            {
                int checkX = anchorChest.xPos + dx[i];
                int checkY = anchorChest.yPos + dy[i];
                if (checkX < 0 || checkY < 0)
                    continue;

                if (!ConveyorHelper.IsConveyorTile(checkX, checkY, null))
                    continue;

                Vector2Int pos = new Vector2Int(checkX, checkY);
                if (visitedConveyors.Add(pos))
                    queue.Enqueue(pos);
            }

            while (queue.Count > 0)
            {
                Vector2Int current = queue.Dequeue();

                for (int i = 0; i < 4; i++)
                {
                    int crateX = current.x + dx[i];
                    int crateY = current.y + dy[i];
                    if (crateX < 0 || crateY < 0)
                        continue;

                    if (!ConveyorHelper.IsVacuumCrate(crateX, crateY, null))
                        continue;

                    long crateKey = PosKey(crateX, crateY);
                    if (!seenGreenCrates.Add(crateKey))
                        continue;

                    greenCrateCount++;
                    if (greenCrateCount >= 2)
                        return true;
                }

                for (int i = 0; i < 4; i++)
                {
                    int nextX = current.x + dx[i];
                    int nextY = current.y + dy[i];
                    if (nextX < 0 || nextY < 0)
                        continue;

                    Vector2Int nextPos = new Vector2Int(nextX, nextY);
                    if (visitedConveyors.Contains(nextPos))
                        continue;

                    if (ConveyorHelper.IsConveyorTile(nextX, nextY, null))
                    {
                        visitedConveyors.Add(nextPos);
                        queue.Enqueue(nextPos);
                    }
                }
            }

            return false;
        }

        private static float GetVacuumFlushDelay()
        {
            return Plugin.ScanInterval;
        }

        private static List<VacuumDropGroup> BuildDropGroups(List<DroppedItem> candidates)
        {
            List<VacuumDropGroup> groups = new List<VacuumDropGroup>();
            if (candidates == null || candidates.Count == 0)
                return groups;

            Dictionary<string, VacuumDropGroup> grouped = new Dictionary<string, VacuumDropGroup>();

            for (int i = 0; i < candidates.Count; i++)
            {
                DroppedItem drop = candidates[i];
                if (drop == null || drop.myItemId < 0 || drop.stackAmount <= 0)
                    continue;

                InventoryItem invItem = Inventory.Instance.allItems[drop.myItemId];
                bool isStackable = invItem != null && invItem.checkIfStackable();
                Vector2Int dropTile = GetDropTile(drop);
                string key = isStackable
                    ? dropTile.x + ":" + dropTile.y + ":" + drop.myItemId + ":" + drop.endOfDayTallyType
                    : drop.netId.ToString();

                if (!grouped.TryGetValue(key, out VacuumDropGroup group))
                {
                    group = new VacuumDropGroup
                    {
                        itemId = drop.myItemId,
                        totalStackAmount = 0,
                        tallyType = drop.endOfDayTallyType,
                        dropTile = dropTile,
                        startWorld = drop.transform.position
                    };
                    grouped[key] = group;
                    groups.Add(group);
                }

                group.totalStackAmount += drop.stackAmount;
                group.drops.Add(drop);
                group.dropIds.Add(drop.netId);
            }

            return groups;
        }

        private static void ActivateAllVacuumCrates()
        {
            int mapSize = WorldManager.Instance.GetMapSize();

            for (int y = 0; y < mapSize; y++)
            {
                for (int x = 0; x < mapSize; x++)
                {
                    if (WorldManager.Instance.onTileMap[x, y] != Plugin.GREEN_CRATE_TILE_ID)
                        continue;

                    ContainerManager.manage.getChestForRecycling(x, y, null);
                }
            }
        }

        private static bool IsVacuumHarvestCandidate(int xPos, int yPos, TileObject tileObj, TileObjectGrowthStages growth, int stage)
        {
            if (tileObj == null || growth == null)
                return false;

            // NEVER auto-harvest incubators or anything that spawns farm animals
            if (growth.spawnsFarmAnimal)
                return false;

            bool isFarmCrop = IsFarmCropTile(xPos, yPos, tileObj, growth);
            bool canHarvest = isFarmCrop
                ? growth.canBeHarvested(stage, deathCheck: true)
                : growth.canBeHarvested(stage);

            if (!canHarvest || growth.isCrabPot || growth.mustBeInWater)
                return false;

            if (IsClassicMachineHarvestTile(tileObj.tileObjectId))
                return false;

            bool isTreeFruitStyle = growth.harvestableByHand &&
                                    ((bool)growth.harvestDrop || (bool)growth.dropsFromLootTable) &&
                                    tileObj.tileObjectChest == null &&
                                    tileObj.tileObjectItemChanger == null;

            return isFarmCrop || isTreeFruitStyle;
        }

        private static bool IsFarmCropTile(int xPos, int yPos, TileObject tileObj, TileObjectGrowthStages growth)
        {
            if (tileObj == null || growth == null || WorldManager.Instance == null)
                return false;

            if (growth.needsTilledSoil || growth.isAPlantSproutFromAFarmPlant(tileObj.tileObjectId))
                return true;

            if (xPos < 0 || yPos < 0 ||
                xPos >= WorldManager.Instance.tileTypeMap.GetLength(0) ||
                yPos >= WorldManager.Instance.tileTypeMap.GetLength(1))
            {
                return false;
            }

            int tileType = WorldManager.Instance.tileTypeMap[xPos, yPos];
            bool isFarmSoil = tileType == 7 || tileType == 8 || tileType == 12 || tileType == 13 || tileType == SEASON_ALL_DRY_TILE_TYPE || tileType == SEASON_ALL_WET_TILE_TYPE;
            return isFarmSoil && growth.canBeHarvested(WorldManager.Instance.onTileStatusMap[xPos, yPos], deathCheck: true);
        }

        private static bool IsClassicMachineHarvestTile(int tileObjectId)
        {
            return tileObjectId == 117 ||  // Crab Pot
                   tileObjectId == 118 ||  // Bee House
                   tileObjectId == 366 ||  // Worm Farm
                   tileObjectId == 897;    // Key Cutter
        }

        private static void HarvestVacuumTargetAt(Chest owningChest, HarvestTarget target)
        {
            if (target.tileObj == null || target.growth == null)
                return;

            bool isFarmCrop = IsFarmCropTile(target.xPos, target.yPos, target.tileObj, target.growth);

            if (isFarmCrop)
            {
                HarvestPlantAt(target.xPos, target.yPos, target.tileObj, target.growth);
                return;
            }

            HarvestNaturalPlantAt(owningChest, target.xPos, target.yPos, target.tileObj, target.growth);
        }

        private static void HarvestPlantAt(int xPos, int yPos, TileObject tileObj, TileObjectGrowthStages growth)
        {
            WorldManager.Instance.lockTileClient(xPos, yPos);
            int newStatus = WorldManager.Instance.onTileStatusMap[xPos, yPos] + growth.takeOrAddFromStateOnHarvest;
            if (newStatus < 0)
                newStatus = 0;

            AutomationCreditHelper.TryGrantHarvestMilestone(tileObj.tileObjectId);
            AutomationCreditHelper.TryGrantGrowthHarvestCredit(tileObj.tileObjectId);

            if (growth.diesOnHarvest)
            {
                NetworkMapSharer.Instance.RpcHarvestObject(-1, xPos, yPos, spawnDrop: true);
            }
            else
            {
                NetworkMapSharer.Instance.RpcHarvestObject(newStatus, xPos, yPos, spawnDrop: true);
                WorldManager.Instance.onTileStatusMap[xPos, yPos] = newStatus;
            }

            WorldManager.Instance.onTileChunkHasChanged(xPos, yPos);
        }

        private static void HarvestNaturalPlantAt(Chest owningChest, int xPos, int yPos, TileObject tileObj, TileObjectGrowthStages growth)
        {
            if (owningChest == null)
                return;

            WorldManager.Instance.lockTileClient(xPos, yPos);
            int newStatus = WorldManager.Instance.onTileStatusMap[xPos, yPos] + growth.takeOrAddFromStateOnHarvest;
            if (newStatus < 0)
                newStatus = 0;

            // RpcHarvestObject defers harvest() past this call stack, so the forced chest
            // must be handed off keyed by tile, not via the transient static.
            HarvestHelper.SetForcedHarvestChestAt(xPos, yPos, owningChest);

            AutomationCreditHelper.TryGrantHarvestMilestone(tileObj.tileObjectId);
            AutomationCreditHelper.TryGrantGrowthHarvestCredit(tileObj.tileObjectId);

            if (growth.diesOnHarvest)
            {
                NetworkMapSharer.Instance.RpcHarvestObject(-1, xPos, yPos, spawnDrop: true);
            }
            else
            {
                NetworkMapSharer.Instance.RpcHarvestObject(newStatus, xPos, yPos, spawnDrop: true);
                WorldManager.Instance.onTileStatusMap[xPos, yPos] = newStatus;
            }

            WorldManager.Instance.onTileChunkHasChanged(xPos, yPos);
        }

        private static bool TryRemoveDropFromWorld(DroppedItem drop)
        {
            if (!Plugin.CanRunServerAutomation() || drop == null || WorldManager.Instance == null || WorldManager.Instance.itemsOnGround == null)
                return false;

            try
            {
                WorldManager.Instance.itemsOnGround.Remove(drop);

                Collider collider = drop.GetComponent<Collider>();
                if (collider != null)
                    collider.enabled = false;

                Renderer[] renderers = drop.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                {
                    renderers[i].enabled = false;
                }

                if (NetworkServer.active && drop.netIdentity != null)
                    NetworkServer.Destroy(drop.gameObject);
                else
                    GameObject.Destroy(drop.gameObject);

                return true;
            }
            catch (System.Exception e)
            {
                Plugin.LogWarningThrottled("vacuum-remove-drop-failed", "Autom8er: Failed to remove drop for vacuum crate: " + e.Message, 10f);
                return false;
            }
        }

        private static void SpawnFallbackGroundDrop(int itemId, int stackAmount, int tallyType, Vector3 chestWorld, HouseDetails inside)
        {
            // Must go through spawnAServerDrop: it calls NetworkServer.Spawn after dropAnItem.
            // A bare dropAnItem here creates the exact ghost-item bug documented in Rule 12
            // (visible locally, netId 0, clients never see it, nobody can pick it up).
            if (NetworkMapSharer.Instance == null)
                return;

            NetworkMapSharer.Instance.spawnAServerDrop(itemId, stackAmount, chestWorld, inside, tryNotToStack: false, xPType: tallyType);
        }

        private static void TriggerVacuumVisual(int tileX, int tileY, float duration)
        {
            long key = PosKey(tileX, tileY);
            float expireAt = Time.time + Mathf.Max(0.25f, duration);

            if (activeVisuals.TryGetValue(key, out VacuumVisualState state))
            {
                state.expireAt = Mathf.Max(state.expireAt, expireAt);
                return;
            }

            GameObject visual = CreateHarVacVisual(tileX, tileY);
            if (visual == null)
                return;

            activeVisuals[key] = new VacuumVisualState
            {
                visual = visual,
                expireAt = expireAt,
                tileX = tileX,
                tileY = tileY
            };
        }

        private static GameObject CreateHarVacVisual(int tileX, int tileY)
        {
            try
            {
                InventoryItem harVac = Inventory.Instance.allItems[Plugin.HAR_VAC_ITEM_ID];
                if (harVac == null || harVac.itemPrefab == null)
                    return null;

                GameObject visual = GameObject.Instantiate(harVac.itemPrefab);
                Vector3 basePos = ConveyorAnimator.TileToWorld(tileX, tileY);
                visual.transform.position = new Vector3(basePos.x, basePos.y + VACUUM_VISUAL_HEIGHT, basePos.z);
                visual.transform.localScale = Vector3.one;

                Rigidbody rb = visual.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.isKinematic = true;
                    rb.detectCollisions = false;
                }

                Collider[] colliders = visual.GetComponentsInChildren<Collider>(true);
                for (int i = 0; i < colliders.Length; i++)
                {
                    colliders[i].enabled = false;
                }

                Animator animator = visual.GetComponent<Animator>();
                if (animator != null)
                    animator.enabled = false;

                DroppedItem droppedItem = visual.GetComponent<DroppedItem>();
                if (droppedItem != null)
                    GameObject.Destroy(droppedItem);

                DroppedItemBounce bounce = visual.GetComponent<DroppedItemBounce>();
                if (bounce != null)
                    GameObject.Destroy(bounce);

                Renderer[] renderers = visual.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                {
                    if (!(renderers[i] is ParticleSystemRenderer))
                        renderers[i].enabled = false;
                }

                VacuumMachine vacuumMachine = visual.GetComponentInChildren<VacuumMachine>(true);
                if (vacuumMachine != null)
                {
                    Transform target = new GameObject("Autom8erVacTarget").transform;
                    target.SetParent(visual.transform, worldPositionStays: false);
                    target.localPosition = new Vector3(0f, -0.35f, 0f);
                    vacuumMachine.targetTransform = target;

                    if (vacuumMachine.particleSystem1 != null)
                    {
                        vacuumMachine.particleSystem1.Clear(withChildren: true);
                        vacuumMachine.particleSystem1.Play(withChildren: true);
                    }

                    if (vacuumMachine.particleSystem2 != null)
                    {
                        vacuumMachine.particleSystem2.Clear(withChildren: true);
                        vacuumMachine.particleSystem2.Play(withChildren: true);
                    }
                }
                else
                {
                    GameObject.Destroy(visual);
                    return null;
                }

                return visual;
            }
            catch (System.Exception e)
            {
                Plugin.LogWarningThrottled("har-vac-visual-create-failed", "Autom8er: Failed to create Har-Vac visual: " + e.Message, 10f);
                return null;
            }
        }

        private static Vector3 GetVacuumTargetWorld(int tileX, int tileY)
        {
            Vector3 basePos = ConveyorAnimator.TileToWorld(tileX, tileY);
            return new Vector3(basePos.x, basePos.y + 0.95f, basePos.z);
        }
    }
}
