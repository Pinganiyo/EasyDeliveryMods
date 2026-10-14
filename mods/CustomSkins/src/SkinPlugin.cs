using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace EasyDeliveryCoCustomSkins;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public sealed class SkinPlugin : BaseUnityPlugin
{
    public const string PluginGuid = "coopmod.easydeliveryco.customskins";
    public const string PluginName = "EasyDeliveryCoCustomSkins";
    public const string PluginVersion = "0.1.0";

    internal static ManualLogSource Log = null!;
    internal static ConfigEntry<KeyCode>? MenuKey;

    private Harmony? _harmony;
    private GameObject? _managerGo;

    private void Awake()
    {
        Log = Logger;

        MenuKey = Config.Bind(
            "Customization",
            "MenuKey",
            KeyCode.F7,
            "Keyboard key to open the in-game character customization panel.");

        _harmony = new Harmony(PluginGuid);
        _harmony.PatchAll(typeof(SkinPlugin).Assembly);

        _managerGo = new GameObject("EasyDeliveryCoCustomSkins.Manager");
        DontDestroyOnLoad(_managerGo);
        _managerGo.hideFlags = HideFlags.HideAndDontSave;
        _managerGo.AddComponent<CustomizationMenu>();

        PlayerCustomization.Initialize();

        Log.LogInfo($"{PluginName} {PluginVersion} loaded. Skins dir: {PlayerCustomization.CustomPlayerDir}");
    }

    private void OnDestroy()
    {
        try { _harmony?.UnpatchSelf(); } catch { /* ignore */ }
        try { if (_managerGo != null) Destroy(_managerGo); } catch { /* ignore */ }
    }
}
