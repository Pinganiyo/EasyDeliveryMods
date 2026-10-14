using UnityEngine;

namespace EasyDeliveryCoLanCoop;

internal static class MaterialUtil
{
    private static Material? _cached;
    private static float _nextRescanAt;

    internal static Material? GetSceneMaterialFallback()
    {
        if (_cached != null)
            return _cached;

        // Avoid scanning every frame if this is called early.
        if (Time.unscaledTime < _nextRescanAt)
            return null;

        _nextRescanAt = Time.unscaledTime + 1.0f;

        // Prefer car materials (usually compatible with the current render pipeline).
        if (GameAccess.TryFindLocalCarVisualRoot(out var carRoot))
        {
            var m = FindFirstMaterial(carRoot);
            if (m != null)
                return _cached = m;
        }

        // Then player materials.
        if (GameAccess.TryFindLocalPlayerVisualRoot(out var playerRoot))
        {
            var m = FindFirstMaterial(playerRoot);
            if (m != null)
                return _cached = m;
        }

        // Last resort: any renderer in scene.
        var anyRenderer = UnityEngine.Object.FindObjectOfType<Renderer>();
        if (anyRenderer != null)
        {
            var mats = anyRenderer.sharedMaterials;
            if (mats != null)
            {
                for (var i = 0; i < mats.Length; i++)
                {
                    if (mats[i] != null)
                        return _cached = mats[i];
                }
            }
        }

        return null;
    }

    private static Material? FindFirstMaterial(Transform root)
    {
        var renderers = root.GetComponentsInChildren<Renderer>(includeInactive: true);
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
                if (mats[j] != null)
                    return mats[j];
            }
        }

        return null;
    }
}
