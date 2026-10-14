using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace EasyDeliveryCoLanCoop;

/// <summary>
/// The game's FirstPersonCameraHack switches two sets of truck objects with SetActive: the exterior
/// parts (toDisable) are hidden and the interior parts (toEnable) shown while the camera is first
/// person. That switch is global, so with two cameras one player's view mode broke the other's
/// (first person next to a third-person player showed an almost empty cab).
///
/// While splitscreen runs the game's switch is skipped (see FirstPersonCameraHackTogglePatch), both sets
/// are kept active, and each camera hides the set it must not see just while it renders.
/// </summary>
internal static class ViewModeVisibility
{
    private static bool _active;
    private static FirstPersonCameraHack? _hack;
    private static Camera? _cam1, _cam2;
    private static readonly List<GameObject> _exteriorObjects = new();
    private static readonly List<GameObject> _interiorObjects = new();
    private static readonly List<Renderer> _exterior = new();
    private static readonly List<Renderer> _interior = new();
    private static readonly List<Renderer> _hiddenNow = new();
    private static float _nextRefresh;
    private static bool _loggedSkip;

    /// <summary>True for the hack whose Toggle must be skipped because this class handles visibility.</summary>
    internal static bool Owns(FirstPersonCameraHack hack) => _active && hack == _hack;

    internal static void Init(Camera cam2)
    {
        Shutdown();

        var cam1 = SplitscreenManager.P1CameraInstance;
        if (cam1 == null) return;

        var p1Rig = SplitscreenManager.P1Rig;
        foreach (var h in UnityEngine.Object.FindObjectsByType<FirstPersonCameraHack>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (h != null && p1Rig != null && h.realCamera == p1Rig) { _hack = h; break; }
        }
        if (_hack == null)
        {
            Plugin.Log.LogWarning("[Splitscreen] ViewModeVisibility: no FirstPersonCameraHack for P1, truck parts stay global.");
            return;
        }

        _exteriorObjects.Clear();
        _interiorObjects.Clear();
        if (_hack.toDisable != null) _exteriorObjects.AddRange(_hack.toDisable);
        if (_hack.toEnable != null) _interiorObjects.AddRange(_hack.toEnable);

        // Neutral state: everything is active, cameras pick what they draw
        foreach (var go in _exteriorObjects) if (go != null) go.SetActive(true);
        foreach (var go in _interiorObjects) if (go != null) go.SetActive(true);

        _cam1 = cam1;
        _cam2 = cam2;
        _nextRefresh = 0f;
        RenderPipelineManager.beginCameraRendering += OnBegin;
        RenderPipelineManager.endCameraRendering += OnEnd;
        _active = true;
        Plugin.Log.LogInfo($"[Splitscreen] ViewModeVisibility: {_exteriorObjects.Count} exterior + {_interiorObjects.Count} interior truck objects handled per camera.");
        Plugin.Log.LogInfo("[Splitscreen] ViewModeVisibility exterior: " + string.Join(", ", _exteriorObjects.ConvertAll(o => o != null ? o.name : "null")));
        Plugin.Log.LogInfo("[Splitscreen] ViewModeVisibility interior: " + string.Join(", ", _interiorObjects.ConvertAll(o => o != null ? o.name : "null")));
    }

    internal static void Shutdown()
    {
        if (!_active) return;
        _active = false;
        RenderPipelineManager.beginCameraRendering -= OnBegin;
        RenderPipelineManager.endCameraRendering -= OnEnd;
        Restore();

        // Hand the objects back to the game in the state P1's camera needs
        var rig = _hack != null ? _hack.realCamera : null;
        bool firstPerson = rig != null && rig.firstPerson && !rig.fixedPerspective;
        foreach (var go in _exteriorObjects) if (go != null) go.SetActive(!firstPerson);
        foreach (var go in _interiorObjects) if (go != null) go.SetActive(firstPerson);

        _exterior.Clear();
        _interior.Clear();
        _exteriorObjects.Clear();
        _interiorObjects.Clear();
        _hack = null;
        _cam1 = _cam2 = null;
        _loggedSkip = false;
    }

    private static void Refresh()
    {
        if (Time.unscaledTime < _nextRefresh) return;
        _nextRefresh = Time.unscaledTime + 1f;
        Collect(_exteriorObjects, _exterior);
        Collect(_interiorObjects, _interior);
    }

    private static void Collect(List<GameObject> objects, List<Renderer> into)
    {
        into.Clear();
        var tmp = new List<Renderer>();
        var p1 = SplitscreenManager.P1CharacterObject;
        var p2 = SplitscreenManager.P2CharacterObject;
        int skipped = 0;
        foreach (var go in objects)
        {
            if (go == null) continue;
            go.GetComponentsInChildren(true, tmp);
            foreach (var r in tmp)
            {
                // The seated player models are children of the truck. Their visibility is handled by
                // OwnModelHider (hidden only on the owner's screen), never by the view mode.
                if ((p1 != null && r.transform.IsChildOf(p1.transform)) ||
                    (p2 != null && r.transform.IsChildOf(p2.transform)))
                {
                    skipped++;
                    continue;
                }
                into.Add(r);
            }
        }
        if (skipped > 0 && !_loggedSkip)
        {
            _loggedSkip = true;
            Plugin.Log.LogInfo($"[Splitscreen] ViewModeVisibility: left {skipped} player-model renderer(s) out of the view-mode hiding.");
        }
    }

    private static bool IsFirstPerson(sCameraController? rig) =>
        rig != null && rig.firstPerson && !rig.fixedPerspective;

    private static void HideAll(List<Renderer> list)
    {
        foreach (var r in list)
        {
            if (r != null && r.enabled)
            {
                r.enabled = false;
                _hiddenNow.Add(r);
            }
        }
    }

    private static void Restore()
    {
        foreach (var r in _hiddenNow)
        {
            if (r != null) r.enabled = true;
        }
        _hiddenNow.Clear();
    }

    private static void OnBegin(ScriptableRenderContext ctx, Camera cam)
    {
        if (!_active) return;

        sCameraController? rig;
        if (cam == _cam1) rig = SplitscreenManager.P1Rig;
        else if (cam == _cam2) rig = SplitscreenManager.P2Rig;
        else return;

        Restore(); // in case an end callback was skipped
        Refresh();

        // First person: inside only. Third person: outside only (what the game's own switch did).
        HideAll(IsFirstPerson(rig) ? _exterior : _interior);
    }

    private static void OnEnd(ScriptableRenderContext ctx, Camera cam)
    {
        if (!_active) return;
        if (cam != _cam1 && cam != _cam2) return;
        Restore();
    }
}
