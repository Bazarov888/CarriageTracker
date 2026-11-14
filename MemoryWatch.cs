using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using BepInEx.Logging;
using ProjectM;
using ProjectM.Network;
using Stunlock.Core;
using Unity.Entities;

namespace CarriageTracker;

// Counts the lines every plugin and the game write to the BepInEx log, by who wrote them. A flood of errors is the
// first thing to look for when the game heap grows on an empty server. Log lines can come from other threads.
sealed class LogCounter : ILogListener
{
    readonly object _lock = new();
    Dictionary<string, (int Count, int Bad, string First)> _by = new();

    // The levels the log file takes anyway. Asking for Debug too would make BepInEx build every plugin's debug
    // lines, which nobody else listens to.
    public LogLevel LogLevelFilter => LogLevel.Fatal | LogLevel.Error | LogLevel.Warning | LogLevel.Message | LogLevel.Info;

    public void LogEvent(object sender, LogEventArgs e)
    {
        try
        {
            string source = e.Source?.SourceName ?? "?";
            bool bad = (e.Level & (LogLevel.Fatal | LogLevel.Error | LogLevel.Warning)) != 0;
            lock (_lock)
            {
                if (_by.Count >= 40 && !_by.ContainsKey(source)) source = "others";
                _by.TryGetValue(source, out var v);
                string first = v.First;
                // Only the first warning or error of a source in an interval is kept as text.
                if (bad && first == null)
                {
                    first = e.Data?.ToString() ?? "";
                    if (first.Length > 160) first = first.Substring(0, 160);
                    first = first.Replace('\r', ' ').Replace('\n', ' ');
                }
                _by[source] = (v.Count + 1, v.Bad + (bad ? 1 : 0), first);
            }
        }
        catch { }
    }

    // What was written since the last call.
    public Dictionary<string, (int Count, int Bad, string First)> Take()
    {
        lock (_lock)
        {
            var taken = _by;
            _by = new Dictionary<string, (int, int, string)>();
            return taken;
        }
    }

    public void Dispose() { }
}

// Samples memory every 10 seconds and, when it jumps or the log floods (MemoryLogic), writes at once what the
// world, the log and the mod were doing: to the log and to BepInEx/CarriageTracker-memory.txt, which is written
// through right away and so survives the server being killed.
static class MemoryWatch
{
    static readonly DateTime Origin = DateTime.UtcNow;
    static DateTime _next = DateTime.UtcNow, _nextBase = DateTime.MinValue, _worldSince = DateTime.MinValue;
    static readonly List<long> _heap = new(), _ram = new();
    static readonly List<DateTime> _at = new();
    static readonly MemoryLogic.Budget _heapLines = new(), _otherLines = new();
    static int _frames;
    static bool _fileFull;
    static long _lastFrameAt, _quietHeap;
    static double _longestGapMs;
    static int _collections = -1;
    static bool _collected;
    static double _workMs, _longestMs;
    static (int All, int Prefab, int Networked) _base;
    static bool _fileFailed;
    internal static LogCounter Log;

    // The time the mod's own per-frame work took, and how many frames the server made.
    public static void Frame(double ms)
    {
        _frames++;
        _workMs += ms;
        if (ms > _longestMs) _longestMs = ms;
        // The longest time between two frames: a server that stalls (a save, a collection) shows here.
        long at = Stopwatch.GetTimestamp();
        if (_lastFrameAt != 0)
        {
            double gap = (at - _lastFrameAt) * 1000.0 / Stopwatch.Frequency;
            if (gap > _longestGapMs) _longestGapMs = gap;
        }
        _lastFrameAt = at;
    }

    // Objects in the server world: all of them, those made from a prefab, and those sent to players.
    public static (int All, int Prefab, int Networked) Objects()
    {
        var world = Caravans.ServerWorld();
        if (world == null) return (-1, -1, -1);
        var em = world.EntityManager;
        int all = em.UniversalQuery.CalculateEntityCount();
        int prefab = Queries.Get(em, "watch prefab", true, Ct<PrefabGUID>.RO).CalculateEntityCount();
        int net = Queries.Get(em, "watch networked", true, Ct<PrefabGUID>.RO, Ct<NetworkId>.RO).CalculateEntityCount();
        return (all, prefab, net);
    }

    public static string ObjectsText()
    {
        try
        {
            var o = Objects();
            return o.All < 0 ? "world not ready" : $"{o.All}, from prefabs {o.Prefab}, networked {o.Networked}";
        }
        catch (Exception e) { return $"unknown ({e.GetType().Name})"; }
    }

    static void Keep(List<long> list, long mb)
    {
        list.Add(mb);
        if (list.Count > MemoryLogic.Kept) list.RemoveAt(0);
    }

