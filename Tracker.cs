using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using ProjectM;
using ProjectM.Network;
using ProjectM.Scripting;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace CarriageTracker;

// Announcement to online players, two lines. The first one: which caravan and of which act.
// The second one is personal: distance, direction and the mark command with this caravan's number in their .cv l.
// Who gets it is set by AnnounceTo: everyone, players of the caravan's act, or players within AnnounceRadius.
static class Announcer
{
    public static void CaravanStarted(EntityManager em, PrefabCollectionSystem prefabs, string route, Entity patrol, Entity cart, float3 pos)
    {
        var acts = new SortedSet<int>();
        try { acts = Acts.OfRouteKey(em, prefabs, route); }
        catch (Exception e) { Plugin.Warn($"CarriageTracker: act of route {route} unknown: {e.Message}"); }
        bool sameAct = Settings.AnnounceSameAct && acts.Count > 0;
        bool nearby = Settings.AnnounceNearby;
        float radius = Math.Max(100, Settings.AnnounceRadius.Value);
        var withRoutes = sameAct ? Acts.WithRoutes(em, prefabs) : null;
        int sent = 0;
        string title = Settings.Color(Settings.TitleColor, "#FFC83C");
        string text = Settings.Color(Settings.TextColor, "#FFFFFF");
        string command = Settings.Color(Settings.CommandColor, "#7CFC7C");
        string language = L.Current;

        var query = Queries.Get(em, "User", false, Ct<User>.RO);
        var users = query.ToEntityArray(Allocator.Temp);
        try
        {
            foreach (var ue in users)
            {
                var user = em.GetComponentData<User>(ue);
                if (!user.IsConnected) continue;
                if (sameAct && !Acts.Served(Acts.OfUser(em, prefabs, ue).Act, withRoutes).Exists(acts.Contains)) continue;
                string where = "";
                string mark = ".cv m";
                var character = user.LocalCharacter.GetEntityOnServer();
                bool hasCharacter = em.Exists(character);
                float3 me = hasCharacter ? Caravans.PositionOf(em, character) : float3.zero;
                float dist = hasCharacter ? Caravans.Dist2(pos, me) : float.MaxValue;
                if (nearby && dist > radius) continue;
                // Each player reads it in their own language.
                L.Use(user.PlatformId);
                try
                {
                    string name = Routes.Name(route, patrol) + (acts.Count > 0 ? " (" + L.ActList(acts) + ")" : "");
                    string head = $"<color={title}><b>{L.T("announce.title", name)}</b></color>";
                    if (hasCharacter)
                    {
                        // Direction first: a chat filter masked a color code followed by digits (07.11, see Settings.Color).
                        string w = L.T("announce.where", Math.Round(dist), Caravans.Direction(pos.x - me.x, pos.z - me.z));
                        where = (w.Length > 0 ? char.ToUpperInvariant(w[0]) + w.Substring(1) : w) + " ";
                        int n = NumberFor(me, cart);
                        mark = n == 1 ? ".cv m" : n > 1 ? $".cv m {n}" : ".cv l";
                    }
                    Chat.Send(em, user, head);
                    Chat.Send(em, user, $"<color={text}>{where}{L.T("announce.mark")} </color><color={command}><b>{mark}</b></color>");
                }
                finally { L.Use(language); }
                sent++;
            }
        }
        finally
        {
            users.Dispose();
        }
        Plugin.Trace($"CarriageTracker: announcement of {route} sent to {sent} players");
    }

    // Number of the caravan in the player's list (.cv l sorts by distance from them). 0 if not found.
    static int NumberFor(float3 me, Entity cart)
    {
        var list = new List<(float Dist, Entity Target)>();
        foreach (var c in Tracker.Current) list.Add((Caravans.Dist2(c.Pos, me), c.Target));
        list.Sort((a, b) => a.Dist.CompareTo(b.Dist));
        for (int i = 0; i < list.Count; i++)
            if (list[i].Target == cart) return i + 1;
        return 0;
    }
}

static class Chat
{
    // Texts that were too long, each written to the log once (at most 50 kept).
    static readonly HashSet<string> Cut = new();

