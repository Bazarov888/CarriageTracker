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

// Forced caravan start. The game updates a patrol and spawns its squad only when a player is near.
// Here the patrol is unfrozen, spawning is allowed without a player and without waiting, and the new
// units are kept from being frozen. While a start is running, it is checked once a second; what to do
// with the squad is decided by RunLogic (see there and ARCHITECTURE.md), this class only carries it out.
static partial class Starter
{
    class Run
    {
        public Entity Patrol;
        public string Route;
        public Entity User;
        public bool Auto;
        public int Act;
        public bool Reset;
        public bool Loop;                                              // the route has no end (Oakveil)
        public int Expected;                                           // units in the route's squad, 0 if unknown
        public float3 StartPos;                                        // where the patrol stood at the start
        public readonly RunState State = new();
        public readonly Dictionary<string, object> Saved = new();    // the route's own spawner values
        public readonly HashSet<(int, int)> Awake = new();           // units and carts the mod keeps from freezing
        public readonly HashSet<(int, int)> BuffsRemoved = new();    // units whose leftover spawn buff was removed
        public string LastState = "";
        public int Blocked;
        public int OverLimit;
        public int Errors;
    }

    static DateTime _nextUpdate;
    static (int, int) Key(Entity e) => (e.Index, e.Version);

    static readonly List<Run> Runs = new();
    static readonly string[] TimingFields = { "RespawnTimeModifier", "MinRespawnTime", "MaxRespawnTime" };

    // A trial caravan (.cvdiag trial) is looked at once a second for as long as it exists, its start over or not.
    public static bool Any => Runs.Count > 0 || _trial != null;
    internal static int RunCount => Runs.Count;
    public static bool IsRunning(Entity patrol) => Runs.Exists(r => r.Patrol == patrol);

    // A route whose start failed (no squad appeared, or a woken route didn't go back to its first stop) is skipped
    // by auto-start for an hour, so one broken route doesn't take every start of its act. Kept by patrol: the
    // other MilitiaCotton patrol isn't skipped with it.
    static readonly Dictionary<(int, int), DateTime> FailedAt = new();
    public static bool RecentlyFailed(Entity patrol) =>
        FailedAt.TryGetValue(Key(patrol), out var t) && DateTime.UtcNow < t.AddMinutes(60);

    // Caravans started by auto-start that still have their cart: all of them (act = 0) or in one act.
    // A run whose cart is destroyed goes on until its last units reach the end of the route,
    // but it no longer takes a place in the auto-start limits.
    public static int AutoCount(int act = 0)
    {
        int n = 0;
        foreach (var r in Runs) if (r.Auto && !r.State.CartGone && (act == 0 || r.Act == act)) n++;
        return n;
    }

    static readonly Dictionary<string, ComponentType?> Tags = new();

    static ComponentType? Tag(string name)
    {
        if (Tags.TryGetValue(name, out var cached)) return cached;
        var t = GameTypes.Find(name);
        ComponentType? ct = t == null ? null : ComponentType.ReadWrite(Il2CppType.From(t));
        if (t == null) Plugin.Warn($"CarriageTracker start: game type {name} not found");
        Tags[name] = ct;
        return ct;
    }

    static void RemoveTag(EntityManager em, Entity e, string name)
    {
        var ct = Tag(name);
        if (ct.HasValue && em.HasComponent(e, ct.Value)) em.RemoveComponent(e, ct.Value);
    }

    static void AddTag(EntityManager em, Entity e, string name)
    {
        var ct = Tag(name);
        if (ct.HasValue && !em.HasComponent(e, ct.Value)) em.AddComponent(e, ct.Value);
    }

    // Fields without which a start can't be undone (GameCheck: needed). Failing to write one throws, so the caller
    // doesn't write back a half-changed copy of the spawner; until 1.0.21 it was only a warning (audit 07.11 by
    // Nikolai, A6). Any other field gives a warning and is skipped.
    static readonly HashSet<string> Required = new() { "PlayerRange", "RespawnTimeModifier", "MinRespawnTime", "MaxRespawnTime" };

