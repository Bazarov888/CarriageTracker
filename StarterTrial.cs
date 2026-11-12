using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime;
using ProjectM;
using ProjectM.Network;
using ProjectM.Scripting;
using ProjectM.Shared;
using Stunlock.Core;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;

namespace CarriageTracker;

// Trial caravan: a developer tool (.cvdiag trial, EnableDiagnostics = true) that answers by experiment what the
// dumps of 12.11 couldn't, before the mod's own caravans are designed:
// 1. Does a copy of a route's patrol work as a route (its own squad, optionally the stops of another route), and
//    does the copy get into the save?
// 2. Does a rift boss ("Blood Soul", CHAR_*_GateBoss_Minor / _Major) walk in the squad, and does it fight the
//    guards (it is of Faction_Legion) unless it gets the animal's team and faction?
// 3. Can the mod replace what is in the loot container before a player opens it?
//
// The copy is started like any route (TryStart) and tracked like any start (Runs), so everything a start does
// applies to it. It is never one of the mod's routes (Routes.Scan skips it): auto-start, Source and .cv routes
// don't see it. When its start ends its spawn range is blocked, so the game doesn't send a second squad from it.
// One at a time; .cvdiag trialclean removes it with its squad. Nothing here runs unless the command is given.
static partial class Starter
{
    sealed class TrialCaravan
    {
        public Entity Clone;
        public string Key;
        public bool HasBoss;
        public PrefabGUID Boss;
        public bool Ally;
        public readonly HashSet<(int, int)> BossesDone = new();
        public Entity Cart;                 // the cart once seen
        public float3 CartPos;              // where it last was on the map
        public DateTime CartLostAt;         // default: not lost
        public bool LootDone;
        public DateTime NextContainerLook;
        public readonly HashSet<(int, int)> OldContainers = new();   // containers that were there before the cart was lost
    }

    static TrialCaravan _trial;
    static readonly List<(PrefabGUID Item, int Amount, string Name)> TrialLootList = new();
    static readonly List<string> TrialNotes = new();

    internal static bool HasTrial => _trial != null;
    public static bool IsTrial(Entity patrol) => _trial != null && _trial.Clone == patrol;

    static void TrialNote(string text)
    {
        Plugin.Info($"CarriageTracker trial: {text}");
        TrialNotes.Add($"{DateTime.Now:HH:mm:ss} {text}");
        if (TrialNotes.Count > 80) TrialNotes.RemoveRange(0, TrialNotes.Count - 80);
    }

    public static List<string> TrialInfo(EntityManager em)
    {
        var lines = new List<string>();
        var t = _trial;
        if (t == null) lines.Add("No trial caravan.");
        else
        {
            bool exists = em.Exists(t.Clone);
            var pos = exists ? Caravans.PositionOf(em, t.Clone) : default;
            lines.Add($"Trial caravan of {t.Key}: patrol {t.Clone.Index}:{t.Clone.Version}{(exists ? $" at ({Math.Round(pos.x)}, {Math.Round(pos.z)})" : " (gone)")}, " +
                      $"{(IsRunning(t.Clone) ? "start running" : "start over")}, " +
                      (exists ? $"{Routes.ActiveUnits(em, t.Clone).Count} units, {Routes.Describe(em, t.Clone)}" : ""));
        }
        lines.Add(TrialLootList.Count == 0 ? "Loot to put into its container: none (the game's loot stays)."
                                           : "Loot to put into its container: " + string.Join(", ", TrialLootList.ConvertAll(l => $"{l.Amount} x {l.Name}")));
        int from = Math.Max(0, TrialNotes.Count - 8);
        for (int i = from; i < TrialNotes.Count; i++) lines.Add(TrialNotes[i]);
        return lines;
    }

    public static string TrialLoot(PrefabGUID item, int amount, string name)
    {
        TrialLootList.Add((item, Math.Clamp(amount, 1, 100000), name));
        return $"The trial caravan's container will get {TrialLootList.Count} kinds of items instead of the game's loot.";
    }

