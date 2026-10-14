using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace EasyDeliveryCoLanCoop;

/// <summary>
/// A building's ProximityToggle turns the outside world's lights, snow and sounds OFF while the player
/// is inside (the interior has its own lighting). With two players that is wrong for the one who
/// is still outside. While splitscreen runs those objects stay on, and each camera hides the lights and
/// renderers of a toggle only while ITS player is near that toggle.
/// </summary>
internal static class ProximityToggleVisibility
{
    private sealed class Entry
    {
        public ProximityToggle Toggle = null!;
        public readonly List<Light> Lights = new();
        public readonly List<Renderer> Renderers = new();
        public readonly List<AudioSource> Audio = new();
        // What the toggle switches ON (interior): its lights, screen effects (Volume) and renderers
        public readonly List<Light> InLights = new();
        public readonly List<Behaviour> InVolumes = new();
        public readonly List<Renderer> InRenderers = new();
    }

    private static bool _active;
    private static Camera? _cam1, _cam2;
    private static readonly List<Entry> _entries = new();
    private static readonly List<Light> _lightsHidden = new();
    private static readonly List<Renderer> _renderersHidden = new();
    private static readonly List<Behaviour> _volumesHidden = new();
    private static float _nextRefresh;

    internal static void Init(Camera cam1, Camera cam2)
    {
        Shutdown();
        _cam1 = cam1;
        _cam2 = cam2;
        _nextRefresh = 0f;
        RenderPipelineManager.beginCameraRendering += OnBegin;
        RenderPipelineManager.endCameraRendering += OnEnd;
        _active = true;
    }

    internal static void Shutdown()
    {
        if (!_active) return;
        _active = false;
        RenderPipelineManager.beginCameraRendering -= OnBegin;
        RenderPipelineManager.endCameraRendering -= OnEnd;
        Restore();
        // un-mute anything we muted
        foreach (var e in _entries)
            foreach (var a in e.Audio)
                if (a != null) a.mute = false;
        _entries.Clear();
        _cam1 = _cam2 = null;
    }

    private static void Refresh()
    {
        if (Time.unscaledTime < _nextRefresh) return;
        _nextRefresh = Time.unscaledTime + 3f;

        _entries.Clear();
        var tmpL = new List<Light>();
        var tmpR = new List<Renderer>();
        var tmpA = new List<AudioSource>();
        foreach (var t in UnityEngine.Object.FindObjectsByType<ProximityToggle>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (t == null) continue;
            var e = new Entry { Toggle = t };
            var tmpB = new List<Behaviour>();
            if (t.toEnable != null)
            {
                foreach (var go in t.toEnable)
                {
                    if (go == null) continue;
                    go.GetComponentsInChildren(true, tmpL);
                    go.GetComponentsInChildren(true, tmpR);
                    go.GetComponentsInChildren(true, tmpB);
                    e.InLights.AddRange(tmpL);
                    e.InRenderers.AddRange(tmpR);
                    foreach (var b in tmpB)
                        if (b != null && b.GetType().Name == "Volume") e.InVolumes.Add(b);
                }
            }
            if (t.toDisable == null) t.toDisable = new List<GameObject>();
            foreach (var go in t.toDisable)
            {
                if (go == null) continue;
                go.GetComponentsInChildren(true, tmpL);
                go.GetComponentsInChildren(true, tmpR);
                go.GetComponentsInChildren(true, tmpA);
                e.Lights.AddRange(tmpL);
                e.Renderers.AddRange(tmpR);
                e.Audio.AddRange(tmpA);
            }
            _entries.Add(e);
        }
    }

    /// <summary>The listener (game audio) belongs to P1: mute a toggle's sounds while P1 is the one inside.</summary>
    internal static void UpdateAudio()
    {
        if (!_active) return;
        var p1 = SplitscreenManager.P1CharacterObject;
        if (p1 == null) return;
        Refresh();
        foreach (var e in _entries)
        {
            if (e.Toggle == null || !FollowsP1(e.Toggle)) continue;
            bool p1Near = Vector3.Distance(e.Toggle.transform.position, p1.transform.position) < e.Toggle.distanceThreashold;
            foreach (var a in e.Audio)
                if (a != null) a.mute = p1Near;
        }
    }

    internal static bool FollowsP1(ProximityToggle t)
    {
        var p1 = SplitscreenManager.P1CharacterObject;
        return p1 != null && t.target != null && (t.target == p1.transform || t.target.IsChildOf(p1.transform));
    }

    private static void Restore()
    {
        foreach (var l in _lightsHidden) if (l != null) l.enabled = true;
        foreach (var r in _renderersHidden) if (r != null) r.enabled = true;
        foreach (var v in _volumesHidden) if (v != null) v.enabled = true;
        _lightsHidden.Clear();
        _renderersHidden.Clear();
        _volumesHidden.Clear();
    }

    private static void OnBegin(ScriptableRenderContext ctx, Camera cam)
    {
        if (!_active) return;

        GameObject? player;
        if (cam == _cam1) player = SplitscreenManager.P1CharacterObject;
        else if (cam == _cam2) player = SplitscreenManager.P2CharacterObject;
        else return;
        if (player == null) return;

        Restore();
        Refresh();

        var otherPlayer = cam == _cam1 ? SplitscreenManager.P2CharacterObject : SplitscreenManager.P1CharacterObject;
        Vector3 pos = player.transform.position;
        Vector3 otherPos = otherPlayer != null ? otherPlayer.transform.position : new Vector3(1e9f, 1e9f, 1e9f);

        foreach (var e in _entries)
        {
            var t = e.Toggle;
            if (t == null || !FollowsP1(t)) continue;

            Vector3 here = t.transform.position;
            bool mineNear = Vector3.Distance(here, pos) < t.distanceThreashold;
            bool otherNear = Vector3.Distance(here, otherPos) < t.distanceThreashold;
            if (!mineNear && !otherNear) continue; // nobody near: the area is off, nothing to hide

            if (mineNear)
            {
                // This camera's player is inside: no outside sun / snow in their view
                foreach (var l in e.Lights)
                    if (l != null && l.enabled) { l.enabled = false; _lightsHidden.Add(l); }
                foreach (var r in e.Renderers)
                    if (r != null && r.enabled) { r.enabled = false; _renderersHidden.Add(r); }
            }
            else
            {
                // Only the other player is inside: the interior's lights, screen effects and models
                // are not part of this camera's world
                foreach (var l in e.InLights)
                    if (l != null && l.enabled) { l.enabled = false; _lightsHidden.Add(l); }
                foreach (var v in e.InVolumes)
                    if (v != null && v.enabled) { v.enabled = false; _volumesHidden.Add(v); }
                foreach (var r in e.InRenderers)
                    if (r != null && r.enabled) { r.enabled = false; _renderersHidden.Add(r); }
            }
        }
    }

    private static void OnEnd(ScriptableRenderContext ctx, Camera cam)
    {
        if (!_active) return;
        if (cam != _cam1 && cam != _cam2) return;
        Restore();
    }
}
