using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using ProjectM;
using ProjectM.Network;
using Stunlock.Core;
using Unity.Entities;
using Unity.Mathematics;
using VampireCommandFramework;

namespace CarriageTracker;

[CommandGroup("carriage", "cv")]
public class Commands
{
    static bool Ready(ChatCommandContext ctx, out EntityManager em, out PrefabCollectionSystem prefabs)
    {
        var lang = Lang.Of(ctx);
        em = default;
        var world = Caravans.ServerWorld();
        prefabs = world == null ? null : Caravans.Prefabs(world);
        if (prefabs == null) { ctx.Reply(L.T(lang, "world.notready")); return false; }
        em = world.EntityManager;
        return true;
    }

    static List<(Caravans.Caravan C, float Dist, string Dir)> Sorted(ChatCommandContext ctx, out EntityManager em)
    {
        var lang = Lang.Of(ctx);
        em = default;
        var world = Caravans.ServerWorld();
        if (world == null || !Tracker.Fresh()) { ctx.Reply(L.T(lang, "world.notready")); return null; }
        em = world.EntityManager;
        float3 me = Caravans.PositionOf(em, ctx.Event.SenderCharacterEntity);
        var result = new List<(Caravans.Caravan C, float Dist, string Dir)>();
        foreach (var c in Tracker.Current)
            result.Add((c, Caravans.Dist2(c.Pos, me), Caravans.Direction(lang, c.Pos.x - me.x, c.Pos.z - me.z)));
        result.Sort((a, b) => a.Dist.CompareTo(b.Dist));
        return result;
    }

    // ---- for everyone ----

    [Command("list", shortHand: "l", description: "Caravans on the map, nearest first")]
    public void List(ChatCommandContext ctx)
    {
        var lang = Lang.Of(ctx);
        var list = Sorted(ctx, out _);
        if (list == null) return;
        if (list.Count == 0) { ctx.Reply(L.T(lang, "list.empty")); return; }
        ctx.Reply(L.T(lang, "list.head", list.Count));
        for (int i = 0; i < list.Count; i++)
        {
            var (c, dist, dir) = list[i];
            ctx.Reply(L.T(lang, "list.item", i + 1, Routes.Name(lang, c.Route, c.Patrol), Math.Round(dist), dir, c.Note.In(lang)));
        }
    }

    [Command("mark", shortHand: "m", usage: "[number]", description: "Map marker on a caravan, the nearest by default")]
    public void Mark(ChatCommandContext ctx, int number = 1)
    {
        var lang = Lang.Of(ctx);
        var list = Sorted(ctx, out var em);
        if (list == null) return;
        if (list.Count == 0) { ctx.Reply(L.T(lang, "list.empty")); return; }
        if (number < 1 || number > list.Count) { ctx.Reply(L.T(lang, "mark.bad", number)); return; }

        var (c, dist, dir) = list[number - 1];
        SetMarker(ctx, em, c.Pos);
        ctx.Reply(L.T(lang, "mark.ok", Routes.Name(lang, c.Route, c.Patrol), Math.Round(dist), dir, c.Note.In(lang)));
    }

    // The player's own map marker (the one set by clicking the map), with the path to it.
    static void SetMarker(ChatCommandContext ctx, EntityManager em, float3 pos)
    {
        var e = em.CreateEntity(
            Ct<FromCharacter>.RW,
            Ct<SetMapMarkerEvent>.RW,
            Ct<ReceiveNetworkEventTag>.RW);
        em.SetComponentData(e, new FromCharacter { User = ctx.Event.SenderUserEntity, Character = ctx.Event.SenderCharacterEntity });
        em.SetComponentData(e, new SetMapMarkerEvent { Position = new float2(pos.x, pos.z) });
    }

