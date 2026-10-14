using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using System;
using UnityEngine;

namespace EasyDeliveryCoLanCoop;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class Plugin : BaseUnityPlugin
{
    public const string PluginGuid = "coopmod.easydeliveryco.lancoop";
    public const string PluginName = "EasyDeliveryCoLanCoop";
    public const string PluginVersion = "0.2.19";

    internal static ManualLogSource Log = null!;

    internal static ConfigEntry<string> Mode = null!; // Off | Host | Client
    internal static ConfigEntry<int> Port = null!;
    internal static ConfigEntry<string> HostAddress = null!;
    internal static ConfigEntry<int> TickRate = null!;
    internal static ConfigEntry<float> ClientTimeoutSeconds = null!;
    internal static ConfigEntry<bool> AutoDiscovery = null!;
    internal static ConfigEntry<int> DiscoveryPort = null!;
    internal static ConfigEntry<int> DiscoveryIntervalMs = null!;

    internal static ConfigEntry<bool> SaveKeySyncEnabled = null!;
    internal static ConfigEntry<string> SaveKeyDenySubstrings = null!;
    internal static ConfigEntry<string> SaveKeyPersonalDenySubstrings = null!;

    internal static ConfigEntry<bool> SharedMoneyEnabled = null!;
    internal static ConfigEntry<bool> AutoEnterWorldOnConnect = null!;

    internal static ConfigEntry<bool> AutoReconnectEnabled = null!;
    internal static ConfigEntry<float> AutoReconnectLostSeconds = null!;
    internal static ConfigEntry<float> AutoReconnectBackoffMaxSeconds = null!;

    internal static ConfigEntry<bool> ClientReceivesHostSaveOnJoin = null!;
    internal static ConfigEntry<bool> ClientWipeLocalSaveOnJoin = null!;

    internal static ConfigEntry<bool> PlayerPositionsEnabled = null!;
    internal static ConfigEntry<string> PlayerPositionsSaveIdOverride = null!;
    internal static ConfigEntry<bool> PlayerPositionsClientTeleportOnJoin = null!;

    internal static ConfigEntry<string> Nickname = null!;
    internal static ConfigEntry<float> RemoteAvatarYawOffsetDegrees = null!;

    internal static ConfigEntry<float> RemoteHeldPayloadOffsetX = null!;
    internal static ConfigEntry<float> RemoteHeldPayloadOffsetY = null!;
    internal static ConfigEntry<float> RemoteHeldPayloadOffsetZ = null!;
    internal static ConfigEntry<float> RemoteHeldPayloadUniformScale = null!;

    internal static ConfigEntry<bool> CarSoundSyncEnabled = null!;
    internal static ConfigEntry<string> CarSoundSyncMode = null!;
    internal static ConfigEntry<bool> CarSoundSyncHornOnly = null!;
    internal static ConfigEntry<float> CarSoundSyncMinIntervalSeconds = null!;

    internal static ConfigEntry<bool> ExternalAssetsEnabled = null!;
    internal static ConfigEntry<string> ExternalAssetsBundlePath = null!;
    internal static ConfigEntry<string> ExternalPlayerPrefabAssetName = null!;
    internal static ConfigEntry<string> ExternalCarPrefabAssetName = null!;

    internal static ConfigEntry<bool> LanCoopEnabled = null!;
    internal static ConfigEntry<bool> DebugLogs = null!;
    internal static ConfigEntry<float> DebugLogIntervalSeconds = null!;
    internal static string RuntimeModeOverride = string.Empty;

    // ── Character Customization ────────────────────────────────────────────
    internal static ConfigEntry<float> CustomColorR = null!;
    internal static ConfigEntry<float> CustomColorG = null!;
    internal static ConfigEntry<float> CustomColorB = null!;
    internal static ConfigEntry<KeyCode> CustomMenuKey = null!;

