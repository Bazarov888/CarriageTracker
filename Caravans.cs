using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using ProjectM;
using ProjectM.Network;
using Stunlock.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace CarriageTracker;

public enum Kind { None, Container, Cart, Horse }

// Finding caravans in the world. A caravan is a cart (CHAR_Carriage_*) pulled by a horse
// (CHAR_CarriageHorse_*); after the cart is destroyed its loot stays in a container (TM_CarriageContainer_*).
// The part after the prefix is the route key, e.g. MilitiaIron.
static class Caravans
{
    const string ContainerPrefix = "TM_CarriageContainer_";
    const string HorsePrefix = "CHAR_CarriageHorse_";
    const string CartPrefix = "CHAR_Carriage_";

    // Cache: prefab id -> kind and route key. The name is looked up once per prefab.
    static readonly Dictionary<int, (Kind Kind, string Route)> Kinds = new();

    // Frozen: the cart is frozen (no players nearby), Pos is where its patrol stands.
    // Status: state of the cart's patrol from the game (e.g. Moving, WaitForDespawn), carts only.
    // Patrol and Stops: the cart's patrol and how far along its path it is (carts only).
    // Orphan: a cart on the map that no animal pulls any more (its animal is gone, frozen elsewhere or far away).
    public record Found(Kind Kind, string Route, Entity Entity, float3 Pos, bool Empty, bool Frozen, string Status,
                        Entity Patrol, Progress Stops, bool Orphan);
    // Icon: whether to put an icon on the map. Frozen at the end of the route get none: they vanish when approached.
    // State: short English state for the server log.
    public record Caravan(string Route, Entity Target, float3 Pos, string Note, bool Frozen, bool Icon,
                          Entity Patrol, Progress Stops, string State);

    // The server world and its prefab system are looked up once and kept while the world exists (audit 07.11, R3):
    // until 1.0.21 every call went through World.s_AllWorlds and read each world's name, every frame while a start
    // runs, and each time made new IL2CPP wrappers and strings. A world that is gone drops the cache and the queries
    // made in it (Queries).
    static World _world;
    static PrefabCollectionSystem _prefabs;

    public static World ServerWorld()
    {
        var cached = _world;
        if (cached != null && cached.IsCreated) return cached;
        if (cached != null || _prefabs != null) Queries.Reset();
        _world = null;
        _prefabs = null;
        if (World.s_AllWorlds == null) return null;
        foreach (var w in World.s_AllWorlds)
            if (w.Name == "Server" && w.IsCreated)
            {
                Queries.Reset();
                _world = w;
                return w;
            }
        return null;
    }

    public static PrefabCollectionSystem Prefabs(World world)
    {
        if (_prefabs != null && ReferenceEquals(world, _world)) return _prefabs;
        var p = world.GetExistingSystemManaged<PrefabCollectionSystem>();
        if (p == null || !p.GameDataInitialized) return null;
        if (ReferenceEquals(world, _world)) _prefabs = p;
        return p;
    }

    public static bool Has<T>(EntityManager em, Entity e) => em.HasComponent(e, Ct<T>.RO);

    public static (Kind Kind, string Route) Classify(PrefabCollectionSystem prefabs, PrefabGUID guid)
    {
        if (Kinds.TryGetValue(guid.GuidHash, out var known)) return known;
        (Kind, string) result = (Kind.None, null);
        if (prefabs._PrefabLookupMap.TryGetName(guid, out string name) && !string.IsNullOrEmpty(name))
        {
            if (name.StartsWith(ContainerPrefix, StringComparison.Ordinal)) result = (Kind.Container, name.Substring(ContainerPrefix.Length));
            else if (name.StartsWith(HorsePrefix, StringComparison.Ordinal)) result = (Kind.Horse, name.Substring(HorsePrefix.Length));
            else if (name.StartsWith(CartPrefix, StringComparison.Ordinal)) result = (Kind.Cart, name.Substring(CartPrefix.Length));
        }
        Kinds[guid.GuidHash] = result;
        return result;
    }