    public static void Send(EntityManager em, User user, string text)
    {
        text = ChatText.Fit(text, out bool cut);
        if (cut && Cut.Count < 50 && Cut.Add(text))
            Plugin.Warn($"CarriageTracker: a chat message was longer than {ChatText.MaxBytes} bytes and was cut: \"{text.Substring(0, Math.Min(60, text.Length))}...\"");
        FixedString512Bytes msg = new(text);
        ServerChatUtils.SendSystemMessageToClient(em, user, ref msg);
    }

    // A message built in the language of that player.
    public static void Send(EntityManager em, Entity userEntity, Func<string> text)
    {
        if (userEntity == Entity.Null || !em.Exists(userEntity) || !Caravans.Has<User>(em, userEntity)) return;
        var user = em.GetComponentData<User>(userEntity);
        if (!user.IsConnected) return;
        var saved = L.Current;
        try { L.Use(user.PlatformId); Send(em, user, text()); }
        finally { L.Use(saved); }
    }
}

// A line for a player who has just come into the world: what the mod does and how to pick the language.
// A player who picked a language gets it in that one, others in English and in Russian (two lines).
static class Greeter
{
    static readonly HashSet<ulong> InWorld = new();
    static bool _primed;

    public static void Tick(EntityManager em)
    {
        var users = new Dictionary<ulong, User>();
        var query = Queries.Get(em, "User", false, Ct<User>.RO);
        var arr = query.ToEntityArray(Allocator.Temp);
        try
        {
            foreach (var ue in arr)
            {
                var user = em.GetComponentData<User>(ue);
                if (!user.IsConnected) continue;
                var character = user.LocalCharacter.GetEntityOnServer();
                if (!em.Exists(character) || !Caravans.OnMap(Caravans.PositionOf(em, character))) continue;
                users[user.PlatformId] = user;
            }
        }
        finally
        {
            arr.Dispose();
        }
        var greet = Welcome.Greet(InWorld, users.Keys, _primed);
        _primed = true;
        if (!Settings.Welcome.Value) return;
        foreach (var id in greet)
        {
            var own = Languages.Of(id);
            foreach (var lang in own != null ? new[] { own } : new[] { "en", "ru" })
                Chat.Send(em, users[id], Line(lang));
        }
        if (greet.Count > 0) Plugin.Trace($"CarriageTracker: welcome sent to {greet.Count} players");
    }

    // The server's own text from the config (WelcomeEn / WelcomeRu), or the mod's with the commands highlighted.
    static string Line(string lang)
    {
        string own = (lang == "ru" ? Settings.WelcomeRu : Settings.WelcomeEn).Value?.Trim();
        if (!string.IsNullOrEmpty(own)) return own;
        string text = Settings.Color(Settings.TextColor, "#FFFFFF");
        string command = Settings.Color(Settings.CommandColor, "#7CFC7C");
        string Cmd(string c) => $"<color={command}>{c}</color>";
        return $"<color={text}>{L.In(lang, "welcome", Cmd(".cv l"), Cmd(".cv m"), Cmd(".cv lang en"), Cmd(".cv lang ru"))}</color>";
    }
}

static class Tracker
{
    static List<Caravans.Caravan> _caravans = new();
    static DateTime _nextSafety = DateTime.MinValue;
    static DateTime _lastSafetyAt = DateTime.MinValue;
    static long _lastSafetyMs = -1;
    static int _lastMissed;
    static string _lastStats;
    public static string LastStats => _lastStats ?? L.T("stats.none");
    public static List<Caravans.Caravan> Current => _caravans;
    static HashSet<(int, int)> _announced = new();
    static bool _primed;
    static bool _repaired;
    static int _repairTries;
    static DateTime _lastTick = DateTime.MinValue;
    // Objects only the full search found: looked at again on every check, so they stay in the list between
    // two full searches instead of dropping out until the next one.
    static readonly Dictionary<(int, int), Entity> _missed = new();

    // Player commands (.cv l, .cv m) read the list of the last check and refresh it only if it is older than
    // MinRefresh, so commands typed in a row don't each run a whole search (audit 02.11: until 1.0.10 every
    // .cv l ran one).
    public static readonly TimeSpan MinRefresh = TimeSpan.FromSeconds(5);