    // ── Splitscreen ───────────────────────────────────────────────────────
    internal static ConfigEntry<KeyCode>? SplitscreenToggleKey;
    internal static ConfigEntry<string>? SplitscreenOrientation;
    internal static ConfigEntry<KeyCode>? SplitscreenSwapControlKey;
    internal static ConfigEntry<float>? PassengerCameraOffset;

    private Harmony? _harmony;
    private GameObject? _managerGo;

    private void Awake()
    {
        Log = Logger;

        try
        {
            Application.runInBackground = true;
        }
        catch
        {
        }

        LanCoopEnabled = Config.Bind("Network", "LanEnabled", false, "Enable LAN multiplayer co-op networking and UI. Set to false to disable LAN mod.");
        Mode = Config.Bind("Network", "Mode", "Off", "Off | Host | Client");
        Port = Config.Bind("Network", "Port", 27777, "UDP port");
        HostAddress = Config.Bind("Network", "HostAddress", "127.0.0.1", "Server IP for Client mode");
        TickRate = Config.Bind("Network", "TickRate", 20, "Net update rate (Hz)");
        ClientTimeoutSeconds = Config.Bind("Network", "ClientTimeoutSeconds", 6.0f, "Host-side timeout (seconds) after which a silent client is considered disconnected and its ghost objects are removed");
        AutoDiscovery = Config.Bind("Discovery", "AutoDiscovery", true, "Enable LAN auto-discovery for clients");
        DiscoveryPort = Config.Bind("Discovery", "DiscoveryPort", 27778, "UDP port used for LAN discovery broadcast/listen");
        DiscoveryIntervalMs = Config.Bind("Discovery", "DiscoveryIntervalMs", 700, "Host broadcast interval (ms)");

        SaveKeySyncEnabled = Config.Bind("Sync", "SaveKeySyncEnabled", true, "Replicate sSaveSystem SetString/DeleteKey over the network (progress sync)");
        SaveKeyDenySubstrings = Config.Bind(
            "Sync",
            "SaveKeyDenySubstrings",
            "pos,rot,camera,cam,look,player,guy,controller,car,vehicle,rb,transform",
            "Comma-separated substrings; matching keys will NOT be replicated/applied (case-insensitive). Use to avoid syncing per-player runtime state.");

        SaveKeyPersonalDenySubstrings = Config.Bind(
            "Sync",
            "SaveKeyPersonalDenySubstrings",
            "inventory,backpack,bag,hotbar,quickslot,helditem,equipped,loadout,playerinventory",
            "Additional comma-separated substrings treated as personal player state. These keys are excluded from host->client save snapshots and live save replication so each player keeps their own inventory/loadout."
        );

        SharedMoneyEnabled = Config.Bind(
            "Sync",
            "SharedMoneyEnabled",
            true,
            "If true, money becomes shared across all players. Clients send money changes to the host, and everyone displays the host's money."
        );

        AutoEnterWorldOnConnect = Config.Bind(
            "Network",
            "AutoEnterWorldOnConnect",
            true,
            "If true, when using the in-game Connect button, the client will try to automatically enter the host's world without going through the save menu (best-effort)."
        );

        AutoReconnectEnabled = Config.Bind(
            "Network",
            "AutoReconnectEnabled",
            true,
            "If true (Client mode), automatically retries connection to the last known host if the host becomes unreachable, without restarting the game."
        );

        AutoReconnectLostSeconds = Config.Bind(
            "Network",
            "AutoReconnectLostSeconds",
            6.0f,
            "Client considers the connection lost if no packets are received from host for this many seconds (then handshake restarts)."
        );

        AutoReconnectBackoffMaxSeconds = Config.Bind(
            "Network",
            "AutoReconnectBackoffMaxSeconds",
            12.0f,
            "Maximum delay between reconnect attempts (seconds)."
        );

        Log.LogInfo($"Config: AutoEnterWorldOnConnect={AutoEnterWorldOnConnect.Value}");

        ClientReceivesHostSaveOnJoin = Config.Bind(
            "Sync",
            "ClientReceivesHostSaveOnJoin",
            true,
            "If true, host sends a full save snapshot when the client joins, and the client applies it (so the session starts from host progress)."
        );

        ClientWipeLocalSaveOnJoin = Config.Bind(
            "Sync",
            "ClientWipeLocalSaveOnJoin",
            true,
            "If true (and ClientReceivesHostSaveOnJoin is true), the client first deletes its local allowed save keys before applying the host snapshot."
        );

        PlayerPositionsEnabled = Config.Bind(
            "Positions",
            "Enabled",
            true,
            "If true, host persists player positions by nickname per-save-id, and sends them to joining clients."
        );

        PlayerPositionsSaveIdOverride = Config.Bind(
            "Positions",
            "SaveIdOverride",
            "",
            "Optional stable id used to select the positions file. Leave empty to auto-detect; fallback is 'default'."
        );

        PlayerPositionsClientTeleportOnJoin = Config.Bind(
            "Positions",
            "ClientTeleportOnJoin",
            true,
            "If true, after receiving positions from host, client teleports its local player to the saved position for its nickname (best-effort)."
        );

        Nickname = Config.Bind("Profile", "Nickname", Environment.UserName, "Player nickname shown above remote players/cars");

        RemoteAvatarYawOffsetDegrees = Config.Bind(
            "Visual",
            "RemoteAvatarYawOffsetDegrees",
            -90f,
            "Yaw offset (degrees) applied to remote player avatars so they face correctly. Typical values: -90 or 90");

        RemoteHeldPayloadOffsetX = Config.Bind(
            "Visual",
            "RemoteHeldPayloadOffsetX",
            0.0f,
            "Local X offset for the held payload visual on remote players.");
        RemoteHeldPayloadOffsetY = Config.Bind(
            "Visual",
            "RemoteHeldPayloadOffsetY",
            1.05f,
            "Local Y offset for the held payload visual on remote players.");
        RemoteHeldPayloadOffsetZ = Config.Bind(
            "Visual",
            "RemoteHeldPayloadOffsetZ",
            0.35f,
            "Local Z offset for the held payload visual on remote players.");
        RemoteHeldPayloadUniformScale = Config.Bind(
            "Visual",
            "RemoteHeldPayloadUniformScale",
            1.0f,
            "Uniform scale multiplier for the held payload visual on remote players.");

        CarSoundSyncEnabled = Config.Bind(
            "Sync",
            "CarSoundSyncEnabled",
            true,
            "If true, sync car sound events (currently horn/beep) across networked players."
        );

        CarSoundSyncMode = Config.Bind(
            "Sync",
            "CarSoundSyncMode",
            "All",
            "Car sound sync mode: All | HornOnly. 'All' syncs horn + tires + impacts."
        );

        CarSoundSyncHornOnly = Config.Bind(
            "Sync",
            "CarSoundSyncHornOnly",
            true,
            "If true, only horn/beep events are synced to avoid mixed engine/skid/crash noise."
        );

        CarSoundSyncMinIntervalSeconds = Config.Bind(
            "Sync",
            "CarSoundSyncMinIntervalSeconds",
            0.12f,
            "Minimum interval between synced local car sound events (seconds)."
        );

        ExternalAssetsEnabled = Config.Bind(
            "ExternalAssets",
            "Enabled",
            false,
            "If true, try to load Player/Car visual prefabs from an external AssetBundle (built from the extracted project). Falls back to in-game cloning if unavailable.");

        ExternalAssetsBundlePath = Config.Bind(
            "ExternalAssets",
            "BundlePath",
            "",
            "Path to AssetBundle file. Absolute path or relative to BepInEx folders. Example: 'plugins/EasyDeliveryCoLanCoop/edc_lancoop_models' (no extension required)."
        );

        ExternalPlayerPrefabAssetName = Config.Bind(
            "ExternalAssets",
            "PlayerPrefabAssetName",
            "",
            "Asset name/path inside the bundle for the player prefab. Example: 'Assets/Prefabs/Player.prefab'."
        );

        ExternalCarPrefabAssetName = Config.Bind(
            "ExternalAssets",
            "CarPrefabAssetName",
            "",
            "Asset name/path inside the bundle for the car/truck prefab. Example: 'Assets/Prefabs/Car.prefab'."
        );

        DebugLogs = Config.Bind("Debug", "DebugLogs", false, "Enable extra network snapshot logging");
        DebugLogIntervalSeconds = Config.Bind("Debug", "DebugLogIntervalSeconds", 2.0f, "Minimum seconds between debug log lines");

        // ── Character Customization ────────────────────────────────────────
        CustomColorR = Config.Bind("Customization", "ColorR", 1.0f,
            "Red component of your player avatar tint color (0.0 – 1.0).");
        CustomColorG = Config.Bind("Customization", "ColorG", 0.5f,
            "Green component of your player avatar tint color (0.0 – 1.0).");
        CustomColorB = Config.Bind("Customization", "ColorB", 0.1f,
            "Blue component of your player avatar tint color (0.0 – 1.0).");
        CustomMenuKey = Config.Bind("Customization", "MenuKey", KeyCode.F7,
            "Keyboard key to open the in-game character customization panel.");

        // ── Splitscreen ────────────────────────────────────────────────────
        SplitscreenToggleKey = Config.Bind(
            "Splitscreen",
            "ToggleKey",
            KeyCode.F8,
            "Key to toggle splitscreen local co-op mode. Requires a gameplay world to be loaded.");

        SplitscreenSwapControlKey = Config.Bind(
            "Splitscreen",
            "SwapControlKey",
            KeyCode.F6,
            "Testing aid: while splitscreen is active, switches the keyboard between controlling Player 1 and Player 2.");

        PassengerCameraOffset = Config.Bind(
            "Splitscreen",
            "PassengerCameraOffset",
            0.5f,
            "Only used if the truck's first-person view point is exactly centred: how far (meters) the passenger's inside view is moved to the left seat. Normally the view point is mirrored from the driver's seat automatically.");

        SplitscreenOrientation = Config.Bind(
            "Splitscreen",
            "Orientation",
            "Vertical",
            "Screen split direction: Vertical (left/right halves) or Horizontal (top/bottom halves).");

        ApplyCommandLineModeOverride();

        _harmony = new Harmony(PluginGuid);
        _harmony.PatchAll(typeof(Plugin).Assembly);

        _managerGo = new GameObject("EasyDeliveryCoLanCoop.Manager");
        DontDestroyOnLoad(_managerGo);
        _managerGo.hideFlags = HideFlags.HideAndDontSave;

        if (LanCoopEnabled.Value)
        {
            _managerGo.AddComponent<LanCoopManager>();
            _managerGo.AddComponent<MoneyUiFormatter>();
            Log.LogInfo($"[LAN Co-op] Enabled. Mode={GetEffectiveNetworkMode()}, Port={Port.Value}");
        }
        else
        {
            Log.LogInfo("[LAN Co-op] Disabled via config (LanEnabled=false). Running in local mode.");
        }

        _managerGo.AddComponent<CustomizationMenu>();
        _managerGo.AddComponent<SplitscreenManager>();

        PlayerCustomization.Initialize();

        Log.LogInfo($"{PluginName} {PluginVersion} loaded. Mode={GetEffectiveNetworkMode()}, Port={Port.Value}");
    }