    static void SetField(object boxed, string name, object value)
    {
        try
        {
            var f = boxed.GetType().GetField(name) ?? throw new MissingFieldException(boxed.GetType().Name, name);
            if (value != null && f.FieldType != value.GetType() && value is IConvertible) value = Convert.ChangeType(value, f.FieldType);
            f.SetValue(boxed, value);
        }
        catch (Exception e)
        {
            if (Required.Contains(name))
                throw new InvalidOperationException($"can't set spawner field {name}: {e.Message}", e);
            Plugin.Warn($"CarriageTracker start: can't set spawner field {name}: {e.Message}");
        }
    }

    // A unit must not freeze without players. If the game already froze it and moved it
    // to (10000, 10000), unfreeze it and put it back at the patrol.
    static void KeepAwake(EntityManager em, Run run, Entity unit, float3 patrolPos)
    {
        if (run.Awake.Add(Key(unit))) RemoveTag(em, unit, "DisableWhenNoPlayersInRange");
        var frozen = Tag("DisabledDueToNoPlayersInRange");
        if (frozen.HasValue && em.HasComponent(unit, frozen.Value))
        {
            em.RemoveComponent(unit, frozen.Value);
            em.SetEnabled(unit, true);
            if (!Caravans.OnMap(Caravans.PositionOf(em, unit)))
            {
                if (Caravans.Has<Translation>(em, unit)) em.SetComponentData(unit, new Translation { Value = patrolPos });
                if (Caravans.Has<LocalTransform>(em, unit))
                {
                    var lt = em.GetComponentData<LocalTransform>(unit);
                    lt.Position = patrolPos;
                    em.SetComponentData(unit, lt);
                }
            }
            Plugin.Trace($"CarriageTracker start: unit {unit.Index}:{unit.Version} was frozen, woke it up");
        }
    }

    // note: why this route was picked, for the log line (auto-start). Returns the reply for the player.
    public static string Start(EntityManager em, Routes.Route route, Entity userEntity, bool auto = false, int act = 0, string note = "")
    {
        TryStart(em, route, userEntity, out var text, auto, act, note);
        return text.ToString();
    }

    // true if the caravan is being started. A start writes into the route step by step (the old squad removed,
    // spawner range and timers, the freeze tags) and is tracked only from its end (Runs). Until 1.0.18 an error in
    // between left the route with the start's endless range and zero timers until the server restarted, as
    // RepairLeftovers runs once after the start (audit of 07.11). Now such a route is repaired at once (or, if that
    // fails, every 5 seconds by RetryRepairs) and auto-start skips it for an hour (RecentlyFailed).
    // text: the reply, made in the language of whoever reads it (auto-start keeps it for .cv auto, LateText).
    public static bool TryStart(EntityManager em, Routes.Route route, Entity userEntity, out LateText text,
                                bool auto = false, int act = 0, string note = "")
    {
        var patrol = route.Patrol;
        GameCheck.Ensure();
        if (!GameCheck.StartsOk) { text = new LateText(() => L.T("start.broken", GameCheck.Missing)); return false; }
        if (Paused) { text = new LateText(() => L.T("cleanup.paused")); return false; }
        if (IsRunning(patrol)) { text = new LateText(() => L.T("start.already", Routes.Name(route))); return false; }
        // A route still waiting for its settings back is repaired here first: a start would save the start's settings
        // as its own. If that fails again it isn't started (review of 11.11).
        if (AwaitsRepair(patrol))
        {
            try
            {
                RepairRoute(em, route);
                RepairQueue.Remove(Key(patrol));
                Plugin.Info($"CarriageTracker: route {route.Key} patrol {patrol.Index}:{patrol.Version} got its settings back before a start");
            }
            catch (Exception e)
            {
                Plugin.Warn($"CarriageTracker start: route {route.Key} not started, its settings still can't be given back: {e.Message}");
                text = new LateText(() => L.T("start.repairing", Routes.Name(route)));
                return false;
            }
        }
        try
        {
            text = LateText.Of(Begin(em, route, userEntity, auto, act, note));
            return true;
        }
        catch (Exception e)
        {
            bool tracked = IsRunning(patrol);
            string after = "; the start goes on and is tracked";
            if (!tracked)
            {
                // Repaired at once, so the game can't spawn an untracked squad with the start's settings in between;
                // if that fails too, every 5 seconds (RetryRepairs).
                FailedAt[Key(patrol)] = DateTime.UtcNow;
                BlockedRoutes.Remove(Key(patrol));
                try
                {
                    after = RepairRoute(em, route)
                        ? "; its settings were given back, auto-start skips it for an hour"
                        : "; nothing of the start had been written into it yet, auto-start skips it for an hour";
                }
                catch (Exception e2)
                {
                    QueueRepair(patrol, route.Key);
                    after = $"; giving its settings back failed too ({e2.Message}), tried again every 5 s; auto-start skips it for an hour";
                }
            }
            Plugin.Error($"CarriageTracker start: route {route.Key} patrol {patrol.Index}:{patrol.Version} failed: {e}{after}");
            text = new LateText(() => L.T(tracked ? "start.goingon" : "start.error", Routes.Name(route)));
            return tracked;
        }
    }

