using System.Collections.Generic;

namespace CarriageTracker;

// When the memory watch speaks. On 12.11 the server was killed for memory twice with nobody online: the game heap
// went from 69 MB to 1570 MB within 10 minutes and the process from 4.08 to 5.02 to 7.49 GiB minute by minute. The
// 10-minute memory line saw the result but not what was happening, so the watch samples every 10 seconds and
// writes at once when memory jumps or the log floods.
public static class MemoryLogic
{
    public const int SampleSeconds = 10;
    // Samples kept: the newest and the six before it, a minute in all.
    public const int Kept = 7;
    // The game heap of this server stays at 66-69 MB for hours; 50 MB more within a minute is not normal work.
    public const long HeapMinuteMb = 50;
    // The process grows and shrinks more (saves, players coming in), so its limits are wider.
    public const long RamStepMb = 100, RamMinuteMb = 300;
    // 50 log lines a second for 10 seconds.
    public const int FloodLines = 500;
    // The world loads in the first minutes and memory grows fast then.
    public const int QuietAfterStartSeconds = 300;
    public const int LineEverySeconds = 30, LinesPerIncident = 20, IncidentEndsSeconds = 600, IncidentMaxSeconds = 3600;
    // On 14.11 the server was killed 40 s after the first line and only two lines were written. The first lines of
    // an incident come at every sample.
    public const int FastLines = 6;
    // The heap is this much over its quiet size: time to ask the game's collector what of it is garbage.
    public const long CollectOverMb = 300;

    // Samples oldest first, the newest last, in MB. A sample that couldn't be read is 0 and never counts.
    public static bool HeapJump(IReadOnlyList<long> mb) => Grew(mb, long.MaxValue, HeapMinuteMb);
    public static bool RamJump(IReadOnlyList<long> mb) => Grew(mb, RamStepMb, RamMinuteMb);

    static bool Grew(IReadOnlyList<long> mb, long step, long minute)
    {
        int n = mb.Count;
        if (n < 2 || mb[n - 1] <= 0) return false;
        if (mb[n - 2] > 0 && mb[n - 1] - mb[n - 2] >= step) return true;
        long oldest = 0;
        for (int i = 0; i < n - 1 && oldest <= 0; i++) oldest = mb[i];
        return oldest > 0 && mb[n - 1] - oldest >= minute;
    }

    public static bool Flood(int logLines) => logLines >= FloodLines;

    // One forced collection per incident, once the heap is far over its quiet size. What it frees tells garbage
    // (made faster than the game collects it, or the collector not running) from objects something still holds.
    public static bool Collect(long quietMb, long nowMb) => quietMb > 0 && nowMb - quietMb >= CollectOverMb;

    // After the forced collection: was most of what had grown freed? "No" is the weaker answer: the size read
    // right after one collection is an upper bound of what is alive (review of 1.0.25).
    public static bool WasGarbage(long quietMb, long beforeMb, long afterMb) =>
        beforeMb > quietMb && (beforeMb - afterMb) * 2 >= beforeMb - quietMb;

    // The heap's quiet size: the smallest of the kept samples (a minute), unread ones (0) aside; 0 if none.
    public static long Quiet(IReadOnlyList<long> mb)
    {
        long least = 0;
        foreach (long v in mb)
            if (v > 0 && (least == 0 || v < least)) least = v;
        return least;
    }

    // How many lines an incident may write: the first at once, the next ones no more often than every 30 seconds,
    // 20 in all, so the watch doesn't add to the trouble it reports. An incident ends after 10 minutes without an
    // alarm, and after an hour in any case: something harmless that alarms every few minutes for days (a save
    // that grows the process, a talkative plugin) must not keep the watch silent when the real jump comes. The
    // game heap has a budget of its own for the same reason (review of 1.0.24).
    public sealed class Budget
    {
        double _first, _lastLine, _lastAlarm;
        int _lines;

        public int Lines => _lines;

        // An incident that wrote lines and hasn't ended by the time t (seconds on any steady clock).
        public bool Open(double t) => _lines > 0 && t - _lastAlarm < IncidentEndsSeconds && t - _first < IncidentMaxSeconds;

        // An alarm at the time t: is a line written for it?
        public bool Alarm(double t)
        {
            if (!Open(t)) _lines = 0;
            _lastAlarm = t;
            if (_lines >= LinesPerIncident) return false;
            if (_lines >= FastLines && t - _lastLine < LineEverySeconds) return false;
            if (_lines == 0) _first = t;
            _lastLine = t;
            _lines++;
            return true;
        }
    }
}