    public static string TrialLootClear()
    {
        TrialLootList.Clear();
        return "The trial caravan's container keeps the game's loot.";
    }

    // ---- making the copy ----

    // boss: the unit that replaces the first guard of every group, null for the route's own squad.
    // stats: its UnitBaseStatsType (Trash, Normal, Elite, Boss; the game's roaming V Blood are Elite or Boss), "" to
    // keep the replaced guard's. ally: it gets the animal's team and faction when it appears.
    // stopsFrom: the route whose stops the copy walks, null for the source's own. keepTag: the copy keeps the
    // game's StoreSubSceneEntity tag (which the mod assumes marks what the game saves with the scene).
    public static List<string> MakeTrial(EntityManager em, PrefabCollectionSystem prefabs, Routes.Route source, Entity user,
                                         PrefabGUID? boss, string stats, bool ally, Routes.Route stopsFrom, bool keepTag)
    {
        var said = new List<string>();
        GameCheck.Ensure();
        if (!GameCheck.StartsOk) { said.Add($"Starts are off: this game version lacks {GameCheck.Missing}."); return said; }
        var missing = GameCheck.TrialMissing(boss.HasValue, stopsFrom != null);
        if (missing.Count > 0) { said.Add("Not made, this game version lacks: " + string.Join(", ", missing)); return said; }
        if (Paused) { said.Add("The mod is paused (.cv cleanup); restart the server first."); return said; }
        if (_trial != null) { said.Add("There is a trial caravan already. Remove it first: .cvdiag trialclean"); return said; }
        if (IsRunning(source.Patrol)) { said.Add("That route is being started by the mod right now; try again when its caravan is on the road."); return said; }

        var trial = new TrialCaravan { Key = source.Key, HasBoss = boss.HasValue, Boss = boss ?? default, Ally = ally };
        try
        {
            var clone = em.Instantiate(source.Patrol);
            trial.Clone = clone;
            _trial = trial;   // from here Routes skips it and trialclean removes it, whatever fails below

            // The copy must own nothing of the source's living squad: a start destroys the units a patrol lists.
            int listed = em.HasBuffer<UnitCompositionActiveUnit>(clone) ? em.GetBuffer<UnitCompositionActiveUnit>(clone).Length : 0;
            if (listed > 0) em.GetBuffer<UnitCompositionActiveUnit>(clone).Clear();
            int followers = ClearBufferNamed(em, clone, "FollowerBuffer");
            ClearBufferNamed(em, clone, "WaypointTargetBufferEntry");
            if (Routes.ActiveUnits(em, clone).Count > 0) throw new InvalidOperationException("the copy still lists the source's units");

            // Its squad appears at the copy, and it waits for a squad.
            object sp = em.GetComponentData<UnitCompositionSpawner>(clone);
            var positionSource = sp.GetType().GetField("PositionSource");
            bool own = positionSource.GetValue(sp) is Entity at && at == source.Patrol;
            if (own) positionSource.SetValue(sp, clone);
            var state = sp.GetType().GetField("SpawnerState");
            state.SetValue(sp, Enum.Parse(state.FieldType, "WaitingForSpawn"));
            em.SetComponentData(clone, (UnitCompositionSpawner)sp);

            object gp = em.GetComponentData<GlobalPatrolState>(clone);
            gp.GetType().GetField("PathRequestEntity").SetValue(gp, Entity.Null);
            em.SetComponentData(clone, (GlobalPatrolState)gp);

            string walks = "its own stops";
            if (stopsFrom != null)
            {
                int n = TakeStops(em, stopsFrom.Patrol, clone);
                walks = $"the {n} stops of {stopsFrom.Key}, from where that route's patrol stands";
            }

            string squad = "the route's own squad";
            if (boss.HasValue)
            {
                int n = PutBoss(em, prefabs, clone, boss.Value, stats);
                prefabs._PrefabLookupMap.TryGetName(boss.Value, out string bossName);
                squad = $"{bossName} in place of the first guard in {n} groups{(stats.Length > 0 ? $", stats {stats}" : "")}" +
                        $"{(ally ? ", given the animal's team and faction" : ", left in its own faction")}, its own drop removed";
            }

            string tag = "kept the scene tag";
            if (!keepTag)
            {
                var ct = Tag("StoreSubSceneEntity");
                if (ct.HasValue && em.HasComponent(clone, ct.Value)) { em.RemoveComponent(clone, ct.Value); tag = "scene tag removed"; }
                else tag = "had no scene tag to remove";
            }

            var pos = Caravans.PositionOf(em, clone);
            TrialNote($"copy of {source.Key} patrol {source.Patrol.Index}:{source.Patrol.Version} made: patrol {clone.Index}:{clone.Version} at " +
                      $"({Math.Round(pos.x)}, {Math.Round(pos.z)}), walks {walks}, {squad}; {tag}; " +
                      $"{listed} units and {Math.Max(0, followers)} followers of the source taken off its lists" +
                      (own ? "" : "; the source's squad appears somewhere other than at its patrol, left as it is"));

            bool started = TryStart(em, new Routes.Route(clone, source.Key), user, out var text);
            said.Add(text.ToString());
            if (!started)
            {
                TrialNote("the copy wasn't started, it is removed");
                said.AddRange(RemoveTrial(em));
                return said;
            }
            said.Add($"Trial caravan: patrol {clone.Index}:{clone.Version}, walks {walks}, {squad}; {tag}. " +
                     "Watch it with .cv l and .cvdiag trialinfo; remove it with .cvdiag trialclean.");
        }
        catch (Exception e)
        {
            Plugin.Error($"CarriageTracker trial: not made: {e}");
            said.Add($"Trial caravan not made: {(e.InnerException ?? e).Message}");
            if (_trial == trial)
            {
                try { said.AddRange(RemoveTrial(em)); }
                catch (Exception e2) { said.Add($"...and the copy can't be removed: {e2.Message}. Try .cvdiag trialclean."); }
            }
        }
        return said;
    }

