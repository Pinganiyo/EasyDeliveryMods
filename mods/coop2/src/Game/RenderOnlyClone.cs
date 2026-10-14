using UnityEngine;

namespace EasyDeliveryCoLanCoop;

internal static class RenderOnlyClone
{
    internal static GameObject? TryCreateRenderOnlyPrefab(Transform sourceRoot)
    {
        if (sourceRoot == null)
            return null;

        try
        {
            var clone = UnityEngine.Object.Instantiate(sourceRoot.gameObject);
            clone.name = $"EasyDeliveryCoLanCoop.RenderOnlyPrefab.{sourceRoot.name}";
            clone.hideFlags = HideFlags.DontSave;

            foreach (var t in clone.GetComponentsInChildren<Transform>(includeInactive: true))
                t.gameObject.hideFlags = HideFlags.DontSave;

            // Strip everything except transforms + render components.
            var comps = clone.GetComponentsInChildren<Component>(includeInactive: true);
            for (var i = 0; i < comps.Length; i++)
            {
                var c = comps[i];
                if (c == null)
                    continue;

                if (c is Transform)
                    continue;

                if (c is Renderer)
                    continue;

                if (c is MeshFilter)
                    continue;

                UnityEngine.Object.Destroy(c);
            }

            clone.SetActive(false);
            return clone;
        }
        catch
        {
            return null;
        }
    }
}
