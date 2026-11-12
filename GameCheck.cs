using System;
using System.Collections.Generic;
using ProjectM;
using ProjectM.Network;
using ProjectM.Shared;
using Stunlock.Core;
using Unity.Entities;
using Unity.Mathematics;

namespace CarriageTracker;

// The mod reads and writes some game fields and types by name (reflection), since the game's own C# wrappers
// don't expose them or their namespace isn't known in advance. A game update that renames one would make that
// part fail with a warning while the rest goes on: without PlayerRange, for instance, Source = Mod would stop
// blocking the game's own caravans unnoticed. So everything is checked once, at the first check after the
// server start (the game's assemblies are all loaded by then), with one clear line in the log:
// - Needed: without it a forced start can't be undone (spawner range and timers, the freeze tags). Starts,
//   auto-start and Source = Mod are then turned off, and the game spawns its caravans by itself.
// - Optional: only one feature goes (putting a route back to its first stop, stop counts).
static class GameCheck
{
    static bool _done;
    public static bool StartsOk { get; private set; } = true;
    public static string Missing { get; private set; } = "";

    public static void Ensure()
    {
        if (_done) return;
        _done = true;
        var needed = new List<string>();
        var optional = new List<string>();

        Field(typeof(UnitCompositionSpawner), "PlayerRange", typeof(float2), needed);
        foreach (var f in new[] { "RespawnTimeModifier", "MinRespawnTime", "MaxRespawnTime" })
            Field(typeof(UnitCompositionSpawner), f, null, needed, numeric: true);
        Field(typeof(UnitCompositionSpawner), "RespawnTime", null, needed);
        TypeByName("DisableWhenNoPlayersInRange", needed);
        TypeByName("DisabledDueToNoPlayersInRange", needed);

        Field(typeof(GlobalPatrolState), "CurrentBusStopIndex", null, optional, numeric: true);
        Field(typeof(GlobalPatrolState), "TargetBusStopIndex", null, optional, numeric: true);
        var status = typeof(MovePatrolState).GetField("Status");
        if (status == null || !status.FieldType.IsEnum || !Enum.IsDefined(status.FieldType, "WaitingForPath"))
            optional.Add("MovePatrolState.Status.WaitingForPath");
        TypeByName("PatrolBusStopNode", optional);

        if (needed.Count > 0)
        {
            StartsOk = false;
            Missing = string.Join(", ", needed);
            Plugin.Error($"CarriageTracker: this game version lacks {Missing}. Starting caravans (by command and auto-start) " +
                         "and Source = Mod are turned off, the game spawns caravans by itself; tracking, icons and announcements work. " +
                         "Report it with the game version.");
        }
        if (optional.Count > 0)
            Plugin.Warn($"CarriageTracker: this game version lacks {string.Join(", ", optional)}: " +
                        "routes left mid-way aren't put back to their first stop, and stop numbers may be unknown.");
        if (needed.Count == 0 && optional.Count == 0)
            Plugin.Info("CarriageTracker: game check passed, every game field and type the mod uses is there");
    }

    // What a trial caravan (.cvdiag trial, Starter.MakeTrial) reads and writes by name on top of the above. Checked
    // when one is asked for: a developer tool must not turn anything off for everyone or add to the start-up lines.
    public static List<string> TrialMissing(bool boss, bool stops)
    {
        var missing = new List<string>();
        Field(typeof(UnitCompositionSpawner), "PositionSource", typeof(Entity), missing);
        EnumValue(typeof(UnitCompositionSpawner), "SpawnerState", "WaitingForSpawn", missing);
        Field(typeof(GlobalPatrolState), "PathRequestEntity", typeof(Entity), missing);
        if (boss)
        {
            Field(typeof(UnitCompositionGroupUnitEntry), "Unit", typeof(PrefabGUID), missing);
            Field(typeof(UnitCompositionGroupUnitEntry), "IsVBloodUnit", typeof(bool), missing);
            var stats = typeof(UnitCompositionGroupUnitEntry).GetField("UnitBaseStatsType");
            if (stats == null || !stats.FieldType.IsEnum) missing.Add("UnitCompositionGroupUnitEntry.UnitBaseStatsType");
        }
        if (stops)
        {
            foreach (var f in new[] { "StartBusStopIndex", "CurrentBusStopIndex", "TargetBusStopIndex" })
                Field(typeof(GlobalPatrolState), f, null, missing, numeric: true);
            EnumValue(typeof(MovePatrolState), "Status", "WaitingForPath", missing);
            TypeByName("PatrolBusStopNode", missing);
        }
        return missing;
    }

    static void EnumValue(Type type, string name, string value, List<string> missing)
    {
        var f = type.GetField(name);
        if (f == null || !f.FieldType.IsEnum || !Enum.IsDefined(f.FieldType, value)) missing.Add($"{type.Name}.{name}.{value}");
    }

    static void Field(Type type, string name, Type expected, List<string> missing, bool numeric = false)
    {
        var f = type.GetField(name);
        bool ok = f != null && (expected == null || f.FieldType == expected)
                  && (!numeric || f.FieldType.IsPrimitive || f.FieldType.IsEnum);
        if (!ok) missing.Add($"{type.Name}.{name}");
    }

    static void TypeByName(string name, List<string> missing)
    {
        if (GameTypes.Find(name) == null) missing.Add(name);
    }
}
