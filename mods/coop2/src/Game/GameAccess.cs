using System.Collections;
using System.Reflection;
using System.Runtime.Serialization;
using UnityEngine;

namespace EasyDeliveryCoLanCoop;

internal static class GameAccess
{
    private static readonly BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly string[] CarHornKeywords = { "horn", "honk", "beep", "klaxon", "claxon", "signal" };
    private static readonly string[] CarSkidKeywords = { "skid", "drift", "tire", "screech", "slide", "brake", "squeak", "scrape", "friction", "slip" };
    private static readonly string[] CarCrashKeywords = { "crash", "impact", "hit", "collision", "bump", "metal", "smash", "thud", "clunk", "bang", "bonk" };
    private static readonly string[] CarHornNoiseKeywords = { "headlight", "light", "door", "engine", "motor", "idle", "rpm", "gear", "radio", "music", "wind", "road" };
    private static readonly Dictionary<int, bool> LocalCarSfxPlayingBySource = new();
    private static readonly Dictionary<int, float> LocalCarSfxNextLoopEmitAt = new();
    private static readonly Dictionary<string, AudioClip> LoadedAudioClipByName = new(StringComparer.OrdinalIgnoreCase);

    internal const byte CarSfxHorn = 1;
    internal const byte CarSfxSkid = 2;
    internal const byte CarSfxCrash = 3;

    private static bool _jobApplyDisabled;
    private static string? _jobApplyDisabledReason;
    private static bool _loggedPlayerVisualRootFail;
    private static bool _loggedMenuButtonDump;
    private static bool _loggedUiToolkitButtonDump;

