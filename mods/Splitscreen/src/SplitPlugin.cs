using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace EasyDeliveryCoSplitscreen;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class SplitPlugin : BaseUnityPlugin
{
    public const string PluginGuid = "coopmod.easydeliveryco.splitscreen";
    public const string PluginName = "EasyDeliveryCoSplitscreen";
    public const string PluginVersion = "0.2.0";

    internal static ManualLogSource Log = null!;
    internal static ConfigEntry<KeyCode>? ToggleKey;
    internal static ConfigEntry<string>? Orientation;

    private Harmony? _harmony;
    private GameObject? _managerGo;

    private void Awake()
    {
        Log = Logger;

        ToggleKey = Config.Bind(
            "Splitscreen",
            "ToggleKey",
            KeyCode.F8,
            "Key to toggle splitscreen local co-op mode. Requires a gameplay world to be loaded.");

        Orientation = Config.Bind(
            "Splitscreen",
            "Orientation",
            "Vertical",
            "Screen split direction: Vertical (left/right halves) or Horizontal (top/bottom halves).");

        _harmony = new Harmony(PluginGuid);
        _harmony.PatchAll(typeof(SplitPlugin).Assembly);

        _managerGo = new GameObject("EasyDeliveryCoSplitscreen.Manager");
        DontDestroyOnLoad(_managerGo);
        _managerGo.hideFlags = HideFlags.HideAndDontSave;
        _managerGo.AddComponent<SplitscreenManager>();

        Log.LogInfo($"{PluginName} {PluginVersion} loaded (clean model).");
    }

    private void OnDestroy()
    {
        try { _harmony?.UnpatchSelf(); } catch { /* ignore */ }
        try { if (_managerGo != null) Destroy(_managerGo); } catch { /* ignore */ }
    }
}