    static string Begin(EntityManager em, Routes.Route route, Entity userEntity, bool auto, int act, string note)
    {
        var patrol = route.Patrol;
        string name = Routes.Name(route);
        var run = new Run { Patrol = patrol, Route = route.Key, User = userEntity, Auto = auto, Act = act };
        run.State.Started = DateTime.UtcNow;

        // 1. The old squad (if stuck) is removed completely, together with its carts.
        var units = Routes.ActiveUnits(em, patrol);
        var carts = Routes.CartsOf(em, units);
        foreach (var e in carts) DestroyUtility.Destroy(em, e);
        foreach (var e in units) DestroyUtility.Destroy(em, e);

        // A route left mid-way by an earlier caravan is put back to the first stop of its path.
        var before = Routes.StopsOf(em, patrol);
        var posBefore = Caravans.PositionOf(em, patrol);
        run.StartPos = posBefore;
        bool reset = Settings.ResetToStart.Value && ResetToStart(em, patrol, before);
        run.Reset = reset;
        run.Loop = before?.Loop ?? false;
        run.Expected = SquadSize(em, patrol);

        // 2. The route's own spawner values are saved to restore them later. A route at its last stop is
        // woken first with spawning off (RunLogic.ReadyToArm); any other one may spawn at once.
        var sp = em.GetComponentData<UnitCompositionSpawner>(patrol);
        object boxed = sp;
        run.Saved["PlayerRange"] = boxed.GetType().GetField("PlayerRange")?.GetValue(boxed);
        foreach (var f in TimingFields) run.Saved[f] = boxed.GetType().GetField(f)?.GetValue(boxed);
        // A route that still has a start's values (a start not undone) gets its normal ones saved instead, so the end
        // of this start doesn't write the start's values back for good.
        var normal = NormalOf(em, patrol);
        if (run.Saved["PlayerRange"] is float2 range && SpawnRange.IsStart(range.x, range.y))
        {
            run.Saved["PlayerRange"] = new float2(0f, normal.RangeY);
            Plugin.Warn($"CarriageTracker start: route {route.Key} had a start's spawn range, its normal one is restored after this start");
        }
        if (TimersZero(boxed))
        {
            run.Saved["RespawnTimeModifier"] = normal.Modifier;
            run.Saved["MinRespawnTime"] = normal.MinRespawn;
            run.Saved["MaxRespawnTime"] = normal.MaxRespawn;
            Plugin.Warn($"CarriageTracker start: route {route.Key} had a start's respawn timers, its normal ones are restored after this start");
        }
        run.State.Phase = RunLogic.FirstPhase(Routes.MoveStatus(em, patrol));
        if (run.State.Phase == RunPhase.Armed) Arm(em, run);
        else
        {
            SetField(boxed, "PlayerRange", new float2(0f, SpawnRange.BlockedY));
            em.SetComponentData(patrol, (UnitCompositionSpawner)boxed);
        }

        // 3. Unfreeze the patrol and keep the game from freezing it again.
        RemoveTag(em, patrol, "DisableWhenNoPlayersInRange");
        RemoveTag(em, patrol, "DisabledDueToNoPlayersInRange");
        em.SetEnabled(patrol, true);

        var stops = Routes.StopsOf(em, patrol);
        Runs.Add(run);
        Plugin.Info($"CarriageTracker start: patrol {patrol.Index}:{patrol.Version} route {route.Key}{(auto ? $" (auto, act {act}{note})" : "")}, " +
                    $"stop {stops?.Log}{(reset ? $" (put back from stop {before?.Log})" : "")} at ({Math.Round(posBefore.x)}, {Math.Round(posBefore.z)}), " +
                    $"squad of {(run.Expected > 0 ? run.Expected.ToString() : "?")}, " +
                    $"removed {units.Count} units and {carts.Count} carts" +
                    (run.State.Phase == RunPhase.Waking ? "; at its last stop: waking it before the squad may spawn" : ""));
        bool midway = RouteLogic.NeedsReset(stops, Routes.MoveStatus(em, patrol));
        return L.T("start.ok", name, units.Count, carts.Count, Routes.StopText(stops)) +
               (reset ? L.T("start.reset", Routes.StopText(before)) : midway ? L.T("start.midway") : "");
    }