    public static bool Fresh() => DateTime.UtcNow - _lastTick < MinRefresh || Tick(false);

    // Every time a fast search. A full pass runs on schedule or on command;
    // it also checks whether the fast search missed a caravan.
    public static bool Tick(bool forceFull)
    {
        var world = Caravans.ServerWorld();
        if (world == null) return false;
        var prefabs = Caravans.Prefabs(world);
        if (prefabs == null) return false;
        var em = world.EntityManager;
        var now = DateTime.UtcNow;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        var found = Caravans.FastScan(em, prefabs, out int scanned);
        var fast = new HashSet<(int, int)>();
        foreach (var f in found) fast.Add((f.Entity.Index, f.Entity.Version));
        foreach (var kv in new List<KeyValuePair<(int, int), Entity>>(_missed))
        {
            var again = fast.Contains(kv.Key) ? null : Caravans.Recheck(em, prefabs, kv.Value);
            if (again == null) { _missed.Remove(kv.Key); continue; }
            found.Add(again);
            fast.Add(kv.Key);
        }
        long fastMs = sw.ElapsedMilliseconds;

        int safetyMin = Settings.SafetyScanMinutes.Value;
        bool full = forceFull || (safetyMin > 0 && now >= _nextSafety);
        if (full)
        {
            var fsw = System.Diagnostics.Stopwatch.StartNew();
            var all = Caravans.FullScan(em, prefabs, out _);
            _lastSafetyMs = fsw.ElapsedMilliseconds;
            _lastSafetyAt = now;
            _nextSafety = now.AddMinutes(Math.Max(1, safetyMin));
            _lastMissed = 0;
            foreach (var f in all)
            {
                var key = (f.Entity.Index, f.Entity.Version);
                if (f.Kind == Kind.Horse || fast.Contains(key)) continue;
                _lastMissed++;
                found.Add(f);
                _missed[key] = f.Entity;
                Plugin.Warn($"CarriageTracker: fast search missed {f.Kind} on route {f.Route}; it is looked at on every check from now on");
            }
        }

        var built = Caravans.Build(found);
        if (_primed) LogGone(em, _caravans, built);
        else if (built.Count > 0)
            Plugin.Info($"CarriageTracker: on the map at start: " +
                                  string.Join("; ", built.ConvertAll(c => $"{c.Route} {c.State} at ({Math.Round(c.Pos.x)}, {Math.Round(c.Pos.z)})")));
        _caravans = built;

        // First check after the server start: routes left with start settings by a crash get normal values back.
        // If the pass itself fails it is tried again on the next checks (a single route that fails is queued by
        // RepairLeftovers and retried every 5 seconds).
        GameCheck.Ensure();
        if (!_repaired && _repairTries < 10)
        {
            _repairTries++;
            try
            {
                Starter.RepairLeftovers(em, prefabs);
                _repaired = true;
            }
            catch (Exception e) { Plugin.Error($"CarriageTracker: repair of routes failed (try {_repairTries} of 10): {e}"); }
        }

        // Announcement: once per cart, when it is first seen moving on the map (the game spawned it next to
        // a player, or the mod started it). A cart that froze and woke up again, or was hidden for a moment
        // as left without its animal, isn't announced again. A cart parked off the map is forgotten, so if
        // the game uses it for a new caravan, that one is announced. The first check after server start
        // only remembers the carts already on the map.
        var announced = new HashSet<(int, int)>();
        bool paused = Starter.Paused;
        foreach (var f in found)
        {
            if (paused || f.Kind != Kind.Cart || !Caravans.OnMap(f.Pos)) continue;
            var key = (f.Entity.Index, f.Entity.Version);
            bool before = _announced.Contains(key);
            if (!CaravanRules.Announce(_primed, before, true, f.Frozen, f.Orphan, f.Status))
            {
                // Carts already out at the server start, and carts announced before, are remembered.
                if (!_primed || before) announced.Add(key);
                continue;
            }
            announced.Add(key);
            bool byMod = f.Patrol != Entity.Null && Starter.IsRunning(f.Patrol);
            string stops = f.Stops != null ? $", stop {f.Stops.Log}" : "";
            Plugin.Info($"CarriageTracker: caravan {f.Route} set out at ({Math.Round(f.Pos.x)}, {Math.Round(f.Pos.z)}){stops}{(byMod ? ", started by the mod" : "")}");
            if (Settings.Announce.Value && (byMod || Settings.AnnounceVanilla.Value))
                Announcer.CaravanStarted(em, prefabs, f.Route, f.Patrol, f.Entity, f.Pos);
        }
        _announced = announced;
        _primed = true;
        _lastTick = now;
        if (!paused)
        {
            try { Loot.Keep(world, em, found); }
            catch (Exception e) { Plugin.Warn($"CarriageTracker: keeping loot failed: {e.Message}"); }
        }
        if (!paused)
        {
            try { Greeter.Tick(em); }
            catch (Exception e) { Plugin.Warn($"CarriageTracker: welcome failed: {e.Message}"); }
        }

        bool proxyMode = Settings.ProxyMode;
        var none = new List<Caravans.Caravan>();
        // Frozen carts physically lie at (10000, 10000), so their icon goes on a separate marker
        // where the patrol stands. Moving ones as the mode says. After .cv cleanup no icons are wanted,
        // so all of the mod's icons and markers are removed.
        var withIcon = paused ? none : _caravans.FindAll(c => c.Icon);
        var active = withIcon.FindAll(c => !c.Frozen);
        var frozenWithIcon = withIcon.FindAll(c => c.Frozen);
        var direct = Icons.Sync(em, prefabs, proxyMode ? none : active);
        var viaProxy = Proxies.Sync(em, prefabs, proxyMode ? withIcon : frozenWithIcon);
        int created = direct.Created + viaProxy.Created;
        int removed = direct.Removed + viaProxy.Removed;
        int globalized = direct.Globalized + viaProxy.Globalized;
        long ms = sw.ElapsedMilliseconds;
        int empty = found.FindAll(f => f.Kind == Kind.Container && f.Empty).Count;
        int parked = found.FindAll(f => f.Kind != Kind.Horse && !Caravans.OnMap(f.Pos)).Count;
        int frozenCount = _caravans.FindAll(c => c.Frozen).Count;

        _lastStats = L.T("stats.main", proxyMode ? "proxy" : "direct", ms, fastMs, scanned) +
                     (full ? L.T("stats.full", _lastSafetyMs) : "") + ". " +
                     L.T("stats.counts", _caravans.Count, empty, frozenCount) +
                     (parked > 0 ? L.T("stats.parked", parked) : "") + ". " +
                     L.T("stats.icons", created, removed) +
                     (globalized > 0 ? L.T("stats.globalized", globalized) : "") + "." +
                     (_lastSafetyMs >= 0 ? " " + L.T("stats.safety", Math.Round((now - _lastSafetyAt).TotalMinutes), _lastMissed) : "");
        if (created > 0 || removed > 0 || globalized > 0)
            Plugin.Trace($"CarriageTracker: check {ms} ms (fast {fastMs} ms over {scanned}), caravans {_caravans.Count}, " +
                                  $"empty containers {empty}, icons added {created}, removed {removed}, made global {globalized}");
        return true;
    }