    // The copy takes another route's stops and walks them the way that route does (order and direction come with its
    // GlobalPatrolState). It starts where that route's patrol stands, heading for the route's first stop.
    static int TakeStops(EntityManager em, Entity from, Entity clone)
    {
        int n = CopyBufferNamed(em, from, clone, "PatrolBusStopNode");
        object gp = em.GetComponentData<GlobalPatrolState>(from);
        var type = gp.GetType();
        type.GetField("PathRequestEntity").SetValue(gp, Entity.Null);
        object start = type.GetField("StartBusStopIndex").GetValue(gp);
        type.GetField("CurrentBusStopIndex").SetValue(gp, start);
        type.GetField("TargetBusStopIndex").SetValue(gp, start);
        em.SetComponentData(clone, (GlobalPatrolState)gp);

        object mp = em.GetComponentData<MovePatrolState>(clone);
        var status = mp.GetType().GetField("Status");
        status.SetValue(mp, Enum.Parse(status.FieldType, "WaitingForPath"));
        em.SetComponentData(clone, (MovePatrolState)mp);

        var pos = Caravans.PositionOf(em, from);
        if (Caravans.Has<Translation>(em, clone)) em.SetComponentData(clone, new Translation { Value = pos });
        if (Caravans.Has<LocalTransform>(em, clone))
        {
            var lt = em.GetComponentData<LocalTransform>(clone);
            lt.Position = pos;
            em.SetComponentData(clone, lt);
        }
        return n;
    }

