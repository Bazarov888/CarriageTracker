using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using ProjectM;
using ProjectM.Network;
using ProjectM.Shared;
using Stunlock.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace CarriageTracker;

// Giving routes the game's normal settings back: after a crash, and before the mod is removed (.cv cleanup).
static partial class Starter
{
    // A numeric spawner field as double; NaN if it isn't a number.
    static double Num(object o)
    {
        try { return o is IConvertible c ? Convert.ToDouble(c) : double.NaN; }
        catch { return double.NaN; }
    }

    static bool TimersZero(object boxed) =>
        SpawnRange.TimersZero(Num(Caravans.Field(boxed, "MinRespawnTime")), Num(Caravans.Field(boxed, "MaxRespawnTime")));

    // A route's normal spawner values: those the carriage routes themselves show (SpawnRange.Learn), the mod's
    // constants (SpawnRange) for what no route shows yet. Until 1.0.18 the constants were always written, which
    // after a game update that changes the normal values would have written old ones back (audit of 07.11). From
    // 1.0.19 to 1.0.22 they were to be read from the route's prefab, but a route patrol has no PrefabGUID (patrol
    // dump of 12.11), so the constants were still used every time, without a word in the log.
    // Learned: range and timers both came from the routes; then they are kept until the server restarts.
    internal readonly record struct Normal(float RangeY, double Modifier, double MinRespawn, double MaxRespawn, bool Learned);

    static readonly Normal Defaults = new(SpawnRange.NormalY, 1, SpawnRange.NormalMinRespawn, SpawnRange.NormalMaxRespawn, false);
    static Normal? _learned;
    static DateTime _learnNext;

    internal static Normal NormalOf(EntityManager em, Entity patrol)
    {
        // Never throws: ApplySource asks for every route every 5 seconds, and an error here must not stop the loop.
        if (_learned.HasValue) return _learned.Value;
        var now = DateTime.UtcNow;
        if (now < _learnNext) return Defaults;
        _learnNext = now.AddSeconds(30);
        try
        {
            var seen = new List<SpawnRange.Seen>();
            foreach (var r in Routes.Known)
            {
                if (!em.Exists(r.Patrol) || !Caravans.Has<UnitCompositionSpawner>(em, r.Patrol)) continue;
                object b = em.GetComponentData<UnitCompositionSpawner>(r.Patrol);
                seen.Add(new SpawnRange.Seen(Caravans.Field(b, "PlayerRange") is float2 range ? range.y : float.NaN,
                                             Num(Caravans.Field(b, "RespawnTimeModifier")),
                                             Num(Caravans.Field(b, "MinRespawnTime")), Num(Caravans.Field(b, "MaxRespawnTime"))));
            }
            var (y, t) = SpawnRange.Learn(seen);
            var n = new Normal(y ?? Defaults.RangeY, t?.Modifier ?? Defaults.Modifier, t?.Min ?? Defaults.MinRespawn,
                               t?.Max ?? Defaults.MaxRespawn, y.HasValue && t.HasValue);
            if (!n.Learned) return n;   // asked again in 30 s: the range shows once a route isn't blocked
            _learned = n;
            bool same = n.RangeY == Defaults.RangeY && n.Modifier == Defaults.Modifier && n.MinRespawn == Defaults.MinRespawn &&
                        n.MaxRespawn == Defaults.MaxRespawn;
            Plugin.Info($"CarriageTracker: normal spawner values read from {seen.Count} routes: range {n.RangeY} m, " +
                        $"respawn {n.MinRespawn}-{n.MaxRespawn} s, modifier {n.Modifier}" +
                        (same ? "" : " (not the mod's defaults; the routes' own are used)"));
            return n;
        }
        catch { return Defaults; }
    }

    static void NormalTimers(object boxed, Normal n)
    {
        SetField(boxed, "RespawnTimeModifier", n.Modifier);
        SetField(boxed, "MinRespawnTime", n.MinRespawn);
        SetField(boxed, "MaxRespawnTime", n.MaxRespawn);
        ResetField(boxed, "RespawnTime");
    }

    // Routes whose settings couldn't be given back at the end of a start: repaired again every 5 seconds
    // (from the Source pass of the Ticker) until it works.
    static readonly Dictionary<(int, int), string> RepairQueue = new();

    // A route waiting for its repair still has a start's settings: it isn't started again until repaired (TryStart),
    // since a start would save those settings as the route's own, and auto-start skips it for an hour (review of
    // 11.11: until 1.0.22 a route whose Finish failed could be started again and kept the start's settings).
    // failed = false: a leftover found at the server start (RepairLeftovers), not a failed start; it isn't
    // skipped by auto-start for an hour, only repaired before its next start.
    static void QueueRepair(Entity patrol, string route, bool failed = true)
    {
        RepairQueue[Key(patrol)] = route;
        if (failed) FailedAt[Key(patrol)] = DateTime.UtcNow;
    }

    static bool AwaitsRepair(Entity patrol) => RepairQueue.ContainsKey(Key(patrol));

    public static void RetryRepairs(EntityManager em, PrefabCollectionSystem prefabs)
    {
        if (RepairQueue.Count == 0) return;
        foreach (var r in Routes.All(em, prefabs))
        {
            if (!RepairQueue.ContainsKey(Key(r.Patrol))) continue;
            if (IsRunning(r.Patrol)) { RepairQueue.Remove(Key(r.Patrol)); continue; }
            try
            {
                RepairRoute(em, r);
                RepairQueue.Remove(Key(r.Patrol));
                Plugin.Info($"CarriageTracker: route {r.Key} patrol {r.Patrol.Index}:{r.Patrol.Version} got its settings back on a retry");
            }
            catch (Exception e) { Plugin.Trace($"CarriageTracker: route {r.Key} still can't be repaired: {e.Message}"); }
        }
        // A patrol that no longer exists has nothing to repair.
        foreach (var key in new List<(int, int)>(RepairQueue.Keys))
            if (!Routes.All(em, prefabs).Exists(r => Key(r.Patrol) == key)) RepairQueue.Remove(key);
    }

