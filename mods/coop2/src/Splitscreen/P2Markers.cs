using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace EasyDeliveryCoLanCoop;

/// <summary>
/// The game's floating markers and labels (job destination, package, place names, shopkeeper marks...) are drawn
/// into Player 1's HUD only. Markers that belong to the shared job are recorded as the game draws them for P1
/// and shown again on P2's camera. Markers that depend on where the player stands (doors, shopkeepers) are
/// worked out again for P2's own position.
/// </summary>
internal static class P2Markers
{
    private struct Recorded
    {
        public P2WorldInteraction Item;
        public int Frame;
    }

    private static readonly List<Recorded> Shared = new();
    private static bool _recording;

    /// <summary>True while Player 2's inventory display is drawing its label (it is shown on P2's screen only).</summary>
    internal static bool InP2Inventory;
    private static string? _p2InvLabel;
    private static Vector3 _p2InvPos;
    private static int _p2InvFrame = -10;

    private static bool TakeP2InventoryLabel(Vector3 pos, string label)
    {
        if (!InP2Inventory) return true;
        _p2InvLabel = label;
        _p2InvPos = pos;
        _p2InvFrame = Time.frameCount;
        return false;   // not on Player 1's HUD
    }

    private static sTeleporter[] _doors = System.Array.Empty<sTeleporter>();
    private static sShopNPC[] _shops = System.Array.Empty<sShopNPC>();
    private static float _nextScan;
    private static readonly System.Reflection.FieldInfo? DialogStarted = AccessTools.Field(typeof(sShopNPC), "dialogStarted");
    private static readonly System.Reflection.FieldInfo? ActionPoint = AccessTools.Field(typeof(sShopNPC), "actionPoint");

    // ── Recording what the game draws for P1 (only from callers whose markers are not about P1's position) ──

    private static void Record(P2WorldInteraction item)
    {
        if (!_recording || !SplitscreenManager.SplitActive) return;
        Shared.Add(new Recorded { Item = item, Frame = Time.frameCount });
    }

    [HarmonyPatch]
    private static class SharedCallers
    {
        [HarmonyTargetMethods]
        private static IEnumerable<System.Reflection.MethodBase> Targets()
        {
            yield return AccessTools.Method(typeof(jobBoard), "DrawMarkers");
            yield return AccessTools.Method(typeof(ChallengeManager), "Update");
            yield return AccessTools.Method(typeof(DrawHUDLabel), "Update");
        }

        [HarmonyPrefix] private static void Pre() => _recording = true;
        [HarmonyFinalizer] private static void Fin() => _recording = false;
    }

    [HarmonyPatch(typeof(sHUD), "DrawWorldPointMarker")]
    private static class MarkerPatch
    {
        [HarmonyPrefix]
        private static void Pre(Vector3 __0) =>
            Record(new P2WorldInteraction { WorldPosition = __0, IsMarker = true, IsSprite = true, Sprite = new Rect(48f, 32f, 8f, 8f) });
    }

    [HarmonyPatch(typeof(sHUD), "DrawWorldPointLabel", typeof(Vector3), typeof(string))]
    private static class LabelPatch
    {
        [HarmonyPrefix]
        private static bool Pre(Vector3 __0, string __1)
        {
            if (!TakeP2InventoryLabel(__0, __1)) return false;
            Record(new P2WorldInteraction { WorldPosition = __0, Label = __1 });
            return true;
        }
    }

    [HarmonyPatch(typeof(sHUD), "DrawWorldPointLabel", typeof(Vector3), typeof(string), typeof(Vector2))]
    private static class LabelOffsetPatch
    {
        [HarmonyPrefix]
        private static bool Pre(Vector3 __0, string __1)
        {
            if (!TakeP2InventoryLabel(__0, __1)) return false;
            Record(new P2WorldInteraction { WorldPosition = __0, Label = __1 });
            return true;
        }
    }

    [HarmonyPatch(typeof(sHUD), "DrawWorldPointSprite")]
    private static class SpritePatch
    {
        [HarmonyPrefix]
        private static void Pre(Vector3 __0, float __1, float __2, float __3, float __4) =>
            Record(new P2WorldInteraction { WorldPosition = __0, IsSprite = true, Sprite = new Rect(__1, __2, __3, __4) });
    }

    // ── What to draw on P2's camera this frame ──

    internal static void Collect(List<P2WorldInteraction> into)
    {
        into.Clear();
        if (!SplitscreenManager.SplitActive || SplitscreenManager.P2Dead) return;

        Shared.RemoveAll(r => r.Frame < Time.frameCount - 2);
        int newest = 0;
        foreach (var r in Shared) if (r.Frame > newest) newest = r.Frame;
        foreach (var r in Shared)
            if (r.Frame == newest) into.Add(r.Item);   // one frame's worth, not every frame piled up

        if (_p2InvLabel != null && Time.frameCount - _p2InvFrame <= 1)
            into.Add(new P2WorldInteraction { WorldPosition = _p2InvPos, Label = _p2InvLabel });

        var p2 = SplitscreenManager.P2CharacterObject;
        if (p2 == null) return;
        if (Time.unscaledTime >= _nextScan)
        {
            _nextScan = Time.unscaledTime + 1f;
            _doors = UnityEngine.Object.FindObjectsByType<sTeleporter>(FindObjectsSortMode.None);
            _shops = UnityEngine.Object.FindObjectsByType<sShopNPC>(FindObjectsSortMode.None);
        }

        bool onFoot = !SplitscreenManager.IsP2InCar;
        var car = UnityEngine.Object.FindAnyObjectByType<sCarController>();
        Vector3 anchor = onFoot || car == null ? p2.transform.position : car.transform.position;

        // doors: the place name while walking toward them, a marker while driving near
        foreach (var t in _doors)
        {
            if (t == null || !t.isActiveAndEnabled) continue;
            float d = Vector3.Distance(anchor, t.transform.position);
            if (onFoot && d > 2f && d < 20f && !string.IsNullOrEmpty(t.label))
                into.Add(new P2WorldInteraction { WorldPosition = t.transform.position + Vector3.up, Label = t.label });
            else if (!onFoot && d < 20f)
                into.Add(new P2WorldInteraction { WorldPosition = t.transform.position + Vector3.up, IsMarker = true, IsSprite = true, Sprite = new Rect(48f, 32f, 8f, 8f) });
        }

        // shopkeepers / job givers
        foreach (var s in _shops)
        {
            if (s == null || !s.isActiveAndEnabled || (DialogStarted?.GetValue(s) is bool started && started) || !(ActionPoint?.GetValue(s) is Component ap && ap != null && ap.gameObject.activeSelf)) continue;
            float d = Vector3.Distance(anchor, s.transform.position);
            if (d > 2f && d < 40f)
                into.Add(new P2WorldInteraction { WorldPosition = s.transform.position + Vector3.up * 2f, IsMarker = true, IsSprite = true, Sprite = new Rect(48f, 32f, 8f, 8f) });
        }
    }

    internal static void Reset()
    {
        Shared.Clear();
        _recording = false;
    }
}