    // The boss replaces the first guard of every group (StopLogic.GuardSlots). It is an ordinary unit to the
    // spawner (IsVBloodUnit = false): the owner wants a strong enemy, not a V Blood.
    static int PutBoss(EntityManager em, PrefabCollectionSystem prefabs, Entity patrol, PrefabGUID boss, string stats)
    {
        var groups = new List<(int, int)>();
        var groupBuffer = em.GetBuffer<UnitCompositionGroupEntry>(patrol);
        for (int i = 0; i < groupBuffer.Length; i++) groups.Add(((int)groupBuffer[i].UnitsStartIndex, (int)groupBuffer[i].UnitsCount));

        var units = em.GetBuffer<UnitCompositionGroupUnitEntry>(patrol);
        var isAnimal = new List<bool>();
        for (int i = 0; i < units.Length; i++)
        {
            object unit = units[i].Unit;
            isAnimal.Add(unit is PrefabGUID guid && Caravans.Classify(prefabs, guid).Kind == Kind.Horse);
        }
        var slots = StopLogic.GuardSlots(groups, isAnimal);
        if (slots.Count == 0) throw new InvalidOperationException("the squad has no guard to replace");
        foreach (int i in slots)
        {
            object entry = units[i];
            var type = entry.GetType();
            type.GetField("Unit").SetValue(entry, boss);
            type.GetField("IsVBloodUnit").SetValue(entry, false);
            if (stats.Length > 0)
            {
                var f = type.GetField("UnitBaseStatsType");
                f.SetValue(entry, Enum.Parse(f.FieldType, stats, true));
            }
            // Taken out and put back in: the buffer's indexer gives a copy, not the element itself.
            units.RemoveAt(i);
            units.Insert(i, (UnitCompositionGroupUnitEntry)entry);
        }
        return slots.Count;
    }

    // ---- once a second while the copy exists ----

    static void TrialSweep(World world, EntityManager em, PrefabCollectionSystem prefabs, DateTime now)
    {
        var t = _trial;
        if (t == null || prefabs == null) return;
        if (!em.Exists(t.Clone))
        {
            TrialNote($"patrol {t.Clone.Index}:{t.Clone.Version} is gone (not removed by the mod)");
            _trial = null;
            return;
        }
        var units = Routes.ActiveUnits(em, t.Clone);

        if (t.HasBoss)
            foreach (var u in units)
            {
                if (!Caravans.Has<PrefabGUID>(em, u) || em.GetComponentData<PrefabGUID>(u).GuidHash != t.Boss.GuidHash) continue;
                if (!t.BossesDone.Add(Key(u))) continue;
                TreatBoss(em, prefabs, t, u, AnimalOf(em, prefabs, units));
            }

        // The cart: where it is while it lives, and the moment it is gone.
        if (t.Cart == Entity.Null)
        {
            var carts = Routes.CartsOf(em, units);
            if (carts.Count > 0) t.Cart = carts[0];
        }
        if (t.Cart != Entity.Null && t.CartLostAt == default)
        {
            if (em.Exists(t.Cart))
            {
                var p = Caravans.PositionOf(em, t.Cart);
                t.CartPos = Caravans.OnMap(p) ? p : Caravans.PositionOf(em, t.Clone);
                // Containers that are there already (of earlier caravans) aren't this cart's.
                if (TrialLootList.Count > 0 && now >= t.NextContainerLook)
                {
                    t.NextContainerLook = now.AddSeconds(10);
                    t.OldContainers.Clear();
                    foreach (var f in Caravans.FastScan(em, prefabs, out _))
                        if (f.Kind == Kind.Container) t.OldContainers.Add(Key(f.Entity));
                }
            }
            else
            {
                t.CartLostAt = now;
                TrialNote($"the cart is gone at ({Math.Round(t.CartPos.x)}, {Math.Round(t.CartPos.z)})" +
                          (TrialLootList.Count > 0 ? "; looking for its container every second" : ""));
            }
        }

        if (TrialLootList.Count > 0 && !t.LootDone && t.CartLostAt != default)
        {
            double waited = (now - t.CartLostAt).TotalSeconds;
            Entity container = Entity.Null;
            float best = 20f;
            foreach (var f in Caravans.FastScan(em, prefabs, out _))
            {
                if (f.Kind != Kind.Container || t.OldContainers.Contains(Key(f.Entity))) continue;
                float d = Caravans.Dist2(f.Pos, t.CartPos);
                if (d < best) { best = d; container = f.Entity; }
            }
            if (container != Entity.Null)
            {
                t.LootDone = true;
                try { ReplaceLoot(world, em, prefabs, container, waited); }
                catch (Exception e) { TrialNote($"the container's loot wasn't replaced: {(e.InnerException ?? e).Message}"); }
            }
            else if (waited > 30)
            {
                t.LootDone = true;
                TrialNote("no new container within 20 m of the cart in 30 s (prisoner carts leave none); loot not replaced");
            }
        }
    }

