# CLAUDE.md

Context for Claude sessions working on this repository. Read ARCHITECTURE.md and the top of CHANGELOG.md first;
this file only says how the work is done and keeps the backlog.

## The mod

Server-side V Rising mod (BepInEx IL2CPP, .NET 6, VampireCommandFramework): carriages on the map for everyone,
chat announcements, automatic carriage starts per act, longer-lasting loot. MIT, author Bazarov, GUID
`CarriageTracker`, published on Thunderstore as Bazarov/CarriageTracker. Called CaravanTracker until 1.0.11.
It runs on the owner's own PvE server (Brutal, Bloodcraft and about 18 other mods, see the README's compatibility
table), which is where every fix is tested.

## Rules of the code

- Three layers (ARCHITECTURE.md): reading the game into plain records, decisions in `Logic/` without game types,
  carrying them out. A new decision goes into `Logic/` with a test in `tests/` taken from a real case.
- Every destroy goes through `DestroyUtility` and the `Doomed` list, never `EntityManager.DestroyEntity`
  (05.11: markers destroyed past the game's pipeline piled up and the server was killed for memory).
- Map markers are networked objects. Make as few as possible; never touch markers or icons that aren't the mod's
  (`IconLogic.Owned`; KindredPortals uses the same marker prefab, the default icon is the game's merchant icon).
- Component types through `Ct<T>.RO / .RW`; per-check queries through `Queries.Get` and never disposed; the world
  through `Caravans.ServerWorld()`.
- Anything read or written by reflection is listed in `GameCheck`; a field a start can't be undone without is
  required (`SetField` throws).

## Build, test, release

- The version lives only in `CarriageTracker.csproj`. Every set of code changes gets a new version and a CHANGELOG
  entry; docs-only changes keep the version (release.yml skips a version that already has a release).
- `dotnet run --project tests` runs the logic tests anywhere. The mod itself needs NuGet (game reference
  assemblies); in a cloud session NuGet is blocked, so it is built by CI (build.yml, on push and PR).
- Branch `vX.Y.Z` → PR (in a cloud session through `gh api repos/Bazarov888/CarriageTracker/pulls`, GraphQL is not
  available) → the owner merges → release.yml makes the GitHub Release and calls thunderstore.yml.
- Before every PR an independent review by a subagent that hasn't seen the work; its findings are fixed before the
  merge. README, ARCHITECTURE.md and CHANGELOG.md are updated in the same PR.
- On the server: `tools/deploy.sh <server folder>`, restart, then check the log for the `loaded` and `game check`
  lines and, 10 minutes later, the `CarriageTracker memory:` line (RAM, markers, destroy queue).

## Working with the owner

- Short, plain answers; a simple question gets a simple answer without a round of tool calls.
- Server instructions: goal, numbered full commands, expected result of each. Never "as before".
- Never ask for passwords or tokens in chat.
- Design first, then code. Important fixes right away, but no stream of small versions: each one is a server
  restart.
- Search or read the code before claiming something. No overclaiming in README or player-facing texts.
- No config backups.

## Backlog

Good remarks not done yet. Add new ones here with the date and where they came from; remove them when done.

- R2, audit 07.11 (Nikolai): move markers instead of making them anew, if `.cvdiag movemarker` shows that the
  client follows a moved marker. Until then markers are remade and counted by reason in the memory line.
- R4, audit 07.11: check with `.cvdiag markers` that no game merchant marker shows `mod's: yes`. If one does,
  narrow `IconLogic.Owned` (markers made in this session plus one exact clean-up pass at start) or change the
  default icon to one the game doesn't use on the map.
- A3, audit 07.11: one state object per server world instead of static collections; dropped whole on unload or
  a new world. Now only entity keys of icons, markers and the destroy queue are dropped on a new world.
- A4, audit 07.11: split `Tracker.Tick` into explicit stages over one snapshot, with the order written down.
- A5, audit 07.11: pass the language to `L.T` as a parameter instead of the ambient `L.Current`.
- A2, audit 07.11: find an unambiguous mark for the mod's markers (a value in a saved component) so `Owned`
  needs no heuristics.
- Memory, 12.11: the server was killed for memory twice with nobody online (1.0.22 after 13 h, 1.0.23 after 2 h;
  1.0.21 ran 23 h clean). Cause unknown; ARCHITECTURE.md, Tracking and icons. To decide it: the `memory watch`
  lines and `BepInEx/CarriageTracker-memory.txt` of 1.0.24, the owner's per-minute memory log and a day on 1.0.21.
  Don't call it fixed before a cause is shown.
- Own caravans, asked by the owner 12.11: a profile per caravan (drop set whole by the profile, guards including
  the rift bosses "Lesser / Primal Blood Soul" as plain strong enemies, its own schedule apart from auto-start:
  hours, days, per day) and, if the game allows, its own route. Known so far: ARCHITECTURE.md, Game model, dumps
  of 12.11. Next: the owner tries `.cvdiag trial`, `trialloot`, `stops` of 1.0.24 on the server (does a patrol
  copy walk and get into the save, does a Blood Soul walk with the squad, can loot be replaced); nothing in
  `StarterTrial.cs` has run in game yet. Then the design of profiles, with the owner, before any code.
- Dump of 12.11: `Script_RespawnCarriageUponLost_Data` on the animal (720 s). Check on the server whether an animal
  that lost its cart gets a new one; if it does, `RunLogic` (cart lost, `LoopCartLost`) and auto-start limits
  must know.
- Repository: NuGet lock file and Dependabot (GitHub Actions are already pinned by SHA).
- Review of 1.0.21: the count of a repeated error that stops is only in the memory-line total, not per error.
- Review of 1.0.21: `.cvdiag movemarker` on a frozen caravan's marker less than 100 m away leaves it at the admin's
  position until the caravan has moved 100 m from it.
