using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

namespace EasyDeliveryCoLanCoop;

/// <summary>
/// Player 2's own body temperature. Energy is the one thing shared between the players: it
/// is the game's single sHUD pool (P1's), and P2 only adds to its drain / reads it for the
/// tired effect. The cold model follows the sHUD rules: the truck heater warms, campfires warm
/// by distance, being low on energy doubles the cooling rate. The frost screen effect is
/// re-applied per camera so each player only sees their own cold.
/// </summary>
internal static class P2Stats
{
    internal static bool Active { get; private set; }
    internal static float Temperature { get; private set; }

    private static sHUD? _hud;
    private static FrostEffect? _frost;
    private static MethodInfo? _frostUpdate;
    private static FieldInfo? _energyForWarmth;
    private const float EnergyCapacityMultiplier = 2f;
    private static float _originalEnergyCapacity;
    private static Camera? _p1Cam;
    private static Camera? _p2Cam;
    private static sCharacterController? _p2Controller;
    private static sCharacterController? _p1Controller;
    private static Campfire[] _campfires = Array.Empty<Campfire>();
    private static TemperatureBuff[] _zones = Array.Empty<TemperatureBuff>();
    private static sTransactionNPC[] _shops = Array.Empty<sTransactionNPC>();
    private static EndButton[] _endButtons = Array.Empty<EndButton>();
    private static FieldInfo? _fireDead, _fireDestroyed;
    private static float _nextCampfireScan;
    private static string _lastWarmSource = "";
    private const float SourceRadius = 6f;       // campfires and burning ground fires
    private const float InteriorRadius = 12f;    // shop NPCs / ending button, which warm all of their room
    private static int _frostFrame = -1;
    private static float _p1Intensity;

    internal static float TemperatureFraction => _hud != null && _hud.temperatureLimit > 0f ? Temperature / _hud.temperatureLimit : 1f;
    internal static float FrostIntensity => Mathf.Clamp01(1f - TemperatureFraction);

    /// <summary>Shared energy pool (P1's sHUD).</summary>
    internal static bool LowEnergy => _hud != null && _hud.LowEnergy();

    internal static void Init(Camera p1Cam, Camera p2Cam, GameObject p2Character)
    {
        Shutdown();

        _hud = UnityEngine.Object.FindFirstObjectByType<sHUD>();
        if (_hud == null)
        {
            Plugin.Log.LogWarning("[Splitscreen] P2Stats: no sHUD found, P2 energy/cold disabled.");
            return;
        }

        _frost = _hud.GetComponent<FrostEffect>();
        _frostUpdate = typeof(FrostEffect).GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

        _p1Cam = p1Cam;
        _p2Cam = p2Cam;
        _p2Controller = p2Character.GetComponent<sCharacterController>();
        _p1Controller = null;
        foreach (var c in UnityEngine.Object.FindObjectsByType<sCharacterController>(FindObjectsSortMode.None))
        {
            if (c != null && c.gameObject != p2Character) { _p1Controller = c; break; }
        }

        _energyForWarmth = typeof(sHUD).GetField("energyForWarmth", BindingFlags.Instance | BindingFlags.NonPublic);

        // Two players share the pool, so it holds twice as much (same fill level, bars last twice as long)
        _originalEnergyCapacity = _hud.energyCapacity;
        _hud.energyCapacity = _originalEnergyCapacity * EnergyCapacityMultiplier;
        _hud.energy *= EnergyCapacityMultiplier;

        // P2 starts as warm as P1 is right now
        Temperature = _hud.temperature;

        RenderPipelineManager.beginCameraRendering += OnBeginCamera;
        RenderPipelineManager.endCameraRendering += OnEndCamera;
        Active = true;
        Plugin.Log.LogInfo($"[Splitscreen] P2Stats started (temp {Temperature:0}/{_hud.temperatureLimit:0}, energy shared).");
    }

    /// <summary>Back to full warmth after a respawn (the game does the same for P1 when the world reloads).</summary>
    internal static void ResetWarmth()
    {
        if (_hud != null) Temperature = _hud.temperatureLimit;
    }

