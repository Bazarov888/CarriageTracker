using System;
using System.Collections.Generic;
using CarriageTracker;

// Each test is a case seen on the live server or a rule from ARCHITECTURE.md.
// Exit code 1 if any test fails, so CI fails.
static class Program
{
    static int _failed, _passed;

    static void Test(string name, Action body)
    {
        try { body(); _passed++; }
        catch (Exception e) { _failed++; Console.WriteLine($"FAIL {name}: {e.Message}"); }
    }

    static void Eq<T>(T expected, T actual, string what = "")
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{what} expected {expected}, got {actual}");
    }

    static void True(bool v, string what) { if (!v) throw new Exception(what); }
    static void False(bool v, string what) { if (v) throw new Exception(what + " should be false"); }

    static RouteSnap Route(string key = "R", string move = "Moving", string spawner = "Spawned", Progress stops = null,
                           int units = 0, int onMap = -1, int carts = 0, bool running = false, bool failed = false, string id = null) =>
        new(key, move, spawner, stops, units, onMap < 0 ? units : onMap, carts, running, failed, id);

    static Progress Stop(int number, int count) => new(number - 1, 0, count);

    static readonly DateTime T0 = new(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
    static (int, int) U(int i) => (i, 1);
    static List<(int, int)> Us(params int[] ids) { var l = new List<(int, int)>(); foreach (var i in ids) l.Add(U(i)); return l; }

    static int Main()
    {
        // --- Progress: stops as players count them
        Test("stop numbering", () =>
        {
            var first = new Progress(0, 0, 7);
            var last = new Progress(6, 0, 7);
            Eq("1/7", first.Log); Eq("7/7", last.Log);
            True(first.AtStart && !first.AtLast, "first stop"); True(last.AtLast && !last.AtStart, "last stop");
            Eq(6, first.Left); Eq(0, last.Left);
        });

        // --- Route states
        Test("empty route at start is free", () =>
        {
            var s = Route(spawner: "WaitingForSpawn", stops: Stop(1, 8), move: "WaitingForPath");
            Eq(RouteState.Empty, RouteLogic.Classify(s)); True(RouteLogic.IsFree(RouteState.Empty), "free");
            Eq(PickGroup.AtStart, RouteLogic.Group(s));
        });
        Test("caravan on the road is busy", () =>
            Eq(RouteState.OnRoad, RouteLogic.Classify(Route(units: 8, carts: 1, stops: Stop(3, 8)))));
        Test("robbed caravan walking on is busy (log 31.10: cart gone at 3/8, units go on)", () =>
        {
            var st = RouteLogic.Classify(Route(units: 3, carts: 0, stops: Stop(3, 8), move: "StoppedDueToUnit"));
            Eq(RouteState.CartLost, st); True(!RouteLogic.IsFree(st), "busy");
        });
        Test("frozen horse without cart is free", () =>
        {
            var st = RouteLogic.Classify(Route(units: 1, onMap: 0, carts: 0, stops: Stop(5, 7)));
            Eq(RouteState.FrozenLeftover, st); True(RouteLogic.IsFree(st), "free");
        });
        Test("squad at the last stop is free", () =>
        {
            var st = RouteLogic.Classify(Route(move: "WaitForDespawn", units: 6, carts: 1, stops: Stop(7, 7)));
            Eq(RouteState.AtEnd, st); True(RouteLogic.IsFree(st), "free");
        });
        Test("running or recently failed route is busy", () =>
        {
            True(!RouteLogic.IsFree(RouteLogic.Classify(Route(running: true))), "running");
            True(!RouteLogic.IsFree(RouteLogic.Classify(Route(failed: true))), "failed");
        });

        // --- Picking: the 30.10 bug was a Copper caravan started at stop 6/8
        Test("route at start goes before routes mid-way", () =>
        {
            var free = new List<RouteSnap> { Route("Copper", stops: Stop(6, 8)), Route("Plank", stops: Stop(1, 7)) };
            for (int i = 0; i < 20; i++)
                Eq("Plank", free[RouteLogic.Choose(free, true, false, _ => DateTime.MinValue, new Random(i))].Key);
        });
        Test("log 31.10: the route started last isn't picked again while another one is ready", () =>
        {
            // MilitiaCotton finished, stood at its first stop again and was picked 5 times in a row.
            var free = new List<RouteSnap> { Route("Cotton", stops: Stop(1, 4)), Route("Quartz", stops: Stop(1, 5)),
                                             Route("Iron", move: "WaitForDespawn", stops: Stop(5, 5)) };
            var last = new Dictionary<string, DateTime> { ["Cotton"] = T0 };
            var seen = new HashSet<string>();
            for (int i = 0; i < 50; i++)
                seen.Add(free[RouteLogic.Choose(free, true, false, k => last.TryGetValue(k, out var t) ? t : DateTime.MinValue, new Random(i))].Key);
            True(!seen.Contains("Cotton"), "Cotton picked again");
            True(seen.Contains("Quartz") && seen.Contains("Iron"), "routes at the start and at the end are one group");
        });
        Test("a single ready route is picked even if it ran last", () =>
        {
            var free = new List<RouteSnap> { Route("Cotton", stops: Stop(1, 4)), Route("Copper", stops: Stop(4, 8)) };
            Eq("Cotton", free[RouteLogic.Choose(free, true, false, k => k == "Cotton" ? T0 : DateTime.MinValue, new Random(1))].Key);
        });
        Test("route at the end goes before routes mid-way", () =>
        {
            var free = new List<RouteSnap> { Route("Copper", stops: Stop(6, 8)), Route("Iron", move: "WaitForDespawn", stops: Stop(7, 7)) };
            Eq("Iron", free[RouteLogic.Choose(free, true, false, _ => DateTime.MinValue, new Random(1))].Key);
        });
        Test("mid-way without ResetToStart: most stops left", () =>
        {
            var free = new List<RouteSnap> { Route("A", stops: Stop(6, 8)), Route("B", stops: Stop(2, 8)) };
            Eq("B", free[RouteLogic.Choose(free, false, false, _ => DateTime.MinValue, new Random(1))].Key);
        });
        Test("rotate takes the route started longest ago", () =>
        {
            var free = new List<RouteSnap> { Route("A", stops: Stop(1, 8)), Route("B", stops: Stop(1, 8)), Route("C", stops: Stop(1, 8)) };
            var last = new Dictionary<string, DateTime> { ["A"] = T0, ["B"] = T0.AddMinutes(-30) };
            Eq("C", free[RouteLogic.Choose(free, true, true, k => last.TryGetValue(k, out var t) ? t : DateTime.MinValue, new Random(1))].Key);
            last["C"] = T0;
            Eq("B", free[RouteLogic.Choose(free, true, true, k => last.TryGetValue(k, out var t) ? t : DateTime.MinValue, new Random(1))].Key);
        });
        Test("nothing free", () => Eq(-1, RouteLogic.Choose(new List<RouteSnap>(), true, false, _ => DateTime.MinValue, new Random(1))));
        Test("audit 02.11: two patrols with the same key keep their own start history", () =>
        {
            // The two MilitiaCotton patrols: rotate must take the one that hasn't run, random must skip the one that ran.
            var free = new List<RouteSnap> { Route("MilitiaCotton", stops: Stop(1, 4), id: "71689:2"),
                                             Route("MilitiaCotton", stops: Stop(1, 7), id: "71665:2") };
            var last = new Dictionary<string, DateTime> { ["71689:2"] = T0 };
            Func<string, DateTime> ls = k => last.TryGetValue(k, out var t) ? t : DateTime.MinValue;
            Eq("71665:2", free[RouteLogic.Choose(free, true, true, ls, new Random(1))].Id, "rotate");
            for (int i = 0; i < 20; i++) Eq("71665:2", free[RouteLogic.Choose(free, true, false, ls, new Random(i))].Id, "random");
            last["71665:2"] = T0.AddMinutes(20);
            Eq("71689:2", free[RouteLogic.Choose(free, true, true, ls, new Random(1))].Id, "rotate back");
        });
        Test("loop routes (Oakveil): no mid-way, no reset, robbed leftovers free the route once frozen", () =>
        {
            var loop = new Progress(2, 6, 8, true);
            True(!loop.AtLast && loop.Left == 0, "no end");
            Eq("3/8 loop", loop.Log);
            Eq(PickGroup.AtStart, RouteLogic.Group(Route(stops: loop)), "counts as at its start");
            True(!RouteLogic.NeedsReset(loop, "Moving"), "no reset");
            var robbed = RouteLogic.Classify(Route(units: 4, carts: 0, stops: loop));
            Eq(RouteState.LoopLeftover, robbed); True(!RouteLogic.IsFree(robbed), "busy while players are near");
            var frozen = RouteLogic.Classify(Route(units: 4, onMap: 0, carts: 0, stops: loop));
            Eq(RouteState.FrozenLeftover, frozen); True(RouteLogic.IsFree(frozen), "free once frozen");
            Eq(RouteState.OnRoad, RouteLogic.Classify(Route(units: 5, carts: 1, stops: loop)));
            var s = new RunState { Started = T0 };
            RunLogic.Squad(s, T0, false, Us(1, 2), 20);
            var five = TimeSpan.FromMinutes(5); var ninety = TimeSpan.FromMinutes(90);
            Eq(RunEnd.None, RunLogic.Outcome(s, T0.AddMinutes(1), 2, 1, false, five, ninety, out _, true));
            Eq(RunEnd.LoopCartLost, RunLogic.Outcome(s, T0.AddMinutes(2), 1, 0, false, five, ninety, out var lost, true));
            True(lost, "cart lost reported");
        });
        Test("reset only mid-way routes", () =>
        {
            True(RouteLogic.NeedsReset(Stop(6, 8), "StoppedDueToUnit"), "mid-way");
            True(!RouteLogic.NeedsReset(Stop(1, 8), "WaitingForPath"), "at start");
            True(!RouteLogic.NeedsReset(Stop(8, 8), "WaitForDespawn"), "at end");
            True(!RouteLogic.NeedsReset(null, "Moving"), "unknown path");
        });

        // --- Why a caravan disappeared
        Test("log 31.10: last seen 7/8, route back at 1/8 with no units = reached the end", () =>
            Eq(GoneReason.ReachedEnd, GoneLogic.Reason(false, Stop(7, 8), false, false,
                Route(move: "Inactive", spawner: "WaitingForSpawn", stops: Stop(1, 8), units: 0))));
        Test("log 31.10: cart gone at 3/8, route stays there with 3 units = destroyed", () =>
            Eq(GoneReason.CartDestroyed, GoneLogic.Reason(false, Stop(3, 8), false, false,
                Route(move: "StoppedDueToUnit", stops: Stop(3, 8), units: 3))));
        Test("whole squad killed mid-way = destroyed", () =>
            Eq(GoneReason.CartDestroyed, GoneLogic.Reason(false, Stop(4, 8), false, false, Route(stops: Stop(4, 8), units: 0))));
        Test("robbed right at the first stop = destroyed", () =>
            Eq(GoneReason.CartDestroyed, GoneLogic.Reason(false, Stop(1, 8), false, false, Route(stops: Stop(1, 8), units: 0))));
        Test("cart gone while the route is at the end = reached the end", () =>
            Eq(GoneReason.ReachedEnd, GoneLogic.Reason(false, Stop(7, 8), false, false, Route(move: "WaitForDespawn", stops: Stop(8, 8), units: 2))));
        Test("cart still there at the last stop", () =>
            Eq(GoneReason.AtLastStop, GoneLogic.Reason(false, Stop(7, 8), true, true, Route(move: "WaitForDespawn", stops: Stop(8, 8)))));
        Test("cart without its animal / parked", () =>
        {
            Eq(GoneReason.LeftWithoutAnimal, GoneLogic.Reason(false, Stop(6, 7), true, true, Route(stops: Stop(6, 7))));
            Eq(GoneReason.Parked, GoneLogic.Reason(false, Stop(6, 7), true, false, Route(stops: Stop(1, 7))));
        });
        Test("loot", () =>
        {
            Eq(GoneReason.Looted, GoneLogic.Reason(true, null, true, true, null));
            Eq(GoneReason.ContainerRemoved, GoneLogic.Reason(true, null, false, false, null));
        });

        // --- Spawn range
        Test("Source = Mod blocks a waiting route, opens it once a squad is out", () =>
        {
            Eq(SpawnRange.BlockedY, SpawnRange.SourceTarget(true, true, 0, 90));
            Eq<float?>(null, SpawnRange.SourceTarget(true, true, 0, SpawnRange.BlockedY));
            Eq(SpawnRange.NormalY, SpawnRange.SourceTarget(true, false, 0, SpawnRange.BlockedY));
            Eq<float?>(null, SpawnRange.SourceTarget(true, false, 0, 90));
        });
        Test("Source = Mod blocks a route idle at its last stop too, not one with a caravan out", () =>
        {
            True(SpawnRange.Idle("WaitingForSpawn") && SpawnRange.Idle("WaitingForDespawn"), "idle");
            True(!SpawnRange.Idle("Active"), "caravan out");
        });
        Test("zero range is never left: blocked with Mod, opened otherwise", () =>
        {
            Eq(SpawnRange.BlockedY, SpawnRange.SourceTarget(true, true, 0, 0));
            Eq(SpawnRange.NormalY, SpawnRange.SourceTarget(false, true, 0, 0));
        });
        Test("Game / Both open blocked routes and leave the rest", () =>
        {
            Eq(SpawnRange.NormalY, SpawnRange.SourceTarget(false, true, 0, SpawnRange.BlockedY));
            Eq<float?>(null, SpawnRange.SourceTarget(false, true, 0, 90));
            Eq<float?>(null, SpawnRange.SourceTarget(false, false, 0, SpawnRange.StartY));
        });
        Test("restore after a start: blocked or zero becomes the game's range", () =>
        {
            Eq(SpawnRange.NormalY, SpawnRange.RestoreY(0, SpawnRange.BlockedY));
            Eq(SpawnRange.NormalY, SpawnRange.RestoreY(0, 0));
            Eq(120f, SpawnRange.RestoreY(0, 120));
        });
        Test("cleanup opens everything the mod may have written", () =>
        {
            True(SpawnRange.CleanupOpens(0, SpawnRange.BlockedY) && SpawnRange.CleanupOpens(0, 0) && SpawnRange.CleanupOpens(0, SpawnRange.StartY), "opens");
            True(!SpawnRange.CleanupOpens(0, 90), "leaves the game's range");
            True(SpawnRange.TimersZero(0, 0) && !SpawnRange.TimersZero(1000, 2700), "timers");
        });

        // --- A forced start, second by second
        const int Max = 20;
        var timeout = TimeSpan.FromMinutes(5);
        var maxRun = TimeSpan.FromMinutes(90);

        Test("log 30.10: guards come a minute after the horse and stay (caravan without guards)", () =>
        {
            var s = new RunState { Started = T0 };
            var a = RunLogic.Squad(s, T0.AddSeconds(5), false, Us(1), Max);       // horse with cart
            True(a.JustSpawned && !a.Assembled, "spawned, not assembled");
            var b = RunLogic.Squad(s, T0.AddSeconds(30), false, Us(1), Max);
            True(!b.Assembled, "still assembling after 25 s");
            var c = RunLogic.Squad(s, T0.AddSeconds(65), false, Us(1, 2, 3, 4, 5, 6, 7, 8), Max);  // guards
            Eq(8, c.Remaining.Count, "squad"); Eq(0, c.Replacements.Count, "replacements");
            True(!c.Assembled, "assembly window restarts when the squad grows");
            True(!RunLogic.Squad(s, T0.AddSeconds(150), false, Us(1, 2, 3, 4, 5, 6, 7, 8), Max).Assembled, "89 s quiet");
            True(RunLogic.Squad(s, T0.AddSeconds(156), false, Us(1, 2, 3, 4, 5, 6, 7, 8), Max).Assembled, "90 s quiet");
            s.Restored = true;   // the executor wrote the route's settings
            True(!RunLogic.Squad(s, T0.AddSeconds(157), false, Us(1, 2, 3, 4, 5, 6, 7, 8), Max).Assembled, "only once");
        });
        Test("assembly ends 4 minutes after the first unit even if the squad keeps growing", () =>
        {
            var s = new RunState { Started = T0 };
            RunLogic.Squad(s, T0, false, Us(1), Max);
            var ids = new List<int> { 1 };
            bool assembled = false;
            for (int sec = 60; sec <= 240; sec += 60) { ids.Add(ids.Count + 1); assembled |= RunLogic.Squad(s, T0.AddSeconds(sec), false, Us(ids.ToArray()), Max).Assembled; }
            True(assembled, "assembled by 4 min");
        });
        Test("respawn guard: after a kill any new unit is a replacement (memory leak 28.10)", () =>
        {
            var s = new RunState { Started = T0 };
            RunLogic.Squad(s, T0, false, Us(1, 2, 3), Max);
            var k = RunLogic.Squad(s, T0.AddSeconds(10), false, Us(1, 2, 9), Max);   // 3 killed, 9 sent by the game
            Eq(1, k.Replacements.Count, "replacements"); Eq(U(9), k.Replacements[0]); Eq(2, k.Remaining.Count, "remaining");
            True(s.LostAny, "lost");
        });
        Test("04.11: a kill while the squad assembles gives the route its own timers back at once (126 respawns)", () =>
        {
            var s = new RunState { Started = T0 };
            True(!RunLogic.Squad(s, T0, false, Us(1, 2, 3), Max, 8).Assembled, "assembling");
            var k = RunLogic.Squad(s, T0.AddSeconds(30), false, Us(1, 2), Max, 8);
            True(k.Assembled, "a member was killed 30 s after the spawn: restore now, not 90 s after the last growth");
        });
        Test("audit 02.11: a guard still on its way after an early kill is a member, not a replacement", () =>
        {
            var s = new RunState { Started = T0 };
            RunLogic.Squad(s, T0, false, Us(1, 2, 3), Max, 8);                    // animal, cart guard, one guard
            var k = RunLogic.Squad(s, T0.AddSeconds(10), false, Us(1, 2, 4), Max, 8); // 3 killed, 4 is the next original guard
            Eq(0, k.Replacements.Count, "late guard kept"); Eq(3, k.Remaining.Count);
            RunLogic.Squad(s, T0.AddSeconds(20), false, Us(1, 2, 4, 5, 6, 7, 8), Max, 8);
            Eq(8, s.Known.Count, "squad complete, the killed one counted");
            var r = RunLogic.Squad(s, T0.AddSeconds(30), false, Us(1, 2, 4, 5, 6, 7, 8, 9), Max, 8);
            Eq(1, r.Replacements.Count, "beyond its size it is a replacement"); Eq(U(9), r.Replacements[0]);
        });
        Test("audit 02.11: the route's settings are asked for until the executor confirms them", () =>
        {
            var s = new RunState { Started = T0 };
            RunLogic.Squad(s, T0, false, Us(1), Max);
            True(RunLogic.Squad(s, T0.AddSeconds(90), false, Us(1), Max).Assembled, "assembled");
            True(!s.Restored, "not marked before the write");
            True(RunLogic.Squad(s, T0.AddSeconds(91), false, Us(1), Max).Assembled, "the write failed: asked again");
        });
        Test("04.11: a loot container stays LootKeepMinutes after it was first seen", () =>
        {
            Eq((double?)1600, LootLogic.EndTime(1000, 600, 1060), "game's 60 s pushed out to 10 min");
            Eq((double?)null, LootLogic.EndTime(1000, 600, 1600), "already there");
            Eq((double?)null, LootLogic.EndTime(1000, 600, 1700), "the game's own is later");
            Eq((double?)null, LootLogic.EndTime(1000, 0, 1060), "off");
        });
        Test("02.11: a spawn buff is a leftover 10 s after the unit appeared", () =>
        {
            var s = new RunState { Started = T0 };
            True(!RunLogic.PastSpawn(s, null, T0.AddSeconds(30)), "nothing spawned");
            RunLogic.Squad(s, T0, false, Us(1), Max);
            RunLogic.Squad(s, T0.AddSeconds(8), false, Us(1, 2), Max);
            True(!RunLogic.PastSpawn(s, U(1), T0.AddSeconds(9)), "animal 9 s");
            True(RunLogic.PastSpawn(s, U(1), T0.AddSeconds(10)), "animal 10 s");
            True(!RunLogic.PastSpawn(s, U(2), T0.AddSeconds(10)), "guard 2 s");
            True(RunLogic.PastSpawn(s, null, T0.AddSeconds(10)), "cart: since the first unit");
            True(!RunLogic.PastSpawn(s, U(5), T0.AddSeconds(60)), "not a member");
        });
        Test("squad over MaxSquadSize is cut", () =>
        {
            var s = new RunState { Started = T0 };
            var step = RunLogic.Squad(s, T0, false, Us(1, 2, 3, 4, 5, 6), 4);
            Eq(4, step.Remaining.Count); Eq(2, step.OverLimit.Count);
        });
        Test("units of an old squad at the end are not the new caravan", () =>
        {
            var s = new RunState { Started = T0 };
            var step = RunLogic.Squad(s, T0.AddSeconds(3), true, Us(1, 2), Max);
            True(!s.Spawned && !step.JustSpawned, "not spawned"); Eq(2, step.Remaining.Count);
        });
        Test("outcomes: no spawn, cart lost, done, timeout", () =>
        {
            var s = new RunState { Started = T0 };
            Eq(RunEnd.None, RunLogic.Outcome(s, T0.AddMinutes(4), 0, 0, false, timeout, maxRun, out _));
            Eq(RunEnd.NoSpawn, RunLogic.Outcome(s, T0.AddMinutes(6), 0, 0, false, timeout, maxRun, out _));

            s = new RunState { Started = T0 };
            RunLogic.Squad(s, T0, false, Us(1, 2), Max);
            Eq(RunEnd.None, RunLogic.Outcome(s, T0.AddMinutes(1), 2, 1, false, timeout, maxRun, out var lost1)); True(!lost1, "cart there");
            Eq(RunEnd.None, RunLogic.Outcome(s, T0.AddMinutes(2), 1, 0, false, timeout, maxRun, out var lost2)); True(lost2 && s.CartGone, "cart lost");
            RunLogic.Outcome(s, T0.AddMinutes(3), 1, 0, false, timeout, maxRun, out var lost3); True(!lost3, "reported once");
            Eq(RunEnd.Done, RunLogic.Outcome(s, T0.AddMinutes(4), 0, 0, false, timeout, maxRun, out _));

            s = new RunState { Started = T0 };
            RunLogic.Squad(s, T0, false, Us(1), Max);
            Eq(RunEnd.Timeout, RunLogic.Outcome(s, T0.AddMinutes(91), 1, 1, false, timeout, maxRun, out _));
        });
        Test("log 01.11: a squad standing at the last stop for 2 minutes is stuck, not 77 minutes", () =>
        {
            var s = new RunState { Started = T0 };
            RunLogic.Squad(s, T0, false, Us(1, 2, 3, 4, 5, 6, 7, 8), Max);
            Eq(RunEnd.None, RunLogic.Outcome(s, T0.AddMinutes(13), 8, 1, true, timeout, maxRun, out _));
            Eq(RunEnd.None, RunLogic.Outcome(s, T0.AddMinutes(14), 8, 1, true, timeout, maxRun, out _));
            Eq(RunEnd.StuckAtEnd, RunLogic.Outcome(s, T0.AddMinutes(15), 8, 1, true, timeout, maxRun, out _));
            var s2 = new RunState { Started = T0 };
            RunLogic.Squad(s2, T0, false, Us(1), Max);
            RunLogic.Outcome(s2, T0.AddMinutes(13), 1, 1, true, timeout, maxRun, out _);
            RunLogic.Outcome(s2, T0.AddMinutes(14), 1, 1, false, timeout, maxRun, out _);
            Eq(RunEnd.None, RunLogic.Outcome(s2, T0.AddMinutes(15), 1, 1, true, timeout, maxRun, out _));
        });

        Test("log 31.10 Cotton: a route at its last stop is woken before it may spawn", () =>
        {
            Eq(RunPhase.Waking, RunLogic.FirstPhase("WaitForDespawn"));
            Eq(RunPhase.Armed, RunLogic.FirstPhase("WaitingForPath"));
            Eq(RunPhase.Armed, RunLogic.FirstPhase("StoppedDueToUnit"));
        });
        Test("waking: spawn only after the game returned the route to its first stop", () =>
        {
            var s = new RunState { Started = T0, Phase = RunPhase.Waking };
            Eq(WakeStep.Wait, RunLogic.ReadyToArm(s, T0.AddSeconds(1), true, false, 0, false), "still at the end");
            Eq(WakeStep.Wait, RunLogic.ReadyToArm(s, T0.AddSeconds(2), false, true, 0, true), "index reset, patrol not moved yet");
            Eq(WakeStep.GiveUp, RunLogic.ReadyToArm(s, T0.AddSeconds(21), true, false, 0, false), "gives up after 20 s, no spawn at the end");
        });
        Test("log 01.11: no spawn one second after the reset; the spawner must be idle and it must hold 3 s", () =>
        {
            var s = new RunState { Started = T0, Phase = RunPhase.Waking };
            // 1.0.8 armed here (back at the first stop 1 s after waking): the animal was immortal.
            Eq(WakeStep.Wait, RunLogic.ReadyToArm(s, T0.AddSeconds(1), false, true, 266, false), "old squad still being removed");
            Eq(WakeStep.Wait, RunLogic.ReadyToArm(s, T0.AddSeconds(2), false, true, 266, true), "idle just now");
            Eq(WakeStep.Wait, RunLogic.ReadyToArm(s, T0.AddSeconds(4), false, true, 266, true), "2 s idle");
            Eq(WakeStep.Arm, RunLogic.ReadyToArm(s, T0.AddSeconds(5), false, true, 266, true), "3 s idle");
            var s2 = new RunState { Started = T0, Phase = RunPhase.Waking };
            RunLogic.ReadyToArm(s2, T0.AddSeconds(2), false, true, 50, true);
            Eq(WakeStep.Wait, RunLogic.ReadyToArm(s2, T0.AddSeconds(4), false, true, 50, false), "not idle again: wait anew");
            Eq(WakeStep.Wait, RunLogic.ReadyToArm(s2, T0.AddSeconds(6), false, true, 50, true), "idle again for 0 s");
        });

        // --- Acts and caravan rules
        Test("acts by level", () =>
        {
            Eq(0, ActLogic.OfLevel(0)); Eq(1, ActLogic.OfLevel(39)); Eq(2, ActLogic.OfLevel(40)); Eq(2, ActLogic.OfLevel(59));
            Eq(3, ActLogic.OfLevel(60)); Eq(3, ActLogic.OfLevel(65)); Eq(4, ActLogic.OfLevel(66));
        });
        Test("EmptyAct = Lower: the nearest lower act with routes, else the nearest higher", () =>
        {
            var acts = new HashSet<int> { 1, 2 };
            Eq("2", string.Join("+", ActLogic.Served(4, acts, EmptyActUse.Lower)));
            Eq("1", string.Join("+", ActLogic.Served(1, acts, EmptyActUse.Lower)));
            Eq("3", string.Join("+", ActLogic.Served(1, new HashSet<int> { 3 }, EmptyActUse.Lower)));
            Eq(0, ActLogic.Served(1, new HashSet<int>(), EmptyActUse.Lower).Count);
        });
        Test("03.11: a player in act III (no carriages in the game) gets act IV routes by default", () =>
        {
            var acts = new HashSet<int> { 1, 2, 4 };
            Eq("4", string.Join("+", ActLogic.Served(3, acts, EmptyActUse.Higher)), "Higher");
            Eq("2+4", string.Join("+", ActLogic.Served(3, acts, EmptyActUse.Both)), "Both");
            Eq("2", string.Join("+", ActLogic.Served(3, acts, EmptyActUse.Lower)), "Lower");
            Eq("2", string.Join("+", ActLogic.Served(3, new HashSet<int> { 1, 2 }, EmptyActUse.Higher)), "nothing higher");
            Eq("2", string.Join("+", ActLogic.Served(2, acts, EmptyActUse.Higher)), "own act has routes");
        });
        Test("03.11: the final V Blood of an act opens the next act", () =>
        {
            Eq(1, ActLogic.ByVBlood(new (int, bool)[0]), "nothing drunk");
            Eq(2, ActLogic.ByVBlood(new[] { (1, false), (2, false) }));
            Eq(3, ActLogic.ByVBlood(new[] { (1, true), (2, false), (2, true) }), "Octavian drunk, no act III V Blood yet");
            Eq(4, ActLogic.ByVBlood(new[] { (4, true) }), "not above IV");
        });
        Test("player act by level (Bloodcraft) or the higher of both", () =>
        {
            Eq(2, ActLogic.Of(PlayerActBy.VBlood, 2, 70));
            Eq(4, ActLogic.Of(PlayerActBy.Level, 2, 70));
            Eq(1, ActLogic.Of(PlayerActBy.Level, 3, 0), "level unknown");
            Eq(3, ActLogic.Of(PlayerActBy.Highest, 3, 45));
            Eq(4, ActLogic.Of(PlayerActBy.Highest, 3, 66));
        });
        Test("03.11: the Draculin prison carriage (ChurchLegion) is act II, with Dunley, whatever its guards' level", () =>
        {
            var none = ActLogic.ParseRouteActs("");
            Eq("2", string.Join("+", ActLogic.RouteActs("ChurchLegion", 70, none, out _)), "Dunley");
            Eq("4", string.Join("+", ActLogic.RouteActs("ChuchSilver", 70, none, out _)), "Silverlight");
            Eq("4", string.Join("+", ActLogic.RouteActs("ChurchMutants", 63, none, out _)), "Silverlight, guards below 66");
            Eq("4", string.Join("+", ActLogic.RouteActs("BlackfangSouth", 80, none, out _)), "Oakveil");
            Eq("1", string.Join("+", ActLogic.RouteActs("BanditCopper", 25, none, out _)), "Farbane");
        });
        Test("regions of routes (wiki, Carriage page)", () =>
        {
            Eq("Farbane", ActLogic.Region("BanditCopper")); Eq("Dunley", ActLogic.Region("MilitiaCotton"));
            Eq("Dunley", ActLogic.Region("ChurchLegion")); Eq("Silverlight", ActLogic.Region("ChuchSilver"));
            Eq("Silverlight", ActLogic.Region("ChurchMutants")); Eq("Oakveil", ActLogic.Region("BlackfangSouth"));
            Eq(null, ActLogic.Region("SomethingNew"));
        });
        Test("RouteActs: route keys, regions, several acts", () =>
        {
            var m = ActLogic.ParseRouteActs(" MilitiaIron=2, silverlight=3+4, Oakveil=4, bad=7, x, ChuchGrape=1+9 ");
            Eq(3, m.Count, "parsed");
            Eq("2", string.Join("+", ActLogic.RouteActs("MilitiaIron", 50, m, out bool man1))); True(man1, "manual");
            Eq("3+4", string.Join("+", ActLogic.RouteActs("ChuchSilver", 70, m, out _)), "by region");
            Eq("3+4", string.Join("+", ActLogic.RouteActs("ChuchGrape", 70, m, out _)), "a wrong value is skipped, region applies");
            Eq("2", string.Join("+", ActLogic.RouteActs("MilitiaQuartz", 50, m, out bool man2))); True(!man2, "by region");
            var key = ActLogic.ParseRouteActs("ChurchLegion=4,Dunley=3");
            Eq("4", string.Join("+", ActLogic.RouteActs("ChurchLegion", 70, key, out _)), "the route key wins over its region");
            Eq("3", string.Join("+", ActLogic.RouteActs("MilitiaIron", 50, key, out _)), "region set by hand");
            Eq("1", string.Join("+", ActLogic.RouteActs("NewRoute", 30, m, out _)), "unknown route: by level");
            Eq(0, ActLogic.RouteActs("X", 0, m, out _).Count, "unknown level");
        });
        Test("log 30.10: a cart left without its animal gets no icon", () =>
        {
            True(!CaravanRules.ShowCart(true, false, "Moving", true), "orphan");
            True(!CaravanRules.ShowCart(true, false, "WaitForDespawn", false), "at the end");
            True(!CaravanRules.ShowCart(true, true, "Moving", false), "loot already shown");
            True(CaravanRules.ShowCart(true, false, "Moving", false), "moving caravan");
            True(!CaravanRules.ShowContainer(true, true) && CaravanRules.ShowContainer(false, true), "containers");
        });
        Test("log 31.10 21:53: a cart in a fight keeps its icon though its animal is 30+ m away", () =>
        {
            True(CaravanRules.Pulled(true, true, 45, "StoppedDueToUnit"), "fight");
            True(CaravanRules.Pulled(true, true, 10, "Moving"), "on the road");
            True(!CaravanRules.Pulled(true, true, 45, "Moving"), "animal walked off");
            True(!CaravanRules.Pulled(false, false, float.MaxValue, "StoppedDueToUnit"), "animal gone");
            True(!CaravanRules.Pulled(true, false, 10, "Moving"), "animal frozen off the map");
        });
        Test("a caravan is announced once, not at server start, not frozen", () =>
        {
            True(!CaravanRules.Announce(false, false, true, false, false, "Moving"), "first check");
            True(CaravanRules.Announce(true, false, true, false, false, "Moving"), "sets out");
            True(!CaravanRules.Announce(true, true, true, false, false, "Moving"), "again");
            True(!CaravanRules.Announce(true, false, true, true, false, "Moving"), "frozen");
            True(!CaravanRules.Announce(true, false, true, false, true, "Moving"), "orphan");
            True(!CaravanRules.Announce(true, false, true, false, false, "WaitForDespawn"), "at the end");
        });

        // --- Languages file
        Test("audit 02.11: a failed read of the languages file loses nobody's choice", () =>
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ct-lang-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            string path = System.IO.Path.Combine(dir, "CarriageTracker.languages.txt");
            System.IO.File.WriteAllLines(path, new[] { "1=ru", "7=en" });
            var now = T0;
            var store = new LanguageStore(path, l => l == "en" || l == "ru") { Now = () => now };
            using (new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.ReadWrite, System.IO.FileShare.None))
            {
                Eq(null, store.Of(1), "file locked");
                True(!store.Set(2, "ru"), "not written while unread");
            }
            Eq("ru", store.Of(2), "kept meanwhile");
            now = T0.AddSeconds(31);
            Eq("ru", store.Of(1), "read again later");
            Eq("1=ru|7=en|2=ru", string.Join("|", System.IO.File.ReadAllLines(path)), "file");
            True(store.Set(2, "ru"), "unchanged");
            System.IO.Directory.Delete(dir, true);
        });
        Test("languages file of the old name is moved, missing file is an empty list", () =>
        {
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ct-lang-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            string old = System.IO.Path.Combine(dir, "Bazarov.CaravanTracker.languages.txt");
            string path = System.IO.Path.Combine(dir, "CarriageTracker.languages.txt");
            System.IO.File.WriteAllLines(old, new[] { "5=en" });
            var store = new LanguageStore(path, l => l == "en" || l == "ru", old);
            Eq("en", store.Of(5)); True(!System.IO.File.Exists(old) && System.IO.File.Exists(path), "moved");
            var empty = new LanguageStore(System.IO.Path.Combine(dir, "none.txt"), l => true);
            Eq(null, empty.Of(5)); True(empty.Loaded, "missing file read as empty");
            True(empty.Set(5, "ru"), "written"); Eq("ru", new LanguageStore(System.IO.Path.Combine(dir, "none.txt"), l => true).Of(5));
            System.IO.Directory.Delete(dir, true);
        });

        // --- Chat
        Test("long Russian lists are packed into lines that fit 512 bytes", () =>
        {
            var items = new List<string>();
            for (int i = 0; i < 6; i++) items.Add($"«ополчение: хлопок» (авто, акт II): едет к концу маршрута, остановка {i + 1} из 4, {i * 3} мин с запуска");
            var lines = ChatLines.Pack(items, "; ", "Запуски: ");
            True(lines.Count > 1, "split");
            foreach (var l in lines) True(System.Text.Encoding.UTF8.GetByteCount(l) <= ChatLines.MaxBytes, "too long: " + l.Length);
            True(lines[0].StartsWith("Запуски: "), "prefix");
            Eq(string.Join("; ", items), string.Join("; ", lines).Replace("Запуски: ", ""), "nothing lost");
        });
        Test("short list stays one line, empty list gives nothing", () =>
        {
            Eq(1, ChatLines.Pack(new[] { "a", "b" }, ", ").Count);
            Eq("a, b", ChatLines.Pack(new[] { "a", "b" }, ", ")[0]);
            Eq(0, ChatLines.Pack(new string[0], ", ").Count);
        });
        Test("welcome: new players once, nobody at the server start, again after leaving", () =>
        {
            var seen = new HashSet<ulong>();
            Eq(0, Welcome.Greet(seen, new ulong[] { 1, 2 }, false).Count);
            Eq(0, Welcome.Greet(seen, new ulong[] { 1, 2 }, true).Count);
            var g = Welcome.Greet(seen, new ulong[] { 1, 2, 3 }, true);
            Eq(1, g.Count); Eq(3UL, g[0]);
            Eq(0, Welcome.Greet(seen, new ulong[] { 1, 3 }, true).Count);
            Eq(2UL, Welcome.Greet(seen, new ulong[] { 1, 2, 3 }, true)[0]);
        });

        // --- 1.0.19: a route's normal values come from its prefab (audit 07.11)
        Test("a route's own normal range is what gets written back", () =>
        {
            Eq(120f, SpawnRange.SourceTarget(false, true, 0, SpawnRange.BlockedY, 120f));
            Eq(120f, SpawnRange.SourceTarget(true, false, 0, 0, 120f));
            Eq(SpawnRange.BlockedY, SpawnRange.SourceTarget(true, true, 0, 0, 120f));
            Eq(120f, SpawnRange.RestoreY(0, SpawnRange.BlockedY, 120f));
            Eq(75f, SpawnRange.RestoreY(0, 75f, 120f));
        });
        Test("a prefab's odd values aren't taken for normal ones", () =>
        {
            True(SpawnRange.SaneRange(90f), "90 m is a normal range");
            True(!SpawnRange.SaneRange(0f), "0 is not");
            True(!SpawnRange.SaneRange(SpawnRange.BlockedY), "blocked is not");
            True(!SpawnRange.SaneRange(SpawnRange.StartY), "a start range is not");
            True(!SpawnRange.SaneRange(float.NaN), "NaN is not");
            True(SpawnRange.SaneTimers(1000, 2700), "the game's timers");
            True(!SpawnRange.SaneTimers(0, 0), "a start's zero timers");
            True(!SpawnRange.SaneTimers(2700, 1000), "min above max");
        });

        // --- 1.0.19: chat messages fit FixedString512Bytes (audit 07.11)
        Test("a short message is left as it is", () =>
        {
            Eq("hello", ChatText.Fit("hello", out bool cut));
            True(!cut, "not cut");
        });
        Test("a long Russian message is cut on a whole letter within 509 bytes", () =>
        {
            string text = new string('я', 300);   // 600 bytes
            string fit = ChatText.Fit(text, out bool cut);
            True(cut, "cut");
            Eq(254, fit.Length, "letters kept");
            True(System.Text.Encoding.UTF8.GetByteCount(fit) <= ChatText.MaxBytes, "fits");
        });
        Test("a message is not cut inside a color tag", () =>
        {
            string text = new string('a', 500) + "<color=#FFC83C>bb</color>";
            string fit = ChatText.Fit(text, out bool cut);
            True(cut, "cut");
            Eq(new string('a', 500), fit, "the tag cut in two is dropped");
        });
        Test("a whole tag before the cut stays", () =>
        {
            string text = "<b>" + new string('a', 600);
            string fit = ChatText.Fit(text, out _);
            True(fit.StartsWith("<b>"), "tag kept");
            Eq(ChatText.MaxBytes, System.Text.Encoding.UTF8.GetByteCount(fit), "filled up to the limit");
        });

        // --- 1.0.20: an emptied container goes as the game would have it (07.11)
        Test("an emptied container the mod kept goes 60 s later", () =>
        {
            Eq<double?>(1060, LootLogic.EmptiedEndTime(1000, 1600));
            Eq<double?>(null, LootLogic.EmptiedEndTime(1000, 1030), "a sooner time stays");
        });

        // --- 1.0.21: icons and markers (IconLogic), audit 07.11 by Nikolai, A1
        IconFacts Icon(IconKind kind, bool exists = true, bool caravan = true, bool onMarker = false, bool wanted = true, bool covered = false) =>
            new(kind, exists, caravan, onMarker, wanted, covered);
        MarkerFacts Marker(bool made = false, bool oneOfOurs = true, Spawner spawner = Spawner.Caravan, bool wanted = true,
                           bool current = true, bool moved = false, bool covered = false) =>
            new(made, oneOfOurs, spawner, wanted, current, moved, covered);

        Test("icon kinds: current first, then earlier, then preset", () =>
        {
            var earlier = new HashSet<int> { 2 };
            var presets = new HashSet<int> { 1, 2, 3 };
            Eq(IconKind.Current, IconLogic.KindOf(1, 1, earlier, presets));
            Eq(IconKind.Earlier, IconLogic.KindOf(2, 1, earlier, presets));
            Eq(IconKind.Preset, IconLogic.KindOf(3, 1, earlier, presets));
            Eq(IconKind.Other, IconLogic.KindOf(4, 1, earlier, presets));
        });
        Test("an icon of the current kind on a wanted caravan stays, a second one goes", () =>
        {
            Eq((IconAct.Keep, DropWhy.None), IconLogic.ForIcon(Icon(IconKind.Current)));
            Eq((IconAct.Drop, DropWhy.Duplicate), IconLogic.ForIcon(Icon(IconKind.Current, covered: true)));
        });
        Test("icon on a caravan that no longer gets one goes (it froze: the marker takes over)", () =>
            Eq((IconAct.Drop, DropWhy.Unwanted), IconLogic.ForIcon(Icon(IconKind.Current, wanted: false))));
        Test("icon of an earlier kind or a preset on a caravan goes (Icon changed)", () =>
        {
            Eq((IconAct.Drop, DropWhy.Kind), IconLogic.ForIcon(Icon(IconKind.Earlier)));
            Eq((IconAct.Drop, DropWhy.Kind), IconLogic.ForIcon(Icon(IconKind.Preset)));
        });
        Test("another mod's icon on a caravan is left alone", () =>
            Eq(IconAct.Ignore, IconLogic.ForIcon(Icon(IconKind.Other)).Act));
        Test("an icon of the mod's kind whose object is gone goes", () =>
            Eq((IconAct.Drop, DropWhy.Gone), IconLogic.ForIcon(Icon(IconKind.Current, exists: false, caravan: false))));
        Test("a merchant icon on a game merchant is never touched (Icon = merchant, audit 07.11 R4)", () =>
        {
            Eq(IconAct.Ignore, IconLogic.ForIcon(Icon(IconKind.Current, exists: true, caravan: false)).Act, "on a living merchant");
            Eq(IconAct.Ignore, IconLogic.ForIcon(Icon(IconKind.Current, exists: false, caravan: false, onMarker: true)).Act, "on a marker");
        });
        Test("a preset icon off a caravan is left alone (it may be the game's)", () =>
            Eq(IconAct.Ignore, IconLogic.ForIcon(Icon(IconKind.Preset, exists: false, caravan: false)).Act));

        Test("KindredPortals marker (spawned by a player) is never the mod's (audit 02.11)", () =>
        {
            False(IconLogic.Owned(Marker(spawner: Spawner.Other)), "not owned");
            Eq(IconAct.Ignore, IconLogic.ForMarker(Marker(spawner: Spawner.Other)).Act);
        });
        Test("after a restart a marker with our icon spawned by a caravan or by nothing is the mod's", () =>
        {
            True(IconLogic.Owned(Marker(spawner: Spawner.Caravan)), "caravan");
            True(IconLogic.Owned(Marker(spawner: Spawner.Gone)), "gone");
            True(IconLogic.Owned(Marker(spawner: Spawner.None)), "none");
            False(IconLogic.Owned(Marker(oneOfOurs: false, spawner: Spawner.None)), "two icons or another kind");
        });
        Test("a marker the mod made is its own whatever spawned it", () =>
            True(IconLogic.Owned(Marker(made: true, oneOfOurs: false, spawner: Spawner.Other)), "made here"));
        Test("the mod's marker: kept, or removed with the reason", () =>
        {
            Eq((IconAct.Keep, DropWhy.None), IconLogic.ForMarker(Marker()));
            Eq((IconAct.Drop, DropWhy.Gone), IconLogic.ForMarker(Marker(spawner: Spawner.Gone)), "caravan gone while the server was off");
            Eq((IconAct.Drop, DropWhy.Unwanted), IconLogic.ForMarker(Marker(wanted: false)), "caravan thawed");
            Eq((IconAct.Drop, DropWhy.Kind), IconLogic.ForMarker(Marker(current: false)), "Icon changed");
            Eq((IconAct.Drop, DropWhy.Moved), IconLogic.ForMarker(Marker(moved: true)), "caravan moved away");
            Eq((IconAct.Drop, DropWhy.Duplicate), IconLogic.ForMarker(Marker(covered: true)), "second marker");
        });
        Test("frozen caravan whose route goes on: its marker isn't made anew every check (05.11)", () =>
        {
            // Until 1.0.16 every 10 m: a new marker at almost every 30 s check, rows of stale icons along the road.
            False(IconLogic.MovedFar(true, 40, TimeSpan.FromSeconds(30)), "40 m after 30 s");
            False(IconLogic.MovedFar(true, 150, TimeSpan.FromMinutes(2)), "150 m but younger than 3 min");
            True(IconLogic.MovedFar(true, 150, TimeSpan.FromMinutes(3)), "150 m and 3 min old");
            True(IconLogic.MovedFar(true, 150, null), "left from before a restart: by distance");
            False(IconLogic.MovedFar(true, 99, null), "under 100 m");
        });
        Test("moving caravan in proxy mode: the marker follows in 30 m steps", () =>
        {
            False(IconLogic.MovedFar(false, 29, TimeSpan.Zero), "29 m");
            True(IconLogic.MovedFar(false, 30, TimeSpan.Zero), "30 m");
        });
        Test("stale icon on a marker that is gone is swept, only of the mod's own kinds", () =>
        {
            True(IconLogic.Orphan(IconKind.Current, false), "current");
            True(IconLogic.Orphan(IconKind.Earlier, false), "earlier");
            False(IconLogic.Orphan(IconKind.Preset, false), "preset");
            False(IconLogic.Orphan(IconKind.Current, true), "marker still there");
        });

        // --- 1.0.21: objects the game doesn't remove (DoomLogic), audit 07.11 by Nikolai, R1
        Test("an object still there after 5 min is destroyed once more, after 10 min given up", () =>
        {
            Eq(DoomStep.Wait, DoomLogic.Step(TimeSpan.FromMinutes(4), false, false), "4 min");
            Eq(DoomStep.Retry, DoomLogic.Step(TimeSpan.FromMinutes(5), false, false), "5 min");
            Eq(DoomStep.Wait, DoomLogic.Step(TimeSpan.FromMinutes(9), true, false), "9 min, tried again");
            Eq(DoomStep.GiveUp, DoomLogic.Step(TimeSpan.FromMinutes(10), true, false), "10 min");
            Eq(DoomStep.Wait, DoomLogic.Step(TimeSpan.FromHours(5), true, true), "given up: nothing more");
        });

        Console.WriteLine($"{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }
}
