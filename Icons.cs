using System;
using System.Collections.Generic;
using HarmonyLib;
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

static class Icons
{
    // Since the server start, for the memory line (Tracker.Memory): made, removed (and why), and as seen at the last
    // check: the icons kept on caravans (Live) and all icons of the mod's kinds on caravans or left without their
    // object (InWorld; the game's own merchant icons on its markers aren't counted, audit 07.11 R4).
    internal static long Made, Dropped;
    internal static readonly Dictionary<DropWhy, long> DroppedBy = new();
    internal static int Live, InWorld;

    internal static void Count(Dictionary<DropWhy, long> by, DropWhy why) => by[why] = (by.TryGetValue(why, out long n) ? n : 0) + 1;

    // Icon kinds as the mod sees them: (current, earlier = OldIcons without the current one, named presets).
    internal static (int Current, HashSet<int> Earlier, HashSet<int> Presets) Kinds()
    {
        int current = Settings.IconGuid;
        var earlier = Ours();
        earlier.Remove(current);
        return (current, earlier, new HashSet<int>(Settings.IconPresets.Values));
    }

    internal static IconKind KindOf(int guid, (int Current, HashSet<int> Earlier, HashSet<int> Presets) k) =>
        IconLogic.KindOf(guid, k.Current, k.Earlier, k.Presets);

    // Icons the mod treats as its own: the current one and all used before.
    public static HashSet<int> Ours()
    {
        var set = new HashSet<int> { Settings.IconGuid };
        foreach (var s in Settings.OldIcons.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(s, out int g)) set.Add(g);
        return set;
    }

    // Removes the "discover first" requirement and lets every player see the icon on the whole map
    // (the same way KindredPortals does it). Returns true if something had to be changed.
    internal static bool MakeGlobal(EntityManager em, Entity icon)
    {
        if (!Settings.GlobalIcons.Value || !Caravans.Has<MapIconData>(em, icon)) return false;
        var data = em.GetComponentData<MapIconData>(icon);
        if (!data.RequiresReveal && data.AllySetting == MapIconShowSettings.Global && data.EnemySetting == MapIconShowSettings.Global)
            return false;
        data.RequiresReveal = false;
        data.AllySetting = MapIconShowSettings.Global;
        data.EnemySetting = MapIconShowSettings.Global;
        em.SetComponentData(icon, data);
        return true;
    }

    // Removes "show only to some players" restrictions.
    internal static void Unrestrict(EntityManager em, Entity e)
    {
        if (Caravans.Has<SyncToUserBitMask>(em, e))
            em.RemoveComponent(e, Ct<SyncToUserBitMask>.RW);
        if (em.HasBuffer<SyncToUserBuffer>(e)) em.GetBuffer<SyncToUserBuffer>(e).Clear();
        if (Caravans.Has<OnlySyncToUsersTag>(em, e))
            em.RemoveComponent(e, Ct<OnlySyncToUsersTag>.RW);
    }

