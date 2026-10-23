using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Godot;
using MegaCrit.Sts2.Core.Localization;

namespace SpireLingo.SpireLingoCode.Discovery;

/// <summary>
/// Where a word stands for the player. Words without a record are unseen.
/// </summary>
internal enum WordState
{
    Seen,
    LookedUp,
    Learning,
    Recognised,
    Known,
    Ignored
}

internal sealed class WordRecord
{
    public required string Lemma { get; init; }
    public WordState State { get; set; }
    public int Lookups { get; set; }
    public int Seen { get; set; }
    public DateTime FirstSeen { get; set; }
    public DateTime LastSeen { get; set; }

    /// <summary>
    /// Game text lines the word was met in ("cards/STRIKE.description#0"), newest last.
    /// </summary>
    public List<string> Contexts { get; } = [];

    // Spaced review, for later.
    public DateTime? ReviewDue { get; set; }
    public int ReviewInterval { get; set; }
    public int ReviewStreak { get; set; }

    public List<(DateTime date, WordState state)> History { get; } = [];
}

/// <summary>
/// The player's words for one language pair, saved as JSON in the game's user folder
/// (user://SpireLingo/words_deu_eng.json). Changes are written about a second later, and right away when
/// discovery mode closes or the game quits.
/// </summary>
internal static class WordStore
{
    private const int MaxContexts = 5;
    private const ulong SaveDelayMs = 1000;

    private static readonly Dictionary<string, WordRecord> Words = new(StringComparer.Ordinal);
    private static string? _path;
    private static string? _loadedFor;
    private static ulong? _dirtySince;

    /// <summary>
    /// Every saved word, for the vocabulary screen.
    /// </summary>
    public static IReadOnlyCollection<WordRecord> All
    {
        get
        {
            EnsureLoaded();
            return Words.Values;
        }
    }

    public static WordRecord? Get(string lemma)
    {
        EnsureLoaded();
        return Words.GetValueOrDefault(lemma);
    }

    /// <summary>
    /// A click on the word in discovery mode. Raises an unseen or seen word to looked up, and sends a
    /// recognised or known word back to learning, since looking it up means it wasn't known after all.
    /// </summary>
    public static WordRecord RecordLookup(string lemma, string? context)
    {
        var record = Touch(lemma, context);
        record.Lookups++;
        switch (record.State)
        {
            case WordState.Seen:
                SetState(record, WordState.LookedUp);
                break;
            case WordState.Recognised or WordState.Known:
                SetState(record, WordState.Learning);
                break;
        }
        MarkDirty();
        return record;
    }

    /// <summary>
    /// The word was on screen in a line the player was reading in discovery mode.
    /// </summary>
    public static void RecordSeen(string lemma, string? context)
    {
        Touch(lemma, context).Seen++;
        MarkDirty();
    }

    /// <summary>
    /// A state the player chose in the popup.
    /// </summary>
    public static void Choose(WordRecord record, WordState state)
    {
        SetState(record, state);
        MarkDirty();
    }

    /// <summary>
    /// Writes pending changes once they are a second old. Called every frame.
    /// </summary>
    public static void Tick()
    {
        if (_dirtySince is { } since && Time.GetTicksMsec() - since >= SaveDelayMs) Flush();
    }

