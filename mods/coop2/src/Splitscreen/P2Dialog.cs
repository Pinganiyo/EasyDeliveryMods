using System.Collections.Generic;
using UnityEngine;

namespace EasyDeliveryCoLanCoop;

/// <summary>
/// Player 2's own conversation with a shopkeeper, made to look and sound like Player 1's.
/// The game's dialog (sDialog / sTransactionNPC.DoDialog) is built around Player 1: it freezes P1 and
/// draws in P1's HUD. This copies what sDialog does, for P2 only:
///  - text in the game's font at the same place of the HUD (no box), one page per sentence, typed at the
///    game's own speed, with the same bobbing arrow when a page is done;
///  - the NPC's voice: the same letter-by-letter reading of the NPC's voice clip (LetterToSpeech);
///  - interact / back both continue (the game's Continue and Skip are the same thing).
/// Nothing is bought or charged here.
/// </summary>
internal static class P2Dialog
{
    internal static bool Active { get; private set; }

    private static sTransactionNPC? _npc;
    private static string _name = "";
    private static readonly List<string> Pages = new();
    private static int _page;
    private static int _revealed;      // letters shown on the current page
    private static float _letterTimer;
    private static float _doneTimer;   // the game waits 0.2 s after the last letter before it is "complete"
    private static bool _complete;

    private static AudioClip? _voice;
    private static AudioSource? _speaker;
    private static Animator? _anim;

    // Values of the game's own dialog (read from the instance so a changed setting carries over)
    private static float _lettersPerSecond = 8f;
    private static int _textBoxWidth = 32;
    private static int _textBoxHeight = 4;
    private static float _hudWidth = 320f;
    private static float _hudHeight = 256f;
    private static float _quadAspect = 1.25f;

    /// <summary>Returns false if this NPC has no custom lines (the caller then does what it did before).</summary>
    internal static bool Start(sTransactionNPC npc)
    {
        if (Active) return true;

        var info = UnityEngine.Object.FindFirstObjectByType<sNPCInformation>();
        var look = npc.GetComponent<sNPCAppearence>();
        if (info == null || look == null || look.npcIndex < 0 || look.npcIndex >= info.npcData.Count) return false;

        string name = info.npcData[look.npcIndex].name;
        bool duo = SplitscreenManager.BothNear(npc.transform.position);
        var set = CoopDialogs.Get(name, duo) ?? CoopDialogs.Get(name, false);
        if (set == null) return false;

        ReadGameDialogSettings();

        Pages.Clear();
        _voice = null;
        if (info.IsDead(look.npcIndex))
        {
            Pages.Add("..."); // a terminated NPC has nothing to say, same as for P1
        }
        else
        {
            npc.SendMessage("UpdatePrice", npc, SendMessageOptions.DontRequireReceiver);
            string interaction = CoopDialogs.NextInteraction(name, duo);
            Pages.Add(set.Greeting);
            if (interaction.Length > 0)
                Pages.AddRange(SplitIntoPages(WithPrice(interaction, npc.price)));
            if (set.Sendoff.Length > 0) Pages.Add(set.Sendoff);
            try { _voice = info.GetVoice(look.npcIndex); } catch { _voice = null; }
        }
        Pages.RemoveAll(p => p.Trim().Length == 0);
        if (Pages.Count == 0) return false;

        _npc = npc;
        _name = name;
        _anim = npc.GetComponent<Animator>();
        EnsureSpeaker();
        _page = -1;
        Active = true;
        NextPage();
        Plugin.Log.LogInfo($"[Dialog] P2 talks to {name} ({(duo ? "both players" : "P2")}), {Pages.Count} pages.");
        return true;
    }