    internal static void Shutdown()
    {
        if (!Active) return;
        Active = false;
        RenderPipelineManager.beginCameraRendering -= OnBeginCamera;
        RenderPipelineManager.endCameraRendering -= OnEndCamera;

        if (_p2Controller != null && _p1Controller != null)
            _p2Controller.acceleration = _p1Controller.acceleration;

        // Give the pool back its normal size, keeping the current fill level
        if (_hud != null && _originalEnergyCapacity > 0f)
        {
            _hud.energy = Mathf.Min(_hud.energy / EnergyCapacityMultiplier, _originalEnergyCapacity * 1.5f);
            _hud.energyCapacity = _originalEnergyCapacity;
        }
        _originalEnergyCapacity = 0f;

        _hud = null;
        _frost = null;
        _p1Cam = _p2Cam = null;
        _p2Controller = null;
        _p1Controller = null;
    }

    internal static void Tick(float dt, bool p2InCar, Vector3 p2Position)
    {
        if (!Active || _hud == null) return;
        if (SplitscreenManager.P2Dead) return; // nothing drains while P2 is down

        float buff = 0f;
        if (p2InCar)
        {
            // Truck heater, same formula as sHUD.DoTemperature
            buff = Mathf.Max(Mathf.Clamp01(_hud.fuel / _hud.fuelCapacity / 0.25f) * 3f, 1f);
        }
        else
        {
            buff = WarmthAt(p2Position, out string source);
            if (source != _lastWarmSource)
            {
                _lastWarmSource = source;
                Plugin.Log.LogInfo($"[Splitscreen] P2 warmth source: {(source.Length > 0 ? source : "none")} (buff {buff:0.##})");
            }
        }

        float rate = _hud.temperatureRate;
        if (LowEnergy) rate = _hud.temperatureRate * 2f;
        if (buff > 0f) rate = _hud.temperatureRate - buff;

        bool energyForWarmth = rate > 0f;
        Temperature = Mathf.Clamp(Temperature - dt * rate, 0f, _hud.temperatureLimit);

        // Shared pool: P2 burns it too, but only when P1 isn't already draining it this frame
        // (so both sitting in the truck doesn't drain twice as fast).
        if (p2InCar || energyForWarmth)
        {
            bool p1Draining = (_hud.navigation != null && _hud.navigation.car != null && !_hud.navigation.car.GuyActive)
                              || (_energyForWarmth != null && _energyForWarmth.GetValue(_hud) is bool b && b);
            if (!p1Draining)
                _hud.LowerEnergy(dt);
        }

        // Tired: TiredEffect is the only thing that ever changes walking acceleration, and it already
        // follows the shared energy, so P2 simply walks at exactly the speed P1's controller has.
        if (_p2Controller != null)
        {
            if (_p1Controller != null)
                _p2Controller.acceleration = _p1Controller.acceleration;
            if (_p2Controller.animator != null)
                _p2Controller.animator.SetBool("Tired", LowEnergy);
        }
    }