    // Units in one squad of the route: the largest group of its composition (the game picks one group per
    // squad; militia and bandit routes have three groups of 8). 0 if unknown.
    static int SquadSize(EntityManager em, Entity patrol)
    {
        try
        {
            if (!em.HasBuffer<UnitCompositionGroupEntry>(patrol)) return 0;
            var groups = em.GetBuffer<UnitCompositionGroupEntry>(patrol);
            int n = 0;
            for (int i = 0; i < groups.Length; i++) n = Math.Max(n, (int)groups[i].UnitsCount);
            return n;
        }
        catch (Exception e)
        {
            Plugin.Warn($"CarriageTracker start: squad size of patrol {patrol.Index}:{patrol.Version} unknown: {e.Message}");
            return 0;
        }
    }

    // Spawning without a player nearby and without waiting.
    static void Arm(EntityManager em, Run run)
    {
        var sp = em.GetComponentData<UnitCompositionSpawner>(run.Patrol);
        object boxed = sp;
        SetField(boxed, "PlayerRange", new float2(0f, SpawnRange.StartY));
        foreach (var f in TimingFields) SetField(boxed, f, 0);
        // A respawn time the game scheduled earlier (up to 45 minutes ahead, after the previous squad was
        // destroyed) would hold the new squad back until then, so it is cleared.
        ResetField(boxed, "RespawnTime");
        em.SetComponentData(run.Patrol, (UnitCompositionSpawner)boxed);
        run.State.Phase = RunPhase.Armed;
    }

    // Puts a route that stands mid-way back to the first stop of its path: stop index to the start, movement
    // waiting for a new path. A squad appears where the patrol entity stands, so this most likely changes only
    // where the caravan heads first: it walks back to the first stop and drives the whole route from there.
    // The "squad appeared" log line tells by the distance. A route at its last stop (WaitForDespawn) isn't
    // touched: it is woken and the game returns it to the start itself (RunLogic.ReadyToArm).
    static bool ResetToStart(EntityManager em, Entity patrol, Progress stops)
    {
        if (!RouteLogic.NeedsReset(stops, Routes.MoveStatus(em, patrol))) return false;
        try
        {
            object gp = em.GetComponentData<GlobalPatrolState>(patrol);
            SetField(gp, "CurrentBusStopIndex", stops.Start);
            SetField(gp, "TargetBusStopIndex", stops.Start);
            em.SetComponentData(patrol, (GlobalPatrolState)gp);
            object mp = em.GetComponentData<MovePatrolState>(patrol);
            var status = mp.GetType().GetField("Status");
            if (status != null && status.FieldType.IsEnum && Enum.IsDefined(status.FieldType, "WaitingForPath"))
            {
                status.SetValue(mp, Enum.Parse(status.FieldType, "WaitingForPath"));
                em.SetComponentData(patrol, (MovePatrolState)mp);
            }
            return true;
        }
        catch (Exception e)
        {
            Plugin.Warn($"CarriageTracker start: can't put patrol {patrol.Index}:{patrol.Version} back to the start: {e.Message}");
            return false;
        }
    }

