using System;
using System.Collections.Generic;

namespace CarriageTracker;

// Chat texts in English and Russian. Each player can pick their own language (.cv lang, see Languages);
// General.Language in the config is the default. Placeholders {0}, {1}... are filled in the order the code
// passes the values.
static class L
{
    // Language of the player being answered now (set around each command and each personal message);
    // null = the server default.
    static string _lang;

    public static bool Known(string lang) => lang == "en" || lang == "ru";
    static bool Russian => (_lang ?? (Settings.Russian ? "ru" : "en")) == "ru";

    public static void Use(ulong platformId) => _lang = Languages.Of(platformId);
    public static void Use(string lang) => _lang = Known(lang) ? lang : null;
    public static void UseDefault() => _lang = null;
    public static string Current => _lang;

    // A text in a given language, whatever language is in use now.
    public static string In(string lang, string key, params object[] args)
    {
        var saved = _lang;
        try { Use(lang); return T(key, args); }
        finally { _lang = saved; }
    }

    // A text in the language of one player, whatever language is in use now.
    public static string For(ulong platformId, string key, params object[] args)
    {
        var saved = _lang;
        try { Use(platformId); return T(key, args); }
        finally { _lang = saved; }
    }

    public static string T(string key, params object[] args)
    {
        var table = Russian ? Ru : En;
        if (!table.TryGetValue(key, out var text) && !En.TryGetValue(key, out text)) return key;
        if (args == null || args.Length == 0) return text;
        try { return string.Format(text, args); }
        catch (FormatException) { return text; }
    }

    // Readable route name. An unknown route (e.g. added by a game update) shows its key.
    // side: "east" / "west" for the two patrols of one key (Routes.Route.Side).
    public static string Route(string key, string side = null)
    {
        if (string.IsNullOrEmpty(key)) return "?";
        var table = Russian ? Ru : En;
        string name = table.TryGetValue("route." + key, out var t) || En.TryGetValue("route." + key, out t) ? t : key;
        return side == null ? name : name + T("side." + side);
    }

    public static string Act(int act) => act >= 1 && act <= 4 ? T("act.name", Acts.Roman(act)) : T("act.unknown");

    // "Act III+IV" for a route or a player served by several acts.
    public static string ActList(IEnumerable<int> acts)
    {
        var romans = new List<string>();
        foreach (int a in acts) if (a >= 1 && a <= 4) romans.Add(Acts.Roman(a));
        return romans.Count == 0 ? T("act.unknown") : T("act.name", string.Join("+", romans));
    }

    public static string Region(string region) => region == null ? "?" : T("region." + region);