    // "previous -> now", and the oldest sample kept with how long ago it really was (a stalling server samples late).
    static string Was(List<long> list)
    {
        int n = list.Count;
        string step = n >= 2 ? $"{list[n - 2]} -> {list[n - 1]}" : $"{list[n - 1]}";
        return n >= 3 ? $"{step} MB ({Math.Round((_at[n - 1] - _at[0]).TotalSeconds)} s ago {list[0]})" : $"{step} MB";
    }

    public static void Tick()
    {
        var now = DateTime.UtcNow;
        if (now < _next) return;
        double seconds = MemoryLogic.SampleSeconds + (now - _next).TotalSeconds;
        _next = now.AddSeconds(MemoryLogic.SampleSeconds);

        long heap = 0, ram = 0;
        try { heap = Il2CppSystem.GC.GetTotalMemory(false) >> 20; } catch { }
        try { using var p = Process.GetCurrentProcess(); ram = p.WorkingSet64 >> 20; } catch { }
        Keep(_heap, heap);
        Keep(_ram, ram);
        _at.Add(now);
        if (_at.Count > MemoryLogic.Kept) _at.RemoveAt(0);
        var log = Log?.Take();
        int logLines = 0;
        if (log != null) foreach (var kv in log) logLines += kv.Value.Count;
        int frames = _frames;
        double workMs = _workMs, longestMs = _longestMs, longestGapMs = _longestGapMs;
        _frames = 0; _workMs = 0; _longestMs = 0; _longestGapMs = 0;
        // How many times the game's collector ran since the last sample.
        int ran = -1;
        try
        {
            int total = Il2CppSystem.GC.CollectionCount(0);
            if (_collections >= 0) ran = total - _collections;
            _collections = total;
        }
        catch { }

        // Quiet while the world loads: 5 minutes from the first sample that finds the server world.
        if (_worldSince == DateTime.MinValue)
        {
            if (Caravans.ServerWorld() == null) return;
            _worldSince = now;
        }
        if ((now - _worldSince).TotalSeconds < MemoryLogic.QuietAfterStartSeconds) return;
        double t = (now - Origin).TotalSeconds;

        bool heapJump = MemoryLogic.HeapJump(_heap), ramJump = MemoryLogic.RamJump(_ram), flood = MemoryLogic.Flood(logLines);
        if (!heapJump && !ramJump && !flood)
        {
            if (!_heapLines.Open(t))
            {
                if (heap > 0) _quietHeap = heap;
                _collected = false;
            }
            // What the world normally holds, to compare with: counted every 10 minutes, only at a sample without
            // an alarm and outside an incident.
            if (now >= _nextBase && !_heapLines.Open(t) && !_otherLines.Open(t))
            {
                _nextBase = now.AddMinutes(10);
                try { _base = Objects(); } catch { }
            }
            return;
        }
        // Both budgets are asked, so each counts its own alarm.
        bool write = heapJump && _heapLines.Alarm(t);
        if ((ramJump || flood) && _otherLines.Alarm(t)) write = true;
        if (!write) return;
        int lineNo = heapJump ? _heapLines.Lines : _otherLines.Lines;

        var why = new List<string>();
        if (heapJump) why.Add("game heap grows");
        if (ramJump) why.Add("process grows");
        if (flood) why.Add("log floods");
        string mod;
        try
        {
            var doomed = Doomed.Counts(now);
            mod = $"caravans {Tracker.Current.Count}, starts {Starter.RunCount}, trial {(Starter.HasTrial ? "yes" : "no")}, destroy queue {doomed.Waiting}, " +
                  $"icons {Icons.Live}, markers {Proxies.Live}, repeated errors {Plugin.Repeats}";
        }
        catch (Exception e) { mod = $"unknown ({e.GetType().Name})"; }
        // Once per incident, with the heap far over its quiet size: what does a full collection free?
        string collect = "";
        if (heapJump && !_collected && MemoryLogic.Collect(_quietHeap, heap))
        {
            _collected = true;
            try
            {
                long began = Stopwatch.GetTimestamp();
                Il2CppSystem.GC.Collect();
                long after = Il2CppSystem.GC.GetTotalMemory(false) >> 20;
                double took = (Stopwatch.GetTimestamp() - began) * 1000.0 / Stopwatch.Frequency;
                collect = $"; forced collection: {heap} -> {after} MB in {Math.Round(took)} ms, " +
                          (MemoryLogic.WasGarbage(_quietHeap, heap, after) ? "most of the growth was garbage" : "most of the growth is still held by something");
            }
            catch (Exception e) { collect = $"; forced collection failed ({e.GetType().Name})"; }
        }
        string was = _base.All > 0 ? $" (when quiet: {_base.All}, from prefabs {_base.Prefab}, networked {_base.Networked})" : "";
        string line = $"CarriageTracker memory watch, line {lineNo} of {MemoryLogic.LinesPerIncident} ({string.Join(", ", why)}): " +
                      $"game heap {Was(_heap)} (quiet {_quietHeap}), process in RAM {Was(_ram)}, plugins' .NET heap {GC.GetTotalMemory(false) >> 20} MB{collect}; " +
                      $"collector runs in the interval {(ran < 0 ? "unknown" : ran.ToString())}, {UnityText()}; " +
                      $"players online {PlayersText()}; newest save {SaveText()}; " +
                      $"world objects {ObjectsText()}{was}; " +
                      $"log lines in the last {Math.Round(seconds)} s: {LogText(log, logLines)}; " +
                      $"server frames {frames}, longest gap between frames {Math.Round(longestGapMs)} ms, the mod's work in them {Math.Round(workMs)} ms, longest {Math.Round(longestMs, 1)} ms; mod: {mod}";
        Plugin.Warn(line);
        ToFile(line);
    }

