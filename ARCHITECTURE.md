# How CarriageTracker works

A server-side BepInEx (IL2CPP) plugin for V Rising. Nothing is installed on clients: everything players see
(map icons, chat lines) is made of the game's own entities and messages. Until 1.0.11 it was called
CaravanTracker; inside, the game's carriages are still called caravans (`Caravans`, `Caravan`, `CaravanRules`).

This page is for reviewers. It describes the game mechanics the mod relies on, which of them were seen on a
live server and which are assumptions, and where in the code each part lives.

## Layers

The mod is split so that every decision can be tested without the game:

1. **Reading the game** (`Routes.Snap`, `Caravans.FastScan` and friends): turns entities into plain records
   (`RouteSnap`, `Progress`). No decisions here.
2. **Decisions** (`Logic/`): pure functions over those records. No game types, no Unity, no BepInEx.
   `RouteLogic` (is a route free, which one to start), `RunLogic` (a forced start second by second),
   `GoneLogic` (why a caravan disappeared), `SpawnRange` (what range and timers a route gets),
   `CaravanRules` (what is a caravan, what is announced), `ActLogic` (acts of players and routes, regions,
   `RouteActs`), `ChatLines` (lists that fit a chat message), `Welcome` (who gets the welcome lines),
   `LanguageStore` (the players' languages file).
3. **Acting on the game** (`Starter`, `StarterSource`, `StarterRepair`, `Icons`, `Tracker`): carry the decisions out.

`tests/` is a console program that runs the decision layer against cases taken from the server logs
(a caravan started mid-way, a caravan without guards, a cart left without its animal, a caravan that reached
the end between two checks, and so on). CI runs it before the build; a failing case fails the build.
A change of behaviour starts with a test case.

## Files

| File | What it does |
|---|---|
| `Logic/*.cs` | The decision layer (above). |
| `tests/` | Tests of the decision layer: `dotnet run --project tests`. |
| `Plugin.cs` | Entry point. `Ticker` runs every server frame and schedules all work (see Timing). |
| `Settings.cs` | BepInEx config entries. |
| `Caravans.cs` | Finding carts, horses and loot containers in the world; reading game components by name. |
| `Routes.cs` | Caravan routes (game patrols): list, squad, carts, progress along the path, `Snap`, state text. |
| `Tracker.cs` | Every check: search, announcements in chat, map icons, log of caravans that disappeared. |
| `Icons.cs` | Map icons on caravans (`Icons`) and on separate markers (`Proxies`). |
| `AutoStart.cs` | Which route to start in which act, and when. |
| `Starter.cs` | A forced start of one route and looking after it until it ends. |
| `StarterSource.cs` | `Source = Mod`: keeping the game from spawning caravans by itself. |
| `StarterRepair.cs` | Giving routes the game's settings back after a crash, and `.cv cleanup`; each route's normal values (`NormalOf`). |
| `GameCheck.cs` | Once per server start: are all game fields and types the mod uses by name there. |
| `StarterAnimal.cs` | The caravan's animal: its state in the log, mortal again once its cart is lost; leftover spawn buffs removed from the squad. |
| `Acts.cs` | Act of a player (by V Blood drunk or by level, `PlayerAct`) and of a route (by its region, `RouteActs`, or guard level for an unknown route). |
| `Commands.cs` | Chat commands (VampireCommandFramework). |
| `Loc.cs` | Chat texts in English and Russian; `L` answers in the language in use for the current player. |
| `Languages.cs` | Each player's own language (`BepInEx/config/CarriageTracker.languages.txt`, kept by `LanguageStore`) and the VCF middleware that switches to it for every command of this mod. |
| `Diag.cs` | Developer commands `.cvdiag`, registered only with `EnableDiagnostics = true`. |

## Game model

A caravan route is a patrol entity with `GlobalPatrolState`, `MovePatrolState` and `UnitCompositionSpawner`.
Its squad is a horse `CHAR_CarriageHorse_<Route>`, guards, and a cart `CHAR_Carriage_<Route>` the horse pulls.
When the cart is destroyed its loot stays in a container `TM_CarriageContainer_<Route>`.

Observed on a live server (game build with BepInEx 6.0.0-be.733):

- The path is the `PatrolBusStopNode` buffer of the patrol. `CurrentBusStopIndex` is 0-based; `Count - 1` is the last stop.
- The game spawns a squad only when a player is within `PlayerRange` of the patrol.
- At the last stop the route goes to `WaitForDespawn`, the squad is removed and the route goes back to index 0.
- A squad comes out in parts: the horse with the cart first, the guards up to about a minute later.
- Units without players near get `DisabledDueToNoPlayersInRange` and are moved to about (10000, 10000).
  `DisableWhenNoPlayersInRange` is the tag that allows this.
- After a squad is destroyed the game schedules `RespawnTime` up to 45 minutes ahead.
- Killing a guard of a squad whose spawner timers are 0 makes the game send a replacement at once.
- A squad appears where the patrol entity stands, whatever its stop index. Starting a frozen route at its last stop
  with spawning allowed at once put the squad at the end of the route (31.10, MilitiaCotton, 0 m from the end point).
- A route the game finishes itself while its patrol is awake goes back to its first stop, position included
  (31.10, BanditCopper; MilitiaCotton four times: its first stop is at (-1677, -926), its last at (-1639, -896),
  and every start after a finished run found the patrol at the first stop).
- In a fight the route stops (`StoppedDueToUnit`) and the animal can be more than 30 m from its cart.
- A route whose whole squad was killed mid-way keeps moving while awake: MilitiaCotton was "Moving, units 0" at
  stop 3/4 after the fight and stood at its first stop by the next pick (01.11).
- Waking a route at its last stop makes the game return it to its first stop within a second (01.11, three
  times; MilitiaIron's first stop is 266 m from its last).
- Two different patrols can carry the same route key: MilitiaCotton has a 4-stop and a 7-stop route (01.11), the
  wiki's Western and Eastern Cotton. Start history and failures are kept by patrol, not by key; `Routes.Route.Side`
  names them east and west by where the patrols are (about 600 m apart).
- The two Oakveil patrols (Blackfang) have `PatrolType = Sequential`, every other caravan route `Sequential_Once`
  (dump of 30.10): their path has no end and the caravan circles until destroyed, as the wiki says.
  `Progress.Loop` carries this: such a route is never put back to its start, counts as standing at its start when
  a route is picked, and its run ends when the cart is lost, so what is left may freeze once no player is near.
  While players are near it the route stays busy (`RouteState.LoopLeftover`, they may still be fighting it);
  frozen, it is free and the next start removes it. Not seen in game yet: nobody has reached Oakveil on the server.
- The squad size is the largest group of the patrol's composition (`UnitCompositionGroupEntry.UnitsCount`; 8 for
  militia and bandit routes, three groups of 8 to pick from).
- On 01.11 and 02.11 the animal of squads of woken routes took no damage, also after its cart was destroyed, while
  squads that set out from their first stop could be killed. The log of 1.0.10 showed `Immortal.IsImmortal` false
  and the game's spawn buff `Buff_General_Spawn_Unit` still on that animal at assembly and after the cart was lost,
  only on woken starts. That buff lasts a few seconds when the game spawns a unit normally.
- Routes finished while no player was near stay frozen at their last stop (`WaitForDespawn`, no units) until
  something wakes them (dump of 30.10: most caravan patrols were in that state).

Not verified, treated as assumptions:

- `PlayerRange = (0, 0.001)` keeps the game from spawning (`Source = Mod`). The range is kept above 0 on purpose,
  in case 0 means "no limit". If a squad appears on a blocked route anyway, a warning goes to the log once per route.
  So far (31.10–01.11, several evenings of play next to routes) every caravan that set out was started by the mod.
- Writing `CurrentBusStopIndex` / `TargetBusStopIndex` and `MovePatrolState.Status = WaitingForPath` puts a route
  back to its first stop (`ResetToStart`). Given the facts above it most likely changes only the stop number: the
  squad appears where the route stood and walks to the first stop first. The log gives the distance and warns then.
- The leftover spawn buff is what keeps the animal from taking damage. Since 1.0.11 a `Buff_General_Spawn_*` buff
  still on a unit or cart of a start 10 s after it appeared is destroyed (`RunLogic.PastSpawn`,
  `Starter.RemoveSpawnBuffs`, the way KindredCommands removes buffs) and the log says so once per unit. The log
  still shows the animal's immortal flag and buffs at assembly, and the mod still makes the animal mortal once its
  cart is lost.
- Spawning a squad right after the game has reset a route (1 s after waking, 1.0.8) gave squads that were not
  removed at the last stop (01.11, three starts). Spawning only once the spawner is idle again and the route has
  stood at its first stop for 3 s is assumed to avoid it (1.0.10 armed about 4 s after waking every time).
- Spawner settings changed by the mod are kept in the server save. That is why `RepairLeftovers` runs on the first
  check after the server starts and why `.cv cleanup` exists.
- The values written back as "normal" (range, respawn timers and modifier) are each route's own, read from the
  route's prefab (`Starter.NormalOf`, by the patrol's `PrefabGUID`). The constants in `SpawnRange` (90 m,
  1000-2700 s) are used only when the prefab's values can't be read or look wrong, with a warning per prefab. Until
  1.0.18 the constants were always written (audit of 07.11).
