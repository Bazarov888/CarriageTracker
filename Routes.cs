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

// Caravan routes. Each route is a game patrol (GlobalPatrolState + UnitCompositionSpawner)
// whose squad includes the horse CHAR_CarriageHorse_<route>.
static class Routes
{
    // Side: "east" or "west" when two patrols share the key (MilitiaCotton: the wiki's Eastern and Western
    // Cotton, about 600 m apart), by where the patrols are; null otherwise.
    public record Route(Entity Patrol, string Key, string Side = null);

    static List<Route> _cache;
    static DateTime _cacheAt;

    // Routes don't change while the server runs, so the list is kept. It is built again every
    // 10 minutes or as soon as one of its patrols no longer exists.
    public static List<Route> All(EntityManager em, PrefabCollectionSystem prefabs)
    {
        var now = DateTime.UtcNow;
        if (_cache == null || now >= _cacheAt.AddMinutes(10) || !_cache.TrueForAll(r => em.Exists(r.Patrol)))
        {
            _cache = Scan(em, prefabs);
            _cacheAt = now;
        }
        return new List<Route>(_cache);
    }

    static List<Route> Scan(EntityManager em, PrefabCollectionSystem prefabs)
    {
        var result = new List<Route>();
        var query = Queries.Get(em, "GlobalPatrolState+UnitCompositionSpawner all", true, Ct<GlobalPatrolState>.RO, Ct<UnitCompositionSpawner>.RO);
        var patrols = query.ToEntityArray(Allocator.Temp);
        try
        {
            foreach (var p in patrols)
            {
                if (!em.HasBuffer<UnitCompositionGroupUnitEntry>(p)) continue;
                var units = em.GetBuffer<UnitCompositionGroupUnitEntry>(p);
                for (int i = 0; i < units.Length; i++)
                {
                    object unit = units[i].Unit;
                    if (unit is not PrefabGUID guid || !prefabs._PrefabLookupMap.TryGetName(guid, out string name)) continue;
                    const string prefix = "CHAR_CarriageHorse_";
                    if (name == null || !name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    result.Add(new Route(p, name.Substring(prefix.Length)));
                    break;
                }
            }
        }
        finally
        {
            patrols.Dispose();
        }
        // By key, and two patrols of one key west first: the query's order changes when freeze tags come and go, and
        // until 1.0.22 the two MilitiaCotton patrols could swap their numbers between .cv routes and .cv start
        // (review of 11.11).
        var x = new Dictionary<(int, int), float>();
        foreach (var r in result) x[(r.Patrol.Index, r.Patrol.Version)] = Caravans.PositionOf(em, r.Patrol).x;
        result.Sort((a, b) =>
        {
            int byKey = string.CompareOrdinal(a.Key, b.Key);
            return byKey != 0 ? byKey : x[(a.Patrol.Index, a.Patrol.Version)].CompareTo(x[(b.Patrol.Index, b.Patrol.Version)]);
        });
        // Exactly two patrols of one key: the one farther east (greater x, as .cv m directions count) is "east".
        for (int i = 0; i < result.Count;)
        {
            int j = i;
            while (j < result.Count && result[j].Key == result[i].Key) j++;
            if (j - i == 2)
            {
                bool firstEast = Caravans.PositionOf(em, result[i].Patrol).x > Caravans.PositionOf(em, result[i + 1].Patrol).x;
                result[i] = result[i] with { Side = firstEast ? "east" : "west" };
                result[i + 1] = result[i + 1] with { Side = firstEast ? "west" : "east" };
            }
            i = j;
        }
        return result;
    }

    // Readable route name for chat, with the side when two patrols share the key.
    public static string Name(Route r) => L.Route(r.Key, r.Side);

    public static string Name(string key, Entity patrol)
    {
        var known = _cache?.Find(r => r.Patrol == patrol);
        return L.Route(key, known?.Key == key ? known.Side : null);
    }

    internal static Entity AsEntity(object o) =>
        o is Entity e ? e : o is NetworkedEntity ne ? ne.GetEntityOnServer() : Entity.Null;

    // Living units of the squad (horse, guards), from the UnitCompositionActiveUnit buffer.
    public static List<Entity> ActiveUnits(EntityManager em, Entity patrol)
    {
        var list = new List<Entity>();
        if (!em.HasBuffer<UnitCompositionActiveUnit>(patrol)) return list;
        var buf = em.GetBuffer<UnitCompositionActiveUnit>(patrol);
        for (int i = 0; i < buf.Length; i++)
        {
            var e = AsEntity(buf[i].UnitEntity);
            if (e != Entity.Null && em.Exists(e)) list.Add(e);
        }
        return list;
    }

    // Carts pulled by the horses in the list.
    public static List<Entity> CartsOf(EntityManager em, List<Entity> units)
    {
        var horses = new HashSet<(int, int)>();
        foreach (var u in units) horses.Add((u.Index, u.Version));
        var carts = new List<Entity>();
        var query = Queries.Get(em, "Script_CarriageData all", true, Ct<Script_CarriageData>.RO);
        var arr = query.ToEntityArray(Allocator.Temp);
        try
        {
            foreach (var c in arr)
            {
                var horse = em.GetComponentData<Script_CarriageData>(c).Horse.GetEntityOnServer();
                if (horses.Contains((horse.Index, horse.Version))) carts.Add(c);
            }
        }
        finally
        {
            arr.Dispose();
        }
        return carts;
    }

    public static string MoveStatus(EntityManager em, Entity patrol) =>
        Caravans.Has<MovePatrolState>(em, patrol) ? em.GetComponentData<MovePatrolState>(patrol).Status.ToString() : "?";

    public static Progress StopsOf(EntityManager em, Entity patrol)
    {
        if (patrol == Entity.Null || !em.Exists(patrol) || !Caravans.Has<GlobalPatrolState>(em, patrol)) return null;
        var gp = em.GetComponentData<GlobalPatrolState>(patrol);
        // Sequential: the path goes round with no end (Oakveil); Sequential_Once: it ends at the last stop.
        bool loop = gp.PatrolType.ToString() == "Sequential";
        return new Progress((int)gp.CurrentBusStopIndex, (int)gp.StartBusStopIndex, StopCount(em, patrol), loop);
    }

    // "stop 3 of 7" for chat, empty if unknown.
    public static string StopText(Progress p) =>
        p == null ? "" : p.Count > 0 ? L.T("stops.of", p.Number, p.Count) : L.T("stops.at", p.Number);

    static Type _stopType;
    static bool _stopTypeLooked;
    static System.Reflection.MethodInfo _getBuffer;
    static readonly Dictionary<(int, int), int> StopCounts = new();

    // Number of stops of a patrol's path: length of its PatrolBusStopNode buffer. The path doesn't
    // change, so it is read once per patrol. The type is looked up by name, as its namespace may differ.
    static int StopCount(EntityManager em, Entity patrol)
    {
        var key = (patrol.Index, patrol.Version);
        if (StopCounts.TryGetValue(key, out int n)) return n;
        n = 0;
        try
        {
            if (!_stopTypeLooked)
            {
                _stopType = GameTypes.Find("PatrolBusStopNode");
                _stopTypeLooked = true;
                if (_stopType == null) Plugin.Warn("CarriageTracker: game type PatrolBusStopNode not found, stop counts unknown");
            }
            if (_stopType != null && em.HasComponent(patrol, ComponentType.ReadOnly(Il2CppType.From(_stopType))))
            {
                if (_getBuffer == null)
                    foreach (var m in typeof(EntityManager).GetMethods())
                        if (m.Name == "GetBuffer" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2) { _getBuffer = m; break; }
                if (_getBuffer != null)
                {
                    object buffer = _getBuffer.MakeGenericMethod(_stopType).Invoke(em, new object[] { patrol, true });
                    n = (int)buffer.GetType().GetProperty("Length").GetValue(buffer);
                }
            }
        }
        catch (Exception e)
        {
            var inner = e.InnerException ?? e;
            Plugin.Warn($"CarriageTracker: stops of patrol {patrol.Index}:{patrol.Version} unknown: {inner.Message}");
        }
        StopCounts[key] = n;
        return n;
    }

    // Everything decisions about a route need (RouteLogic), read from the game in one place.
    public static RouteSnap Snap(EntityManager em, Entity patrol, string key)
    {
        var units = ActiveUnits(em, patrol);
        int onMap = 0;
        foreach (var u in units) if (Caravans.OnMap(Caravans.PositionOf(em, u))) onMap++;
        int carts = units.Count > 0 ? CartsOf(em, units).Count : 0;
        string spawner = Caravans.Has<UnitCompositionSpawner>(em, patrol)
            ? em.GetComponentData<UnitCompositionSpawner>(patrol).SpawnerState.ToString() : "?";
        return new RouteSnap(key, MoveStatus(em, patrol), spawner, StopsOf(em, patrol), units.Count, onMap, carts,
                             Starter.IsRunning(patrol), key != null && Starter.RecentlyFailed(patrol), $"{patrol.Index}:{patrol.Version}");
    }

    public static RouteSnap Snap(EntityManager em, Route r) => Snap(em, r.Patrol, r.Key);

    // State of a route for .cv routes and .cv ss.
    public static string Describe(EntityManager em, Entity patrol)
    {
        var s = Snap(em, patrol, null);
        return RouteLogic.Classify(s with { Running = false, FailedRecently = false }) switch
        {
            RouteState.AtEnd => s.Units > 0 ? L.T("route.state.endguarded") : L.T("route.state.endempty"),
            // The cart was destroyed: the horse (and maybe some guards) walk on to the end of the route.
            RouteState.CartLost or RouteState.FrozenLeftover => L.T("route.state.nocart"),
            RouteState.LoopLeftover => L.T("route.state.loopleft"),
            _ when s.WaitingForSpawn => L.T("route.state.waiting"),
            _ when s.Units > 0 && s.Move == "Moving" => L.T("route.state.moving"),
            _ => L.T("route.state.other", s.Spawner, s.Move, s.Units),
        };
    }
}
