using System.Collections.Generic;

namespace CarriageTracker;

// Stops of patrol routes and the squads that walk them, as far as the dumps of 12.11 show.
public static class StopLogic
{
    // A stop is the id of a road node: a map chunk and a GUID. The chunk tells where the node is to within one
    // square of 160 m: its corner is at chunk * 160 - 3200 on both axes. Seen on 12.11: all 12 patrols standing at
    // their last stop stood inside the square of that stop's chunk (ChurchLegion at (-1811, -552), chunk 8,16).
    public const float CellSize = 160f;
    public const float Origin = -3200f;

    // The corner of the chunk's square with the smaller x and z.
    public static (float X, float Z) Cell(int chunkX, int chunkZ) => (Origin + chunkX * CellSize, Origin + chunkZ * CellSize);

    public static bool InCell(int chunkX, int chunkZ, float x, float z)
    {
        var (x0, z0) = Cell(chunkX, chunkZ);
        return x >= x0 && x <= x0 + CellSize && z >= z0 && z <= z0 + CellSize;
    }

    // Where a strong guard of a trial caravan goes: in each group of the squad the first unit that isn't the animal
    // pulling the cart. A patrol has 8 places in its formation and the game's groups fill them, so a guard is
    // replaced, not added. A group with no guard, or one that reaches past the unit list, gives no place.
    public static List<int> GuardSlots(IReadOnlyList<(int Start, int Count)> groups, IReadOnlyList<bool> isAnimal)
    {
        var slots = new List<int>();
        foreach (var (start, count) in groups)
        {
            if (start < 0 || count <= 0 || start + count > isAnimal.Count) continue;
            for (int i = start; i < start + count; i++)
                if (!isAnimal[i]) { slots.Add(i); break; }
        }
        return slots;
    }
}
