using UnityEngine;

namespace EasyDeliveryCoLanCoop;

internal static class CarVisualClone
{
    internal static GameObject? TryCreateCarPrefab(Transform carRoot)
    {
        if (carRoot == null)
            return null;

        try
        {
            var root = new GameObject($"EasyDeliveryCoLanCoop.CarPrefab.{carRoot.name}");
            root.hideFlags = HideFlags.DontSave;

            // Preserve the car's world scale. If the car is under a scaled parent in the scene,
            // lossyScale captures the effective scale we need for a root-level ghost object.
            root.transform.localScale = carRoot.lossyScale;

            // Keep root-level car audio (many builds place impact/horn sources on Car root).
            CopyAudioSource(carRoot, root.transform);

            // Copy children (not the root itself) to avoid inheriting any odd scaling on intermediate nodes.
            for (var i = 0; i < carRoot.childCount; i++)
                CopyRecursive(carRoot.GetChild(i), root.transform);

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
        if (ShouldSkip(src))
            return;

        var dst = new GameObject(src.name);
        dst.hideFlags = HideFlags.DontSave;
        dst.transform.SetParent(dstParent, worldPositionStays: false);
        dst.transform.localPosition = src.localPosition;
        dst.transform.localRotation = src.localRotation;
        dst.transform.localScale = src.localScale;

        // Copy MeshRenderer/MeshFilter visuals.
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
                mats[i] = srcMats[i] != null ? new Material(srcMats[i]) : null!;

            mr.sharedMaterials = mats;
            mr.shadowCastingMode = srcMeshRenderer.shadowCastingMode;
            mr.receiveShadows = srcMeshRenderer.receiveShadows;
            mr.lightProbeUsage = srcMeshRenderer.lightProbeUsage;
            mr.reflectionProbeUsage = srcMeshRenderer.reflectionProbeUsage;
        }

        CopyAudioSource(src, dst.transform);

        for (var i = 0; i < src.childCount; i++)
            CopyRecursive(src.GetChild(i), dst.transform);
    }

    private static void CopyAudioSource(Transform src, Transform dst)
    {
        if (src == null || dst == null)
            return;

        var srcAudio = src.GetComponent<AudioSource>();
        if (srcAudio == null)
            return;

        var dstAudio = dst.GetComponent<AudioSource>();
        if (dstAudio == null)
            dstAudio = dst.gameObject.AddComponent<AudioSource>();

        dstAudio.clip = srcAudio.clip;
        dstAudio.outputAudioMixerGroup = srcAudio.outputAudioMixerGroup;
        dstAudio.mute = srcAudio.mute;
        dstAudio.bypassEffects = srcAudio.bypassEffects;
        dstAudio.bypassListenerEffects = srcAudio.bypassListenerEffects;
        dstAudio.bypassReverbZones = srcAudio.bypassReverbZones;
        dstAudio.playOnAwake = false;
        dstAudio.loop = srcAudio.loop;
        dstAudio.priority = srcAudio.priority;
        dstAudio.volume = srcAudio.volume;
        dstAudio.pitch = srcAudio.pitch;
        dstAudio.panStereo = srcAudio.panStereo;
        dstAudio.spatialBlend = srcAudio.spatialBlend;
        dstAudio.reverbZoneMix = srcAudio.reverbZoneMix;
        dstAudio.dopplerLevel = srcAudio.dopplerLevel;
        dstAudio.spread = srcAudio.spread;
        dstAudio.rolloffMode = srcAudio.rolloffMode;
        dstAudio.minDistance = srcAudio.minDistance;
        dstAudio.maxDistance = srcAudio.maxDistance;
        dstAudio.enabled = srcAudio.enabled;
    }

    private static bool ShouldSkip(Transform t)
    {
        // Skip obvious collider/debug meshes that tend to show up as huge grey spheres.
        var n = t.name;
        if (!string.IsNullOrEmpty(n))
        {
            var low = n.ToLowerInvariant();
            if (low.Contains("wheelcollider") || low.Contains("collider") || low.Contains("trigger") || low.Contains("hitbox"))
                return true;
            if (low.Contains("debug") || low.Contains("gizmo"))
                return true;
            if (low == "sphere" || low.EndsWith("sphere"))
                return true;
        }

        // Keep nodes with audio even if they also have colliders (common for car root).
        // Otherwise collider-only debug objects are skipped.
        if (t.GetComponent<Collider>() != null && t.GetComponent<AudioSource>() == null)
            return true;

        return false;
    }
}
