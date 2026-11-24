using System;
using System.Collections.Generic;

namespace CarriageTracker;

// Errors that repeat (audit 07.11 by Nikolai, R5): the same one (place, type, message) is written in full once per
// window; the repeats in between are only counted. A count is never lost: it goes out with the next full line of that
// error, or, if the error stops coming, with Flush once its window has passed (review of 1.0.21: until 1.0.26 the
// count of an error that stopped was only in the memory line's total).
public sealed class RepeatLog
{
    readonly TimeSpan _window;
    readonly int _max;
    readonly Dictionary<string, (DateTime At, int Count)> _seen = new();

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
        if (_seen.TryGetValue(key, out var last) && now - last.At < _window)
        {
            _seen[key] = (last.At, last.Count + 1);
            Skipped++;
            return false;
        }
        if (!_seen.ContainsKey(key) && _seen.Count >= _max)
        {
            Prune(now, null);
            if (_seen.Count >= _max) { Skipped++; return false; } // that many different errors within one window: counted only
        }
        repeated = last.Count;
        _seen[key] = (now, 0);
        return true;
    }

    // Errors whose window has passed: forgotten, and those with repeats counted are given back to be written.
    public List<(string Key, int Count)> Flush(DateTime now)
    {
        var done = new List<(string Key, int Count)>();
        Prune(now, done);
        return done;
    }

    void Prune(DateTime now, List<(string Key, int Count)> done)
    {
        foreach (var k in new List<string>(_seen.Keys))
        {
            var v = _seen[k];
            if (now - v.At < _window) continue;
            if (done != null && v.Count > 0) done.Add((k, v.Count));
            else if (done == null && v.Count > 0) continue; // its count waits for Flush
            _seen.Remove(k);
        }
    }
}