    public static bool IsCaravanEntity(EntityManager em, PrefabCollectionSystem prefabs, Entity e) =>
        Has<PrefabGUID>(em, e) && Classify(prefabs, em.GetComponentData<PrefabGUID>(e)).Kind != Kind.None;

    // A container is empty if none of its inventory slots holds an item.
    // If the inventory can't be read, the container counts as full, so the caravan isn't lost.
    internal static bool IsEmpty(EntityManager em, Entity container)
    {
        if (!InventoryUtilities.TryGetInventoryEntity(em, container, out Entity inventory)) return false;
        if (!em.HasBuffer<InventoryBuffer>(inventory)) return false;
        var slots = em.GetBuffer<InventoryBuffer>(inventory);
        for (int i = 0; i < slots.Length; i++)
            if (slots[i].ItemType.GuidHash != 0 && slots[i].Amount > 0) return false;
        return true;
    }

    // Goes through networked objects that have the marker component (or all networked objects if there is no marker).
    static void Collect(EntityManager em, PrefabCollectionSystem prefabs, string name, ComponentType? marker,
                        List<Found> found, HashSet<(int, int)> seen, ref int scanned)
    {
        var query = marker.HasValue
            ? Queries.Get(em, "scan " + name, true, Ct<PrefabGUID>.RO, Ct<NetworkId>.RO, marker.Value)
            : Queries.Get(em, "scan " + name, true, Ct<PrefabGUID>.RO, Ct<NetworkId>.RO);
        var entities = query.ToEntityArray(Allocator.Temp);
        try
        {
            foreach (var e in entities)
            {
                scanned++;
                var info = Classify(prefabs, em.GetComponentData<PrefabGUID>(e));
                if (info.Kind == Kind.None || !seen.Add((e.Index, e.Version))) continue;
                found.Add(Describe(em, e, info));
            }
        }
        finally
        {
            entities.Dispose();
        }
    }

    static Found Describe(EntityManager em, Entity e, (Kind Kind, string Route) info)
    {
        bool empty = info.Kind == Kind.Container && IsEmpty(em, e);
        var pos = PositionOf(em, e);
        bool frozen = false;
        string status = null;
        Entity patrol = Entity.Null;
        Progress stops = null;
        if (info.Kind == Kind.Cart && TryGetPatrol(em, e, out patrol))
        {
            stops = Routes.StopsOf(em, patrol);
            status = Routes.MoveStatus(em, patrol);
            // With no players nearby the game freezes the caravan and moves the cart to (10000, 10000).
            // Its real place is where the patrol stands that the horse follows.
            var patrolPos = PositionOf(em, patrol);
            if (!OnMap(pos) && OnMap(patrolPos))
            {
                pos = patrolPos;
                frozen = true;
            }
        }
        bool orphan = info.Kind == Kind.Cart && !frozen && OnMap(pos) && !Pulled(em, e, pos, status);
        return new Found(info.Kind, info.Route, e, pos, empty, frozen, status, patrol, stops, orphan);
    }

    // One object looked at again (one the fast search missed before). null if it no longer exists or isn't a
    // caravan object.
    public static Found Recheck(EntityManager em, PrefabCollectionSystem prefabs, Entity e)
    {
        if (e == Entity.Null || !em.Exists(e) || !Has<PrefabGUID>(em, e)) return null;
        var info = Classify(prefabs, em.GetComponentData<PrefabGUID>(e));
        return info.Kind == Kind.None ? null : Describe(em, e, info);
    }

    // Fast search: moving carts by their component, standing containers among objects with an inventory.
    public static List<Found> FastScan(EntityManager em, PrefabCollectionSystem prefabs, out int scanned)
    {
        scanned = 0;
        var found = new List<Found>();
        var seen = new HashSet<(int, int)>();
        Collect(em, prefabs, "carts", Ct<ProjectM.Shared.Script_CarriageData>.RO, found, seen, ref scanned);
        Collect(em, prefabs, "inventories", Ct<InventoryInstanceElement>.RO, found, seen, ref scanned);
        return found;
    }