    /// <summary>
    /// What warms the world around Player 2. The game's warmth sources all add to one shared value that
    /// only follows Player 1 (they measure the distance to P1), so each one is re-evaluated at P2.
    /// </summary>
    private static float WarmthAt(Vector3 pos, out string source)
    {
        string bestName = "";
        float best = 0f;

        if (Time.unscaledTime >= _nextCampfireScan)
        {
            _nextCampfireScan = Time.unscaledTime + 3f;
            _campfires = UnityEngine.Object.FindObjectsByType<Campfire>(FindObjectsSortMode.None);
            _zones = UnityEngine.Object.FindObjectsByType<TemperatureBuff>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            _shops = UnityEngine.Object.FindObjectsByType<sTransactionNPC>(FindObjectsSortMode.None);
            _endButtons = UnityEngine.Object.FindObjectsByType<EndButton>(FindObjectsSortMode.None);
        }

        void Consider(float value, string name)
        {
            if (value > best) { best = value; bestName = name; }
        }

        // Campfires (Campfire.Update)
        foreach (var fire in _campfires)
        {
            if (fire == null || fire.state != 2) continue;
            float d = Vector3.Distance(fire.transform.position, pos);
            if (d < SourceRadius) Consider((1f - d / SourceRadius) * 3f, "campfire");
        }

        // Fires that spread on the ground (FireSpread.Warm), as long as they are alive
        var fires = FireSpread.fires;
        if (fires != null)
        {
            _fireDead ??= typeof(FireSpread).GetField("dead", BindingFlags.Instance | BindingFlags.NonPublic);
            _fireDestroyed ??= typeof(FireSpread).GetField("destroyed", BindingFlags.Instance | BindingFlags.NonPublic);
            foreach (var f in fires)
            {
                if (f == null) continue;
                if (_fireDead?.GetValue(f) is true || _fireDestroyed?.GetValue(f) is true) continue;
                float d = Vector3.Distance(f.transform.position, pos);
                if (d < SourceRadius) Consider((1f - d / SourceRadius) * 3f, "fire");
            }
        }

        // Warm areas (shelters): TemperatureBuff objects that are switched on, if P2 is inside them
        foreach (var zone in _zones)
        {
            if (zone == null || !zone.isActiveAndEnabled) continue;
            if (Inside(zone.gameObject, pos)) Consider(zone.temperature, "shelter '" + zone.name + "'");
        }

        // Shop NPCs and the ending button warm their whole room
        foreach (var shop in _shops)
        {
            if (shop != null && shop.isActiveAndEnabled && Vector3.Distance(shop.transform.position, pos) < InteriorRadius)
                Consider(2f, "shop '" + shop.name + "'");
        }
        foreach (var eb in _endButtons)
        {
            if (eb != null && eb.isActiveAndEnabled && Vector3.Distance(eb.transform.position, pos) < InteriorRadius)
                Consider(2f, "end room");
        }

        source = bestName;
        return best;
    }

    /// <summary>Is the point inside any collider of this object, its children or its parents (zone volumes)?</summary>
    private static bool Inside(GameObject go, Vector3 p)
    {
        bool anyCollider = false;
        var colliders = new System.Collections.Generic.List<Collider>();
        colliders.AddRange(go.GetComponentsInChildren<Collider>(true));
        colliders.AddRange(go.GetComponentsInParent<Collider>(true));
        foreach (var c in colliders)
        {
            if (c == null || !c.enabled || !c.gameObject.activeInHierarchy) continue;
            anyCollider = true;
            if (!c.bounds.Contains(p)) continue;
            try
            {
                if ((c.ClosestPoint(p) - p).sqrMagnitude < 0.0025f) return true;
            }
            catch
            {
                return true; // non-convex mesh collider: its bounds are the best we have
            }
        }
        // A zone without any collider has no volume to test: fall back to a small area around it
        return !anyCollider && Vector3.Distance(go.transform.position, p) < 8f;
    }

    // ── Per-camera frost ────────────────────────────────────────────────────

    private static void ApplyFrost(float intensity)
    {
        if (_frost == null || _frostUpdate == null) return;
        _frost.intensity = intensity;
        try { _frostUpdate.Invoke(_frost, null); }
        catch { }
    }

    private static void OnBeginCamera(ScriptableRenderContext ctx, Camera cam)
    {
        if (!Active || _frost == null) return;
        if (cam != _p1Cam && cam != _p2Cam) return;

        // sHUD writes P1's intensity once per frame before any camera renders
        if (Time.frameCount != _frostFrame)
        {
            _frostFrame = Time.frameCount;
            _p1Intensity = _frost.intensity;
        }

        ApplyFrost(cam == _p2Cam ? FrostIntensity : _p1Intensity);
    }

    private static void OnEndCamera(ScriptableRenderContext ctx, Camera cam)
    {
        // Leave P1's values in place afterwards (the audio low-pass follows them too)
        if (!Active || _frost == null || cam != _p2Cam) return;
        ApplyFrost(_p1Intensity);
    }
}