    public static (int Created, int Removed, int Globalized) Sync(EntityManager em, PrefabCollectionSystem prefabs, List<Caravans.Caravan> caravans)
    {
        var kinds = Kinds();
        int iconGuid = kinds.Current;
        var wanted = new Dictionary<(int, int), Entity>();
        foreach (var c in caravans) wanted[(c.Target.Index, c.Target.Version)] = c.Target;

        // Which icons are kept, removed or left alone: IconLogic.ForIcon. Icons on marker objects (stale icons on
        // markers that are gone) are swept by Proxies.SweepOrphans.
        var covered = new HashSet<(int, int)>();
        var drop = new List<(Entity Icon, DropWhy Why)>();
        var now = DateTime.UtcNow;
        Doomed.Prune(em, prefabs, now);
        int removed = 0, globalized = 0, inWorld = 0;
        var query = Queries.Get(em, "MapIconTargetEntity+PrefabGUID", false, Ct<MapIconTargetEntity>.RO, Ct<PrefabGUID>.RO);
        var icons = query.ToEntityArray(Allocator.Temp);
        try
        {
            foreach (var icon in icons)
            {
                if (Doomed.IsDying(icon)) continue;
                var kind = KindOf(em.GetComponentData<PrefabGUID>(icon).GuidHash, kinds);
                if (kind == IconKind.Other) continue;
                var t = em.GetComponentData<MapIconTargetEntity>(icon).TargetEntity.GetEntityOnServer();
                bool exists = em.Exists(t);
                bool caravanTarget = exists && Caravans.IsCaravanEntity(em, prefabs, t);
                bool onMarker = !exists && Caravans.Has<Attach>(em, icon);
                if (caravanTarget || (!exists && !onMarker)) inWorld++;
                var key = (t.Index, t.Version);
                var (act, why) = IconLogic.ForIcon(new IconFacts(kind, exists, caravanTarget, onMarker,
                                                                  caravanTarget && wanted.ContainsKey(key), covered.Contains(key)));
                if (act == IconAct.Ignore) continue;
                if (act == IconAct.Keep)
                {
                    covered.Add(key);
                    if (MakeGlobal(em, icon)) globalized++;
                    continue;
                }
                drop.Add((icon, why));
            }
        }
        finally
        {
            icons.Dispose();
        }
        foreach (var (icon, why) in drop)
        {
            Doomed.Kill(em, icon, now);
            Count(DroppedBy, why);
        }
        removed = drop.Count;
        Dropped += removed;

        InWorld = inWorld - removed;
        int created = 0;
        if (!prefabs._PrefabGuidToEntityMap.TryGetValue(new PrefabGUID(iconGuid), out Entity iconPrefab))
        {
            Plugin.Warn($"CarriageTracker: icon prefab {iconGuid} not found, no icons created");
            return (0, removed, globalized);
        }
        foreach (var kv in wanted)
        {
            if (covered.Contains(kv.Key)) continue;
            var target = kv.Value;
            var icon = em.Instantiate(iconPrefab);
            if (!Caravans.Has<MapIconTargetEntity>(em, icon))
                em.AddComponent(icon, Ct<MapIconTargetEntity>.RW);
            em.SetComponentData(icon, new MapIconTargetEntity { TargetEntity = target, TargetNetworkId = em.GetComponentData<NetworkId>(target) });
            Unrestrict(em, icon);
            if (MakeGlobal(em, icon)) globalized++;
            created++;
            Made++;
        }
        Live = covered.Count + created;
        InWorld += created;
        return (created, removed, globalized);
    }
}

// Proxy mode: the icon hangs not on the caravan itself but on a separate map marker object
// (MapIcon_ProxyObject_POI_Unknown), like KindredPortals does. The game sends such markers to players
// regardless of distance. Also used for frozen caravans in direct mode.
// A marker is put where its caravan is and made anew when the caravan has moved away (MovedFar). Until 1.0.16 that
// was every 10 m, so a frozen caravan whose route goes on got a new marker at almost every check, and the old ones
// were destroyed past the game's destroy pipeline. On 05.11 players saw rows of stale carriage icons along a road
// and the server was killed for memory. Now markers are destroyed through DestroyUtility, a frozen caravan's marker
// is made anew only after 100 m and at most every 3 minutes, and icons of the mod's kinds left on a marker that no
// longer exists are removed at every check (SweepOrphans).
// Other mods make the same markers (KindredPortals: SpawnedBy = a player's character, an icon of the player's
// choice), so only markers of this mod are touched (Owned).
static class Proxies
{
    internal const int ProxyGuid = 636813227; // MapIcon_ProxyObject_POI_Unknown
    static (int, int) Key(Entity e) => (e.Index, e.Version);

    // Markers this mod made since the server start (when), and of them those whose icon the game hasn't made yet
    // (it is made global as soon as it appears, see GlobalizeNewIcons).
    static readonly Dictionary<(int, int), DateTime> MadeAt = new();
    static readonly HashSet<(int, int)> Pending = new();
    static DateTime _pendingUntil;

    // For the memory line (Memory): since the server start (removed markers also by why), and as seen at the last
    // check: Live = the mod's markers kept or made, InWorld = all markers of this kind
    // (KindredPortals' too).
    internal static long Made, Dropped, Orphans;
    internal static readonly Dictionary<DropWhy, long> DroppedBy = new();
    internal static int Live, InWorld;

    internal static bool HasPending => Pending.Count > 0;