    // The boss when it appears: its own drop is taken off, and with "ally" it gets the animal's team and faction.
    static void TreatBoss(EntityManager em, PrefabCollectionSystem prefabs, TrialCaravan t, Entity boss, Entity animal)
    {
        var done = new List<string>();
        try
        {
            if (em.HasBuffer<DropTableBuffer>(boss))
            {
                var drops = em.GetBuffer<DropTableBuffer>(boss);
                done.Add($"{drops.Length} drop tables removed");
                drops.Clear();
            }
            if (t.Ally && animal != Entity.Null)
            {
                if (Caravans.Has<Team>(em, boss) && Caravans.Has<Team>(em, animal))
                {
                    em.SetComponentData(boss, em.GetComponentData<Team>(animal));
                    done.Add("team of the animal");
                }
                if (Caravans.Has<TeamReference>(em, boss) && Caravans.Has<TeamReference>(em, animal))
                {
                    em.SetComponentData(boss, em.GetComponentData<TeamReference>(animal));
                    done.Add("team reference of the animal");
                }
                if (Caravans.Has<FactionReference>(em, boss) && Caravans.Has<FactionReference>(em, animal))
                {
                    em.SetComponentData(boss, em.GetComponentData<FactionReference>(animal));
                    done.Add("faction of the animal");
                }
            }
            else if (t.Ally) done.Add("no animal in the squad to take the team from");
        }
        catch (Exception e)
        {
            done.Add($"error: {(e.InnerException ?? e).Message}");
        }
        var pos = Caravans.PositionOf(em, boss);
        int level = Caravans.Has<UnitLevel>(em, boss) ? em.GetComponentData<UnitLevel>(boss).Level._Value : 0;
        TrialNote($"boss {boss.Index}:{boss.Version} appeared at ({Math.Round(pos.x)}, {Math.Round(pos.z)}), level {level}: " +
                  (done.Count > 0 ? string.Join(", ", done) : "nothing changed"));
    }

    static string Contents(EntityManager em, PrefabCollectionSystem prefabs, Entity inventory)
    {
        var parts = new List<string>();
        var slots = em.GetBuffer<InventoryBuffer>(inventory);
        for (int i = 0; i < slots.Length; i++)
        {
            if (slots[i].ItemType.GuidHash == 0 || slots[i].Amount <= 0) continue;
            prefabs._PrefabLookupMap.TryGetName(slots[i].ItemType, out string name);
            parts.Add($"{slots[i].Amount} x {name ?? slots[i].ItemType.GuidHash.ToString()}");
        }
        return parts.Count == 0 ? "empty" : string.Join(", ", parts);
    }