    static readonly Dictionary<string, string> En = new()
    {
        ["world.notready"] = "The server world isn't ready yet.",

        ["act.name"] = "Act {0}",
        ["act.unknown"] = "act ?",

        ["side.east"] = ", east",
        ["side.west"] = ", west",
        ["region.Farbane"] = "Farbane Woods",
        ["region.Dunley"] = "Dunley Farmlands",
        ["region.Silverlight"] = "Silverlight Hills",
        ["region.Oakveil"] = "Oakveil Woodlands",

        ["route.BanditAnimal"] = "Bandits: animals",
        ["route.BanditCopper"] = "Bandits: copper",
        ["route.BanditPlank"] = "Bandits: planks",
        ["route.BanditPrisoners"] = "Bandits: prisoners",
        ["route.BanditSulfur"] = "Bandits: sulfur",
        ["route.BlackfangBastion"] = "Venom Blades: emery",
        ["route.BlackfangSouth"] = "Venom Blades: venom sap and oak",
        ["route.ChuchGrape"] = "Church: grapes",
        ["route.ChuchSilver"] = "Church: silver",
        ["route.ChurchLegion"] = "Church: Draculin prisoners",
        ["route.ChurchMutants"] = "Church: mutants",
        ["route.MilitiaCotton"] = "Militia: cotton",
        ["route.MilitiaIron"] = "Militia: iron",
        ["route.MilitiaQuartz"] = "Militia: quartz",

        ["dir.0"] = "north",
        ["dir.1"] = "north-east",
        ["dir.2"] = "east",
        ["dir.3"] = "south-east",
        ["dir.4"] = "south",
        ["dir.5"] = "south-west",
        ["dir.6"] = "west",
        ["dir.7"] = "north-west",

        ["note.loot"] = " (standing, has loot)",
        ["note.moving"] = " (on the road{0})",
        ["note.frozen"] = " (froze mid-route, no players near; wakes up when you come near; {0}{1})",

        ["announce.title"] = "CARRIAGE ON THE ROAD: {0}",
        ["announce.where"] = "{1}, {0} m.",
        ["announce.mark"] = "Map marker:",

        ["stats.none"] = "No check has run yet.",
        ["stats.main"] = "Mode {0}. Check: {1} ms, fast search {2} ms over {3} objects",
        ["stats.full"] = ", full search {0} ms",
        ["stats.counts"] = "Carriages: {0}, empty containers: {1}, frozen without players: {2}",
        ["stats.parked"] = ", parked off the map without a patrol point: {0}",
        ["stats.icons"] = "Icons added: {0}, removed: {1}",
        ["stats.globalized"] = ", made visible on the whole map: {0}",
        ["stats.safety"] = "Last full search {0} min ago, the fast one missed: {1}.",

        ["route.state.endguarded"] = "reached the end, frozen with guards",
        ["route.state.endempty"] = "stuck at the end of the route, empty",
        ["route.state.waiting"] = "waiting to spawn",
        ["route.state.nocart"] = "cart destroyed, the animal walks to the end",
        ["route.state.loopleft"] = "cart destroyed, the rest circles the route",
        ["route.state.moving"] = "on the road",
        ["route.state.other"] = "{0}/{1}, units {2}",

        ["start.already"] = "Carriage {0} is already being started.",
        ["start.error"] = "Carriage {0} couldn't be started: an error, see the server log. The route's own settings are put back.",
        ["start.goingon"] = "Starting carriage {0}. An error was written to the server log, the start goes on.",
        ["start.broken"] = "Starting carriages is off on this game version: it lacks {0}. See the server log.",
        ["start.ok"] = "Starting carriage {0}. Removed stuck: units {1}, carts {2}. The squad appears where the route stands ({3}) within a minute, then players get an announcement.",
        ["start.midway"] = " The route stands mid-way, left there by an earlier carriage, so this one sets out from there.",
        ["start.reset"] = " The route stood mid-way ({0}), it was put back to the start.",
        ["start.spawned"] = "Carriage {0} appeared and sets out.",
        ["start.failed"] = "Carriage {0} didn't appear in {1} min. See the server log (lines \"CarriageTracker start\").",
        ["start.done"] = "Carriage {0}: the squad is gone (reached the end or was looted).",
        ["start.wakefailed"] = "Carriage {0} didn't start: its route didn't go back to the first stop. Auto-start skips it for an hour.",
        ["start.bad"] = "No route number {0}. List: .cv routes",

        ["status.none"] = "No carriages started by the mod.",
        ["status.gone"] = "patrol is gone",
        ["status.item"] = "{0}{1}: {2}, {3} min since start",
        ["status.auto"] = " (auto, {0})",
        ["stop.ok"] = "Stopped: {0}. Route settings restored.",

        ["list.empty"] = "There are no carriages on the map right now.",
        ["list.head"] = "Carriages on the map: {0}. Marker on one: .cv m <number>",
        ["list.item"] = "{0}. {1} - {2} m, {3}{4}",
        ["mark.bad"] = "No carriage number {0}. List: .cv l",
        ["mark.ok"] = "Marker: {0}, {1} m, {2}{3}. Open the map, the path is set.",
        ["routemark.ok"] = "Marker on route {0} at its point from .cv r: {1} m, {2}. Open the map, the path is set.",

        ["routes.head"] = "Routes: {0}. Start one (admin): .cv start <number>",
        ["routes.item"] = "{0}. {1} [{2}] ({12}, lvl {3}, {4}{5}): {6}{10}{7}{11}, {8} m, {9}",
        ["routes.manual"] = " manual",
        ["routes.running"] = " [started]",
        ["routes.excluded"] = " [no auto-start]",
        ["stops.of"] = "stop {0} of {1}",
        ["stops.at"] = "stop {0}",

        ["act.none"] = "Your act: {0}{1}. No V Blood drunk yet.",
        ["act.mine"] = "Your act: {0}{1}. V Blood drunk: {2}, the highest: {3} (lvl {4}).",
        ["act.bylevel"] = " (by level {0})",
        ["act.level"] = " Level: {0}.",
        ["act.next"] = " The strongest V Blood of {0} is drunk, so {1} is open.",
        ["act.online"] = "Online: {0}",
        ["act.routes"] = "Carriages for you ({0}): {1}",
        ["act.empty"] = "{0} has no routes, so you get the carriages of {1}.",
        ["act.noroutes"] = "There are no routes for {0}.",

        ["icon.bad"] = "{0} is not an icon. Use a name ({1}) or a number from .cv icons",
        ["icon.notmap"] = "{0} is not a map icon. List: .cv icons <filter>",
        ["icon.ok"] = "Carriage icon: {0} ({1}).",
        ["icons.head"] = "Icons: {0}, page {1}/{2}. All of them are written to the server log.",
        ["mode.bad"] = "Mode: direct or proxy.",
        ["lang.ok"] = "Language: English. Only for you; other players keep theirs.",
        ["welcome"] = "Welcome! Every carriage here is shown on the map, and new ones set out in your act. {0} - carriages by distance, {1} - map marker on the nearest. Language: {2} / {3}",

        ["auto.off"] = "Auto-start is off. Turn it on: .cv auto <minutes>",
        ["auto.status"] = "Auto-start: every {0} min per act, {1}, at most {2} per act and {3} in total. On the road now: {4}.",
        ["auto.scope.all"] = "in every act with players online",
        ["auto.scope.leader"] = "only in the act of the most advanced player online",
        ["auto.actline"] = "{0}: {1} on the road, next in {2} min",
        ["auto.last"] = "Last: {0}",
        ["auto.nothing"] = "Nothing to start now.",
        ["auto.nobody"] = "Nobody is online, skipped.",
        ["auto.gameonly"] = "Source = Game: only the game starts carriages, auto-start is off. Change: .cv source mod",
        ["auto.modoff"] = "Auto-start is off, so with Source = Mod no carriages appear at all. Turn it on: .cv auto 20",
        ["routes.adminonly"] = "This command is for admins on this server.",
        ["cleanup.ok"] = "Cleaned up: starts stopped {0}, routes restored {1}, units given their freezing back {2}, the mod's icons removed. Until the server restarts the mod changes nothing. Now remove CarriageTracker.dll and restart the server.",
        ["cleanup.paused"] = "The mod is paused after .cv cleanup until the server restarts.",
        ["source.now"] = "Carriages: {0}.",
        ["source.mod"] = "only the mod's, the game doesn't spawn them by itself",
        ["source.game"] = "only the game's, auto-start is off",
        ["source.both"] = "the mod's and the game's",
        ["auto.noroutes"] = "{0}: there are no routes for this act.",
        ["auto.maxtotal"] = "Server limit: {0} of {1} on the road.",
        ["auto.maxact"] = "{0}: limit, {1} of {2} on the road.",
        ["auto.busy"] = "{0}: every route already has a carriage.",
        ["auto.started"] = "{1}: started {0} (free routes: {2}; {3}).",
    };