    // One icon requested, of one of the given kinds.
    static bool RequestsOneOf(EntityManager em, Entity p, HashSet<int> kinds)
    {
        if (!em.HasBuffer<AttachMapIconsToEntity>(p)) return false;
        var buf = em.GetBuffer<AttachMapIconsToEntity>(p);
        return buf.Length == 1 && kinds.Contains(buf[0].Prefab.GuidHash);
    }

    // One icon attached, of one of the given kinds (in case the game empties the request once the icon is made).
    static bool CarriesOneOf(EntityManager em, Entity p, HashSet<int> kinds)
    {
        var attached = Attached(em, p);
        return attached.Count == 1 && Caravans.Has<PrefabGUID>(em, attached[0])
               && kinds.Contains(em.GetComponentData<PrefabGUID>(attached[0]).GuidHash);
    }

    // What a marker's SpawnedBy points to.
    static Spawner SpawnerOf(EntityManager em, PrefabCollectionSystem prefabs, Entity t) =>
        t == Entity.Null ? Spawner.None : !em.Exists(t) ? Spawner.Gone : Caravans.IsCaravanEntity(em, prefabs, t) ? Spawner.Caravan : Spawner.Other;

    // The facts IconLogic decides a marker by, except those about its caravan (filled in by Sync).
    static MarkerFacts FactsOf(EntityManager em, PrefabCollectionSystem prefabs, Entity p, HashSet<int> kinds)
    {
        bool made = MadeAt.ContainsKey(Key(p));
        bool oneOfOurs = !made && (RequestsOneOf(em, p, kinds) || CarriesOneOf(em, p, kinds));
        return new MarkerFacts(made, oneOfOurs, SpawnerOf(em, prefabs, em.GetComponentData<SpawnedBy>(p).Value), false, false, false, false);
    }

    // The mod's kinds of icons for markers: current, earlier and named presets.
    static HashSet<int> MarkerKinds()
    {
        var kinds = Icons.Ours();
        kinds.UnionWith(Settings.IconPresets.Values);
        return kinds;
    }

    // Whether a marker is the mod's (IconLogic.Owned), for .cvdiag markers.
    internal static bool IsMine(EntityManager em, PrefabCollectionSystem prefabs, Entity p) =>
        IconLogic.Owned(FactsOf(em, prefabs, p, MarkerKinds()));

    // The icon kind a marker requests, or failing that the kind of the one it carries; 0 if none or several.
    internal static int IconOf(EntityManager em, Entity p)
    {
        if (em.HasBuffer<AttachMapIconsToEntity>(p))
        {
            var buf = em.GetBuffer<AttachMapIconsToEntity>(p);
            if (buf.Length == 1) return buf[0].Prefab.GuidHash;
        }
        var attached = Attached(em, p);
        return attached.Count == 1 && Caravans.Has<PrefabGUID>(em, attached[0]) ? em.GetComponentData<PrefabGUID>(attached[0]).GuidHash : 0;
    }

    // .cvdiag movemarker: a marker moved by hand counts as just made, so a frozen caravan's marker isn't made anew
    // for 3 minutes (IconLogic.MovedFar) and stays where it was put while the map is looked at.
    internal static void Moved(EntityManager em, Entity p, float3 pos)
    {
        SetPos(em, p, pos);
        MadeAt[Key(p)] = DateTime.UtcNow;
    }

    // Called from the patch before MapIconSpawnSystem while new icons on our markers are expected.
    // Each marker is done once its icon has been seen; with GlobalIcons off nothing is waited for.
    internal static void GlobalizeNewIcons()
    {
        if (DateTime.UtcNow > _pendingUntil || !Settings.GlobalIcons.Value) { Pending.Clear(); return; }
        var world = Caravans.ServerWorld();
        if (world == null) return;
        var em = world.EntityManager;
        var query = Queries.Get(em, "MapIconData+Attach all", true, Ct<MapIconData>.RO, Ct<Attach>.RO);
        var icons = query.ToEntityArray(Allocator.Temp);
        try
        {
            foreach (var icon in icons)
            {
                var parent = Key(em.GetComponentData<Attach>(icon).Parent);
                if (!Pending.Contains(parent)) continue;
                Icons.MakeGlobal(em, icon);
                Pending.Remove(parent);
                if (Pending.Count == 0) break;
            }
        }
        finally
        {
            icons.Dispose();
        }
    }

