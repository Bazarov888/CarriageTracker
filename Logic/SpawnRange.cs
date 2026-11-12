using System.Collections.Generic;

namespace CarriageTracker;

// Values the mod writes into a route's UnitCompositionSpawner, and when.
// PlayerRange (x, y): the game spawns the squad only for a player within y meters of the patrol point.
public static class SpawnRange
{
    // The game's own range, seen on every route of a live server. The mod learns the normal values from the routes
    // themselves (Learn, Starter.NormalOf) and uses these only until it has, so a game update that changes them
    // doesn't get the old ones written back.
    public const float NormalY = 90f;
    // Source = Mod: nobody stands within 1 mm of a patrol point, so the game doesn't spawn. Kept above 0
    // on purpose, in case the game takes 0 for "no limit".
    public const float BlockedY = 0.001f;
    // A forced start: any distance.
    public const float StartY = 100000f;
    // The game's own respawn timers (seconds), seen on every route of a live server.
    public const int NormalMinRespawn = 1000;
    public const int NormalMaxRespawn = 2700;

    public static bool IsBlocked(float x, float y) => y > 0f && y < 0.01f;
    public static bool IsZero(float x, float y) => x == 0f && y == 0f;
    public static bool IsStart(float x, float y) => y > 1000f;

    // A prefab's range that can be the game's normal one: not blocked, not zero, not a start range.
    public static bool SaneRange(float y) => !float.IsNaN(y) && y >= 1f && y <= 1000f;
    // A prefab's respawn timers that can be the game's normal ones (a start sets both to 0).
    public static bool SaneTimers(double min, double max) => min > 0 && max >= min;

    // A route with no caravan out: waiting for a new squad, or standing at its last stop until the game removes
    // what is left and returns it to the start (WaitingForDespawn). The second kind must be blocked too: a player
    // coming near wakes it, the game resets it and would spawn its own squad before the next check.
    public static bool Idle(string spawnerState) => spawnerState == "WaitingForSpawn" || spawnerState == "WaitingForDespawn";

    // Source: what to write into a route that isn't being started by the mod; null = leave it.
    // With Mod a route waiting for a new squad is blocked; any other route gets the game's range back
    // if it is blocked (or zero, which an earlier version could leave).
    // normalY: the route's own range (from its prefab, NormalY if unknown).
    public static float? SourceTarget(bool modOnly, bool waitingForSpawn, float x, float y, float normalY = NormalY)
    {
        if (modOnly && waitingForSpawn) return IsBlocked(x, y) ? null : BlockedY;
        return IsBlocked(x, y) || IsZero(x, y) ? normalY : null;
    }

    // End of a start's assembly window: the saved range comes back, but a blocked or zero one becomes the
    // game's range, so the squad on the road behaves as the game's own. The block returns once the route
    // waits for a new squad (SourceTarget).
    public static float RestoreY(float savedX, float savedY, float normalY = NormalY) =>
        IsBlocked(savedX, savedY) || IsZero(savedX, savedY) ? normalY : savedY;

    // .cv cleanup: everything the mod may have written becomes the game's range.
    public static bool CleanupOpens(float x, float y) => IsBlocked(x, y) || IsZero(x, y) || IsStart(x, y);

    // A start sets both respawn timers to 0; the game's own are never 0.
    public static bool TimersZero(double min, double max) => min == 0 && max == 0;

    // A route's spawner values as they are now.
    public readonly record struct Seen(float RangeY, double Modifier, double Min, double Max);

    // The normal values of carriage routes, learned from the routes themselves: the most common range and the most
    // common respawn timers among the values that can be the game's own (SaneRange, SaneTimers, a modifier above 0).
    // What the mod writes never counts: a blocked or zero range, a start's range, a start's zero timers. null for
    // what no route shows now (with Source = Mod every idle route is blocked, so the range is known only once a
    // caravan is out). A route patrol has no prefab to read them from (patrol dump of 12.11: no PrefabGUID).
    public static (float? RangeY, (double Modifier, double Min, double Max)? Timers) Learn(IEnumerable<Seen> routes)
    {
        var ranges = new Dictionary<float, int>();
        var timers = new Dictionary<(double, double, double), int>();
        foreach (var r in routes)
        {
            if (SaneRange(r.RangeY)) ranges[r.RangeY] = (ranges.TryGetValue(r.RangeY, out int n) ? n : 0) + 1;
            if (SaneTimers(r.Min, r.Max) && r.Modifier > 0)
            {
                var key = (r.Modifier, r.Min, r.Max);
                timers[key] = (timers.TryGetValue(key, out int m) ? m : 0) + 1;
            }
        }
        float? range = null;
        int best = 0;
        foreach (var kv in ranges) if (kv.Value > best) { best = kv.Value; range = kv.Key; }
        (double, double, double)? timer = null;
        best = 0;
        foreach (var kv in timers) if (kv.Value > best) { best = kv.Value; timer = kv.Key; }
        return (range, timer);
    }
}
