using System;
using System.IO;
using UnityEngine;

namespace EasyDeliveryCoLanCoop;

/// <summary>
/// Handles custom player skin textures for Player 1 and Player 2.
/// Clones the texture loading logic from EasyCustomPlayerSkin and extends it
/// to support multiple players with point-filtered 64x64 pixel textures.
/// </summary>
internal static class PlayerCustomization
{
    public static string CustomPlayerDir => Path.Combine(Application.streamingAssetsPath, "CustomPlayer");

    // File paths
    public static string Player1TexturePath => Path.Combine(CustomPlayerDir, "player1.png");
    public static string Player2TexturePath => Path.Combine(CustomPlayerDir, "player2.png");
    public static string DefaultTexturePath => Path.Combine(CustomPlayerDir, "player.png");

    // Cached loaded textures
    public static Texture2D? TextureP1 { get; private set; }
    public static Texture2D? TextureP2 { get; private set; }

    public static bool HasTextureP1 => TextureP1 != null;
    public static bool HasTextureP2 => TextureP2 != null;

    private static bool _initialized = false;
    private static FileSystemWatcher? _watcher;
    private static volatile bool _pendingReload = false;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        EnsureDirectoryExists();
        ReloadTextures();
        SetupWatcher();
    }

    public static void EnsureDirectoryExists()
    {
        try
        {
            if (!Directory.Exists(CustomPlayerDir))
                Directory.CreateDirectory(CustomPlayerDir);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[PlayerCustomization] Failed creating CustomPlayer dir: {ex.Message}");
        }
    }

    public static void ReloadTextures()
    {
        EnsureDirectoryExists();

        // 1. Resolve Player 1 texture
        Texture2D? p1 = null;
        if (File.Exists(Player1TexturePath))
            p1 = LoadTexture(Player1TexturePath);
        else if (File.Exists(DefaultTexturePath))
            p1 = LoadTexture(DefaultTexturePath);

        // 2. Resolve Player 2 texture
        Texture2D? p2 = null;
        if (File.Exists(Player2TexturePath))
            p2 = LoadTexture(Player2TexturePath);
        else if (File.Exists(DefaultTexturePath))
            p2 = LoadTexture(DefaultTexturePath);

        TextureP1 = p1;
        TextureP2 = p2;

        Plugin.Log.LogInfo($"[PlayerCustomization] Loaded skins - P1: {(TextureP1 != null ? "Custom" : "Default")}, P2: {(TextureP2 != null ? "Custom" : "Default")}");

        // Apply immediately to all active player instances in scene
        ApplyToAllActivePlayers();
    }

    private static Texture2D? LoadTexture(string path)
    {
        try
        {
            byte[] fileData = File.ReadAllBytes(path);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Point;
            tex.wrapMode = TextureWrapMode.Clamp;
            if (tex.LoadImage(fileData))
            {
                tex.filterMode = FilterMode.Point; // Ensure point filtering after LoadImage
                return tex;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[PlayerCustomization] Error loading texture from {path}: {ex.Message}");
        }
        return null;
    }

    private static void SetupWatcher()
    {
        try
        {
            if (!Directory.Exists(CustomPlayerDir)) return;
            if (_watcher != null)
            {
                _watcher.Dispose();
                _watcher = null;
            }

            _watcher = new FileSystemWatcher(CustomPlayerDir, "*.png")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true
            };

            _watcher.Changed += (s, e) => _pendingReload = true;
            _watcher.Created += (s, e) => _pendingReload = true;
            _watcher.Renamed += (s, e) => _pendingReload = true;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[PlayerCustomization] Could not set up file watcher: {ex.Message}");
        }
    }

    public static void UpdateTick()
    {
        if (_pendingReload)
        {
            _pendingReload = false;
            ReloadTextures();
        }
    }

    /// <summary>
    /// Applies skin to a specific sCosmeticAppearance instance.
    /// playerIndex: 1 for P1, 2 for P2. If 0, auto-detects from gameObject name.
    /// </summary>
    public static void ApplySkin(sCosmeticAppearance appearance, int playerIndex = 0)
    {
        if (appearance == null) return;

        if (playerIndex == 0)
        {
            bool isP2 = appearance.gameObject.name.Contains("P2") ||
                        (appearance.transform.root != null && appearance.transform.root.name.Contains("P2"));
            playerIndex = isP2 ? 2 : 1;
        }

        Texture2D? targetTex = playerIndex == 2 ? TextureP2 : TextureP1;
        if (targetTex == null) return;

        targetTex.filterMode = FilterMode.Point;

        // Apply to head/head
        Transform head = appearance.transform.Find("head");
        if (head != null)
        {
            Transform head2 = head.Find("head");
            if (head2 != null)
            {
                MeshRenderer r = head2.GetComponent<MeshRenderer>();
                if (r != null)
                {
                    foreach (Material m in r.materials)
                    {
                        if (m != null)
                        {
                            m.SetTexture("_BaseMap", targetTex);
                            m.SetTexture("_MainTex", targetTex);
                        }
                    }
                }
            }
        }

        // Apply to cylinder (body)
        Transform cylinder = appearance.transform.Find("cylinder");
        if (cylinder != null)
        {
            MeshRenderer r2 = cylinder.GetComponent<MeshRenderer>();
            if (r2 != null)
            {
                foreach (Material m in r2.materials)
                {
                    if (m != null)
                    {
                        m.SetTexture("_BaseMap", targetTex);
                        m.SetTexture("_MainTex", targetTex);
                    }
                }
            }
        }
    }

    public static void ApplyToAllActivePlayers()
    {
        var appearances = UnityEngine.Object.FindObjectsByType<sCosmeticAppearance>(FindObjectsSortMode.None);
        foreach (var app in appearances)
        {
            if (app != null)
                ApplySkin(app);
        }
    }

    // ── Legacy stubs for LAN Co-op compatibility ────────────────────────────
    internal static string LocalNickname => Plugin.SanitizeNickname(Plugin.Nickname?.Value);
    internal static Color LocalColor { get; set; } = Color.white;
    internal static void SetRemoteTint(string peerKey, Color color) { }
    internal static Color GetRemoteTint(string peerKey) => Color.white;
    internal static void RemovePeer(string peerKey) { }
    internal static void ClearAll() { }
    internal static Color ClampColor(float r, float g, float b) =>
        new Color(Mathf.Clamp01(r), Mathf.Clamp01(g), Mathf.Clamp01(b), 1f);
    internal static void ApplyToLocalPlayer() => ApplyToAllActivePlayers();
}