    static readonly Dictionary<string, string> Ru = new()
    {
        ["world.notready"] = "Мир сервера ещё не готов.",

        ["act.name"] = "акт {0}",
        ["act.unknown"] = "акт ?",

        ["side.east"] = ", восток",
        ["side.west"] = ", запад",
        ["region.Farbane"] = "Farbane Woods",
        ["region.Dunley"] = "Dunley Farmlands",
        ["region.Silverlight"] = "Silverlight Hills",
        ["region.Oakveil"] = "Oakveil Woodlands",

        ["route.BanditAnimal"] = "бандиты: животные",
        ["route.BanditCopper"] = "бандиты: медь",
        ["route.BanditPlank"] = "бандиты: доски",
        ["route.BanditPrisoners"] = "бандиты: пленники",
        ["route.BanditSulfur"] = "бандиты: сера",
        ["route.BlackfangBastion"] = "Venom Blades: наждак",
        ["route.BlackfangSouth"] = "Venom Blades: ядовитый сок и дуб",
        ["route.ChuchGrape"] = "церковь: виноград",
        ["route.ChuchSilver"] = "церковь: серебро",
        ["route.ChurchLegion"] = "церковь: пленные дракулины",
        ["route.ChurchMutants"] = "церковь: мутанты",
        ["route.MilitiaCotton"] = "ополчение: хлопок",
        ["route.MilitiaIron"] = "ополчение: железо",
        ["route.MilitiaQuartz"] = "ополчение: кварц",

        ["dir.0"] = "север",
        ["dir.1"] = "северо-восток",
        ["dir.2"] = "восток",
        ["dir.3"] = "юго-восток",
        ["dir.4"] = "юг",
        ["dir.5"] = "юго-запад",
        ["dir.6"] = "запад",
        ["dir.7"] = "северо-запад",

        ["note.loot"] = " (стоит, есть лут)",
        ["note.moving"] = " (в пути{0})",
        ["note.frozen"] = " (застыл посреди маршрута, рядом нет игроков; оживёт, когда подойдёшь; {0}{1})",

        ["announce.title"] = "КАРАВАН В ПУТИ: {0}",
        ["announce.where"] = "{1}, {0} м.",
        ["announce.mark"] = "Метка на карте:",

        ["stats.none"] = "Проверка ещё не выполнялась.",
        ["stats.main"] = "Режим {0}. Проверка: {1} мс, быстрый поиск {2} мс по {3} объектам",
        ["stats.full"] = ", полный поиск {0} мс",
        ["stats.counts"] = "Караванов: {0}, пустых контейнеров: {1}, застывших без игроков: {2}",
        ["stats.parked"] = ", за картой без точки патруля: {0}",
        ["stats.icons"] = "Иконок добавлено: {0}, удалено: {1}",
        ["stats.globalized"] = ", открыто для всей карты: {0}",
        ["stats.safety"] = "Последний полный поиск {0} мин назад, быстрый пропустил: {1}.",

        ["route.state.endguarded"] = "доехал до конца, застыл с охраной",
        ["route.state.endempty"] = "застрял в конце маршрута, пустой",
        ["route.state.waiting"] = "ждёт появления",
        ["route.state.nocart"] = "повозка разбита, животное идёт к концу",
        ["route.state.loopleft"] = "повозка разбита, остальные кружат по маршруту",
        ["route.state.moving"] = "едет",
        ["route.state.other"] = "{0}/{1}, юнитов {2}",

        ["start.already"] = "Караван «{0}» уже запускается.",
        ["start.error"] = "Караван «{0}» не запустился: ошибка, подробности в логе сервера. Маршруту возвращаются его настройки.",
        ["start.goingon"] = "Запускаю караван «{0}». В лог сервера записана ошибка, запуск продолжается.",
        ["start.broken"] = "Запуск караванов выключен на этой версии игры: в ней нет {0}. Подробности в логе сервера.",
        ["start.ok"] = "Запускаю караван «{0}». Убрано застрявших: юнитов {1}, повозок {2}. Отряд появится там, где стоит маршрут ({3}), в течение минуты, и игрокам придёт анонс.",
        ["start.midway"] = " Маршрут стоит посреди пути: там его оставил прошлый караван, и этот поедет оттуда.",
        ["start.reset"] = " Маршрут стоял посреди пути ({0}), мод вернул его в начало.",
        ["start.spawned"] = "Караван «{0}» появился и выезжает.",
        ["start.failed"] = "Караван «{0}» не появился за {1} мин. Смотри лог сервера (строки «CarriageTracker start»).",
        ["start.done"] = "Караван «{0}»: отряд исчез (доехал или разграблен).",
        ["start.wakefailed"] = "Караван «{0}» не запущен: маршрут не вернулся на первую остановку. Автозапуск пропустит его на час.",
        ["start.bad"] = "Нет маршрута с номером {0}. Список: .cv routes",

        ["status.none"] = "Запущенных модом караванов нет.",
        ["status.gone"] = "патруль пропал",
        ["status.item"] = "«{0}»{1}: {2}, {3} мин с запуска",
        ["status.auto"] = " (авто, {0})",
        ["stop.ok"] = "Остановлено запусков: {0}. Настройки маршрутов возвращены.",

        ["list.empty"] = "Сейчас на карте нет ни одного каравана.",
        ["list.head"] = "Караванов на карте: {0}. Метка на нужный: .cv m <номер>",
        ["list.item"] = "{0}. {1} - {2} м, {3}{4}",
        ["mark.bad"] = "Нет каравана с номером {0}. Список: .cv l",
        ["mark.ok"] = "Метка: {0}, {1} м, {2}{3}. Открой карту, путь проложен.",
        ["routemark.ok"] = "Метка на маршруте «{0}», на его точке из .cv r: {1} м, {2}. Открой карту, путь проложен.",

        ["routes.head"] = "Маршрутов: {0}. Запуск (админ): .cv start <номер>",
        ["routes.item"] = "{0}. {1} [{2}] ({12}, ур. {3}, {4}{5}): {6}{10}{7}{11}, {8} м, {9}",
        ["routes.manual"] = " вручную",
        ["routes.running"] = " [запущен]",
        ["routes.excluded"] = " [без автозапуска]",
        ["stops.of"] = "остановка {0} из {1}",
        ["stops.at"] = "остановка {0}",

        ["act.none"] = "Твой {0}{1}. Выпитых V Blood пока нет.",
        ["act.mine"] = "Твой {0}{1}. Выпито V Blood: {2}, старший: {3} (ур. {4}).",
        ["act.bylevel"] = " (по уровню {0})",
        ["act.level"] = " Уровень: {0}.",
        ["act.next"] = " Самый сильный V Blood ({0}) выпит, поэтому открыт {1}.",
        ["act.online"] = "Онлайн: {0}",
        ["act.routes"] = "Караваны для тебя ({0}): {1}",
        ["act.empty"] = "В {0} маршрутов нет, поэтому тебе идут караваны: {1}.",
        ["act.noroutes"] = "Маршрутов для {0} нет.",

        ["icon.bad"] = "{0} - не иконка. Нужно имя ({1}) или номер из .cv icons",
        ["icon.notmap"] = "{0} - это не иконка карты. Список: .cv icons <фильтр>",
        ["icon.ok"] = "Иконка караванов: {0} ({1}).",
        ["icons.head"] = "Иконок: {0}, страница {1}/{2}. Все записаны в лог сервера.",
        ["mode.bad"] = "Режим: direct или proxy.",
        ["lang.ok"] = "Язык: русский. Только для тебя, у остальных игроков свой.",
        ["welcome"] = "Добро пожаловать! Здесь все караваны видны на карте, а новые выходят в твоём акте. {0} - список караванов, {1} - метка на ближайшем. Язык: {2} / {3}",

        ["auto.off"] = "Автозапуск выключен. Включить: .cv auto <минуты>",
        ["auto.status"] = "Автозапуск: раз в {0} мин в каждом акте, {1}, не больше {2} на акт и {3} всего. Сейчас едут: {4}.",
        ["auto.scope.all"] = "во всех актах, где есть игроки онлайн",
        ["auto.scope.leader"] = "только в акте самого продвинутого игрока онлайн",
        ["auto.actline"] = "{0}: едут {1}, следующий через {2} мин",
        ["auto.last"] = "Последний раз: {0}",
        ["auto.nothing"] = "Сейчас запускать нечего.",
        ["auto.nobody"] = "На сервере никого нет, пропуск.",
        ["auto.gameonly"] = "Source = Game: караваны запускает только игра, автозапуск выключен. Сменить: .cv source mod",
        ["auto.modoff"] = "Автозапуск выключен, а при Source = Mod караванов не будет совсем. Включить: .cv auto 20",
        ["routes.adminonly"] = "На этом сервере эта команда только для админов.",
        ["cleanup.ok"] = "Очищено: остановлено запусков {0}, маршрутов восстановлено {1}, юнитам возвращена заморозка {2}, иконки мода удалены. До перезапуска сервера мод ничего не меняет. Теперь удали CarriageTracker.dll и перезапусти сервер.",
        ["cleanup.paused"] = "Мод на паузе после .cv cleanup до перезапуска сервера.",
        ["source.now"] = "Караваны: {0}.",
        ["source.mod"] = "только мода, сама игра их не выпускает",
        ["source.game"] = "только игры, автозапуск выключен",
        ["source.both"] = "и мода, и игры",
        ["auto.noroutes"] = "{0}: для этого акта маршрутов нет.",
        ["auto.maxtotal"] = "Лимит сервера: едут {0} из {1}.",
        ["auto.maxact"] = "{0}: лимит, едут {1} из {2}.",
        ["auto.busy"] = "{0}: на всех маршрутах уже есть караваны.",
        ["auto.started"] = "{1}: запущен «{0}» (свободных маршрутов: {2}; {3}).",
    };
}