- A start writes into the route step by step and is tracked only once it is in `Runs`. An error before that
  (`Starter.TryStart`) repairs the route at once (`RepairRoute`; if that fails too, it is queued and retried every
  5 s) and marks it failed, so auto-start skips it for an hour; auto-start counts a start only when it happened.
- Fields and types read by name (spawner range and timers, the freeze tags, stop indexes, `PatrolBusStopNode`)
  are checked once at the first check (`GameCheck`). If one a start can't be undone without is missing, starts,
  auto-start and `Source = Mod` are off (blocked routes are opened again) with one error in the log; tracking,
  icons and announcements go on.

## Timing

`Ticker.Update` runs every frame:

- `Starter.Update` while a start is running; the work itself once a second.
- `Starter.ApplySource` and `Starter.RetryRepairs` every 5 seconds.
- `Tracker.Tick` and `AutoStart.Tick` every `CheckSeconds` (30 by default).

Player commands don't run their own search: `.cv l` and `.cv m` read the list of the last check and refresh it
only if it is older than 5 s (`Tracker.Fresh`). Admin commands (`.cv refresh`, `.cv icon`, `.cv mode`) still
run a check at once.

All world access is on the main thread. Each step has its own try/catch, so an error in one doesn't stop the others.

Messages the mod sends itself (announcements, welcome lines, start results; `Chat.Send`) are cut to 509 bytes of
UTF-8 (`ChatText.Fit`, what `FixedString512Bytes` holds), on a whole character and not inside a rich text tag; a
cut text is logged once. Command replies (`ctx.Reply`) go through VampireCommandFramework and are packed into short
lines by the commands (`ChatLines.Pack`).

