using UnityEngine;

namespace EasyDeliveryCoLanCoop;

internal static class RemoteCars
{
    private static readonly Dictionary<string, float> NextMaterialFixAt = new(StringComparer.Ordinal);
    private static readonly HashSet<string> ExactCollidersApplied = new(StringComparer.Ordinal);

    private sealed class RemoteCar
    {
        public readonly GameObject Root;
        public readonly Dictionary<string, GameObject> Cargo = new(StringComparer.Ordinal);
        public readonly TextMesh Label;
        public readonly bool IsFallbackPrimitive;

        public RemoteCar(string key)
        {
            var prefab = RemoteVisualPrefabs.GetCarPrefab();
            if (prefab != null)
            {
                Root = UnityEngine.Object.Instantiate(prefab);
                Root.SetActive(true);
                IsFallbackPrimitive = false;
            }
            else
            {
                Root = GameObject.CreatePrimitive(PrimitiveType.Cube);
                IsFallbackPrimitive = true;

                // In SRP/ShaderGraph games, Unity's default primitive material can appear pink.
                // Reuse an existing scene material as a safe fallback.
                var m = MaterialUtil.GetSceneMaterialFallback();
                if (m != null)
                {
                    var mr = Root.GetComponent<Renderer>();
                    if (mr != null)
                        mr.sharedMaterial = m;
                }

                // Make it roughly car-sized.
                Root.transform.localScale = new Vector3(1.8f, 1.1f, 3.6f);

                // No collider interactions.
                if (Root.TryGetComponent<Collider>(out var col))
                    UnityEngine.Object.Destroy(col);
            }

            Root.name = $"EasyDeliveryCoLanCoop.RemoteCar.{key}";
            Root.hideFlags = HideFlags.DontSave;

            EnsureNetworkPhysics(key, Root);

            var labelGo = new GameObject($"EasyDeliveryCoLanCoop.RemoteCarLabel.{key}");
            labelGo.hideFlags = HideFlags.DontSave;
            labelGo.transform.SetParent(Root.transform, worldPositionStays: false);
            labelGo.transform.localPosition = new Vector3(0f, 1.3f, 0f);
            labelGo.transform.localRotation = Quaternion.identity;

            Label = labelGo.AddComponent<TextMesh>();
            Label.anchor = TextAnchor.LowerCenter;
            Label.alignment = TextAlignment.Center;
            Label.characterSize = 0.09f;
            Label.fontSize = 64;
            Label.richText = false;
        }
    }

    private static readonly Dictionary<string, RemoteCar> Cars = new(StringComparer.Ordinal);

    internal static void Remove(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        if (!Cars.TryGetValue(key, out var car))
            return;

        try
        {
            foreach (var kv in car.Cargo)
            {
                if (kv.Value != null)
                    UnityEngine.Object.Destroy(kv.Value);
            }
        }
        catch
        {
            // ignore
        }

        if (car.Root != null)
            UnityEngine.Object.Destroy(car.Root);

        Cars.Remove(key);
        NextMaterialFixAt.Remove(key);
        ExactCollidersApplied.Remove(key);
    }

    internal static void ApplyCarState(string key, Vector3 pos, Quaternion rot, string? nickname = null)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        if (!Cars.TryGetValue(key, out var car) || car.Root == null)
        {
            car = new RemoteCar(key);
            Cars[key] = car;
        }

        // If the car was created before the local visual prefab was available, upgrade it.
        if (car.IsFallbackPrimitive)
        {
            var prefab = RemoteVisualPrefabs.GetCarPrefab();
            if (prefab != null)
            {
                try
                {
                    var old = car;
                    var upgraded = new RemoteCar(key);

                    // Keep cargo by reparenting existing cargo GOs to the new root.
                    foreach (var kv in old.Cargo)
                    {
                        if (kv.Value == null)
                            continue;
                        kv.Value.transform.SetParent(upgraded.Root.transform, worldPositionStays: false);
                        upgraded.Cargo[kv.Key] = kv.Value;
                    }

                    Cars[key] = upgraded;

                    if (old.Root != null)
                        UnityEngine.Object.Destroy(old.Root);
                    car = upgraded;
                }
                catch
                {
                    // ignore upgrade failures; keep fallback.
                }
            }
        }

