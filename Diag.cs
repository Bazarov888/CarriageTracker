using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;
using ProjectM;
using ProjectM.Network;
using ProjectM.Shared;
using Stunlock.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using VampireCommandFramework;

namespace CarriageTracker;

// Developer tools: dumps of caravan objects and patrols. Registered only with EnableDiagnostics = true.
[CommandGroup("cvdiag")]
public class DiagCommands
{
    [Command("comps", adminOnly: true, usage: "[radius]", description: "Components of the nearest caravan object")]
    public void Comps(ChatCommandContext ctx, int radius = 10)
    {
        var world = Caravans.ServerWorld();
        var prefabs = world == null ? null : Caravans.Prefabs(world);
        if (prefabs == null) { ctx.Reply(L.T("world.notready")); return; }
        var em = world.EntityManager;
        float3 me = Caravans.PositionOf(em, ctx.Event.SenderCharacterEntity);

        Entity best = Entity.Null;
        float bestDist = float.MaxValue;
        var builder = new EntityQueryBuilder(Allocator.Temp)
            .AddAll(Ct<PrefabGUID>.RO)
            .WithOptions(EntityQueryOptions.IncludeDisabled);
        var query = em.CreateEntityQuery(ref builder);
        builder.Dispose();
        var entities = query.ToEntityArray(Allocator.Temp);
        try
        {
            foreach (var e in entities)
            {
                if (Caravans.Classify(prefabs, em.GetComponentData<PrefabGUID>(e)).Kind == Kind.None) continue;
                float d = Caravans.Dist2(Caravans.PositionOf(em, e), me);
                if (d <= radius && d < bestDist) { bestDist = d; best = e; }
            }
        }
        finally
        {
            entities.Dispose();
            query.Dispose();
        }
        if (best == Entity.Null) { ctx.Reply($"No caravan objects within {radius} m."); return; }

        var names = new List<string>();
        var types = em.GetComponentTypes(best);
        try
        {
            for (int i = 0; i < types.Length; i++) names.Add(TypeManager.GetType(types[i].TypeIndex).Name);
        }
        finally
        {
            types.Dispose();
        }
        names.Sort(StringComparer.Ordinal);
        if (!prefabs._PrefabLookupMap.TryGetName(em.GetComponentData<PrefabGUID>(best), out string prefabName)) prefabName = "?";
        Plugin.Info($"CarriageTracker comps {prefabName}: {string.Join(", ", names)}");
        ctx.Reply($"{prefabName}, {Math.Round(bestDist)} m, components: {names.Count}. Full list in the server log.");
        for (int i = 0; i < names.Count; i += 6)
            ctx.Reply(string.Join(", ", names.GetRange(i, Math.Min(6, names.Count - i))));

        // Values of the components that remove the object by time (a loot container vanished a minute after its
        // cart was destroyed, 03.11), to the log and to chat.
        var values = new List<string>();
        var all = em.GetComponentTypes(best);
        try
        {
            for (int i = 0; i < all.Length; i++)
            {
                var il2 = TypeManager.GetType(all[i].TypeIndex);
                if (il2 == null || !IsTimer(il2.Name)) continue;
                var managed = ManagedByFullName(il2.FullName);
                string text;
                if (managed == null || !managed.IsValueType) text = "(unreadable)";
                else if (managed.GetFields(BindingFlags.Public | BindingFlags.Instance).Length == 0) text = "(tag without data)";
                else
                {
                    try
                    {
                        var value = ReadRaw(em, best, all[i], managed);
                        text = value == null ? "null" : Fields(value, em, prefabs, 0);
                    }
                    catch (Exception ex) { text = $"<{(ex.Message ?? "").Split('\n')[0]}>"; }
                }
                values.Add($"{il2.Name}: {text}");
            }
        }
        finally
        {
            all.Dispose();
        }
        foreach (var v in values)
        {
            Plugin.Info($"CarriageTracker comps {prefabName} {v}");
            foreach (var line in ChatLines.Pack(v.Split(", "), ", ")) ctx.Reply(line);
        }
    }

    static bool IsTimer(string name) =>
        name.StartsWith("Destroy", StringComparison.Ordinal) || name.IndexOf("LifeTime", StringComparison.OrdinalIgnoreCase) >= 0 ||
        name.StartsWith("Age", StringComparison.Ordinal);

    [Command("near", adminOnly: true, usage: "[radius]", description: "Objects around you with names and ids")]
    public void Near(ChatCommandContext ctx, int radius = 8)
    {
        var world = Caravans.ServerWorld();
        var prefabs = world == null ? null : Caravans.Prefabs(world);
        if (prefabs == null) { ctx.Reply(L.T("world.notready")); return; }
        var em = world.EntityManager;
        float3 me = Caravans.PositionOf(em, ctx.Event.SenderCharacterEntity);

        var seen = new Dictionary<int, (string Name, int Count, float Dist, bool Net)>();
        var builder = new EntityQueryBuilder(Allocator.Temp)
            .AddAll(Ct<PrefabGUID>.RO)
            .WithOptions(EntityQueryOptions.IncludeDisabled);
        var query = em.CreateEntityQuery(ref builder);
        builder.Dispose();
        var entities = query.ToEntityArray(Allocator.Temp);
        try
        {
            foreach (var e in entities)
            {
                if (!Caravans.Has<LocalToWorld>(em, e) && !Caravans.Has<Translation>(em, e)) continue;
                float d = Caravans.Dist2(Caravans.PositionOf(em, e), me);
                if (d > radius) continue;
                var guid = em.GetComponentData<PrefabGUID>(e);
                if (seen.TryGetValue(guid.GuidHash, out var s))
                {
                    seen[guid.GuidHash] = (s.Name, s.Count + 1, Math.Min(s.Dist, d), s.Net);
                    continue;
                }
                if (!prefabs._PrefabLookupMap.TryGetName(guid, out string name)) name = "?";
                seen[guid.GuidHash] = (name, 1, d, Caravans.Has<NetworkId>(em, e));
            }
        }
        finally
        {
            entities.Dispose();
            query.Dispose();
        }

        var list = new List<KeyValuePair<int, (string Name, int Count, float Dist, bool Net)>>(seen);
        list.Sort((a, b) => a.Value.Dist.CompareTo(b.Value.Dist));
        ctx.Reply($"Different objects within {radius} m: {list.Count}. First 20:");
        for (int i = 0; i < list.Count && i < 20; i++)
        {
            var v = list[i].Value;
            ctx.Reply($"{v.Name} ({list[i].Key}) x{v.Count}, {Math.Round(v.Dist)} m, network={(v.Net ? "yes" : "no")}");
        }
    }

