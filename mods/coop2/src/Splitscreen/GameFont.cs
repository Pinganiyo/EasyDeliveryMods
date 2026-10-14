using System;
using System.Collections.Generic;
using System.Xml;
using UnityEngine;

namespace EasyDeliveryCoLanCoop;

/// <summary>
/// Draws text in IMGUI with the game's own bitmap font (the sheet + glyph metrics that
/// MiniRenderer/sFancyText use for the HUD), so Player 2's labels match Player 1's.
/// </summary>
internal static class GameFont
{
    private sealed class Glyph
    {
        public int Sx, Sy, Sw, Sh, Xo, Yo, W;
    }

    private static Dictionary<int, Glyph>? _fancy;
    private static Dictionary<int, Glyph>? _legacy;
    private static Texture? _fancySheet;
    private static Texture? _legacySheet;
    private static Texture? _spriteSheet;
    private static float _nextTry;

    internal static bool Ready
    {
        get
        {
            if (_fancy != null) return true;
            if (Time.unscaledTime < _nextTry) return false;
            _nextTry = Time.unscaledTime + 1f;
            TryLoad();
            return _fancy != null;
        }
    }

    private static void TryLoad()
    {
        try
        {
            foreach (var r in UnityEngine.Object.FindObjectsByType<MiniRenderer>(FindObjectsSortMode.None))
            {
                if (r == null || r.fontDataFile == null || r.fancyFontSheet == null) continue;

                var xml = new XmlDocument();
                xml.LoadXml(r.fontDataFile.text);
                var fancy = new Dictionary<int, Glyph>();
                var nodes = xml.SelectNodes("font/chars/char");
                if (nodes == null) continue;
                foreach (XmlNode n in nodes)
                {
                    var a = n.Attributes!;
                    int id = int.Parse(a["id"]!.Value);
                    int xa = int.Parse(a["xadvance"]!.Value);
                    int w = xa;
                    if (id == 12289 || id == 12290 || id == 65311 || id == 65281) w += 4; // same tweaks as sFancyText.CharInfo
                    fancy[id] = new Glyph
                    {
                        Sx = int.Parse(a["x"]!.Value), Sy = int.Parse(a["y"]!.Value),
                        Sw = int.Parse(a["width"]!.Value), Sh = int.Parse(a["height"]!.Value),
                        Xo = int.Parse(a["xoffset"]!.Value), Yo = int.Parse(a["yoffset"]!.Value),
                        W = w,
                    };
                }

                var legacy = new Dictionary<int, Glyph>();
                for (int i = 0; i < 96; i++)
                    legacy[i + 32] = new Glyph { Sx = i % 32 * 8, Sy = i / 32 * 8, Sw = 8, Sh = 8, W = 8 };

                _fancySheet = r.fancyFontSheet;
                _legacySheet = r.fontSheet;
                _spriteSheet = r.spriteSheet;
                _legacy = legacy;
                _fancy = fancy;
                return;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[Splitscreen] GameFont load failed: {ex.Message}");
        }
    }

    /// <summary>Translates a label like the HUD does (so languages match P1).</summary>
    internal static string Translate(string text)
    {
        try { return LocalizationDictionary.Translate(text); }
        catch { return text; }
    }

    /// <summary>
    /// Draws one line of text. (x, y) is the top-left of the text box in GUI pixels (or the top-centre
    /// when centered); scale is screen pixels per game pixel.
    /// </summary>
    internal static void Draw(string text, float x, float y, float scale, Color color, bool centered)
    {
        if (!Ready || string.IsNullOrEmpty(text)) return;

        bool legacyFont;
        try { legacyFont = LocalizationDictionary.LegacyFont(); }
        catch { legacyFont = false; }

        var table = legacyFont ? _legacy! : _fancy!;
        var sheet = legacyFont ? _legacySheet : _fancySheet;
        if (sheet == null) return;

        float width = 0f;
        foreach (char ch in text)
            width += Lookup(table, ch).W;

        float cx = centered ? x - width * scale / 2f : x;
        float yOff = legacyFont ? 0f : 0f;
        var prev = GUI.color;
        GUI.color = color;
        foreach (char ch in text)
        {
            var g = Lookup(table, ch);
            var dst = new Rect(cx + g.Xo * scale, y + (g.Yo + yOff) * scale, g.Sw * scale, g.Sh * scale);
            var src = new Rect((float)g.Sx / sheet.width,
                               (sheet.height - g.Sy - g.Sh) / (float)sheet.height,
                               (float)g.Sw / sheet.width,
                               (float)g.Sh / sheet.height);
            GUI.DrawTextureWithTexCoords(dst, sheet, src);
            cx += g.W * scale;
        }
        GUI.color = prev;
    }

    /// <summary>Draws a rect of the game's sprite sheet (sheet pixels) with its top-left at (x, y).</summary>
    internal static void DrawSprite(Rect sheetRect, float x, float y, float scale)
    {
        if (!Ready || _spriteSheet == null) return;
        var dst = new Rect(x, y, sheetRect.width * scale, sheetRect.height * scale);
        var src = new Rect(sheetRect.x / _spriteSheet.width,
                           (_spriteSheet.height - sheetRect.y - sheetRect.height) / _spriteSheet.height,
                           sheetRect.width / _spriteSheet.width,
                           sheetRect.height / _spriteSheet.height);
        var prev = GUI.color;
        GUI.color = Color.white;
        GUI.DrawTextureWithTexCoords(dst, _spriteSheet, src);
        GUI.color = prev;
    }

    private static Glyph Lookup(Dictionary<int, Glyph> table, char c)
    {
        if (table.TryGetValue(c, out var g)) return g;
        return table['@'];
    }
}