    // Gives the route its own spawner settings back (SpawnRange.RestoreY for the range).
    static void RestoreSpawner(EntityManager em, Run run)
    {
        if (!em.Exists(run.Patrol)) return;
        var sp = em.GetComponentData<UnitCompositionSpawner>(run.Patrol);
        object boxed = sp;
        foreach (var kv in run.Saved)
        {
            object value = kv.Value;
            if (kv.Key == "PlayerRange" && value is float2 range)
                value = new float2(range.x, SpawnRange.RestoreY(range.x, range.y, NormalOf(em, run.Patrol).RangeY));
            if (value != null) SetField(boxed, kv.Key, value);
        }
        ResetField(boxed, "RespawnTime");
        em.SetComponentData(run.Patrol, (UnitCompositionSpawner)boxed);
    }

    // Sets a field to its empty value (for RespawnTime: "no respawn scheduled"), so the game computes
    // the next respawn again from the restored timers instead of a time computed while they were zero.
    static void ResetField(object boxed, string name)
    {
        try
        {
            var f = boxed.GetType().GetField(name);
            if (f == null) { Plugin.Warn($"CarriageTracker start: spawner field {name} not found"); return; }
            f.SetValue(boxed, f.FieldType.IsValueType ? Activator.CreateInstance(f.FieldType) : null);
        }
        catch (Exception e)
        {
            Plugin.Warn($"CarriageTracker start: can't reset spawner field {name}: {e.Message}");
        }
    }

    // End of a run: the route gets its settings back, and units that are still alive (the run timed out
    // or was stopped by command) may freeze without players again, as all units of the game do.
    // If the settings can't be written, the route is repaired again every 5 seconds (RetryRepairs).
    static void Finish(EntityManager em, Run run, string why)
    {
        if (!run.State.Restored)
        {
            try
            {
                RestoreSpawner(em, run);
                run.State.Restored = true;
            }
            catch (Exception e)
            {
                QueueRepair(run.Patrol, run.Route);
                Plugin.Error($"CarriageTracker start: {Id(run)}: can't give the route its settings back, will try again: {e.Message}");
            }
        }
        int back = 0;
        if (em.Exists(run.Patrol))
        {
            BlockIfWaiting(em, run.Patrol);
            BlockTrial(em, run.Patrol);
            AddTag(em, run.Patrol, "DisableWhenNoPlayersInRange");
            var units = Routes.ActiveUnits(em, run.Patrol);
            var rest = new List<Entity>(units);
            if (units.Count > 0) rest.AddRange(Routes.CartsOf(em, units));
            foreach (var e in rest)
            {
                if (!run.Awake.Contains(Key(e))) continue;
                AddTag(em, e, "DisableWhenNoPlayersInRange");
                back++;
            }
        }
        Plugin.Info($"CarriageTracker start: {Id(run)} finished after {Math.Round((DateTime.UtcNow - run.State.Started).TotalMinutes)} min: {why}" +
                    (back > 0 ? $", {back} units left may freeze again" : ""));
    }

    public static int StopAll(EntityManager em)
    {
        int n = Runs.Count;
        // One run that can't be finished must not keep the others going (review of 11.11).
        foreach (var run in Runs)
        {
            try { Finish(em, run, "stopped by command"); }
            catch (Exception e)
            {
                Plugin.Error($"CarriageTracker start: {run.Route} can't be finished: {e.Message}");
                QueueRepair(run.Patrol, run.Route);
            }
        }
        Runs.Clear();
        return n;
    }