    [Command("routemark", shortHand: "rm", usage: "<number from .cv routes>", description: "Map marker on the point of a route from .cv routes")]
    public void RouteMark(ChatCommandContext ctx, int number)
    {
        var lang = Lang.Of(ctx);
        if (!RouteCommandAllowed(ctx) || !Ready(ctx, out var em, out var prefabs)) return;
        var routes = Routes.All(em, prefabs);
        if (number < 1 || number > routes.Count) { ctx.Reply(L.T(lang, "start.bad", number)); return; }
        var r = routes[number - 1];
        var pos = Caravans.PositionOf(em, r.Patrol);
        float3 me = Caravans.PositionOf(em, ctx.Event.SenderCharacterEntity);
        SetMarker(ctx, em, pos);
        ctx.Reply(L.T(lang, "routemark.ok", Routes.Name(lang, r), Math.Round(Caravans.Dist2(pos, me)), Caravans.Direction(lang, pos.x - me.x, pos.z - me.z)));
    }

    // .cv routes and .cv rm show where every route is; RouteCommandsForAll = false keeps them for admins.
    static bool RouteCommandAllowed(ChatCommandContext ctx)
    {
        var lang = Lang.Of(ctx);
        if (Settings.RouteCommandsForAll.Value || ctx.Event.User.IsAdmin) return true;
        ctx.Reply(L.T(lang, "routes.adminonly"));
        return false;
    }

    [Command("routes", shortHand: "r", description: "All carriage routes with region, level, act and state")]
    public void RoutesList(ChatCommandContext ctx)
    {
        var lang = Lang.Of(ctx);
        if (!RouteCommandAllowed(ctx) || !Ready(ctx, out var em, out var prefabs)) return;
        var routes = Routes.All(em, prefabs);
        float3 me = Caravans.PositionOf(em, ctx.Event.SenderCharacterEntity);
        ctx.Reply(L.T(lang, "routes.head", routes.Count));
        for (int i = 0; i < routes.Count; i++)
        {
            var r = routes[i];
            var pos = Caravans.PositionOf(em, r.Patrol);
            var act = Acts.OfRoute(em, prefabs, r);
            string stops = Routes.StopText(lang, Routes.StopsOf(em, r.Patrol));
            ctx.Reply(L.T(lang, "routes.item", i + 1, Routes.Name(lang, r), r.Key, act.Level, L.ActList(lang, act.Acts), act.Manual ? L.T(lang, "routes.manual") : "",
                          Routes.Describe(lang, em, r.Patrol), Starter.IsRunning(r.Patrol) ? L.T(lang, "routes.running") : "",
                          Math.Round(Caravans.Dist2(pos, me)), Caravans.Direction(lang, pos.x - me.x, pos.z - me.z),
                          stops.Length > 0 ? ", " + stops : "", Settings.Excluded(r.Key) ? L.T(lang, "routes.excluded") : "",
                          L.Region(lang, ActLogic.Region(r.Key))));
        }
    }