`Plugin.Unload` (a runtime unload) sets `Ticker.Stopped`, gives the world back what the mod changed the way
`.cv cleanup` does, destroys the `Ticker` and then removes the commands and the patch.

## A forced start (`Starter.Start`, `Starter.UpdateRun`)

1. A squad still on the route is destroyed together with its carts.
2. With `ResetToStart`, a route standing mid-way is put back to its first stop.
3. The spawner's own values are saved in the run. A route at its last stop gets a blocked range and is woken
   (`RunPhase.Waking`); once the game has returned it to its first stop and its spawner is idle for 3 s
   (`RunLogic.ReadyToArm`) it is armed like any other route: `PlayerRange = (0, 100000)`, zero respawn timers,
   no `RespawnTime`. If that doesn't happen within 20 s the start is given up and the route skipped for an hour
   (until 1.0.10 the squad was let spawn where the route stood).
4. The patrol loses `DisableWhenNoPlayersInRange` and is enabled.

Then once a second `RunLogic.Squad` and `RunLogic.Outcome` decide, and `UpdateRun` carries it out:

- The first unit seen (not at `WaitForDespawn`) marks the squad as spawned.
- Respawn guard: every unit that appears while the squad assembles is added to `Known`. Once a known unit is gone,
  a new unit is a replacement and is destroyed, unless `Known` is still smaller than the route's squad size: then
  it is a guard still on its way (a fight right at the start no longer strips the squad of its late guards).
  Units above `MaxSquadSize` are destroyed too, and a cart pulled by a destroyed unit with it.
- The start settings stay until the squad hasn't grown for 90 seconds (at most 4 minutes after the first unit), or
  until a member is killed (04.11: a fight right after the spawn made the game send 126 units in 54 s with zero timers),
  then `RestoreSpawner` puts the saved spawner values back. A blocked or zero range is restored as the game's (0, 90).
  `RunState.Restored` is set only after the write; until then `RunLogic.Squad` asks for it every second.