    // Full pass over all networked objects of the world, about 0.2 s. Safety net for the fast search.
    public static List<Found> FullScan(EntityManager em, PrefabCollectionSystem prefabs, out int scanned)
    {
        scanned = 0;
        var found = new List<Found>();
        Collect(em, prefabs, "all", null, found, new HashSet<(int, int)>(), ref scanned);
        return found;
    }

    // Whether a cart on the map is still pulled: its animal exists, stands on the map and is close to it.
    // A cart left behind (e.g. at the end of the route while its animal froze elsewhere) isn't a caravan.
    static bool Pulled(EntityManager em, Entity cart, float3 cartPos, string move)
    {
        if (!Has<ProjectM.Shared.Script_CarriageData>(em, cart)) return false;
        var horse = em.GetComponentData<ProjectM.Shared.Script_CarriageData>(cart).Horse.GetEntityOnServer();
        bool exists = horse != Entity.Null && em.Exists(horse);
        var horsePos = exists ? PositionOf(em, horse) : default;
        return CaravanRules.Pulled(exists, exists && OnMap(horsePos), exists ? Dist2(horsePos, cartPos) : float.MaxValue, move);
    }

    // Which found objects are caravans: CaravanRules. Which caravans get a map icon is set by IconOnLoot
    // and IconOnFrozen.
    public static List<Caravan> Build(List<Found> found)
    {
        var containers = found.FindAll(f => f.Kind == Kind.Container && CaravanRules.ShowContainer(f.Empty, OnMap(f.Pos)));
        var carts = found.FindAll(f => f.Kind == Kind.Cart);
        var result = new List<Caravan>();
        bool lootIcon = Settings.IconOnLoot.Value;
        bool frozenIcon = Settings.IconOnFrozen.Value;
        foreach (var c in containers)
            result.Add(new Caravan(c.Route, c.Entity, c.Pos, L.T("note.loot"), false, lootIcon, Entity.Null, null, "loot"));
        foreach (var cart in carts)
        {
            bool lootInPlace = containers.Exists(c => c.Route == cart.Route && Dist2(c.Pos, cart.Pos) < 15);
            if (!CaravanRules.ShowCart(OnMap(cart.Pos), lootInPlace, cart.Status, cart.Orphan)) continue;
            string stops = Routes.StopText(cart.Stops);
            string stopNote = stops.Length > 0 ? ", " + stops : "";
            string stopLog = cart.Stops != null ? $" stop {cart.Stops.Log}" : "";
            if (!cart.Frozen)
                result.Add(new Caravan(cart.Route, cart.Entity, cart.Pos, L.T("note.moving", stopNote), false, true,
                                       cart.Patrol, cart.Stops, "moving" + stopLog));
            else
                result.Add(new Caravan(cart.Route, cart.Entity, cart.Pos, L.T("note.frozen", cart.Status, stopNote), true, frozenIcon,
                                       cart.Patrol, cart.Stops, $"frozen ({cart.Status})" + stopLog));
        }
        return result;
    }

    // Reads a game component by type name when its namespace isn't known in advance:
    // the type is looked up at runtime and the data is copied from memory.
    static readonly Dictionary<string, (Type Type, ComponentType Ct)> RawTypes = new();

    internal static unsafe object ReadRaw(EntityManager em, Entity e, string typeName)
    {
        if (!RawTypes.TryGetValue(typeName, out var info))
        {
            var t = GameTypes.Find(typeName);
            info = (t, t == null ? default : ComponentType.ReadOnly(Il2CppType.From(t)));
            RawTypes[typeName] = info;
            if (t == null) Plugin.Warn($"CarriageTracker: game type {typeName} not found");
        }
        if (info.Type == null || e == Entity.Null || !em.Exists(e) || !em.HasComponent(e, info.Ct)) return null;
        void* ptr = em.GetComponentDataRawRO(e, info.Ct.TypeIndex);
        return ptr == null ? null : Marshal.PtrToStructure(new IntPtr(ptr), info.Type);
    }

    internal static object Field(object boxed, string name) => boxed?.GetType().GetField(name)?.GetValue(boxed);

