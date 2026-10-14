using UnityEngine;

namespace EasyDeliveryCoLanCoop;

internal static class MeshOnlyClone
{
    internal static GameObject? TryCreateMeshRendererHierarchyPrefab(Transform sourceRoot)
    {
        if (sourceRoot == null)
            return null;

        try
        {
            var root = new GameObject($"EasyDeliveryCoLanCoop.MeshOnlyPrefab.{sourceRoot.name}");
            root.hideFlags = HideFlags.DontSave;

            CopyRecursive(sourceRoot, root.transform);

            root.SetActive(false);
            return root;
        }
        catch
        {
            return null;
        }
    }

    private static void CopyRecursive(Transform src, Transform dstParent)
    {
        var dst = new GameObject(src.name);
        dst.hideFlags = HideFlags.DontSave;
        dst.transform.SetParent(dstParent, worldPositionStays: false);
        dst.transform.localPosition = src.localPosition;
        dst.transform.localRotation = src.localRotation;
        dst.transform.localScale = src.localScale;

        var srcMeshFilter = src.GetComponent<MeshFilter>();
        var srcMeshRenderer = src.GetComponent<MeshRenderer>();

        if (srcMeshFilter != null && srcMeshFilter.sharedMesh != null)
        {
            var mf = dst.AddComponent<MeshFilter>();
            mf.sharedMesh = srcMeshFilter.sharedMesh;
        }

        if (srcMeshRenderer != null)
        {
            var mr = dst.AddComponent<MeshRenderer>();
            var srcMats = srcMeshRenderer.sharedMaterials;
            var mats = new Material[srcMats.Length];
            for (var i = 0; i < srcMats.Length; i++)
            {
                mats[i] = srcMats[i] != null ? new Material(srcMats[i]) : null!;
            }

            mr.sharedMaterials = mats;
            mr.shadowCastingMode = srcMeshRenderer.shadowCastingMode;
            mr.receiveShadows = srcMeshRenderer.receiveShadows;
            mr.lightProbeUsage = srcMeshRenderer.lightProbeUsage;
            mr.reflectionProbeUsage = srcMeshRenderer.reflectionProbeUsage;
        }

        // NOTE: this intentionally copies only MeshRenderer/MeshFilter, mirroring the other mod.
        // It avoids cloning gameplay scripts/rigidbodies/animators etc.
        for (var i = 0; i < src.childCount; i++)
        {
            var child = src.GetChild(i);
            CopyRecursive(child, dst.transform);
        }
    }
}
