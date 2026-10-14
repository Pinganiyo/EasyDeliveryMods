using UnityEngine;

namespace EasyDeliveryCoLanCoop;

internal static class PayloadPrefabLibrary
{
    private static readonly Dictionary<string, GameObject?> Cache = new(StringComparer.OrdinalIgnoreCase);

    internal static GameObject CreatePayloadInstanceOrFallback(string payloadName, string instanceName)
    {
        payloadName = NormalizePayloadName(payloadName);

        var prefab = GetPayloadPrefab(payloadName);
        GameObject go;

        if (prefab != null)
        {
            go = UnityEngine.Object.Instantiate(prefab);
            go.SetActive(true);
        }
        else
        {
            go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var m = MaterialUtil.GetSceneMaterialFallback();
            if (m != null)
            {
                var mr = go.GetComponent<Renderer>();
                if (mr != null)
                    mr.sharedMaterial = m;
            }

            var rb = go.GetComponent<Rigidbody>();
            if (rb == null)
                rb = go.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.useGravity = false;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
        }

        go.name = instanceName;
        go.hideFlags = HideFlags.DontSave;

        ConfigureNetworkDrivenPhysics(go);
        return go;
    }

    private static GameObject? GetPayloadPrefab(string payloadName)
    {
        if (string.IsNullOrWhiteSpace(payloadName))
            return null;

        if (Cache.TryGetValue(payloadName, out var cached))
            return cached;

        GameObject? prefab = null;

        // 1) External bundle first (has full list of PAYLOAD assets).
        prefab = ExternalAssetBundle.TryLoadGameObject(payloadName);
        if (prefab == null && Plugin.ExternalAssetsEnabled.Value)
            prefab = ExternalAssetBundle.TryLoadGameObjectByBaseName(payloadName);

        // 2) Fallback to local in-memory payload object from the running game.
        if (prefab == null && GameAccess.TryFindPayloadVisualRoot(payloadName, out var localRoot))
            prefab = localRoot.gameObject;

        GameObject? physicsPrefab = null;
        if (prefab != null)
            physicsPrefab = PhysicsVisualClone.TryCreatePhysicsPrefab(prefab.transform);

        Cache[payloadName] = physicsPrefab;
        return physicsPrefab;
    }

    internal static void ConfigureNetworkDrivenPhysics(GameObject root)
    {
        if (root == null)
            return;

        try
        {
            var joints = root.GetComponentsInChildren<Joint>(includeInactive: true);
            for (var i = 0; i < joints.Length; i++)
            {
                var j = joints[i];
                if (j == null)
                    continue;
                UnityEngine.Object.Destroy(j);
            }

            var rigidbodies = root.GetComponentsInChildren<Rigidbody>(includeInactive: true);
            for (var i = 0; i < rigidbodies.Length; i++)
            {
                var rb = rigidbodies[i];
                if (rb == null)
                    continue;

                rb.isKinematic = true;
                rb.useGravity = false;
                rb.interpolation = RigidbodyInterpolation.None;
                rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
                rb.constraints = RigidbodyConstraints.FreezeAll;
                rb.detectCollisions = false;
            }

            var colliders = root.GetComponentsInChildren<Collider>(includeInactive: true);
            for (var i = 0; i < colliders.Length; i++)
            {
                var c = colliders[i];
                if (c == null)
                    continue;
                c.enabled = true;
            }
        }
        catch
        {
            // ignore
        }
    }

    private static string NormalizePayloadName(string? name)
    {
        name ??= string.Empty;
        name = name.Trim();
        if (name.EndsWith("(Clone)", StringComparison.Ordinal))
            name = name.Substring(0, name.Length - "(Clone)".Length).Trim();
        return name;
    }
}