- A spawn buff left on a unit or cart 10 s after it appeared is removed (see Game model).
- Every unit and cart of the squad loses `DisableWhenNoPlayersInRange` and is woken up if the game froze it.
- Once the cart is lost, an animal the game still holds immortal is made mortal (`StarterAnimal.cs`).
- The run ends when no unit appeared within `SpawnTimeoutMinutes` (the route is then skipped by auto-start for
  an hour), when the squad is gone, when the squad has stood at the last stop for 2 minutes (the mod removes it),
  when the cart is lost on a route without an end, or after `MaxRunMinutes`. `Finish` restores the spawner, gives
  the freeze tag back to units still alive and, with `Source = Mod`, blocks the route again if it waits for a new
  squad. If the spawner can't be written, the route goes to a repair queue retried every 5 seconds.

Runs live only in memory. After a restart in the middle of a start, `RepairLeftovers` finds routes with start
settings (range above 1000, zero timers, no freeze tag) and gives them the game's values. A route that fails there
is queued like above; if the whole pass fails it is tried again on the next checks (10 times).

## Acts (`Acts`, `ActLogic`, `AutoStart`)

- A player's act, `PlayerAct = VBlood`: the act of the highest V Blood drunk, and the next act once the strongest
  V Blood of an act is drunk (Quincey, Octavian, Cyril; the game itself moves acts on by its journal). `Level`:
  `OfLevel` of the character's level (`Equipment.GetFullLevel`; Bloodcraft's leveling writes its own level there,
  without it this is the gear level). `Highest`: the higher of both.
- A route's acts: `RouteActs` for its key, else for its region (`ActLogic.Region`, after the wiki), else the act
  of its region (`ActLogic.RegionAct`: Farbane I, Dunley II, Silverlight and Oakveil IV), else, for a route the
  wiki doesn't know, `OfLevel` of its strongest guard. A route can have several acts (`Silverlight=3+4`). Until
  1.0.12 the guard level was the rule; it put the Draculin prison carriage of Dunley (`ChurchLegion`), whose
  guards are the same Church of Luminance units as in Silverlight, outside act II (the 1.0.10 log: act II had
  only the four militia routes).
- `ActLogic.Served`: a player act with routes gets its own; one without (act III: no carriages in Gloomrot and the
  Cursed Forest) gets the next one up, down or both (`EmptyAct`).
- Auto-start keeps its interval and `MaxPerAct` per route act. Start history (`LastRouteStart`, `FailedAt`) is per
  patrol.

## Source

- `Both`: the game and the mod start caravans.
- `Game`: auto-start is off; `.cv start` still works.
- `Mod`: `ApplySource` gives every idle route (`SpawnerState` `WaitingForSpawn` or `WaitingForDespawn`,
  `SpawnRange.Idle`) that isn't being started by the mod the blocked range, and every other route the game's range back. With `Mod` and
  `IntervalMinutes = 0` there are no caravans at all; the load log and `.cv auto` warn about it.

## Tracking and icons (`Tracker.Tick`)

- A fast search every check, a full pass over networked entities every `SafetyScanMinutes` as a safety net.
- A caravan is a cart on the map pulled by its horse (horse exists, on the map, within 30 m or the route is in a
  fight: `CaravanRules.Pulled`), or a container with loot.
  Carts at `WaitForDespawn` and carts no horse pulls are skipped.
- A cart is announced once, the first time it is seen on the map after the first check. Frozen carts are not announced.
- When a caravan disappears, `GoneLogic` names the reason from what the route does now. Checks are 30 s apart,
  so a caravan that reached the end is usually last seen one stop before it; the route back at its first stop
  with no units tells it apart from a robbed one, whose route stays where it was robbed.
- Icons: in `direct` mode the icon entity targets the cart; frozen carts and `proxy` mode use a marker entity
  that follows the caravan. `Icons.Sync` touches only icons the mod could have made: on a caravan, icons of the
  current kind, `OldIcons` or a named preset; off a caravan, only an icon of the current or an `OldIcons` kind
  whose object is gone. Icons on marker objects (`Attach`), where the game keeps its own icons, aren't touched there;
  only `Proxies.SweepOrphans` removes such an icon of the mod's own kinds whose marker no longer exists.