    internal static bool TryReadLocalPlayerPose(out Vector3 pos, out Quaternion rot)
    {
        pos = default;
        rot = default;

        var controller = TryFindObjectOfTypeByName("sCharacterController");
        if (controller is Component comp)
        {
            try
            {
                // Use visual bounds center when possible so remote avatar matches what the player sees.
                if (TryGetCombinedRendererBounds(comp.transform, out var b))
                {
                    pos = b.center;
                    rot = comp.transform.rotation;
                    return true;
                }

                pos = comp.transform.position;
                rot = comp.transform.rotation;
                return true;
            }
            catch
            {
                // fall through to camera
            }
        }

        // Fallback for newer builds / renamed controller scripts: use the active camera.
        try
        {
            var cam = Camera.main;
            if (cam == null)
                cam = UnityEngine.Object.FindObjectOfType<Camera>();

            if (cam == null)
                return false;

            pos = cam.transform.position;
            rot = cam.transform.rotation;
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryReadLocalPlayerControllerPose(out Vector3 pos, out Quaternion rot)
    {
        pos = default;
        rot = default;

        var controller = TryFindObjectOfTypeByName("sCharacterController");
        if (controller is Component comp)
        {
            try
            {
                pos = comp.transform.position;
                rot = comp.transform.rotation;
                return true;
            }
            catch
            {
                return false;
            }
        }

        return TryReadLocalPlayerPose(out pos, out rot);
    }

    internal static bool TryApplyLocalPlayerControllerPose(Vector3 pos, Quaternion rot)
    {
        var controller = TryFindObjectOfTypeByName("sCharacterController");
        if (controller is not Component comp)
            return false;

        try
        {
            comp.transform.position = pos;
            comp.transform.rotation = rot;
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryReadSaveId(out string saveId)
    {
        saveId = string.Empty;
        try
        {
            var inst = TryGetSaveSystemInstance();
            if (inst == null)
                return false;

            var t = inst.GetType();

            // Common field/property names in save managers.
            var names = new[] { "saveSlot", "currentSlot", "slot", "saveName", "profileName", "currentProfile", "fileName", "saveFile" };
            for (var i = 0; i < names.Length; i++)
            {
                var n = names[i];
                var f = t.GetField(n, Any);
                if (f != null)
                {
                    var v = f.GetValue(inst);
                    if (v != null)
                    {
                        saveId = v.ToString() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(saveId))
                            return true;
                    }
                }

                var p = t.GetProperty(n, Any);
                if (p != null)
                {
                    var v = p.GetValue(inst, null);
                    if (v != null)
                    {
                        saveId = v.ToString() ?? string.Empty;
                        if (!string.IsNullOrWhiteSpace(saveId))
                            return true;
                    }
                }
            }

            // Fallback: inspect save data dictionary for likely meta keys.
            if (TryReadSaveSystemSnapshot() is { Count: > 0 } snap)
            {
                foreach (var kv in snap)
                {
                    var k = kv.Key;
                    if (string.IsNullOrEmpty(k))
                        continue;

                    var low = k.ToLowerInvariant();
                    if (low.Contains("saveslot") || low.EndsWith("slot") || low.Contains("savename") || low.Contains("profilename") || low.Contains("savefile"))
                    {
                        var v = kv.Value;
                        if (!string.IsNullOrWhiteSpace(v))
                        {
                            saveId = v;
                            return true;
                        }
                    }
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    internal static bool TryReadCurrentMapBuildIndex(out int buildIndex)
    {
        buildIndex = -1;
        try
        {
            var snap = TryReadSaveSystemSnapshot();
            if (snap == null || snap.Count == 0)
                return false;

            var preferredKeys = new[]
            {
                "deliveryCurrentLastMapBuildIndex",
                "deliveryCurrentMapBuildIndex",
                "currentMapBuildIndex",
                "mapBuildIndex",
                "lastMapBuildIndex",
            };

            for (var i = 0; i < preferredKeys.Length; i++)
            {
                if (!snap.TryGetValue(preferredKeys[i], out var raw) || string.IsNullOrWhiteSpace(raw))
                    continue;

                if (TryParseIntLoose(raw, out buildIndex) && buildIndex >= 0)
                    return true;
            }

            foreach (var kv in snap)
            {
                var key = kv.Key;
                var raw = kv.Value;
                if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(raw))
                    continue;

                var low = key.ToLowerInvariant();
                if (!low.Contains("map") || !low.Contains("index"))
                    continue;

                if (TryParseIntLoose(raw, out buildIndex) && buildIndex >= 0)
                    return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool TryParseIntLoose(string raw, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        if (int.TryParse(raw, out value))
            return true;

        if (float.TryParse(raw, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f))
        {
            value = Mathf.RoundToInt(f);
            return true;
        }

        return false;
    }

    internal static bool IsGameplayWorldLoaded()
    {
        // Menu scenes typically still have a camera, and sometimes even character prefabs.
        // Use objects that should only exist in the actual world/gameplay.
        try
        {
            if (TryFindObjectOfTypeByName("jobBoard") != null)
                return true;
            if (TryFindObjectOfTypeByName("sCarController") != null)
                return true;
            if (TryFindObjectOfTypeByName("sDeliveryManager") != null)
                return true;
            if (TryFindObjectOfTypeByName("sCityManager") != null)
                return true;
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static bool TryGetCombinedRendererBounds(Transform root, out Bounds bounds)
    {
        bounds = default;
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

                // Skip our own ghost objects if they happen to be parented under player hierarchy.
                var n = r.gameObject.name;
                if (!string.IsNullOrEmpty(n) && n.StartsWith("EasyDeliveryCoLanCoop.", StringComparison.Ordinal))
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

    internal static bool TryFindLocalPlayerVisualRoot(out Transform visualRoot)
    {
        visualRoot = null!;

        // Primary: locate the controller component by type.
        var controller = TryFindObjectOfTypeByName("sCharacterController");
        if (controller is Component comp)
        {
            var best = FindBestVisualRoot(comp.transform);
            if (best != null)
            {
                visualRoot = best;
                return true;
            }
        }

        // Fallback: some builds/mods may move renderers out from under the controller transform,
        // but the player object is still named predictably (seen in CustomTruckShop logs: "guy").
        try
        {
            var go = GameObject.Find("guy")
                     ?? GameObject.Find("Guy")
                     ?? GameObject.Find("player")
                     ?? GameObject.Find("Player");
            if (go != null)
            {
                var best = FindBestVisualRoot(go.transform);
                if (best != null)
                {
                    visualRoot = best;
                    return true;
                }
            }
        }
        catch
        {
            // ignore
        }

        if (!_loggedPlayerVisualRootFail)
        {
            _loggedPlayerVisualRootFail = true;
            Plugin.Log.LogWarning("TryFindLocalPlayerVisualRoot failed; remote player may fall back to primitive visuals.");
        }

        return false;
    }

    internal static bool TryReadInCar(out bool inCar)
    {
        inCar = false;
        var car = TryFindObjectOfTypeByName("sCarController");
        if (car == null)
            return false;

        // GuyActive indicates character inside car.
        var prop = car.GetType().GetProperty("GuyActive", Any);
        if (prop != null && prop.PropertyType == typeof(bool))
        {
            try
            {
                inCar = (bool)prop.GetValue(car, null)!;
                return true;
            }
            catch { return false; }
        }

        var field = car.GetType().GetField("guyActive", Any);
        if (field != null && field.FieldType == typeof(bool))
        {
            try
            {
                inCar = (bool)field.GetValue(car)!;
                return true;
            }
            catch { return false; }
        }

        return false;
    }

    internal static bool TryReadCarState(out Vector3 pos, out Quaternion rot, out Vector3 vel, out Vector3 angVel, out bool hasCar)
    {
        pos = default;
        rot = default;
        vel = default;
        angVel = default;
        hasCar = false;

        var car = TryFindObjectOfTypeByName("sCarController");
        if (car == null)
            return false;

        var rbField = car.GetType().GetField("rb", Any);
        if (rbField?.GetValue(car) is Rigidbody rb)
        {
            hasCar = true;
            pos = rb.position;
            rot = rb.rotation;
            vel = rb.velocity;
            angVel = rb.angularVelocity;
            return true;
        }

        // Fallback: use controller transform (better than nothing; keeps visuals in sync).
        if (car is Component comp)
        {
            hasCar = true;
            pos = comp.transform.position;
            rot = comp.transform.rotation;
            vel = Vector3.zero;
            angVel = Vector3.zero;
            return true;
        }

        return false;
    }

    internal static bool TryGetLocalCarSfxPulses(out List<(byte SfxId, string ClipName, string SourceName)> sfxEvents)
    {
        sfxEvents = new List<(byte SfxId, string ClipName, string SourceName)>(4);

        var car = TryFindObjectOfTypeByName("sCarController");
        if (car is not Component comp)
            return false;

        var hornOnlyMode = Plugin.IsHornOnlyCarSoundMode();
        var hasDirectHornPulse = false;

        if (TryReadDirectLocalHornPulse(car, out var hornPressed, out var clipName, out var sourceName))
        {
            hasDirectHornPulse = hornPressed;
            if (hornPressed)
                sfxEvents.Add((CarSfxHorn, clipName, sourceName));

            if (hornOnlyMode)
                return hornPressed;
        }

        var hasOtherPulse = TryDetectCarSfxPulsesOnRoot(comp.transform, sfxEvents);
        return hasDirectHornPulse || hasOtherPulse;
    }

    private static bool TryReadDirectLocalHornPulse(object carController, out bool hornPressed, out string clipName, out string sourceName)
    {
        hornPressed = false;
        clipName = string.Empty;
        sourceName = string.Empty;

        try
        {
            if (carController == null)
                return false;

            var carType = carController.GetType();
            var playerField = carType.GetField("player", Any);
            if (playerField == null || playerField.GetValue(carController) is not int playerIndex || playerIndex < 0)
                return false;

            var inputType = carType.Assembly.GetType("sInputManager");
            if (inputType == null)
                return false;

            var playersField = inputType.GetField("players", Any);
            if (playersField?.GetValue(null) is not Array players || playerIndex >= players.Length)
                return false;

            var inputInstance = players.GetValue(playerIndex);
            if (inputInstance == null)
                return false;

            var hornPressedField = inputInstance.GetType().GetField("hornPressed", Any);
            if (hornPressedField == null)
                return false;

            hornPressed = hornPressedField.GetValue(inputInstance) is bool isPressed && isPressed;
            if (!hornPressed)
                return true;

            sourceName = "Headlights";
            TryResolveLocalHornClipMetadata(carController, ref clipName, ref sourceName);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryResolveLocalHornClipMetadata(object carController, ref string clipName, ref string sourceName)
    {
        try
        {
            if (carController is not Component comp)
                return;

            var headlightType = carController.GetType().Assembly.GetType("Headlights");
            if (headlightType == null)
                return;

            var headlights = comp.GetComponentInChildren(headlightType, includeInactive: true);
            if (headlights == null)
                return;

            var hornField = headlightType.GetField("horn", Any);
            if (hornField?.GetValue(headlights) is AudioClip hornClip && hornClip != null && !string.IsNullOrWhiteSpace(hornClip.name))
                clipName = hornClip.name;

            var sourceField = headlightType.GetField("source", Any);
            if (sourceField?.GetValue(headlights) is AudioSource source && source != null && source.gameObject != null)
                sourceName = source.gameObject.name ?? sourceName;
        }
        catch
        {
        }
    }

    internal static bool TryPlayLikelyCarSfx(Transform root, byte sfxId, string? clipName = null, string? sourceName = null)
    {
        if (root == null)
            return false;

        try
        {
            if (sfxId == CarSfxHorn)
            {
                if (TryPlayHeadlightsHorn(root, clipName, sourceName))
                    return true;

                var requiredHornClipName = clipName ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(requiredHornClipName))
                {
                    var requiredClip = ResolveLoadedAudioClipByName(requiredHornClipName);
                    if (requiredClip != null)
                        return TryPlayClipOnRootEmitter(root, requiredClip);

                    // In horn-only mode avoid playing a wrong substitute clip.
                    return false;
                }
            }

            var sources = root.GetComponentsInChildren<AudioSource>(includeInactive: true);
            if (sources == null || sources.Length == 0)
                return false;

            var clip = clipName ?? string.Empty;
            var source = sourceName ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(clip))
            {
                for (var i = 0; i < sources.Length; i++)
                {
                    var src = sources[i];
                    if (src == null || src.clip == null)
                        continue;
                    if (string.Equals(src.clip.name, clip, StringComparison.OrdinalIgnoreCase))
                        return TryPlaySourcePulse(src);
                }
            }

            if (!string.IsNullOrWhiteSpace(source) && sfxId != CarSfxHorn)
            {
                for (var i = 0; i < sources.Length; i++)
                {
                    var src = sources[i];
                    if (src == null || src.gameObject == null)
                        continue;
                    if (src.gameObject.name.IndexOf(source, StringComparison.OrdinalIgnoreCase) >= 0)
                        return TryPlaySourcePulse(src);
                }
            }

            if (sfxId == 0)
                return false;

            if (sfxId == CarSfxHorn)
                return false;

            AudioSource? best = null;
            var bestScore = int.MinValue;
            for (var i = 0; i < sources.Length; i++)
            {
                var src = sources[i];
                if (src == null)
                    continue;

                var score = ScoreLikelyCarSfxSource(src, sfxId);
                if (score > bestScore)
                {
                    best = src;
                    bestScore = score;
                }
            }

            if (best == null || bestScore <= 0)
                return false;

            return TryPlaySourcePulse(best);
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryPlayCarSfxAudibleFallback(Transform root, byte sfxId, string? clipName = null, string? sourceName = null)
    {
        if (root == null)
            return false;

        try
        {
            if (sfxId == CarSfxHorn)
            {
                if (TryPlayHeadlightsHorn(root, clipName, sourceName))
                    return true;

                var requiredHornClipName = clipName ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(requiredHornClipName))
                {
                    var requiredClip = ResolveLoadedAudioClipByName(requiredHornClipName);
                    if (requiredClip != null)
                        return TryPlayClipOnRootEmitter(root, requiredClip);

                    return false;
                }
            }

            var sources = root.GetComponentsInChildren<AudioSource>(includeInactive: true);
            if (sources == null || sources.Length == 0)
                return false;

            var clip = ResolveBestClipForSfx(sources, sfxId, clipName, sourceName);
            if (clip == null)
                return false;

            var emitter = root.GetComponent<AudioSource>();
            if (emitter == null)
                emitter = root.gameObject.AddComponent<AudioSource>();

            emitter.playOnAwake = false;
            emitter.loop = false;
            emitter.spatialBlend = 1f;
            emitter.volume = 0.95f;
            emitter.pitch = 1f;
            emitter.minDistance = 3f;
            emitter.maxDistance = 22f;

            emitter.Stop();
            emitter.clip = clip;
            emitter.PlayOneShot(clip);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static AudioClip? ResolveBestClipForSfx(AudioSource[] sources, byte sfxId, string? clipName, string? sourceName)
    {
        if (sources == null || sources.Length == 0)
            return null;

        var clip = clipName ?? string.Empty;
        var srcName = sourceName ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(clip))
        {
            for (var i = 0; i < sources.Length; i++)
            {
                var src = sources[i];
                if (src?.clip == null)
                    continue;
                if (string.Equals(src.clip.name, clip, StringComparison.OrdinalIgnoreCase))
                    return src.clip;
            }
        }

        if (!string.IsNullOrWhiteSpace(srcName))
        {
            for (var i = 0; i < sources.Length; i++)
            {
                var src = sources[i];
                if (src?.clip == null || src.gameObject == null)
                    continue;
                if (src.gameObject.name.IndexOf(srcName, StringComparison.OrdinalIgnoreCase) >= 0)
                    return src.clip;
            }
        }

        AudioClip? best = null;
        var bestScore = int.MinValue;
        for (var i = 0; i < sources.Length; i++)
        {
            var src = sources[i];
            if (src?.clip == null)
                continue;

            var score = ScoreLikelyCarSfxSource(src, sfxId);

            // For horn fallback prefer short one-shots over loops.
            if (sfxId == CarSfxHorn)
            {
                if (!src.loop)
                    score += 25;
                if (src.clip.length > 0.01f && src.clip.length <= 2.5f)
                    score += 20;
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = src.clip;
            }
        }

        return best;
    }

    private static bool TryPlaySourcePulse(AudioSource src)
    {
        if (src == null || src.clip == null)
            return false;

        if (src.isPlaying)
            src.Stop();
        src.Play();
        return true;
    }

    private static bool TryPlayClipOnRootEmitter(Transform root, AudioClip clip)
    {
        if (root == null || clip == null)
            return false;

        try
        {
            var emitter = root.GetComponent<AudioSource>();
            if (emitter == null)
                emitter = root.gameObject.AddComponent<AudioSource>();

            emitter.playOnAwake = false;
            emitter.loop = false;
            emitter.spatialBlend = 1f;
            emitter.volume = 0.95f;
            emitter.pitch = 1f;
            emitter.minDistance = 3f;
            emitter.maxDistance = 22f;

            emitter.PlayOneShot(clip);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryPlayHeadlightsHorn(Transform root, string? preferredClipName = null, string? preferredSourceName = null)
    {
        if (root == null)
            return false;

        try
        {
            var headlightType = typeof(GameAccess).Assembly.GetType("Headlights");
            if (headlightType == null)
                return false;

            var headlights = root.GetComponentInChildren(headlightType, includeInactive: true);
            if (headlights == null)
                return false;

            var hornField = headlightType.GetField("horn", Any);
            var sourceField = headlightType.GetField("source", Any);
            if (sourceField?.GetValue(headlights) is not AudioSource source || source == null)
                return false;

            AudioClip? clipToPlay = null;
            var targetClipName = preferredClipName ?? string.Empty;
            var targetSourceName = preferredSourceName ?? string.Empty;

            if (!string.IsNullOrWhiteSpace(targetClipName))
                clipToPlay = ResolveLoadedAudioClipByName(targetClipName);

            var requireExactPreferredClip = !string.IsNullOrWhiteSpace(targetClipName);

            if (clipToPlay == null && hornField?.GetValue(headlights) is AudioClip hornClip && hornClip != null)
                clipToPlay = hornClip;

            if (!string.IsNullOrWhiteSpace(targetClipName))
            {
                if (clipToPlay == null || !string.Equals(clipToPlay.name, targetClipName, StringComparison.OrdinalIgnoreCase))
                {
                    var allSources = root.GetComponentsInChildren<AudioSource>(includeInactive: true);
                    for (var i = 0; i < allSources.Length; i++)
                    {
                        var src = allSources[i];
                        if (src?.clip == null)
                            continue;
                        if (string.Equals(src.clip.name, targetClipName, StringComparison.OrdinalIgnoreCase))
                        {
                            clipToPlay = src.clip;
                            break;
                        }
                    }
                }
            }

            if (clipToPlay == null && !string.IsNullOrWhiteSpace(targetSourceName))
            {
                var allSources = root.GetComponentsInChildren<AudioSource>(includeInactive: true);
                for (var i = 0; i < allSources.Length; i++)
                {
                    var src = allSources[i];
                    if (src?.clip == null || src.gameObject == null)
                        continue;
                    if (src.gameObject.name.IndexOf(targetSourceName, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        clipToPlay = src.clip;
                        break;
                    }
                }
            }

            if (requireExactPreferredClip)
            {
                if (clipToPlay == null)
                    return false;
                if (!string.Equals(clipToPlay.name, targetClipName, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            if (clipToPlay == null)
                return false;

            source.PlayOneShot(clipToPlay);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static AudioClip? ResolveLoadedAudioClipByName(string clipName)
    {
        if (string.IsNullOrWhiteSpace(clipName))
            return null;

        if (LoadedAudioClipByName.TryGetValue(clipName, out var cached) && cached != null)
            return cached;

        try
        {
            var allClips = Resources.FindObjectsOfTypeAll<AudioClip>();
            for (var i = 0; i < allClips.Length; i++)
            {
                var clip = allClips[i];
                if (clip == null || string.IsNullOrWhiteSpace(clip.name))
                    continue;

                if (string.Equals(clip.name, clipName, StringComparison.OrdinalIgnoreCase))
                {
                    LoadedAudioClipByName[clipName] = clip;
                    return clip;
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static bool TryDetectCarSfxPulsesOnRoot(Transform root, List<(byte SfxId, string ClipName, string SourceName)> sfxEvents)
    {
        if (root == null)
            return false;

        try
        {
            var pulse = false;
            var eventSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var alive = new HashSet<int>();
            var sources = root.GetComponentsInChildren<AudioSource>(includeInactive: true);
            var now = Time.unscaledTime;

            for (var i = 0; i < sources.Length; i++)
            {
                var src = sources[i];
                if (src == null)
                    continue;

                var clipName = src.clip != null ? src.clip.name ?? string.Empty : string.Empty;
                var sourceName = src.gameObject != null ? src.gameObject.name ?? string.Empty : string.Empty;

                var sfxId = ClassifyCarSfxSource(src);
                if (sfxId == 0 && Plugin.IsHornOnlyCarSoundMode() && IsLikelyHornFallbackPulse(src, clipName, sourceName))
                    sfxId = CarSfxHorn;

                var id = src.GetInstanceID();
                alive.Add(id);

                var isPlaying = src.isPlaying;
                var wasPlaying = LocalCarSfxPlayingBySource.TryGetValue(id, out var prev) && prev;
                var loopRefresh = false;
                if (isPlaying && src.loop)
                {
                    if (!LocalCarSfxNextLoopEmitAt.TryGetValue(id, out var nextLoopAt) || now >= nextLoopAt)
                        loopRefresh = true;
                }

                if (isPlaying && (!wasPlaying || loopRefresh))
                {
                    // If classification failed, still sync by clip/source identifiers.
                    var effectiveSfxId = sfxId;

                    if (effectiveSfxId == 0)
                    {
                        LocalCarSfxPlayingBySource[id] = isPlaying;
                        continue;
                    }

                    pulse = true;

                    if (src.loop)
                        LocalCarSfxNextLoopEmitAt[id] = now + 1.25f;

                    var dedupeKey = $"{effectiveSfxId}|{clipName}|{sourceName}";
                    if (eventSet.Add(dedupeKey))
                        sfxEvents.Add((effectiveSfxId, clipName, sourceName));
                }

                LocalCarSfxPlayingBySource[id] = isPlaying;
            }

            if (LocalCarSfxPlayingBySource.Count > 0)
            {
                var stale = new List<int>();
                foreach (var kv in LocalCarSfxPlayingBySource)
                {
                    if (!alive.Contains(kv.Key))
                        stale.Add(kv.Key);
                }

                for (var i = 0; i < stale.Count; i++)
                    LocalCarSfxPlayingBySource.Remove(stale[i]);

                for (var i = 0; i < stale.Count; i++)
                    LocalCarSfxNextLoopEmitAt.Remove(stale[i]);
            }

            return pulse;
        }
        catch
        {
            return false;
        }
    }

    private static byte ClassifyCarSfxSource(AudioSource src)
    {
        var horn = ScoreLikelyCarSfxSource(src, CarSfxHorn);
        var skid = ScoreLikelyCarSfxSource(src, CarSfxSkid);
        var crash = ScoreLikelyCarSfxSource(src, CarSfxCrash);

        var best = Mathf.Max(horn, Mathf.Max(skid, crash));
        // Require a stronger signal than generic "any clip + spatial" to avoid classifying engine loops.
        if (best < 60)
            return 0;
        if (best == horn)
            return CarSfxHorn;
        if (best == skid)
            return CarSfxSkid;
        return CarSfxCrash;
    }

    private static int ScoreLikelyCarSfxSource(AudioSource src, byte sfxId)
    {
        if (src == null)
            return 0;

        var score = 0;

        var sourceName = src.gameObject != null ? src.gameObject.name : string.Empty;
        var clipName = src.clip != null ? src.clip.name : string.Empty;

        switch (sfxId)
        {
            case CarSfxHorn:
                if (ContainsAnyKeyword(sourceName, CarHornKeywords))
                    score += 100;
                if (ContainsAnyKeyword(clipName, CarHornKeywords))
                    score += 80;
                if (src.loop)
                    score -= 30;
                break;
            case CarSfxSkid:
                if (ContainsAnyKeyword(sourceName, CarSkidKeywords))
                    score += 100;
                if (ContainsAnyKeyword(clipName, CarSkidKeywords))
                    score += 80;
                break;
            case CarSfxCrash:
                if (ContainsAnyKeyword(sourceName, CarCrashKeywords))
                    score += 100;
                if (ContainsAnyKeyword(clipName, CarCrashKeywords))
                    score += 80;
                if (src.loop)
                    score -= 40;
                break;
        }

        if (src.spatialBlend > 0.3f)
            score += 10;

        if (src.clip != null)
            score += 5;

        return score;
    }

    private static bool ContainsAnyKeyword(string? value, IReadOnlyList<string> keywords)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        for (var i = 0; i < keywords.Count; i++)
        {
            var k = keywords[i];
            if (value.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }

        return false;
    }

    private static bool IsLikelyHornFallbackPulse(AudioSource src, string clipName, string sourceName)
    {
        if (src == null)
            return false;
        if (src.loop)
            return false;

        var hasHornKeyword = ContainsAnyKeyword(clipName, CarHornKeywords) || ContainsAnyKeyword(sourceName, CarHornKeywords);
        if (hasHornKeyword)
            return true;

        if (ContainsAnyKeyword(clipName, CarSkidKeywords) || ContainsAnyKeyword(sourceName, CarSkidKeywords))
            return false;
        if (ContainsAnyKeyword(clipName, CarCrashKeywords) || ContainsAnyKeyword(sourceName, CarCrashKeywords))
            return false;
        if (ContainsAnyKeyword(clipName, CarHornNoiseKeywords) || ContainsAnyKeyword(sourceName, CarHornNoiseKeywords))
            return false;

        var clip = src.clip;
        if (clip == null)
            return false;

        var len = clip.length;
        if (len < 0.05f || len > 2.5f)
            return false;

        return src.volume >= 0.02f;
    }

    internal static bool TryReadHeldPayloadName(out string payloadName)
    {
        payloadName = string.Empty;

        var controller = TryFindObjectOfTypeByName("sCharacterController");
        if (controller is not Component comp)
            return false;

        try
        {
            // Common pattern: controller has a pickup point Transform with the held object as a child.
            // Strings seen in Assembly-CSharp: pickupPoint, heldItem, payload.
            var t = comp.GetType();

            Transform? pickupPoint = null;
            var ppField = t.GetField("pickupPoint", Any);
            if (ppField?.GetValue(comp) is Transform pp)
                pickupPoint = pp;
            else
            {
                var ppProp = t.GetProperty("pickupPoint", Any);
                if (ppProp?.GetValue(comp, null) is Transform pp2)
                    pickupPoint = pp2;
            }

            if (pickupPoint != null)
            {
                for (var i = 0; i < pickupPoint.childCount; i++)
                {
                    var child = pickupPoint.GetChild(i);
                    if (child == null)
                        continue;
                    var name = NormalizePayloadName(child.name);
                    if (IsPayloadName(name))
                    {
                        payloadName = name;
                        return true;
                    }
                }
            }

            // Fallback: inspect heldItem-style fields/properties.
            if (TryGetLikelyHeldObjectName(comp, out var held))
            {
                held = NormalizePayloadName(held);
                if (IsPayloadName(held))
                {
                    payloadName = held;
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool TryGetLikelyHeldObjectName(Component controller, out string name)
    {
        name = string.Empty;
        if (controller == null)
            return false;

        try
        {
            var t = controller.GetType();

            // Direct common names first.
            var candidates = new[] { "heldItem", "held", "payload", "currentPayload" };
            for (var i = 0; i < candidates.Length; i++)
            {
                if (TryGetUnityObjectName(t, controller, candidates[i], out name))
                    return true;
            }

            // Heuristic: any field with name containing held/payload.
            var fields = t.GetFields(Any);
            for (var i = 0; i < fields.Length; i++)
            {
                var f = fields[i];
                if (f == null)
                    continue;
                var fn = f.Name;
                if (string.IsNullOrEmpty(fn))
                    continue;
                var low = fn.ToLowerInvariant();
                if (!low.Contains("held") && !low.Contains("payload"))
                    continue;

                var v = f.GetValue(controller);
                if (TryConvertUnityObjectToName(v, out name))
                    return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static bool TryGetUnityObjectName(System.Type type, object instance, string memberName, out string name)
    {
        name = string.Empty;
        try
        {
            var f = type.GetField(memberName, Any);
            if (f != null)
            {
                var v = f.GetValue(instance);
                return TryConvertUnityObjectToName(v, out name);
            }

            var p = type.GetProperty(memberName, Any);
            if (p != null)
            {
                var v = p.GetValue(instance, null);
                return TryConvertUnityObjectToName(v, out name);
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static bool TryConvertUnityObjectToName(object? v, out string name)
    {
        name = string.Empty;
        if (v == null)
            return false;

        switch (v)
        {
            case GameObject go:
                name = go.name;
                return !string.IsNullOrEmpty(name);
            case Transform tr:
                name = tr.name;
                return !string.IsNullOrEmpty(name);
            case Component c:
                name = c.gameObject != null ? c.gameObject.name : c.name;
                return !string.IsNullOrEmpty(name);
            default:
                return false;
        }
    }

    private static bool IsPayloadName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        return name.TrimStart().StartsWith("PAYLOAD", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePayloadName(string? name)
    {
        name ??= string.Empty;
        name = name.Trim();
        if (name.EndsWith("(Clone)", StringComparison.Ordinal))
            name = name.Substring(0, name.Length - "(Clone)".Length).Trim();
        return name;
    }

    internal static bool TryFindPayloadVisualRoot(string payloadName, out Transform visualRoot)
    {
        visualRoot = null!;
        payloadName = NormalizePayloadName(payloadName);
        if (string.IsNullOrWhiteSpace(payloadName))
            return false;

        try
        {
            var all = Resources.FindObjectsOfTypeAll<GameObject>();
            Transform? best = null;
            var bestScore = int.MinValue;

            for (var i = 0; i < all.Length; i++)
            {
                var go = all[i];
                if (go == null)
                    continue;

                var n = NormalizePayloadName(go.name);
                if (!n.StartsWith("PAYLOAD", StringComparison.OrdinalIgnoreCase))
                    continue;

                var score = 0;
                if (string.Equals(n, payloadName, StringComparison.OrdinalIgnoreCase))
                    score += 200;
                else if (n.IndexOf(payloadName, StringComparison.OrdinalIgnoreCase) >= 0 || payloadName.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)
                    score += 80;

                if (go.GetComponentsInChildren<Renderer>(includeInactive: true).Length > 0)
                    score += 20;
                if (go.GetComponentsInChildren<Collider>(includeInactive: true).Length > 0)
                    score += 20;
                if (go.activeInHierarchy)
                    score += 5;

                if (score <= bestScore)
                    continue;

                best = go.transform;
                bestScore = score;
            }

            if (best != null)
            {
                visualRoot = best;
                return true;
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    internal static bool TryFindLocalCarVisualRoot(out Transform visualRoot)
    {
        visualRoot = null!;
        var car = TryFindObjectOfTypeByName("sCarController");
        if (car is not Component comp)
            return false;

        // For remote-car visuals we want the whole car hierarchy (body + wheels).
        // Filtering of collider/debug renderers is handled in the cloning code.
        visualRoot = comp.transform;
        return true;
    }

    private static Component? TryFindComponentByTypeName(Component root, string typeName)
    {
        try
        {
            var comps = root.GetComponentsInChildren<Component>(includeInactive: true);
            for (var i = 0; i < comps.Length; i++)
            {
                var c = comps[i];
                if (c == null)
                    continue;
                if (string.Equals(c.GetType().Name, typeName, StringComparison.Ordinal))
                    return c;
                if (string.Equals(c.GetType().FullName, typeName, StringComparison.Ordinal))
                    return c;
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static Transform LiftToReasonableScale(Transform candidate, Transform limitRoot)
    {
        // Heuristic: if lossyScale is too tiny/huge, climb up until it isn't,
        // or until we reach the provided limit root.
        var t = candidate;
        for (var i = 0; i < 12; i++)
        {
            var s = t.lossyScale;
            var mag = (s.x + s.y + s.z) / 3f;
            if (mag >= 0.35f && mag <= 3.0f)
                return t;

            if (t.parent == null)
                return t;
            if (t == limitRoot)
                return t;

            t = t.parent;
        }

        return t;
    }

    internal static bool TryApplyCarState(Vector3 pos, Quaternion rot, Vector3 vel, Vector3 angVel)
    {
        var car = TryFindObjectOfTypeByName("sCarController");
        if (car == null)
            return false;

        var rbField = car.GetType().GetField("rb", Any);
        if (rbField?.GetValue(car) is Rigidbody rb)
        {
            try
            {
                rb.position = pos;
                rb.rotation = rot;
                rb.velocity = vel;
                rb.angularVelocity = angVel;
            }
            catch
            {
                // ignore
            }
        }

        // Always update the transform so the visible car moves even if RB wiring differs.
        if (car is Component comp)
        {
            comp.transform.position = pos;
            comp.transform.rotation = rot;
        }

        return true;
    }

    internal static IReadOnlyList<(string Name, Vector3 LocalPos, Quaternion LocalRot)>? TryReadCarCargo()
    {
        var car = TryFindObjectOfTypeByName("sCarController");
        if (car == null)
            return null;

        var pivotField = car.GetType().GetField("payloadPivot", Any);
        if (pivotField?.GetValue(car) is not Transform pivot)
            return null;

        var list = new List<(string, Vector3, Quaternion)>();
        for (var i = 0; i < pivot.childCount; i++)
        {
            var child = pivot.GetChild(i);
            list.Add((child.name, child.localPosition, child.localRotation));
        }
        return list;
    }

    internal static bool TryApplyCarCargo(IReadOnlyList<(string Name, Vector3 LocalPos, Quaternion LocalRot)> cargo)
    {
        var car = TryFindObjectOfTypeByName("sCarController");
        if (car == null)
            return false;

        var pivotField = car.GetType().GetField("payloadPivot", Any);
        if (pivotField?.GetValue(car) is not Transform pivot)
            return false;

        // Best-effort apply: match by name, fallback by index.
        var byName = new Dictionary<string, Transform>(StringComparer.Ordinal);
        for (var i = 0; i < pivot.childCount; i++)
            byName[pivot.GetChild(i).name] = pivot.GetChild(i);

        for (var i = 0; i < cargo.Count; i++)
        {
            Transform? child = null;
            if (!string.IsNullOrEmpty(cargo[i].Name))
                byName.TryGetValue(cargo[i].Name, out child);
            child ??= i < pivot.childCount ? pivot.GetChild(i) : null;
            if (child == null)
                continue;

            child.localPosition = cargo[i].LocalPos;
            child.localRotation = cargo[i].LocalRot;
        }

        return true;
    }

    internal static IReadOnlyList<JobData>? TryReadJobBoardJobs()
    {
        var board = TryFindObjectOfTypeByName("jobBoard");
        if (board == null)
            return null;

        var jobsField = board.GetType().GetField("jobs", Any);
        var jobsObj = jobsField?.GetValue(board);
        if (jobsObj is not IEnumerable enumerable)
            return null;

        var list = new List<JobData>();
        foreach (var job in enumerable)
        {
            if (job == null)
                continue;
            var t = job.GetType();

            var jd = new JobData
            {
                Name = t.GetField("name", Any)?.GetValue(job)?.ToString(),
                From = t.GetField("from", Any)?.GetValue(job)?.ToString(),
                To = t.GetField("to", Any)?.GetValue(job)?.ToString(),
                StartingCityName = t.GetField("startingCityName", Any)?.GetValue(job)?.ToString(),
                DestCityName = t.GetField("destCityName", Any)?.GetValue(job)?.ToString(),
                DestinationIndex = ReadInt(t.GetField("destinationIndex", Any)?.GetValue(job)),
                PayloadIndex = ReadInt(t.GetField("payloadIndex", Any)?.GetValue(job)),
                Price = ReadFloat(t.GetField("price", Any)?.GetValue(job)),
                Mass = ReadFloat(t.GetField("mass", Any)?.GetValue(job)),
                TimeStart = ReadFloat(t.GetField("timeStart", Any)?.GetValue(job)),
                IsChallenge = ReadBool(t.GetField("isChallenge", Any)?.GetValue(job)),
                IsIntercity = ReadBool(t.GetField("isIntercity", Any)?.GetValue(job)),
                Duration = ReadFloat(t.GetField("duration", Any)?.GetValue(job)),
                Distance = ReadFloat(t.GetField("distance", Any)?.GetValue(job)),
            };
            list.Add(jd);
        }

        return list;
    }

    internal static bool TryApplyJobBoardJobs(IReadOnlyList<JobData> jobs)
    {
        if (_jobApplyDisabled)
            return false;

        var board = TryFindObjectOfTypeByName("jobBoard");
        if (board == null)
            return false;

        // In this game, Job is a nested type: jobBoard+Job
        var boardType = board.GetType();
        var jobType = boardType.GetNestedType("Job", Any)
                      ?? FindTypeInAssemblyCSharp("jobBoard+Job")
                      ?? FindTypeInAssemblyCSharp("Job");
        if (jobType == null)
            return false;

        var listType = typeof(List<>).MakeGenericType(jobType);
        var newList = (IList)Activator.CreateInstance(listType)!;

        try
        {
            foreach (var j in jobs)
            {
                object? job;

                // Prefer parameterless ctor if it exists (public or non-public).
                var ctor = jobType.GetConstructor(Any, binder: null, Type.EmptyTypes, modifiers: null);
                if (ctor != null)
                {
                    job = ctor.Invoke(Array.Empty<object>());
                }
                else
                {
                    // Fallback: allocate without running ctor (best-effort).
                    job = FormatterServices.GetUninitializedObject(jobType);
                }

                if (job == null)
                    continue;

            SetField(jobType, job, "name", j.Name);
            SetField(jobType, job, "from", j.From);
            SetField(jobType, job, "to", j.To);
            SetField(jobType, job, "startingCityName", j.StartingCityName);
            SetField(jobType, job, "destCityName", j.DestCityName);
            SetField(jobType, job, "destinationIndex", j.DestinationIndex);
            SetField(jobType, job, "payloadIndex", j.PayloadIndex);
            SetField(jobType, job, "price", j.Price);
            SetField(jobType, job, "mass", j.Mass);
            SetField(jobType, job, "timeStart", j.TimeStart);
            SetField(jobType, job, "isChallenge", j.IsChallenge);
            SetField(jobType, job, "isIntercity", j.IsIntercity);
            SetField(jobType, job, "duration", j.Duration);
            SetField(jobType, job, "distance", j.Distance);

                newList.Add(job);
            }
        }
        catch (Exception ex)
        {
            _jobApplyDisabled = true;
            _jobApplyDisabledReason = ex.GetType().Name;
            Plugin.Log.LogWarning($"Job sync disabled (cannot construct {jobType.FullName}). Reason={_jobApplyDisabledReason}");
            return false;
        }

        var jobsField = boardType.GetField("jobs", Any);
        if (jobsField == null)
            return false;

        try
        {
            jobsField.SetValue(board, newList);

            // Try to refresh UI/state if method exists
            board.GetType().GetMethod("UpdateJobCount", Any)?.Invoke(board, Array.Empty<object>());
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static Dictionary<string, string?> TryReadSaveSystemSnapshot()
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);

        var saveSystem = TryGetSaveSystemInstance();
        if (saveSystem == null)
            return result;

        // sSaveSystem has field: data
        var dataField = saveSystem.GetType().GetField("data", Any);
        var dataObj = dataField?.GetValue(saveSystem);

        if (dataObj is IDictionary dict)
        {
            foreach (DictionaryEntry entry in dict)
            {
                if (entry.Key is string k)
                {
                    result[k] = entry.Value?.ToString();
                }
            }
        }

        return result;
    }

    internal static bool TryApplySaveKey(string key, string value)
    {
        var saveSystem = TryGetSaveSystemInstance();
        if (saveSystem == null)
            return false;

        // Use SetString as the common denominator; game already stores parsed values too.
        var setString = saveSystem.GetType().GetMethod("SetString", Any);
        if (setString == null)
            return false;

        try
        {
            setString.Invoke(saveSystem, new object?[] { key, value });
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryDeleteSaveKey(string key)
    {
        var saveSystem = TryGetSaveSystemInstance();
        if (saveSystem == null)
            return false;

        var deleteKey = saveSystem.GetType().GetMethod("DeleteKey", Any);
        if (deleteKey == null)
            return false;

        try
        {
            deleteKey.Invoke(saveSystem, new object?[] { key });
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryReadDayNightTime(out float time)
    {
        time = 0f;

        var cycle = TryFindObjectOfTypeByName("sDayNightCycle");
        if (cycle == null)
            return false;

        var timeField = cycle.GetType().GetField("time", Any);
        if (timeField == null)
            return false;

        var value = timeField.GetValue(cycle);
        if (value is float f)
        {
            time = f;
            return true;
        }

        return false;
    }

    internal static bool TryApplyDayNightTime(float time)
    {
        var cycle = TryFindObjectOfTypeByName("sDayNightCycle");
        if (cycle == null)
            return false;

        var timeField = cycle.GetType().GetField("time", Any);
        if (timeField == null)
            return false;

        try
        {
            timeField.SetValue(cycle, time);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryReadHudMoney(out int money)
    {
        money = 0;

        var hud = TryFindObjectOfTypeByName("sHUD");
        if (hud == null)
            return false;

        var field = hud.GetType().GetField("money", Any);
        if (field == null)
            return false;

        var value = field.GetValue(hud);
        if (value is int i)
        {
            money = i;
            return true;
        }

        // Some games store as float
        if (value is float f)
        {
            money = Mathf.RoundToInt(f);
            return true;
        }

        return false;
    }

    internal static bool TryApplyHudMoney(int money)
    {
        var hud = TryFindObjectOfTypeByName("sHUD");
        if (hud == null)
            return false;

        var field = hud.GetType().GetField("money", Any);
        if (field == null)
            return false;

        try
        {
            if (field.FieldType == typeof(int))
            {
                field.SetValue(hud, money);
                return true;
            }

            if (field.FieldType == typeof(float))
            {
                field.SetValue(hud, (float)money);
                return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static object? TryGetSaveSystemInstance()
    {
        // sSaveSystem has field: instance
        var type = FindTypeInAssemblyCSharp("sSaveSystem");
        if (type == null)
            return null;

        var instanceField = type.GetField("instance", Any);
        return instanceField?.GetValue(null);
    }

    private static object? TryFindObjectOfTypeByName(string typeName)
    {
        var type = FindTypeInAssemblyCSharp(typeName);
        if (type == null)
            return null;

        try
        {
            // UnityEngine.Object.FindObjectOfType(Type)
            var find = typeof(UnityEngine.Object).GetMethod("FindObjectOfType", Any, null, new[] { typeof(Type) }, null);
            if (find == null)
                return null;

            return find.Invoke(null, new object?[] { type });
        }
        catch
        {
            return null;
        }
    }

    internal static bool TryApplyPlayerPrefString(string key, string value)
    {
        try
        {
            if (string.IsNullOrEmpty(key))
                return false;
            UnityEngine.PlayerPrefs.SetString(key, value ?? string.Empty);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryDeletePlayerPref(string key)
    {
        try
        {
            if (string.IsNullOrEmpty(key))
                return false;
            UnityEngine.PlayerPrefs.DeleteKey(key);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static void TrySavePlayerPrefs()
    {
        try { UnityEngine.PlayerPrefs.Save(); } catch { /* ignore */ }
    }

    internal static bool TryAutoEnterWorldFromMenu(string? preferredSaveId, out string reason)
    {
        reason = string.Empty;
        preferredSaveId ??= string.Empty;

        // Derive a more useful save-slot hint from the host-provided saveId.
        // The host often sends a path-like id ending in "file2.txt"; menu APIs typically expect "file2" or index 2.
        var slotNameHint = TryExtractSaveSlotName(preferredSaveId) ?? preferredSaveId;
        var slotIndexHint = TryExtractSaveSlotIndex(preferredSaveId);

        // Prefer clicking real UI controls first (least invasive / safest).
        // Unity 6 games often use UI Toolkit, but some screens still use UGUI.
        if (TryClickUiToolkitButton(out var uiTkReason))
        {
            reason = uiTkReason;
            return true;
        }

        if (TryClickMenuButton(out var btnReason))
        {
            reason = btnReason;
            return true;
        }

        // Next: try to invoke common menu/loader methods, but ONLY parameterless ones.
        // Calling methods with indices (e.g., ScreenSystem.Resume(int)) has proven unsafe in this build.
        var typeNames = new[]
        {
            "sMainMenu",
            "sMenu",
            "sMenuManager",
            "sSaveMenu",
            "sSaveSelect",
            "sSaveSelectMenu",
            "sSceneLoader",
            "sGameManager",
        };

        var methodNames = new[]
        {
            "Continue",
            "ContinueGame",
            "OnContinue",
            "OnContinueButton",
            "Play",
            "OnPlay",
            "OnPlayButton",
            "StartGame",
            "Load",
            "LoadGame",
            "LoadSave",
            "LoadFromSave",
        };

        for (var ti = 0; ti < typeNames.Length; ti++)
        {
            var obj = TryFindObjectOfTypeByName(typeNames[ti]);
            if (obj == null)
                continue;

            var t = obj.GetType();
            var methods = t.GetMethods(Any);
            for (var mi = 0; mi < methodNames.Length; mi++)
            {
                var name = methodNames[mi];
                for (var k = 0; k < methods.Length; k++)
                {
                    var m = methods[k];
                    if (m == null)
                        continue;
                    if (!string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))
                        continue;

                    try
                    {
                        if (m.GetParameters().Length != 0)
                            continue;

                        m.Invoke(obj, null);
                        reason = $"Invoked {t.Name}.{m.Name}()";
                        return true;
                    }
                    catch
                    {
                        // try next overload
                    }
                }
            }
        }

        // Fallback 2: broader scan over MonoBehaviours and invoke likely menu handlers (restricted).
        if (TryInvokeLikelyMenuHandler(preferredSaveId, out var scanReason))
        {
            reason = scanReason;
            return true;
        }

        // Fallback 3: last resort - substring heuristic (continue/load/play/start) on active UI-ish behaviours (restricted).
        if (TryInvokeSubstringHeuristic(slotNameHint, slotIndexHint, out var heurReason))
        {
            reason = heurReason;
            return true;
        }

        reason = $"No known menu/loader method found. UGUI={btnReason}. UITK={uiTkReason}. Scan={scanReason}";
        return false;
    }

    internal static string NormalizeSaveIdForSyncCompare(string? saveId)
    {
        saveId ??= string.Empty;
        saveId = saveId.Trim();
        if (saveId.Length == 0)
            return string.Empty;

        var slotName = TryExtractSaveSlotName(saveId);
        if (!string.IsNullOrWhiteSpace(slotName))
            return slotName.Trim().ToLowerInvariant();

        var fallback = Plugin.SanitizeFileName(saveId);
        return fallback.Trim().ToLowerInvariant();
    }

    private static int? TryExtractSaveSlotIndex(string preferredSaveId)
    {
        if (string.IsNullOrEmpty(preferredSaveId))
            return null;

        try
        {
            // Examples: "...file2.txt", "file1", "FILE3".
            var m = System.Text.RegularExpressions.Regex.Match(preferredSaveId, "file\\s*(\\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success && m.Groups.Count > 1 && int.TryParse(m.Groups[1].Value, out var idx))
                return idx;
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static string? TryExtractSaveSlotName(string preferredSaveId)
    {
        var idx = TryExtractSaveSlotIndex(preferredSaveId);
        if (idx == null)
            return null;
        return $"file{idx.Value}";
    }

    private static bool TryClickMenuButton(out string reason)
    {
        reason = string.Empty;

        Type? buttonType = null;
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                buttonType = asm.GetTypes().FirstOrDefault(t => string.Equals(t.FullName, "UnityEngine.UI.Button", StringComparison.Ordinal));
                if (buttonType != null)
                    break;
            }
            catch
            {
                // ignore
            }
        }

        if (buttonType == null)
        {
            reason = "UnityEngine.UI.Button type not found";
            return false;
        }

        var buttons = TryResourcesFindObjectsOfTypeAll(buttonType);
        if (buttons == null || buttons.Length == 0)
        {
            reason = "No UI buttons found";
            return false;
        }

        (UnityEngine.Object Obj, Component Comp, string Label, int Score)? best = null;

        for (var i = 0; i < buttons.Length; i++)
        {
            var o = buttons[i];
            if (o == null)
                continue;
            if (o is not Component c)
                continue;

            var label = string.Empty;
            try { label = TryGetButtonLabelText(c) ?? string.Empty; } catch { /* ignore */ }

            var hay = ((c.gameObject != null ? c.gameObject.name : string.Empty) + " " + label).ToLowerInvariant();
            if (hay.Length == 0)
                continue;

            var score = ScoreMenuAction(hay);
            if (score <= 0)
                continue;

            if (best == null || score > best.Value.Score)
                best = (o, c, label, score);
        }

        if (best != null)
        {
            // Invoke button.onClick.Invoke() via reflection.
            try
            {
                var o = best.Value.Obj;
                var c = best.Value.Comp;
                var label = best.Value.Label;

                var onClickProp = o.GetType().GetProperty("onClick", Any);
                var onClick = onClickProp?.GetValue(o);
                if (onClick != null)
                {
                    var invoke = onClick.GetType().GetMethod("Invoke", Any, null, Type.EmptyTypes, null);
                    if (invoke != null)
                    {
                        invoke.Invoke(onClick, null);
                        var goName = c.gameObject != null ? c.gameObject.name : "(null)";
                        reason = $"Clicked UI Button '{goName}' label='{label}' score={best.Value.Score}";
                        return true;
                    }
                }
            }
            catch
            {
                // ignore
            }
        }

        if (!_loggedMenuButtonDump)
        {
            _loggedMenuButtonDump = true;
            try
            {
                var samples = new List<string>();
                for (var i = 0; i < buttons.Length && samples.Count < 12; i++)
                {
                    if (buttons[i] is not Component c)
                        continue;
                    var goName = c.gameObject != null ? c.gameObject.name : string.Empty;
                    var label = string.Empty;
                    try { label = TryGetButtonLabelText(c) ?? string.Empty; } catch { /* ignore */ }
                    if (string.IsNullOrWhiteSpace(goName) && string.IsNullOrWhiteSpace(label))
                        continue;
                    samples.Add($"{goName}:'{label}'");
                }

                if (samples.Count > 0)
                    reason = $"No matching UI menu button found (total={buttons.Length}). Samples: {string.Join(" | ", samples)}";
                else
                    reason = $"No matching UI menu button found (total={buttons.Length})";
            }
            catch
            {
                reason = "No matching UI menu button found";
            }
        }
        else
        {
            reason = "No matching UI menu button found";
        }
        return false;
    }

    private static int ScoreMenuAction(string hay)
    {
        if (string.IsNullOrWhiteSpace(hay))
            return 0;

        // Avoid accidentally starting a new game.
        if (hay.Contains("new") || hay.Contains("нов") || hay.Contains("с нуля") || hay.Contains("new game") || hay.Contains("новая"))
            return 0;

        if (hay.Contains("continue") || hay.Contains("продолж"))
            return 120;
        if (hay.Contains("resume"))
            return 110;
        if (hay.Contains("load") || hay.Contains("загруз"))
            return 100;
        if (hay.Contains("play") || hay.Contains("играть"))
            return 90;

        // Keep start as a low-priority fallback, and only if it looks like start game rather than new game.
        if (hay.Contains("start game") || hay.Contains("startgame"))
            return 80;
        if (hay.Contains("start") || hay.Contains("начать") || hay.Contains("старт"))
            return 60;

        return 0;
    }

    private static bool TryClickUiToolkitButton(out string reason)
    {
        reason = string.Empty;

        // Resolve UIDocument without referencing UIElements assemblies directly.
        var uiDocType = FindTypeAnyAssembly("UnityEngine.UIElements.UIDocument")
                        ?? FindTypeAnyAssembly("UnityEngine.UIElements.UIDocumentBehaviour");
        var buttonType = FindTypeAnyAssembly("UnityEngine.UIElements.Button");

        if (uiDocType == null || buttonType == null)
        {
            reason = "UI Toolkit types not found";
            return false;
        }

        var docs = TryResourcesFindObjectsOfTypeAll(uiDocType);
        if (docs == null || docs.Length == 0)
        {
            reason = "No UIDocument found";
            return false;
        }

        var keys = new[]
        {
            "continue", "resume", "play", "start game", "startgame", "load", "load game", "loadgame",
            "продолж", "играть", "начать", "старт", "загруз",
        };

        for (var di = 0; di < docs.Length; di++)
        {
            if (docs[di] is not Component docComp)
                continue;
            if (docComp == null)
                continue;

            object? root = null;
            try
            {
                var p = docComp.GetType().GetProperty("rootVisualElement", Any);
                root = p?.GetValue(docComp, null);
            }
            catch
            {
                // ignore
            }

            if (root == null)
                continue;

            if (TryFindAndClickUiToolkitButton(root, buttonType, keys, out var clickedReason))
            {
                reason = clickedReason;
                return true;
            }
        }

        if (!_loggedUiToolkitButtonDump)
        {
            _loggedUiToolkitButtonDump = true;
            try
            {
                var samples = new List<string>();
                var totalButtons = 0;
                for (var di = 0; di < docs.Length; di++)
                {
                    if (docs[di] is not Component docComp)
                        continue;
                    object? root = null;
                    try
                    {
                        var p = docComp.GetType().GetProperty("rootVisualElement", Any);
                        root = p?.GetValue(docComp, null);
                    }
                    catch { /* ignore */ }

                    if (root == null)
                        continue;

                    CollectUiToolkitButtonSamples(root, buttonType, samples, ref totalButtons, maxSamples: 14);
                    if (samples.Count >= 14)
                        break;
                }

                if (totalButtons > 0)
                    reason = $"No matching UI Toolkit button found (docs={docs.Length}, totalButtons~={totalButtons}). Samples: {string.Join(" | ", samples)}";
                else
                    reason = $"No matching UI Toolkit button found (docs={docs.Length}, totalButtons~=0)";
            }
            catch
            {
                reason = "No matching UI Toolkit button found";
            }
        }
        else
        {
            reason = "No matching UI Toolkit button found";
        }
        return false;
    }

    private static void CollectUiToolkitButtonSamples(object rootVisualElement, Type buttonType, List<string> samples, ref int totalButtons, int maxSamples)
    {
        var stack = new Stack<object>();
        stack.Push(rootVisualElement);

        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur == null)
                continue;

            var t = cur.GetType();
            if (buttonType.IsAssignableFrom(t))
            {
                totalButtons++;
                if (samples.Count < maxSamples)
                {
                    var name = TryGetStringProp(cur, "name") ?? string.Empty;
                    var text = TryGetStringProp(cur, "text") ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(name) || !string.IsNullOrWhiteSpace(text))
                        samples.Add($"{name}:'{text}'");
                }
            }

            try
            {
                var childrenM = t.GetMethod("Children", Any, null, Type.EmptyTypes, null);
                if (childrenM != null && childrenM.Invoke(cur, null) is IEnumerable enumerable)
                {
                    foreach (var ch in enumerable)
                        if (ch != null)
                            stack.Push(ch);
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    private static bool TryFindAndClickUiToolkitButton(object rootVisualElement, Type buttonType, string[] keys, out string reason)
    {
        reason = string.Empty;

        // Traverse the visual tree using reflection: VisualElement.Children()
        var stack = new Stack<object>();
        stack.Push(rootVisualElement);

        (object Button, string Name, string Text, int Score)? best = null;

        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur == null)
                continue;

            var t = cur.GetType();
            if (buttonType.IsAssignableFrom(t))
            {
                var name = TryGetStringProp(cur, "name") ?? string.Empty;
                var text = TryGetStringProp(cur, "text") ?? string.Empty;
                var hay = (name + " " + text).ToLowerInvariant();

                // Keep original keys as a coarse filter.
                var keyHit = false;
                for (var k = 0; k < keys.Length; k++)
                {
                    if (hay.Contains(keys[k].ToLowerInvariant()))
                    {
                        keyHit = true;
                        break;
                    }
                }

                if (keyHit)
                {
                    var score = ScoreMenuAction(hay);
                    if (score > 0 && (best == null || score > best.Value.Score))
                        best = (cur, name, text, score);
                }
            }

            // Enqueue children.
            try
            {
                var childrenM = t.GetMethod("Children", Any, null, Type.EmptyTypes, null);
                if (childrenM != null && childrenM.Invoke(cur, null) is IEnumerable enumerable)
                {
                    foreach (var ch in enumerable)
                        if (ch != null)
                            stack.Push(ch);
                }
            }
            catch
            {
                // ignore
            }
        }

        if (best != null && TryInvokeUiToolkitButtonClick(best.Value.Button))
        {
            reason = $"Clicked UI Toolkit Button name='{best.Value.Name}' text='{best.Value.Text}' score={best.Value.Score}";
            return true;
        }

        return false;
    }

    private static bool TryInvokeUiToolkitButtonClick(object button)
    {
        try
        {
            var t = button.GetType();

            // 1) Button.Click() (if exists)
            var click = t.GetMethod("Click", Any, null, Type.EmptyTypes, null);
            if (click != null)
            {
                click.Invoke(button, null);
                return true;
            }

            // 2) clickable.Invoke() / clickable.SimulateSingleClick(...)
            var clickableProp = t.GetProperty("clickable", Any);
            var clickable = clickableProp?.GetValue(button, null);
            if (clickable != null)
            {
                var ct = clickable.GetType();
                var invoke = ct.GetMethod("Invoke", Any, null, Type.EmptyTypes, null);
                if (invoke != null)
                {
                    invoke.Invoke(clickable, null);
                    return true;
                }

                // Some Unity versions: SimulateSingleClick(EventBase evt)
                var sim = ct.GetMethod("SimulateSingleClick", Any);
                if (sim != null)
                {
                    var ps = sim.GetParameters();
                    if (ps.Length == 0)
                    {
                        sim.Invoke(clickable, null);
                        return true;
                    }
                    if (ps.Length == 1)
                    {
                        sim.Invoke(clickable, new object?[] { null });
                        return true;
                    }
                }
            }

            // 3) Invoke backing field for 'clicked' action if present.
            var f = t.GetField("clicked", Any) ?? t.GetField("<clicked>k__BackingField", Any);
            if (f?.GetValue(button) is Delegate del)
            {
                del.DynamicInvoke();
                return true;
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static string? TryGetStringProp(object o, string prop)
    {
        try
        {
            var p = o.GetType().GetProperty(prop, Any);
            if (p?.GetValue(o, null) is string s)
                return s;
        }
        catch
        {
            // ignore
        }
        return null;
    }

    private static UnityEngine.Object[]? TryResourcesFindObjectsOfTypeAll(Type t)
    {
        try
        {
            var m = typeof(UnityEngine.Resources).GetMethod("FindObjectsOfTypeAll", Any, null, new[] { typeof(Type) }, null);
            if (m == null)
                return null;

            if (m.Invoke(null, new object?[] { t }) is not Array arr)
                return null;

            var res = new UnityEngine.Object[arr.Length];
            for (var i = 0; i < arr.Length; i++)
                res[i] = (UnityEngine.Object)arr.GetValue(i)!;
            return res;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryGetButtonLabelText(Component button)
    {
        // Look for common text components under the button.
        var textType = FindTypeAnyAssembly("UnityEngine.UI.Text") ?? FindTypeAnyAssembly("TMPro.TextMeshProUGUI");
        if (textType == null)
            return null;

        try
        {
            var comps = button.GetComponentsInChildren<Component>(includeInactive: true);
            for (var i = 0; i < comps.Length; i++)
            {
                var c = comps[i];
                if (c == null)
                    continue;
                if (!textType.IsInstanceOfType(c))
                    continue;

                var p = c.GetType().GetProperty("text", Any);
                if (p?.GetValue(c) is string s && !string.IsNullOrWhiteSpace(s))
                    return s.Trim();
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static Type? FindTypeAnyAssembly(string fullName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var t = asm.GetType(fullName, throwOnError: false);
                if (t != null)
                    return t;
            }
            catch
            {
                // ignore
            }
        }
        return null;
    }

    private static bool TryInvokeLikelyMenuHandler(string preferredSaveId, out string reason)
    {
        reason = string.Empty;

        var slotNameHint = TryExtractSaveSlotName(preferredSaveId) ?? preferredSaveId;
        var slotIndexHint = TryExtractSaveSlotIndex(preferredSaveId);

        var objs = TryResourcesFindObjectsOfTypeAll(typeof(MonoBehaviour));
        if (objs == null || objs.Length == 0)
        {
            reason = "No MonoBehaviours found";
            return false;
        }

        var methodNames = new[]
        {
            "Continue", "ContinueGame", "OnContinue", "OnContinuePressed", "OnContinueButton",
            "Play", "OnPlay", "OnPlayPressed", "OnPlayButton",
            "StartGame", "OnStart", "OnStartPressed",
            "Load", "LoadGame", "LoadSave", "LoadFromSave", "LoadLast",
        };

        for (var i = 0; i < objs.Length; i++)
        {
            if (objs[i] is not MonoBehaviour mb)
                continue;

            // Avoid known unsafe systems.
            if (string.Equals(mb.GetType().Name, "ScreenSystem", StringComparison.OrdinalIgnoreCase))
                continue;

            // Prefer active UI/menu components.
            try
            {
                if (!mb.isActiveAndEnabled)
                    continue;
                if (mb.gameObject == null)
                    continue;
                if (!mb.gameObject.activeInHierarchy)
                    continue;
                if (!mb.gameObject.scene.isLoaded)
                    continue;
            }
            catch
            {
                // ignore
            }

            var tn = mb.GetType().Name;
            var gn = mb.gameObject != null ? mb.gameObject.name : string.Empty;
            var hay = (tn + " " + gn).ToLowerInvariant();
            if (!(hay.Contains("menu") || hay.Contains("main") || hay.Contains("title") || hay.Contains("save") || hay.Contains("select") || hay.Contains("loader") || hay.Contains("scene") || hay.Contains("start") || hay.Contains("ui") || hay.Contains("canvas") || hay.Contains("front")))
                continue;

            MethodInfo[] methods;
            try { methods = mb.GetType().GetMethods(Any); }
            catch { continue; }

            for (var mi = 0; mi < methodNames.Length; mi++)
            {
                var want = methodNames[mi];
                for (var k = 0; k < methods.Length; k++)
                {
                    var m = methods[k];
                    if (m == null)
                        continue;
                    if (!string.Equals(m.Name, want, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var ps = m.GetParameters();
                    try
                    {
                        if (ps.Length == 0)
                        {
                            m.Invoke(mb, null);
                            reason = $"Invoked {mb.GetType().Name}.{m.Name}()";
                            return true;
                        }

                        if (ps.Length == 1)
                        {
                            var pt = ps[0].ParameterType;
                            // Common Unity UI handlers take BaseEventData; passing null is usually fine.
                            if (pt.FullName != null && pt.FullName.Contains("EventSystems", StringComparison.OrdinalIgnoreCase))
                            {
                                m.Invoke(mb, new object?[] { null });
                                reason = $"Invoked {mb.GetType().Name}.{m.Name}(eventData:null)";
                                return true;
                            }

                            // Some UI handlers take object/UnityEngine.Object; passing null is usually fine.
                            if (pt == typeof(object) || typeof(UnityEngine.Object).IsAssignableFrom(pt))
                            {
                                m.Invoke(mb, new object?[] { null });
                                reason = $"Invoked {mb.GetType().Name}.{m.Name}(null)";
                                return true;
                            }
                        }
                    }
                    catch
                    {
                        // keep trying other overloads
                    }
                }
            }
        }

        reason = "No likely menu handler found in scan";
        return false;
    }

    private static bool TryInvokeSubstringHeuristic(string slotNameHint, int? slotIndexHint, out string reason)
    {
        reason = string.Empty;

        var objs = TryResourcesFindObjectsOfTypeAll(typeof(MonoBehaviour));
        if (objs == null || objs.Length == 0)
        {
            reason = "No MonoBehaviours found (heuristic)";
            return false;
        }

        var keywords = new (string Key, int Score)[]
        {
            ("continue", 120),
            ("resume", 110),
            ("load", 100),
            ("playsave", 95),
            ("play", 90),
            ("startgame", 85),
        };

        (MonoBehaviour Mb, MethodInfo Method, int Score)? best = null;

        for (var i = 0; i < objs.Length; i++)
        {
            if (objs[i] is not MonoBehaviour mb)
                continue;

            // Avoid known unsafe systems.
            if (string.Equals(mb.GetType().Name, "ScreenSystem", StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                if (!mb.isActiveAndEnabled)
                    continue;
                if (mb.gameObject == null)
                    continue;
                if (!mb.gameObject.activeInHierarchy)
                    continue;
                if (!mb.gameObject.scene.isLoaded)
                    continue;
            }
            catch
            {
                continue;
            }

            var tn = mb.GetType().Name;
            var gn = mb.gameObject != null ? mb.gameObject.name : string.Empty;
            var ctx = (tn + " " + gn).ToLowerInvariant();
            if (!(ctx.Contains("menu") || ctx.Contains("ui") || ctx.Contains("canvas") || ctx.Contains("title") || ctx.Contains("front") || ctx.Contains("save") || ctx.Contains("select")))
                continue;

            MethodInfo[] methods;
            try { methods = mb.GetType().GetMethods(Any); }
            catch { continue; }

            for (var k = 0; k < methods.Length; k++)
            {
                var m = methods[k];
                if (m == null)
                    continue;
                if (m.IsSpecialName || m.IsGenericMethod)
                    continue;
                if (m.ReturnType != typeof(void))
                    continue;

                // Never invoke Unity lifecycle methods.
                var mn = m.Name;
                if (string.IsNullOrEmpty(mn))
                    continue;
                if (string.Equals(mn, "Awake", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(mn, "Start", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(mn, "Update", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(mn, "FixedUpdate", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(mn, "LateUpdate", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(mn, "OnEnable", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(mn, "OnDisable", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(mn, "OnGUI", StringComparison.OrdinalIgnoreCase))
                    continue;

                var lname = mn.ToLowerInvariant();

                var score = 0;
                for (var j = 0; j < keywords.Length; j++)
                {
                    if (lname.Contains(keywords[j].Key))
                    {
                        score = Math.Max(score, keywords[j].Score);
                        break;
                    }
                }
                if (score == 0)
                    continue;

                // Boost if component name looks very menu-like.
                if (ctx.Contains("mainmenu") || ctx.Contains("savemenu") || ctx.Contains("save") || ctx.Contains("sceneloader"))
                    score += 15;
                if (lname.StartsWith("on", StringComparison.Ordinal))
                    score += 5;

                var ps = m.GetParameters();
                if (ps.Length > 1)
                    continue;

                if (ps.Length == 1)
                {
                    var pt = ps[0].ParameterType;
                    // Restrict to UI-event-style handlers only (passing indices/slot names is risky and caused crashes).
                    var ok = pt == typeof(object) || typeof(UnityEngine.Object).IsAssignableFrom(pt);
                    if (!ok && pt.FullName != null)
                        ok = pt.FullName.Contains("EventSystems", StringComparison.OrdinalIgnoreCase);
                    if (!ok)
                        continue;
                }

                if (best == null || score > best.Value.Score)
                    best = (mb, m, score);
            }
        }

        if (best == null)
        {
            reason = "No heuristic candidate found";
            return false;
        }

        try
        {
            var mb = best.Value.Mb;
            var m = best.Value.Method;

            // Extra guard: never call ScreenSystem.Resume*.
            if (string.Equals(mb.GetType().Name, "ScreenSystem", StringComparison.OrdinalIgnoreCase) &&
                m.Name.IndexOf("resume", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                reason = "Skipped unsafe ScreenSystem.Resume heuristic";
                return false;
            }

            var ps = m.GetParameters();
            if (ps.Length == 0)
            {
                m.Invoke(mb, null);
                reason = $"Heuristic invoke {mb.GetType().Name}.{m.Name}() score={best.Value.Score}";
                return true;
            }

            var pt = ps[0].ParameterType;
            // Common Unity UI handlers take BaseEventData; passing null is usually fine.
            if (pt.FullName != null && pt.FullName.Contains("EventSystems", StringComparison.OrdinalIgnoreCase))
            {
                m.Invoke(mb, new object?[] { null });
                reason = $"Heuristic invoke {mb.GetType().Name}.{m.Name}(eventData:null) score={best.Value.Score}";
                return true;
            }

            m.Invoke(mb, new object?[] { null });
            reason = $"Heuristic invoke {mb.GetType().Name}.{m.Name}(null) score={best.Value.Score}";
            return true;
        }
        catch
        {
            reason = "Heuristic invoke failed";
            return false;
        }
    }

    private static Type? FindTypeInAssemblyCSharp(string typeName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!string.Equals(asm.GetName().Name, "Assembly-CSharp", StringComparison.Ordinal))
                continue;

            return asm.GetTypes().FirstOrDefault(t =>
                string.Equals(t.Name, typeName, StringComparison.Ordinal) ||
                string.Equals(t.FullName, typeName, StringComparison.Ordinal));
        }

        return null;
    }

    private static Transform? FindBestVisualRoot(Transform root)
    {
        var renderers = root.GetComponentsInChildren<Renderer>(includeInactive: true);
        if (renderers == null || renderers.Length == 0)
            return null;

        // Heuristic: pick the immediate child of the controller root that contains
        // the most renderers. This usually corresponds to the player model hierarchy
        // (e.g. "Guy"), avoiding cloning the entire controller object.
        var childCounts = new Dictionary<Transform, int>();
        for (var i = 0; i < renderers.Length; i++)
        {
            var r = renderers[i];
            if (r == null)
                continue;

            var n = r.gameObject.name;
            if (!string.IsNullOrEmpty(n) && n.StartsWith("EasyDeliveryCoLanCoop.", StringComparison.Ordinal))
                continue;

            var t = r.transform;

            // If a renderer is on the root itself, keep it on root.
            if (t == root)
            {
                // keep t=root
            }
            else
            {
                // Lift to the immediate child under root.
                while (t != root && t.parent != null && t.parent != root)
                    t = t.parent;

                // If we somehow escaped the hierarchy, fall back to root.
                if (t.parent == null)
                    t = root;
            }

            if (!childCounts.TryGetValue(t, out var c))
                childCounts[t] = 1;
            else
                childCounts[t] = c + 1;
        }

        Transform? best = null;
        var bestCount = 0;
        foreach (var kv in childCounts)
        {
            if (kv.Value <= bestCount)
                continue;
            best = kv.Key;
            bestCount = kv.Value;
        }

        return best ?? root;
    }

    private static int ReadInt(object? v)
    {
        if (v is int i) return i;
        if (v is short s) return s;
        if (v is byte b) return b;
        if (v is float f) return Mathf.RoundToInt(f);
        return 0;
    }

    private static float ReadFloat(object? v)
    {
        if (v is float f) return f;
        if (v is int i) return i;
        if (v is double d) return (float)d;
        return 0f;
    }

    private static bool ReadBool(object? v)
    {
        if (v is bool b) return b;
        return false;
    }

    private static void SetField(Type t, object instance, string fieldName, object? value)
    {
        var f = t.GetField(fieldName, Any);
        if (f == null)
            return;

        try
        {
            if (f.FieldType == typeof(string))
                f.SetValue(instance, value?.ToString() ?? string.Empty);
            else if (f.FieldType == typeof(int) && value is int i)
                f.SetValue(instance, i);
            else if (f.FieldType == typeof(float) && value is float fl)
                f.SetValue(instance, fl);
            else if (f.FieldType == typeof(bool) && value is bool bo)
                f.SetValue(instance, bo);
        }
        catch
        {
            // ignore
        }
    }
}
