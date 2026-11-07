# CarriageTracker

[![build](https://github.com/Bazarov888/CarriageTracker/actions/workflows/build.yml/badge.svg)](https://github.com/Bazarov888/CarriageTracker/actions/workflows/build.yml)
[![Thunderstore](https://img.shields.io/thunderstore/v/Bazarov/CarriageTracker?label=Thunderstore)](https://thunderstore.io/c/v-rising/p/Bazarov/CarriageTracker/)

Server-side mod for V Rising that turns carriages into something players actually find and fight over.

- **On the map for everyone.** Every carriage on the road gets a map icon that all players see on the whole map, without discovering the area first. So do carriages that stopped mid-route because nobody is near, and the loot left after a robbed carriage.
- **Announced in chat.** When a carriage sets out, players get a short message in their own language: which carriage, its act, how far and in which direction it is from them, and a command that puts a marker on it.
- **Started where players are.** In the base game a carriage only spawns when someone happens to pass its route. The mod starts carriages itself in every act that has players online, a few at a time, so there is always something to hunt.
- **Loot that waits.** The container of a robbed carriage stays 10 minutes instead of the game's 60 seconds, so a robber who died in the fight can come back for it.

Players don't install anything. Messages are in English and Russian, each player picks their own.

## Installation

**With a mod manager** (r2modman, Thunderstore Mod Manager): pick your V Rising dedicated server profile, search for CarriageTracker, install. BepInExPack V Rising and VampireCommandFramework come with it.

**By hand:**

1. Install [BepInExPack V Rising](https://thunderstore.io/c/v-rising/p/BepInEx/BepInExPack_V_Rising/) on the server and put [VampireCommandFramework](https://thunderstore.io/c/v-rising/p/deca/VampireCommandFramework/) into `BepInEx/plugins/`.
2. Download `CarriageTracker.dll` from the [latest release](https://github.com/Bazarov888/CarriageTracker/releases/latest) and put it into `BepInEx/plugins/CarriageTracker/`.
3. Start the server. The settings file `BepInEx/config/CarriageTracker.cfg` is created on the first start.

The server log should then have `CarriageTracker <version> loaded` and, at the first check, `game check passed`. Works on a dedicated server; it isn't made for Host & Play.

## How it plays out of the box

With the defaults nothing has to be set up:

- every 20 minutes, in each act where someone is online, the mod starts a carriage on a free route of that act (at most 2 per act and 6 on the server at once);
- every online player gets the announcement;
- the game keeps spawning carriages the vanilla way too, next to players.

The first start in each act comes one interval after the server starts (and after `.cv auto <minutes>` or `.cv source`), and only while someone is online. To see one at once, run `.cv an` as an admin. Admin commands need the game's admin rights: your SteamID in `adminlist.txt` and `adminauth` in the console.

The settings most servers change:

| Want | Setting |
|---|---|
| Carriages more or less often | `IntervalMinutes` (minutes between starts in one act) |
| Only the mod's carriages, none from the game | `Source = Mod` |
| Announcements only for the players a carriage is meant for | `AnnounceTo = SameAct`, or `Nearby` with `AnnounceRadius` |
| Acts by character level (Bloodcraft) instead of V Blood | `PlayerAct = Level` or `Highest` |
| A route never started by the mod | `ExcludedRoutes`, e.g. `BanditPrisoners,ChurchLegion` |

The interval, the source, the icon and the icon mode can also be changed in game, see the admin commands; the rest is read at server start.

## Commands

For everyone:

| Command | What it does |
|---|---|
| `.cv l` | Carriages on the map, nearest first, with distance, direction and state |
| `.cv m [number]` | Map marker on a carriage from `.cv l`, the nearest by default |
| `.cv r` | All routes with region, level, act and state |
| `.cv rm <number>` | Map marker on a route from `.cv r` |
| `.cv a` | Your act, how it was found, and the routes you get |
| `.cv lang <en or ru>` | Your language of the mod's messages |

For admins:

| Command | What it does |
|---|---|
| `.cv start <number>` | Start a carriage on a route from `.cv r` right now |
| `.cv ss` | How the carriages started by the mod are doing |
| `.cv stop` | Stop looking after started carriages and give their routes the normal settings back (auto-start goes on; `.cv auto 0` turns it off) |
| `.cv auto [minutes]` | Auto-start status; a number sets the interval, `0` turns it off |
| `.cv an` | Run auto-start now, ignoring the interval |
| `.cv source [mod, game or both]` | Who starts carriages; without a value shows it |
| `.cv icon <name or number>` | The map icon: `merchant`, `unknown`, `vblood` or a number from `.cv icons` |
| `.cv icons [filter] [page]` | The game's map icons with their numbers |
| `.cv mode <direct or proxy>` | Icon on the carriage itself, or on a separate marker that follows it |
| `.cv stats`, `.cv refresh` | Results of the last check, or a check right now |
| `.cv cleanup` | Before removing the mod, see Uninstalling |

`.cv` is short for `.carriage`.

## Acts and routes

Acts are those of the game's V Blood menu (K).

- **A player's act** is by default the highest act of the V Blood they have drunk. The strongest V Blood of an act opens the next one at once: Quincey opens Act II, Octavian Act III, Cyril Act IV. With `PlayerAct = Level` it goes by character level instead (Bloodcraft's level when its leveling is on, otherwise gear level): up to 39 Act I, 40-59 Act II, 60-65 Act III, 66 and up Act IV. `Highest` takes the higher of the two.
- **A route's act** is the act of its region: Farbane I, Dunley II, Silverlight and Oakveil IV. A route the mod doesn't know (one a game update adds) gets its act from its guards' level.
- **Act III has no carriages in the game** (none in Gloomrot or the Cursed Forest). Players in Act III get the next act up by default (`EmptyAct = Higher`); `Lower` gives the act below, `Both` one of each.
- **`RouteActs`** assigns acts by hand, to a route or a whole region, one act or several joined by `+`: `Silverlight=3+4` gives Silverlight carriages to Act III and IV players.

| Region | Routes (keys as the game spells them) |
|---|---|
| `Farbane` | `BanditAnimal` (creature prison), `BanditCopper`, `BanditPrisoners` (villager prison), `BanditPlank`, `BanditSulfur` |
| `Dunley` | `MilitiaCotton` (two routes, east and west), `MilitiaIron`, `MilitiaQuartz`, `ChurchLegion` (Draculin prison) |
| `Silverlight` | `ChuchSilver`, `ChurchMutants` (mutant prison), `ChuchGrape` |
| `Oakveil` | `BlackfangBastion` (emery), `BlackfangSouth` (venom sap and oak) |

The Draculin prison carriage counts as Act II, as Dunley's hard one, even though its guards are the Silverlight church units. The two Oakveil routes have no end: their carriage circles until it is destroyed.

## Configuration

`BepInEx/config/CarriageTracker.cfg`:

| Section | Setting | Default | Meaning |
|---|---|---|---|
| General | Language | `en` | `en` or `ru`, for players who haven't picked their own |
| Map | Icon | `merchant` | `merchant`, `unknown`, `vblood` or a map icon number (`.cv icons`) |
| Map | OldIcons | empty | Kept by `.cv icon`: icons used before, so their leftovers are removed. No need to edit |
| Map | GlobalIcons | `true` | Icons visible to everyone on the whole map |
| Map | IconMode | `direct` | `direct` (icon on the carriage) or `proxy` (on a separate marker) |
| Map | IconOnLoot | `true` | Icon on the loot of a robbed carriage, while it has loot |
| Map | LootKeepMinutes | `10` | How long that loot stays; `0` = the game's timers |
| Map | IconOnFrozen | `true` | Icon on a carriage that stopped mid-route because nobody is near |
| Chat | Announce | `true` | Announce carriages that set out |
| Chat | AnnounceTo | `All` | `All`, `SameAct` (players who get that act's carriages) or `Nearby` |
| Chat | AnnounceRadius | `1500` | Meters, for `Nearby` |
| Chat | AnnounceVanilla | `true` | Also announce carriages the game spawned; `false` = only the mod's |
| Chat | RouteCommandsForAll | `true` | `.cv r` and `.cv rm` for everyone; `false` = admins only |
| Chat | ShowOthersActs | `true` | `.cv a` also lists every online player's act; `false` = admins only (on PvP servers it tells who is how far) |
| Chat | Welcome | `true` | A line to a player entering the world: what the mod does and how to pick the language. A player who hasn't picked one gets it in English and in Russian |
| Chat | WelcomeEn, WelcomeRu | empty | Your own welcome lines; color tags work, about 450 bytes (a Russian letter is 2) |
| Chat | TitleColor, TextColor, CommandColor | `#FFC83C`, `#FFFFFF`, `#7CFC7C` | Announcement colors, as `#RRGGBB` (names like `yellow` are ignored) |
| AutoStart | Source | `Both` | `Mod` = only the mod starts carriages, the game no longer spawns them near players; `Game` = only the game; `Both` |
| AutoStart | IntervalMinutes | `20` | Minutes between two starts in one act; `0` = auto-start off |
| AutoStart | Scope | `AllOnlineActs` | `AllOnlineActs`, or `Leader`: only the act of the most advanced player online |
| AutoStart | MaxPerAct | `2` | Carriages of the mod on the road at once in one act |
| AutoStart | MaxTotal | `6` | The same for the whole server |
| AutoStart | RoutePick | `Random` | `Random` (a free route, not the one started last) or `Rotate` (the one started longest ago) |
| AutoStart | ExcludedRoutes | empty | Routes the mod never starts |
| AutoStart | RouteActs | empty | Acts by hand, e.g. `Silverlight=3+4,MilitiaIron=2`; a route's own entry wins over its region's |
| AutoStart | PlayerAct | `VBlood` | `VBlood`, `Level` or `Highest` |
| AutoStart | EmptyAct | `Higher` | For players in an act without routes: `Higher`, `Lower` or `Both` |
| Start | SpawnTimeoutMinutes | `5` | How long to wait for a started squad to appear |
| Start | ResetToStart | `true` | A route left mid-way is put back to its first stop before a start |
| Start | MaxRunMinutes | `90` | How long a started carriage is looked after |
| Start | MaxSquadSize | `20` | Safety limit for a started squad |
| Timing | CheckSeconds | `30` | How often carriages are searched |
| Timing | SafetyScanMinutes | `30` | How often the whole world is searched as a safety net; `0` = off |
| Debug | VerboseLog | `false` | Every check and every step of a start in the log |
| Debug | EnableDiagnostics | `false` | Developer commands `.cvdiag` |

The limits count only carriages auto-start started that still have their cart; `.cv start` and the game's own carriages don't count. With `Source = Mod` and `IntervalMinutes = 0` there are no carriages at all; the log warns about it. Each player's language is kept in `BepInEx/config/CarriageTracker.languages.txt`.

Edit the file while the server is stopped: admin commands that change a setting (`.cv auto`, `.cv source`, `.cv icon`, `.cv mode`) write the whole file back.

## Compatibility

Runs on the author's server together with Bloodcraft 1.13.26, KindredCommands 2.5.8, KindredLogistics, KindredPortals, KindredSchematics, Faust, ScarletCore, ScarletBrazier, ScarletTrash, CoffinSleep, BetterMissions, AutoCloseDoors and VampireCommandFramework 0.11. With [Bloodcraft](https://thunderstore.io/c/v-rising/p/zfolmt/Bloodcraft/), `PlayerAct = Level` uses its character level.

With KindredPortals installed, icons of stopped carriages (and of all carriages with `IconMode = proxy`) are visible to everyone even with `GlobalIcons = false`: KindredPortals makes every icon on a marker object global, and those icons hang on such markers.

At the first check after the server starts, the mod checks every game field it relies on. If a game update has changed one it can't do without, the log says which, and starting carriages and `Source = Mod` are turned off while tracking, icons and announcements go on. If a less important one is missing, routes aren't put back to their first stop and stop numbers may be unknown.

## Good to know

- The game has no carriage map icon, and a server mod can't send new pictures to players, so the icon is one of the game's own. Pick one with `.cv icon`.
- A route carries one carriage at a time. When the cart is destroyed, the animal and what is left of the squad walk on to the end of the route; until then that route is busy.
- A started carriage keeps going if everybody logs off (the mod looks after it up to `MaxRunMinutes`) and is removed at the end of its route, as usual.
- An Oakveil carriage that nobody robs keeps circling, as in the base game, and its route stays busy until it is destroyed.

## Uninstalling

While it runs, the mod changes some route settings in the world (with `Source = Mod` it blocks the game's own spawning), and the save keeps them. Before removing it:

1. Run `.cv cleanup` in chat as an admin. It stops started carriages, gives every route the game's settings back and removes the mod's icons.
2. Stop the server the normal way, so the world is saved, delete `BepInEx/plugins/CarriageTracker`, start the server.

Without the cleanup, routes blocked by `Source = Mod` would stay without carriages from the game, a route caught in the middle of a start could keep its start settings, and the mod's icons would stay on the map.

## Problems and ideas

[GitHub issues](https://github.com/Bazarov888/CarriageTracker/issues). The server log lines starting with `CarriageTracker` help a lot.

## For developers

`dotnet build -c Release` builds the DLL and the Thunderstore package (`dist/`); reference assemblies come from NuGet. `dotnet run --project tests` runs the tests of the decision logic. The version lives only in `CarriageTracker.csproj`. A new version merged into `main` is released on GitHub and Thunderstore by GitHub Actions. How the mod works inside is in [ARCHITECTURE.md](https://github.com/Bazarov888/CarriageTracker/blob/main/ARCHITECTURE.md).

## Credits

- Making map icons global follows [KindredPortals](https://thunderstore.io/c/v-rising/p/odjit/KindredPortals/) by odjit.
- Removing buffs follows [KindredCommands](https://thunderstore.io/c/v-rising/p/odjit/KindredCommands/) and Bloodcraft.
- Acts follow the [V Blood list](https://vrising.fandom.com/wiki/V_Blood_Carriers); routes and regions follow the [Carriage](https://wiki.v-ris.ing/w/Carriage) wiki page.

License: MIT.

---

## По-русски

Серверный мод для V Rising: караваны (повозки) становятся тем, что игроки находят и за что дерутся.

- **Видны на карте всем.** У каждой повозки в пути есть значок на карте, его видят все игроки на всей карте, даже в неоткрытых местах. Так же отмечены повозки, остановившиеся посреди пути, потому что рядом никого нет, и добыча с разбитой повозки.
- **Анонс в чате.** Когда повозка выезжает, игрокам приходит сообщение на их языке: какой караван и какого акта, где он от игрока и команда, которая ставит на него метку.
- **Запуск там, где игроки.** В обычной игре повозка появляется, только если кто-то случайно проходит мимо маршрута. Мод сам запускает караваны в каждом акте, где есть игроки онлайн.
- **Добыча ждёт.** Сундук разбитой повозки стоит 10 минут вместо 60 секунд в игре, так что погибший в бою может за ним вернуться.

Игрокам ничего ставить не нужно.

**Установка.** Мод для выделенного сервера. Через r2modman или Thunderstore Mod Manager: найди CarriageTracker в профиле выделенного сервера и установи, зависимости поставятся сами. Вручную: нужны [BepInExPack V Rising](https://thunderstore.io/c/v-rising/p/BepInEx/BepInExPack_V_Rising/) и [VampireCommandFramework](https://thunderstore.io/c/v-rising/p/deca/VampireCommandFramework/), а `CarriageTracker.dll` из [последнего релиза](https://github.com/Bazarov888/CarriageTracker/releases/latest) кладётся в `BepInEx/plugins/CarriageTracker/` на сервере. Настройки появятся в `BepInEx/config/CarriageTracker.cfg` после первого запуска.

**Команды для всех.**

| Команда | Что делает |
|---|---|
| `.cv l` | Повозки на карте, ближние первыми |
| `.cv m [номер]` | Метка на повозке, по умолчанию на ближней |
| `.cv r` | Все маршруты с регионом, уровнем, актом и состоянием |
| `.cv rm <номер>` | Метка на маршруте из `.cv r` |
| `.cv a` | Твой акт и маршруты, которые тебе достаются |
| `.cv lang <ru или en>` | Язык сообщений мода для тебя |

Команды админа, настройки и подробности про акты описаны выше, в английской части. Коротко: по умолчанию каждые 20 минут в каждом акте с игроками онлайн выходит караван; первый — через один интервал (по умолчанию 20 минут) после запуска сервера, сразу можно запустить командой админа `.cv an`. Акт игрока считается по выпитым V Blood, акт маршрута — по региону (Farbane I, Dunley II, Silverlight и Oakveil IV). Игроки III акта, где в игре повозок нет, получают караваны IV акта.

**Удаление.** Сначала выполни в чате `.cv cleanup` (админ): мод вернёт маршрутам настройки игры и уберёт свои значки. Потом останови сервер обычным способом, чтобы мир сохранился, удали `BepInEx/plugins/CarriageTracker` и запусти сервер. Без этого маршруты, заблокированные при `Source = Mod`, останутся без караванов игры, а значки мода — на карте.

Ошибки и идеи: [GitHub issues](https://github.com/Bazarov888/CarriageTracker/issues).
