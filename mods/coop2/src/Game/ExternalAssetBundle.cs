using BepInEx;
using System;
using System.IO;
using UnityEngine;

namespace EasyDeliveryCoLanCoop;

internal static class ExternalAssetBundle
{
    private static AssetBundle? _bundle;
    private static string? _loadedPath;
    private static float _nextRetryAt;
    private static bool _loggedLoadError;

    internal static GameObject? TryLoadGameObject(string assetName)
    {
        if (!Plugin.ExternalAssetsEnabled.Value)
            return null;

        if (string.IsNullOrWhiteSpace(assetName))
            return null;

        var bundle = EnsureLoaded();
        if (bundle == null)
            return null;

        try
        {
            // AssetRipper/Unity typically uses full paths like 'Assets/.../Thing.prefab'.
            var go = bundle.LoadAsset<GameObject>(assetName);
            if (go != null)
                return go;

            // Also try by file name only, just in case the caller provided a short name.
            var fileName = Path.GetFileName(assetName);
            if (!string.IsNullOrWhiteSpace(fileName) && !string.Equals(fileName, assetName, StringComparison.OrdinalIgnoreCase))
                return bundle.LoadAsset<GameObject>(fileName);
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"ExternalAssetBundle: failed to load asset '{assetName}': {e.Message}");
        }

        return null;
    }

    internal static GameObject? TryLoadGameObjectByBaseName(string baseName)
    {
        if (!Plugin.ExternalAssetsEnabled.Value)
            return null;

        if (string.IsNullOrWhiteSpace(baseName))
            return null;

        var bundle = EnsureLoaded();
        if (bundle == null)
            return null;

        try
        {
            baseName = Path.GetFileNameWithoutExtension(baseName).Trim();
            if (baseName.Length == 0)
                return null;

            var all = bundle.GetAllAssetNames();
            if (all == null || all.Length == 0)
                return null;

            for (var i = 0; i < all.Length; i++)
            {
                var a = all[i];
                if (string.IsNullOrEmpty(a))
                    continue;

                var fn = Path.GetFileNameWithoutExtension(a);
                if (string.Equals(fn, baseName, StringComparison.OrdinalIgnoreCase))
                {
                    var go = bundle.LoadAsset<GameObject>(a);
                    if (go != null)
                        return go;
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Log.LogWarning($"ExternalAssetBundle: TryLoadGameObjectByBaseName failed for '{baseName}': {e.Message}");
        }

        return null;
    }

    internal static void InvalidateIfConfigChanged()
    {
        var desired = ResolveBundlePath(Plugin.ExternalAssetsBundlePath.Value);
        if (string.IsNullOrWhiteSpace(desired))
            desired = null;

        if (!string.Equals(_loadedPath, desired, StringComparison.OrdinalIgnoreCase))
            Unload();
    }

    private static AssetBundle? EnsureLoaded()
    {
        InvalidateIfConfigChanged();

        if (_bundle != null)
            return _bundle;

        // Throttle repeated failing attempts.
        if (Time.unscaledTime < _nextRetryAt)
            return null;

        _nextRetryAt = Time.unscaledTime + 2.0f;

        var path = ResolveBundlePath(Plugin.ExternalAssetsBundlePath.Value);
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            if (!File.Exists(path))
            {
                if (!_loggedLoadError)
                {
                    _loggedLoadError = true;
                    Plugin.Log.LogWarning($"ExternalAssetBundle: bundle not found at '{path}'.");
                }
                return null;
            }

            var bundle = AssetBundle.LoadFromFile(path);
            if (bundle == null)
            {
                if (!_loggedLoadError)
                {
                    _loggedLoadError = true;
                    Plugin.Log.LogWarning($"ExternalAssetBundle: failed to load bundle from '{path}'.");
                }
                return null;
            }

            _bundle = bundle;
            _loadedPath = path;
            _loggedLoadError = false;
            Plugin.Log.LogInfo($"ExternalAssetBundle: loaded '{path}'.");
            return _bundle;
        }
        catch (Exception e)
        {
            if (!_loggedLoadError)
            {
                _loggedLoadError = true;
                Plugin.Log.LogWarning($"ExternalAssetBundle: exception loading bundle: {e.Message}");
            }
            return null;
        }
    }

    private static void Unload()
    {
        try
        {
            if (_bundle != null)
                _bundle.Unload(unloadAllLoadedObjects: false);
        }
        catch
        {
            // ignore
        }

        _bundle = null;
        _loadedPath = null;
        _loggedLoadError = false;
    }

    private static string? ResolveBundlePath(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        raw = raw.Trim();

        // Unity allows bundle files without extension; users often type with or without '.bundle'.
        // We'll accept whatever they give and also try adding '.bundle' if it doesn't exist.
        string TryResolve(string candidate)
        {
            if (Path.IsPathRooted(candidate))
                return candidate;

            // Try common BepInEx-relative locations.
            var p1 = Path.Combine(Paths.BepInExRootPath, candidate);
            if (File.Exists(p1))
                return p1;

            var p2 = Path.Combine(Paths.PluginPath, candidate);
            if (File.Exists(p2))
                return p2;

            var p3 = Path.Combine(Paths.GameRootPath, candidate);
            return p3;
        }

        var resolved = TryResolve(raw);
        if (File.Exists(resolved))
            return resolved;

        // Try with '.bundle' extension as a convenience.
        if (!raw.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase))
        {
            var resolved2 = TryResolve(raw + ".bundle");
            if (File.Exists(resolved2))
                return resolved2;
        }

        return resolved;
    }
}
