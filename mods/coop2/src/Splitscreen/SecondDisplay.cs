using UnityEngine;

namespace EasyDeliveryCoLanCoop;

/// <summary>
/// Optional two-monitor mode: Player 1 keeps the game window, Player 2 gets the second monitor, both full screen.
/// The game shows its low-res render texture through an output camera + quad. For P2 the same is rebuilt far away
/// from the original (so P1's output camera never sees it), pointed at display 2, and fed from P2's own texture.
/// IMGUI (P2's prompts, dialog, throw bar) only draws on the main display, so those go into an overlay texture
/// that is shown over P2's picture.
/// </summary>
internal static class SecondDisplay
{
    private static readonly Vector3 Offset = new(5000f, 0f, 0f);

    private static RenderTexture? _p2Rt;
    private static RenderTexture? _overlayRt;
    private static GameObject? _root;
    private static Material? _overlayMat;
    private static RenderTexture? _previous;
    private static RenderTexture? _sourceRt;
    private static Transform? _p2RigRoot;

    internal static bool Active { get; private set; }
    internal static bool DrawingOverlay { get; private set; }

    /// <summary>Builds the second display's pipeline. Returns false (and the normal split stays) if it cannot.</summary>
    internal static bool Setup(Camera p1Cam, Transform p2Rig)
    {
        Teardown();
        if (Plugin.TwoMonitors?.Value != true) return false;

        if (Display.displays.Length < 2)
        {
            Plugin.Log.LogWarning("[TwoMonitors] Only one display found; using the normal splitscreen.");
            return false;
        }

        var sourceRt = p1Cam.targetTexture;
        if (sourceRt == null)
        {
            Plugin.Log.LogWarning("[TwoMonitors] Player 1's camera has no render texture; using the normal splitscreen.");
            return false;
        }

        // The game's quad that shows the render texture, and the camera that looks at it
        MeshRenderer? quad = null;
        foreach (var r in UnityEngine.Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
        {
            var m = r.sharedMaterial;
            if (m != null && m.mainTexture == sourceRt && r.GetComponent<MeshFilter>() != null) { quad = r; break; }
        }
        if (quad == null)
        {
            Plugin.Log.LogWarning("[TwoMonitors] Could not find the quad that shows the game picture; using the normal splitscreen.");
            return false;
        }

        Camera? outCam = null;
        foreach (var c in Camera.allCameras)
        {
            if (c.targetTexture != null || !c.enabled) continue;
            if ((c.cullingMask & (1 << quad.gameObject.layer)) == 0) continue;
            if (outCam == null || (c.orthographic && !outCam.orthographic)) outCam = c;
        }
        if (outCam == null)
        {
            Plugin.Log.LogWarning("[TwoMonitors] Could not find the output camera; using the normal splitscreen.");
            return false;
        }

        _sourceRt = sourceRt;
        _p2RigRoot = p2Rig;
        // P2's own picture, same format as the game's
        var desc = sourceRt.descriptor;
        _p2Rt = new RenderTexture(desc) { name = "LanCoop.P2Screen", filterMode = sourceRt.filterMode, hideFlags = HideFlags.HideAndDontSave };
        _p2Rt.Create();
        foreach (var c in p2Rig.GetComponentsInChildren<Camera>(true))
        {
            if (c.targetTexture == sourceRt)
            {
                c.targetTexture = _p2Rt;
                c.rect = new Rect(0f, 0f, 1f, 1f);
            }
        }

        _root = new GameObject("LanCoop.SecondDisplay");
        _root.transform.position = Offset;

        var picture = MakeQuad(quad, "P2Picture", _p2Rt, 0);
        picture.transform.SetParent(_root.transform, true);

        var cam = new GameObject("P2OutputCamera").AddComponent<Camera>();
        cam.CopyFrom(outCam);
        cam.targetTexture = null;
        cam.targetDisplay = 1;
        cam.rect = new Rect(0f, 0f, 1f, 1f);
        cam.transform.SetPositionAndRotation(outCam.transform.position + Offset, outCam.transform.rotation);
        cam.transform.SetParent(_root.transform, true);
        cam.enabled = true;
        var listener = cam.GetComponent<AudioListener>();
        if (listener != null) UnityEngine.Object.Destroy(listener);

        // P2's HUD pieces that IMGUI draws (prompts, dialog...) go into a texture shown over the picture
        EnsureOverlay();
        var overlay = MakeQuad(quad, "P2Overlay", _overlayRt!, 1);
        overlay.transform.SetParent(_root.transform, true);
        _overlayMat = overlay.GetComponent<MeshRenderer>().sharedMaterial;

        Display.displays[1].Activate();
        Active = true;
        Plugin.Log.LogInfo($"[TwoMonitors] Player 2 is shown on display 2 ({Display.displays[1].systemWidth}x{Display.displays[1].systemHeight}), " +
                           $"picture {_p2Rt.width}x{_p2Rt.height}, from '{quad.name}' / '{outCam.name}'.");
        return true;
    }

    private static GameObject MakeQuad(MeshRenderer source, string name, Texture texture, int queueBoost)
    {
        var go = new GameObject(name) { layer = source.gameObject.layer };
        go.transform.SetPositionAndRotation(source.transform.position + Offset, source.transform.rotation);
        go.transform.localScale = source.transform.lossyScale;
        go.AddComponent<MeshFilter>().sharedMesh = source.GetComponent<MeshFilter>().sharedMesh;
        var renderer = go.AddComponent<MeshRenderer>();
        var mat = new Material(source.sharedMaterial) { mainTexture = texture };
        if (queueBoost > 0) mat.renderQueue = Mathf.Max(mat.renderQueue, 3000) + queueBoost;
        renderer.sharedMaterial = mat;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        return go;
    }

    private static void EnsureOverlay()
    {
        int w = Mathf.Max(64, Screen.width), h = Mathf.Max(64, Screen.height);
        if (_overlayRt != null && _overlayRt.width == w && _overlayRt.height == h) return;
        if (_overlayRt != null) { _overlayRt.Release(); UnityEngine.Object.Destroy(_overlayRt); }
        _overlayRt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32)
        {
            name = "LanCoop.P2Overlay",
            filterMode = FilterMode.Point,
            hideFlags = HideFlags.HideAndDontSave,
        };
        _overlayRt.Create();
        if (_overlayMat != null) _overlayMat.mainTexture = _overlayRt;
    }