    private static void ReadGameDialogSettings()
    {
        var dialog = UnityEngine.Object.FindFirstObjectByType<sDialog>();
        if (dialog != null)
        {
            _lettersPerSecond = Mathf.Max(1f, dialog.lettersPerSecond);
            _textBoxWidth = dialog.textBoxWidth;
            _textBoxHeight = dialog.textBoxHeight;
            var r = dialog.GetComponentInParent<MiniRenderer>() ?? dialog.GetComponent<MiniRenderer>();
            if (r != null && r.width > 0 && r.height > 0)
            {
                _hudWidth = r.width;
                _hudHeight = r.height;
            }
        }
        var quad = UnityEngine.Object.FindFirstObjectByType<pixelPerfectView>();
        if (quad != null && quad.menuHeight > 0f)
            _quadAspect = quad.menuWidth / quad.menuHeight;
    }

    /// <summary>The same sentence-by-sentence paging sNPCInformation.GetLine gives P1's story lines.</summary>
    internal static List<string> SplitIntoPages(string text)
    {
        text = text.Replace(". ", ".\n").Replace("? ", "?\n").Replace("! ", "!\n");
        var pages = new List<string>();
        foreach (var part in text.Split('\n'))
            if (part.Trim().Length > 0) pages.Add(part.Trim());
        return pages;
    }

    /// <summary>Same as SplitIntoPages, joined back with newlines (for lines handed to the game's own dialog).</summary>
    internal static string FormatLikeStory(string text) => string.Join("\n", SplitIntoPages(text));

    private static void EnsureSpeaker()
    {
        if (_speaker != null) return;
        var go = new GameObject("EasyDeliveryCoLanCoop.P2DialogVoice");
        UnityEngine.Object.DontDestroyOnLoad(go);
        _speaker = go.AddComponent<AudioSource>();
        _speaker.playOnAwake = false;
        _speaker.loop = false;
        _speaker.spatialBlend = 0f;

        // same output as the game's own dialog voice
        var dialog = UnityEngine.Object.FindFirstObjectByType<sDialog>();
        var original = dialog != null ? dialog.GetComponent<AudioSource>() : null;
        if (original != null)
        {
            _speaker.outputAudioMixerGroup = original.outputAudioMixerGroup;
            _speaker.volume = original.volume;
            _speaker.priority = original.priority;
        }
    }

    private static void NextPage()
    {
        _page++;
        if (_page >= Pages.Count) { End(); return; }
        _revealed = 0;
        _letterTimer = 0f;
        _doneTimer = 0f;
        _complete = false;
    }

    internal static void Tick(float dt)
    {
        if (!Active) return;
        if (_npc == null || SplitscreenManager.P2Dead) { End(); return; }

        string line = Pages[_page];
        if (!_complete)
        {
            if (_revealed < line.Length)
            {
                _letterTimer += dt;
                float step = 1f / _lettersPerSecond;
                while (_letterTimer >= step && _revealed < line.Length)
                {
                    _letterTimer -= step;
                    _revealed++;
                    Speak(line);
                }
            }
            else
            {
                _doneTimer += dt;
                if (_doneTimer >= 0.2f)
                {
                    _complete = true;
                    if (_speaker != null) _speaker.Stop();
                }
            }
        }
        if (_anim != null) _anim.SetBool("Talking", !_complete);
    }

    /// <summary>sDialog.Continue and sDialog.Skip do the same thing, so interact and back both end up here.</summary>
    internal static void OnInteract()
    {
        if (!Active) return;
        if (!_complete)
        {
            _revealed = Pages[_page].Length;   // like the game: stop typing, then the page completes
            _doneTimer = 0.2f;
            _complete = true;
            if (_speaker != null) _speaker.Stop();
            return;
        }
        NextPage();
    }

    internal static void Cancel()
    {
        if (Active) End();
    }

    private static void End()
    {
        if (_anim != null) _anim.SetBool("Talking", false);
        if (_speaker != null) _speaker.Stop();
        Active = false;
        _npc = null;
        _anim = null;
        Pages.Clear();
    }

