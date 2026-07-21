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

    // Conveyor animation sync for modded guests. Host-side automation is unchanged;
    // guests only ever RENDER animations, never move items.
    //
    // This game's Mirror build DISCONNECTS a connection that receives an unknown message
    // id, so neither side may blind-send custom messages (an unmodded or older peer would
    // kick us). Handshake order:
    //   1. Host repeats a harmless vanilla RPC beacon (RpcFeedFishSound at a far-away magic
    //      position; vanilla clients play an inaudible sound and are otherwise unaffected).
    //   2. A modded guest sees the beacon (ConveyorNetSyncBeaconPatch), which proves the
    //      host has our handlers, and only then sends Autom8erHelloMessage.
    //   3. Host marks that connection modded and replies Autom8erWelcomeMessage carrying
    //      the HOST's conveyor tile type (guest configs may differ and are ignored while
    //      a guest). Only marked connections ever receive animation messages.
    public static class ConveyorNetSync
    {
        public struct Autom8erHelloMessage : NetworkMessage
        {
            public int version;
        }

        public struct Autom8erWelcomeMessage : NetworkMessage
        {
            public int conveyorTileType;
        }

        public struct Autom8erConveyorAnimMessage : NetworkMessage
        {
            public int itemId;
            public int amount;
            public int startX;
            public int startY;
            public int endX;
            public int endY;
            public float delay;
        }

        public const int SYNC_VERSION = 1;
        public static readonly Vector3 BeaconPosition = new Vector3(-8888.25f, -8888.25f, -8888.25f);
        private const float BEACON_INTERVAL_SECONDS = 4f;
        private const float HELLO_RETRY_SECONDS = 3f;

        private static bool serializersRegistered = false;
        private static bool serverHandlersRegistered = false;
        private static bool clientHandlersRegistered = false;
        private static readonly HashSet<int> moddedConnectionIds = new HashSet<int>();
        private static float nextBeaconAt = 0f;

        private static bool hostIsModded = false;
        private static float nextHelloAt = 0f;
        private static bool syncActive = false;
        private static bool clientTileTypeOverridden = false;
        private static bool clientWasConnected = false;

        private static void EnsureSerializers()
        {
            if (serializersRegistered)
                return;
            serializersRegistered = true;

            Writer<Autom8erHelloMessage>.write = (writer, msg) => writer.WriteInt(msg.version);
            Reader<Autom8erHelloMessage>.read = reader => new Autom8erHelloMessage { version = reader.ReadInt() };

            Writer<Autom8erWelcomeMessage>.write = (writer, msg) => writer.WriteInt(msg.conveyorTileType);
            Reader<Autom8erWelcomeMessage>.read = reader => new Autom8erWelcomeMessage { conveyorTileType = reader.ReadInt() };

            Writer<Autom8erConveyorAnimMessage>.write = (writer, msg) =>
            {
                writer.WriteInt(msg.itemId);
                writer.WriteInt(msg.amount);
                writer.WriteInt(msg.startX);
                writer.WriteInt(msg.startY);
                writer.WriteInt(msg.endX);
                writer.WriteInt(msg.endY);
                writer.WriteFloat(msg.delay);
            };
            Reader<Autom8erConveyorAnimMessage>.read = reader => new Autom8erConveyorAnimMessage
            {
                itemId = reader.ReadInt(),
                amount = reader.ReadInt(),
                startX = reader.ReadInt(),
                startY = reader.ReadInt(),
                endX = reader.ReadInt(),
                endY = reader.ReadInt(),
                delay = reader.ReadFloat()
            };
        }

        // Host side. Called from Plugin.Update after the NetworkServer.active gate.
        public static void ServerUpdate()
        {
            if (!serverHandlersRegistered)
            {
                EnsureSerializers();
                NetworkServer.RegisterHandler<Autom8erHelloMessage>(OnServerHello, false);
                serverHandlersRegistered = true;
            }

            // Beacon is pointless until the tile type is cached (Welcome carries it).
            if (Plugin.ConveyorTileType == -1 || NetworkMapSharer.Instance == null)
                return;

            if (Time.realtimeSinceStartup < nextBeaconAt)
                return;
            nextBeaconAt = Time.realtimeSinceStartup + BEACON_INTERVAL_SECONDS;

            if (HasRemoteConnections())
                NetworkMapSharer.Instance.RpcFeedFishSound(BeaconPosition);
        }

        private static bool HasRemoteConnections()
        {
            foreach (var kvp in NetworkServer.connections)
            {
                // connectionId 0 is the host's own local connection
                if (kvp.Value != null && kvp.Value.connectionId != 0)
                    return true;
            }
            return false;
        }

        private static void OnServerHello(NetworkConnection conn, Autom8erHelloMessage msg)
        {
            if (conn == null || Plugin.ConveyorTileType == -1)
                return; // guest retries hello until welcomed

            bool isNew = moddedConnectionIds.Add(conn.connectionId);
            conn.Send(new Autom8erWelcomeMessage { conveyorTileType = Plugin.ConveyorTileType });
            if (isNew)
                Plugin.Log.LogInfo("Autom8er: Modded client on connection " + conn.connectionId + ", conveyor animation sync enabled.");
        }

        // Host side. Called from ConveyorAnimator.StartAnimation for outdoor animations.
        public static void BroadcastAnimation(int itemId, int amount, List<Vector2Int> path, float delay)
        {
            if (!NetworkServer.active || moddedConnectionIds.Count == 0 || path == null || path.Count < 2)
                return;

            Autom8erConveyorAnimMessage msg = new Autom8erConveyorAnimMessage
            {
                itemId = itemId,
                amount = amount,
                startX = path[0].x,
                startY = path[0].y,
                endX = path[path.Count - 1].x,
                endY = path[path.Count - 1].y,
                delay = delay
            };

            List<int> stale = null;
            foreach (int connectionId in moddedConnectionIds)
            {
                if (NetworkServer.connections.TryGetValue(connectionId, out NetworkConnectionToClient conn) && conn != null && conn.isReady)
                {
                    conn.Send(msg);
                }
                else
                {
                    if (stale == null)
                        stale = new List<int>();
                    stale.Add(connectionId);
                }
            }

            if (stale != null)
            {
                foreach (int connectionId in stale)
                    moddedConnectionIds.Remove(connectionId);
            }
        }

        // Runs every frame from Plugin.Update BEFORE the server gate. Self-guards per role.
        public static void ClientUpdate()
        {
            if (NetworkServer.active)
            {
                ResetClientState(false);
                return;
            }

            if (serverHandlersRegistered)
            {
                // Server stopped: Mirror's Shutdown cleared its handler table.
                serverHandlersRegistered = false;
                moddedConnectionIds.Clear();
            }

            if (!NetworkClient.active || !NetworkClient.isConnected)
            {
                if (clientWasConnected)
                    ResetClientState(true);
                return;
            }

            clientWasConnected = true;

            if (!clientHandlersRegistered)
            {
                EnsureSerializers();
                NetworkClient.RegisterHandler<Autom8erWelcomeMessage>(OnClientWelcome, false);
                NetworkClient.RegisterHandler<Autom8erConveyorAnimMessage>(OnClientConveyorAnim, false);
                clientHandlersRegistered = true;
            }

            if (hostIsModded && !syncActive && Time.realtimeSinceStartup >= nextHelloAt)
            {
                nextHelloAt = Time.realtimeSinceStartup + HELLO_RETRY_SECONDS;
                NetworkClient.Send(new Autom8erHelloMessage { version = SYNC_VERSION });
            }

            // Pure guests never reach the animator update below the server gate.
            ConveyorAnimator.UpdateAnimations();
        }

        private static void ResetClientState(bool clearAnimations)
        {
            hostIsModded = false;
            syncActive = false;
            nextHelloAt = 0f;
            clientHandlersRegistered = false; // NetworkClient shutdown clears Mirror's handler table
            if (clientWasConnected && clearAnimations)
                ConveyorAnimator.ClearAllAnimations();
            clientWasConnected = false;
            if (clientTileTypeOverridden)
            {
                // Re-cache from our own config if we host or join someone else later.
                Plugin.ConveyorTileType = -1;
                clientTileTypeOverridden = false;
            }
        }

        public static void NotifyBeaconReceived()
        {
            if (NetworkServer.active)
                return; // the host hears its own beacon; only guests care
            hostIsModded = true;
        }

        private static void OnClientWelcome(Autom8erWelcomeMessage msg)
        {
            if (NetworkServer.active || msg.conveyorTileType < 0)
                return;

            Plugin.ConveyorTileType = msg.conveyorTileType;
            clientTileTypeOverridden = true;
            syncActive = true;
            Plugin.Log.LogInfo("Autom8er: Host is modded, conveyor animation sync active (host tile type " + msg.conveyorTileType + ").");
        }

        private static void OnClientConveyorAnim(Autom8erConveyorAnimMessage msg)
        {
            if (NetworkServer.active || !syncActive || !Plugin.AnimationEnabled)
                return;

            if (WorldManager.Instance == null || Inventory.Instance == null)
                return;

            // Render-only: same pathfinder over the synced tile map, using the host's
            // conveyor tile type applied in OnClientWelcome. No item logic on guests.
            List<Vector2Int> path = ConveyorPathfinder.FindPath(
                new Vector2Int(msg.startX, msg.startY),
                new Vector2Int(msg.endX, msg.endY),
                null);

            if (path == null || path.Count < 2)
                return;

            ConveyorAnimator.StartAnimation(msg.itemId, msg.amount, path, () => { }, msg.delay);
        }
    }
}
