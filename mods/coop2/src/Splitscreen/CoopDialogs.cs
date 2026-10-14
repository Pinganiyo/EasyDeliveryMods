using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;

namespace EasyDeliveryCoLanCoop;

/// <summary>
/// The custom two-player NPC lines (dialogs\custom\coop_lines.csv, compiled into the plugin).
/// The CSV has one column per language: "text" is English (and the fallback), "text_es" is Latin American
/// Spanish; the line shown follows the language chosen in the game. Other languages can be added as
/// "text_<code>" columns (codes: jp, sch, kor, de, fr, rus, es, pl, uk, tr, pt).
/// To change lines without rebuilding, put an edited copy at BepInEx\config\coop_lines.csv:
/// it is used instead of the built-in one the next time the game starts.
/// </summary>
internal static class CoopDialogs
{
    internal sealed class Set
    {
        public string Greeting = "";
        public string Sendoff = "";
        public readonly List<string> Interactions = new();
    }

    private sealed class Tables
    {
        public readonly Dictionary<string, Set> P2 = new();
        public readonly Dictionary<string, Set> Duo = new();
    }

    // The game's language numbers (LocalizationDictionary.selectedLanguage), in the order of its sheets.
    private static readonly string[] LanguageCodes =
        { "en", "jp", "sch", "kor", "de", "fr", "rus", "es", "pl", "uk", "tr", "pt" };

    private static List<List<string>>? _rows;
    private static readonly Dictionary<string, Tables> ByLanguage = new();
    private static readonly Dictionary<string, int> Counters = new();

    private static string CurrentLanguage()
    {
        try
        {
            int i = LocalizationDictionary.localization.selectedLanguage;
            return i >= 0 && i < LanguageCodes.Length ? LanguageCodes[i] : "en";
        }
        catch
        {
            return "en";
        }
    }

    internal static Set? Get(string npcName, bool duo)
    {
        var tables = TablesFor(CurrentLanguage());
        var table = duo ? tables.Duo : tables.P2;
        return table.TryGetValue(npcName.Trim().ToLowerInvariant(), out var set) ? set : null;
    }

    /// <summary>The next interaction line for this NPC, going through all of them in turn.</summary>
    internal static string NextInteraction(string npcName, bool duo)
    {
        var set = Get(npcName, duo);
        if (set == null || set.Interactions.Count == 0) return "";
        string key = (duo ? "duo:" : "p2:") + npcName.ToLowerInvariant();
        Counters.TryGetValue(key, out int n);
        Counters[key] = n + 1;
        return set.Interactions[n % set.Interactions.Count];
    }

    private static Tables TablesFor(string language)
    {
        if (ByLanguage.TryGetValue(language, out var cached)) return cached;

        if (_rows == null)
        {
            string text = ReadSource(out string source);
            _rows = ParseCsv(text);
            Plugin.Log.LogInfo($"[CoopDialogs] {Math.Max(0, _rows.Count - 1)} rows read from {source}.");
        }

        var tables = new Tables();
        int column = -1;
        if (_rows.Count > 0 && language != "en")
            column = _rows[0].IndexOf("text_" + language);

        int used = 0, translated = 0;
        for (int i = 1; i < _rows.Count; i++) // row 0 is the header
        {
            var r = _rows[i];
            if (r.Count < 6) continue;
            var table = r[3] == "duo" ? tables.Duo : r[3] == "p2" ? tables.P2 : null;
            if (table == null) continue;

            string line = r[5];
            if (column >= 0 && column < r.Count && r[column].Length > 0)
            {
                line = r[column];
                translated++;
            }

            string name = r[1].Trim().ToLowerInvariant();
            if (!table.TryGetValue(name, out var set))
                table[name] = set = new Set();

            switch (r[4])
            {
                case "greeting": set.Greeting = line; break;
                case "sendoff": set.Sendoff = line; break;
                default:
                    if (r[4].StartsWith("interaction")) set.Interactions.Add(line);
                    break;
            }
            used++;
        }

        ByLanguage[language] = tables;
        Plugin.Log.LogInfo($"[CoopDialogs] Language '{language}': {used} lines for {tables.P2.Count} NPCs ({translated} translated, the rest in English).");
        return tables;
    }

    private static string ReadSource(out string source)
    {
        try
        {
            string over = Path.Combine(BepInEx.Paths.ConfigPath, "coop_lines.csv");
            if (File.Exists(over))
            {
                source = over;
                return File.ReadAllText(over, Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[CoopDialogs] Could not read the override file: {ex.Message}");
        }

        source = "the built-in table";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("coop_lines.csv");
        if (stream == null) return "";
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>Small RFC 4180 reader: quoted fields, doubled quotes, commas and newlines inside quotes.</summary>
    private static List<List<string>> ParseCsv(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else quoted = false;
                }
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString());
                field.Clear();
                if (row.Count > 1 || row[0].Length > 0) rows.Add(row);
                row = new List<string>();
            }
            else field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }
}