    public static List<string> Status(EntityManager em)
    {
        if (Runs.Count == 0) return new List<string> { L.T("status.none") };
        var parts = new List<string>();
        foreach (var run in Runs)
        {
            string state = em.Exists(run.Patrol) ? Routes.Describe(em, run.Patrol) : L.T("status.gone");
            string stops = Routes.StopText(Routes.StopsOf(em, run.Patrol));
            if (stops.Length > 0) state += ", " + stops;
            parts.Add(L.T("status.item", Routes.Name(run.Route, run.Patrol), run.Auto ? L.T("status.auto", L.Act(run.Act)) : "", state,
                          Math.Round((DateTime.UtcNow - run.State.Started).TotalMinutes)));
        }
        return ChatLines.Pack(parts, "; ");
    }

    // Every frame while there are runs; the work itself is done once a second.
    public static void Update(EntityManager em)
    {
        var now = DateTime.UtcNow;
        if (now < _nextUpdate) return;
        _nextUpdate = now.AddSeconds(1);

        int spawnTimeout = Math.Max(1, Settings.SpawnTimeoutMinutes.Value);
        int maxRun = Math.Max(10, Settings.MaxRunMinutes.Value);
        int maxSquad = Math.Max(5, Settings.MaxSquadSize.Value);
        var world = Caravans.ServerWorld();
        var prefabs = world == null ? null : Caravans.Prefabs(world);

        for (int i = Runs.Count - 1; i >= 0; i--)
        {
            var run = Runs[i];
            try
            {
                if (UpdateRun(em, prefabs, run, now, spawnTimeout, maxRun, maxSquad)) Runs.RemoveAt(i);
                run.Errors = 0;
            }
            catch (Exception e)
            {
                // One broken start must not stop the others. After 10 errors in a row it is given up.
                run.Errors++;
                if (run.Errors == 1 || run.Errors == 10)
                    Plugin.Error($"CarriageTracker start: route {run.Route}, error {run.Errors}: {e}");
                if (run.Errors >= 10)
                {
                    try { Finish(em, run, "given up after 10 errors in a row"); }
                    catch (Exception e2) { Plugin.Error($"CarriageTracker start: route {run.Route} can't be finished: {e2.Message}"); }
                    Runs.RemoveAt(i);
                }
            }
        }

        if (_trial != null)
        {
            try { TrialSweep(world, em, prefabs, now); }
            catch (Exception e) { Plugin.Fail("trial", e); }
        }
    }

