using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using VampireCommandFramework;

namespace CarriageTracker;

[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
[BepInProcess("VRisingServer.exe")]
[BepInDependency("gg.deca.VampireCommandFramework")]
public class Plugin : BasePlugin
{
    internal static ManualLogSource Logger;
    Harmony _harmony;
    Ticker _ticker;

    // Bloodcraft (zfolmt) with its leveling sets the character level the game shows (Equipment), which
    // PlayerAct = Level reads.
    internal static bool HasBloodcraft => IL2CPPChainloader.Instance.Plugins.ContainsKey("io.zfolmt.Bloodcraft");

    // The BepInEx log has no time, so every line of the mod starts with the server's time: then the log
    // shows how long a squad took to appear, to assemble, to drive its route.
    static string Now => DateTime.Now.ToString("HH:mm:ss");
    internal static void Info(string message) => Logger.LogInfo($"{Now} {message}");
    internal static void Warn(string message) => Logger.LogWarning($"{Now} {message}");
    internal static void Error(string message) => Logger.LogError($"{Now} {message}");

    // An error that repeats (the same place, type and message) is written in full once per 10 minutes; the repeats in
    // between are only counted (RepeatLog). Until 1.0.21 an error at every check wrote a full stack trace every 30 s
    // (audit 07.11 by Nikolai, R5).
    static readonly RepeatLog Repeated = new(TimeSpan.FromMinutes(10));
    internal static long Repeats => Repeated.Skipped;

    internal static void Fail(string where, Exception e)
    {
        if (!Repeated.Report($"{where}|{e.GetType().FullName}: {e.Message}", DateTime.UtcNow, out int again)) return;
        Error($"{where}{(again > 0 ? $" (the same error {again} more times since its last full line)" : "")}: {e}");
    }

    // Repeats of errors that have stopped coming: one line each, so their count isn't lost (Memory calls it).
    internal static void FlushRepeats()
    {
        foreach (var (key, count) in Repeated.Flush(DateTime.UtcNow))
        {
            int bar = key.IndexOf('|');
            Warn($"{key.Substring(0, bar)}: the error \"{key.Substring(bar + 1)}\" came {count} more times after its last full line, then stopped");
        }
    }

    // Detailed lines (every check, every step of a started caravan) only with Debug.VerboseLog = true.
    internal static void Trace(string message)
    {
        if (Settings.VerboseLog != null && Settings.VerboseLog.Value) Info(message);
    }

    public override void Load()
    {
        Logger = Log;
        // Until 1.0.11 the mod was called CaravanTracker. Both copies at once would start caravans twice.
        string old = OldCopy();
        if (old != null)
        {
            Log.LogError($"CarriageTracker: {old} is the same mod under its old name. Delete it (the folder BepInEx/plugins/CaravanTracker) " +
                         "and restart the server; until then CarriageTracker does nothing, so only one copy runs.");
            return;
        }
        MoveOldConfig(Config);
        Settings.Bind(Config);
        try { _harmony = Harmony.CreateAndPatchAll(typeof(MapIconSpawnPatch), MyPluginInfo.PLUGIN_GUID); }
        catch (Exception e) { Log.LogError($"CarriageTracker: Harmony patch failed, proxy icons become global only on the next check: {e}"); }
        // The memory watch counts the log lines of the game and of every plugin (MemoryWatch).
        try { BepInEx.Logging.Logger.Listeners.Add(MemoryWatch.Log = new LogCounter()); }
        catch (Exception e) { MemoryWatch.Log = null; Log.LogWarning($"CarriageTracker: the memory watch can't count log lines: {e.Message}"); }
        _ticker = AddComponent<Ticker>();
        CommandRegistry.RegisterCommandType(typeof(Commands));
        if (Settings.EnableDiagnostics.Value) CommandRegistry.RegisterCommandType(typeof(DiagCommands));
        Log.LogInfo($"{MyPluginInfo.PLUGIN_NAME} {MyPluginInfo.PLUGIN_VERSION} loaded, source {Settings.Source.Value}, auto-start every {Settings.AutoInterval.Value} min");
        if (Settings.ModOnly && Settings.AutoInterval.Value <= 0)
            Log.LogWarning("CarriageTracker: Source = Mod and IntervalMinutes = 0: neither the game nor the mod starts caravans, there will be none.");
    }

    static string OldCopy()
    {
        try
        {
            var files = Directory.GetFiles(Paths.PluginPath, "CaravanTracker.dll", SearchOption.AllDirectories);
            return files.Length > 0 ? files[0] : null;
        }
        catch { return null; }
    }

    // The settings of the old name (Bazarov.CaravanTracker.cfg) move to this name's file on the first start.
    void MoveOldConfig(ConfigFile config)
    {
        try
        {
            string old = Path.Combine(Paths.ConfigPath, "Bazarov.CaravanTracker.cfg");
            if (File.Exists(config.ConfigFilePath) || !File.Exists(old)) return;
            File.Move(old, config.ConfigFilePath);
            config.Reload();
            Log.LogInfo($"CarriageTracker: settings moved from {Path.GetFileName(old)} to {Path.GetFileName(config.ConfigFilePath)}");
        }
        catch (Exception e) { Log.LogWarning($"CarriageTracker: can't move the old settings file: {e.Message}"); }
    }

    // Unloading at runtime: the work stops first, then the world gets back what the mod changed (as with
    // .cv cleanup: starts stopped, routes given the game's settings, icons and markers removed), then the
    // commands and the patch go. Until 1.0.10 only the commands went and the Ticker kept running.
    public override bool Unload()
    {
        Ticker.Stopped = true;
        try
        {
            var world = Caravans.ServerWorld();
            var prefabs = world == null ? null : Caravans.Prefabs(world);
            if (prefabs != null && Settings.Source != null)
            {
                var r = Starter.Cleanup(world.EntityManager, prefabs);
                Tracker.Tick(false);
                Log.LogInfo($"CarriageTracker unloading: stopped {r.Runs} starts, restored {r.Routes} routes and {r.Units} units, icons removed");
            }
        }
        catch (Exception e) { Log.LogError($"CarriageTracker unloading: can't give the world back its settings: {e}"); }
        if (_ticker != null) UnityEngine.Object.Destroy(_ticker);
        try { if (MemoryWatch.Log != null) BepInEx.Logging.Logger.Listeners.Remove(MemoryWatch.Log); } catch { }
        MemoryWatch.Log = null;
        CommandRegistry.UnregisterAssembly();
        _harmony?.UnpatchSelf();
        return true;
    }
}

// Runs every server frame. Forced caravan starts are checked every frame (the work itself once a second),
// the spawn block (Source) and routes waiting for a repair every 5 seconds, the caravan search and auto-start
// every CheckSeconds.
public class Ticker : UnityEngine.MonoBehaviour
{
    public Ticker(IntPtr ptr) : base(ptr) { }
    static DateTime _next;
    static DateTime _nextSource;
    internal static bool Stopped;

    public void Update()
    {
        if (Stopped) return;
        long began = System.Diagnostics.Stopwatch.GetTimestamp();
        MemoryWatch.FrameStart(began);
        Work();
        MemoryWatch.Frame((System.Diagnostics.Stopwatch.GetTimestamp() - began) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
    }

    static void Work()
    {
        if (Starter.Any)
        {
            try
            {
                var world = Caravans.ServerWorld();
                if (world != null) Starter.Update(world.EntityManager);
            }
            catch (Exception e) { Plugin.Fail("CarriageTracker start", e); }
        }
        try { Memory.Tick(); }
        catch (Exception e) { Plugin.Fail("CarriageTracker memory", e); }
        try { MemoryWatch.Tick(); }
        catch (Exception e) { Plugin.Fail("CarriageTracker memory watch", e); }
        var now = DateTime.UtcNow;
        if (now >= _next)
        {
            _next = now.AddSeconds(Math.Max(5, Settings.CheckSeconds.Value));
            try { Tracker.Tick(false); }
            catch (Exception e) { Plugin.Fail("CarriageTracker", e); }
            try { AutoStart.Tick(); }
            catch (Exception e) { Plugin.Fail("CarriageTracker auto", e); }
        }
        // A route that has just started waiting for a new caravan is blocked within 5 seconds, so a player
        // standing next to it doesn't get a caravan from the game in between.
        if (now >= _nextSource && !Starter.Paused)
        {
            _nextSource = now.AddSeconds(5);
            try
            {
                var world = Caravans.ServerWorld();
                var prefabs = world == null ? null : Caravans.Prefabs(world);
                if (prefabs != null)
                {
                    Starter.ApplySource(world.EntityManager, prefabs);
                    Starter.RetryRepairs(world.EntityManager, prefabs);
                }
            }
            catch (Exception e) { Plugin.Fail("CarriageTracker source", e); }
        }
    }
}
