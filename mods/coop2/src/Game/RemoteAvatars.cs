using UnityEngine;

namespace EasyDeliveryCoLanCoop;

internal static class RemoteAvatars
{
    private static readonly Dictionary<string, GameObject> Avatars = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, TextMesh> Labels = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, GameObject> HeldPayloads = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> HeldPayloadNames = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, float> NextMaterialFixAt = new(StringComparer.Ordinal);
    private static readonly HashSet<string> PrimitiveFallback = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Color> AvatarTints = new(StringComparer.Ordinal);

    internal static void ApplyPlayerPose(string key, string nickname, float px, float py, float pz, float qx, float qy, float qz, float qw, bool inCar, string? heldPayloadName)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        if (!Avatars.TryGetValue(key, out var go) || go == null)
        {
            var prefab = RemoteVisualPrefabs.GetPlayerPrefab();
            if (prefab != null)
            {
                go = UnityEngine.Object.Instantiate(prefab);
                go.SetActive(true);
                PrimitiveFallback.Remove(key);
            }
            else
            {
                go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                PrimitiveFallback.Add(key);

                // In SRP/ShaderGraph games, Unity's default primitive material can appear pink.
                // Reuse an existing scene material as a safe fallback.
                var m = MaterialUtil.GetSceneMaterialFallback();
                if (m != null)
                {
                    var primRenderer = go.GetComponent<Renderer>();
                    if (primRenderer != null)
                        primRenderer.sharedMaterial = m;
                }

                // Remove collider to avoid physics interactions.
                if (go.TryGetComponent<Collider>(out var col))
                    UnityEngine.Object.Destroy(col);
            }

            go.name = $"EasyDeliveryCoLanCoop.RemotePlayer.{key}";
            go.hideFlags = HideFlags.DontSave;

            Avatars[key] = go;
        }

        // If we spawned a primitive early (prefab not ready), keep retrying and upgrade to the real prefab
        // once the local player model becomes available.
        if (go != null && PrimitiveFallback.Contains(key))
        {
            var prefab = RemoteVisualPrefabs.GetPlayerPrefab();
            if (prefab != null)
            {
                var newGo = UnityEngine.Object.Instantiate(prefab);
                newGo.SetActive(true);
                newGo.name = $"EasyDeliveryCoLanCoop.RemotePlayer.{key}";
                newGo.hideFlags = HideFlags.DontSave;

                // Move label to new root if it already exists.
                if (Labels.TryGetValue(key, out var existingLabel) && existingLabel != null)
                {
                    existingLabel.transform.SetParent(newGo.transform, worldPositionStays: false);
                    existingLabel.transform.localPosition = new Vector3(0f, 1.8f, 0f);
                    existingLabel.transform.localRotation = Quaternion.identity;
                }

                UnityEngine.Object.Destroy(go);
                go = newGo;
                Avatars[key] = go;
                PrimitiveFallback.Remove(key);
                NextMaterialFixAt.Remove(key);
                Plugin.Log.LogInfo($"Upgraded remote player '{key}' from primitive to prefab.");
            }
            else
            {
                // If we're still on primitive fallback, try to apply a safe scene material once it becomes available.
                ApplyFallbackMaterialToRoot(go);
            }
        }

        if (go == null)
            return;

        if (!Labels.TryGetValue(key, out var label) || label == null)
        {
            var labelGo = new GameObject($"EasyDeliveryCoLanCoop.RemotePlayerLabel.{key}");
            labelGo.hideFlags = HideFlags.DontSave;
            labelGo.transform.SetParent(go.transform, worldPositionStays: false);
            labelGo.transform.localPosition = new Vector3(0f, 1.8f, 0f);
            labelGo.transform.localRotation = Quaternion.identity;

            label = labelGo.AddComponent<TextMesh>();
            label.anchor = TextAnchor.LowerCenter;
            label.alignment = TextAlignment.Center;
            label.characterSize = 0.08f;
            label.fontSize = 64;
            label.richText = false;
            Labels[key] = label;
        }

        // Keep avatar visible even if the remote player is in a car.
        // Hiding makes it seem like players never connect.
        go.SetActive(true);
        go.transform.position = new Vector3(px, py, pz);
        // Some models face a different forward direction; apply a constant yaw offset.
        var q = new Quaternion(qx, qy, qz, qw);
        var yaw = Plugin.RemoteAvatarYawOffsetDegrees != null ? Plugin.RemoteAvatarYawOffsetDegrees.Value : -90f;
        go.transform.rotation = q * Quaternion.Euler(0f, yaw, 0f);

        // Repair unsupported shaders (pink) for any renderer in the avatar hierarchy.
        // This can happen when cloned materials reference shaders not supported/loaded on the other peer.
        FixUnsupportedMaterials(key, go);

        ApplyHeldPayload(key, go, heldPayloadName);

        // If this is a primitive fallback and its shader is unsupported (pink), reapply a scene material.
        var mr = go.GetComponent<Renderer>();
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

        if (label != null)
        {
            label.text = Plugin.SanitizeNickname(nickname);
            // Face the camera if possible.
            var cam = Camera.main;
            if (cam != null)
                label.transform.rotation = Quaternion.LookRotation(label.transform.position - cam.transform.position);
        }

