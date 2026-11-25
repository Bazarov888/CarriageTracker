using System;
using System.Collections.Generic;
using ProjectM;
using ProjectM.Shared;
using Stunlock.Core;
using Unity.Entities;

namespace CarriageTracker;

// The caravan's animal (horse or ox). On 01.11 and 02.11 the animal of squads spawned right after a wake took no
// damage, even after its cart was destroyed; squads that set out from their first stop could be killed. The log of
// 1.0.10 showed why: Immortal was false, but the game's spawn buff Buff_General_Spawn_Unit, which lasts a few
// seconds, was still on that animal at assembly and after the cart was lost, and only on woken starts. So a spawn
// buff left on any unit or cart of a start SpawnBuffMax after it appeared is removed (RemoveSpawnBuffs). The
// animal's state is still written to the log at assembly and when the cart is lost, and an animal still immortal
// after losing its cart is made mortal: the rest of a robbed caravan must be killable.
static partial class Starter
{
    static Entity AnimalOf(EntityManager em, PrefabCollectionSystem prefabs, List<Entity> units)
    {
        if (prefabs == null) return Entity.Null;
        foreach (var u in units)
            if (Caravans.Has<PrefabGUID>(em, u) && Caravans.Classify(prefabs, em.GetComponentData<PrefabGUID>(u)).Kind == Kind.Horse)
                return u;
        return Entity.Null;
    }

    static bool IsImmortal(EntityManager em, Entity unit) =>
        Caravans.Has<Immortal>(em, unit) && em.GetComponentData<Immortal>(unit).IsImmortal;

    // "immortal=False, buffs: A, B" for the log.
    static string AnimalState(EntityManager em, PrefabCollectionSystem prefabs, Entity unit)
    {
        var names = new List<string>();
        if (em.HasBuffer<BuffBuffer>(unit))
        {
            var buffs = em.GetBuffer<BuffBuffer>(unit);
            for (int i = 0; i < buffs.Length && names.Count < 12; i++)
            {
                var guid = buffs[i].PrefabGuid;
                names.Add(prefabs != null && prefabs._PrefabLookupMap.TryGetName(guid, out string n) ? n : guid.GuidHash.ToString());
            }
        }
        return $"immortal={IsImmortal(em, unit)}, buffs: {(names.Count == 0 ? "none" : string.Join(", ", names))}";
    }

    // Removes the game's spawn buffs (Buff_General_Spawn_Unit and its kin) from a unit or cart of a start that
    // appeared long enough ago (RunLogic.PastSpawn). Written to the log once per unit.
    static void RemoveSpawnBuffs(EntityManager em, PrefabCollectionSystem prefabs, Run run, Entity unit, DateTime now)
    {
        if (prefabs == null || !em.Exists(unit) || !em.HasBuffer<BuffBuffer>(unit)) return;
        var buffs = em.GetBuffer<BuffBuffer>(unit);
        var drop = new List<(Entity Buff, string Name)>();
        for (int i = 0; i < buffs.Length; i++)
        {
            if (!prefabs._PrefabLookupMap.TryGetName(buffs[i].PrefabGuid, out string name) || name == null) continue;
            if (name.StartsWith("Buff_General_Spawn_", StringComparison.Ordinal)) drop.Add((buffs[i].Entity, name));
        }
        foreach (var (buff, name) in drop)
        {
            if (buff == Entity.Null || !em.Exists(buff)) continue;
            DestroyUtility.Destroy(em, buff, DestroyDebugReason.TryRemoveBuff);
            if (!run.BuffsRemoved.Add(Key(unit))) continue;
            string what = Caravans.Has<PrefabGUID>(em, unit) && prefabs._PrefabLookupMap.TryGetName(em.GetComponentData<PrefabGUID>(unit), out string n) ? n : "unit";
            // Seen on every start of every route (12.11): the game's way, not a fault; Info since 1.0.26.
            Plugin.Info($"CarriageTracker start: {Id(run)}: {name} was still on {what} {unit.Index}:{unit.Version} " +
                        $"{Math.Round((now - run.State.SpawnedAt).TotalSeconds)} s after the squad appeared; removed it");
        }
    }

    static void LogAnimal(EntityManager em, PrefabCollectionSystem prefabs, Run run, List<Entity> units, string when)
    {
        var animal = AnimalOf(em, prefabs, units);
        if (animal == Entity.Null) return;
        Plugin.Info($"CarriageTracker start: {Id(run)}: animal {when}: {AnimalState(em, prefabs, animal)}");
    }

    // After its cart is gone the animal must be killable.
    static void MakeAnimalMortal(EntityManager em, PrefabCollectionSystem prefabs, Run run, List<Entity> units)
    {
        var animal = AnimalOf(em, prefabs, units);
        if (animal == Entity.Null || !IsImmortal(em, animal)) return;
        var immortal = em.GetComponentData<Immortal>(animal);
        immortal.IsImmortal = false;
        em.SetComponentData(animal, immortal);
        Plugin.Warn($"CarriageTracker start: {Id(run)}: the animal was still immortal after losing its cart; made it mortal");
    }
}