    // A caravan that was in the list at the previous check and isn't now: written to the log with what
    // it was and what its route is doing, so it is clear later where it went.
    static void LogGone(EntityManager em, List<Caravans.Caravan> before, List<Caravans.Caravan> now)
    {
        var still = new HashSet<(int, int)>();
        foreach (var c in now) still.Add((c.Target.Index, c.Target.Version));
        foreach (var c in before)
        {
            if (still.Contains((c.Target.Index, c.Target.Version))) continue;
            bool exists = em.Exists(c.Target);
            bool onMap = exists && Caravans.OnMap(Caravans.PositionOf(em, c.Target));
            var route = c.Patrol != Entity.Null && em.Exists(c.Patrol) ? Routes.Snap(em, c.Patrol, c.Route) : null;
            var why = GoneLogic.Reason(c.State == "loot", c.Stops, exists, onMap, route);
            string routeNow = route == null ? "" : $"; route now {route.Move}, units {route.Units}, stop {route.Stops?.Log}";
            Plugin.Info($"CarriageTracker: caravan {c.Route} gone at ({Math.Round(c.Pos.x)}, {Math.Round(c.Pos.z)}): was {c.State}, {GoneLogic.Text(why)}{routeNow}");
        }
    }
}

// Loot containers of robbed carriages stay LootKeepMinutes (LootLogic). Every check: each container with loot
// gets its removal time pushed out to LootKeepMinutes after it was first seen.
// Every container on the map is written to the log when it appears and when it is gone, empty ones too: on 05.11 a
// robbed carriage left no line about its container at all, and it wasn't clear whether there was one.
static class Loot
{
    sealed class Seen
    {
        public double First;
        public string Route;
        public float3 Pos;
        public bool Empty;
    }

