using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace FlixyPix.Core;

/// <summary>A language FlixyPix speaks: its code (also the name of its translation file) and its own name.</summary>
public sealed record LanguageInfo(string Code, string NativeName, string EnglishName, string CultureName);

/// <summary>Texts of one language. English is the source language: the English text in the code is the key, so English
/// needs no file. Other languages come from an embedded JSON file per language, overlaid by the user's own file;
/// a missing entry falls back to the English key.</summary>
public sealed class Translator
{
    /// <summary>Separates a context from the text in a key, as gettext does; the context is never shown.</summary>
    public const char ContextSeparator = '\u0004';
    /// <summary>Pseudo language for tests: longer, accented, bracketed texts make untranslated and cut-off labels visible.</summary>
    public const string PseudoCode = "qps";

    private readonly Dictionary<string, string> texts;
    private readonly Dictionary<string, Dictionary<string, string>> plurals;
    private readonly CultureInfo formatting;

    private Translator(string language, Dictionary<string, string> texts, Dictionary<string, Dictionary<string, string>> plurals, CultureInfo? formatting)
    {
        Language = language; this.texts = texts; this.plurals = plurals; this.formatting = formatting ?? CultureInfo.CurrentCulture;
    }

    /// <summary>English, the source language: every key is its own text.</summary>
    public static Translator English { get; } = new("en", [], [], null);
    public string Language { get; }
    /// <summary>Problems while loading (an unreadable file of the user); the texts then come from the built-in file.</summary>
    public IReadOnlyList<string> Problems { get; private init; } = [];
    /// <summary>Numbers and dates follow this culture (the regional format of Windows), not the language.</summary>
    public CultureInfo Formatting => formatting;

    /// <summary>Loads a language from the embedded file and, if present, the user's own file in
    /// <paramref name="customDirectory"/> (<c>&lt;code&gt;.json</c>); its entries replace or add single texts.</summary>
    public static Translator Load(string language, string? customDirectory = null, CultureInfo? formatting = null)
    {
        var problems = new List<string>();
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        var plurals = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        if (language != "en" && language != PseudoCode)
        {
            using var stream = typeof(Translator).Assembly.GetManifestResourceStream($"FlixyPix.Core.Languages.{language}.json")
                ?? throw new ArgumentException($"Unknown language '{language}'.", nameof(language));
            Read(JsonDocument.Parse(stream), texts, plurals);
        }
        if (customDirectory != null)
        {
            var custom = Path.Combine(customDirectory, language + ".json");
            try { if (File.Exists(custom)) Read(JsonDocument.Parse(File.ReadAllBytes(custom)), texts, plurals); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                problems.Add($"{custom}: {ex.Message}");
            }
        }
        return new(language, texts, plurals, formatting) { Problems = problems };
    }

    /// <summary>Reads one translation file: <c>"key": "text"</c>, or for plurals <c>"key": { "one": …, "other": … }</c>.</summary>
    public static void Read(JsonDocument document, Dictionary<string, string> texts, Dictionary<string, Dictionary<string, string>> plurals)
    {
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("A translation file holds one JSON object.");
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            if (entry.Value.ValueKind == JsonValueKind.String) { if (entry.Value.GetString() is { Length: > 0 } text) texts[entry.Name] = text; }
            else if (entry.Value.ValueKind == JsonValueKind.Object)
            {
                var forms = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var form in entry.Value.EnumerateObject()) if (form.Value.ValueKind == JsonValueKind.String && form.Value.GetString() is { Length: > 0 } text) forms[form.Name] = text;
                if (forms.Count > 0) plurals[entry.Name] = forms;
            }
        }
    }

    /// <summary>The text of <paramref name="key"/> in this language.</summary>
    public string Text(string key)
    {
        if (texts.TryGetValue(key, out var text)) return text;
        var shown = Shown(key);
        return Language == PseudoCode ? Pseudo(shown) : shown;
    }

    /// <summary>The text of <paramref name="key"/> with its placeholders filled like <see cref="string.Format(IFormatProvider, string, object[])"/>.</summary>
    public string Format(string key, ReadOnlySpan<object?> args) => args.Length == 0 ? Text(key) : string.Format(formatting, Text(key), args);

    /// <summary>The form of <paramref name="other"/> that fits <paramref name="count"/>; <c>{0}</c> is the count,
    /// further placeholders are <paramref name="args"/>. <paramref name="one"/> is the English singular.</summary>
    public string Plural(long count, string one, string other, ReadOnlySpan<object?> args)
    {
        var all = new object?[args.Length + 1];
        all[0] = count; args.CopyTo(all.AsSpan(1));
        return string.Format(formatting, PluralForm(count, one, other), all);
    }

    private string PluralForm(long count, string one, string other)
    {
        if (plurals.TryGetValue(other, out var forms))
        {
            var category = Plurals.Category(Language, count);
            if (forms.TryGetValue(category, out var form) || forms.TryGetValue("other", out form)) return form;
        }
        var shown = Shown(count == 1 ? one : other);
        return Language == PseudoCode ? Pseudo(shown) : shown;
    }

    /// <summary>A key without its context.</summary>
    public static string Shown(string key) => key.IndexOf(ContextSeparator) is var at and >= 0 ? key[(at + 1)..] : key;

    /// <summary>Pseudo text: accented letters, about a third longer, in brackets; placeholders stay intact.</summary>
    public static string Pseudo(string text)
    {
        const string plain = "aeiouAEIOUcnsyCNSY", accented = "àéîõüÀÉÎÕÜçñšýÇÑŠÝ";
        var builder = new System.Text.StringBuilder("[");
        var depth = 0;
        foreach (var c in text)
        {
            if (c == '{') depth++;
            if (depth == 0 && plain.IndexOf(c) is var at and >= 0) builder.Append(accented[at]); else builder.Append(c);
            if (c == '}' && depth > 0) depth--;
        }
        builder.Append('~', Math.Max(1, text.Length / 3)).Append(']');
        return builder.ToString();
    }
}

