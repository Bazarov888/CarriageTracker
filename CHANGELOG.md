# Changelog

## 1.0.25

The memory watch says more. On 14.11 it caught the jump (game heap 67 to 1393 MB in 40 seconds, no new objects in the world, nothing in the log, the mod's own work 3 ms) before the server was killed, but not who took the memory. Nothing changes for players.

- The first 6 lines of an incident are written at every 10-second sample (before: one per 30 seconds, so only two lines made it).
- New in each line: how many times the game's collector ran in the interval and its mode; Unity's own numbers (native memory, the collector's heap); players online; the newest save file with its size and age, if the saves are in `save-data/Saves` under the server folder (a save being written at that moment shows); the longest time between two server frames.
- Once per incident, at a line written with the game heap 300 MB over its quiet size, the watch runs one full collection and writes how much it freed. Freed: the memory was garbage made faster than the game collects it. Not freed by that one collection: most likely something holds it, but that answer is the weaker one. If the collector didn't run at all, the line says so. This is the only thing the watch does besides reporting.

## 1.0.24

A memory watch, after the server was killed for memory twice on 12.11 with nobody online (the cause isn't known yet), and developer commands that try out what the mod's own caravans will need. Nothing changes for players.

- Memory watch. Every 10 seconds the mod looks at the game heap, the process's memory and how many lines the game and all plugins wrote to the log. When the heap grows by 50 MB within a minute, the process by 100 MB within 10 seconds or 300 MB within a minute, or the log gets 500 lines in 10 seconds, it writes one line at once: memory now and before, how many objects the world holds compared with a quiet moment, who wrote to the log and the first warning or error of each, how many frames the server made and how long the mod's own work took. The line also goes to `BepInEx/CarriageTracker-memory.txt`, which is written through right away, so it is still there after the server was killed. At most one line per 30 seconds and 20 per incident (an incident ends after 10 quiet minutes or an hour; the game heap is counted apart from the rest); nothing in the first 5 minutes after the world is loaded; the file stops at 1 MB with one warning. The watch only reports: it doesn't find the cause by itself and doesn't stop anything.
- The 10-minute memory line also shows how many objects the world holds (all, from prefabs, networked).
- New developer commands (`EnableDiagnostics = true`), not tried on a server before this release:
  - `.cvdiag trial <route number> [boss prefab or -] [ally,elite,stops=<route number>,keeptag]` starts one trial caravan as a copy of a route's patrol, optionally with a strong unit (a Blood Soul, `CHAR_..._GateBoss_Minor`) in place of the first guard and with another route's stops. `.cvdiag trialloot <item prefab or clear> [amount]` sets what replaces the loot of its container, `.cvdiag trialinfo` tells what happened to it, `.cvdiag trialclean` removes it whole, `.cvdiag trialfind` writes every carriage patrol to a file. If the game kept a copy in its save (more than its 15 carriage routes after a restart; the log warns), `.cvdiag trialclean <index:version>` removes the patrol named, nothing is removed by guessing. `.cv cleanup` and unloading the mod remove the trial caravan too. Players see it like any started caravan (announcement, map icon, `.cv l`), so it is best tried while nobody else is online; it is not in `.cv r`, and auto-start and `Source` don't touch it.
  - `.cvdiag stops` writes every stop of every patrol route with the 160 m square of the map it is in, and the exact place where a patrol was seen standing at it.

## 1.0.23

Tools for finding out how the game builds routes and squads (for the mod's own caravans later), and one fix found with them. Nothing changes for players.

- The values the mod writes back as a route's normal ones (spawn range, respawn timers) are now learned from the game: the range from the game's other patrols, which the mod never touches (all 189 had 90 m on 12.11), the respawn timers from what most carriage routes have. Since 1.0.19 they were meant to come from the route's prefab, but a route has no prefab (patrol dump of 12.11), so the mod's constants (90 m, 1000-2700 s) were used every time without a word in the log. The constants still stand in until both are learned; the log says once what was learned, or that it wasn't yet.
- `.cvdiag dump` reads buffers element by element. Before, a buffer (a patrol's stops, squad and groups) was read as one value and the file showed made-up numbers for it.
- `.cvdiag patrols` writes whole squads (up to 40 units, 10 groups, 30 active units; before 12, 6 and 6).
- New developer commands (`EnableDiagnostics = true`): `.cvdiag prefabs <text> [full]` writes every game prefab whose name contains the text, with its number, unit level and the components that tell what it is; with `full` the first 60 with all their data (drop tables, squads). `.cvdiag types <text> [objects per type]` writes every game component type whose name contains the text, how many objects have it and, for each object, where it is and what the component holds (`BusStop`: the stops of patrol routes). Both need at least 3 letters and stop at a fixed size (8000 prefabs; 60 types, 30000 lines). They read game data in ways the mod doesn't use elsewhere, so they are best run while nobody is in a fight.

## 1.0.22

Fixes from a code review of 11.11.

- `.cv l`, `.cv m`, `.cv stats`, start replies and the last results in `.cv auto` are now always in the reader's language. Before, the caravan notes and stats were made at the check in the language of whoever caused it (usually the server's), so a player could get a line half in their language, half in another.
- The two MilitiaCotton patrols keep their numbers: routes with the same key are ordered west to east (where their patrols stand at that moment, about 600 m apart). Before, their order could change between `.cv routes` and `.cv start N` when freeze tags came and went, so a start could take the other Cotton.
- A route whose settings couldn't be given back after a start is repaired first when it is started next, and not started if that fails; auto-start skips it for an hour. A start of a route that still has a start's spawn range or zero respawn timers saves the route's normal values instead, so they can't stay for good.
- A failing announcement no longer stops the check (before, the same caravan was announced again every 30 seconds), and `.cv stop` / `.cv cleanup` finish every start even if one of them fails.
- `.cv lang` says when the choice couldn't be saved to the file yet. A wrong `Icon` in the config is warned about once, not at every check.

## 1.0.21

After the architecture and memory audit of 07.11 (Nikolai). No change to what players see; the server log and the memory line say more, and the mod makes less garbage.

- Objects the mod hands to the game's destroy pipeline and the game doesn't remove are no longer invisible. The memory line shows how many are waiting, how many are older than a minute and how many were given up. One still there after 5 minutes is destroyed once more; one still there after 10 minutes is given up with one warning for all such objects of that check (before: one warning per object, then it was counted nowhere).
- The memory line shows why icons and markers were removed since the last line (`moved`, `unwanted`, `gone`, `kind`, `duplicate`), so it is clear how many network objects the mod makes and removes and what for. The icon count no longer includes the game's own merchant icons on its markers.
- The server world, the prefab system, component types and the entity queries of every check are looked up once and kept instead of being made again on every check (and every frame while a start runs). This removes a steady stream of IL2CPP wrappers that only the .NET finalizer frees and that made the game heap on the memory graph look like a slow leak.
- The decisions about icons and markers (which to keep, which to remove and why, which markers are the mod's, when a marker is made anew, when a destroyed object is tried again) moved to `Logic/IconLogic.cs` and are covered by 18 new tests (two of them check every combination against the 1.0.20 conditions) built on the cases of 02.11, 05.11 and 07.11.
- A marker removed by someone else (the game, another mod, an admin) is forgotten by the mod at the next check.
- An error that repeats is written in full once per 10 minutes; the repeats in between are counted and the count goes into the next full line and the memory line. Before, an error at every check wrote a full stack trace every 30 seconds.
- Failing to write a spawner field a start can't be undone without (spawn range, respawn timers) stops that write instead of writing back a half-changed spawner. `Source = Mod` and `.cv cleanup` go on with the other routes when one route fails.
- New developer commands (`EnableDiagnostics = true`): `.cvdiag markers` lists every map marker of the kind the mod uses, grouped by what spawned it, its icon and whether the mod takes it as its own, and every icon of the mod's kinds grouped by what it hangs on, to check that the mod never takes a game merchant marker for its own. `.cvdiag movemarker` moves the nearest of the mod's markers to you, to check whether a moved marker shows in the new place on the map (then markers could be moved instead of made anew).

## 1.0.20

- README rewritten for server owners installing the mod for the first time: what it does, installation with a mod manager or by hand, what the defaults do and which settings to change, commands, acts and routes, every setting, compatibility, uninstalling.
- The second announcement line names the direction first (`North-west, 234 m.`) and uses the color name `white` instead of `#FFFFFF` for the default text color. On 07.11 a player's chat filter masked `FFFFFF>234` of `<color=#FFFFFF>234 m`, and the broken tag showed up as text.
- A loot container the mod kept longer is removed about 60 seconds after it is emptied (plus up to one check), as the game would have it. Until now an empty container stayed for the whole `LootKeepMinutes`.

## 1.0.19

After an outside code audit (07.11):

- A start that fails midway no longer leaves its route broken. A start removes the old squad and writes the start settings into the route step by step; an error in between used to leave the route with an endless spawn range and zero respawn timers until the server restarted. Now the route gets its normal settings back at once (if that fails too, every 5 seconds), auto-start skips it for an hour, and the player sees that the start failed. Auto-start also counts a start only when it really happened (before, a refused start still counted as this act's start).
- The values the mod writes back as a route's normal ones (spawn range, respawn timers) come from the route's own prefab. The mod's constants (90 m, 1000-2700 s) are used only when the prefab's values can't be read or look wrong, with a warning. A game update that changes the normal values no longer gets the old ones written back.
- At the first check after the server start the mod checks every game field and type it uses by name. If one that a start can't be undone without is missing (after a game update), starting caravans and `Source = Mod` are turned off with one clear error in the log, and routes blocked by `Source = Mod` are opened again so the game spawns its caravans; tracking, icons and announcements go on. Before, such a field only gave a warning and the mod went on working halfway.
- Icons on carriages are removed through the game's destroy pipeline with the same list as map markers (1.0.17), so an icon queued for removal isn't counted or removed again on the next check.
- Chat messages longer than a chat line holds (509 bytes; a Russian letter is 2) are cut on a whole character and not inside a color tag, with a warning in the log. Before, a long welcome line from the config could be cut inside a tag.
- New setting `Chat.ShowOthersActs` (default `true`, as before): `.cv act` lists the act of every player online; `false` shows that list only to admins, for PvP servers.
- GitHub Actions are pinned by commit SHA, and `tools/deploy.sh` pins the .NET SDK image by digest like the git image. The README says why the mod targets .NET 6 and that it is out of support.

## 1.0.18

Follow-up to 1.0.17 after a second independent review (no blocking issues found):

- The sweep of stale icons (icons left on a map marker that no longer exists) removes only the mod's own icon kind: the current `Icon` and `OldIcons`. In 1.0.17 it also took the named presets (merchant, unknown, vblood) that the mod wasn't using.
- An object the mod handed to the game's destroy pipeline stays skipped until the game has actually removed it (in 1.0.17: for 10 seconds). One the game still hasn't removed a minute later is written to the log once.
- Comments and ARCHITECTURE.md say where icons on markers are touched (only the sweep), and that in `Mode = proxy` a moving carriage's marker is still made anew at nearly every check, without the leak.

## 1.0.17

- Fixed rows of stale carriage icons on the map and a memory leak behind them. The icon of a frozen carriage hangs on a separate map marker, which the mod made anew whenever the carriage's route had moved 10 m, so almost at every check. The old markers and their icons were destroyed with `EntityManager.DestroyEntity`, past the game's destroy pipeline: players kept seeing the old icons, and the game's records of these networked objects stayed in the server's memory. On 05.11 the server was killed for memory (6.3 GB limit). Now:
  - markers and their icons are destroyed through the game's `DestroyUtility`, as units and carts already were;
  - a frozen carriage's marker is made anew only after 100 m and at most every 3 minutes (a moving one's in `Mode = proxy` after 30 m);
  - at every check, icons of the mod's kinds left on a marker that no longer exists are removed, also those left by earlier versions;
  - a marker whose icon request the game has emptied is still recognised as the mod's after a restart.
- Once every 10 minutes the log has a line `CarriageTracker memory:` with the server process's memory, the game's and the plugins' heaps, and the mod's icons and markers: how many exist and how many were made and removed since the previous line. It shows whether memory grows with the mod's work or apart from it.
- Reviewed for other leaks (every query and native array, static collections, event entities, per-frame code): none found.

## 1.0.16

- Removed the setting `Start.AnimalKeepsDamage` (1.0.14) and its code. The game heals the animal to full every second while it is hitched to its cart, the mod set it back once a second, and in game its health bar jumped up and down all the time, which looked like a bug. The animal now heals as in the game. The spawn buff fix of 1.0.11 stays: the animal takes damage. The line `AnimalKeepsDamage` left in an existing config file does nothing.
- Every loot container of a carriage on the map is written to the log when it appears (empty or with loot, and when the game would remove it), when it is emptied and when it is gone, with how long it stood. On 05.11 a robbed carriage left no line about its container at all.

## 1.0.15

- Fixed a doubled carriage. Players attacked a started squad before it had finished coming out, and with the start's zero respawn timers the game sent 126 replacement units in 54 seconds, among them a second animal with its own cart. The mod removed the units but not that cart, so a second cart stood on the road. Now the route gets its own timers back as soon as a member of the squad is killed, and a cart of a removed unit is removed with it.
- The loot container of a robbed carriage stays 10 minutes after it appeared while it still has loot (new setting `Map.LootKeepMinutes`, 0 = the game's own timers). The game removes it 60 seconds after nobody is within 20 m and 60 seconds after something was taken (read with `.cvdiag comps` on 04.11), so a robber who died lost it.
- The log of `AnimalKeepsDamage` tells only big heals (5% of the animal's health or more at once) and counts its regeneration (about 1 point a second) in the line of the finished run. Before, its first five lines all went to regeneration ticks.
- `tools/deploy.sh` stops at once with a clear message when the server path has no `BepInEx/core`. A mistyped path made Docker create empty folders there and the build fail with hundreds of errors.

## 1.0.14

- The animal of a carriage started by the mod no longer heals. In a fight it breaks off from its cart, comes back, hitches up again and got full health from the game; now its health is set back to the lowest it had. New setting `Start.AnimalKeepsDamage`, on by default. The log tells each time (the first five per carriage) and whether the animal was hitched to its cart at that moment.

## 1.0.13

- `.cvdiag comps` also shows the values of the components that remove an object by time (`DestroyAfterDuration` and its kin, `LifeTime`), in chat and in the server log. A loot container vanished about a minute after its cart was destroyed while no player was near (03.11); its timers come next.

## 1.0.12

- A route's act is now its region's (Farbane I, Dunley II, Silverlight and Oakveil IV) instead of its guards' level. The Draculin prison carriage of Dunley (`ChurchLegion`, vampires guarded by the Church of Luminance) is an act II carriage now; by its guards' level it landed outside act II, with the Silverlight routes (in the 1.0.10 log act II had only the four militia routes). A route of an unknown region still goes by its guards' level.

## 1.0.11

- The mod is now called CarriageTracker, after the game's own name for caravans: `CarriageTracker.dll` in `BepInEx/plugins/CarriageTracker/`, plugin `CarriageTracker`, repository [Bazarov888/CarriageTracker](https://github.com/Bazarov888/CarriageTracker). `Bazarov.CaravanTracker.cfg` and the players' languages file move to the new names on the first start. While the old `CaravanTracker.dll` is still installed, the new mod writes an error to the log and does nothing; `tools/deploy.sh` removes the old folder. Commands stay `.cv`. English chat says "carriage".
- Fixed the animal of a carriage that took no damage, also after its cart was destroyed. It happened to squads of routes woken at their last stop: the game's spawn buff `Buff_General_Spawn_Unit` stayed on the animal. Such a buff still on a unit or cart of a start 10 seconds after it appeared is now removed, and the log says so.
- A player's act moves on as soon as the strongest V Blood of their act is drunk (Quincey, Octavian, Cyril). Before, a player who had beaten act II still got act II carriages until they drank an act III V Blood.
- Act III has no carriages in the game (none in Gloomrot and the Cursed Forest). A player in an act without routes now gets the next act up, so act III players get the Silverlight and Oakveil carriages. New setting `EmptyAct`: `Higher` (default), `Lower` (as before) or `Both`.
- New setting `PlayerAct`: `VBlood` (default), `Level` (by the character's level; with Bloodcraft leveling that is the Bloodcraft level) or `Highest`.
- `RouteActs` takes regions (`Farbane`, `Dunley`, `Silverlight`, `Oakveil`) and several acts per entry, e.g. `Silverlight=3+4,Oakveil=4`. `.cv r` shows each route's region; `.cv a` shows how the act was found and which routes you get.
- Route names follow the wiki: Venom Blades emery and venom sap routes, the Draculin prison carriage; the two `MilitiaCotton` routes are told apart as east and west.
- The two Oakveil routes have no end: they are no longer put back to the start and count as ready when a route is picked. The mod stops looking after a carriage there when its cart is destroyed, so what is left can freeze once nobody is near and the route is free again; before, the mod kept it awake and the route busy for up to `MaxRunMinutes`.
- A route woken at its last stop that doesn't go back to its first stop within 20 seconds is given up and skipped for an hour, instead of spawning its squad at the end of the route.
- A fight right at the start no longer removes the guards still on their way: a new unit after a loss is a replacement only once the squad has reached its size.
- Fixes from the code audit of 02.11:
  - Markers of other mods (KindredPortals) are no longer removed; the mod touches only its own markers.
  - The route's own settings count as restored only after they were written. A write that fails is tried again, also at the end of a start and at the server start.
  - Unloading the mod at runtime stops its work and gives the world its settings back, as `.cv cleanup` does.
  - `.cv l` and `.cv m` read the last check instead of running a search each time; they refresh it at most every 5 seconds.
  - With `GlobalIcons = false` new markers no longer cause a search on every frame for 10 seconds.
  - The two `MilitiaCotton` patrols keep their own start history and failures.
  - A failed read of the languages file no longer loses the players' choices; the file is written through a temporary file and only when something changed.
  - An object only the full search found stays in the list between full searches.
  - `tools/deploy.sh` stops on local changes in the repository, prints the commit it installs and replaces the DLL in one step.
- Releases: a new version merged into `main` gets a GitHub Release with `CarriageTracker.dll` and the Thunderstore package by itself. README's installation points there; the CI artifact, which needed a GitHub login, is no longer the way to get the mod.

## 1.0.10

- A route woken at its last stop now spawns its squad only after the game has finished removing the old one (spawner idle again) and the route has stood at its first stop for 3 seconds. In 1.0.8 the squad spawned one second after the reset: its animal took no damage, even after the cart was destroyed, and a squad that reached the end stayed there instead of being removed.
- An animal still immortal after its cart is lost is made mortal, so the rest of a robbed caravan can always be killed. The log says when this happens.
- A started squad that stands at the last stop for 2 minutes is removed by the mod (the game removes one there within seconds). Before, it stood there until `MaxRunMinutes` and took a place in the auto-start limits.
- The log shows the animal's state (immortal flag and buffs) when the squad is assembled and when its cart is lost, and the spawner state when a woken route is armed.

## 1.0.9

- Config descriptions of `RoutePick` and `ResetToStart` match how picking and putting back work since 1.0.8.
- The Thunderstore package links to the GitHub repository.
- README and ARCHITECTURE brought up to date for review: installation before the Thunderstore release, what happens to a route whose squad was killed, facts seen in game on 01.11.

## 1.0.8

- A route standing at its last stop is now woken before its squad may spawn, so the game first returns it to its first stop and the caravan sets out from there. Before, the squad appeared at the end of the route. If the route doesn't go back within 20 seconds, the squad spawns where it stands, as before, and the log says so.
- Auto-start takes routes at their first stop and routes at their last stop as one group, and doesn't pick the route it started last while another one is free. Before, a route that had just finished stood at its first stop again and was picked over and over, while routes at their last stop waited.
- A caravan in a fight keeps its map icon even when its animal is more than 30 m from the cart. Before, the icon vanished during the attack and the log said "cart left without its animal".
- With `Source = Mod`, routes standing at their last stop are blocked too, not only routes waiting for a new squad. A player walking near such a route wakes it, the game resets it and could spawn its own caravan before the next check.
- The log tells how far from where the route stood the squad appeared. For a route put back to the start (`ResetToStart`) it warns when the squad appeared where the route stood, i.e. the caravan first walks back to the first stop.
- Each player picks their own language with `.cv lang en` or `.cv lang ru` (no longer an admin command that switched it for everyone). Announcements, replies and start messages come in each player's language; `Language` in the config is the default for players who haven't picked one.
- A player who has just come into the world gets two lines, in English and Russian (or in their own language if they picked one): what the mod does and how to pick the language. Setting `Chat.Welcome`, on by default; `Chat.WelcomeEn` / `Chat.WelcomeRu` replace the text with the server's own.
- Long replies (`.cv ss` with several started caravans, `.cv auto`, `.cv a`) are split into several lines. In Russian they could exceed the 512-byte chat limit and get cut or not arrive at all.

## 1.0.7

- Every decision of the mod (is a route free, which route to start, what to do with a started squad, why a caravan disappeared, what spawn range a route gets, what is announced) now lives in one place without game calls, and is covered by tests built from cases seen on the server. CI runs them before every build.
- The log no longer says "cart destroyed" about a caravan that reached the end of its route between two checks.
- Every log line of the mod starts with the server time. Start lines tell how many seconds the squad took to appear and to assemble, and how many minutes the run lasted.
- An auto-start log line tells which free routes there were (at the start, at the end, mid-way) and from which group the route was picked.
- `.cv ss` no longer says "cart destroyed" twice.

## 1.0.6

- `.cv cleanup` (admin): run it before removing the mod. It stops started caravans, gives every route the game's own spawn range, timers and freezing back, and removes the mod's icons and markers. Until the next restart the mod then changes nothing in the world.
- With `Source = Mod`, routes are checked for blocking every 5 seconds instead of every `CheckSeconds`, so the game has less time to spawn its own caravan next to a player.
- With `Source = Mod` and `IntervalMinutes = 0` no caravans appear at all; the server log at load and `.cv auto` now say so.
- New setting `RouteCommandsForAll` (section Chat, on by default): `false` makes `.cv r` and `.cv rm` admin-only, since they show where every route is.
- Map icons another mod puts on a caravan are no longer removed; the mod removes only icons of the kinds it uses.
- The version is written only in `CaravanTracker.csproj`; `MyPluginInfo` and the Thunderstore `manifest.json` are generated from it.
- `tools/deploy.sh` can't break when `git pull` updates the script itself, and runs a pinned `alpine/git` image.
- CI uses current versions of the GitHub actions.
- ARCHITECTURE.md describes how the mod works inside, for reviewers.

## 1.0.5

- Fixed a started caravan setting out without guards: the start settings now stay until the whole squad has appeared, not only the animal with the cart. With `Source = Mod` the squad on the road also gets the game's normal spawn range back instead of the blocked one.
- The server log tells how many units the squad of a started caravan has once it is assembled.
- With `Source = Mod`, a route whose start has ended is blocked at once, so the game can't spawn its own caravan there before the next check.
- A caravan is announced once: a cart that froze and woke up again, or was hidden for a moment, is no longer announced again.
- After a crash in the middle of a start, units and carts of the route get their freezing without players back on the next server start.
- An error in one started caravan no longer stops the others; a start that fails 10 times in a row is given up.

## 1.0.4

- Stops are shown the way players count them: "stop 1 of 7" at the start, "stop 7 of 7" at the last one.
- `.cv r` tells apart a route whose cart was destroyed and only the animal walks on.
- `.cv rm <number>`: map marker on the point of a route from `.cv r`.
- A cart at the last stop of its route, or a cart left standing without its animal, is no longer listed or shown on the map.
- A start clears the respawn time the game scheduled after the previous squad was destroyed, so the new squad doesn't wait up to 45 minutes for it.
- The server log shows the `Source` setting at load and every change made with `.cv source`.
- A route left mid-way by an earlier caravan is put back to the first stop of its path before the mod starts it, so the caravan drives the whole way. Setting `ResetToStart`, on by default.
- Auto-start takes routes at their last stop before routes left mid-way: the game returns those to the start by itself.
- A route whose start failed (no squad within `SpawnTimeoutMinutes`) is skipped by auto-start for an hour.
- The server log tells where the squad of a started caravan appeared, and warns if a route put back to the start set out from somewhere else.

## 1.0.2

- New setting `Source` and command `.cv source`: caravans only from the mod (the game no longer spawns them when a player comes near a route), only from the game (auto-start off), or both. With `Mod`, a warning goes to the log if the game still spawns a caravan on a blocked route.
- Auto-start prefers routes standing at the start of their path. A route left mid-way by an earlier caravan is used only when no route stands at the start, and then the one with the most stops left.
- `.cv l`, `.cv r` and `.cv ss` show how far along its path a caravan is. `.cv start` says when a route stands mid-way.
- A started caravan whose cart is destroyed no longer counts in the auto-start limits; the horse still walks to the end of the route, where the game removes it.
- When a start ends by timeout or `.cv stop`, units still alive may freeze without players again, as all units of the game do.
- A robbed caravan's horse frozen mid-route no longer keeps its route busy for auto-start.
- Safety limit `MaxSquadSize`: extra units of a squad started by the mod are removed at once.
- New settings: `IconOnLoot`, `IconOnFrozen`, `AnnounceTo`, `AnnounceRadius`, `AnnounceVanilla`, `RoutePick`, `ExcludedRoutes`, `SpawnTimeoutMinutes`, `MaxRunMinutes`, `MaxSquadSize`, `VerboseLog`.
- The server log records every caravan that disappears from the map, with its last state and what its route is doing, and lists the caravans found at server start. Routine lines moved behind `VerboseLog`.
- The route list is kept between checks instead of being collected from the world every time; `RouteActs` is parsed only when it changes.

## 1.0.1

- Fixed killed guards of a started caravan coming back over and over, which made the server run out of memory: replacements are removed, and the respawn time computed during the start is cleared.
- Routes left with start settings after a crash get their normal settings back on server start.
- A started caravan is checked once a second instead of every frame.
- Starting a route that stands at its last stop no longer ends right away: the mod waits until the game clears the old squad and returns the route to the start.
- A cart standing at the last stop of its route is no longer announced as setting out.
- Removed the horse icon preset: the game shows that icon only to the owner of the mount.

## 1.0.0

First public release.

- Caravans, frozen caravans and loot containers on the map, visible to every player on the whole map.
- Two-line chat announcement with distance, direction and the marker command for each player.
- Auto-start by act: per-act interval, MaxPerAct and MaxTotal limits, AllOnlineActs or Leader scope.
- Player act by V Blood drunk, route act by guard level, manual override with RouteActs.
- English and Russian chat messages.
- Map icon presets: merchant, unknown, vblood, or any icon number.
- Developer commands moved behind EnableDiagnostics.