    [Command("act", shortHand: "a", description: "Your act (by V Blood or level, see PlayerAct) and the carriage routes you get")]
    public void Act(ChatCommandContext ctx)
    {
        var lang = Lang.Of(ctx);
        if (!Ready(ctx, out var em, out var prefabs)) return;
        var mine = Acts.OfUser(em, prefabs, ctx.Event.SenderUserEntity);
        string by = mine.ByLevel ? L.T(lang, "act.bylevel", mine.Level) : "";
        string text = mine.Boss == null
            ? L.T(lang, "act.none", L.Act(lang, mine.Act), by)
            : L.T(lang, "act.mine", L.Act(lang, mine.Act), by, mine.Drunk, mine.Boss, mine.BossLevel);
        if (mine.Opened > 0 && mine.ByVBlood == mine.Opened + 1 && !mine.ByLevel) text += L.T(lang, "act.next", L.Act(lang, mine.Opened), L.Act(lang, mine.ByVBlood));
        if (Settings.PlayerActMode != PlayerActBy.VBlood && !mine.ByLevel && mine.Level > 0) text += L.T(lang, "act.level", mine.Level);
        ctx.Reply(text);

        var online = Acts.Online(em, prefabs);
        if (online.Count > 1 && (Settings.ShowOthersActs.Value || ctx.Event.User.IsAdmin))
        {
            var parts = new List<string>();
            foreach (var p in online) parts.Add($"{p.Name} - {L.Act(lang, p.Act.Act)}");
            foreach (var line in ChatLines.Pack(parts, ", ", L.T(lang, "act.online", ""))) ctx.Reply(line);
        }

        var served = Acts.Served(mine.Act, Acts.WithRoutes(em, prefabs));
        if (served.Count == 0) { ctx.Reply(L.T(lang, "act.noroutes", L.Act(lang, mine.Act))); return; }
        if (!served.Contains(mine.Act)) ctx.Reply(L.T(lang, "act.empty", L.Act(lang, mine.Act), L.ActList(lang, served)));
        var names = new List<string>();
        foreach (var r in Routes.All(em, prefabs))
        {
            if (Settings.Excluded(r.Key)) continue;
            string n = Routes.Name(lang, r);
            if (Acts.OfRoute(em, prefabs, r).Acts.Overlaps(served) && !names.Contains(n)) names.Add(n);
        }
        foreach (var line in ChatLines.Pack(names, ", ", L.T(lang, "act.routes", L.ActList(lang, served), ""))) ctx.Reply(line);
    }

    // ---- admin: map icons ----

    [Command("icon", adminOnly: true, usage: "<merchant|unknown|vblood|number>", description: "Change the caravan map icon. Numbers: .cv icons")]
    public void Icon(ChatCommandContext ctx, string icon)
    {
        var lang = Lang.Of(ctx);
        if (!Ready(ctx, out var em, out var prefabs)) return;
        if (!Settings.TryParseIcon(icon, out int guid)) { ctx.Reply(L.T(lang, "icon.bad", icon, Settings.IconPresetNames)); return; }
        var pg = new PrefabGUID(guid);
        if (!prefabs._PrefabGuidToEntityMap.TryGetValue(pg, out Entity prefab) || !Caravans.Has<MapIconData>(em, prefab))
        {
            ctx.Reply(L.T(lang, "icon.notmap", icon));
            return;
        }
        int current = Settings.IconGuid;
        if (current != guid)
        {
            var old = Icons.Ours();
            old.Remove(guid);
            Settings.OldIcons.Value = string.Join(",", old);
            Settings.Icon.Value = icon.Trim();
            Settings.Save();
        }
        Tracker.Tick(false);
        if (!prefabs._PrefabLookupMap.TryGetName(pg, out string name)) name = "?";
        ctx.Reply(L.T(lang, "icon.ok", name, guid) + " " + Tracker.LastStats(lang));
    }