    // One second of one start: the game's state is read, RunLogic decides, the decisions are carried out.
    // Returns true when the start is over and must be removed.
    static bool UpdateRun(EntityManager em, PrefabCollectionSystem prefabs, Run run, DateTime now, int spawnTimeout, int maxRun, int maxSquad)
    {
        if (!em.Exists(run.Patrol)) return true;
        var s = run.State;
        var patrolPos = Caravans.PositionOf(em, run.Patrol);
        if (s.Phase == RunPhase.Waking)
        {
            var at = Routes.StopsOf(em, run.Patrol);
            float moved = Caravans.Dist2(patrolPos, run.StartPos);
            string spawner = em.GetComponentData<UnitCompositionSpawner>(run.Patrol).SpawnerState.ToString();
            var wake = RunLogic.ReadyToArm(s, now, Routes.MoveStatus(em, run.Patrol) == "WaitForDespawn", at?.AtStart ?? false, moved,
                                           spawner == "WaitingForSpawn");
            if (wake == WakeStep.Wait) return false;
            string where = $"stop {at?.Log} at ({Math.Round(patrolPos.x)}, {Math.Round(patrolPos.z)}), {Math.Round(moved)} m from its last stop, " +
                           $"spawner {spawner}, {Math.Round((now - s.Started).TotalSeconds)} s after waking";
            if (wake == WakeStep.GiveUp)
            {
                Finish(em, run, $"the route didn't go back to its first stop by itself in {RunLogic.WakeMax.TotalSeconds} s ({where}); " +
                                "start given up, auto-start skips this route for an hour");
                FailedAt[Key(run.Patrol)] = now;
                Chat.Send(em, run.User, () => L.T("start.wakefailed", Routes.Name(run.Route, run.Patrol)));
                return true;
            }
            Arm(em, run);
            Plugin.Info($"CarriageTracker start: {Id(run)}: the game returned the route to its first stop ({where}); the squad may spawn");
        }
        var units = Routes.ActiveUnits(em, run.Patrol);
        var byKey = new Dictionary<(int, int), Entity>();
        var keys = new List<(int, int)>();
        foreach (var u in units) { byKey[Key(u)] = u; keys.Add(Key(u)); }
        string move = Routes.MoveStatus(em, run.Patrol);

        var step = RunLogic.Squad(s, now, move == "WaitForDespawn", keys, maxSquad, run.Expected);
        if (step.JustSpawned)
        {
            Chat.Send(em, run.User, () => L.T("start.spawned", Routes.Name(run.Route, run.Patrol)));
            // Where the squad appeared, to see that a route put back to the start really sets out from there.
            var p = Routes.StopsOf(em, run.Patrol);
            var at = Caravans.PositionOf(em, units[0]);
            float fromStart = Caravans.Dist2(at, run.StartPos);
            string msg = $"CarriageTracker start: {Id(run)}: squad appeared at stop {p?.Log} at ({Math.Round(at.x)}, {Math.Round(at.z)}), " +
                         $"{Math.Round(fromStart)} m from where the route stood, {Math.Round((now - s.Started).TotalSeconds)} s after the start";
            // Putting a route back to the start changes its stop number; whether the game also moves the squad
            // there is told by the distance: next to where the route stood means the caravan walks back first.
            if (run.Reset && p != null && !p.AtStart) Plugin.Warn(msg + ", although the route was put back to the start");
            else if (run.Reset && fromStart < RunLogic.SamePlace) Plugin.Warn(msg + "; put back to the start, but it appeared where the route stood: it walks to the first stop first");
            else Plugin.Info(msg);
        }
        // A replacement animal comes with its own cart, which isn't a squad unit: it goes with its animal (04.11: a
        // second cart stayed on the road and was robbed too).
        var extra = new List<Entity>();
        foreach (var k in step.Replacements) extra.Add(byKey[k]);
        foreach (var k in step.OverLimit) extra.Add(byKey[k]);
        var extraCarts = extra.Count > 0 ? Routes.CartsOf(em, extra) : new List<Entity>();
        foreach (var c in extraCarts) DestroyUtility.Destroy(em, c);
        foreach (var e in extra) DestroyUtility.Destroy(em, e);
        if (step.Replacements.Count > 0)
        {
            run.Blocked += step.Replacements.Count;
            Plugin.Warn($"CarriageTracker start: {Id(run)}: removed {step.Replacements.Count} respawned guards (total {run.Blocked})" +
                        (extraCarts.Count > 0 ? $" and {extraCarts.Count} extra carts" : ""));
        }
        if (step.OverLimit.Count > 0)
        {
            run.OverLimit += step.OverLimit.Count;
            Plugin.Warn($"CarriageTracker start: {Id(run)}: squad over MaxSquadSize {maxSquad}, removed {step.OverLimit.Count} units (total {run.OverLimit})");
        }
        var remaining = new List<Entity>();
        foreach (var k in step.Remaining) remaining.Add(byKey[k]);
        var carts = remaining.Count > 0 ? Routes.CartsOf(em, remaining) : new List<Entity>();
        // The game's spawn buff left on a member long after it appeared makes it take no damage (02.11).
        foreach (var k in step.Remaining)
            if (RunLogic.PastSpawn(s, k, now)) RemoveSpawnBuffs(em, prefabs, run, byKey[k], now);
        if (RunLogic.PastSpawn(s, null, now))
            foreach (var c in carts) RemoveSpawnBuffs(em, prefabs, run, c, now);
        if (step.Assembled)
        {
            // Restored only after the write: if it throws, the next second asks again (RunLogic.Squad).
            RestoreSpawner(em, run);
            s.Restored = true;
            Plugin.Info($"CarriageTracker start: {Id(run)}: " +
                        (s.LostAny ? $"a member was killed {Math.Round((now - s.SpawnedAt).TotalSeconds)} s after the first one appeared, {step.Remaining.Count} units left"
                                   : $"squad assembled, {step.Remaining.Count} units {Math.Round((now - s.SpawnedAt).TotalSeconds)} s after the first one") +
                        "; the route has its own settings back");
            LogAnimal(em, prefabs, run, remaining, "at assembly");
        }
        foreach (var u in remaining) KeepAwake(em, run, u, patrolPos);
        foreach (var c in carts) KeepAwake(em, run, c, patrolPos);

        var end = RunLogic.Outcome(s, now, remaining.Count, carts.Count, move == "WaitForDespawn", TimeSpan.FromMinutes(spawnTimeout),
                                   TimeSpan.FromMinutes(maxRun), out bool cartJustLost, run.Loop);
        if (cartJustLost)
        {
            Plugin.Info($"CarriageTracker start: {Id(run)}: the cart is gone, {remaining.Count} units go on to the end of the route; " +
                        "not counted in auto-start limits any more");
            LogAnimal(em, prefabs, run, remaining, "after its cart was lost");
        }
        if (s.CartGone) MakeAnimalMortal(em, prefabs, run, remaining);

        // State changes go to the verbose log (movement flickers between Moving and Stopped, it isn't compared).
        var sp = em.GetComponentData<UnitCompositionSpawner>(run.Patrol);
        string state = $"spawner={sp.SpawnerState} stop={Routes.StopsOf(em, run.Patrol)?.Log} units={remaining.Count}";
        if (state != run.LastState)
        {
            Plugin.Trace($"CarriageTracker start: {Id(run)} {state} move={move} at ({Math.Round(patrolPos.x)}, {Math.Round(patrolPos.z)})");
            run.LastState = state;
        }

        switch (end)
        {
            case RunEnd.NoSpawn:
                Finish(em, run, $"no units after {spawnTimeout} minutes; auto-start skips this route for an hour");
                FailedAt[Key(run.Patrol)] = now;
                Chat.Send(em, run.User, () => L.T("start.failed", Routes.Name(run.Route, run.Patrol), spawnTimeout));
                return true;
            case RunEnd.Done:
                Finish(em, run, "route completed or caravan destroyed");
                Chat.Send(em, run.User, () => L.T("start.done", Routes.Name(run.Route, run.Patrol)));
                return true;
            case RunEnd.LoopCartLost:
                Finish(em, run, $"the cart is gone on a route without an end; {remaining.Count} units circle on, " +
                                "the route is free again once they freeze without players near");
                Chat.Send(em, run.User, () => L.T("start.done", Routes.Name(run.Route, run.Patrol)));
                return true;
            case RunEnd.StuckAtEnd:
                // The game removes a squad at the last stop within seconds; this one stayed, so the mod removes it.
                foreach (var c in carts) DestroyUtility.Destroy(em, c);
                foreach (var u in remaining) DestroyUtility.Destroy(em, u);
                Finish(em, run, $"the squad stood at the last stop for {RunLogic.EndGrace.TotalMinutes} minutes; " +
                                $"removed {remaining.Count} units and {carts.Count} carts the game left there");
                return true;
            case RunEnd.Timeout:
                Finish(em, run, $"timeout {maxRun} minutes");
                return true;
            default:
                return false;
        }
    }

    static string Id(Run run) => $"patrol {run.Patrol.Index}:{run.Patrol.Version} route {run.Route}";
}