    private static void ApplyCommandLineModeOverride()
    {
        try
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i] ?? string.Empty;
                if (arg.Equals("--lancoop-server", StringComparison.OrdinalIgnoreCase) ||
                    arg.Equals("--lancoop-host", StringComparison.OrdinalIgnoreCase))
                {
                    RuntimeModeOverride = "Host";
                    Log.LogInfo("Launch arg detected: Host mode override enabled (--lancoop-server).");
                    continue;
                }

                if (arg.Equals("--lancoop-client", StringComparison.OrdinalIgnoreCase))
                {
                    RuntimeModeOverride = "Client";
                    Log.LogInfo("Launch arg detected: Client mode override enabled (--lancoop-client).");
                    continue;
                }

                if (arg.Equals("--lancoop-off", StringComparison.OrdinalIgnoreCase))
                {
                    RuntimeModeOverride = "Off";
                    Log.LogInfo("Launch arg detected: Off mode override enabled (--lancoop-off).");
                }
            }
        }
        catch
        {
        }
    }

    internal static string GetEffectiveNetworkMode()
    {
        if (!string.IsNullOrWhiteSpace(RuntimeModeOverride))
            return RuntimeModeOverride;

        return Mode != null ? (Mode.Value ?? string.Empty) : string.Empty;
    }

    internal static bool IsSaveKeyAllowed(string key)
    {
        if (!SaveKeySyncEnabled.Value)
            return false;

        if (string.IsNullOrEmpty(key))
            return false;

        var deny = SaveKeyDenySubstrings.Value;
        if (string.IsNullOrWhiteSpace(deny))
            return true;
        foreach (var raw in deny.Split(','))
        {
            var s = raw.Trim();
            if (s.Length == 0)
                continue;

            if (key.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0)
                return false;
        }

        return true;
    }

    internal static bool IsPersonalSaveKey(string key)
    {
        if (string.IsNullOrEmpty(key))
            return false;

        if (ContainsAnyCsvSubstring(key, SaveKeyPersonalDenySubstrings?.Value))
            return true;

        // Hard fallback heuristics for common per-player keys even if config was emptied.
        var lower = key.ToLowerInvariant();
        if (lower.Contains("inventory") || lower.Contains("backpack") || lower.Contains("loadout"))
            return true;
        if (lower.Contains("hotbar") || lower.Contains("quickslot") || lower.Contains("equipped"))
            return true;
        if (lower.Contains("helditem") || lower.Contains("playerinventory"))
            return true;

        return false;
    }

    internal static bool IsSaveKeyAllowedForWorldSync(string key)
    {
        if (!IsSaveKeyAllowed(key))
            return false;
        if (IsPersonalSaveKey(key))
            return false;
        return true;
    }

    private static bool ContainsAnyCsvSubstring(string key, string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
            return false;

        foreach (var raw in csv.Split(','))
        {
            var s = raw.Trim();
            if (s.Length == 0)
                continue;
            if (key.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }

        return false;
    }

    internal static string SanitizeNickname(string? nick)
    {
        nick ??= string.Empty;
        nick = nick.Trim();
        if (nick.Length == 0)
            nick = "Player";
        if (nick.Length > 24)
            nick = nick.Substring(0, 24);
        return nick;
    }

    internal static bool IsHornOnlyCarSoundMode()
    {
        var mode = CarSoundSyncMode?.Value?.Trim() ?? string.Empty;
        if (mode.Equals("HornOnly", StringComparison.OrdinalIgnoreCase) || mode.Equals("Horn", StringComparison.OrdinalIgnoreCase))
            return true;
        if (mode.Equals("All", StringComparison.OrdinalIgnoreCase) || mode.Equals("Full", StringComparison.OrdinalIgnoreCase))
            return false;

        return CarSoundSyncHornOnly != null && CarSoundSyncHornOnly.Value;
    }

    internal static string SanitizeFileName(string? s)
    {
        s ??= string.Empty;
        s = s.Trim();
        if (s.Length == 0)
            return "default";

        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            var bad = false;
            for (var j = 0; j < invalid.Length; j++)
            {
                if (ch == invalid[j]) { bad = true; break; }
            }
            sb.Append(bad ? '_' : ch);
        }

        var outStr = sb.ToString().Trim();
        return outStr.Length == 0 ? "default" : outStr;
    }

    private void OnDestroy()
    {
        try { _harmony?.UnpatchSelf(); } catch { /* ignore */ }
        try { if (_managerGo != null) Destroy(_managerGo); } catch { /* ignore */ }
    }
}
