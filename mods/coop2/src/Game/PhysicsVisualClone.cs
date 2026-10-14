using UnityEngine;

namespace EasyDeliveryCoLanCoop;

internal static class PhysicsVisualClone
{
    internal static GameObject? TryCreatePhysicsPrefab(Transform sourceRoot)
    {
        if (sourceRoot == null)
            return null;

        try
        {
            var root = new GameObject($"EasyDeliveryCoLanCoop.PhysicsPrefab.{sourceRoot.name}");
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

        CopyRenderers(src, dst);
        CopyCollider(src, dst);
        CopyRigidbody(src, dst);

        for (var i = 0; i < src.childCount; i++)
            CopyRecursive(src.GetChild(i), dst.transform);
    }

    private static void CopyRenderers(Transform src, GameObject dst)
    {
        var srcMeshFilter = src.GetComponent<MeshFilter>();
        if (srcMeshFilter != null && srcMeshFilter.sharedMesh != null)
        {
            var mf = dst.AddComponent<MeshFilter>();
            mf.sharedMesh = srcMeshFilter.sharedMesh;
        }

        var srcMeshRenderer = src.GetComponent<MeshRenderer>();
        if (srcMeshRenderer != null)
        {
            var mr = dst.AddComponent<MeshRenderer>();
            mr.sharedMaterials = srcMeshRenderer.sharedMaterials;
            mr.shadowCastingMode = srcMeshRenderer.shadowCastingMode;
            mr.receiveShadows = srcMeshRenderer.receiveShadows;
            mr.lightProbeUsage = srcMeshRenderer.lightProbeUsage;
            mr.reflectionProbeUsage = srcMeshRenderer.reflectionProbeUsage;
        }

        var srcSkinned = src.GetComponent<SkinnedMeshRenderer>();
        if (srcSkinned != null)
        {
            var smr = dst.AddComponent<SkinnedMeshRenderer>();
            smr.sharedMesh = srcSkinned.sharedMesh;
            smr.sharedMaterials = srcSkinned.sharedMaterials;
            smr.shadowCastingMode = srcSkinned.shadowCastingMode;
            smr.receiveShadows = srcSkinned.receiveShadows;
            smr.lightProbeUsage = srcSkinned.lightProbeUsage;
            smr.reflectionProbeUsage = srcSkinned.reflectionProbeUsage;
        }
    }

    private static void CopyCollider(Transform src, GameObject dst)
    {
        var box = src.GetComponent<BoxCollider>();
        if (box != null)
        {
            var c = dst.AddComponent<BoxCollider>();
            c.center = box.center;
            c.size = box.size;
            c.isTrigger = box.isTrigger;
            c.sharedMaterial = box.sharedMaterial;
            c.enabled = box.enabled;
        }

        var sphere = src.GetComponent<SphereCollider>();
        if (sphere != null)
        {
            var c = dst.AddComponent<SphereCollider>();
            c.center = sphere.center;
            c.radius = sphere.radius;
            c.isTrigger = sphere.isTrigger;
            c.sharedMaterial = sphere.sharedMaterial;
            c.enabled = sphere.enabled;
        }

        var capsule = src.GetComponent<CapsuleCollider>();
        if (capsule != null)
        {
            var c = dst.AddComponent<CapsuleCollider>();
            c.center = capsule.center;
            c.radius = capsule.radius;
            c.height = capsule.height;
            c.direction = capsule.direction;
            c.isTrigger = capsule.isTrigger;
            c.sharedMaterial = capsule.sharedMaterial;
            c.enabled = capsule.enabled;
        }

        var mesh = src.GetComponent<MeshCollider>();
        if (mesh != null)
        {
            var c = dst.AddComponent<MeshCollider>();
            c.sharedMesh = mesh.sharedMesh;
            c.convex = mesh.convex;
            c.cookingOptions = mesh.cookingOptions;
            c.isTrigger = mesh.isTrigger;
            c.sharedMaterial = mesh.sharedMaterial;
            c.enabled = mesh.enabled;
        }
    }

    private static void CopyRigidbody(Transform src, GameObject dst)
    {
        var srcRb = src.GetComponent<Rigidbody>();
        if (srcRb == null)
            return;

        var rb = dst.AddComponent<Rigidbody>();
        rb.mass = srcRb.mass;
        rb.drag = srcRb.drag;
        rb.angularDrag = srcRb.angularDrag;
        rb.useGravity = srcRb.useGravity;
        rb.isKinematic = srcRb.isKinematic;
        rb.interpolation = srcRb.interpolation;
        rb.collisionDetectionMode = srcRb.collisionDetectionMode;
        rb.constraints = srcRb.constraints;
        rb.detectCollisions = srcRb.detectCollisions;
    }
}
