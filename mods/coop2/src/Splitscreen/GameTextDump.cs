using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace EasyDeliveryCoLanCoop;

/// <summary>
/// Developer tool: writes every readable text that the game's own scripts hold in the loaded scene
/// (NPC dialog lines, tutorial text, labels...) to BepInEx\\game_texts_dump.json, with the object and
/// field each one comes from. The big localization tables are exported separately from the data files.
/// </summary>
internal static class GameTextDump
{
    private sealed class Row
    {
        public string Script = "", Object = "", Field = "", Text = "";
    }

    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Assembly GameAssembly = typeof(sHUD).Assembly;

    internal static void Run()
    {
        try
        {
            var rows = new List<Row>();
            var seen = new HashSet<string>();
            int scripts = 0;

            foreach (var mb in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (mb == null) continue;
                var type = mb.GetType();
                if (type.Assembly != GameAssembly) continue;
                scripts++;
                int before = rows.Count;
                Walk(mb, type.Name, PathOf(mb.transform), "", 0, rows);
                // the same text on many copies of an object (tires, lights...) is noise
                for (int i = rows.Count - 1; i >= before; i--)
                    if (!seen.Add(rows[i].Script + "|" + rows[i].Field + "|" + rows[i].Text))
                        rows.RemoveAt(i);
            }

            var sb = new StringBuilder();
            sb.Append("[\n");
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                sb.Append("  {\"script\":").Append(Q(r.Script))
                  .Append(",\"object\":").Append(Q(r.Object))
                  .Append(",\"field\":").Append(Q(r.Field))
                  .Append(",\"text\":").Append(Q(r.Text)).Append('}');
                sb.Append(i < rows.Count - 1 ? ",\n" : "\n");
            }
            sb.Append("]\n");

            string path = Path.Combine(BepInEx.Paths.BepInExRootPath, "game_texts_dump.json");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            Plugin.Log.LogInfo($"[TextDump] {rows.Count} texts from {scripts} game scripts -> {path}");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[TextDump] failed: {ex}");
        }
    }

    private static void Walk(object obj, string script, string objectPath, string path, int depth, List<Row> rows)
    {
        if (obj == null || depth > 4) return;
        var type = obj.GetType();

        foreach (var f in type.GetFields(Fields))
        {
            if (f.IsStatic || f.IsNotSerialized) continue;
            bool serialized = f.IsPublic || f.GetCustomAttribute<SerializeField>() != null;
            if (!serialized) continue;

            object? value;
            try { value = f.GetValue(obj); } catch { continue; }
            if (value == null) continue;

            string fieldPath = path.Length == 0 ? f.Name : path + "." + f.Name;
            Visit(value, f.FieldType, script, objectPath, fieldPath, depth, rows);
        }
    }

    private static void Visit(object value, Type declared, string script, string objectPath, string path, int depth, List<Row> rows)
    {
        if (value is string s)
        {
            if (IsText(s)) rows.Add(new Row { Script = script, Object = objectPath, Field = path, Text = s });
            return;
        }

        if (value is UnityEngine.Object) return; // references to other objects, not data
        var t = value.GetType();
        if (t.IsPrimitive || t.IsEnum || t == typeof(decimal)) return;
        if (t.Namespace != null && t.Namespace.StartsWith("UnityEngine")) return; // Vector3, Rect, curves...

        if (value is IEnumerable list && !(value is string))
        {
            int i = 0;
            foreach (var item in list)
            {
                if (item != null && i < 300)
                    Visit(item, item.GetType(), script, objectPath, path + "[" + i + "]", depth + 1, rows);
                i++;
            }
            return;
        }

        if (t.IsClass || t.IsValueType)
        {
            if (t.Assembly == GameAssembly || t.IsSerializable)
                Walk(value, script, objectPath, path, depth + 1, rows);
        }
    }

    private static bool IsText(string s)
    {
        if (s.Length < 3) return false;
        int letters = 0;
        foreach (char c in s) if (char.IsLetter(c)) letters++;
        return letters >= 2;
    }

    private static string PathOf(Transform t)
    {
        string p = t.name;
        for (int i = 0; i < 4 && t.parent != null; i++) { t = t.parent; p = t.name + "/" + p; }
        return p;
    }

    private static string Q(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }
}
