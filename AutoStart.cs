using System;
using System.Collections.Generic;
using ProjectM;
using Unity.Entities;

namespace CarriageTracker;

// Auto-start. Every check the mod looks at the acts of online players (all of them or only the most
// advanced one, see Scope; how a player's act is found: PlayerAct). Each player act gets the caravans of its own
// act, or of the act EmptyAct gives when it has no routes (act III). In each such route act a caravan starts on a
// free route if at least IntervalMinutes passed since the previous auto-start in that act and the MaxPerAct and
// MaxTotal limits allow it. Nobody online - nothing starts. Routes from ExcludedRoutes are never used. Which routes
// are free and which one goes first is decided by RouteLogic (IsFree, Choose).
static class AutoStart
{
    static readonly Dictionary<int, DateTime> LastStart = new();
    static readonly Dictionary<string, DateTime> LastRouteStart = new();   // by patrol ("index:version")
    static bool _toldBloodcraft;
    static readonly Random Rng = new();
    static DateTime _ready = DateTime.MinValue;
    public static readonly List<LateText> LastResults = new();
    static string _lastBlocked = "";

    // Time of the previous auto-start in the act. Before the first one: when the server became ready,
    // so the first caravan of every act sets out one interval after the server start.
    static DateTime LastOf(int act) => LastStart.TryGetValue(act, out var t) ? t : _ready;

    public static double MinutesLeft(int act)
    {
        if (_ready == DateTime.MinValue) return Settings.AutoInterval.Value;
        return Math.Max(0, (LastOf(act).AddMinutes(Settings.AutoInterval.Value) - DateTime.UtcNow).TotalMinutes);
    }

    // Called by .cv auto <minutes>: the new interval counts from now.
    public static void Restart()
    {
        LastStart.Clear();
        _ready = DateTime.UtcNow;
    }

    public static void Tick()
    {
        if (Settings.AutoInterval.Value <= 0 || Settings.GameOnly || Starter.Paused) return;
        var world = Caravans.ServerWorld();
        var prefabs = world == null ? null : Caravans.Prefabs(world);
        if (prefabs == null) return;
        if (_ready == DateTime.MinValue)
        {
            _ready = DateTime.UtcNow;
            return;
        }
        if (!_toldBloodcraft)
        {
            _toldBloodcraft = true;
            Plugin.Info($"CarriageTracker auto: player act by {Settings.PlayerActMode}, act without routes uses {Settings.EmptyActMode}; " +
                        $"Bloodcraft {(Plugin.HasBloodcraft ? "found: the character level is its level" : "not found: the character level is the gear level")}");
        }
        var results = Run(world.EntityManager, prefabs, false);
        if (results.Count == 0) return;
        // A start is always logged. A reason why nothing started (limit, busy routes) is logged
        // only when it changes, otherwise it would repeat every check.
        string blocked = string.Join(" | ", results.FindAll(r => !r.Started).ConvertAll(r => r.Text.ToString()));
        bool started = results.Exists(r => r.Started);
        if (!started && blocked == _lastBlocked) return;
        _lastBlocked = blocked;
        LastResults.Clear();
        foreach (var r in results)
        {
            LastResults.Add(r.Text);
            if (r.Started) Plugin.Info($"CarriageTracker auto: {r.Text}");
            else Plugin.Trace($"CarriageTracker auto: {r.Text}");
        }
    }