/// <summary>Plural categories of the CLDR for whole numbers in the languages FlixyPix speaks.</summary>
public static class Plurals
{
    public static string Category(string language, long count)
    {
        // Avoid Math.Abs(long.MinValue), which overflows, even though counts are normally positive.
        var n = count < 0 ? (ulong)(-(count + 1)) + 1 : (ulong)count;
        switch (language)
        {
            case "ja": return "other";
            case "fr": case "pt":
                if (n is 0 or 1) return "one";
                return n % 1_000_000 == 0 ? "many" : "other";
            case "es": case "it":
                if (n == 1) return "one";
                return n != 0 && n % 1_000_000 == 0 ? "many" : "other";
            case "pl":
                if (n == 1) return "one";
                return n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? "few" : "many";
            case "ru":
                if (n % 10 == 1 && n % 100 != 11) return "one";
                return n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? "few" : "many";
            default: return n == 1 ? "one" : "other";
        }
    }

    /// <summary>The forms a translation of a plural needs in <paramref name="language"/>.</summary>
    public static IReadOnlyList<string> Needed(string language) => language switch
    {
        "ja" => ["other"],
        "pl" or "ru" => ["one", "few", "many"],
        "fr" or "es" or "it" or "pt" => ["one", "many", "other"],
        _ => ["one", "other"],
    };
}

/// <summary>The language of the running program. Set once at start; the code reads texts through <see cref="T(string)"/>
/// and <see cref="Tn"/>, which import statically.</summary>
public static class Texts
{
    /// <summary>Setting value: follow the display language of Windows.</summary>
    public const string Automatic = "system";

    public static IReadOnlyList<LanguageInfo> Languages { get; } =
    [
        new("de", "Deutsch", "German", "de-DE"),
        new("en", "English", "English", "en-US"),
        new("fr", "Français", "French", "fr-FR"),
        new("es", "Español", "Spanish", "es-ES"),
        new("it", "Italiano", "Italian", "it-IT"),
        new("pt", "Português (Brasil)", "Portuguese (Brazil)", "pt-BR"),
        new("nl", "Nederlands", "Dutch", "nl-NL"),
        new("pl", "Polski", "Polish", "pl-PL"),
        new("ja", "日本語", "Japanese", "ja-JP"),
        new("ru", "Русский", "Russian", "ru-RU"),
    ];

    public static Translator Current { get; private set; } = Translator.English;

    /// <summary>Folder for the user's own translation files (<c>%LOCALAPPDATA%\FlixyPix\languages</c>).</summary>
    public static string CustomDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), ProductIdentity.Name, "languages");

    public static void Use(Translator translator) => Current = translator;

    /// <summary>The language to use for a setting value: a known code as is, otherwise (<see cref="Automatic"/>, empty or
    /// unknown) the first language along <paramref name="display"/> and its parents (de-AT → de, pt-PT → pt), else English.</summary>
    public static string Resolve(string? choice, CultureInfo display)
    {
        if (choice == Translator.PseudoCode) return choice;
        if (Find(choice) is { } chosen) return chosen.Code;
        for (var culture = display; !string.IsNullOrEmpty(culture.Name); culture = culture.Parent)
            if (Find(culture.TwoLetterISOLanguageName) is { } match) return match.Code;
        return "en";
    }

    public static LanguageInfo? Find(string? code) => Languages.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>The FlixyPix language for the original's <c>Language</c> value in user.config (English names such as "German");
    /// null when FlixyPix does not speak it (Chinese, Danish, Finnish, Korean).</summary>
    public static string? FromOriginal(string? original) =>
        Languages.FirstOrDefault(l => string.Equals(l.EnglishName, original?.Trim(), StringComparison.OrdinalIgnoreCase))?.Code;

    /// <summary>Text in the current language.</summary>
    public static string T(string text) => Current.Text(text);
    /// <summary>Text in the current language with its placeholders filled (regional format of Windows).</summary>
    public static string T(string text, params ReadOnlySpan<object?> args) => Current.Format(text, args);
    /// <summary>Text with a context that tells apart equal English words; the context is not shown.</summary>
    public static string Tc(string context, string text, params ReadOnlySpan<object?> args) => Current.Format(context + Translator.ContextSeparator + text, args);
    /// <summary>The plural form for <paramref name="count"/>; <c>{0}</c> is the count.</summary>
    public static string Tn(long count, string one, string other, params ReadOnlySpan<object?> args) => Current.Plural(count, one, other, args);
    /// <summary>Marks an English text that the code keeps as an identifier and translates where it shows it (with <see cref="T(string)"/>).</summary>
    public static string TKey(string text) => text;
}
