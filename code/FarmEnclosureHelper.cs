using System.Collections.Generic;
using UnityEngine;

namespace Autom8er
{

    // Limits green crate farm work to the fence or path area around the crate.
    // A crate with no credible enclosure keeps its full square range.
    public static class FarmEnclosureHelper
    {
        private const byte OPEN_CELL = 0;
        private const byte BOUNDARY_CELL = 1;
        private const byte GAP_CELL = 2;
        private const byte OFF_MAP_CELL = 3;
        // At least 75% of the region's edge must be real fence, gate, path, or the crate.
        private const int MIN_CLOSED_EDGE_PERCENT = 75;
        private const int MAX_GAP_TILES = 2;
        // A gap may pass through a wall up to three tiles thick. A longer narrow passage is a corridor.
        private const int MAX_GAP_DEPTH_STEPS = 2;

        private class FarmEnclosure
        {
            public Chest chest;
            public int frame = -1;
            public bool enclosed;
            public int minX;
            public int minY;
            public int size;
            public bool[] region;
            // The boundary cells this verdict came from.
            public byte[] cells;
        }

        private static readonly int[] neighbourX = { -1, 0, 1, -1, 1, -1, 0, 1 };
        private static readonly int[] neighbourY = { -1, -1, -1, 0, 0, 1, 1, 1 };
        private static readonly int[] floodStepX = { -1, 1, 0, 0 };
        private static readonly int[] floodStepY = { 0, 0, -1, 1 };
        // Gap lines run straight or diagonal, so conveyor openings in round or staircase fences close too.
        private static readonly int[] gapLineX = { 1, 0, 1, 1 };
        private static readonly int[] gapLineY = { 0, 1, 1, -1 };
        private static readonly Dictionary<long, FarmEnclosure> enclosureByCrate = new Dictionary<long, FarmEnclosure>();
        private static byte[] scratchCells = new byte[0];
        private static int[] scratchMarks = new int[0];
        private static int[] scratchQueue = new int[0];
        private static int scratchMarkStamp = 0;

        // True when the tile is inside the crate's square range and, if the crate is enclosed, inside that enclosure.
        public static bool IsInFarmArea(Chest chest, int xPos, int yPos)
        {
            if (chest == null ||
                Mathf.Abs(xPos - chest.xPos) > Plugin.AUTO_FARMER_RANGE_TILES ||
                Mathf.Abs(yPos - chest.yPos) > Plugin.AUTO_FARMER_RANGE_TILES)
                return false;

            return IsInsideEnclosure(chest, xPos, yPos);
        }

        // True when the crate has no credible enclosure or the tile is inside it. Logging rows use this
        // without the square range because a row can reach past it.
        public static bool IsInsideEnclosure(Chest chest, int xPos, int yPos)
        {
            if (chest == null)
                return false;

            FarmEnclosure enclosure = GetEnclosure(chest);
            if (!enclosure.enclosed)
                return true;

            int i = Mathf.Clamp(xPos - enclosure.minX, 0, enclosure.size - 1);
            int j = Mathf.Clamp(yPos - enclosure.minY, 0, enclosure.size - 1);
            return enclosure.region[j * enclosure.size + i];
        }

        public static void ClearState()
        {
            enclosureByCrate.Clear();
        }

        // The window is re-read every frame, so a fence, gate, or path edit applies on the next frame.
        private static FarmEnclosure GetEnclosure(Chest chest)
        {
            long key = ((long)chest.xPos << 32) | (uint)chest.yPos;
            FarmEnclosure enclosure;
            if (!enclosureByCrate.TryGetValue(key, out enclosure))
            {
                enclosure = new FarmEnclosure();
                enclosureByCrate[key] = enclosure;
            }

            if (enclosure.frame == Time.frameCount && object.ReferenceEquals(enclosure.chest, chest))
                return enclosure;

            enclosure.chest = chest;
            enclosure.frame = Time.frameCount;
            ComputeEnclosure(chest.xPos, chest.yPos, enclosure);
            return enclosure;
        }

