using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using MegaCrit.Sts2.Core.Localization;
using FileAccess = Godot.FileAccess;

namespace SpireLingo.SpireLingoCode;

/// <summary>
/// Holds the raw text of every loc table in the target language, keyed the same way as the game's
/// own tables, and lets code format a <see cref="LocString"/> in that language for a moment.
/// </summary>
public static class TargetText
{
    // table name ("cards") -> key ("STRIKE.title") -> raw text in the target language
    private static readonly Dictionary<string, Dictionary<string, string>> Tables = [];

    [ThreadStatic] private static int _activeDepth;

    // Counts raw text lookups served while active, to tell whether the swap reached the formatter.
    [ThreadStatic] private static int _servedCount;

    private static bool _warnedSwapUnused;

    public static string TargetCode => SpireLingoConfig.TargetLanguage.ToString();

    /// <summary>
    /// True while the game is running code inside <see cref="Format{T}"/>.
    /// </summary>
    public static bool IsActive => _activeDepth > 0;

    /// <summary>
    /// The game's current language differs from the target, and some target text was loaded.
    /// </summary>
    public static bool IsUseful => Tables.Count > 0 && LocManager.Instance?.Language != TargetCode;

    internal static void Store(string table, Dictionary<string, string> entries)
    {
        if (!Tables.TryGetValue(table, out var existing))
        {
            existing = [];
            Tables[table] = existing;
        }
        // Later files win, the same as for the game's own tables (mods add to base tables).
        foreach (var (key, value) in entries) existing[key] = value;
    }

    internal static bool TryGetRaw(string table, string key, out string raw)
    {
        raw = "";
        if (!Tables.TryGetValue(table, out var entries) || !entries.TryGetValue(key, out var found)) return false;
        raw = found;
        _servedCount++;
        return true;
    }

    /// <summary>
    /// Runs <paramref name="produce"/> with every loc table lookup answered in the target language,
    /// so anything it formats (with the current numbers filled in) comes out in that language.
    /// </summary>
    public static T Format<T>(Func<T> produce)
    {
        _activeDepth++;
        var servedBefore = _servedCount;
        try
        {
            return produce();
        }
        finally
        {
            _activeDepth--;
            if (_servedCount == servedBefore && !_warnedSwapUnused)
            {
                _warnedSwapUnused = true;
                MainFile.Logger.Warn("Formatting ran without reading any target-language text; " +
                                     "the game may not read text through LocTable.GetRawText.");
            }
        }
    }

    /// <summary>
    /// Reads a loc table file straight from the game's files, for languages the game did not load.
    /// </summary>
    internal static Dictionary<string, string>? ReadFile(string path)
    {
        if (!FileAccess.FileExists(path)) return null;
        try
        {
            // JsonDocument rather than JsonSerializer: the game may switch off reflection-based serialization.
            using var doc = JsonDocument.Parse(FileAccess.GetFileAsString(path));
            return doc.RootElement.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.String)
                .ToDictionary(p => p.Name, p => p.Value.GetString()!);
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"Could not read {path}: {e.Message}");
            return null;
        }
    }
}

/// <summary>
/// Whenever the game loads a loc file in its own language, also loads the same file in the target
/// language. The game loads English first underneath every language, so English often arrives on its own.
/// </summary>
// LoadTable is private, so it is named by string.
[HarmonyPatch(typeof(LocManager), "LoadTable")]
static class CaptureTargetTablesPatch
{
    [ThreadStatic] private static bool _loadingTarget;

    // Static in the game (a Dictionary<string, string> from a file path).
    private static readonly MethodInfo? LoadTable = AccessTools.Method(typeof(LocManager), "LoadTable");

    [HarmonyPostfix]
    [HarmonyPriority(Priority.Last)] // after BaseLib's SimpleLoc has processed the text
    static void Postfix(string path, Dictionary<string, string>? __result)
    {
        if (_loadingTarget || __result == null) return;

        var (lang, table) = ParsePath(path);
        if (lang == null || table == null) return;

        var target = TargetText.TargetCode;
        if (lang == target)
        {
            TargetText.Store(table, __result);
            return;
        }

        var current = LocManager.Instance?.Language;
        if (current != null && lang != current) return;

        var targetPath = path.Replace($"/localization/{lang}/", $"/localization/{target}/");
        _loadingTarget = true;
        try
        {
            // Going through LoadTable keeps the game's and BaseLib's processing; fall back to a plain read.
            var entries = LoadThroughGame(targetPath) ?? TargetText.ReadFile(targetPath);
            if (entries is { Count: > 0 }) TargetText.Store(table, entries);
        }
        finally
        {
            _loadingTarget = false;
        }
    }

    private static Dictionary<string, string>? LoadThroughGame(string path)
    {
        try
        {
            if (LoadTable == null) return null;
            var instance = LoadTable.IsStatic ? null : LocManager.Instance;
            if (!LoadTable.IsStatic && instance == null) return null;
            return LoadTable.Invoke(instance, [path]) as Dictionary<string, string>;
        }
        catch (Exception e)
        {
            MainFile.Logger.Debug($"LoadTable failed for {path}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// "res://localization/deu/cards.json" -> ("deu", "cards"); also works for mods' "res://Mod/localization/...".
    /// </summary>
    internal static (string? lang, string? table) ParsePath(string path)
    {
        var parts = path.Replace('\\', '/').Split('/');
        var index = Array.IndexOf(parts, "localization");
        if (index < 0 || index + 2 >= parts.Length) return (null, null);
        var file = parts[^1];
        if (!file.EndsWith(".json")) return (null, null);
        return (parts[index + 1], file[..^".json".Length]);
    }
}

/// <summary>
/// While <see cref="TargetText.IsActive"/>, answers raw text lookups from the target language.
/// Runs before BaseLib's missing-key patch so keys missing in the game's language still resolve.
/// </summary>
[HarmonyPatch(typeof(LocTable), nameof(LocTable.GetRawText))]
static class TargetRawTextPatch
{
    [HarmonyPrefix]
    [HarmonyPriority(Priority.First)]
    static bool Prefix(string key, string ____name, ref string __result)
    {
        if (!TargetText.IsActive) return true;
        if (!TargetText.TryGetRaw(____name, key, out var raw)) return true;
        __result = raw;
        return false;
    }
}