    static void SetPos(EntityManager em, Entity e, float3 pos)
    {
        if (Caravans.Has<Translation>(em, e)) em.SetComponentData(e, new Translation { Value = pos });
        if (Caravans.Has<LocalToWorld>(em, e)) em.SetComponentData(e, new LocalToWorld { Value = float4x4.Translate(pos) });
    }

    static List<Entity> Attached(EntityManager em, Entity p)
    {
        var list = new List<Entity>();
        if (!em.HasBuffer<AttachedBuffer>(p)) return list;
        var buf = em.GetBuffer<AttachedBuffer>(p);
        for (int i = 0; i < buf.Length; i++)
        {
            var e = buf[i].Entity;
            if (e != Entity.Null && em.Exists(e)) list.Add(e);
        }
        return list;
    }

    static bool IsDying(Entity e) => Doomed.IsDying(e);
    static void Kill(EntityManager em, Entity e, DateTime now) => Doomed.Kill(em, e, now);

    // Destroys a marker together with its icons, through the game's destroy pipeline (DestroyUtility), as the mod
    // removes units and carts: markers are networked objects, and the game says itself (server log 05.11) that
    // objects must be destroyed only there. Returns the number of removed icons.
    static int Destroy(EntityManager em, Entity p, DateTime now, DropWhy why)
    {
        var attached = Attached(em, p);
        foreach (var e in attached) Kill(em, e, now);
        MadeAt.Remove(Key(p));
        Pending.Remove(Key(p));
        Kill(em, p, now);
        Dropped++;
        Icons.Count(DroppedBy, why);
        return attached.Count;
    }

    // Icons of the mod's kinds hanging on a marker that no longer exists: left over when a marker was destroyed
    // without its icons (until 1.0.16, or if the game hadn't filled the marker's list of icons yet). Nothing else
    // would ever remove them, and with GlobalIcons every player sees them. Only the mod's own kinds (Icons.Ours: the
    // current icon and OldIcons) are swept; a game icon of such a kind on a parent that is gone is a leftover too.
    static int SweepOrphans(EntityManager em, (int Current, HashSet<int> Earlier, HashSet<int> Presets) kinds, DateTime now)
    {
        int swept = 0;
        var query = Queries.Get(em, "MapIconData+Attach+PrefabGUID all", true, Ct<MapIconData>.RO, Ct<Attach>.RO, Ct<PrefabGUID>.RO);
        var icons = query.ToEntityArray(Allocator.Temp);
        var orphans = new List<Entity>();
        try
        {
            foreach (var icon in icons)
            {
                if (IsDying(icon)) continue;
                var kind = Icons.KindOf(em.GetComponentData<PrefabGUID>(icon).GuidHash, kinds);
                if (kind == IconKind.Other) continue;
                var parent = em.GetComponentData<Attach>(icon).Parent;
                if (IconLogic.Orphan(kind, parent != Entity.Null && em.Exists(parent))) orphans.Add(icon);
            }
        }
        finally
        {
            icons.Dispose();
        }
        foreach (var icon in orphans) { Kill(em, icon, now); swept++; }
        Orphans += swept;
        return swept;
    }

