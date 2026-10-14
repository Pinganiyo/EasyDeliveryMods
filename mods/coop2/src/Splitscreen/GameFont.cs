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
    internal static void Draw(string text, float x, float y, float scale, Color color, bool centered) =>
        Draw(text, x, y, scale, scale, color, centered);

    /// <summary>Same as the uniform Draw, with separate horizontal and vertical scales (the HUD can be squeezed).</summary>
    internal static void Draw(string text, float x, float y, float scaleX, float scaleY, Color color, bool centered)
    {
        if (!Ready || string.IsNullOrEmpty(text)) return;

        bool legacyFont;
        try { legacyFont = LocalizationDictionary.LegacyFont(); }
        catch { legacyFont = false; }

        var table = legacyFont ? _legacy! : _fancy!;
        var sheet = Masked(legacyFont ? _legacySheet : _fancySheet);
        if (sheet == null) return;

        float width = 0f;
        foreach (char ch in text)
            width += Lookup(table, ch).W;

        float cx = centered ? x - width * scaleX / 2f : x;
        float yOff = legacyFont ? 0f : 0f;
        var prev = GUI.color;
        GUI.color = color;
        foreach (char ch in text)
        {
            var g = Lookup(table, ch);
            var dst = new Rect(cx + g.Xo * scaleX, y + (g.Yo + yOff) * scaleY, g.Sw * scaleX, g.Sh * scaleY);
            var src = new Rect((float)g.Sx / sheet.width,
                               (sheet.height - g.Sy - g.Sh) / (float)sheet.height,
                               (float)g.Sw / sheet.width,
                               (float)g.Sh / sheet.height);
            GUI.DrawTextureWithTexCoords(dst, sheet, src);
            cx += g.W * scaleX;
        }
        GUI.color = prev;
    }

    /// <summary>True when the game draws text with the old 8x8 font (English by default).</summary>
    internal static bool IsLegacy
    {
        get
        {
            try { return LocalizationDictionary.LegacyFont(); }
            catch { return false; }
        }
    }

    /// <summary>Width of a line in game pixels (before scaling), using the font Draw uses.</summary>
    internal static float Measure(string text)
    {
        if (!Ready || string.IsNullOrEmpty(text)) return 0f;
        bool legacyFont;
        try { legacyFont = LocalizationDictionary.LegacyFont(); }
        catch { legacyFont = false; }
        var table = legacyFont ? _legacy! : _fancy!;
        float width = 0f;
        foreach (char ch in text) width += Lookup(table, ch).W;
        return width;
    }

    /// <summary>
    /// Breaks text into lines no wider than maxWidth game pixels, at spaces. Each line keeps the space it
    /// ended with, so the lengths of the lines add up to the length of the text.
    /// </summary>
    internal static List<string> Wrap(string text, float maxWidth)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Split('\n'))
        {
            string current = "";
            foreach (var word in paragraph.Split(' '))
            {
                string candidate = current + word + " ";
                if (current.Length > 0 && Measure(candidate.TrimEnd()) > maxWidth)
                {
                    lines.Add(current);
                    current = word + " ";
                }
                else current = candidate;
            }
            lines.Add(current);
        }
        return lines;
    }

    /// <summary>Draws a rect of the game's sprite sheet (sheet pixels) with its top-left at (x, y).</summary>
    internal static void DrawSprite(Rect sheetRect, float x, float y, float scale) =>
        DrawSprite(sheetRect, x, y, scale, scale);

    internal static void DrawSprite(Rect sheetRect, float x, float y, float scaleX, float scaleY)
    {
        if (!Ready || _spriteSheet == null) return;
        var sprites = Masked(_spriteSheet);
        if (sprites == null) return;
        var dst = new Rect(x, y, sheetRect.width * scaleX, sheetRect.height * scaleY);
        var src = new Rect(sheetRect.x / sprites.width,
                           (sprites.height - sheetRect.y - sheetRect.height) / sprites.height,
                           sheetRect.width / sprites.width,
                           sheetRect.height / sprites.height);
        var prev = GUI.color;
        GUI.color = Color.white;
        GUI.DrawTextureWithTexCoords(dst, sprites, src);
        GUI.color = prev;
    }

    // ── Match how the game shows its HUD ────────────────────────────────────
    //
    // The HUD is drawn into a texture over black and shown with the "Unlit/AlphaMask" shader, which uses
    // that same texture as its opacity: black is fully transparent, white is opaque, and the soft edges
    // in between look like a glow. Normal IMGUI blending keeps the font sheet's black outline as solid
    // black. So each sheet is converted once into the same thing: colour = the sheet over black, opacity =
    // its brightness.

    private static readonly Dictionary<Texture, Texture2D> MaskedSheets = new();

    private static Texture? Masked(Texture? source)
    {
        if (source == null) return null;
        if (MaskedSheets.TryGetValue(source, out var cached) && cached != null) return cached;

        try
        {
            int w = source.width, h = source.height;
            var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
            var previous = RenderTexture.active;
            Graphics.Blit(source, rt);
            RenderTexture.active = rt;

            var copy = new Texture2D(w, h, TextureFormat.RGBA32, false);
            copy.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(rt);

            var px = copy.GetPixels32();
            for (int i = 0; i < px.Length; i++)
            {
                var c = px[i];
                // the sheet drawn over black, like the HUD texture
                byte r = (byte)(c.r * c.a / 255), g = (byte)(c.g * c.a / 255), b = (byte)(c.b * c.a / 255);
                byte brightness = (byte)Mathf.Max(r, Mathf.Max(g, b));
                px[i] = new Color32(r, g, b, brightness);
            }
            copy.SetPixels32(px);
            copy.Apply(false);
            copy.filterMode = FilterMode.Point;
            copy.wrapMode = TextureWrapMode.Clamp;
            copy.hideFlags = HideFlags.HideAndDontSave;

            MaskedSheets[source] = copy;
            return copy;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[GameFont] Could not build the masked sheet, using the plain one: {ex.Message}");
            return source;
        }
    }

    private static Glyph Lookup(Dictionary<int, Glyph> table, char c)
    {
        if (table.TryGetValue(c, out var g)) return g;
        return table['@'];
    }
}
