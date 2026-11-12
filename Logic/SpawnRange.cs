using System.Collections.Generic;

namespace CarriageTracker;

// Values the mod writes into a route's UnitCompositionSpawner, and when.
// PlayerRange (x, y): the game spawns the squad only for a player within y meters of the patrol point.
public static class SpawnRange
{
    // The game's own range, seen on every route of a live server. The mod learns the normal values from the game
    // (LearnRange, LearnTimers, Starter.NormalOf) and uses these only until it has, so a game update that changes
    // them doesn't get the old ones written back.
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

    // A range that can be the game's normal one: not blocked, not zero, not a start range.
    public static bool SaneRange(float y) => !float.IsNaN(y) && y >= 1f && y <= 1000f;
    // Respawn timers that can be the game's normal ones (a start sets both to 0).
    public static bool SaneTimers(double min, double max) => min > 0 && max >= min;

    // A route with no caravan out: waiting for a new squad, or standing at its last stop until the game removes
    // what is left and returns it to the start (WaitingForDespawn). The second kind must be blocked too: a player
    // coming near wakes it, the game resets it and would spawn its own squad before the next check.
    public static bool Idle(string spawnerState) => spawnerState == "WaitingForSpawn" || spawnerState == "WaitingForDespawn";

    // Source: what to write into a route that isn't being started by the mod; null = leave it.
    // With Mod a route waiting for a new squad is blocked; any other route gets the game's range back
    // if it is blocked (or zero, which an earlier version could leave).
    // normalY: the game's normal range (LearnRange; NormalY until it is known).
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

    // The game's normal values, learned from the game itself (Starter.NormalOf). A route patrol has no prefab to
    // read them from (patrol dump of 12.11: no PrefabGUID).
    //
    // The range can't be learned from the carriage routes: with Source = Mod every one of them is blocked while it
    // waits and opened again by the mod with the value it takes for normal, and that stays in the save. It is taken
    // from the game's other patrols, which the mod never writes into: on 12.11 all 189 of them had the same 90 m as
    // the carriage routes had before the mod blocked any.
    public static float? LearnRange(IEnumerable<float> otherPatrols)
    {
        var seen = new Dictionary<float, int>();
        foreach (float y in otherPatrols)
            if (SaneRange(y)) seen[y] = (seen.TryGetValue(y, out int n) ? n : 0) + 1;
        return Common(seen);
    }

    // The respawn timers are the carriage routes' own (1000-2700 s; other patrols have theirs, 3-5 s to
    // 1800-2100 s on 12.11), so they are the most common ones among the carriage routes. A start's zero timers never
    // count. The mod writes other timers only when it repairs a route, with what it has learned here, so the game's
    // own are the majority.
    public static (double Modifier, double Min, double Max)? LearnTimers(IEnumerable<(double Modifier, double Min, double Max)> routes)
    {
        var seen = new Dictionary<(double, double, double), int>();
        foreach (var r in routes)
            if (SaneTimers(r.Min, r.Max) && r.Modifier > 0)
                seen[(r.Modifier, r.Min, r.Max)] = (seen.TryGetValue((r.Modifier, r.Min, r.Max), out int n) ? n : 0) + 1;
        return Common(seen);
    }

    // The value seen most often, if at least two show it and no other value is seen as often: one odd patrol, or
    // two values in a draw, teach nothing.
    static T? Common<T>(Dictionary<T, int> seen) where T : struct
    {
        T? best = null;
        int most = 0, second = 0;
        foreach (var kv in seen)
        {
            if (kv.Value > most) { second = most; most = kv.Value; best = kv.Key; }
            else if (kv.Value > second) second = kv.Value;
        }
        return most >= 2 && most > second ? best : null;
    }
}