    static readonly Dictionary<(int, int), Seen> Containers = new();
    static readonly HashSet<(int, int)> Kept = new();
    static bool _noClockLogged;

    static string At(float3 p) => $"({Math.Round(p.x)}, {Math.Round(p.z)})";

    public static void Keep(World world, EntityManager em, List<Caravans.Found> found)
    {
        double keep = Math.Max(0, Settings.LootKeepMinutes.Value) * 60.0;
        var mapper = world.GetExistingSystemManaged<ServerScriptMapper>();
        if (mapper == null)
        {
            if (!_noClockLogged) Plugin.Warn("CarriageTracker: no ServerScriptMapper, loot containers are left to the game");
            _noClockLogged = true;
            return;
        }
        double now = mapper.GetServerGameManager().ServerTime;
        var seen = new HashSet<(int, int)>();
        foreach (var f in found)
        {
            if (f.Kind != Kind.Container || !Caravans.OnMap(f.Pos)) continue;
            var key = (f.Entity.Index, f.Entity.Version);
            seen.Add(key);
            bool hasTimer = Caravans.Has<DestroyAfterDuration>(em, f.Entity);
            var timer = hasTimer ? em.GetComponentData<DestroyAfterDuration>(f.Entity) : default;
            if (!Containers.TryGetValue(key, out var c))
            {
                Containers[key] = c = new Seen { First = now, Route = f.Route, Pos = f.Pos, Empty = f.Empty };
                Plugin.Info($"CarriageTracker: loot container of {f.Route} appeared at {At(f.Pos)}, {(f.Empty ? "empty" : "with loot")}; " +
                            (hasTimer ? $"the game would remove it in {Math.Round(Math.Max(0, timer.EndTime - now))} s" : "no removal timer"));
            }
            if (f.Empty && !c.Empty)
            {
                string after = "";
                if (Kept.Contains(key) && hasTimer && LootLogic.EmptiedEndTime(now, timer.EndTime) is double soon)
                {
                    timer.EndTime = soon;
                    em.SetComponentData(f.Entity, timer);
                    after = $", the game removes it in {Math.Round(soon - now)} s";
                }
                Plugin.Info($"CarriageTracker: loot container of {f.Route} at {At(f.Pos)} emptied after {Math.Round(now - c.First)} s{after}");
            }
            c.Empty = f.Empty;
            c.Pos = f.Pos;
            if (f.Empty || keep <= 0 || !hasTimer) continue;
            if (LootLogic.EndTime(c.First, keep, timer.EndTime) is not double until) continue;
            double gameEnd = timer.EndTime;
            timer.EndTime = until;
            em.SetComponentData(f.Entity, timer);
            if (Kept.Add(key))
                Plugin.Info($"CarriageTracker: loot of {f.Route} at {At(f.Pos)} kept " +
                            $"{Math.Round((until - now) / 60, 1)} more min (the game would remove it in {Math.Round(Math.Max(0, gameEnd - now))} s)");
        }
        foreach (var key in new List<(int, int)>(Containers.Keys))
        {
            if (seen.Contains(key)) continue;
            var c = Containers[key];
            Plugin.Info($"CarriageTracker: loot container of {c.Route} at {At(c.Pos)} gone after {Math.Round(now - c.First)} s, " +
                        (c.Empty ? "empty" : "still with loot"));
            Containers.Remove(key);
            Kept.Remove(key);
        }
    }
}