    // Patrol of a caravan: cart -> its horse (Script_CarriageData.Horse) -> who spawned the horse (SpawnedBy).
    internal static bool TryGetPatrol(EntityManager em, Entity cart, out Entity patrol)
    {
        patrol = Entity.Null;
        if (!Has<ProjectM.Shared.Script_CarriageData>(em, cart)) return false;
        var horse = em.GetComponentData<ProjectM.Shared.Script_CarriageData>(cart).Horse.GetEntityOnServer();
        if (horse == Entity.Null || !em.Exists(horse) || !Has<SpawnedBy>(em, horse)) return false;
        patrol = em.GetComponentData<SpawnedBy>(horse).Value;
        return patrol != Entity.Null && em.Exists(patrol);
    }

    // The map of Vardoran is roughly -3200..3200 on x and z. Farther out the game parks spare carts.
    public static bool OnMap(float3 p) => Math.Abs(p.x) <= 4000 && Math.Abs(p.z) <= 4000;

    public static float3 PositionOf(EntityManager em, Entity entity)
    {
        if (Has<LocalToWorld>(em, entity)) return em.GetComponentData<LocalToWorld>(entity).Position;
        if (Has<Translation>(em, entity)) return em.GetComponentData<Translation>(entity).Value;
        return float3.zero;
    }

    public static float Dist2(float3 a, float3 b) => math.distance(new float2(a.x, a.z), new float2(b.x, b.z));

    public static string Direction(float dx, float dz)
    {
        double angle = (Math.Atan2(dx, dz) * 180 / Math.PI + 360) % 360;
        return L.T("dir." + ((int)Math.Round(angle / 45) % 8));
    }
}

// Component types by C# type, made once per type (audit 07.11, R3): Il2CppType.Of<T>() makes a new wrapper with a
// GCHandle on every call, which only the .NET finalizer frees, and the mod called it on every component check.
static class Ct<T>
{
    public static readonly ComponentType RO = ComponentType.ReadOnly(Il2CppType.Of<T>());
    public static readonly ComponentType RW = ComponentType.ReadWrite(Il2CppType.Of<T>());
}

// Entity queries the mod runs at every check, made once in the server world instead of at every check (audit 07.11,
// R3). They belong to the world: never disposed by the mod, dropped when the world is gone (Caravans.ServerWorld)
// and made again in the new one. Only the arrays read from them are disposed after use.
static class Queries
{
    static readonly Dictionary<string, EntityQuery> Made = new();

    public static void Reset() => Made.Clear();

    public static EntityQuery Get(EntityManager em, string name, bool includeDisabled, params ComponentType[] all)
    {
        if (Made.TryGetValue(name, out var query)) return query;
        var builder = new EntityQueryBuilder(Allocator.Temp);
        foreach (var c in all) builder = builder.AddAll(c);
        if (includeDisabled) builder = builder.WithOptions(EntityQueryOptions.IncludeDisabled);
        query = em.CreateEntityQuery(ref builder);
        builder.Dispose();
        Made[name] = query;
        return query;
    }
}

// Finds the C# wrapper of a game type by its short name when the namespace isn't known in advance.
static class GameTypes
{
    static readonly Dictionary<string, Type> Cache = new();

    public static Type Find(string simpleName)
    {
        if (Cache.TryGetValue(simpleName, out var cached)) return cached;
        Type found = null, fallback = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            string asmName = asm.GetName().Name ?? "";
            if (!asmName.StartsWith("ProjectM", StringComparison.Ordinal) && !asmName.StartsWith("Stunlock", StringComparison.Ordinal)) continue;
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types; }
            catch { continue; }
            foreach (var t in types)
            {
                if (t == null || t.Name != simpleName || !t.IsValueType) continue;
                if (t.Namespace != null && t.Namespace.StartsWith("ProjectM", StringComparison.Ordinal)) { found = t; break; }
                fallback ??= t;
            }
            if (found != null) break;
        }
        found ??= fallback;
        Cache[simpleName] = found;
        return found;
    }
}