    [Command("icons", adminOnly: true, usage: "[filter] [page]", description: "Map icons of the game with their numbers, for .cv icon")]
    public void IconList(ChatCommandContext ctx, string filter = "all", int page = 1)
    {
        var lang = Lang.Of(ctx);
        if (!Ready(ctx, out _, out var prefabs)) return;
        bool any = filter.Equals("all", StringComparison.OrdinalIgnoreCase);
        var list = new List<(string Name, int Guid)>();
        foreach (var kv in prefabs._PrefabGuidToEntityMap)
        {
            if (!prefabs._PrefabLookupMap.TryGetName(kv.Key, out string name) || string.IsNullOrEmpty(name)) continue;
            if (!name.StartsWith("MapIcon", StringComparison.OrdinalIgnoreCase)) continue;
            if (!any && name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
            list.Add((name, kv.Key.GuidHash));
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        foreach (var i in list) Plugin.Info($"CarriageTracker icon: {i.Name} {i.Guid}");

        const int perPage = 15;
        int pages = Math.Max(1, (list.Count + perPage - 1) / perPage);
        page = Math.Clamp(page, 1, pages);
        ctx.Reply(L.T(lang, "icons.head", list.Count, page, pages));
        for (int i = (page - 1) * perPage; i < list.Count && i < page * perPage; i++)
            ctx.Reply($"{list[i].Name} ({list[i].Guid})");
    }

    [Command("mode", adminOnly: true, usage: "<direct|proxy>", description: "direct - icon on the caravan itself, proxy - on a separate marker")]
    public void Mode(ChatCommandContext ctx, string mode)
    {
        var lang = Lang.Of(ctx);
        mode = mode.ToLowerInvariant();
        if (mode != "direct" && mode != "proxy") { ctx.Reply(L.T(lang, "mode.bad")); return; }
        Settings.IconMode.Value = mode;
        Settings.Save();
        if (!Tracker.Tick(false)) { ctx.Reply(L.T(lang, "world.notready")); return; }
        ctx.Reply(Tracker.LastStats(lang));
    }

    [Command("stats", adminOnly: true, description: "Results of the last check")]
    public void Stats(ChatCommandContext ctx) => ctx.Reply(Tracker.LastStats(Lang.Of(ctx)));

    [Command("refresh", adminOnly: true, description: "Fast and full caravan search right now")]
    public void Refresh(ChatCommandContext ctx)
    {
        var lang = Lang.Of(ctx);
        if (!Tracker.Tick(true)) { ctx.Reply(L.T(lang, "world.notready")); return; }
        ctx.Reply(Tracker.LastStats(lang));
    }

    // Personal: every player picks their own language. The default for players who haven't is General.Language.
    [Command("lang", usage: "<en|ru>", description: "Your language of the mod's chat messages")]
    public void Language(ChatCommandContext ctx, string code)
    {
        code = code.Trim().ToLowerInvariant();
        if (!L.Known(code)) { ctx.Reply("en | ru"); return; }
        bool saved = Languages.Set(ctx.Event.User.PlatformId, code);
        var lang = new Lang(code);
        ctx.Reply(saved ? L.T(lang, "lang.ok") : L.T(lang, "lang.ok") + " " + L.T(lang, "lang.unsaved"));
    }

    // ---- admin: starting caravans ----

    [Command("start", adminOnly: true, usage: "<number from .cv routes>", description: "Start a caravan on a route now")]
    public void StartCaravan(ChatCommandContext ctx, int number)
    {
        var lang = Lang.Of(ctx);
        if (!Ready(ctx, out var em, out var prefabs)) return;
        var routes = Routes.All(em, prefabs);
        if (number < 1 || number > routes.Count) { ctx.Reply(L.T(lang, "start.bad", number)); return; }
        ctx.Reply(Starter.Start(lang, em, routes[number - 1], ctx.Event.SenderUserEntity));
    }

    [Command("startstatus", shortHand: "ss", adminOnly: true, description: "How the started caravans are doing")]
    public void StartStatus(ChatCommandContext ctx)
    {
        var lang = Lang.Of(ctx);
        if (!Ready(ctx, out var em, out _)) return;
        foreach (var line in Starter.Status(lang, em)) ctx.Reply(line);
    }

    [Command("stop", adminOnly: true, description: "Stop tracking all started caravans and restore normal route settings")]
    public void StopAll(ChatCommandContext ctx)
    {
        var lang = Lang.Of(ctx);
        if (!Ready(ctx, out var em, out _)) return;
        ctx.Reply(L.T(lang, "stop.ok", Starter.StopAll(em)));
    }

    [Command("auto", adminOnly: true, usage: "[minutes]", description: "Auto-start: no number - status, number - interval in minutes per act, 0 - off")]
    public void Auto(ChatCommandContext ctx, int minutes = -1)
    {
        var lang = Lang.Of(ctx);
        if (minutes >= 0)
        {
            Settings.AutoInterval.Value = minutes;
            Settings.Save();
            if (minutes > 0) AutoStart.Restart();
        }
        int m = Settings.AutoInterval.Value;
        ctx.Reply(SourceText(lang));
        if (Settings.GameOnly) return;
        if (m <= 0) { ctx.Reply(L.T(lang, "auto.off")); return; }
        ctx.Reply(L.T(lang, "auto.status", m, Settings.LeaderScope ? L.T(lang, "auto.scope.leader") : L.T(lang, "auto.scope.all"),
                      Settings.AutoMaxPerAct.Value, Settings.AutoMaxTotal.Value, Starter.AutoCount()));
        var parts = new List<string>();
        for (int act = 1; act <= 4; act++)
            parts.Add(L.T(lang, "auto.actline", L.Act(lang, act), Starter.AutoCount(act), Math.Ceiling(AutoStart.MinutesLeft(act))));
        foreach (var line in ChatLines.Pack(parts, "; ")) ctx.Reply(line);
        if (AutoStart.LastResults.Count > 0)
            foreach (var line in ChatLines.Pack(AutoStart.LastResults.ConvertAll(t => t.In(lang)), "; ", L.T(lang, "auto.last", ""))) ctx.Reply(line);
    }

    static string SourceText(Lang lang) =>
        L.T(lang, "source.now", Settings.ModOnly ? L.T(lang, "source.mod") : Settings.GameOnly ? L.T(lang, "source.game") : L.T(lang, "source.both")) +
        (Settings.ModOnly && Settings.AutoInterval.Value <= 0 ? " " + L.T(lang, "auto.modoff") : "");

    [Command("source", adminOnly: true, usage: "[mod|game|both]", description: "Who starts caravans: only the mod, only the game, or both")]
    public void Source(ChatCommandContext ctx, string source = "")
    {
        var lang = Lang.Of(ctx);
        if (!Ready(ctx, out var em, out var prefabs)) return;
        if (source.Length > 0)
        {
            string v = source.Trim().ToLowerInvariant() switch { "mod" => "Mod", "game" => "Game", "both" => "Both", _ => null };
            if (v == null) { ctx.Reply("mod | game | both"); return; }
            Settings.Source.Value = v;
            Settings.Save();
            Plugin.Info($"CarriageTracker: source set to {v} by command");
            if (v != "Game") AutoStart.Restart();
            Starter.ApplySource(em, prefabs);
        }
        ctx.Reply(SourceText(lang));
    }

    [Command("cleanup", adminOnly: true, description: "Before removing the mod: stop starts, give routes the game's settings back, remove the mod's icons")]
    public void Cleanup(ChatCommandContext ctx)
    {
        var lang = Lang.Of(ctx);
        if (!Ready(ctx, out var em, out var prefabs)) return;
        var r = Starter.Cleanup(em, prefabs);
        Tracker.Tick(false);
        Plugin.Info($"CarriageTracker: cleanup by command: stopped {r.Runs} starts, restored {r.Routes} routes and {r.Units} units, " +
                              "icons removed; the mod changes nothing more until the server restarts");
        ctx.Reply(L.T(lang, "cleanup.ok", r.Runs, r.Routes, r.Units));
    }

    [Command("autonow", shortHand: "an", adminOnly: true, description: "Run auto-start right now, ignoring the interval")]
    public void AutoNow(ChatCommandContext ctx)
    {
        var lang = Lang.Of(ctx);
        if (!Ready(ctx, out var em, out var prefabs)) return;
        var results = AutoStart.Run(em, prefabs, true);
        if (results.Count == 0) { ctx.Reply(L.T(lang, "auto.nothing")); return; }
        foreach (var r in results)
        {
            Plugin.Info($"CarriageTracker auto (command): {r.Text}");
            ctx.Reply(r.Text.In(lang));
        }
    }
}