    /// <summary>sDialog.Speak: vowels only (other letters stay silent), other characters every third letter.</summary>
    private static void Speak(string line)
    {
        if (_voice == null || _speaker == null || line.Length < 1) return;
        _speaker.clip = _voice;

        char c = line[Mathf.Max(0, _revealed - 1)];
        const string letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        const bool vowelsOnly = true;
        if (letters.IndexOf(c) >= 0)
        {
            if (vowelsOnly && "AEIOUYaeiouy".IndexOf(c) < 0) return;
        }
        else
        {
            if (vowelsOnly && _revealed % 3 != 0) return;
            c = letters[UnityEngine.Random.Range(0, letters.Length)];
        }
        LetterToSpeech.ReadLetters(_speaker, char.ToLower(c));
    }

    /// <summary>Shows the price the way the game writes it, without charging anything.</summary>
    internal static string WithPrice(string text, float price)
    {
        if (!text.Contains("$price")) return text;
        return text.Replace("$price", Money(price));
    }

    internal static string Money(float n)
    {
        float whole = (int)n;
        string cents = Mathf.Abs((int)(n * 100f) % 100).ToString();
        if (cents.Length < 2) cents += "0";
        return "$" + whole + "." + cents;
    }

    // ── Drawing (called from SplitscreenHud.OnGUI) ──────────────────────────

    internal static void Draw()
    {
        if (!Active || _page < 0 || _page >= Pages.Count || !GameFont.Ready) return;
        var cam = SplitscreenManager.P2ViewCamera;
        if (cam == null) return;

        // Where the HUD (the game's 2D overlay) lands on P2's view. In a half view the HUD quad is squeezed
        // to the view's width (see PixelPerfectViewPatch); in a wide view it keeps its shape and is centred.
        Rect view = cam.rect;
        float vx = view.x * Screen.width;
        float vy = (1f - view.y - view.height) * Screen.height;
        float vw = view.width * Screen.width;
        float vh = view.height * Screen.height;

        var rt = cam.targetTexture;
        float rtAspect = rt != null ? (float)rt.width / rt.height : (float)Screen.width / Screen.height;
        float viewAspect = view.width / view.height * rtAspect;

        float scaleY = vh / _hudHeight;
        float scaleX, offsetX;
        if (viewAspect >= _quadAspect)
        {
            scaleX = scaleY;
            offsetX = vx + (vw - _hudWidth * scaleX) / 2f;
        }
        else
        {
            scaleX = vw / _hudWidth;
            offsetX = vx;
        }
        float offsetY = vy;

        bool legacy = GameFont.IsLegacy;
        float spacing = legacy ? 9f : 13f;   // sFancyText.put lowers the line spacing by 4 for the old font
        float top = legacy ? 64f : 61f;      // and fput starts 3 px higher for the new font

        float boxLeft = _hudWidth / 2f - _textBoxWidth * 4f;
        float boxWidth = _textBoxWidth * 8f;

        string full = Pages[_page];
        var lines = GameFont.Wrap(full, boxWidth);
        int remaining = _complete ? full.Length : _revealed;
        float y = top;
        foreach (var line in lines)
        {
            if (remaining <= 0) break;
            string part = line.Length <= remaining ? line : line.Substring(0, remaining);
            GameFont.Draw(part, offsetX + boxLeft * scaleX, offsetY + y * scaleY, scaleX, scaleY, Color.white, centered: false);
            remaining -= line.Length;
            y += spacing;
        }

        if (_complete)
        {
            float arrowX = _hudWidth / 2f + _textBoxWidth * 4f;
            float arrowY = 64f + _textBoxHeight * 8f + 8f + Mathf.Sin(Time.time * 4f) * 2f;
            GameFont.DrawSprite(new Rect(24f, 0f, 8f, 8f), offsetX + arrowX * scaleX, offsetY + arrowY * scaleY, scaleX, scaleY);
        }
    }
}