        // Flood fill from the crate over open tiles inside a window twice the farm radius, widened so a
        // full logging row and the wall depth past its end fit inside it.
        // A region qualifies when gaps of up to two tiles leave at least 75% of its edge real.
        // A region that runs past the window or map edge is judged on the edge it shows.
        private static void ComputeEnclosure(int crateX, int crateY, FarmEnclosure enclosure)
        {
            if (WorldManager.Instance == null || WorldManager.Instance.onTileMap == null || WorldManager.Instance.tileTypeMap == null)
            {
                enclosure.enclosed = false;
                enclosure.cells = null;
                return;
            }

            int mapW = WorldManager.Instance.onTileMap.GetLength(0);
            int mapH = WorldManager.Instance.onTileMap.GetLength(1);
            int reach = Mathf.Max(Plugin.AUTO_FARMER_RANGE_TILES * 2, VacuumCrateHelper.MAX_LOGGING_ROW_LENGTH + MAX_GAP_DEPTH_STEPS + 1);
            int size = reach * 2 + 1;
            int cellCount = size * size;
            int minX = crateX - reach;
            int minY = crateY - reach;

            if (scratchCells.Length < cellCount)
            {
                scratchCells = new byte[cellCount];
                scratchMarks = new int[cellCount];
                scratchQueue = new int[cellCount];
            }

            for (int j = 0; j < size; j++)
            {
                for (int i = 0; i < size; i++)
                {
                    int x = minX + i;
                    int y = minY + j;
                    if (x < 0 || y < 0 || x >= mapW || y >= mapH)
                        scratchCells[j * size + i] = OFF_MAP_CELL;
                    else
                        scratchCells[j * size + i] = (x == crateX && y == crateY) || IsBoundaryTile(x, y) ? BOUNDARY_CELL : OPEN_CELL;
                }
            }

            // The verdict depends only on these cells. Tree, crop, and growth edits leave them unchanged,
            // so the last flood still holds and the gap and flood passes can be skipped.
            bool unchanged = enclosure.cells != null;
            if (enclosure.cells == null)
                enclosure.cells = new byte[cellCount];
            for (int cell = 0; cell < cellCount; cell++)
            {
                if (enclosure.cells[cell] != scratchCells[cell])
                {
                    unchanged = false;
                    enclosure.cells[cell] = scratchCells[cell];
                }
            }

            if (unchanged)
                return;

            enclosure.enclosed = false;

            for (int j = 0; j < size; j++)
            {
                for (int i = 0; i < size; i++)
                {
                    if (scratchCells[j * size + i] != OPEN_CELL)
                        continue;

                    for (int line = 0; line < gapLineX.Length; line++)
                    {
                        if (IsGapCell(i, j, gapLineX[line], gapLineY[line], size))
                        {
                            scratchCells[j * size + i] = GAP_CELL;
                            break;
                        }
                    }
                }
            }

            if (scratchMarkStamp > int.MaxValue - neighbourX.Length - 1)
            {
                System.Array.Clear(scratchMarks, 0, scratchMarks.Length);
                scratchMarkStamp = 0;
            }

            int outsideStamp = MarkOutside(size);

            // A crate on a fence line or a shared path serves every enclosed plot it touches.
            for (int n = 0; n < neighbourX.Length; n++)
            {
                int seedI = reach + neighbourX[n];
                int seedJ = reach + neighbourY[n];
                int seed = seedJ * size + seedI;
                if (scratchCells[seed] != OPEN_CELL || scratchMarks[seed] > outsideStamp)
                    continue;

                // Each flood gets its own stamp, so marks from an earlier flood never act as walls.
                scratchMarkStamp++;
                int count;
                if (!TryFloodRegion(seed, size, out count))
                    continue;

                if (!enclosure.enclosed)
                {
                    if (enclosure.region == null || enclosure.region.Length != cellCount)
                        enclosure.region = new bool[cellCount];
                    else
                        System.Array.Clear(enclosure.region, 0, cellCount);
                    enclosure.enclosed = true;
                    enclosure.minX = minX;
                    enclosure.minY = minY;
                    enclosure.size = size;
                }

                for (int q = 0; q < count; q++)
                    enclosure.region[scratchQueue[q]] = true;
            }
        }