    // ---- markers: whose map markers and icons there are (audit 07.11 by Nikolai, R4) ----
    // With Icon = merchant (the default) the mod's icon is the game's own merchant icon. Lists every
    // MapIcon_ProxyObject_POI_Unknown marker grouped by what spawned it, its icon and whether the mod takes it as its
    // own (IconLogic.Owned), and every icon of the mod's kinds grouped by what it hangs on. A game merchant marker
    // taken as the mod's would be removed by the mod: then this shows "mod's: yes" next to a merchant.
    [Command("markers", adminOnly: true, description: "Map markers and icons of the mod's kinds, grouped, into chat and the log")]
    public void Markers(ChatCommandContext ctx)
    {
        var world = Caravans.ServerWorld();
        var prefabs = world == null ? null : Caravans.Prefabs(world);
        if (prefabs == null) { ctx.Reply(L.T("world.notready")); return; }
        var em = world.EntityManager;
        string Name(Entity e)
        {
            if (e == Entity.Null) return "nothing";
            if (!em.Exists(e)) return "gone";
            return Caravans.Has<PrefabGUID>(em, e) && prefabs._PrefabLookupMap.TryGetName(em.GetComponentData<PrefabGUID>(e), out string n) ? n : "?";
        }
        string IconName(int g) => g == 0 ? "none" : prefabs._PrefabLookupMap.TryGetName(new PrefabGUID(g), out string n) ? n : g.ToString();

        var markers = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var builder = new EntityQueryBuilder(Allocator.Temp)
            .AddAll(Ct<PrefabGUID>.RO)
            .AddAll(Ct<SpawnedBy>.RO)
            .WithOptions(EntityQueryOptions.IncludeDisabled);
        var query = em.CreateEntityQuery(ref builder);
        builder.Dispose();
        var arr = query.ToEntityArray(Allocator.Temp);
        try
        {
            foreach (var p in arr)
            {
                if (em.GetComponentData<PrefabGUID>(p).GuidHash != Proxies.ProxyGuid) continue;
                string key = $"by {Name(em.GetComponentData<SpawnedBy>(p).Value)}, icon {IconName(Proxies.IconOf(em, p))}, " +
                             $"mod's: {(Proxies.IsMine(em, prefabs, p) ? "yes" : "no")}{(Doomed.IsDying(p) ? ", being destroyed" : "")}";
                markers[key] = (markers.TryGetValue(key, out int n) ? n : 0) + 1;
            }
        }
        finally
        {
            arr.Dispose();
            query.Dispose();
        }

        var kinds = Icons.Kinds();
        var icons = new SortedDictionary<string, int>(StringComparer.Ordinal);
        builder = new EntityQueryBuilder(Allocator.Temp)
            .AddAll(Ct<MapIconData>.RO)
            .AddAll(Ct<PrefabGUID>.RO)
            .WithOptions(EntityQueryOptions.IncludeDisabled);
        query = em.CreateEntityQuery(ref builder);
        builder.Dispose();
        arr = query.ToEntityArray(Allocator.Temp);
        try
        {
            foreach (var icon in arr)
            {
                int g = em.GetComponentData<PrefabGUID>(icon).GuidHash;
                if (Icons.KindOf(g, kinds) == IconKind.Other) continue;
                string on = Caravans.Has<Attach>(em, icon) ? "on marker " + Name(em.GetComponentData<Attach>(icon).Parent)
                          : Caravans.Has<MapIconTargetEntity>(em, icon) ? "on " + Name(em.GetComponentData<MapIconTargetEntity>(icon).TargetEntity.GetEntityOnServer())
                          : "on nothing";
                string key = $"{IconName(g)} {on}";
                icons[key] = (icons.TryGetValue(key, out int n) ? n : 0) + 1;
            }
        }
        finally
        {
            arr.Dispose();
            query.Dispose();
        }

        var lines = new List<string> { $"Markers {IconName(Proxies.ProxyGuid)}: {markers.Count} groups" };
        foreach (var kv in markers) lines.Add($"  {kv.Value} x {kv.Key}");
        lines.Add($"Icons of the mod's kinds: {icons.Count} groups");
        foreach (var kv in icons) lines.Add($"  {kv.Value} x {kv.Key}");
        foreach (var line in lines) Plugin.Info($"CarriageTracker markers: {line}");
        for (int i = 0; i < lines.Count && i < 20; i++) ctx.Reply(lines[i]);
        if (lines.Count > 20) ctx.Reply($"... {lines.Count - 20} more lines in the server log.");
    }

    // ---- movemarker: does a marker that is moved show in the new place (audit 07.11 by Nikolai, R2) ----
    // Puts the nearest of the mod's markers where you stand. If the carriage icon on the map moves there, markers can
    // be moved instead of being made anew. A frozen caravan's marker stays there for 3 minutes; a moving one in proxy
    // mode is made anew at the next check.
    [Command("movemarker", adminOnly: true, usage: "[radius]", description: "Move the nearest marker of the mod to you")]
    public void MoveMarker(ChatCommandContext ctx, int radius = 300)
    {
        var world = Caravans.ServerWorld();
        var prefabs = world == null ? null : Caravans.Prefabs(world);
        if (prefabs == null) { ctx.Reply(L.T("world.notready")); return; }
        var em = world.EntityManager;
        float3 me = Caravans.PositionOf(em, ctx.Event.SenderCharacterEntity);
        Entity best = Entity.Null;
        float bestDist = float.MaxValue;
        var builder = new EntityQueryBuilder(Allocator.Temp)
            .AddAll(Ct<PrefabGUID>.RO)
            .AddAll(Ct<SpawnedBy>.RO)
            .WithOptions(EntityQueryOptions.IncludeDisabled);
        var query = em.CreateEntityQuery(ref builder);
        builder.Dispose();
        var arr = query.ToEntityArray(Allocator.Temp);
        try
        {
            foreach (var p in arr)
            {
                if (em.GetComponentData<PrefabGUID>(p).GuidHash != Proxies.ProxyGuid || Doomed.IsDying(p) || !Proxies.IsMine(em, prefabs, p)) continue;
                float d = Caravans.Dist2(Caravans.PositionOf(em, p), me);
                if (d <= radius && d < bestDist) { bestDist = d; best = p; }
            }
        }
        finally
        {
            arr.Dispose();
            query.Dispose();
        }
        if (best == Entity.Null) { ctx.Reply($"No marker of the mod within {radius} m. Markers are used for frozen caravans, or for all in proxy mode."); return; }
        Proxies.Moved(em, best, me);
        Plugin.Info($"CarriageTracker movemarker: marker {best.Index}:{best.Version} moved {Math.Round(bestDist)} m to ({Math.Round(me.x)}, {Math.Round(me.z)})");
        ctx.Reply($"Marker moved {Math.Round(bestDist)} m to you. Open the map: if the carriage icon is now where you stand, moving markers works. " +
                  "A frozen caravan's marker stays here until it is 3 min old and its caravan is 100 m away.");
    }

    // ---- dump: everything about caravan carts and their data ----