    public static (int Created, int Removed, int Globalized) Sync(EntityManager em, PrefabCollectionSystem prefabs, List<Caravans.Caravan> caravans)
    {
        var now = DateTime.UtcNow;
        Doomed.Prune(em, prefabs, now);
        int iconGuid = Settings.IconGuid;
        var current = new HashSet<int> { iconGuid };
        var kinds = MarkerKinds();
        var wanted = new Dictionary<(int, int), Caravans.Caravan>();
        foreach (var c in caravans) wanted[(c.Target.Index, c.Target.Version)] = c;
        var covered = new HashSet<(int, int)>();
        var present = new HashSet<(int, int)>();
        var drop = new List<(Entity Marker, DropWhy Why)>();
        int created = 0, removed = 0, globalized = 0, inWorld = 0;

        // The mod's markers (IconLogic.Owned) are kept or removed by IconLogic.ForMarker; markers of other mods are
        // left alone.
        var query = Queries.Get(em, "PrefabGUID+SpawnedBy+AttachMapIconsToEntity all", true, Ct<PrefabGUID>.RO, Ct<SpawnedBy>.RO, Ct<AttachMapIconsToEntity>.RO);
        var proxies = query.ToEntityArray(Allocator.Temp);
        try
        {
            foreach (var p in proxies)
            {
                if (em.GetComponentData<PrefabGUID>(p).GuidHash != ProxyGuid) continue;
                present.Add(Key(p));
                if (IsDying(p)) continue;
                inWorld++;
                var facts = FactsOf(em, prefabs, p, kinds);
                if (!IconLogic.Owned(facts)) continue;
                var t = em.GetComponentData<SpawnedBy>(p).Value;
                var key = (t.Index, t.Version);
                Caravans.Caravan c = null;
                bool isWanted = facts.Spawner == Spawner.Caravan && wanted.TryGetValue(key, out c);
                bool currentKind = RequestsOneOf(em, p, current) || CarriesOneOf(em, p, current);
                bool moved = false;
                if (isWanted && currentKind)
                {
                    var age = MadeAt.TryGetValue(Key(p), out var made) ? now - made : (TimeSpan?)null;
                    moved = IconLogic.MovedFar(c.Frozen, Caravans.Dist2(Caravans.PositionOf(em, p), c.Pos), age);
                }
                var (act, why) = IconLogic.ForMarker(facts with { Wanted = isWanted, CurrentKind = currentKind, Moved = moved, Covered = covered.Contains(key) });
                if (act == IconAct.Keep)
                {
                    covered.Add(key);
                    foreach (var icon in Attached(em, p))
                        if (Icons.MakeGlobal(em, icon)) globalized++;
                    continue;
                }
                drop.Add((p, why));
            }
        }
        finally
        {
            proxies.Dispose();
        }
        // Markers that are gone without the mod removing them (the game, another mod, an admin) are forgotten
        // (audit 07.11, R5: until 1.0.21 MadeAt kept them).
        foreach (var k in new List<(int, int)>(MadeAt.Keys))
            if (!present.Contains(k)) { MadeAt.Remove(k); Pending.Remove(k); }
        foreach (var (p, why) in drop) removed += Destroy(em, p, now, why);
        removed += SweepOrphans(em, Icons.Kinds(), now);

        if (!prefabs._PrefabGuidToEntityMap.TryGetValue(new PrefabGUID(ProxyGuid), out Entity proxyPrefab))
        {
            Plugin.Warn($"CarriageTracker: proxy prefab {ProxyGuid} not found, no proxy icons created");
            Live = covered.Count;
            InWorld = inWorld - drop.Count;
            return (0, removed, globalized);
        }
        foreach (var kv in wanted)
        {
            if (covered.Contains(kv.Key)) continue;
            var c = kv.Value;
            var proxy = em.Instantiate(proxyPrefab);
            if (!em.HasBuffer<AttachMapIconsToEntity>(proxy))
            {
                Kill(em, proxy, now);
                Plugin.Warn("CarriageTracker: proxy prefab has no AttachMapIconsToEntity, proxy mode can't work");
                break;
            }
            SetPos(em, proxy, c.Pos);
            if (!Caravans.Has<SpawnedBy>(em, proxy))
                em.AddComponent(proxy, Ct<SpawnedBy>.RW);
            em.SetComponentData(proxy, new SpawnedBy { Value = c.Target });
            Icons.Unrestrict(em, proxy);
            var buf = em.GetBuffer<AttachMapIconsToEntity>(proxy);
            buf.Clear();
            buf.Add(new AttachMapIconsToEntity { Prefab = new PrefabGUID(iconGuid) });
            MadeAt[Key(proxy)] = now;
            if (Settings.GlobalIcons.Value) Pending.Add(Key(proxy));
            created++;
            Made++;
        }
        if (created > 0 && Settings.GlobalIcons.Value) _pendingUntil = now.AddSeconds(10);
        Live = covered.Count + created;
        InWorld = inWorld - drop.Count + created;
        return (created, removed, globalized);
    }
}