- Markers (`Proxies`) are the game's `MapIcon_ProxyObject_POI_Unknown`, which KindredPortals uses too (with
  `SpawnedBy` = a player's character and an icon of the player's choice). `Proxies.Owned` touches a marker only if
  the mod made it since the server start, or if it asks for one icon of the mod's kinds and was spawned by a caravan
  object or by something that no longer exists (after a restart; the session list isn't saved and entity numbers
  change). Until 1.0.10 every such marker not on a wanted caravan was removed (audit of 02.11). Left as a known
  edge: a KindredPortals marker with an icon of the mod's kinds whose character was deleted.
- A marker is made anew where its caravan is when the caravan has moved away: 30 m for a moving caravan (`proxy`
  mode), 100 m and not before the marker is 3 minutes old for a frozen one. Until 1.0.16 it was 10 m, and markers
  and their icons were destroyed with `EntityManager.DestroyEntity`, past the game's destroy pipeline; on 05.11
  players saw rows of stale carriage icons and the server was killed for memory. Markers and icons now go through
  `DestroyUtility`, objects handed to it are skipped until the game has removed them (it is deferred; one still
  there a minute later is logged), and at every check icons of the mod's own kinds (current and `OldIcons`) whose
  marker no longer exists are removed (`Proxies.SweepOrphans`).
- Every 10 minutes `Memory` writes the process's memory, the game and plugin heaps and the mod's icons and markers
  (how many exist, how many were made and removed) to the log.
- New markers' icons are made global as soon as the game creates them (`MapIconSpawnPatch`), each marker once;
  with `GlobalIcons = false` nothing is waited for.
- Loot containers with loot get the game's removal time (`DestroyAfterDuration.EndTime`) pushed out to
  `LootKeepMinutes` after they were first seen (`Loot.Keep`, `LootLogic`). The game's own timers, read on 04.11: 60 s,
  ten times slower with a player within 20 m, and 60 s after something was taken. Every container on the map,
  empty ones too, is written to the log when it appears, when it is emptied and when it is gone. A kept container that
  is emptied gets the game's 60 s back (`LootLogic.EmptiedEndTime`).
- An object only the full search found is remembered and looked at on every check after that, so it doesn't drop
  out of the list until the next full search.

## Chat

- A chat message is 512 bytes at most and VCF cuts replies at 509 characters; a Russian letter is 2 bytes.
  Lists go through `ChatLines.Pack`, which splits them into lines of at most 450 bytes.
- Language: `L` holds the language of the player being answered. `LanguageMiddleware` sets it before each
  command of this mod and resets it after; announcements and start messages set it per recipient;
  `Ticker.Update` resets it to the server default before background work.
- `Greeter` (every check) sends the welcome lines to players who came into the world since the previous
  check (`Welcome.Greet`); nobody is greeted at the first check after the server start.

## Removing the mod (`.cv cleanup`)

Stops all runs, opens blocked, zero or start ranges, restores zero timers, gives the freeze tag back to patrols,
units and carts, and makes the next `Tracker.Tick` remove every icon and marker of the mod. A `Paused` flag then
keeps `Start`, auto-start and `ApplySource` from changing the world until the server restarts.

## Things worth a careful look

- `ResetToStart` writes patrol state the game normally owns. If the game keeps other state about the path,
  the caravan could set out in a strange way. Logged on every start.
- The respawn guard destroys units the game spawns. If the game ever spawns guards later than the 90-second
  window, they count as squad members, not replacements; if it replaces a unit that was never seen, the guard misses it.
- The wake phase (`RunLogic.ReadyToArm`) relies on the game returning an awake route to its first stop. With
  1.0.10 it did so within about 4 s every time; a route that doesn't within 20 s is now given up.
- Removing the spawn buff is a fix by evidence from the log, not from the game's code: the log of the next woken
  starts shows whether the buff was found and whether the animal could be killed.
- `LanguageMiddleware` is added to VCF's global middleware list, so it sees every command of every mod; it acts
  only on commands declared in this assembly.
- Fields of `UnitCompositionSpawner` are set by reflection on a boxed copy (`SetField`); a renamed field only logs a warning.
- `Caravans.Field` / `GameTypes.Find` read some game types by name, because their namespace differs between game builds.

Announcements use the color name `white` for the default text color and put the direction before the distance:
on 07.11 a player's chat filter masked a hex color code followed by digits, and the broken tag showed as text.
