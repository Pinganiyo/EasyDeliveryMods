using UnityEngine;

namespace EasyDeliveryCoLanCoop;

internal static class RemoteVisualPrefabs
{
    private static GameObject? _playerPrefab;
    private static GameObject? _carPrefab;
    private static bool _loggedPlayerPrefabInfo;
    private static bool _loggedCarPrefabInfo;
    private static bool _loggedExternalPlayer;
    private static bool _loggedExternalCar;

    internal static GameObject? GetPlayerPrefab()
    {
        if (_playerPrefab != null)
            return _playerPrefab;

        if (Plugin.ExternalAssetsEnabled.Value)
        {
            var external = ExternalAssetBundle.TryLoadGameObject(Plugin.ExternalPlayerPrefabAssetName.Value);
            if (external != null)
            {
                _playerPrefab = RenderOnlyClone.TryCreateRenderOnlyPrefab(external.transform)
                                ?? MeshOnlyClone.TryCreateMeshRendererHierarchyPrefab(external.transform);

                if (_playerPrefab != null && !_loggedExternalPlayer)
                {
                    _loggedExternalPlayer = true;
                    Plugin.Log.LogInfo($"Using external Player prefab from AssetBundle: '{Plugin.ExternalPlayerPrefabAssetName.Value}'.");
                }

                if (_playerPrefab != null)
                    return _playerPrefab;
            }
        }

        if (!GameAccess.TryFindLocalPlayerVisualRoot(out var root))
            return null;

        // Prefer RenderOnlyClone for players.
        // MeshOnlyClone creates new Material instances which can behave differently across peers
        // (shader variants/timing), resulting in a pink-looking avatar on only one side.
        _playerPrefab = RenderOnlyClone.TryCreateRenderOnlyPrefab(root)
                        ?? (ExternalMods.IsCustomTruckShopLoaded() ? MeshOnlyClone.TryCreateMeshRendererHierarchyPrefab(root) : null);

        if (!_loggedPlayerPrefabInfo && _playerPrefab != null)
        {
            _loggedPlayerPrefabInfo = true;
            LogPrefabInfo("Player", root, _playerPrefab);
        }
        return _playerPrefab;
    }

    internal static GameObject? GetCarPrefab()
    {
        if (_carPrefab != null)
            return _carPrefab;

        if (Plugin.ExternalAssetsEnabled.Value)
        {
            var external = ExternalAssetBundle.TryLoadGameObject(Plugin.ExternalCarPrefabAssetName.Value);
            if (external != null)
            {
                // Prefer RenderOnlyClone to keep original materials/shaders from the bundle.
                _carPrefab = RenderOnlyClone.TryCreateRenderOnlyPrefab(external.transform)
                             ?? MeshOnlyClone.TryCreateMeshRendererHierarchyPrefab(external.transform);

                if (_carPrefab != null && !_loggedExternalCar)
                {
                    _loggedExternalCar = true;
                    Plugin.Log.LogInfo($"Using external Car prefab from AssetBundle: '{Plugin.ExternalCarPrefabAssetName.Value}'.");
                }

                if (_carPrefab != null)
                    return _carPrefab;
            }
        }

        if (!GameAccess.TryFindLocalCarVisualRoot(out var root))
            return null;

        // Car visuals: copy full car hierarchy (body + wheels), while filtering out collider/debug meshes.
        // This also naturally picks up modifications from other mods because it clones what's in the scene.
        _carPrefab = CarVisualClone.TryCreateCarPrefab(root)
                    ?? (ExternalMods.IsCustomTruckShopLoaded() ? MeshOnlyClone.TryCreateMeshRendererHierarchyPrefab(root) : null)
                    ?? RenderOnlyClone.TryCreateRenderOnlyPrefab(root);

        if (!_loggedCarPrefabInfo && _carPrefab != null)
        {
            _loggedCarPrefabInfo = true;
            LogPrefabInfo("Car", root, _carPrefab);
        }
        return _carPrefab;
    }

    private static void LogPrefabInfo(string kind, Transform sourceRoot, GameObject prefab)
    {
        try
        {
            var renderers = prefab.GetComponentsInChildren<Renderer>(includeInactive: true);
            var shaders = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null)
                    continue;

                var mats = r.sharedMaterials;
                if (mats == null)
                    continue;

                for (var j = 0; j < mats.Length; j++)
                {
                    var mat = mats[j];
                    if (mat == null)
                        continue;
                    var sh = mat.shader;
                    shaders.Add(sh != null ? sh.name : "<null-shader>");
                }
            }

            Plugin.Log.LogInfo($"Prefab[{kind}] sourceRoot='{sourceRoot.name}', prefab='{prefab.name}', renderers={renderers.Length}, shaders=[{string.Join(", ", shaders)}]");
        }
        catch
        {
            // ignore
        }
    }
}
