using System;
using System.Collections.Generic;

namespace CarriageTracker;

// Errors that repeat (audit 07.11 by Nikolai, R5): the same one (place, type, message) is written in full once per
// window; the repeats in between are only counted. A count is never lost: it goes out with the next full line of that
// error, or, once the error hasn't come for a whole window, with Flush (review of 1.0.21: until 1.0.26 the count of an
// error that stopped was only in the memory line's total).
public sealed class RepeatLog
{
    sealed class Entry
    {
        public DateTime Written, Seen;
        public int Count;
    }

    readonly TimeSpan _window;
    readonly int _max;
    readonly Dictionary<string, Entry> _seen = new();
    // Counts of errors that stopped, taken out of the table to make room, waiting for Flush.
    readonly List<(string Key, int Count)> _stopped = new();

    // Repeats not written in full, since the start; and those of errors past the limit of different errors.
    public long Skipped { get; private set; }

    public RepeatLog(TimeSpan window, int max = 100)
    {
        _window = window;
        _max = max;
    }

    // Whether to write the error in full now (true), and how many repeats of it were counted since its last full line.
    public bool Report(string key, DateTime now, out int repeated)
    {
        repeated = 0;
        if (_seen.TryGetValue(key, out var e))
        {
            if (now - e.Written < _window)
            {
                e.Count++;
                e.Seen = now;
                Skipped++;
                return false;
            }
            repeated = e.Count;
            e.Written = e.Seen = now;
            e.Count = 0;
            return true;
        }
        if (_seen.Count >= _max)
        {
            Stopped(now);
            if (_seen.Count >= _max) { Skipped++; return false; } // that many different errors within one window: counted only
        }
        _seen[key] = new Entry { Written = now, Seen = now };
        return true;
    }

    // Errors that haven't come for a whole window: forgotten, and those with repeats counted are given back to be
    // written, each once.
    public List<(string Key, int Count)> Flush(DateTime now)
    {
        Stopped(now);
        var done = new List<(string Key, int Count)>(_stopped);
        _stopped.Clear();
        return done;
    }

    void Stopped(DateTime now)
    {
        foreach (var k in new List<string>(_seen.Keys))
        {
            var e = _seen[k];
            if (now - e.Seen < _window) continue;
            if (e.Count > 0) _stopped.Add((k, e.Count));
            _seen.Remove(k);
        }
    }
}