// Once every 10 minutes a line with the server process's memory and what the mod holds: the server was killed for
// memory on 05.11 (6.3 GB limit). If the process grows while the mod's objects stay level, the growth isn't the
// mod's; if markers, icons or objects waiting to be destroyed pile up, they are. Called every frame from Ticker, so
// it runs even if checks fail.
// icons: kept on caravans of all of the mod's kinds on caravans or left without their object; markers: the mod's of
// all MapIcon_ProxyObject_POI_Unknown markers (KindredPortals' included); made and removed since the last line, the
// removed ones by why (audit 07.11 by Nikolai, R2: how many network objects the mod turns over, and for what);
// destroy queue: objects handed to the game's destroy pipeline it hasn't removed yet (R1).
static class Memory
{
    static readonly TimeSpan Every = TimeSpan.FromMinutes(10);
    static DateTime _next = DateTime.UtcNow.AddMinutes(10);
    static long _iconsMade, _iconsDropped, _markersMade, _markersDropped, _orphans, _retries, _gaveUp, _repeats;
    static readonly Dictionary<DropWhy, long> _iconsBy = new(), _markersBy = new();

    static string Why(Dictionary<DropWhy, long> now, Dictionary<DropWhy, long> before)
    {
        var parts = new List<string>();
        foreach (var kv in now)
        {
            long n = kv.Value - (before.TryGetValue(kv.Key, out long b) ? b : 0);
            if (n > 0) parts.Add($"{kv.Key.ToString().ToLowerInvariant()} {n}");
            before[kv.Key] = kv.Value;
        }
        return parts.Count > 0 ? ": " + string.Join(", ", parts) : "";
    }

    public static void Tick()
    {
        var now = DateTime.UtcNow;
        if (now < _next) return;
        _next = now + Every;
        string process;
        try
        {
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            process = $"process {p.PrivateMemorySize64 >> 20} MB private, {p.WorkingSet64 >> 20} MB in RAM";
        }
        catch (Exception e) { process = $"process memory unknown ({e.GetType().Name})"; }
        string game;
        try { game = $"{Il2CppSystem.GC.GetTotalMemory(false) >> 20} MB"; }
        catch (Exception e) { game = $"unknown ({e.GetType().Name})"; }
        var doomed = Doomed.Counts(now);
        Plugin.Info($"CarriageTracker memory: {process}; game heap {game}, plugins' .NET heap {GC.GetTotalMemory(false) >> 20} MB; " +
                    $"mod: caravans {Tracker.Current.Count}, starts {Starter.RunCount}; " +
                    $"icons on caravans {Icons.Live} of {Icons.InWorld} (made {Icons.Made - _iconsMade}, removed {Icons.Dropped - _iconsDropped}{Why(Icons.DroppedBy, _iconsBy)}); " +
                    $"markers the mod's {Proxies.Live} of {Proxies.InWorld} (made {Proxies.Made - _markersMade}, removed {Proxies.Dropped - _markersDropped}{Why(Proxies.DroppedBy, _markersBy)}; " +
                    $"stale icons removed {Proxies.Orphans - _orphans}); " +
                    $"destroy queue {doomed.Waiting}, of them over a minute {doomed.Stuck}, of them given up {doomed.GivenUp} " +
                    $"(tried again {Doomed.Retries - _retries}, given up {Doomed.GaveUp - _gaveUp}); " +
                    $"repeated errors not written {Plugin.Repeats - _repeats}. Numbers in brackets and of errors: since the last line");
        _iconsMade = Icons.Made; _iconsDropped = Icons.Dropped;
        _markersMade = Proxies.Made; _markersDropped = Proxies.Dropped; _orphans = Proxies.Orphans;
        _retries = Doomed.Retries; _gaveUp = Doomed.GaveUp; _repeats = Plugin.Repeats;
    }
}
