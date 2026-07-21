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

    public static class ConveyorPathfinder
    {
        private static readonly int[] dx = { -1, 0, 1, 0 };
        private static readonly int[] dy = { 0, -1, 0, 1 };

        private static bool IsPartOfObject(int x, int y, Vector2Int rootPos, HouseDetails inside)
        {
            if (x == rootPos.x && y == rootPos.y)
                return true;

            int[,] tileMap = inside != null ? inside.houseMapOnTile : WorldManager.Instance.onTileMap;
            int mapW = tileMap.GetLength(0);
            int mapH = tileMap.GetLength(1);
            if (x < 0 || y < 0 || x >= mapW || y >= mapH)
                return false;

            if (tileMap[x, y] < -1)
            {
                Vector2 resolved = WorldManager.Instance.findMultiTileObjectPos(x, y, inside);
                return (int)resolved.x == rootPos.x && (int)resolved.y == rootPos.y;
            }

            return false;
        }

        // BFS with parent tracking. Handles multi-tile objects (silos, fish ponds)
        public static List<Vector2Int> FindPath(Vector2Int start, Vector2Int end, HouseDetails inside)
        {
            Dictionary<Vector2Int, Vector2Int> cameFrom = new Dictionary<Vector2Int, Vector2Int>();
            Queue<Vector2Int> queue = new Queue<Vector2Int>();

            int[,] tileMap = inside != null ? inside.houseMapOnTile : WorldManager.Instance.onTileMap;
            int mapW = tileMap.GetLength(0);
            int mapH = tileMap.GetLength(1);

            // Scan radius 5 around start to find all tiles of multi-tile source object
            int scanRadius = 5;
            int minX = Mathf.Max(0, start.x - scanRadius);
            int maxX = Mathf.Min(mapW - 1, start.x + scanRadius);
            int minY = Mathf.Max(0, start.y - scanRadius);
            int maxY = Mathf.Min(mapH - 1, start.y + scanRadius);

            for (int sx = minX; sx <= maxX; sx++)
            {
                for (int sy = minY; sy <= maxY; sy++)
                {
                    if (!IsPartOfObject(sx, sy, start, inside))
                        continue;

                    for (int i = 0; i < 4; i++)
                    {
                        int nx = sx + dx[i];
                        int ny = sy + dy[i];
                        if (nx < 0 || ny < 0 || nx >= mapW || ny >= mapH)
                            continue;

                        Vector2Int neighbor = new Vector2Int(nx, ny);
                        if (ConveyorHelper.IsConveyorTile(nx, ny, inside) && !cameFrom.ContainsKey(neighbor))
                        {
                            cameFrom[neighbor] = start;
                            queue.Enqueue(neighbor);
                        }
                    }
                }
            }

            Vector2Int effectiveEnd = end;
            bool foundEnd = false;
            // Allow very large conveyor runs so single-chest mega arrays still animate end-to-end.
            // Revisit protection comes from cameFrom, so this won't loop infinitely.
            int maxTiles = mapW * mapH;
            int visited = 0;

            while (queue.Count > 0 && visited < maxTiles)
            {
                Vector2Int current = queue.Dequeue();
                visited++;

                for (int i = 0; i < 4; i++)
                {
                    int nx = current.x + dx[i];
                    int ny = current.y + dy[i];
                    if (nx < 0 || ny < 0 || nx >= mapW || ny >= mapH)
                        continue;

                    Vector2Int neighbor = new Vector2Int(nx, ny);

                    if (IsPartOfObject(nx, ny, end, inside))
                    {
                        effectiveEnd = neighbor;
                        // If the end tile is already tracked (e.g. the destination sits ON a
                        // conveyor-type floor tile), it already has a valid chain to start.
                        // Overwriting its parent here can create a cycle and hang reconstruction.
                        if (!cameFrom.ContainsKey(effectiveEnd))
                            cameFrom[effectiveEnd] = current;
                        foundEnd = true;
                        break;
                    }

                    if (cameFrom.ContainsKey(neighbor))
                        continue;

                    if (ConveyorHelper.IsConveyorTile(nx, ny, inside))
                    {
                        cameFrom[neighbor] = current;
                        queue.Enqueue(neighbor);
                    }
                }

                if (foundEnd)
                    break;
            }

            // Phase 2: check distance 2 (outer-row crab pots are 2 tiles from conveyor)
            if (!foundEnd)
            {
                for (int i = 0; i < 4; i++)
                {
                    int adjX = end.x + dx[i];
                    int adjY = end.y + dy[i];
                    if (adjX < 0 || adjY < 0 || adjX >= mapW || adjY >= mapH)
                        continue;

                    Vector2Int adjTile = new Vector2Int(adjX, adjY);

                    for (int j = 0; j < 4; j++)
                    {
                        int convX = adjX + dx[j];
                        int convY = adjY + dy[j];
                        if (convX < 0 || convY < 0 || convX >= mapW || convY >= mapH)
                            continue;

                        Vector2Int convTile = new Vector2Int(convX, convY);
                        if (cameFrom.ContainsKey(convTile) && !(convTile.x == start.x && convTile.y == start.y))
                        {
                            effectiveEnd = adjTile;
                            if (!cameFrom.ContainsKey(effectiveEnd))
                                cameFrom[effectiveEnd] = convTile;
                            foundEnd = true;
                            break;
                        }
                    }
                    if (foundEnd) break;
                }
            }

            if (!foundEnd)
                return null;

            List<Vector2Int> path = new List<Vector2Int>();
            Vector2Int step = effectiveEnd;
            int reconstructGuard = 0;
            while (step.x != start.x || step.y != start.y)
            {
                path.Add(step);
                // A corrupted/cyclic parent chain must degrade to "no path", never hang the main thread.
                if (++reconstructGuard > maxTiles || !cameFrom.TryGetValue(step, out step))
                    return null;
            }
            path.Add(start);
            path.Reverse();

            return path;
        }
    }
}