    /// <summary>If the second monitor goes away while playing, fall back to the normal splitscreen.</summary>
    internal static void CheckStillConnected()
    {
        if (!Active) return;
        if (Display.displays.Length >= 2 && Display.displays[1].systemWidth > 0) return;
        Plugin.Log.LogWarning("[TwoMonitors] The second monitor is gone; back to the normal splitscreen.");
        Teardown();
    }

    /// <summary>Called from OnGUI (Repaint): from now until EndOverlay the GUI draws into the overlay texture.</summary>
    internal static void BeginOverlay()
    {
        if (!Active || DrawingOverlay) return;
        EnsureOverlay();
        _previous = RenderTexture.active;
        RenderTexture.active = _overlayRt;
        GL.Clear(true, true, Color.clear);
        DrawingOverlay = true;
    }

    internal static void EndOverlay()
    {
        if (!DrawingOverlay) return;
        RenderTexture.active = _previous;
        _previous = null;
        DrawingOverlay = false;
    }

    internal static void Teardown()
    {
        if (!Active && _root == null && _p2Rt == null) return;
        Active = false;
        DrawingOverlay = false;
        // P2's cameras go back to the shared picture so the normal splitscreen works again
        if (_p2RigRoot != null && _sourceRt != null && _p2Rt != null)
            foreach (var c in _p2RigRoot.GetComponentsInChildren<Camera>(true))
                if (c.targetTexture == _p2Rt) c.targetTexture = _sourceRt;
        _p2RigRoot = null;
        _sourceRt = null;
        if (_root != null) UnityEngine.Object.Destroy(_root);
        _root = null;
        _overlayMat = null;
        if (_p2Rt != null) { _p2Rt.Release(); UnityEngine.Object.Destroy(_p2Rt); _p2Rt = null; }
        if (_overlayRt != null) { _overlayRt.Release(); UnityEngine.Object.Destroy(_overlayRt); _overlayRt = null; }
    }
}
