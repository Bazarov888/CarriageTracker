using System;
using System.Collections.Generic;
using BepInEx.Configuration;

namespace CarriageTracker;

// Everything that can be set in BepInEx/config/CarriageTracker.cfg (until 1.0.11 Bazarov.CaravanTracker.cfg,
// moved on the first start: Plugin.MoveOldConfig).
static class Settings
{
    static ConfigFile _file;

    internal static ConfigEntry<string> Language;

    internal static ConfigEntry<string> Icon;
    internal static ConfigEntry<string> OldIcons;
    internal static ConfigEntry<bool> GlobalIcons;
    internal static ConfigEntry<string> IconMode;
    internal static ConfigEntry<bool> IconOnLoot;
    internal static ConfigEntry<bool> IconOnFrozen;
    internal static ConfigEntry<int> LootKeepMinutes;

    internal static ConfigEntry<bool> Announce;
    internal static ConfigEntry<string> AnnounceTo;
    internal static ConfigEntry<int> AnnounceRadius;
    internal static ConfigEntry<bool> AnnounceVanilla;
    internal static ConfigEntry<bool> RouteCommandsForAll;
    internal static ConfigEntry<bool> ShowOthersActs;
    internal static ConfigEntry<bool> Welcome;
    internal static ConfigEntry<string> WelcomeEn;
    internal static ConfigEntry<string> WelcomeRu;
    internal static ConfigEntry<string> TitleColor;
    internal static ConfigEntry<string> TextColor;
    internal static ConfigEntry<string> CommandColor;

    internal static ConfigEntry<string> Source;
    internal static ConfigEntry<int> AutoInterval;
    internal static ConfigEntry<string> AutoScope;
    internal static ConfigEntry<int> AutoMaxPerAct;
    internal static ConfigEntry<int> AutoMaxTotal;
    internal static ConfigEntry<string> RoutePick;
    internal static ConfigEntry<string> ExcludedRoutes;
    internal static ConfigEntry<string> RouteActs;
    internal static ConfigEntry<string> PlayerAct;
    internal static ConfigEntry<string> EmptyAct;

    internal static ConfigEntry<int> SpawnTimeoutMinutes;
    internal static ConfigEntry<int> MaxRunMinutes;
    internal static ConfigEntry<int> MaxSquadSize;
    internal static ConfigEntry<bool> ResetToStart;

    internal static ConfigEntry<int> CheckSeconds;
    internal static ConfigEntry<int> SafetyScanMinutes;

    internal static ConfigEntry<bool> VerboseLog;
    internal static ConfigEntry<bool> EnableDiagnostics;