        // Open space that reaches the window or map edge counts as outside. Only gaps that touch it are
        // fence openings. Breaks in path rows inside a plot turn back into farmland.
        private static int MarkOutside(int size)
        {
            int stamp = ++scratchMarkStamp;
            int head = 0;
            int count = 0;
            for (int cell = 0; cell < size * size; cell++)
            {
                int i = cell % size;
                int j = cell / size;
                if (scratchCells[cell] == OPEN_CELL &&
                    (i == 0 || j == 0 || i == size - 1 || j == size - 1 || TouchesCell(cell, size, OFF_MAP_CELL, 0)))
                {
                    scratchMarks[cell] = stamp;
                    scratchQueue[count++] = cell;
                }
            }

            while (head < count)
            {
                int cell = scratchQueue[head++];
                int i = cell % size;
                int j = cell / size;
                for (int n = 0; n < floodStepX.Length; n++)
                {
                    int nextI = i + floodStepX[n];
                    int nextJ = j + floodStepY[n];
                    int next = nextJ * size + nextI;
                    if (nextI >= 0 && nextJ >= 0 && nextI < size && nextJ < size &&
                        scratchCells[next] == OPEN_CELL && scratchMarks[next] != stamp)
                    {
                        scratchMarks[next] = stamp;
                        scratchQueue[count++] = next;
                    }
                }
            }

            for (int cell = 0; cell < size * size; cell++)
            {
                if (scratchCells[cell] == GAP_CELL && !TouchesCell(cell, size, OPEN_CELL, stamp))
                    scratchCells[cell] = OPEN_CELL;
            }

            return stamp;
        }

        // True when a side neighbour has this kind and, when stamp is set, carries that flood mark.
        private static bool TouchesCell(int cell, int size, byte kind, int stamp)
        {
            int i = cell % size;
            int j = cell / size;
            for (int n = 0; n < floodStepX.Length; n++)
            {
                int nextI = i + floodStepX[n];
                int nextJ = j + floodStepY[n];
                int next = nextJ * size + nextI;
                if (nextI >= 0 && nextJ >= 0 && nextI < size && nextJ < size && scratchCells[next] == kind &&
                    (stamp == 0 || scratchMarks[next] == stamp))
                    return true;
            }

            return false;
        }

        // Gap cells act as walls. The window and map edges count as neither fence nor gap.
        private static bool TryFloodRegion(int seed, int size, out int count)
        {
            int head = 0;
            count = 0;
            int closedEdges = 0;
            int gapEdges = 0;
            scratchMarks[seed] = scratchMarkStamp;
            scratchQueue[count++] = seed;

            while (head < count)
            {
                int cell = scratchQueue[head++];
                int i = cell % size;
                int j = cell / size;

                for (int n = 0; n < floodStepX.Length; n++)
                {
                    int nextI = i + floodStepX[n];
                    int nextJ = j + floodStepY[n];
                    if (nextI < 0 || nextJ < 0 || nextI >= size || nextJ >= size)
                        continue;

                    int next = nextJ * size + nextI;
                    byte kind = scratchCells[next];
                    if (kind == BOUNDARY_CELL)
                        closedEdges++;
                    else if (kind == OFF_MAP_CELL)
                        continue;
                    else if (kind == GAP_CELL)
                        gapEdges++;
                    else if (scratchMarks[next] != scratchMarkStamp)
                    {
                        scratchMarks[next] = scratchMarkStamp;
                        scratchQueue[count++] = next;
                    }
                }
            }

            return closedEdges * 100 >= (closedEdges + gapEdges) * MIN_CLOSED_EDGE_PERCENT;
        }

