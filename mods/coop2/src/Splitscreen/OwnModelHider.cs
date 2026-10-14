using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace EasyDeliveryCoLanCoop;

/// <summary>
/// While a player sits in the truck, their own seated model is hidden on their own screen
/// but stays visible on the other player's screen. Done per camera: the model's renderers are
/// switched off just before that player's camera renders and back on right after, so no layers,
/// culling masks or physics settings are touched.
/// </summary>
internal static class OwnModelHider
{
    private static bool _active;
    private static Camera? _cam1, _cam2;
    private static GameObject? _model1, _model2;
    private static readonly List<Renderer> _renderers1 = new();
    private static readonly List<Renderer> _renderers2 = new();
    private static readonly List<Renderer> _hiddenNow = new();
    private static float _nextRefresh;
    private static SplitscreenManager.CarSeat _lastSeat1, _lastSeat2;

    internal static void Init(Camera cam1, Camera cam2, GameObject model1, GameObject model2)
    {
        Shutdown();
        _cam1 = cam1; _cam2 = cam2;
        _model1 = model1; _model2 = model2;
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
        _renderers1.Clear();
        _renderers2.Clear();
        _cam1 = _cam2 = null;
        _model1 = _model2 = null;
    }

    private static void Refresh()
    {
        bool seatChanged = SplitscreenManager.P1Seat != _lastSeat1 || SplitscreenManager.P2Seat != _lastSeat2;
        if (!seatChanged && Time.unscaledTime < _nextRefresh) return;
        _nextRefresh = Time.unscaledTime + 0.5f;
        _lastSeat1 = SplitscreenManager.P1Seat;
        _lastSeat2 = SplitscreenManager.P2Seat;

        Collect(_model1, _renderers1);
        Collect(_model2, _renderers2);
    }

    private static void Collect(GameObject? model, List<Renderer> into)
    {
        into.Clear();
        if (model == null) return;
        model.GetComponentsInChildren(true, into);
    }

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
        if (cam != _cam1 && cam != _cam2) return;

        Restore(); // in case an end callback was skipped
        Refresh();

        if (cam == _cam1 && SplitscreenManager.P1Seat != SplitscreenManager.CarSeat.None)
            HideAll(_renderers1);
        else if (cam == _cam2 && SplitscreenManager.P2Seat != SplitscreenManager.CarSeat.None)
            HideAll(_renderers2);
    }

    private static void OnEnd(ScriptableRenderContext ctx, Camera cam)
    {
        if (!_active) return;
        if (cam != _cam1 && cam != _cam2) return;
        Restore();
    }
}