    // Everything the game put into the container is taken out and the trial's loot is put in.
    static void ReplaceLoot(World world, EntityManager em, PrefabCollectionSystem prefabs, Entity container, double waited)
    {
        if (!InventoryUtilities.TryGetInventoryEntity(em, container, out Entity inventory) || !em.HasBuffer<InventoryBuffer>(inventory))
        {
            TrialNote($"container {container.Index}:{container.Version} found {waited:0.#} s after the cart was gone, but its inventory can't be read");
            return;
        }
        string before = Contents(em, prefabs, inventory);
        var had = new List<(PrefabGUID Item, int Amount)>();
        var slots = em.GetBuffer<InventoryBuffer>(inventory);
        for (int i = 0; i < slots.Length; i++)
            if (slots[i].ItemType.GuidHash != 0 && slots[i].Amount > 0) had.Add((slots[i].ItemType, slots[i].Amount));
        int removed = 0;
        foreach (var (item, amount) in had)
            if (InventoryUtilitiesServer.TryRemoveItem(em, inventory, item, amount)) removed++;

        var game = world.GetExistingSystemManaged<ServerScriptMapper>().GetServerGameManager();
        foreach (var (item, amount, _) in TrialLootList) game.TryAddInventoryItem(container, item, amount);

        TrialNote($"container {container.Index}:{container.Version} found {waited:0.#} s after the cart was gone. " +
                  $"Was: {before}. Taken out: {removed} of {had.Count} stacks. Now: {Contents(em, prefabs, inventory)}");
    }

    // ---- removing it ----

    // The copy's start is ended, its squad and cart are destroyed, then the copy itself. With no copy known (after
    // a server restart) it looks for copies the game saved: carriage patrols without the scene tag, while nearly
    // all others have it.
    public static List<string> RemoveTrial(EntityManager em)
    {
        var said = new List<string>();
        var t = _trial;
        if (t != null)
        {
            _trial = null;
            said.Add(Destroy(em, t.Clone, t.Cart, $"trial caravan of {t.Key}"));
            return said;
        }

        var world = Caravans.ServerWorld();
        var prefabs = world == null ? null : Caravans.Prefabs(world);
        var tag = Tag("StoreSubSceneEntity");
        if (prefabs == null || !tag.HasValue) { said.Add("No trial caravan, and leftovers can't be looked for (no scene tag type)."); return said; }
        var routes = Routes.All(em, prefabs);
        var without = routes.FindAll(r => !em.HasComponent(r.Patrol, tag.Value));
        if (without.Count == 0) { said.Add($"No trial caravan and no leftover copy: all {routes.Count} carriage routes have the scene tag."); return said; }
        if (without.Count > 2 || routes.Count - without.Count < 10)
        {
            said.Add($"{without.Count} of {routes.Count} carriage routes have no scene tag: that doesn't look like leftover copies, nothing removed. See .cvdiag trialfind.");
            return said;
        }
        foreach (var r in without) said.Add(Destroy(em, r.Patrol, Entity.Null, $"leftover copy of {r.Key}"));
        Routes.Forget();
        return said;
    }

    static string Destroy(EntityManager em, Entity patrol, Entity cart, string what)
    {
        if (!em.Exists(patrol)) return $"The {what} (patrol {patrol.Index}:{patrol.Version}) is gone already.";
        var run = Runs.Find(r => r.Patrol == patrol);
        if (run != null)
        {
            try { Finish(em, run, "trial removed"); }
            catch (Exception e) { Plugin.Error($"CarriageTracker trial: its start can't be finished: {e.Message}"); }
            Runs.Remove(run);
        }
        var units = Routes.ActiveUnits(em, patrol);
        var carts = Routes.CartsOf(em, units);
        if (cart != Entity.Null && em.Exists(cart) && !carts.Contains(cart)) carts.Add(cart);
        foreach (var e in carts) DestroyUtility.Destroy(em, e);
        foreach (var e in units) DestroyUtility.Destroy(em, e);
        Doomed.Kill(em, patrol, DateTime.UtcNow);
        string text = $"The {what} (patrol {patrol.Index}:{patrol.Version}) is removed with {units.Count} units and {carts.Count} carts.";
        TrialNote(text);
        return text;
    }

    // When the copy's start ends, the game must not send another squad from it: its range is blocked whatever
    // Source says (ApplySource doesn't see the copy).
    static void BlockTrial(EntityManager em, Entity patrol)
    {
        if (!IsTrial(patrol) || !em.Exists(patrol)) return;
        var sp = em.GetComponentData<UnitCompositionSpawner>(patrol);
        if (Caravans.Field(sp, "PlayerRange") is float2 range) SetRangeY(em, patrol, sp, range.x, SpawnRange.BlockedY);
    }