    // Ready-made map icons of the game. Any other icon can be set by its PrefabGUID number (.cv icons).
    internal static readonly Dictionary<string, int> IconPresets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["merchant"] = -360752412,  // MapIcon_POI_Discover_Merchant
        ["unknown"] = -1443504104,  // MapIcon_POI_Discover_Unknown
        ["vblood"] = 139106998,     // MapIcon_POI_VBloodSource
    };

    public static void Bind(ConfigFile cfg)
    {
        _file = cfg;

        Language = cfg.Bind("General", "Language", "en",
            new ConfigDescription("Default language of chat messages, for players who haven't picked their own with .cv lang <en|ru>.",
                new AcceptableValueList<string>("en", "ru")));

        Icon = cfg.Bind("Map", "Icon", "merchant",
            $"Map icon for caravans: {string.Join(", ", IconPresets.Keys)} or the PrefabGUID number of any map icon (list: .cv icons). " +
            "The game has no wagon icon, and a server mod can't send new pictures to players, so the icon is one of the game's own. " +
            "Change in game with .cv icon <name|number>.");
        OldIcons = cfg.Bind("Map", "OldIcons", "",
            "Icons used before, comma separated. Their leftover markers are cleaned up. Filled automatically, no need to edit.");
        GlobalIcons = cfg.Bind("Map", "GlobalIcons", true,
            "Show caravan icons to every player on the whole map, without discovering them first.");
        IconMode = cfg.Bind("Map", "IconMode", "direct",
            new ConfigDescription("direct = the icon is attached to the caravan itself. proxy = the icon sits on a separate map marker that follows the caravan. Change in game with .cv mode.",
                new AcceptableValueList<string>("direct", "proxy")));
        IconOnLoot = cfg.Bind("Map", "IconOnLoot", true,
            "Icon on the loot container left after a robbed caravan, while it still has loot.");
        LootKeepMinutes = cfg.Bind("Map", "LootKeepMinutes", 10,
            new ConfigDescription("How long the loot container of a robbed carriage stays, in minutes after it appeared, while it still has loot. " +
                                  "The game removes it 60 seconds after nobody is within 20 m (about 10 minutes while someone is near) " +
                                  "and 60 seconds after something was taken. 0 = the game's own timers.",
                new AcceptableValueRange<int>(0, 120)));
        IconOnFrozen = cfg.Bind("Map", "IconOnFrozen", true,
            "Icon on a caravan that froze mid-route because no players are near. It wakes up and goes on when someone comes.");

        Announce = cfg.Bind("Chat", "Announce", true,
            "Tell online players in chat when a caravan sets out, with distance and direction from them.");
        AnnounceTo = cfg.Bind("Chat", "AnnounceTo", "All",
            new ConfigDescription("Who gets the announcement. All = every online player. SameAct = players who get caravans of that act " +
                                  "(a player in an act without routes counts for the act EmptyAct gives them, as in auto-start). " +
                                  "Nearby = players closer than AnnounceRadius.",
                new AcceptableValueList<string>("All", "SameAct", "Nearby")));
        AnnounceRadius = cfg.Bind("Chat", "AnnounceRadius", 1500,
            new ConfigDescription("Distance in meters for AnnounceTo = Nearby. The map is about 6400 m across.",
                new AcceptableValueRange<int>(100, 10000)));
        AnnounceVanilla = cfg.Bind("Chat", "AnnounceVanilla", true,
            "Also announce caravans the game spawned by itself next to a player. false = only caravans started by the mod.");
        Welcome = cfg.Bind("Chat", "Welcome", true,
            "Two lines to a player who has just come into the world: what the mod does and how to pick the language (.cv lang).");
        WelcomeEn = cfg.Bind("Chat", "WelcomeEn", "",
            "Your own English welcome line instead of the mod's. Empty = the mod's. Color tags like <color=#7CFC7C>.cv l</color> work. At most about 450 bytes.");
        WelcomeRu = cfg.Bind("Chat", "WelcomeRu", "",
            "Your own Russian welcome line instead of the mod's. Empty = the mod's. A Russian letter takes 2 bytes, so at most about 200 letters.");
        RouteCommandsForAll = cfg.Bind("Chat", "RouteCommandsForAll", true,
            "Let every player use .cv routes and .cv rm, which show where all caravan routes are. false = admins only.");
        ShowOthersActs = cfg.Bind("Chat", "ShowOthersActs", true,
            "In .cv act, list the act of every player online. On a PvP server that tells who is how far; false = only admins see the list.");
        TitleColor = cfg.Bind("Chat", "TitleColor", "#FFC83C", "Color of the first announcement line (caravan name).");
        TextColor = cfg.Bind("Chat", "TextColor", "#FFFFFF", "Color of the second announcement line (distance and direction).");
        CommandColor = cfg.Bind("Chat", "CommandColor", "#7CFC7C", "Color of the command in the announcement.");

        Source = cfg.Bind("AutoStart", "Source", "Both",
            new ConfigDescription("Who starts caravans. Mod = only the mod: the game no longer spawns a caravan when a player comes near a route. " +
                                  "Game = only the game, auto-start is off. Both = the mod and the game. Change in game with .cv source <mod|game|both>.",
                new AcceptableValueList<string>("Both", "Mod", "Game")));
        AutoInterval = cfg.Bind("AutoStart", "IntervalMinutes", 20,
            new ConfigDescription("Minimum time between two automatic caravan starts in the same act. Each act has its own timer. 0 = auto-start off. Change in game with .cv auto <minutes>.",
                new AcceptableValueRange<int>(0, 1440)));
        AutoScope = cfg.Bind("AutoStart", "Scope", "AllOnlineActs",
            new ConfigDescription("AllOnlineActs = start caravans in every act where at least one player is online. Leader = only in the act of the most advanced online player.",
                new AcceptableValueList<string>("AllOnlineActs", "Leader")));
        AutoMaxPerAct = cfg.Bind("AutoStart", "MaxPerAct", 2,
            new ConfigDescription("How many caravans started by the mod may travel at the same time in one act. A caravan whose cart is destroyed no longer counts.",
                new AcceptableValueRange<int>(1, 20)));
        AutoMaxTotal = cfg.Bind("AutoStart", "MaxTotal", 6,
            new ConfigDescription("How many caravans started by the mod may travel at the same time on the whole server.",
                new AcceptableValueRange<int>(1, 50)));
        RoutePick = cfg.Bind("AutoStart", "RoutePick", "Random",
            new ConfigDescription("How a route is picked among the free routes of an act. Routes at the first or the last stop of their path go first " +
                                  "(both set out from the first stop); routes left mid-way come after them. Random = any of them but the route " +
                                  "started last, while another one is free. Rotate = the one the mod started longest ago.",
                new AcceptableValueList<string>("Random", "Rotate")));
        ExcludedRoutes = cfg.Bind("AutoStart", "ExcludedRoutes", "",
            "Routes auto-start never uses, comma separated, e.g. BanditPrisoners,ChurchLegion. Route names: .cv routes. .cv start still works for them.");
        RouteActs = cfg.Bind("AutoStart", "RouteActs", "",
            "Manual acts for routes or whole regions, comma separated; several acts joined with +. " +
            "E.g. Silverlight=3+4,Oakveil=4,MilitiaIron=2. Regions: Farbane, Dunley, Silverlight, Oakveil; route names: .cv routes. " +
            "A route's own entry wins over its region's. By default a route's act is its region's: Farbane I, Dunley II " +
            "(the Draculin prison carriage too), Silverlight and Oakveil IV; a route of an unknown region gets it from its guards' level, " +
            "the same bounds as V Blood in the game's menu: up to 39 - I, 40-59 - II, 60-65 - III, 66+ - IV.");
        PlayerAct = cfg.Bind("AutoStart", "PlayerAct", "VBlood",
            new ConfigDescription("How a player's act is found. VBlood = the act of the highest V Blood drunk; the strongest V Blood of an act " +
                                  "(Quincey, Octavian, Cyril) opens the next act at once. Level = by the character's level, same bounds as above " +
                                  "(with Bloodcraft leveling it is the Bloodcraft level, without it the gear level). Highest = the higher of the two.",
                new AcceptableValueList<string>("VBlood", "Level", "Highest")));
        EmptyAct = cfg.Bind("AutoStart", "EmptyAct", "Higher",
            new ConfigDescription("Which caravans a player gets when their act has no routes (act III has none in the game: no carriages " +
                                  "in Gloomrot and the Cursed Forest). Higher = the next act up with routes. Lower = the next act down. " +
                                  "Both = one of each. If there is none in that direction, the other one is used.",
                new AcceptableValueList<string>("Higher", "Lower", "Both")));

        SpawnTimeoutMinutes = cfg.Bind("Start", "SpawnTimeoutMinutes", 5,
            new ConfigDescription("How long to wait for the squad of a started caravan to appear. After that the start counts as failed and the route gets its normal settings back.",
                new AcceptableValueRange<int>(1, 60)));
        MaxRunMinutes = cfg.Bind("Start", "MaxRunMinutes", 90,
            new ConfigDescription("A started caravan is looked after at most this long. After that the mod stops keeping it awake and the route gets its normal settings back.",
                new AcceptableValueRange<int>(10, 600)));
        ResetToStart = cfg.Bind("Start", "ResetToStart", true,
            "A route left mid-way by an earlier caravan is put back to the first stop of its path before the mod starts it: " +
            "the caravan walks to the first stop and then drives the whole way. false = it goes on from the stop where the route stands.");
        MaxSquadSize = cfg.Bind("Start", "MaxSquadSize", 20,
            new ConfigDescription("Safety limit. Units above this number in a squad started by the mod are removed at once, so the game can't fill the server memory with guards.",
                new AcceptableValueRange<int>(5, 100)));

        CheckSeconds = cfg.Bind("Timing", "CheckSeconds", 30,
            new ConfigDescription("How often, in seconds, caravans are searched and icons updated.",
                new AcceptableValueRange<int>(5, 600)));
        SafetyScanMinutes = cfg.Bind("Timing", "SafetyScanMinutes", 30,
            new ConfigDescription("How often, in minutes, the whole world is searched as a safety net (about 0.2 s). 0 = off.",
                new AcceptableValueRange<int>(0, 1440)));

        VerboseLog = cfg.Bind("Debug", "VerboseLog", false,
            "Write every check and every step of started caravans to the server log. Off: only starts, finishes, departures, disappearances, removed units and errors.");
        EnableDiagnostics = cfg.Bind("Debug", "EnableDiagnostics", false,
            "Register the .cvdiag commands for mod developers (object dumps into BepInEx folder). Needs a server restart.");
    }

    public static void Save() => _file.Save();

    public static bool TryParseIcon(string value, out int guid)
    {
        value = (value ?? "").Trim();
        if (IconPresets.TryGetValue(value, out guid)) return true;
        return int.TryParse(value, out guid);
    }

    public static int IconGuid => TryParseIcon(Icon.Value, out int g) ? g : IconPresets["merchant"];

    public static string IconPresetNames => string.Join(", ", IconPresets.Keys);

    public static bool ProxyMode => IconMode.Value.Equals("proxy", StringComparison.OrdinalIgnoreCase);

    public static bool LeaderScope => AutoScope.Value.Equals("Leader", StringComparison.OrdinalIgnoreCase);

    public static bool ModOnly => (Source.Value ?? "").Trim().Equals("Mod", StringComparison.OrdinalIgnoreCase);

    public static bool GameOnly => (Source.Value ?? "").Trim().Equals("Game", StringComparison.OrdinalIgnoreCase);

    public static bool RotatePick => (RoutePick.Value ?? "").Trim().Equals("Rotate", StringComparison.OrdinalIgnoreCase);

    public static PlayerActBy PlayerActMode =>
        Enum.TryParse((PlayerAct?.Value ?? "").Trim(), true, out PlayerActBy v) ? v : PlayerActBy.VBlood;

    public static EmptyActUse EmptyActMode =>
        Enum.TryParse((EmptyAct?.Value ?? "").Trim(), true, out EmptyActUse v) ? v : EmptyActUse.Higher;

    public static bool AnnounceSameAct => (AnnounceTo.Value ?? "").Trim().Equals("SameAct", StringComparison.OrdinalIgnoreCase);

    public static bool AnnounceNearby => (AnnounceTo.Value ?? "").Trim().Equals("Nearby", StringComparison.OrdinalIgnoreCase);

    public static bool Russian => (Language.Value ?? "").StartsWith("ru", StringComparison.OrdinalIgnoreCase);

    public static string Color(ConfigEntry<string> entry, string fallback)
    {
        string v = (entry.Value ?? "").Trim();
        v = v.Length >= 4 && v[0] == '#' ? v : fallback;
        // White by name, not as #FFFFFF: on 07.11 a player's chat filter masked "FFFFFF>234" of
        // <color=#FFFFFF>234 m and the broken tag showed up as text.
        return v.Equals("#FFFFFF", StringComparison.OrdinalIgnoreCase) || v.Equals("#FFF", StringComparison.OrdinalIgnoreCase) ? "white" : v;
    }

    // ExcludedRoutes is parsed again only when its text changes.
    static string _excludedRaw;
    static HashSet<string> _excluded = new(StringComparer.OrdinalIgnoreCase);

    public static bool Excluded(string routeKey)
    {
        string raw = ExcludedRoutes.Value ?? "";
        if (raw != _excludedRaw)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries))
                if (part.Trim().Length > 0) set.Add(part.Trim());
            _excluded = set;
            _excludedRaw = raw;
        }
        return routeKey != null && _excluded.Contains(routeKey);
    }
}