    // force = ignore the interval (the .cv autonow command). Returns what happened in each act;
    // acts that simply wait for their interval are not reported.
    public static List<(bool Started, LateText Text)> Run(EntityManager em, PrefabCollectionSystem prefabs, bool force)
    {
        var results = new List<(bool Started, LateText Text)>();
        if (Starter.Paused)
        {
            results.Add((false, new LateText(() => L.T("cleanup.paused"))));
            return results;
        }
        if (Settings.GameOnly)
        {
            results.Add((false, new LateText(() => L.T("auto.gameonly"))));
            return results;
        }
        GameCheck.Ensure();
        if (!GameCheck.StartsOk)
        {
            results.Add((false, new LateText(() => L.T("start.broken", GameCheck.Missing))));
            return results;
        }
        var online = Acts.Online(em, prefabs);
        if (online.Count == 0)
        {
            if (force) results.Add((false, new LateText(() => L.T("auto.nobody"))));
            return results;
        }

        // Player acts to serve, the most advanced first.
        var playerActs = new List<int>();
        if (Settings.LeaderScope) playerActs.Add(Acts.Leader(online).Act.Act);
        else
            foreach (var p in online)
                if (!playerActs.Contains(p.Act.Act)) playerActs.Add(p.Act.Act);
        playerActs.Sort((a, b) => b.CompareTo(a));

        var routes = Routes.All(em, prefabs).FindAll(r => !Settings.Excluded(r.Key));
        var routeActs = new Dictionary<(int, int), SortedSet<int>>();
        var actsWithRoutes = new HashSet<int>();
        foreach (var r in routes)
        {
            var acts = Acts.OfRoute(em, prefabs, r).Acts;
            routeActs[(r.Patrol.Index, r.Patrol.Version)] = acts;
            actsWithRoutes.UnionWith(acts);
        }
        bool InAct(Routes.Route r, int act) => routeActs[(r.Patrol.Index, r.Patrol.Version)].Contains(act);

        int interval = Math.Max(1, Settings.AutoInterval.Value);
        int maxPerAct = Math.Max(1, Settings.AutoMaxPerAct.Value);
        int maxTotal = Math.Max(1, Settings.AutoMaxTotal.Value);
        var served = new HashSet<int>();
        var now = DateTime.UtcNow;

        // Route acts to serve, in the order of the player acts they serve.
        var toServe = new List<int>();
        foreach (int playerAct in playerActs)
        {
            var acts = ActLogic.Served(playerAct, actsWithRoutes, Settings.EmptyActMode);
            if (acts.Count == 0) results.Add((false, new LateText(() => L.T("auto.noroutes", L.Act(playerAct)))));
            foreach (int a in acts) if (!toServe.Contains(a)) toServe.Add(a);
        }

        foreach (int act in toServe)
        {
            if (!served.Add(act)) continue;
            if (!force && now < LastOf(act).AddMinutes(interval)) continue;

            if (Starter.AutoCount() >= maxTotal)
            {
                int total = Starter.AutoCount();
                results.Add((false, new LateText(() => L.T("auto.maxtotal", total, maxTotal))));
                break;
            }
            if (Starter.AutoCount(act) >= maxPerAct)
            {
                int inAct = Starter.AutoCount(act);
                results.Add((false, new LateText(() => L.T("auto.maxact", L.Act(act), inAct, maxPerAct))));
                continue;
            }
            var candidates = routes.FindAll(r => InAct(r, act));
            var free = new List<Routes.Route>();
            var snaps = new List<RouteSnap>();
            foreach (var r in candidates)
            {
                var snap = Routes.Snap(em, r);
                if (!RouteLogic.IsFree(RouteLogic.Classify(snap))) continue;
                free.Add(r);
                snaps.Add(snap);
            }
            if (free.Count == 0)
            {
                results.Add((false, new LateText(() => L.T("auto.busy", L.Act(act)))));
                continue;
            }
            int chosen = RouteLogic.Choose(snaps, Settings.ResetToStart.Value, Settings.RotatePick,
                                           id => LastRouteStart.TryGetValue(id, out var t) ? t : DateTime.MinValue, Rng);
            var pick = free[chosen];
            // A start that didn't happen (an error, the route already started) doesn't count as this act's start:
            // the next check tries again, and a route whose start failed is skipped for an hour (RecentlyFailed).
            if (!Starter.TryStart(em, pick, Entity.Null, out string why, true, act,
                                  $", picked from {free.Count} free: {RouteLogic.Groups(snaps)}, this one {RouteLogic.Group(snaps[chosen])}"))
            {
                results.Add((false, LateText.Of(why)));
                continue;
            }
            var stops = Routes.StopsOf(em, pick.Patrol);
            LastStart[act] = now;
            LastRouteStart[snaps[chosen].Ident] = now;
            int freeCount = free.Count;
            results.Add((true, new LateText(() => L.T("auto.started", Routes.Name(pick), L.Act(act), freeCount, Routes.StopText(stops)))));
        }
        return results;
    }
}