    // Every carriage patrol with what tells a copy from a game route: the scene tag.
    public static List<string> TrialFind(EntityManager em, PrefabCollectionSystem prefabs)
    {
        var lines = new List<string>();
        var tag = Tag("StoreSubSceneEntity");
        var all = new List<Routes.Route>(Routes.All(em, prefabs));
        if (_trial != null && em.Exists(_trial.Clone)) all.Add(new Routes.Route(_trial.Clone, _trial.Key));
        int without = 0;
        foreach (var r in all)
        {
            bool tagged = tag.HasValue && em.HasComponent(r.Patrol, tag.Value);
            if (!tagged) without++;
            var pos = Caravans.PositionOf(em, r.Patrol);
            lines.Add($"{r.Key}{(r.Side != null ? " " + r.Side : "")}: patrol {r.Patrol.Index}:{r.Patrol.Version} at ({Math.Round(pos.x)}, {Math.Round(pos.z)}), " +
                      $"{(tagged ? "scene tag" : "NO scene tag")}{(IsTrial(r.Patrol) ? ", the trial caravan" : "")}, {Routes.Describe(em, r.Patrol)}");
        }
        lines.Insert(0, $"Carriage patrols: {all.Count}, without the scene tag: {without}{(tag.HasValue ? "" : " (the tag type wasn't found)")}. " +
                        "The game has 15; more, or one without the tag that isn't the trial caravan, is a copy the game kept.");
        return lines;
    }

    // ---- buffers whose type is known only by name ----

    static MethodInfo _getBufferDef;

    static object BufferByType(EntityManager em, Entity e, Type elem)
    {
        if (_getBufferDef == null)
            foreach (var m in typeof(EntityManager).GetMethods())
                if (m.Name == "GetBuffer" && m.IsGenericMethodDefinition && m.GetParameters().Length == 2) { _getBufferDef = m; break; }
        if (_getBufferDef == null) throw new InvalidOperationException("EntityManager.GetBuffer not found");
        return _getBufferDef.MakeGenericMethod(elem).Invoke(em, new object[] { e, false });
    }

    static int LengthOf(object buffer) => (int)buffer.GetType().GetProperty("Length").GetValue(buffer);

    // How many elements were taken off; -1 if the entity has no such buffer.
    static int ClearBufferNamed(EntityManager em, Entity e, string name)
    {
        var t = GameTypes.Find(name);
        if (t == null || !em.HasComponent(e, ComponentType.ReadOnly(Il2CppType.From(t)))) return -1;
        object buffer = BufferByType(em, e, t);
        int n = LengthOf(buffer);
        if (n > 0) buffer.GetType().GetMethod("Clear", Type.EmptyTypes).Invoke(buffer, null);
        return n;
    }

    static int CopyBufferNamed(EntityManager em, Entity from, Entity to, string name)
    {
        var t = GameTypes.Find(name);
        if (t == null) throw new InvalidOperationException($"game type {name} not found");
        var ct = ComponentType.ReadOnly(Il2CppType.From(t));
        if (!em.HasComponent(from, ct) || !em.HasComponent(to, ct)) throw new InvalidOperationException($"a patrol has no {name}");
        object source = BufferByType(em, from, t);
        var get = source.GetType().GetMethod("get_Item");
        var items = new List<object>();
        int n = LengthOf(source);
        for (int i = 0; i < n; i++) items.Add(get.Invoke(source, new object[] { i }));
        if (items.Count == 0) throw new InvalidOperationException($"the route to take stops from has none");

        object target = BufferByType(em, to, t);
        target.GetType().GetMethod("Clear", Type.EmptyTypes).Invoke(target, null);
        var add = target.GetType().GetMethod("Add", new[] { t });
        foreach (var item in items) add.Invoke(target, new[] { item });
        return items.Count;
    }
}
