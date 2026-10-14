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
    private static float _nextCampfireScan;
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

        float buff = 0f;
        if (p2InCar)
        {
            // Truck heater, same formula as sHUD.DoTemperature
            buff = Mathf.Max(Mathf.Clamp01(_hud.fuel / _hud.fuelCapacity / 0.25f) * 3f, 1f);
        }
        else
        {
            if (Time.unscaledTime >= _nextCampfireScan)
            {
                _nextCampfireScan = Time.unscaledTime + 3f;
                _campfires = UnityEngine.Object.FindObjectsByType<Campfire>(FindObjectsSortMode.None);
            }
            foreach (var fire in _campfires)
            {
                if (fire == null || fire.state != 2) continue;
                float d = Vector3.Distance(fire.transform.position, p2Position);
                if (d < 6f)
                    buff = Mathf.Max(buff, (1f - d / 6f) * 3f);
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