        // An open tile is a gap when real boundary sits on both sides along one straight or diagonal line,
        // the gap is at most two tiles wide, and the boundary line carries on past one side.
        // Both tiles across the line must be open, so a one-tile pocket inside a fence stays farmland.
        // Across the line, the opening must reach unpinched tiles on both sides within the wall depth,
        // so a dead-end notch or a long narrow strip between thick walls stays farmland.
        private static bool IsGapCell(int i, int j, int stepX, int stepY, int size)
        {
            int crossX = -stepY;
            int crossY = stepX;
            if (IsBoundaryCell(i - crossX, j - crossY, size) || IsBoundaryCell(i + crossX, j + crossY, size))
                return false;

            int before;
            int after;
            if (!IsPinched(i, j, stepX, stepY, size, out before, out after))
                return false;

            if (!IsBoundaryCell(i - (before + 1) * stepX, j - (before + 1) * stepY, size) &&
                !IsBoundaryCell(i + (after + 1) * stepX, j + (after + 1) * stepY, size))
                return false;

            return OpensPastWall(i, j, crossX, crossY, stepX, stepY, size) &&
                OpensPastWall(i, j, -crossX, -crossY, stepX, stepY, size);
        }

        // True when boundary sits within two tiles on both sides along the line and the opening is at most two tiles.
        private static bool IsPinched(int i, int j, int stepX, int stepY, int size, out int before, out int after)
        {
            before = IsBoundaryCell(i - stepX, j - stepY, size) ? 1 : IsBoundaryCell(i - 2 * stepX, j - 2 * stepY, size) ? 2 : 0;
            after = IsBoundaryCell(i + stepX, j + stepY, size) ? 1 : IsBoundaryCell(i + 2 * stepX, j + 2 * stepY, size) ? 2 : 0;
            return before != 0 && after != 0 && before + after - 1 <= MAX_GAP_TILES;
        }

        private static bool OpensPastWall(int i, int j, int crossX, int crossY, int stepX, int stepY, int size)
        {
            for (int step = 1; step <= MAX_GAP_DEPTH_STEPS; step++)
            {
                int nextI = i + crossX * step;
                int nextJ = j + crossY * step;
                if (IsBoundaryCell(nextI, nextJ, size))
                    return false;

                int before;
                int after;
                if (!IsPinched(nextI, nextJ, stepX, stepY, size, out before, out after))
                    return true;
            }

            return false;
        }

        private static bool IsBoundaryCell(int i, int j, int size)
        {
            return i >= 0 && j >= 0 && i < size && j < size && scratchCells[j * size + i] == BOUNDARY_CELL;
        }

        // Fences, gates, and path floors bound a farm. Conveyor belts stay open so a belt
        // crossing a fence reads as a gap instead of splitting the farm.
        private static bool IsBoundaryTile(int xPos, int yPos)
        {
            WorldManager world = WorldManager.Instance;
            if (xPos < 0 || yPos < 0 || xPos >= world.onTileMap.GetLength(0) || yPos >= world.onTileMap.GetLength(1))
                return false;

            int tileObjectId = world.onTileMap[xPos, yPos];
            if (tileObjectId < -1)
            {
                Vector2 rootPos = world.findMultiTileObjectPos(xPos, yPos);
                tileObjectId = world.onTileMap[(int)rootPos.x, (int)rootPos.y];
            }

            if (tileObjectId >= 0 && tileObjectId < world.allObjects.Length)
            {
                TileObject tileObj = world.allObjects[tileObjectId];
                if (tileObj != null &&
                    ((tileObj.tileObjectConnect != null && tileObj.tileObjectConnect.isFence) ||
                     (tileObj.tileOnOff != null && tileObj.tileOnOff.isGate)))
                    return true;
            }

            int tileType = world.tileTypeMap[xPos, yPos];
            return tileType != Plugin.ConveyorTileType &&
                tileType >= 0 && tileType < world.tileTypes.Length &&
                world.tileTypes[tileType] != null && world.tileTypes[tileType].isPath;
        }
    }
}
