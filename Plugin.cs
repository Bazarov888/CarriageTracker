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
    LanguageMiddleware _language;
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
    // between are only counted, and the count is written with the next full line and in the memory line. Until
    // 1.0.21 an error at every check wrote a full stack trace every 30 s (audit 07.11 by Nikolai, R5).
    static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(10);
    static readonly Dictionary<string, (DateTime At, int Count)> Seen = new();
    internal static long Repeats;

    internal static void Fail(string where, Exception e)
    {
        string key = $"{where}|{e.GetType().FullName}|{e.Message}";
        var now = DateTime.UtcNow;
        if (Seen.TryGetValue(key, out var last) && now - last.At < RepeatWindow)
        {
            Seen[key] = (last.At, last.Count + 1);
            Repeats++;
            return;
        }
        if (Seen.Count >= 100) Seen.Clear();
        Seen[key] = (now, 0);
        string again = last.Count > 0 ? $" (the same error {last.Count} more times in the last {Math.Round((now - last.At).TotalMinutes)} min)" : "";
        Error($"{where}{again}: {e}");
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
        _ticker = AddComponent<Ticker>();
        CommandRegistry.Middlewares.Add(_language = new LanguageMiddleware());
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
        CommandRegistry.UnregisterAssembly();
        if (_language != null) CommandRegistry.Middlewares.Remove(_language);
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
        // Background work speaks the server's default language (a command that failed midway could leave
        // its player's language set).
        L.UseDefault();
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
