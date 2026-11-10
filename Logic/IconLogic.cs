using System;

namespace CarriageTracker;

// Decisions about the mod's map icons and markers, without game types (audit 07.11 by Nikolai, A1). Icons and
// Proxies read the facts from the world and carry out what is decided here; the tests check the decisions
// on the cases that went wrong on the server.
//
// Icon kinds, from the mod's side:
//   Current - the icon set by Icon now;
//   Earlier - an icon the mod used before (OldIcons);
//   Preset  - a named preset of Icon that is neither of the above (the setting may have been changed by hand);
//   Other   - anything else: another mod's or the game's.
public enum IconKind { Other, Current, Earlier, Preset }

public enum IconAct { Ignore, Keep, Drop }

// Why an icon or marker is removed, for the memory line (R2: how many network objects the mod makes and removes,
// and for what).
public enum DropWhy { None, Gone, Unwanted, Kind, Moved, Duplicate, Orphan }

// An icon that hangs on an object directly (MapIconTargetEntity), not on a marker of its own.
// TargetIsCaravan: the target exists and is a cart, horse or loot container. OnMarker: the icon is attached to a
// marker object (it has Attach): such icons belong to markers and are left to IconLogic.ForMarker / Orphan.
// Covered: this caravan already has a kept icon in this check.
public record IconFacts(IconKind Kind, bool TargetExists, bool TargetIsCaravan, bool OnMarker, bool Wanted, bool Covered);

// A map marker object (MapIcon_ProxyObject_POI_Unknown) with the icon it requests or carries.
// MadeHere: the mod made it since the server start. OneOfOurs: exactly one icon, of a kind of the mod (any but Other).
// Spawner: what its SpawnedBy points to. CurrentKind: that one icon is of the current kind.
// Moved: its caravan has moved far from it (MovedFar). Covered: the caravan already has a kept marker in this check.
public enum Spawner { None, Gone, Caravan, Other }

public record MarkerFacts(bool MadeHere, bool OneOfOurs, Spawner Spawner, bool Wanted, bool CurrentKind, bool Moved, bool Covered);

public static class IconLogic
{
    public static IconKind KindOf(int guid, int current, System.Collections.Generic.ICollection<int> earlier,
                                  System.Collections.Generic.ICollection<int> presets)
    {
        if (guid == current) return IconKind.Current;
        if (earlier.Contains(guid)) return IconKind.Earlier;
        if (presets.Contains(guid)) return IconKind.Preset;
        return IconKind.Other;
    }

    // An icon on a caravan stays if it is of the current kind, its caravan is wanted and has no other icon yet.
    // Any other icon of the mod's kinds on a caravan goes. Off a caravan only an icon of the current or an earlier
    // kind whose object is gone is removed, and never one on a marker: the game's own merchant icons hang on
    // markers, and a merchant icon is the default Icon (audit 07.11).
    public static (IconAct Act, DropWhy Why) ForIcon(IconFacts i)
    {
        if (i.TargetIsCaravan)
        {
            if (i.Kind == IconKind.Other) return (IconAct.Ignore, DropWhy.None);
            if (i.Kind != IconKind.Current) return (IconAct.Drop, DropWhy.Kind);
            if (!i.Wanted) return (IconAct.Drop, DropWhy.Unwanted);
            if (i.Covered) return (IconAct.Drop, DropWhy.Duplicate);
            return (IconAct.Keep, DropWhy.None);
        }
        bool ours = i.Kind is IconKind.Current or IconKind.Earlier;
        if (!ours || i.TargetExists || i.OnMarker) return (IconAct.Ignore, DropWhy.None);
        return (IconAct.Drop, DropWhy.Gone);
    }

    // A marker is the mod's if it made it since the server start; after a restart (that list isn't saved and
    // entity numbers change) if it has one icon of the mod's kinds and was spawned by a caravan object or by nothing
    // that still exists. KindredPortals makes the same markers spawned by a player's character: never the mod's
    // (audit 02.11).
    public static bool Owned(MarkerFacts m) =>
        m.MadeHere || (m.OneOfOurs && m.Spawner is Spawner.None or Spawner.Gone or Spawner.Caravan);

    // One of the mod's markers stays if its caravan still exists and is wanted, the icon is of the current kind, the
    // caravan hasn't moved far from it and has no other marker yet. Any other of the mod's markers goes.
    public static (IconAct Act, DropWhy Why) ForMarker(MarkerFacts m)
    {
        if (!Owned(m)) return (IconAct.Ignore, DropWhy.None);
        if (m.Spawner is Spawner.None or Spawner.Gone) return (IconAct.Drop, DropWhy.Gone);
        if (m.Spawner != Spawner.Caravan || !m.Wanted) return (IconAct.Drop, DropWhy.Unwanted);
        if (!m.CurrentKind) return (IconAct.Drop, DropWhy.Kind);
        if (m.Moved) return (IconAct.Drop, DropWhy.Moved);
        if (m.Covered) return (IconAct.Drop, DropWhy.Duplicate);
        return (IconAct.Keep, DropWhy.None);
    }

    // An icon of the mod's kinds attached to a marker that no longer exists: nothing else would ever remove it.
    public static bool Orphan(IconKind kind, bool parentExists) =>
        kind is IconKind.Current or IconKind.Earlier && !parentExists;

    public const float MoveMoving = 30f;   // proxy mode: a moving caravan's marker follows it in 30 m steps
    public const float MoveFrozen = 100f;  // a frozen caravan's marker: 100 m steps
    public static readonly TimeSpan FrozenMinAge = TimeSpan.FromMinutes(3);

    // Whether a marker is to be made anew where its caravan is now. A frozen caravan's marker: only after 100 m and
    // not before it is 3 minutes old; a marker left from before a restart has no known age and goes by distance.
    // Until 1.0.16 it was every 10 m, a new marker at almost every check, and the old ones piled up (05.11).
    public static bool MovedFar(bool frozen, float dist, TimeSpan? age)
    {
        if (!frozen) return dist >= MoveMoving;
        if (dist < MoveFrozen) return false;
        return age == null || age.Value >= FrozenMinAge;
    }
}

// Objects the mod handed to the game's destroy pipeline (DestroyUtility) that the game hasn't removed yet
// (audit 07.11 by Nikolai, R1). Stuck: older than a minute, counted in the memory line. After 5 minutes the mod
// destroys it once more; if it is still there 10 minutes after the first try it is given up: one warning for all
// such objects of that check, and it stays skipped and counted, so a pile-up shows in the memory line.
public enum DoomStep { Wait, Retry, GiveUp }

public static class DoomLogic
{
    public static readonly TimeSpan Stuck = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(10);

    public static DoomStep Step(TimeSpan age, bool retried, bool givenUp)
    {
        if (givenUp) return DoomStep.Wait;
        if (!retried) return age >= RetryAfter ? DoomStep.Retry : DoomStep.Wait;
        return age >= GiveUpAfter ? DoomStep.GiveUp : DoomStep.Wait;
    }
}