        car.Root.transform.SetPositionAndRotation(pos, rot);

        // Keep ghost car size consistent with the game's car size.
        // (Some hierarchies rely on parent scaling; our cloned prefab root is at scene root.)
        if (!car.IsFallbackPrimitive && GameAccess.TryFindLocalCarVisualRoot(out var localCarRoot))
            car.Root.transform.localScale = localCarRoot.lossyScale;

        // If we're still using a primitive renderer and its shader is unsupported, reapply a scene material.
        var mr = car.Root.GetComponent<Renderer>();
        if (mr != null && mr.sharedMaterial != null)
        {
            var sh = mr.sharedMaterial.shader;
            if (sh == null || !sh.isSupported)
            {
                var m = MaterialUtil.GetSceneMaterialFallback();
                if (m != null)
                    mr.sharedMaterial = m;
            }
        }

        // Also scan the full car hierarchy for unsupported materials (pink) and replace them.
        FixUnsupportedMaterials(key, car.Root);

        // Keep remote car collidable and physics-enabled (network-driven kinematic body).
        EnsureNetworkPhysics(key, car.Root);

        if (car.Label != null)
        {
            var text = Plugin.SanitizeNickname(nickname);
            car.Label.text = text;
            var cam = Camera.main;
            if (cam != null)
                car.Label.transform.rotation = Quaternion.LookRotation(car.Label.transform.position - cam.transform.position);
        }
    }

    internal static void ApplyCargo(string carKey, IReadOnlyList<(string Name, Vector3 LocalPos, Quaternion LocalRot)> cargo)
    {
        if (string.IsNullOrWhiteSpace(carKey))
            return;
        if (!Cars.TryGetValue(carKey, out var car) || car.Root == null)
        {
            car = new RemoteCar(carKey);
            Cars[carKey] = car;
        }

        var alive = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < cargo.Count; i++)
        {
            var rawName = cargo[i].Name;
            if (string.IsNullOrWhiteSpace(rawName))
                rawName = $"Cargo#{i}";
            var name = NormalizePayloadName(rawName);
            var cargoKey = $"{i}:{name}";

            alive.Add(cargoKey);

            if (!car.Cargo.TryGetValue(cargoKey, out var go) || go == null)
            {
                go = PayloadPrefabLibrary.CreatePayloadInstanceOrFallback(
                    name,
                    $"EasyDeliveryCoLanCoop.RemoteCargo.{carKey}.{cargoKey}");
                go.transform.SetParent(car.Root.transform, worldPositionStays: false);
                car.Cargo[cargoKey] = go;
            }

            go.transform.localPosition = cargo[i].LocalPos;
            go.transform.localRotation = cargo[i].LocalRot;
        }

        var toRemove = new List<string>();
        foreach (var kv in car.Cargo)
        {
            if (!alive.Contains(kv.Key))
            {
                if (kv.Value != null)
                    UnityEngine.Object.Destroy(kv.Value);
                toRemove.Add(kv.Key);
            }
        }

        foreach (var k in toRemove)
            car.Cargo.Remove(k);
    }

    internal static void PlayCarSfx(string key, byte sfxId, string? clipName = null, string? sourceName = null)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;
        if (!Cars.TryGetValue(key, out var car) || car.Root == null)
            return;

        if (GameAccess.TryPlayLikelyCarSfx(car.Root.transform, sfxId, clipName, sourceName))
            return;

        GameAccess.TryPlayCarSfxAudibleFallback(car.Root.transform, sfxId, clipName, sourceName);
    }

    private static string NormalizePayloadName(string? name)
    {
        name ??= string.Empty;
        name = name.Trim();
        if (name.EndsWith("(Clone)", StringComparison.Ordinal))
            name = name.Substring(0, name.Length - "(Clone)".Length).Trim();
        return name;
    }

    private static void EnsureNetworkPhysics(string carKey, GameObject root)
    {
        if (root == null)
            return;

        try
        {
            // Try to mirror exact colliders from the local car once per remote car key.
            if (!ExactCollidersApplied.Contains(carKey) && GameAccess.TryFindLocalCarVisualRoot(out var localCarRoot))
            {
                TryMirrorCollidersFromLocal(root, localCarRoot);
                ExactCollidersApplied.Add(carKey);
            }

            var colliders = root.GetComponentsInChildren<Collider>(includeInactive: true);
            if (colliders == null || colliders.Length == 0)
            {
                // If exact colliders are unavailable, build a fallback box collider from render bounds.
                if (TryGetCombinedRendererBounds(root, out var b))
                {
                    var box = root.GetComponent<BoxCollider>();
                    if (box == null)
                        box = root.AddComponent<BoxCollider>();

                    var localCenter = root.transform.InverseTransformPoint(b.center);
                    var sx = Mathf.Abs(root.transform.lossyScale.x);
                    var sy = Mathf.Abs(root.transform.lossyScale.y);
                    var sz = Mathf.Abs(root.transform.lossyScale.z);
                    if (sx < 1e-4f) sx = 1f;
                    if (sy < 1e-4f) sy = 1f;
                    if (sz < 1e-4f) sz = 1f;

                    box.center = localCenter;
                    box.size = new Vector3(
                        Mathf.Max(0.1f, b.size.x / sx),
                        Mathf.Max(0.1f, b.size.y / sy),
                        Mathf.Max(0.1f, b.size.z / sz));
                    box.isTrigger = false;
                }
            }
            else
            {
                for (var i = 0; i < colliders.Length; i++)
                {
                    var c = colliders[i];
                    if (c == null)
                        continue;
                    c.enabled = true;
                    c.isTrigger = false;
                }
            }

            var rb = root.GetComponent<Rigidbody>();
            if (rb == null)
                rb = root.AddComponent<Rigidbody>();

            rb.isKinematic = true;
            rb.useGravity = false;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.Discrete;
            rb.constraints = RigidbodyConstraints.None;
            rb.detectCollisions = true;
        }
        catch
        {
            // ignore
        }
    }

    private static void TryMirrorCollidersFromLocal(GameObject remoteRoot, Transform localRoot)
    {
        if (remoteRoot == null || localRoot == null)
            return;

        var locals = localRoot.GetComponentsInChildren<Transform>(includeInactive: true);
        for (var i = 0; i < locals.Length; i++)
        {
            var src = locals[i];
            if (src == null)
                continue;

            // Ignore wheel colliders to avoid unstable suspension simulation on ghost cars.
            if (src.GetComponent("WheelCollider") != null)
                continue;

            var relPath = GetRelativePath(localRoot, src);
            var dst = EnsurePath(remoteRoot.transform, relPath, src);
            if (dst == null)
                continue;

            CopyCollider(src, dst);
        }
    }

    private static Transform? EnsurePath(Transform remoteRoot, string relPath, Transform srcRef)
    {
        if (remoteRoot == null)
            return null;

        if (string.IsNullOrEmpty(relPath))
            return remoteRoot;

        var seg = relPath.Split('/');
        var cur = remoteRoot;
        var srcCur = srcRef;

        // Build reverse map of source parents to assign correct local transforms when creating nodes.
        var srcChain = new List<Transform>();
        while (srcCur != null)
        {
            srcChain.Add(srcCur);
            if (srcCur.parent == null)
                break;
            srcCur = srcCur.parent;
        }
        srcChain.Reverse();

        var srcIndex = 1; // first segment is under root
        for (var i = 0; i < seg.Length; i++)
        {
            var name = seg[i];
            if (string.IsNullOrEmpty(name))
                continue;

            var next = cur.Find(name);
            if (next == null)
            {
                var go = new GameObject(name);
                go.hideFlags = HideFlags.DontSave;
                next = go.transform;
                next.SetParent(cur, worldPositionStays: false);

                if (srcIndex < srcChain.Count)
                {
                    var srcT = srcChain[srcIndex];
                    next.localPosition = srcT.localPosition;
                    next.localRotation = srcT.localRotation;
                    next.localScale = srcT.localScale;
                }
            }

            cur = next;
            srcIndex++;
        }

        return cur;
    }

    private static void CopyCollider(Transform src, Transform dst)
    {
        if (src == null || dst == null)
            return;

        var srcBox = src.GetComponent<BoxCollider>();
        if (srcBox != null && dst.GetComponent<BoxCollider>() == null)
        {
            var c = dst.gameObject.AddComponent<BoxCollider>();
            c.center = srcBox.center;
            c.size = srcBox.size;
            c.isTrigger = false;
            c.sharedMaterial = srcBox.sharedMaterial;
            c.enabled = srcBox.enabled;
        }

        var srcSphere = src.GetComponent<SphereCollider>();
        if (srcSphere != null && dst.GetComponent<SphereCollider>() == null)
        {
            var c = dst.gameObject.AddComponent<SphereCollider>();
            c.center = srcSphere.center;
            c.radius = srcSphere.radius;
            c.isTrigger = false;
            c.sharedMaterial = srcSphere.sharedMaterial;
            c.enabled = srcSphere.enabled;
        }

        var srcCapsule = src.GetComponent<CapsuleCollider>();
        if (srcCapsule != null && dst.GetComponent<CapsuleCollider>() == null)
        {
            var c = dst.gameObject.AddComponent<CapsuleCollider>();
            c.center = srcCapsule.center;
            c.radius = srcCapsule.radius;
            c.height = srcCapsule.height;
            c.direction = srcCapsule.direction;
            c.isTrigger = false;
            c.sharedMaterial = srcCapsule.sharedMaterial;
            c.enabled = srcCapsule.enabled;
        }

        var srcMesh = src.GetComponent<MeshCollider>();
        if (srcMesh != null && dst.GetComponent<MeshCollider>() == null)
        {
            var c = dst.gameObject.AddComponent<MeshCollider>();
            c.sharedMesh = srcMesh.sharedMesh;
            c.convex = srcMesh.convex;
            c.cookingOptions = srcMesh.cookingOptions;
            c.isTrigger = false;
            c.sharedMaterial = srcMesh.sharedMaterial;
            c.enabled = srcMesh.enabled;
        }
    }

    private static string GetRelativePath(Transform root, Transform node)
    {
        if (root == null || node == null || root == node)
            return string.Empty;

        var parts = new List<string>();
        var cur = node;
        while (cur != null && cur != root)
        {
            parts.Add(cur.name);
            cur = cur.parent;
        }

        parts.Reverse();
        return string.Join("/", parts);
    }

    private static bool TryGetCombinedRendererBounds(GameObject root, out Bounds bounds)
    {
        bounds = default;
        if (root == null)
            return false;

        try
        {
            var renderers = root.GetComponentsInChildren<Renderer>(includeInactive: true);
            if (renderers == null || renderers.Length == 0)
                return false;

            var has = false;
            var b = new Bounds();
            for (var i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null)
                    continue;

                if (!has)
                {
                    b = r.bounds;
                    has = true;
                }
                else
                {
                    b.Encapsulate(r.bounds);
                }
            }

            if (!has)
                return false;

            bounds = b;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void FixUnsupportedMaterials(string key, GameObject go)
    {
        if (go == null)
            return;

        var now = Time.unscaledTime;
        if (NextMaterialFixAt.TryGetValue(key, out var next) && now < next)
            return;
        NextMaterialFixAt[key] = now + 2.0f;

        var fallback = MaterialUtil.GetSceneMaterialFallback();
        if (fallback == null)
            return;

        try
        {
            var renderers = go.GetComponentsInChildren<Renderer>(includeInactive: true);
            for (var i = 0; i < renderers.Length; i++)
            {
                var r = renderers[i];
                if (r == null)
                    continue;

                var mats = r.sharedMaterials;
                if (mats == null || mats.Length == 0)
                    continue;

                var changed = false;
                for (var m = 0; m < mats.Length; m++)
                {
                    var mat = mats[m];
                    if (mat == null)
                        continue;

                    var sh = mat.shader;
                    var bad = sh == null || !sh.isSupported || string.Equals(sh.name, "Hidden/InternalErrorShader", StringComparison.Ordinal);
                    if (bad)
                    {
                        mats[m] = fallback;
                        changed = true;
                    }
                }

                if (changed)
                    r.sharedMaterials = mats;
            }
        }
        catch
        {
            // ignore
        }
    }
}