    // After a crash or restart in the middle of a start, a route may keep the start settings: no freeze
    // without players, zero timers, endless player range. Its units and carts may stay without the freeze
    // too. Such routes and units get the game's normal values back. One route that fails doesn't stop the
    // others; it is queued and repaired again every 5 seconds. Returns the number of routes repaired.
    public static int RepairLeftovers(EntityManager em, PrefabCollectionSystem prefabs)
    {
        int repaired = 0;
        foreach (var r in Routes.All(em, prefabs))
        {
            if (IsRunning(r.Patrol) || !em.Exists(r.Patrol)) continue;
            try { if (RepairRoute(em, r)) repaired++; }
            catch (Exception e)
            {
                QueueRepair(r.Patrol, r.Key, failed: false);
                Plugin.Error($"CarriageTracker: repair of route {r.Key} failed, will try again: {e.Message}");
            }
        }
        return repaired;
    }

    // One route: true if something had to be changed.
    static bool RepairRoute(EntityManager em, Routes.Route r)
    {
        var tag = Tag("DisableWhenNoPlayersInRange");
        if (tag.HasValue)
        {
            var units = Routes.ActiveUnits(em, r.Patrol);
            var all = new List<Entity>(units);
            if (units.Count > 0) all.AddRange(Routes.CartsOf(em, units));
            int fixedUnits = 0;
            foreach (var e in all)
                if (!em.HasComponent(e, tag.Value)) { AddTag(em, e, "DisableWhenNoPlayersInRange"); fixedUnits++; }
            if (fixedUnits > 0)
                Plugin.Info($"CarriageTracker: route {r.Key}: {fixedUnits} units and carts left without freezing, restored");
        }
        var sp = em.GetComponentData<UnitCompositionSpawner>(r.Patrol);
        object boxed = sp;
        bool farRange = Caravans.Field(boxed, "PlayerRange") is float2 range && SpawnRange.IsStart(range.x, range.y);
        bool zeroTimers = TimersZero(boxed);
        bool noTag = tag.HasValue && !em.HasComponent(r.Patrol, tag.Value);
        if (!farRange && !zeroTimers && !noTag) return false;
        var normal = NormalOf(em, r.Patrol);
        if (farRange) SetField(boxed, "PlayerRange", new float2(0f, normal.RangeY));
        if (zeroTimers) NormalTimers(boxed, normal);
        else ResetField(boxed, "RespawnTime");
        em.SetComponentData(r.Patrol, (UnitCompositionSpawner)boxed);
        AddTag(em, r.Patrol, "DisableWhenNoPlayersInRange");
        Plugin.Info($"CarriageTracker: route {r.Key} patrol {r.Patrol.Index}:{r.Patrol.Version} had start settings left (range {farRange}, timers {zeroTimers}, no freeze {noTag}), restored");
        return true;
    }

    // After .cv cleanup the mod changes nothing more in the world until the server restarts.
    public static bool Paused { get; private set; }

    // .cv cleanup: everything the mod changed in the world goes back to the game's own state, so the mod
    // can be removed: starts are stopped, blocked or start ranges opened, zero timers restored, freezing
    // without players given back to routes, units and carts. The tracker then removes the mod's icons.
    public static (int Runs, int Routes, int Units) Cleanup(EntityManager em, PrefabCollectionSystem prefabs)
    {
        Paused = true;
        int runs = StopAll(em);
        int routes = 0, units = 0;
        var tag = Tag("DisableWhenNoPlayersInRange");
        foreach (var r in Routes.All(em, prefabs))
        {
            if (!em.Exists(r.Patrol)) continue;
            var sp = em.GetComponentData<UnitCompositionSpawner>(r.Patrol);
            object boxed = sp;
            bool changed = false;
            var normal = NormalOf(em, r.Patrol);
            try
            {
                if (Caravans.Field(boxed, "PlayerRange") is float2 range && SpawnRange.CleanupOpens(range.x, range.y))
                {
                    SetField(boxed, "PlayerRange", new float2(0f, normal.RangeY));
                    changed = true;
                }
                if (TimersZero(boxed))
                {
                    NormalTimers(boxed, normal);
                    changed = true;
                }
                if (changed)
                {
                    em.SetComponentData(r.Patrol, (UnitCompositionSpawner)boxed);
                    routes++;
                }
            }
            catch (Exception e) { Plugin.Error($"CarriageTracker cleanup: route {r.Key} keeps its start settings: {e.Message}"); }
            if (!tag.HasValue) continue;
            AddTag(em, r.Patrol, "DisableWhenNoPlayersInRange");
            var squad = Routes.ActiveUnits(em, r.Patrol);
            var all = new List<Entity>(squad);
            if (squad.Count > 0) all.AddRange(Routes.CartsOf(em, squad));
            foreach (var e in all)
                if (!em.HasComponent(e, tag.Value)) { AddTag(em, e, "DisableWhenNoPlayersInRange"); units++; }
        }
        BlockedRoutes.Clear();
        RepairQueue.Clear();
        return (runs, routes, units);
    }
}