// As in KindredPortals: before the game processes new icons, icons on our markers are made
// visible to everyone. Active only for a few frames after markers are created.
[HarmonyPatch(typeof(MapIconSpawnSystem), nameof(MapIconSpawnSystem.OnUpdate))]
static class MapIconSpawnPatch
{
    public static void Prefix()
    {
        if (!Proxies.HasPending) return;
        try { Proxies.GlobalizeNewIcons(); }
        catch (Exception e) { Plugin.Fail("CarriageTracker icons", e); }
    }
}

// Map icons and markers the mod handed to the game's destroy pipeline (DestroyUtility), for both icon kinds
// (Icons on caravans, Proxies' markers). The game removes them a frame or more later; until then they are skipped,
// so a check right after another one doesn't count, keep or destroy them again. Until 1.0.18 icons on caravans were
// removed with StatChangeUtility.KillOrDestroyEntity without this list (audit of 07.11).
// One the game hasn't removed is tried once more and then given up (DoomLogic); the memory line shows how many are
// waiting, stuck and given up, so objects the game never removes can't pile up unseen (audit 07.11 by Nikolai, R1:
// until 1.0.21 such an object got one warning line and was then skipped and counted nowhere).
static class Doomed
{
    sealed class Entry
    {
        public Entity E;
        public DateTime Since;
        public bool Retried, GivenUp;
    }

    static readonly Dictionary<(int, int), Entry> Queued = new();
    static (int, int) Key(Entity e) => (e.Index, e.Version);

    // Since the server start: second tries and objects given up.
    internal static long Retries, GaveUp;

    public static bool IsDying(Entity e) => Queued.ContainsKey(Key(e));

    public static void Kill(EntityManager em, Entity e, DateTime now)
    {
        if (e == Entity.Null || !em.Exists(e) || IsDying(e)) return;
        DestroyUtility.Destroy(em, e);
        Queued[Key(e)] = new Entry { E = e, Since = now };
    }

    // For the memory line: waiting now, of them older than a minute, of them given up.
    public static (int Waiting, int Stuck, int GivenUp) Counts(DateTime now)
    {
        int stuck = 0, givenUp = 0;
        foreach (var d in Queued.Values)
        {
            if (now - d.Since >= DoomLogic.Stuck) stuck++;
            if (d.GivenUp) givenUp++;
        }
        return (Queued.Count, stuck, givenUp);
    }

    // Forgets objects the game has removed; tries once more those it hasn't removed for 5 minutes; gives up those
    // still there 10 minutes after the first try, with one warning for all of them.
    public static void Prune(EntityManager em, PrefabCollectionSystem prefabs, DateTime now)
    {
        Dictionary<string, int> gaveUp = null;
        foreach (var k in new List<(int, int)>(Queued.Keys))
        {
            var d = Queued[k];
            if (!em.Exists(d.E)) { Queued.Remove(k); continue; }
            switch (DoomLogic.Step(now - d.Since, d.Retried, d.GivenUp))
            {
                case DoomStep.Retry:
                    d.Retried = true;
                    Retries++;
                    try { DestroyUtility.Destroy(em, d.E); }
                    catch (Exception e) { Plugin.Trace($"CarriageTracker: second destroy of {d.E.Index}:{d.E.Version} failed: {e.Message}"); }
                    break;
                case DoomStep.GiveUp:
                    d.GivenUp = true;
                    GaveUp++;
                    string what = Caravans.Has<PrefabGUID>(em, d.E) && prefabs._PrefabLookupMap.TryGetName(em.GetComponentData<PrefabGUID>(d.E), out string n) ? n : "object";
                    gaveUp ??= new Dictionary<string, int>();
                    gaveUp[what] = (gaveUp.TryGetValue(what, out int c) ? c : 0) + 1;
                    break;
            }
        }
        if (gaveUp == null) return;
        var parts = new List<string>();
        int total = 0;
        foreach (var kv in gaveUp) { parts.Add($"{kv.Key} x{kv.Value}"); total += kv.Value; }
        Plugin.Warn($"CarriageTracker: {total} objects the mod destroyed are still in the world {DoomLogic.GiveUpAfter.TotalMinutes} min later, " +
                    $"also after a second try: {string.Join(", ", parts)}. They are left alone and counted in the memory line as given up.");
    }
}