    public static void Flush()
    {
        if (_dirtySince == null || _path == null) return;
        _dirtySince = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            // Write beside the file and swap it in, so a crash mid-write can't leave half a file.
            var temp = _path + ".tmp";
            File.WriteAllText(temp, Serialize(), new UTF8Encoding(false));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception e)
        {
            MainFile.Logger.Warn($"Words: could not save {_path}: {e.Message}");
        }
    }

    private static WordRecord Touch(string lemma, string? context)
    {
        EnsureLoaded();
        var now = DateTime.UtcNow;
        if (!Words.TryGetValue(lemma, out var record))
        {
            record = new WordRecord { Lemma = lemma, State = WordState.Seen, FirstSeen = now };
            record.History.Add((now, WordState.Seen));
            Words[lemma] = record;
        }
        record.LastSeen = now;
        if (context != null)
        {
            record.Contexts.Remove(context);
            record.Contexts.Add(context);
            if (record.Contexts.Count > MaxContexts) record.Contexts.RemoveAt(0);
        }
        return record;
    }

    private static void SetState(WordRecord record, WordState state)
    {
        if (record.State == state) return;
        record.State = state;
        StateVersion++;
        record.History.Add((DateTime.UtcNow, state));
    }

    /// <summary>
    /// Goes up whenever a word's state changes (or another word list is loaded), so on-screen marks update.
    /// </summary>
    public static int StateVersion { get; private set; }

    private static void MarkDirty() => _dirtySince ??= Time.GetTicksMsec();

    private static void EnsureLoaded()
    {
        var from = LocManager.Instance?.Language ?? "unknown";
        var key = $"{from}_{TargetText.TargetCode}";
        if (_loadedFor == key) return;

        Flush();
        _loadedFor = key;
        _path = ProjectSettings.GlobalizePath($"user://SpireLingo/words_{key}.json");
        Words.Clear();
        StateVersion++;
        if (!File.Exists(_path)) return;

        try
        {
            Deserialize(File.ReadAllText(_path, Encoding.UTF8));
            MainFile.Logger.Info($"Words: loaded {Words.Count} words from {_path}");
        }
        catch (Exception e)
        {
            // Keep the unreadable file for the player and start empty rather than lose the game.
            Words.Clear();
            try
            {
                File.Copy(_path, _path + ".bak", overwrite: true);
            }
            catch
            {
                // Nothing more to do; the original stays in place until the next save.
            }
            MainFile.Logger.Warn($"Words: {_path} could not be read ({e.Message}); kept a copy as .bak and started empty");
        }
    }

    private static string Serialize()
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            json.WriteStartObject();
            json.WriteNumber("version", 1);
            json.WriteStartObject("words");
            foreach (var record in Words.Values.OrderBy(r => r.Lemma, StringComparer.Ordinal))
            {
                json.WriteStartObject(record.Lemma);
                json.WriteString("state", Name(record.State));
                json.WriteNumber("lookups", record.Lookups);
                json.WriteNumber("seen", record.Seen);
                json.WriteString("firstSeen", record.FirstSeen);
                json.WriteString("lastSeen", record.LastSeen);
                json.WriteStartArray("contexts");
                foreach (var context in record.Contexts) json.WriteStringValue(context);
                json.WriteEndArray();
                json.WriteStartObject("review");
                if (record.ReviewDue is { } due) json.WriteString("due", due);
                else json.WriteNull("due");
                json.WriteNumber("interval", record.ReviewInterval);
                json.WriteNumber("streak", record.ReviewStreak);
                json.WriteEndObject();
                json.WriteStartArray("history");
                foreach (var (date, state) in record.History)
                {
                    json.WriteStartObject();
                    json.WriteString("date", date);
                    json.WriteString("state", Name(state));
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteEndObject();
            }
            json.WriteEndObject();
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Deserialize(string text)
    {
        using var doc = JsonDocument.Parse(text);
        foreach (var word in doc.RootElement.GetProperty("words").EnumerateObject())
        {
            var w = word.Value;
            var record = new WordRecord
            {
                Lemma = word.Name,
                State = Parse(w.GetProperty("state").GetString()),
                Lookups = w.TryGetProperty("lookups", out var lookups) ? lookups.GetInt32() : 0,
                Seen = w.TryGetProperty("seen", out var seen) ? seen.GetInt32() : 0,
                FirstSeen = w.TryGetProperty("firstSeen", out var first) ? first.GetDateTime() : DateTime.UtcNow,
                LastSeen = w.TryGetProperty("lastSeen", out var last) ? last.GetDateTime() : DateTime.UtcNow
            };
            if (w.TryGetProperty("contexts", out var contexts))
                record.Contexts.AddRange(contexts.EnumerateArray().Select(c => c.GetString() ?? "").Where(c => c.Length > 0));
            if (w.TryGetProperty("review", out var review))
            {
                if (review.TryGetProperty("due", out var due) && due.ValueKind == JsonValueKind.String) record.ReviewDue = due.GetDateTime();
                if (review.TryGetProperty("interval", out var interval)) record.ReviewInterval = interval.GetInt32();
                if (review.TryGetProperty("streak", out var streak)) record.ReviewStreak = streak.GetInt32();
            }
            if (w.TryGetProperty("history", out var history))
                foreach (var change in history.EnumerateArray())
                    record.History.Add((change.GetProperty("date").GetDateTime(), Parse(change.GetProperty("state").GetString())));
            Words[record.Lemma] = record;
        }
    }

    private static string Name(WordState state) => state switch
    {
        WordState.Seen => "seen",
        WordState.LookedUp => "lookedUp",
        WordState.Learning => "learning",
        WordState.Recognised => "recognised",
        WordState.Known => "known",
        _ => "ignored"
    };

    private static WordState Parse(string? name) => name switch
    {
        "lookedUp" => WordState.LookedUp,
        "learning" => WordState.Learning,
        "recognised" => WordState.Recognised,
        "known" => WordState.Known,
        "ignored" => WordState.Ignored,
        _ => WordState.Seen
    };
}