    static readonly string[] ScriptTypes =
        { "Script_CarriageData", "Script_CarriageDriverData", "Script_CarriageCollidersData", "Script_RespawnCarriageUponLost_Data" };

    static readonly string[] Interesting =
        { "Carriage", "Script_", "Respawn", "Spawn", "Route", "Travel", "Path", "Patrol", "Waypoint", "Timer", "Lifetime",
          "Follow", "Horse", "Driver", "Mount", "Lost", "Teleport", "Move", "Idle", "Disable", "Hidden", "Owner", "Attach",
          "Translation", "Transform", "Chain", "Leader", "Group", "Squad", "Minion", "Master", "Region", "Range" };

    // Objects referenced by dumped fields (SpawnedBy, Follower etc.). Dumped in full in a second pass.
    static readonly List<Entity> Refs = new();

    static readonly Dictionary<string, Type> ManagedTypes = new();

    // Finds the C# wrapper of a game type by its full name.
    static Type ManagedByFullName(string fullName)
    {
        if (ManagedTypes.TryGetValue(fullName, out var cached)) return cached;
        Type found = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try { found = asm.GetType(fullName, false); } catch { }
            if (found != null) break;
        }
        ManagedTypes[fullName] = found;
        return found;
    }

    // Finds the C# wrapper of a game type by its short name, preferring ProjectM namespaces.
    static Type ManagedBySimpleName(string name)
    {
        Type fallback = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types; }
            catch { continue; }
            foreach (var t in types)
            {
                if (t == null || t.Name != name) continue;
                if (t.Namespace != null && t.Namespace.StartsWith("ProjectM", StringComparison.Ordinal)) return t;
                fallback ??= t;
            }
        }
        return fallback;
    }

    static bool IsInteresting(string name)
    {
        foreach (var w in Interesting)
            if (name.IndexOf(w, StringComparison.Ordinal) >= 0) return true;
        return false;
    }

    static string EntityName(EntityManager em, PrefabCollectionSystem prefabs, Entity e)
    {
        if (e == Entity.Null) return "null";
        if (!em.Exists(e)) return $"{e.Index}:{e.Version} (gone)";
        string n = "?";
        if (Caravans.Has<PrefabGUID>(em, e) && prefabs._PrefabLookupMap.TryGetName(em.GetComponentData<PrefabGUID>(e), out string pn)) n = pn;
        return $"{e.Index}:{e.Version} {n}";
    }

    static string Fmt(object v, EntityManager em, PrefabCollectionSystem prefabs, int depth)
    {
        switch (v)
        {
            case null: return "null";
            case Entity e:
                if (e != Entity.Null && em.Exists(e)) Refs.Add(e);
                return EntityName(em, prefabs, e);
            case PrefabGUID g: return prefabs._PrefabLookupMap.TryGetName(g, out string gn) ? $"{g.GuidHash} {gn}" : g.GuidHash.ToString();
            case float3 f3: return $"({f3.x:0.#}, {f3.y:0.#}, {f3.z:0.#})";
            case float2 f2: return $"({f2.x:0.#}, {f2.y:0.#})";
            case NetworkedEntity ne: return Fmt(ne.GetEntityOnServer(), em, prefabs, depth);
            case string s: return s;
        }
        var t = v.GetType();
        if (t.IsPrimitive || t.IsEnum) return v.ToString();
        if (t.Name.StartsWith("FixedString", StringComparison.Ordinal))
        {
            try { return "\"" + v.ToString() + "\""; } catch { return "<FixedString>"; }
        }
        if (depth < 2 && t.IsValueType) return "{" + Fields(v, em, prefabs, depth + 1) + "}";
        return v.ToString();
    }

    static string Fields(object boxed, EntityManager em, PrefabCollectionSystem prefabs, int depth)
    {
        var parts = new List<string>();
        foreach (var f in boxed.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            try { parts.Add($"{f.Name}={Fmt(f.GetValue(boxed), em, prefabs, depth)}"); }
            catch (Exception ex) { parts.Add($"{f.Name}=<{ex.GetType().Name}>"); }
        }
        var text = string.Join(", ", parts);
        return text.Length > 700 ? text.Substring(0, 700) + "..." : text;
    }

    static unsafe object ReadRaw(EntityManager em, Entity e, ComponentType ct, Type managed)
    {
        void* ptr = em.GetComponentDataRawRO(e, ct.TypeIndex);
        if (ptr == null) return null;
        return Marshal.PtrToStructure(new IntPtr(ptr), managed);
    }

    static void DumpEntity(EntityManager em, PrefabCollectionSystem prefabs, Entity e, List<string> lines, bool all)
    {
        var pos = Caravans.PositionOf(em, e);
        lines.Add("");
        lines.Add($"== {EntityName(em, prefabs, e)}  pos=({pos.x:0}, {pos.y:0}, {pos.z:0})  onMap={Caravans.OnMap(pos)}  disabled={Caravans.Has<Disabled>(em, e)}");
        var names = new List<string>();
        var types = em.GetComponentTypes(e);
        try
        {
            for (int i = 0; i < types.Length; i++)
            {
                var ct = types[i];
                var il2 = TypeManager.GetType(ct.TypeIndex);
                string shortName = il2 == null ? "?" : il2.Name;
                string fullName = il2 == null ? "?" : il2.FullName;
                names.Add(shortName);
                if (!all && !IsInteresting(shortName)) continue;
                var managed = ManagedByFullName(fullName);
                if (managed == null || !managed.IsValueType) { lines.Add($"  {shortName}: (unreadable)"); continue; }
                // A buffer is read element by element. Until 1.0.22 its header was read as if it were one element, which
                // gave made-up values (the patrol's stops and squad in the dump of 12.11).
                if (ct.IsBuffer) { lines.Add($"  {shortName}: {ReadBuffer(em, prefabs, e, managed, 16)}"); continue; }
                if (managed.GetFields(BindingFlags.Public | BindingFlags.Instance).Length == 0) { lines.Add($"  {shortName}: (tag without data)"); continue; }
                try
                {
                    var value = ReadRaw(em, e, ct, managed);
                    lines.Add($"  {shortName}: {(value == null ? "null" : Fields(value, em, prefabs, 0))}");
                }
                catch (Exception ex)
                {
                    string msg = (ex.Message ?? "").Split('\n')[0];
                    lines.Add($"  {shortName}: <{(msg.Length > 150 ? msg.Substring(0, 150) : msg)}>");
                }
            }
        }
        finally
        {
            types.Dispose();
        }
        names.Sort(StringComparer.Ordinal);
        lines.Add("  all components: " + string.Join(", ", names));
    }

    [Command("dump", adminOnly: true, description: "Everything about caravan carts into BepInEx/CarriageTracker-dump.txt")]
    public void Dump(ChatCommandContext ctx)
    {
        var world = Caravans.ServerWorld();
        var prefabs = world == null ? null : Caravans.Prefabs(world);
        if (prefabs == null) { ctx.Reply(L.T("world.notready")); return; }
        var em = world.EntityManager;
        var lines = new List<string> { $"CarriageTracker dump {DateTime.Now:yyyy-MM-dd HH:mm:ss}" };
        var targets = new List<Entity>();
        var seen = new HashSet<(int, int)>();

        // 1. Carts, horses and containers, including parked ones outside the map.
        foreach (var f in Caravans.FullScan(em, prefabs, out _))
            if (seen.Add((f.Entity.Index, f.Entity.Version))) targets.Add(f.Entity);
        lines.Add($"caravan objects: {targets.Count}");

        // 2. Everything with Script_* caravan data, even if it is not a cart.
        foreach (var name in ScriptTypes)
        {
            try
            {
                var managed = ManagedBySimpleName(name);
                if (managed == null) { lines.Add($"type {name}: not found"); continue; }
                var builder = new EntityQueryBuilder(Allocator.Temp)
                    .AddAll(ComponentType.ReadOnly(Il2CppType.From(managed)))
                    .WithOptions(EntityQueryOptions.IncludeDisabled);
                var query = em.CreateEntityQuery(ref builder);
                builder.Dispose();
                var arr = query.ToEntityArray(Allocator.Temp);
                try
                {
                    lines.Add($"type {managed.FullName}: {arr.Length} entities");
                    foreach (var e in arr)
                        if (seen.Add((e.Index, e.Version))) targets.Add(e);
                }
                finally
                {
                    arr.Dispose();
                    query.Dispose();
                }
            }
            catch (Exception ex)
            {
                lines.Add($"type {name}: error {ex.GetType().Name}: {ex.Message}");
            }
        }

        Refs.Clear();
        int count = Math.Min(targets.Count, 80);
        for (int i = 0; i < count; i++) DumpEntity(em, prefabs, targets[i], lines, false);

        // 3. Objects referenced by carts and horses (who spawned them, whom they follow), in full.
        var refs = new List<Entity>(Refs);
        int refCount = 0;
        lines.Add("");
        lines.Add("==== referenced entities ====");
        foreach (var r in refs)
        {
            if (refCount >= 25) break;
            if (!seen.Add((r.Index, r.Version))) continue;
            // Objects without a prefab name are dumped in full, named ones as usual.
            bool named = Caravans.Has<PrefabGUID>(em, r) && prefabs._PrefabLookupMap.TryGetName(em.GetComponentData<PrefabGUID>(r), out string _);
            DumpEntity(em, prefabs, r, lines, !named);
            refCount++;
        }
        count += refCount;

        var path = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "CarriageTracker-dump.txt");
        System.IO.File.WriteAllLines(path, lines);
        ctx.Reply($"Objects written: {count} of {targets.Count}, lines: {lines.Count}. File: BepInEx/CarriageTracker-dump.txt");
    }

    // ---- patrols: all squad spawners and patrols of the world ----

    static unsafe object ReadComp(EntityManager em, Entity e, Type managed)
    {
        if (managed == null) return null;
        var ct = ComponentType.ReadOnly(Il2CppType.From(managed));
        if (!em.HasComponent(e, ct)) return null;
        return ReadRaw(em, e, ct, managed);
    }

    static object FieldOf(object boxed, string name) => boxed?.GetType().GetField(name)?.GetValue(boxed);

    static MethodInfo _getBuffer;

    // Elements of a DynamicBuffer whose type is known only at runtime. GetBuffer<T> is made for that type by
    // reflection; for a type the game never reads as a buffer itself that can fail, which comes back as an error
    // text instead of the elements.
    static string ReadBuffer(EntityManager em, PrefabCollectionSystem prefabs, Entity e, Type elemType, int max)
    {
        if (elemType == null) return "";
        try
        {
            if (!em.HasComponent(e, ComponentType.ReadOnly(Il2CppType.From(elemType)))) return "";
            if (_getBuffer == null)
                foreach (var m in typeof(EntityManager).GetMethods())
                    if (m.Name == "GetBuffer" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2) { _getBuffer = m; break; }
            if (_getBuffer == null) return "<no GetBuffer>";
            object buffer = _getBuffer.MakeGenericMethod(elemType).Invoke(em, new object[] { e, true });
            var bt = buffer.GetType();
            int length = (int)bt.GetProperty("Length").GetValue(buffer);
            var item = bt.GetProperty("Item") ?? null;
            var getItem = item != null ? item.GetGetMethod() : bt.GetMethod("get_Item");
            var parts = new List<string>();
            for (int i = 0; i < length && i < max; i++)
                parts.Add("{" + Fields(getItem.Invoke(buffer, new object[] { i }), em, prefabs, 1) + "}");
            return $"[{length}] " + string.Join(" ", parts);
        }
        catch (Exception ex)
        {
            var inner = ex.InnerException ?? ex;
            string msg = (inner.Message ?? "").Split('\n')[0];
            return $"<{inner.GetType().Name}: {(msg.Length > 120 ? msg.Substring(0, 120) : msg)}>";
        }
    }

    [Command("patrols", adminOnly: true, description: "All squad spawners and patrols into BepInEx/CarriageTracker-patrols.txt")]
    public void Patrols(ChatCommandContext ctx)
    {
        var world = Caravans.ServerWorld();
        var prefabs = world == null ? null : Caravans.Prefabs(world);
        if (prefabs == null) { ctx.Reply(L.T("world.notready")); return; }
        var em = world.EntityManager;
        var lines = new List<string> { $"CarriageTracker patrols {DateTime.Now:yyyy-MM-dd HH:mm:ss}" };

        var spawnerT = ManagedBySimpleName("UnitCompositionSpawner");
        var patrolT = ManagedBySimpleName("GlobalPatrolState");
        var moveT = ManagedBySimpleName("MovePatrolState");
        var nameT = ManagedBySimpleName("UnitCompositionSpawnerDebugName");
        var groupT = ManagedBySimpleName("UnitCompositionGroupEntry");
        var unitT = ManagedBySimpleName("UnitCompositionGroupUnitEntry");
        var activeT = ManagedBySimpleName("UnitCompositionActiveUnit");
        var busT = ManagedBySimpleName("PatrolBusStopNode");
        foreach (var (label, t) in new[] { ("spawner", spawnerT), ("patrol", patrolT), ("move", moveT), ("debugName", nameT),
                                           ("group", groupT), ("unit", unitT), ("active", activeT), ("busStop", busT) })
            lines.Add($"type {label}: {(t == null ? "not found" : t.FullName)}");
        if (spawnerT == null) { System.IO.File.WriteAllLines(System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "CarriageTracker-patrols.txt"), lines); ctx.Reply("Type UnitCompositionSpawner not found."); return; }

        var builder = new EntityQueryBuilder(Allocator.Temp)
            .AddAll(ComponentType.ReadOnly(Il2CppType.From(spawnerT)))
            .WithOptions(EntityQueryOptions.IncludeDisabled);
        var query = em.CreateEntityQuery(ref builder);
        builder.Dispose();
        var arr = query.ToEntityArray(Allocator.Temp);
        int total = arr.Length;
        var states = new Dictionary<string, int>();
        int patrols = 0, carriagePatrols = 0;
        var patrolLines = new List<string>();
        var otherLines = new List<string>();
        try
        {
            foreach (var e in arr)
            {
                var sp = ReadComp(em, e, spawnerT);
                string state = FieldOf(sp, "SpawnerState")?.ToString() ?? "?";
                states[state] = states.TryGetValue(state, out var n) ? n + 1 : 1;
                var pos = Caravans.PositionOf(em, e);
                string name = nameT == null ? "" : (ReadComp(em, e, nameT) is object dn ? Fields(dn, em, prefabs, 0) : "");
                string units = ReadBuffer(em, prefabs, e, unitT, 40);
                bool isPatrol = patrolT != null && ReadComp(em, e, patrolT) != null;
                string head = $"{e.Index}:{e.Version} pos=({pos.x:0}, {pos.z:0}) onMap={Caravans.OnMap(pos)} disabled={Caravans.Has<Disabled>(em, e)} state={state} name={name}";
                if (!isPatrol)
                {
                    otherLines.Add(head + " units=" + units);
                    continue;
                }
                patrols++;
                bool carriage = units.IndexOf("Carriage", StringComparison.Ordinal) >= 0;
                if (carriage) carriagePatrols++;
                patrolLines.Add("");
                patrolLines.Add($"== PATROL{(carriage ? " CARRIAGE" : "")} " + head);
                patrolLines.Add("  units: " + units);
                patrolLines.Add("  groups: " + ReadBuffer(em, prefabs, e, groupT, 10));
                patrolLines.Add("  active: " + ReadBuffer(em, prefabs, e, activeT, 30));
                patrolLines.Add("  busStops: " + ReadBuffer(em, prefabs, e, busT, 30));
                patrolLines.Add("  GlobalPatrolState: " + Fields(ReadComp(em, e, patrolT), em, prefabs, 0));
                if (ReadComp(em, e, moveT) is object mv) patrolLines.Add("  MovePatrolState: " + Fields(mv, em, prefabs, 0));
                patrolLines.Add("  UnitCompositionSpawner: " + (sp == null ? "null" : Fields(sp, em, prefabs, 0)));
            }
        }
        finally
        {
            arr.Dispose();
            query.Dispose();
        }

        lines.Add($"spawners: {total}, patrols: {patrols}, carriage patrols: {carriagePatrols}");
        foreach (var kv in states) lines.Add($"  state {kv.Key}: {kv.Value}");
        lines.Add("");
        lines.Add("==== patrols ====");
        lines.AddRange(patrolLines);
        lines.Add("");
        lines.Add("==== other spawners ====");
        lines.AddRange(otherLines);
        System.IO.File.WriteAllLines(System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "CarriageTracker-patrols.txt"), lines);
        ctx.Reply($"Spawners: {total}, patrols: {patrols}, with carts: {carriagePatrols}. File: BepInEx/CarriageTracker-patrols.txt");
    }

    // ---- prefabs and types: looking for game objects by a part of their name (custom caravans, 12.11) ----

    // Components that tell what a unit or object is, shown next to each prefab.
    static readonly string[] PrefabMarks = { "VBlood", "Boss", "Soul", "Shard", "Incursion", "Rift", "Gate", "DropTable", "Consume", "Unlock" };

    static List<string> ComponentNames(EntityManager em, Entity e)
    {
        var names = new List<string>();
        var types = em.GetComponentTypes(e);
        try
        {
            for (int i = 0; i < types.Length; i++)
            {
                var il2 = TypeManager.GetType(types[i].TypeIndex);
                names.Add(il2 == null ? "?" : il2.Name);
            }
        }
        finally
        {
            types.Dispose();
        }
        names.Sort(StringComparer.Ordinal);
        return names;
    }

    // A part of a file name made of the letters, digits and _ of the text.
    static string FileSuffix(string text)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in text ?? "")
            if (char.IsLetterOrDigit(c) || c == '_') sb.Append(c);
        string s = sb.ToString();
        return s.Length == 0 ? "all" : s.Length > 30 ? s.Substring(0, 30) : s;
    }

    // Every game prefab whose name contains the text, with its number, its level if it is a unit and the components
    // that tell what it is (PrefabMarks). With "full" the first 60 are written with all their data, buffers included
    // (drop tables, squads).
    [Command("prefabs", adminOnly: true, usage: "<part of the name> [full]", description: "Game prefabs whose name contains the text, into BepInEx/CarriageTracker-prefabs-<text>.txt; full = with all their data")]
    public void PrefabList(ChatCommandContext ctx, string filter, string mode = "")
    {
        var world = Caravans.ServerWorld();
        var prefabs = world == null ? null : Caravans.Prefabs(world);
        if (prefabs == null) { ctx.Reply(L.T("world.notready")); return; }
        var em = world.EntityManager;
        bool full = mode.Equals("full", StringComparison.OrdinalIgnoreCase);
        const int fullMax = 60, listMax = 8000;
        filter = (filter ?? "").Trim();
        if (filter.Length < 3) { ctx.Reply("Give at least 3 letters of the name, e.g. .cvdiag prefabs CHAR_"); return; }

        var found = new List<(string Name, PrefabGUID Guid)>();
        foreach (var kv in prefabs._PrefabGuidToEntityMap)
        {
            if (!prefabs._PrefabLookupMap.TryGetName(kv.Key, out string name) || string.IsNullOrEmpty(name)) continue;
            if (name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            found.Add((name, kv.Key));
        }
        found.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

        var lines = new List<string> { $"CarriageTracker prefabs \"{filter}\"{(full ? " full" : "")} {DateTime.Now:yyyy-MM-dd HH:mm:ss}: {found.Count}" };
        Refs.Clear();
        if (found.Count > listMax) lines.Add($"only the first {listMax} are written; give a longer text");
        for (int i = 0; i < found.Count && i < listMax; i++)
        {
            var (name, guid) = found[i];
            string line = $"{name} {guid.GuidHash}";
            Entity prefab = Entity.Null;
            try
            {
                if (prefabs._PrefabGuidToEntityMap.TryGetValue(guid, out prefab) && em.Exists(prefab))
                {
                    int level = Acts.LevelOf(em, prefabs, guid);
                    if (level > 0) line += $" level={level}";
                    var marks = ComponentNames(em, prefab).FindAll(n => Array.Exists(PrefabMarks, m => n.IndexOf(m, StringComparison.OrdinalIgnoreCase) >= 0));
                    if (marks.Count > 0) line += " [" + string.Join(", ", marks) + "]";
                }
                else prefab = Entity.Null;
            }
            catch (Exception ex) { line += $" <{ex.GetType().Name}>"; }
            lines.Add(line);
            if (!full || i >= fullMax || prefab == Entity.Null) continue;
            try { DumpEntity(em, prefabs, prefab, lines, true); }
            catch (Exception ex) { lines.Add($"  <{ex.GetType().Name}: {(ex.Message ?? "").Split('\n')[0]}>"); }
            lines.Add("");
        }
        Refs.Clear();

        string file = $"CarriageTracker-prefabs-{FileSuffix(filter)}.txt";
        System.IO.File.WriteAllLines(System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, file), lines);
        ctx.Reply($"Prefabs with \"{filter}\" in the name: {found.Count}{(full && found.Count > fullMax ? $", the first {fullMax} in full" : "")}. File: BepInEx/{file}");
        for (int i = 0; i < found.Count && i < 10; i++) ctx.Reply($"{found[i].Name} ({found[i].Guid.GuidHash})");
    }

    // Every game component type whose name contains the text: how many objects have it, and for the first of them
    // where they are and what it holds. For finding out how a part of the game is built when only a name is known:
    // "BusStop" for the stops of patrol routes and where they are on the map.
    [Command("types", adminOnly: true, usage: "<part of the type name> [objects per type]", description: "Game component types whose name contains the text and the objects that have them, into BepInEx/CarriageTracker-types-<text>.txt")]
    public void TypeList(ChatCommandContext ctx, string filter, int max = 3000)
    {
        var world = Caravans.ServerWorld();
        var prefabs = world == null ? null : Caravans.Prefabs(world);
        if (prefabs == null) { ctx.Reply(L.T("world.notready")); return; }
        var em = world.EntityManager;
        max = Math.Clamp(max, 1, 20000);
        // The whole file is held in memory and written on the main thread: at most 60 types and 30000 lines of at
        // most 4000 characters, whatever the text matches.
        const int typeMax = 60, lineMax = 30000, lineLength = 4000;
        filter = (filter ?? "").Trim();
        if (filter.Length < 3) { ctx.Reply("Give at least 3 letters of the type name, e.g. .cvdiag types BusStop"); return; }

        var candidates = new List<Type>();
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            string asmName = asm.GetName().Name ?? "";
            if (!asmName.StartsWith("ProjectM", StringComparison.Ordinal) && !asmName.StartsWith("Stunlock", StringComparison.Ordinal)) continue;
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types; }
            catch { continue; }
            foreach (var t in types)
            {
                if (t == null || !t.IsValueType || t.IsEnum || t.IsGenericType || t.IsGenericTypeDefinition) continue;
                if (t.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                candidates.Add(t);
            }
        }
        candidates.Sort((a, b) => string.CompareOrdinal(a.FullName, b.FullName));

        var lines = new List<string> { $"CarriageTracker types \"{filter}\" {DateTime.Now:yyyy-MM-dd HH:mm:ss}: {candidates.Count} types with the text in their name" };
        if (candidates.Count > typeMax) lines.Add($"only the first {typeMax} are looked at; give a longer text");
        int components = 0, objects = 0;
        Refs.Clear();
        bool cut = false;
        for (int c = 0; c < candidates.Count && c < typeMax && !cut; c++)
        {
            var t = candidates[c];
            ComponentType ct;
            try
            {
                ct = ComponentType.ReadOnly(Il2CppType.From(t));
                // A type the game hasn't registered as a component has no type index: it is never put into a query.
                if (ct.TypeIndex.Equals(default(ComponentType).TypeIndex)) { lines.Add($"type {t.FullName}: not a component"); continue; }
            }
            catch { lines.Add($"type {t.FullName}: not a component"); continue; }
            try
            {
                bool buffer = ct.IsBuffer;
                bool tag = !buffer && t.GetFields(BindingFlags.Public | BindingFlags.Instance).Length == 0;
                var builder = new EntityQueryBuilder(Allocator.Temp)
                    .AddAll(ct)
                    .WithOptions(EntityQueryOptions.IncludeDisabled | EntityQueryOptions.IncludePrefab);
                var query = em.CreateEntityQuery(ref builder);
                builder.Dispose();
                NativeArray<Entity> arr;
                try { arr = query.ToEntityArray(Allocator.Temp); }
                catch { query.Dispose(); throw; }
                try
                {
                    components++;
                    objects += arr.Length;
                    lines.Add("");
                    lines.Add($"== type {t.FullName}: {(buffer ? "buffer" : tag ? "tag" : "component")}, {arr.Length} objects{(arr.Length > max ? $", the first {max} below" : "")}");
                    if (arr.Length > 0) lines.Add("  components of the first one: " + string.Join(", ", ComponentNames(em, arr[0])));
                    for (int i = 0; i < arr.Length && i < max; i++)
                    {
                        if (lines.Count >= lineMax) { cut = true; break; }
                        var e = arr[i];
                        string where = Caravans.Has<LocalToWorld>(em, e) || Caravans.Has<Translation>(em, e)
                            ? $"pos=({Caravans.PositionOf(em, e).x:0.#}, {Caravans.PositionOf(em, e).z:0.#})" : "pos=?";
                        string flags = (Caravans.Has<Unity.Entities.Prefab>(em, e) ? " prefab" : "") + (Caravans.Has<Disabled>(em, e) ? " disabled" : "");
                        string data;
                        try
                        {
                            data = tag ? "" : buffer ? ReadBuffer(em, prefabs, e, t, 30)
                                 : ReadRaw(em, e, ct, t) is object value ? Fields(value, em, prefabs, 0) : "null";
                        }
                        catch (Exception ex) { data = $"<{ex.GetType().Name}: {(ex.Message ?? "").Split('\n')[0]}>"; }
                        if (data.Length > lineLength) data = data.Substring(0, lineLength) + "...";
                        lines.Add($"  {EntityName(em, prefabs, e)} {where}{flags}{(data.Length > 0 ? ": " + data : "")}");
                    }
                }
                finally
                {
                    arr.Dispose();
                    query.Dispose();
                }
            }
            catch (Exception ex) { lines.Add($"type {t.FullName}: error {ex.GetType().Name}: {(ex.Message ?? "").Split('\n')[0]}"); }
        }
        Refs.Clear();
        if (cut) lines.Add($"stopped at {lineMax} lines; give a longer text or fewer objects per type");

        string file = $"CarriageTracker-types-{FileSuffix(filter)}.txt";
        System.IO.File.WriteAllLines(System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, file), lines);
        ctx.Reply($"Types with \"{filter}\" in the name: {candidates.Count}, of them components: {components}, objects with them: {objects}" +
                  $"{(cut ? $", stopped at {lineMax} lines" : "")}. File: BepInEx/{file}");
    }

    // ---- trial caravan and the stops of patrol routes (the mod's own caravans, 12.11) ----

    static void Say(ChatCommandContext ctx, IEnumerable<string> lines)
    {
        foreach (var line in lines) ctx.Reply(line.Length > 450 ? line.Substring(0, 450) + "..." : line);
    }

    // A prefab by its number, its exact name or a part of its name that only one prefab has.
    static bool FindPrefab(PrefabCollectionSystem prefabs, string text, out PrefabGUID guid, out string name, out string problem)
    {
        guid = default;
        name = null;
        problem = null;
        if (int.TryParse(text, out int number))
        {
            guid = new PrefabGUID(number);
            if (prefabs._PrefabGuidToEntityMap.TryGetValue(guid, out _) && prefabs._PrefabLookupMap.TryGetName(guid, out name)) return true;
            problem = $"No prefab has the number {number}.";
            return false;
        }
        var some = new List<(string Name, PrefabGUID Guid)>();
        foreach (var kv in prefabs._PrefabGuidToEntityMap)
        {
            if (!prefabs._PrefabLookupMap.TryGetName(kv.Key, out string n) || string.IsNullOrEmpty(n)) continue;
            if (n.Equals(text, StringComparison.OrdinalIgnoreCase)) { guid = kv.Key; name = n; return true; }
            if (n.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0) some.Add((n, kv.Key));
        }
        if (some.Count == 1) { guid = some[0].Guid; name = some[0].Name; return true; }
        some.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        problem = some.Count == 0 ? $"No prefab has \"{text}\" in its name."
            : $"{some.Count} prefabs have \"{text}\" in their name, give the whole name: " +
              string.Join(", ", some.GetRange(0, Math.Min(6, some.Count)).ConvertAll(x => x.Name)) + (some.Count > 6 ? ", ..." : "");
        return false;
    }

    [Command("trial", adminOnly: true, usage: "<route number> [boss prefab or -] [ally,elite,stops=<route number>,keeptag]",
             description: "A trial caravan: a copy of the route's patrol, started at once. Boss: a unit in place of the first guard. Options, comma-separated: ally (the boss gets the animal's team), trash/normal/elite/boss (its stats), stops=N (walk route N's stops), keeptag")]
    public void Trial(ChatCommandContext ctx, int route, string boss = "-", string options = "")
    {
        var world = Caravans.ServerWorld();
        var prefabs = world == null ? null : Caravans.Prefabs(world);
        if (prefabs == null) { ctx.Reply(L.T("world.notready")); return; }
        var em = world.EntityManager;
        var routes = Routes.All(em, prefabs);
        if (route < 1 || route > routes.Count) { ctx.Reply($"Route numbers are 1 to {routes.Count}, see .cv routes"); return; }

        PrefabGUID? bossGuid = null;
        if (boss != "-" && boss.Length > 0)
        {
            if (!FindPrefab(prefabs, boss, out var guid, out string bossName, out string problem)) { ctx.Reply(problem); return; }
            if (!bossName.StartsWith("CHAR_", StringComparison.Ordinal)) { ctx.Reply($"{bossName} isn't a unit (CHAR_...)."); return; }
            bossGuid = guid;
        }

        bool ally = false, keepTag = false;
        string stats = "";
        Routes.Route stopsFrom = null;
        foreach (string raw in options.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string o = raw.Trim().ToLowerInvariant();
            if (o == "ally") ally = true;
            else if (o == "keeptag") keepTag = true;
            else if (o == "trash" || o == "normal" || o == "elite" || o == "boss") stats = o;
            else if (o.StartsWith("stops=", StringComparison.Ordinal) && int.TryParse(o.Substring(6), out int n) && n >= 1 && n <= routes.Count && n != route)
                stopsFrom = routes[n - 1];
            else { ctx.Reply($"Unknown option \"{raw}\". Options: ally, trash, normal, elite, boss, stops=<another route's number>, keeptag"); return; }
        }
        if (bossGuid == null && (ally || stats.Length > 0)) { ctx.Reply("ally and the stats are for a boss; give its prefab."); return; }

        Say(ctx, Starter.MakeTrial(em, prefabs, routes[route - 1], ctx.Event.SenderUserEntity, bossGuid, stats, ally, stopsFrom, keepTag));
    }

    [Command("trialloot", adminOnly: true, usage: "<item prefab or clear> [amount]", description: "What the trial caravan's container gets instead of the game's loot; several items by several commands, clear = the game's loot stays")]
    public void TrialLoot(ChatCommandContext ctx, string item, int amount = 1)
    {
        if (item.Equals("clear", StringComparison.OrdinalIgnoreCase)) { ctx.Reply(Starter.TrialLootClear()); return; }
        var world = Caravans.ServerWorld();
        var prefabs = world == null ? null : Caravans.Prefabs(world);
        if (prefabs == null) { ctx.Reply(L.T("world.notready")); return; }
        if (!FindPrefab(prefabs, item, out var guid, out string name, out string problem)) { ctx.Reply(problem); return; }
        if (!name.StartsWith("Item_", StringComparison.Ordinal)) { ctx.Reply($"{name} isn't an item (Item_...)."); return; }
        ctx.Reply($"{amount} x {name}. " + Starter.TrialLoot(guid, amount, name));
    }

    [Command("trialinfo", adminOnly: true, description: "The trial caravan and what happened to it")]
    public void TrialInfo(ChatCommandContext ctx)
    {
        var world = Caravans.ServerWorld();
        if (world == null) { ctx.Reply(L.T("world.notready")); return; }
        Say(ctx, Starter.TrialInfo(world.EntityManager));
    }

    [Command("trialclean", adminOnly: true, description: "Removes the trial caravan with its squad; with a patrol id from trialfind, a copy the game kept after a restart")]
    public void TrialClean(ChatCommandContext ctx, string patrol = null)
    {
        var world = Caravans.ServerWorld();
        if (world == null) { ctx.Reply(L.T("world.notready")); return; }
        Say(ctx, Starter.RemoveTrial(world.EntityManager, patrol));
    }

    [Command("trialfind", adminOnly: true, description: "Every carriage patrol and whether it is a game route or a copy, into BepInEx/CarriageTracker-trial.txt")]
    public void TrialFind(ChatCommandContext ctx)
    {
        var world = Caravans.ServerWorld();
        var prefabs = world == null ? null : Caravans.Prefabs(world);
        if (prefabs == null) { ctx.Reply(L.T("world.notready")); return; }
        var lines = Starter.TrialFind(world.EntityManager, prefabs);
        lines.Insert(0, $"CarriageTracker trial {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        lines.AddRange(Starter.TrialInfo(world.EntityManager));
        System.IO.File.WriteAllLines(System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "CarriageTracker-trial.txt"), lines);
        ctx.Reply(lines[1]);
        ctx.Reply("File: BepInEx/CarriageTracker-trial.txt");
    }

    sealed class StopSeen
    {
        public int ChunkX, ChunkZ;
        public bool Exact;
        public float X, Z;
        public string How;
        public readonly List<string> Users = new();
    }

    static readonly System.Text.RegularExpressions.Regex StopPattern =
        new(@"Chunk=(-?\d+),\s*(-?\d+), BusStopGuid=([0-9a-fA-F\-]+)", System.Text.RegularExpressions.RegexOptions.Compiled);
    static readonly System.Text.RegularExpressions.Regex PositionPattern =
        new(@"Position=\((-?[\d.]+), -?[\d.]+, (-?[\d.]+)\)", System.Text.RegularExpressions.RegexOptions.Compiled);

    // Every stop of every patrol route: its chunk and that chunk's square on the map (StopLogic.Cell), who walks it,
    // and its exact place where a patrol shows it now: a patrol waiting at its last stop stands on it, and the last
    // point of a moving patrol's path is taken for the stop it heads for if it lies inside that stop's square (the
    // file says how often it does, which tells whether that guess holds).
    [Command("stops", adminOnly: true, description: "Every stop of every patrol route with its place on the map, into BepInEx/CarriageTracker-stops.txt")]
    public void Stops(ChatCommandContext ctx)
    {
        var world = Caravans.ServerWorld();
        var prefabs = world == null ? null : Caravans.Prefabs(world);
        if (prefabs == null) { ctx.Reply(L.T("world.notready")); return; }
        var em = world.EntityManager;
        var stopT = ManagedBySimpleName("PatrolBusStopNode");
        var wayT = ManagedBySimpleName("WaypointTargetBufferEntry");
        if (stopT == null) { ctx.Reply("Game type PatrolBusStopNode not found."); return; }

        var routeNames = new Dictionary<(int, int), string>();
        foreach (var r in Routes.All(em, prefabs)) routeNames[(r.Patrol.Index, r.Patrol.Version)] = r.Key + (r.Side != null ? "-" + r.Side : "");

        var stops = new Dictionary<string, StopSeen>();
        int patrolCount = 0, standing = 0, standingInside = 0, heading = 0, headingInside = 0;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var query = Queries.Get(em, "GlobalPatrolState+UnitCompositionSpawner all", true, Ct<GlobalPatrolState>.RO, Ct<UnitCompositionSpawner>.RO);
        var patrols = query.ToEntityArray(Allocator.Temp);
        Refs.Clear();
        try
        {
            foreach (var p in patrols)
            {
                var found = StopPattern.Matches(ReadBuffer(em, prefabs, p, stopT, 200));
                if (found.Count == 0) continue;
                patrolCount++;
                string who = routeNames.TryGetValue((p.Index, p.Version), out string rn) ? rn : $"patrol {p.Index}:{p.Version}";
                var mine = new List<StopSeen>();
                for (int i = 0; i < found.Count; i++)
                {
                    string id = found[i].Groups[3].Value;
                    if (!stops.TryGetValue(id, out var stop))
                        stops[id] = stop = new StopSeen { ChunkX = int.Parse(found[i].Groups[1].Value, inv), ChunkZ = int.Parse(found[i].Groups[2].Value, inv) };
                    stop.Users.Add($"{who}#{i}");
                    mine.Add(stop);
                }

                object gp = em.GetComponentData<GlobalPatrolState>(p);
                int current = Caravans.Field(gp, "CurrentBusStopIndex") is IConvertible c ? Convert.ToInt32(c) : -1;
                int target = Caravans.Field(gp, "TargetBusStopIndex") is IConvertible t ? Convert.ToInt32(t) : -1;
                var pos = Caravans.PositionOf(em, p);
                if (Routes.MoveStatus(em, p) == "WaitForDespawn" && current >= 0 && current < mine.Count)
                {
                    standing++;
                    var stop = mine[current];
                    if (StopLogic.InCell(stop.ChunkX, stop.ChunkZ, pos.x, pos.z))
                    {
                        standingInside++;
                        stop.Exact = true; stop.X = pos.x; stop.Z = pos.z; stop.How = $"{who} stands on it";
                    }
                }
                else if (wayT != null && target >= 0 && target < mine.Count)
                {
                    var points = PositionPattern.Matches(ReadBuffer(em, prefabs, p, wayT, 400));
                    if (points.Count == 0) continue;
                    var last = points[points.Count - 1];
                    if (!float.TryParse(last.Groups[1].Value, System.Globalization.NumberStyles.Float, inv, out float x) ||
                        !float.TryParse(last.Groups[2].Value, System.Globalization.NumberStyles.Float, inv, out float z)) continue;
                    heading++;
                    var stop = mine[target];
                    if (!StopLogic.InCell(stop.ChunkX, stop.ChunkZ, x, z)) continue;
                    headingInside++;
                    if (!stop.Exact) { stop.Exact = true; stop.X = x; stop.Z = z; stop.How = $"end of the path of {who}"; }
                }
            }
        }
        finally
        {
            patrols.Dispose();
            Refs.Clear();
        }

        var ids = new List<string>(stops.Keys);
        ids.Sort((a, b) =>
        {
            int byX = stops[a].ChunkX.CompareTo(stops[b].ChunkX);
            if (byX != 0) return byX;
            int byZ = stops[a].ChunkZ.CompareTo(stops[b].ChunkZ);
            return byZ != 0 ? byZ : string.CompareOrdinal(a, b);
        });
        int exact = 0;
        var lines = new List<string>();
        foreach (string id in ids)
        {
            var stop = stops[id];
            if (stop.Exact) exact++;
            var (x0, z0) = StopLogic.Cell(stop.ChunkX, stop.ChunkZ);
            lines.Add($"{id} chunk {stop.ChunkX},{stop.ChunkZ} square x {x0:0}..{x0 + StopLogic.CellSize:0} z {z0:0}..{z0 + StopLogic.CellSize:0} " +
                      (stop.Exact ? string.Format(inv, "at ({0:0.#}, {1:0.#}) ({2})", stop.X, stop.Z, stop.How) : "place within the square unknown") +
                      " | " + string.Join(", ", stop.Users));
        }
        lines.Insert(0, $"CarriageTracker stops {DateTime.Now:yyyy-MM-dd HH:mm:ss}: {stops.Count} stops of {patrolCount} patrols, exact place known for {exact}");
        lines.Insert(1, $"patrols waiting at their last stop: {standing}, of them inside that stop's square: {standingInside}");
        lines.Insert(2, $"moving patrols with a path: {heading}, of them with the path's end inside the square of the stop they head for: {headingInside}");
        lines.Insert(3, "");
        System.IO.File.WriteAllLines(System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "CarriageTracker-stops.txt"), lines);
        ctx.Reply($"Stops: {stops.Count} of {patrolCount} patrols, exact place known for {exact}. File: BepInEx/CarriageTracker-stops.txt");
    }
}
