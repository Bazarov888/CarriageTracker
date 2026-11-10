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

// Source = Mod: keeping the game from spawning caravans by itself.
static partial class Starter
{
    // Source = Mod: the game must not spawn a caravan by itself when a player comes near an empty route.
    // What range each route gets is decided by SpawnRange.SourceTarget; routes the mod is starting are left
    // alone (a start sets its own range). If a squad still appears on a blocked route, the block doesn't work
    // on this game version: a warning goes to the log once per route.
    static readonly HashSet<(int, int)> BlockedRoutes = new();
    static readonly HashSet<(int, int)> WarnedRoutes = new();

    static void SetRangeY(EntityManager em, Entity patrol, UnitCompositionSpawner sp, float x, float y)
    {
        object boxed = sp;
        SetField(boxed, "PlayerRange", new float2(x, y));
        em.SetComponentData(patrol, (UnitCompositionSpawner)boxed);
    }

    // Source = Mod: right after a start ends, an idle route (SpawnRange.Idle) is blocked at once,
    // not at the next check, so that the game can't spawn its own caravan there in between.
    static void BlockIfWaiting(EntityManager em, Entity patrol)
    {
        if (Paused || !GameCheck.StartsOk || !Settings.ModOnly || !em.Exists(patrol)) return;
        var sp = em.GetComponentData<UnitCompositionSpawner>(patrol);
        if (!SpawnRange.Idle(sp.SpawnerState.ToString())) return;
        if (Caravans.Field(sp, "PlayerRange") is not float2 range) return;
        if (SpawnRange.SourceTarget(true, true, range.x, range.y) is not float y) return;
        // ApplySource tries again within 5 seconds.
        try
        {
            SetRangeY(em, patrol, sp, range.x, y);
            BlockedRoutes.Add(Key(patrol));
        }
        catch (Exception e) { Plugin.Warn($"CarriageTracker: route of patrol {patrol.Index}:{patrol.Version} not blocked right away: {e.Message}"); }
    }

    public static int ApplySource(EntityManager em, PrefabCollectionSystem prefabs)
    {
        GameCheck.Ensure();
        if (Paused) return 0;
        // A game version the mod can't start caravans on (GameCheck) gets every blocked route opened: the game
        // spawns caravans by itself then.
        bool modOnly = Settings.ModOnly && GameCheck.StartsOk;
        int blocked = 0, opened = 0;
        foreach (var r in Routes.All(em, prefabs))
        {
            var key = Key(r.Patrol);
            if (IsRunning(r.Patrol) || !em.Exists(r.Patrol)) { BlockedRoutes.Remove(key); continue; }
            var sp = em.GetComponentData<UnitCompositionSpawner>(r.Patrol);
            if (Caravans.Field(sp, "PlayerRange") is not float2 range) continue;
            bool waiting = SpawnRange.Idle(sp.SpawnerState.ToString());
            if (modOnly && !waiting && BlockedRoutes.Contains(key) && Routes.ActiveUnits(em, r.Patrol).Count > 0 && WarnedRoutes.Add(key))
                Plugin.Warn($"CarriageTracker: route {r.Key}: Source = Mod, but the game spawned a caravan on it by itself " +
                                         $"(range {range.x}-{range.y}). The block doesn't work here; switch back with .cv source both and report it.");
            if (modOnly && waiting) BlockedRoutes.Add(key); else BlockedRoutes.Remove(key);
            if (SpawnRange.SourceTarget(modOnly, waiting, range.x, range.y, NormalOf(em, r.Patrol).RangeY) is not float y) continue;
            // One route that can't be written must not stop the others.
            try { SetRangeY(em, r.Patrol, sp, range.x, y); }
            catch (Exception e) { Plugin.Fail($"CarriageTracker: route {r.Key} source", e); continue; }
            if (SpawnRange.IsBlocked(range.x, y)) blocked++; else opened++;
        }
        if (blocked > 0 || opened > 0)
            Plugin.Trace($"CarriageTracker: source {Settings.Source.Value}: game spawning turned off on {blocked} routes, back on for {opened}");
        return blocked;
    }
}
