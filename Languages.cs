using System.IO;
using System.Reflection;
using VampireCommandFramework;

namespace CarriageTracker;

// Each player's own chat language (.cv lang), kept in BepInEx/config/CarriageTracker.languages.txt as
// "steamid=en" lines (how the file is read and written: LanguageStore). Players without one get
// General.Language from the config.
static class Languages
{
    static LanguageStore _store;

    static LanguageStore Store => _store ??= new LanguageStore(
        Path.Combine(BepInEx.Paths.ConfigPath, "CarriageTracker.languages.txt"), L.Known,
        Path.Combine(BepInEx.Paths.ConfigPath, "Bazarov.CaravanTracker.languages.txt"))
    {
        Warn = m => Plugin.Warn($"CarriageTracker: {m}"),
    };

    public static string Of(ulong platformId) => Store.Of(platformId);

    // False when the choice couldn't be written to the file yet (it is kept and written later).
    public static bool Set(ulong platformId, string lang) => Store.Set(platformId, lang);
}

// Every command of this mod answers in the language of the player who typed it.
class LanguageMiddleware : CommandMiddleware
{
    static bool Ours(MethodInfo method) => method?.DeclaringType?.Assembly == typeof(Plugin).Assembly;

    public override void BeforeExecute(ICommandContext ctx, CommandAttribute attribute, MethodInfo method)
    {
        if (Ours(method) && ctx is ChatCommandContext chat) L.Use(chat.Event.User.PlatformId);
    }

    public override void AfterExecute(ICommandContext ctx, CommandAttribute attribute, MethodInfo method)
    {
        if (Ours(method)) L.UseDefault();
    }
}