        // Apply color tint if one has been received for this peer.
        if (AvatarTints.TryGetValue(key, out var tint))
            ApplyTintToGameObject(go, tint);
    }

    private static void ApplyFallbackMaterialToRoot(GameObject go)
    {
        if (go == null)
            return;

        var mr = go.GetComponent<Renderer>();
        if (mr == null)
            return;

        var m = MaterialUtil.GetSceneMaterialFallback();
        if (m != null)
            mr.sharedMaterial = m;
    }

    private static void FixUnsupportedMaterials(string key, GameObject go)
    {
        if (go == null)
            return;

        // Throttle per avatar.
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

    /// <summary>Store and immediately apply a tint color to a remote player's avatar.</summary>
    internal static void ApplyTint(string key, Color color)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        AvatarTints[key] = color;

        if (Avatars.TryGetValue(key, out var go) && go != null)
            ApplyTintToGameObject(go, color);
    }

    private static void ApplyTintToGameObject(GameObject go, Color tint)
    {
        if (go == null) return;
        try
        {
            var renderers = go.GetComponentsInChildren<Renderer>(includeInactive: true);
            foreach (var r in renderers)
            {
                if (r == null) continue;
                // Create per-instance materials so we don't modify the shared asset.
                var mats = r.materials;
                for (var i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null) continue;
                    // Try URP _BaseColor first, then legacy _Color, then .color.
                    if (mats[i].HasProperty("_BaseColor"))
                        mats[i].SetColor("_BaseColor", tint);
                    else if (mats[i].HasProperty("_Color"))
                        mats[i].SetColor("_Color", tint);
                    else
                        mats[i].color = tint;
                }
                r.materials = mats;
            }
        }
        catch { /* ignore shader errors */ }
    }

    internal static void CleanupMissing(HashSet<string> aliveKeys)
    {
        var toRemove = new List<string>();
        foreach (var kv in Avatars)
        {
            if (!aliveKeys.Contains(kv.Key))
            {
                if (kv.Value != null)
                    UnityEngine.Object.Destroy(kv.Value);
                if (Labels.TryGetValue(kv.Key, out var label) && label != null)
                    UnityEngine.Object.Destroy(label.gameObject);
                if (HeldPayloads.TryGetValue(kv.Key, out var payloadGo) && payloadGo != null)
                    UnityEngine.Object.Destroy(payloadGo);
                toRemove.Add(kv.Key);
            }
        }

        foreach (var k in toRemove)
        {
            Avatars.Remove(k);
            Labels.Remove(k);
            HeldPayloads.Remove(k);
            HeldPayloadNames.Remove(k);
            NextMaterialFixAt.Remove(k);
            PrimitiveFallback.Remove(k);
            AvatarTints.Remove(k);
        }
    }

    internal static void Remove(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        if (Avatars.TryGetValue(key, out var go) && go != null)
            UnityEngine.Object.Destroy(go);

        if (Labels.TryGetValue(key, out var label) && label != null)
            UnityEngine.Object.Destroy(label.gameObject);

        if (HeldPayloads.TryGetValue(key, out var payloadGo) && payloadGo != null)
            UnityEngine.Object.Destroy(payloadGo);

        Avatars.Remove(key);
        Labels.Remove(key);
        HeldPayloads.Remove(key);
        HeldPayloadNames.Remove(key);
        NextMaterialFixAt.Remove(key);
        PrimitiveFallback.Remove(key);
        AvatarTints.Remove(key);
    }

    private static void ApplyHeldPayload(string key, GameObject avatarRoot, string? heldPayloadName)
    {
        if (avatarRoot == null)
            return;

        heldPayloadName ??= string.Empty;
        heldPayloadName = NormalizePayloadName(heldPayloadName);

        if (string.IsNullOrWhiteSpace(heldPayloadName) || !heldPayloadName.StartsWith("PAYLOAD", StringComparison.OrdinalIgnoreCase))
        {
            // Remove old payload if any.
            if (HeldPayloads.TryGetValue(key, out var old) && old != null)
                UnityEngine.Object.Destroy(old);
            HeldPayloads.Remove(key);
            HeldPayloadNames.Remove(key);
            return;
        }

        if (HeldPayloadNames.TryGetValue(key, out var existing) && string.Equals(existing, heldPayloadName, StringComparison.Ordinal))
        {
            // Keep it parented and positioned.
            if (HeldPayloads.TryGetValue(key, out var go) && go != null)
            {
                go.transform.SetParent(avatarRoot.transform, worldPositionStays: false);
                go.transform.localPosition = new Vector3(
                    Plugin.RemoteHeldPayloadOffsetX.Value,
                    Plugin.RemoteHeldPayloadOffsetY.Value,
                    Plugin.RemoteHeldPayloadOffsetZ.Value);
                go.transform.localRotation = Quaternion.identity;
                var s = Mathf.Max(0.01f, Plugin.RemoteHeldPayloadUniformScale.Value);
                go.transform.localScale = new Vector3(s, s, s);
            }
            return;
        }

        // Replace payload.
        if (HeldPayloads.TryGetValue(key, out var prev) && prev != null)
            UnityEngine.Object.Destroy(prev);

        var payload = PayloadPrefabLibrary.CreatePayloadInstanceOrFallback(
            heldPayloadName,
            $"EasyDeliveryCoLanCoop.RemoteHeldPayload.{key}.{heldPayloadName}");

        payload.hideFlags = HideFlags.DontSave;
        payload.transform.SetParent(avatarRoot.transform, worldPositionStays: false);
        payload.transform.localPosition = new Vector3(
            Plugin.RemoteHeldPayloadOffsetX.Value,
            Plugin.RemoteHeldPayloadOffsetY.Value,
            Plugin.RemoteHeldPayloadOffsetZ.Value);
        payload.transform.localRotation = Quaternion.identity;
        var scale = Mathf.Max(0.01f, Plugin.RemoteHeldPayloadUniformScale.Value);
        payload.transform.localScale = new Vector3(scale, scale, scale);

        PayloadPrefabLibrary.ConfigureNetworkDrivenPhysics(payload);

        HeldPayloads[key] = payload;
        HeldPayloadNames[key] = heldPayloadName;
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