    // Unity's own account: its native memory (not the collector's heap) and the collector's heap as Unity sees it.
    static string UnityText()
    {
        try
        {
            long used = UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() >> 20;
            long reserved = UnityEngine.Profiling.Profiler.GetTotalReservedMemoryLong() >> 20;
            long monoUsed = UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() >> 20;
            long monoHeap = UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong() >> 20;
            return $"Unity native {used} MB of {reserved} reserved, collector's heap {monoUsed} MB used of {monoHeap}, collector {UnityEngine.Scripting.GarbageCollector.GCMode}";
        }
        catch (Exception e) { return $"Unity memory unknown ({e.GetType().Name})"; }
    }

    static string PlayersText()
    {
        try
        {
            var world = Caravans.ServerWorld();
            if (world == null) return "unknown";
            var em = world.EntityManager;
            var users = Queries.Get(em, "User", false, Ct<User>.RO).ToEntityArray(Unity.Collections.Allocator.Temp);
            try
            {
                int online = 0;
                foreach (var u in users) if (em.GetComponentData<User>(u).IsConnected) online++;
                return $"{online} of {users.Length}";
            }
            finally { users.Dispose(); }
        }
        catch (Exception e) { return $"unknown ({e.GetType().Name})"; }
    }

    // The newest file under save-data/Saves: a save being written at the moment of a jump shows as a file a few
    // seconds old.
    static string SaveText()
    {
        try
        {
            string root = Path.Combine(BepInEx.Paths.GameRootPath, "save-data", "Saves");
            if (!Directory.Exists(root)) return "folder not found";
            FileInfo newest = null;
            int seen = 0;
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (++seen > 2000) break;
                var f = new FileInfo(path);
                if (newest == null || f.LastWriteTimeUtc > newest.LastWriteTimeUtc) newest = f;
            }
            if (newest == null) return "none";
            return $"{newest.Name}, {newest.Length >> 10} KB, written {Math.Round((DateTime.UtcNow - newest.LastWriteTimeUtc).TotalSeconds)} s ago ({seen} files)";
        }
        catch (Exception e) { return $"unknown ({e.GetType().Name})"; }
    }

    static string LogText(Dictionary<string, (int Count, int Bad, string First)> log, int total)
    {
        if (log == null) return "not counted";
        if (total == 0) return "0";
        var sources = new List<KeyValuePair<string, (int Count, int Bad, string First)>>(log);
        sources.Sort((a, b) => b.Value.Count.CompareTo(a.Value.Count));
        var parts = new List<string>();
        for (int i = 0; i < sources.Count && i < 5; i++)
        {
            var v = sources[i].Value;
            string bad = v.Bad > 0 ? $", warnings and errors {v.Bad}, the first: \"{v.First}\"" : "";
            parts.Add($"{sources[i].Key} {v.Count}{bad}");
        }
        string more = sources.Count > 5 ? $"; {sources.Count - 5} more sources" : "";
        return $"{total} ({string.Join("; ", parts)}{more})";
    }

    // The BepInEx log is written to disk with a delay; a killed server loses its last lines. This file doesn't.
    static void ToFile(string line)
    {
        if (_fileFailed) return;
        try
        {
            string path = Path.Combine(BepInEx.Paths.BepInExRootPath, "CarriageTracker-memory.txt");
            var info = new FileInfo(path);
            if (info.Exists && info.Length > 1 << 20)
            {
                if (!_fileFull) Plugin.Warn("CarriageTracker memory watch: CarriageTracker-memory.txt is over 1 MB, new lines go to the log only. Delete the file to have them there again.");
                _fileFull = true;
                return;
            }
            _fileFull = false;
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
        }
        catch (Exception e)
        {
            _fileFailed = true;
            Plugin.Warn($"CarriageTracker memory watch: can't write CarriageTracker-memory.txt ({e.GetType().Name}: {e.Message}); the lines stay in the log only");
        }
    }
}
