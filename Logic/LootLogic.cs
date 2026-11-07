namespace CarriageTracker;

// How long the loot container of a robbed carriage stays (setting LootKeepMinutes).
// The game's own timers, read with .cvdiag comps on 04.11 (TM_CarriageContainer_ChuchGrape):
//   DestroyAfterDuration: Duration=60, EndTime = server time it is removed at;
//   DestroyAfterDuration_ActiveUserCheck: Range=20, DurationModifier=0.1 (with a player within 20 m the timer
//     runs ten times slower, about 10 minutes; with nobody near, 60 seconds);
//   DestroyAfterTimeOnInventoryChange: Duration=60 once something was taken.
// So a container whose robber died and respawned far away was gone a minute later (03.11). The mod pushes
// EndTime out to LootKeepMinutes after the container was first seen, every check while it still has loot.
// Once it is emptied it goes as the game would have it: at most AfterEmptied seconds later (EmptiedEndTime).
// Until 1.0.19 the pushed time stayed, and an empty container stood out its full LootKeepMinutes (07.11).
public static class LootLogic
{
    // The new EndTime, or null to leave it: keepSeconds after firstSeen, never earlier than the game's own.
    public static double? EndTime(double firstSeen, double keepSeconds, double endTime)
    {
        if (keepSeconds <= 0) return null;
        double until = firstSeen + keepSeconds;
        return endTime < until ? until : null;
    }

    // The game removes a container 60 s after something was taken from it (DestroyAfterTimeOnInventoryChange).
    public const double AfterEmptied = 60;

    // The EndTime for a container the mod kept that has just been emptied, or null to leave it: now + AfterEmptied,
    // never later than the time already set.
    public static double? EmptiedEndTime(double now, double endTime)
    {
        double until = now + AfterEmptied;
        return endTime > until ? until : null;
    }
}
