using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;

namespace EasyDeliveryCoLanCoop;

/// <summary>
/// World-space interaction data for Player 2, matching Player 1's native in-game HUD markers.
/// </summary>
public struct P2WorldInteraction
{
    public Vector3 WorldPosition;
    public string Label;       // text exactly as the game's own marker would show it (empty for sprites)
    public string ButtonHint;  // "E" or "A"
    public bool IsMarker;      // ground placement dot (sprite 48,32 anchored at the point's top-left)
    public bool IsSprite;      // icon from the game's sprite sheet, centred on the point
    public Rect Sprite;        // sprite-sheet rect (x, y, w, h in sheet pixels)
}

/// <summary>
/// Manages splitscreen local co-op mode.
/// Toggle with the configured key (default F8).
/// Routing:
///   - 1 controller connected: P1 uses Keyboard & Mouse, P2 uses Controller 1.
///   - 2+ controllers connected: P1 uses Controller 1, P2 uses Controller 2.
///   - 0 controllers: P1 uses Keyboard (WASD), P2 uses Keyboard (Arrow keys).
/// </summary>
internal sealed class SplitscreenManager : MonoBehaviour
{
    private static readonly BindingFlags AnyField =
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    // ── Static references for Harmony patches & HUD ─────────────────────────
    internal static Transform? P2CameraTransform { get; private set; }
    internal static Camera? P2CameraInstance => _instance?._p2Cam;
    internal static Camera? P1CameraInstance => _instance?._p1Cam;
    internal static bool SplitActive => _activeState;
    private static int _p2GamepadIndex = 0;
    private static int _p2LegacyJoystickNumber = 1;
    private static bool _activeState;
    internal static bool IsP2InCar => _p2InCar;
    private static bool _p2InCar = false;
    private static sCarController? _car;
    private static GameObject? _p2Character;
    private static SplitscreenManager? _instance;

    public enum CarSeat { None, Driver, Passenger }
    internal static CarSeat P1Seat = CarSeat.None;
    internal static CarSeat P2Seat = CarSeat.None;
    internal static bool IsP2Driver => P2Seat == CarSeat.Driver;
    internal static bool IsP1Passenger => P1Seat == CarSeat.Passenger;

    public static readonly Vector3 SeatedCharacterScale = new Vector3(0.52f, 0.52f, 0.52f);
    public const float SeatedXOffset = 0.22f;
    public const float SeatedYOffset = -0.12f;
    public const float SeatedZOffset = 0.38f;

    public static Vector3 GetSeatedLocalPosition(CarSeat seat)
    {
        float x = seat == CarSeat.Driver ? SeatedXOffset : -SeatedXOffset;
        return new Vector3(x, SeatedYOffset, SeatedZOffset);
    }

    public static void SetCharacterCollisionsEnabled(GameObject character, bool enabled)
    {
        if (character == null) return;

        var cc = character.GetComponent<CharacterController>();
        if (cc != null) cc.enabled = enabled;

        var rb = character.GetComponent<Rigidbody>();
        if (rb != null)
        {
            if (!enabled)
            {
                rb.isKinematic = true;
                rb.detectCollisions = false;
            }
            else
            {
                rb.isKinematic = false;
                rb.detectCollisions = true;
            }
        }

        var colliders = character.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < colliders.Length; i++)
        {
            if (colliders[i] != null)
                colliders[i].enabled = enabled;
        }
    }

    public static void IgnoreCollisionsWithCar(GameObject character, sCarController car, bool ignore)
    {
        if (character == null || car == null) return;
        var charCols = character.GetComponentsInChildren<Collider>(true);
        var carCols = car.GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < charCols.Length; i++)
        {
            var c1 = charCols[i];
            if (c1 == null) continue;
            for (int j = 0; j < carCols.Length; j++)
            {
                var c2 = carCols[j];
                if (c2 == null || c1 == c2) continue;
                try { Physics.IgnoreCollision(c1, c2, ignore); } catch { }
            }
        }
    }

    // ── State ──────────────────────────────────────────────────────────────
    private bool _active;
    private Camera? _p1Cam;
    private Camera? _p2Cam;
    private SplitscreenHud? _hud;
    private PlayerInput? _p1Input;

    // The game's chase-camera rig (sCameraController) owns the camera transform every
    // frame, so P2 needs its own cloned rig, not just a cloned Camera.
    // Testing aid (F6): the keyboard drives P2 (with the game's normal bindings) instead of P1.
    internal static bool ControlP2 { get; private set; }

    /// <summary>The keyboard's normal-binding input, captured from P1's sInputManager while ControlP2.</summary>
    internal struct RawInput
    {
        public Vector2 PlayerInput, DriveInput, CameraLook;
        public bool SelectPressed, BackPressed, BreakHeld, CameraPressed;
    }
    internal static RawInput Raw;

    internal static sCameraController? P2Rig => _p2Rig;
    internal static sCameraController? P1Rig => _p1Rig;

    // Building interiors: when both players are inside, they share one full-screen camera.
    internal static bool P1Inside { get; private set; }
    internal static bool P2Inside { get; private set; }
    internal static bool MergedView => P1Inside && P2Inside;
    private static bool _loggedMerged;

    /// <summary>The camera that shows Player 2's world (their half, or the shared full-screen one).</summary>
    internal static Camera? P2ViewCamera =>
        MergedView ? _instance?._p1Cam : _instance?._p2Cam;

    // A building interior is a separate area of the map; its exit door ("leave" sTeleporter) is inside it.
    // Anyone close to one of those doors is inside a building, however they got there (door, F8 while
    // already indoors, ...), so the state is read from positions instead of counting door uses.
    private const float InteriorRadius = 20f;
    private static sTeleporter[] _doors = System.Array.Empty<sTeleporter>();
    private static float _nextDoorScan = -100f;

    internal static bool IsInsideBuilding(Vector3 pos)
    {
        if (Time.unscaledTime >= _nextDoorScan)
        {
            _nextDoorScan = Time.unscaledTime + 3f;
            _doors = FindObjectsByType<sTeleporter>(FindObjectsSortMode.None);
        }
        foreach (var tp in _doors)
        {
            if (tp != null && tp.interactText == "leave" && Vector3.Distance(tp.transform.position, pos) < InteriorRadius)
                return true;
        }
        return false;
    }

    private void UpdateInsideState()
    {
        bool p1 = _p1Character != null && IsInsideBuilding(_p1Character.transform.position);
        bool p2 = _p2Character != null && IsInsideBuilding(_p2Character.transform.position);
        if (p1 != P1Inside) Plugin.Log.LogInfo($"[Splitscreen] P1 {(p1 ? "is inside" : "left")} a building.");
        if (p2 != P2Inside) Plugin.Log.LogInfo($"[Splitscreen] P2 {(p2 ? "is inside" : "left")} a building.");
        P1Inside = p1;
        P2Inside = p2;
    }
    internal static GameObject? P1CharacterObject => _instance?._p1Character;
    internal static GameObject? P2CharacterObject => _p2Character;
    private static CarSeat _loggedP1Seat, _loggedP2Seat;
    private static bool _p2HasCameraHack;

    private static sCameraController? _p1Rig;
    private static sCameraController? _p2Rig;
    private GameObject? _p2RigHolder;
    private static float _camBaseDistance = 5f;
    private GameObject? _p1Character;


    // ── Viewport rects ─────────────────────────────────────────────────────
    private static readonly Rect P1RectV = new Rect(0f,   0f, 0.5f, 1f);
    private static readonly Rect P2RectV = new Rect(0.5f, 0f, 0.5f, 1f);
    private static readonly Rect P1RectH = new Rect(0f,  0.5f, 1f, 0.5f);
    private static readonly Rect P2RectH = new Rect(0f,   0f,  1f, 0.5f);
    private static readonly Rect FullRect = new Rect(0f,  0f,  1f, 1f);

    private float _nextToggleCooldown;

    private void Awake()
    {
        _instance = this;
    }

    private void Update()
    {
        if (Plugin.SplitscreenToggleKey != null)
        {
            if (Time.unscaledTime >= _nextToggleCooldown && Input.GetKeyDown(Plugin.SplitscreenToggleKey.Value))
            {
                _nextToggleCooldown = Time.unscaledTime + 0.5f;

                if (_active)
                    Deactivate();
                else
                    TryActivate();
            }
        }

        if (_active && Plugin.SplitscreenSwapControlKey != null && Input.GetKeyDown(Plugin.SplitscreenSwapControlKey.Value))
        {
            ControlP2 = !ControlP2;
            Raw = default;
            Plugin.Log.LogInfo($"[Splitscreen] Keyboard now controls {(ControlP2 ? "Player 2" : "Player 1")}.");
        }

        if (!_active || _p2Character == null) return;

        // Check P2 Interact (E / A / Cross / RT)
        if (!P2Dead && GetP2InteractPressed())
        {
            try
            {
                HandleP2Interaction();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Splitscreen] P2 interaction error: {ex.Message}");
            }
        }

        // Check P2 Back / Drop (Space / B / Circle / LT)
        if (!P2Dead && GetP2BackPressed())
        {
            try
            {
                HandleP2Back();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Splitscreen] P2 back/drop error: {ex.Message}");
            }
        }

        P2Stats.Tick(Time.deltaTime, _p2InCar, _p2Character.transform.position);
        UpdateP2Death();
        if (!P2Dead) CheckP2AutoTeleport();
        LinkInventory();
        ProximityToggleVisibility.UpdateAudio();

        // P2's rig has no FirstPersonCameraHack of its own: toggle first/third person directly
        if (!_p2HasCameraHack && _p2Rig != null && GetP2CameraPressed() && _p2Rig.firstPersonPivot != null)
        {
            _p2Rig.firstPerson = !_p2Rig.firstPerson;
            _p2Rig.firstPersonMode = _p2Rig.firstPerson;
            Plugin.Log.LogInfo($"[Splitscreen] P2 camera: {(_p2Rig.firstPerson ? "first" : "third")} person.");
        }

        UpdateCameras();
    }

    private void UpdateCameras()
    {
        if (_p1Cam == null || _p2Cam == null) return;

        if (P1Seat != _loggedP1Seat || P2Seat != _loggedP2Seat)
        {
            _loggedP1Seat = P1Seat;
            _loggedP2Seat = P2Seat;
            Plugin.Log.LogInfo($"[Splitscreen] Seats: P1={P1Seat} P2={P2Seat} ");
        }

        UpdateInsideState();

        // Both players inside a building: one shared full-screen camera, always Player 1's
        if (MergedView)
        {
            _p1Cam.enabled = true;
            _p2Cam.enabled = false;
            ApplyRect(_p1Rig != null ? _p1Rig.transform : _p1Cam.transform, FullRect);
            if (!_loggedMerged)
            {
                _loggedMerged = true;
                Plugin.Log.LogInfo("[Splitscreen] Both players inside: one shared camera.");
            }
            return;
        }
        if (_loggedMerged)
        {
            _loggedMerged = false;
            Plugin.Log.LogInfo("[Splitscreen] A player left the building: splitscreen again.");
        }

        // Both players keep their own viewport at all times, also while sharing the truck.
        _p1Cam.enabled = true;
        _p2Cam.enabled = true;
        if (_hud != null) _hud.SetActive(true);

        bool vertical = Plugin.SplitscreenOrientation?.Value != "Horizontal";
        ApplyRect(_p1Rig != null ? _p1Rig.transform : _p1Cam.transform, vertical ? P1RectV : P1RectH);
        ApplyRect(_p2Rig != null ? _p2Rig.transform : _p2Cam.transform, vertical ? P2RectV : P2RectH);

    }

    private static void ApplyRect(Transform root, Rect rect)
    {
        // The game's cameras render into a shared low-res RenderTexture that a full-screen
        // camera shows, so `rect` here is the player's half of that texture (keep RT cameras).
        foreach (var c in root.GetComponentsInChildren<Camera>(includeInactive: true))
            c.rect = rect;
    }

    // ── Activation ─────────────────────────────────────────────────────────

    private void TryActivate()
    {
        var mainCam = FindMainCamera();
        if (mainCam == null)
        {
            Plugin.Log.LogWarning("[Splitscreen] Could not find main camera — are you in a gameplay scene?");
            return;
        }

        var p1Controller = SplitscreenHelper.FindLocalCharacterController();
        if (p1Controller == null)
        {
            Plugin.Log.LogWarning("[Splitscreen] Could not find player character — load a world first.");
            return;
        }

        // P1's chase-camera rig: the interaction script holds the exact reference.
        var p1Guy = p1Controller.GetComponent<sCharacterInteraction>();
        _p1Rig = p1Guy != null ? p1Guy.cam : null;
        if (_p1Rig == null) _p1Rig = mainCam.GetComponentInParent<sCameraController>();
        if (_p1Rig == null) _p1Rig = FindFirstObjectByType<sCameraController>();
        _p1Cam = (_p1Rig != null && _p1Rig.cam != null) ? _p1Rig.cam : mainCam;
        _p1Character = p1Controller.gameObject;
        _car = FindAnyObjectByType<sCarController>();

        _camBaseDistance = 5f;
        if (p1Guy != null)
        {
            var baseField = typeof(sCharacterInteraction).GetField("camDistance", AnyField);
            if (baseField != null && baseField.GetValue(p1Guy) is float d && d > 0.01f)
                _camBaseDistance = d;
            else if (_p1Rig != null && _p1Rig.camDistance > 0.01f)
                _camBaseDistance = _p1Rig.camDistance;
        }

        Plugin.Log.LogInfo($"[Splitscreen] P1 rig={(_p1Rig != null ? _p1Rig.name : "null")} cam={_p1Cam.name} " +
                           $"baseDist={_camBaseDistance:0.##}");

        // Find Player 1's PlayerInput component
        _p1Input = p1Controller.GetComponent<PlayerInput>()
                ?? p1Controller.GetComponentInChildren<PlayerInput>(includeInactive: true)
                ?? FindFirstObjectByType<PlayerInput>();

        // Count connected gamepads
        int gamepadCount = Gamepad.all.Count;
        if (gamepadCount == 0)
        {
            gamepadCount = CountConnectedJoysticks();
        }

        if (gamepadCount == 1)
        {
            // ── 1 Controller Connected: P1 = Keyboard, P2 = Controller 1 ───
            _p2GamepadIndex = 0;
            _p2LegacyJoystickNumber = 1;

            if (_p1Input != null)
            {
                try
                {
                    var kbDevices = new List<InputDevice>();
                    if (Keyboard.current != null) kbDevices.Add(Keyboard.current);
                    if (Mouse.current != null) kbDevices.Add(Mouse.current);

                    _p1Input.neverAutoSwitchControlSchemes = true;
                    if (kbDevices.Count > 0)
                        _p1Input.SwitchCurrentControlScheme("Keyboard", kbDevices.ToArray());
                    else
                        _p1Input.SwitchCurrentControlScheme("Keyboard");
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[Splitscreen] Failed locking P1 to Keyboard: {ex.Message}");
                }
            }

            Plugin.Log.LogInfo("[Splitscreen] 1 controller detected -> P1: Keyboard & Mouse, P2: Controller 1");
        }
        else if (gamepadCount >= 2)
        {
            // ── 2+ Controllers: P1 = Controller 1, P2 = Controller 2 ───────
            _p2GamepadIndex = 1;
            _p2LegacyJoystickNumber = 2;

            if (_p1Input != null && Gamepad.all.Count >= 1)
            {
                try
                {
                    _p1Input.neverAutoSwitchControlSchemes = true;
                    _p1Input.SwitchCurrentControlScheme("Gamepad", new InputDevice[] { Gamepad.all[0] });
                }
                catch (Exception ex)
                {
                    Plugin.Log.LogWarning($"[Splitscreen] Failed locking P1 to Gamepad 1: {ex.Message}");
                }
            }

            Plugin.Log.LogInfo("[Splitscreen] 2+ controllers detected -> P1: Controller 1, P2: Controller 2");
        }
        else
        {
            // ── 0 Controllers: P1 = WASD, P2 = Arrow keys fallback ─────────
            _p2GamepadIndex = 0;
            _p2LegacyJoystickNumber = 1;

            Plugin.Log.LogInfo("[Splitscreen] 0 controllers detected -> P1: WASD, P2: Arrow keys");
        }

        // Spawn P2 character clone
        _p2Character = SpawnP2Character(p1Controller.gameObject);

        // Create P2 camera as a clone of P1's camera
        _p2Cam = CreateP2Camera(_p1Cam);
        if (_p2Cam != null)
        {
            P2CameraTransform = _p2Cam.transform;
            SetP2CameraMode(onFoot: true);
            StartCoroutine(FinishP2RigInit());
        }

        if (_hud == null)
            _hud = gameObject.AddComponent<SplitscreenHud>();
        _hud.SetActive(true);

        if (_p1Cam != null && _p2Cam != null && _p2Character != null)
            P2Stats.Init(_p1Cam, _p2Cam, _p2Character);

        ShareInventory();
        if (_p1Cam != null && _p2Cam != null && _p1Character != null && _p2Character != null)
            OwnModelHider.Init(_p1Cam, _p2Cam, _p1Character, _p2Character);

        if (_p2Cam != null)
            ViewModeVisibility.Init(_p2Cam);
        if (_p1Cam != null && _p2Cam != null)
            ProximityToggleVisibility.Init(_p1Cam, _p2Cam);

        P1Inside = false;
        P2Inside = false;
        _loggedMerged = false;
        ControlP2 = false;
        Raw = default;
        _active = true;
        _activeState = true;
        _p2InCar = false;
        P1Seat = CarSeat.None;
        P2Seat = CarSeat.None;
        Plugin.Log.LogInfo("[Splitscreen] Activated — splitscreen co-op running.");

        // Like the game's own start: the second player begins sitting in the truck
        if (_p1Character != null && !IsInsideBuilding(_p1Character.transform.position))
            PlaceP2InTruck();
    }

    internal void Deactivate()
    {
        if (!_active) return;
        _active = false;
        _activeState = false;
        P2Dead = false;
        P2Stats.Shutdown();
        OwnModelHider.Shutdown();
        ViewModeVisibility.Shutdown();
        ProximityToggleVisibility.Shutdown();
        _p2HasCameraHack = false;
        P1Inside = false;
        P2Inside = false;
        ControlP2 = false;
        Raw = default;

        // If in car, exit first cleanly
        if (_p2InCar && _p2Character != null)
        {
            _p2Character.transform.parent = null;
            _p2Character.transform.localScale = Vector3.one;
            SetCharacterCollisionsEnabled(_p2Character, true);
            _p2InCar = false;
            P2Seat = CarSeat.None;
        }

        P1Seat = CarSeat.None;
        P2CameraTransform = null;

        // Restore Player 1's PlayerInput
        if (_p1Input != null)
        {
            try
            {
                _p1Input.neverAutoSwitchControlSchemes = false;
                var allDevices = InputSystem.devices.ToArray();
                if (allDevices.Length > 0)
                    _p1Input.SwitchCurrentControlScheme(allDevices);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Splitscreen] Restore P1 input error: {ex.Message}");
            }
            _p1Input = null;
        }

        // Full-screen camera for P1 again
        if (_p1Cam != null)
        {
            _p1Cam.enabled = true;
            ApplyRect(_p1Rig != null ? _p1Rig.transform : _p1Cam.transform, FullRect);
            _p1Cam = null;
        }
        _p1Rig = null;
        _p1Character = null;

        if (_p2RigHolder != null)
        {
            Destroy(_p2RigHolder);
            _p2RigHolder = null;
        }
        else if (_p2Cam != null)
        {
            Destroy(_p2Cam.gameObject);
        }
        _p2Cam = null;
        _p2Rig = null;

        if (_p2Character != null)
        {
            var p2Items = GetP2ItemManager();
            if (p2Items != null && p2Items.holdingItem && p2Items.heldItemIndex >= 0)
            {
                try { p2Items.PutAway(); } catch { }
            }
            Destroy(_p2Character);
            _p2Character = null;
        }

        if (_hud != null)
            _hud.SetActive(false);

        Plugin.Log.LogInfo("[Splitscreen] Deactivated.");
    }

    private void OnDestroy() => Deactivate();

    // ── Input Query Methods for P2 ──────────────────────────────────────────

    private static bool P2UsesPad => _p2GamepadIndex < Gamepad.all.Count;

    /// <summary>P2 movement. forDriving reads the truck input instead of the on-foot input.</summary>
    public static Vector2 GetP2MovementInput(bool forDriving = false)
    {
        if (!_activeState) return Vector2.zero;

        Vector2 move = Vector2.zero;

        if (ControlP2)
        {
            move = forDriving ? Raw.DriveInput : Raw.PlayerInput;
        }
        else if (P2UsesPad)
        {
            var pad = Gamepad.all[_p2GamepadIndex];
            move = pad.leftStick.ReadValue();
            if (move.sqrMagnitude < 0.01f)
                move = pad.dpad.ReadValue();
        }
        else
        {
            // Legacy joystick
            try
            {
                string joy = $"Joystick{_p2LegacyJoystickNumber}";
                move.x = Input.GetAxis($"{joy}Axis1");
                move.y = -Input.GetAxis($"{joy}Axis2");
            }
            catch { }
        }

        if (move.magnitude < 0.15f) return Vector2.zero;
        return Vector2.ClampMagnitude(move, 1f);
    }

    public static Vector2 GetP2LookInput()
    {
        if (!_activeState) return Vector2.zero;

        Vector2 look = Vector2.zero;

        if (ControlP2)
        {
            look = Raw.CameraLook;
        }
        else if (P2UsesPad)
        {
            look = Gamepad.all[_p2GamepadIndex].rightStick.ReadValue();
        }
        else
        {
            try
            {
                string joy = $"Joystick{_p2LegacyJoystickNumber}";
                look.x = Input.GetAxis($"{joy}Axis3");
                look.y = Input.GetAxis($"{joy}Axis4");
            }
            catch { }
        }

        if (look.magnitude < 0.15f) return Vector2.zero;
        return look;
    }

    public static bool GetP2InteractPressed()
    {
        if (!_activeState) return false;
        if (ControlP2) return Raw.SelectPressed;

        if (P2UsesPad)
        {
            var pad = Gamepad.all[_p2GamepadIndex];
            if (pad.buttonSouth.wasPressedThisFrame ||   // 'A' on Xbox / 'Cross' on PS
                pad.buttonWest.wasPressedThisFrame ||    // 'X' on Xbox / 'Square' on PS
                pad.rightTrigger.wasPressedThisFrame ||   // RT / R2
                pad.rightShoulder.wasPressedThisFrame ||  // RB / R1
                pad.buttonNorth.wasPressedThisFrame)     // 'Y' / 'Triangle'
            {
                return true;
            }
        }

        // Legacy joystick buttons
        try
        {
            string joy = $"joystick {_p2LegacyJoystickNumber} button ";
            if (Input.GetKeyDown(joy + "0") || Input.GetKeyDown(joy + "1") ||
                Input.GetKeyDown(joy + "2") || Input.GetKeyDown(joy + "3") ||
                Input.GetKeyDown(joy + "5"))
            {
                return true;
            }
        }
        catch { }

        return false;
    }

    public static bool GetP2BackPressed()
    {
        if (!_activeState) return false;
        if (ControlP2) return Raw.BackPressed;

        if (P2UsesPad)
        {
            var pad = Gamepad.all[_p2GamepadIndex];
            if (pad.buttonEast.wasPressedThisFrame || pad.leftTrigger.wasPressedThisFrame) // 'B' on Xbox / 'Circle' on PS
                return true;
        }

        // Legacy joystick button 1
        try
        {
            if (Input.GetKeyDown($"joystick {_p2LegacyJoystickNumber} button 1"))
                return true;
        }
        catch { }

        return false;
    }

    /// <summary>Change-camera key (first/third person toggle) for P2.</summary>
    public static bool GetP2CameraPressed()
    {
        if (!_activeState) return false;
        if (ControlP2) { bool v = Raw.CameraPressed; Raw.CameraPressed = false; return v; }
        return P2UsesPad && Gamepad.all[_p2GamepadIndex].rightStickButton.wasPressedThisFrame;
    }

    /// <summary>Brake held (truck driving). Unlike GetP2BackPressed this is true every frame it is held.</summary>
    public static bool GetP2BrakeHeld()
    {
        if (!_activeState) return false;
        if (ControlP2) return Raw.BreakHeld;
        if (P2UsesPad)
        {
            var pad = Gamepad.all[_p2GamepadIndex];
            return pad.buttonEast.isPressed || pad.leftTrigger.isPressed;
        }
        return false;
    }

    // ── In-Game World-Space Interaction Data (Matches P1's sHUD) ─────────────

    private static int _worldFrame = -1;
    private static P2WorldInteraction? _worldCache;

    /// <summary>
    /// The prompt Player 2 should see, chosen with the same rules sItemManager/sCharacterInteraction use
    /// for Player 1: nothing inside the truck, the closest InteractionPoint (or ground item) in front of
    /// the character and within reach of the hands, drawn as its own name or icon.
    /// </summary>
    public static P2WorldInteraction? GetP2WorldInteraction()
    {
        if (!_activeState || _p2Character == null) return null;
        if (_worldFrame == Time.frameCount) return _worldCache;
        _worldFrame = Time.frameCount;
        try { _worldCache = ComputeP2WorldInteraction(); }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[Splitscreen] P2 prompt error: {ex.Message}");
            _worldCache = null;
        }
        return _worldCache;
    }

    private static P2WorldInteraction? ComputeP2WorldInteraction()
    {
        string btn = (_p2GamepadIndex < Gamepad.all.Count || CountConnectedJoysticks() > 0) ? "A" : "E";

        // P1 gets no prompt while sitting in the truck, and neither does P2
        if (_p2InCar) return null;

        var p2Guy = _p2Character!.GetComponent<sCharacterInteraction>();
        if (p2Guy == null) return null;

        // Holding a package: only the placement markers (sCharacterInteraction.DrawPlaceMarkers)
        if (p2Guy.packagePlatform != null && p2Guy.packagePlatform.activeSelf)
        {
            Transform me = _p2Character.transform;
            Vector3 origin = me.position - me.forward * 1.5f + me.up * 6f;
            if (Physics.SphereCast(origin, 0.5f, Vector3.down, out var hit, 10f, -1, QueryTriggerInteraction.Ignore)
                && Vector3.Distance(me.position, hit.point) <= 3f)
            {
                if (hit.rigidbody != null && hit.rigidbody.name == "Car")
                {
                    var car = _car ?? FindAnyObjectByType<sCarController>();
                    var p1Guy = SplitscreenHelper.FindLocalCharacterController()?.GetComponent<sCharacterInteraction>();
                    bool open = p1Guy != null ? p1Guy.tailgateOpen : p2Guy.tailgateOpen;
                    return new P2WorldInteraction
                    {
                        WorldPosition = car != null && car.payloadPivot != null ? car.payloadPivot.position : hit.point,
                        Label = open ? "place" : "open",
                        ButtonHint = btn,
                    };
                }
                return new P2WorldInteraction
                {
                    WorldPosition = hit.point,
                    ButtonHint = btn,
                    IsMarker = true,
                    IsSprite = true,
                    Sprite = new Rect(48f, 32f, 16f, 16f),
                };
            }
            return null;
        }

        var im = GetP2ItemManager();
        if (im != null && im.display != null && im.display.inventoryOpen) return null;

        var target = FindP2Target();
        if (target.Ip != null)
        {
            var ip = target.Ip;
            ip.UpdateDisplay(); // lets e.g. the tailgate point rename itself to open / close
            if (ip.useIcon)
            {
                return new P2WorldInteraction
                {
                    WorldPosition = ip.transform.position,
                    ButtonHint = btn,
                    IsSprite = true,
                    Sprite = ip.icon,
                };
            }
            if (string.IsNullOrEmpty(ip.gameObject.name)) return null;
            return new P2WorldInteraction
            {
                WorldPosition = ip.transform.position,
                Label = ip.gameObject.name,
                ButtonHint = btn,
            };
        }

        Vector3? spritePos = target.Item != null ? target.Item.transform.position
                           : target.Payload != null ? target.Payload.position
                           : (Vector3?)null;
        if (spritePos.HasValue)
        {
            return new P2WorldInteraction
            {
                WorldPosition = spritePos.Value,
                ButtonHint = btn,
                IsSprite = true,
                Sprite = new Rect(80f, 32f, 12f, 12f),
            };
        }

        return null;
    }

    private struct P2Target
    {
        public InteractionPoint? Ip;
        public ItemID? Item;
        public Transform? Payload;
    }

    private static sItemManager? GetP2ItemManager() =>
        _p2Character != null ? _p2Character.GetComponentInChildren<sItemManager>(true) : null;

    /// <summary>
    /// Same selection as sItemManager.FindClosestInteraction / FindClosestItem / InteractionIsCloser
    /// for Player 2's character: in front of the character, valid for the held item, closest to the
    /// hands, and within interactRange.
    /// </summary>
    private static P2Target FindP2Target()
    {
        var result = new P2Target();
        if (_p2Character == null) return result;

        var im = GetP2ItemManager();
        Transform me = _p2Character.transform;
        Vector3 handPos = im != null && im.hands != null ? im.hands.position : me.position;
        float range = im != null ? im.interactRange : 1.5f;
        bool holding = im != null && im.holdingItem;
        int heldIndex = im != null ? im.heldItemIndex : 0;

        bool Looking(Vector3 point)
        {
            Vector3 v = point - me.position;
            v.y = 0f;
            return Vector3.Dot(-me.forward, v.normalized) > 0f;
        }

        InteractionPoint? ip = null;
        foreach (var c in FindObjectsByType<InteractionPoint>(FindObjectsSortMode.None))
        {
            if (c == null || !c.enabled || !Looking(c.transform.position)) continue;
            bool heldOk = !holding || c.ignoreItems || (c.validItemInteractions != null && c.validItemInteractions.Contains(heldIndex));
            bool freeOk = holding || c.validItemInteractions == null || c.validItemInteractions.Count <= 0;
            if (!heldOk || !freeOk) continue;
            if (ip == null || Vector3.Distance(handPos, c.transform.position) < Vector3.Distance(handPos, ip.transform.position))
                ip = c;
        }

        ItemID? item = null;
        foreach (var it in FindObjectsByType<ItemID>(FindObjectsSortMode.None))
        {
            if (it == null || !Looking(it.transform.position)) continue;
            if (item == null || Vector3.Distance(handPos, it.transform.position) < Vector3.Distance(handPos, item.transform.position))
                item = it;
        }

        bool ipCloser = ip != null &&
            (item == null || holding || Vector3.Distance(handPos, ip.transform.position) < Vector3.Distance(handPos, item.transform.position));

        if (ipCloser)
        {
            if (Vector3.Distance(handPos, ip!.transform.position) < range)
                result.Ip = ip;
        }
        else if (!holding && item != null && Vector3.Distance(handPos, item.transform.position) < range)
        {
            result.Item = item;
        }

        // A package P2 dropped has no pick-up point of its own (the game only makes one for P1)
        if (result.Ip == null && result.Item == null && !holding)
        {
            foreach (var pl in FindObjectsByType<Transform>(FindObjectsSortMode.None))
            {
                if (pl == null || pl.name != "Payload" || !pl.gameObject.activeInHierarchy) continue;
                if (pl.parent != null && pl.parent.name.Contains("package")) continue;
                if (Vector3.Distance(handPos, pl.position) < range + 0.5f && Looking(pl.position))
                {
                    result.Payload = pl;
                    break;
                }
            }
        }

        return result;
    }

    // ── P2 Interaction & Truck Entry / Exit ─────────────────────────────────

    // ── Death (warmth reaches 0) ────────────────────────────────────────────
    //
    // Player 1: the game's DeathManager reloads the whole world when P1's warmth hits 0 (see
    // DeathManagerUpdatePatch, which keeps that for "both down" only). Player 2 gets the same collapse
    // here. When only one player is down, the other keeps playing in the same world and the one who
    // died gets back up next to the truck after the death time, warm again.

    internal static bool P2Dead { get; private set; }
    private static float _p2DeathTimer;
    internal const float DeathSeconds = 16f;
    private static DeathManager? _deathManager;
    private static float _nextDeathManagerScan;

    internal static DeathManager? GetDeathManager()
    {
        if (_deathManager == null && Time.unscaledTime >= _nextDeathManagerScan)
        {
            _nextDeathManagerScan = Time.unscaledTime + 2f;
            _deathManager = FindFirstObjectByType<DeathManager>();
        }
        return _deathManager;
    }

    internal static bool P1IsDying => GetDeathManager() != null && GetDeathManager()!.dying;

    /// <summary>A spot beside the truck, on the driver's side for one player and the passenger's for the other.</summary>
    internal static Vector3 SpawnPointNearCar(bool driverSide)
    {
        var car = _car ?? FindAnyObjectByType<sCarController>();
        if (car == null) return P1CharacterObject != null ? P1CharacterObject.transform.position + Vector3.up : Vector3.up;
        return car.transform.position + car.transform.right * (driverSide ? 1.5f : -1.5f) + Vector3.up * 0.4f;
    }

    /// <summary>
    /// The game's "Dead" pose is a dead end: it never plays on after a respawn (the game reloads the
    /// whole world instead). Rebind puts the animator back in its starting state so the character stands up.
    /// </summary>
    internal static void StandUp(Animator? anim)
    {
        if (anim == null) return;
        anim.SetBool("Dead", false);
        anim.Rebind();
        anim.Update(0f);
        anim.SetBool("Move", false);
    }

    /// <summary>Puts Player 2 in the truck, on the passenger side (the driver's seat if P1 sits there).</summary>
    internal static void PlaceP2InTruck()
    {
        var car = _car ?? FindAnyObjectByType<sCarController>();
        if (_p2Character == null || car == null || _p2InCar) return;
        var p2Guy = _p2Character.GetComponent<sCharacterInteraction>();
        if (p2Guy == null) return;

        // EnterTruck picks the seat from which side the character is standing on
        var cc = _p2Character.GetComponent<sCharacterController>();
        Vector3 doorSide = car.transform.position - car.transform.right * 1.5f + Vector3.up * 0.4f;
        if (cc != null) cc.MoveTo(doorSide);
        else _p2Character.transform.position = doorSide;

        EnterTruck(p2Guy, car);
    }

    private void UpdateP2Death()
    {
        if (_p2Character == null) return;
        var p2Guy = _p2Character.GetComponent<sCharacterInteraction>();
        var cc = _p2Character.GetComponent<sCharacterController>();

        if (!P2Dead)
        {
            if (!P2Stats.Active || P2Stats.Temperature > 0.001f) return;

            if (_p2InCar && p2Guy != null)
                ExitTruck(p2Guy, _car ?? FindAnyObjectByType<sCarController>());

            P2Dead = true;
            _p2DeathTimer = DeathSeconds;
            if (cc != null)
            {
                if (cc.animator != null)
                {
                    cc.animator.SetBool("Move", false);
                    cc.animator.SetBool("Dead", true);
                }
                cc.velocity = Vector3.zero;
                cc.enabled = false;
            }
            Plugin.Log.LogInfo("[Splitscreen] Player 2 froze to death.");
            return;
        }

        _p2DeathTimer -= Time.unscaledDeltaTime;
        if (_p2DeathTimer > 0f) return;

        // Both down: the game reloads the world (P1's DeathManager finishes its own countdown)
        if (P1IsDying)
        {
            _p2DeathTimer = 0.25f;
            return;
        }

        if (cc != null)
        {
            StandUp(cc.animator);
            cc.enabled = true;
            cc.MoveTo(SpawnPointNearCar(driverSide: false));
            cc.velocity = Vector3.zero;
        }
        if (_p2Rig != null) _p2Rig.transform.position = _p2Character.transform.position;
        P2Stats.ResetWarmth();
        P2Dead = false;
        Plugin.Log.LogInfo("[Splitscreen] Player 2 is back up.");
        PlaceP2InTruck();
    }

    // ── Shared inventory ────────────────────────────────────────────────────

    private static bool _inventoryLogged;

    private static sItemManager? GetP1ItemManager() =>
        _instance != null && _instance._p1Character != null
            ? _instance._p1Character.GetComponentInChildren<sItemManager>(true)
            : null;

    /// <summary>
    /// Both players use ONE item list: P2's item manager points at P1's list, so whatever either player
    /// stores or takes is the same stash (and P1's inventory screen shows it). P2 does not save it
    /// separately, P1's manager keeps writing the save.
    /// </summary>
    private static void ShareInventory()
    {
        _inventoryLogged = false;
        LinkInventory();
    }

    private static void LinkInventory()
    {
        var p1 = GetP1ItemManager();
        var p2 = GetP2ItemManager();
        if (p1 == null || p2 == null || p1 == p2 || p1.inventory == null) return;

        if (p2.useSaveData) p2.useSaveData = false;
        p2.inventoryBackup = null;
        if (!ReferenceEquals(p2.inventory, p1.inventory))
        {
            p2.inventory = p1.inventory;
            if (!_inventoryLogged)
            {
                _inventoryLogged = true;
                Plugin.Log.LogInfo($"[Splitscreen] Inventory shared between P1 and P2 ({p1.inventory.Count} items).");
            }
        }
    }

    // ── Building doors (sTeleporter) for Player 2 ───────────────────────────

    private static float _p2TeleportCooldown;
    private static sTeleporter[] _teleporters = System.Array.Empty<sTeleporter>();
    private static float _nextTeleporterScan;

    /// <summary>
    /// What sTeleporter.Teleport/DoTeleport do for Player 1 (they use the first sCharacterInteraction
    /// in the scene), done for Player 2: put away the held item, move the character and its camera rig
    /// to the linked door, play the door sound.
    /// </summary>
    internal static void TeleportP2(sTeleporter tp)
    {
        if (_p2Character == null || tp == null || tp.link == null) return;
        if (Time.time < _p2TeleportCooldown) return;
        _p2TeleportCooldown = Time.time + 0.6f;

        var im = GetP2ItemManager();
        if (im != null && im.holdingItem)
            im.PutAway();

        Vector3 dest = tp.link.transform.position;
        PrepareAreaAround(dest);
        var cc = _p2Character.GetComponent<sCharacterController>();
        if (cc != null)
        {
            cc.MoveTo(dest);
            cc.velocity = Vector3.zero;
        }
        else
        {
            _p2Character.transform.position = dest;
        }

        if (_p2Rig != null)
            _p2Rig.transform.position = dest;

        if (_instance != null) _instance.StartCoroutine(_instance.DumpWorldLater(1.5f, "after P2 door '" + tp.interactText + "'"));
        tp.link.PlaySFX();
        Plugin.Log.LogInfo($"[Splitscreen] Player 2 used door '{tp.interactText}' -> {tp.link.name}.");
    }

    /// <summary>
    /// Switches on the area around a point right now (what ProximityToggle would do one frame later),
    /// so a player teleporting in does not drop through a floor whose colliders are still off.
    /// </summary>
    private static void PrepareAreaAround(Vector3 point)
    {
        var hasToggled = typeof(ProximityToggle).GetField("hasToggled", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        foreach (var t in FindObjectsByType<ProximityToggle>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (t == null || Vector3.Distance(t.transform.position, point) >= t.distanceThreashold) continue;
            foreach (var go in t.toEnable) if (go != null) go.SetActive(true);
            hasToggled?.SetValue(t, true);
        }
    }

    // ── Diagnostics: what the world looks like around the players ───────────

    private static string PathOf(Transform t)
    {
        string path = t.name;
        for (int i = 0; i < 4 && t.parent != null; i++) { t = t.parent; path = t.name + "/" + path; }
        return path;
    }

    private static string Names(System.Collections.Generic.List<GameObject> list)
    {
        var parts = new System.Collections.Generic.List<string>();
        foreach (var go in list) parts.Add(go != null ? go.name + (go.activeSelf ? "(on)" : "(off)") : "null");
        return string.Join(",", parts);
    }

    private System.Collections.IEnumerator DumpWorldLater(float delay, string tag)
    {
        yield return new WaitForSeconds(delay);
        DumpWorld(tag);
    }

    internal void DumpWorld(string tag)
    {
        try
        {
            Vector3 p1 = _p1Character != null ? _p1Character.transform.position : Vector3.zero;
            Vector3 p2 = _p2Character != null ? _p2Character.transform.position : Vector3.zero;
            Plugin.Log.LogInfo($"[Splitscreen][world {tag}] P1={p1} P2={p2}");

            foreach (var t in FindObjectsByType<ProximityToggle>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (t == null) continue;
                float d1 = Vector3.Distance(t.transform.position, p1), d2 = Vector3.Distance(t.transform.position, p2);
                if (Mathf.Min(d1, d2) > t.distanceThreashold * 3f) continue; // only the ones that matter right now
                foreach (var go in t.toEnable)
                {
                    if (go == null) continue;
                    var types = new System.Collections.Generic.List<string>();
                    foreach (var c in go.GetComponents<Component>()) if (c != null) types.Add(c.GetType().Name);
                    Plugin.Log.LogInfo($"[Splitscreen][world {tag}]   enable '{go.name}' children={go.transform.childCount} components=[{string.Join(",", types)}]");
                }
                Plugin.Log.LogInfo($"[Splitscreen][world {tag}] toggle '{PathOf(t.transform)}' target='{(t.target != null ? PathOf(t.target) : "null")}' " +
                                   $"thr={t.distanceThreashold:0.#} d1={d1:0.#} d2={d2:0.#} enable=[{Names(t.toEnable)}] disable=[{Names(t.toDisable)}]");
            }

            foreach (var n in FindObjectsByType<sShopNPC>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                Plugin.Log.LogInfo($"[Splitscreen][world {tag}] sShopNPC '{PathOf(n.transform)}' activeInHierarchy={n.gameObject.activeInHierarchy} d1={Vector3.Distance(n.transform.position, p1):0.#} d2={Vector3.Distance(n.transform.position, p2):0.#} renderers={n.GetComponentsInChildren<Renderer>(true).Length}");
            foreach (var n in FindObjectsByType<sTransactionNPC>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                Plugin.Log.LogInfo($"[Splitscreen][world {tag}] sTransactionNPC '{PathOf(n.transform)}' activeInHierarchy={n.gameObject.activeInHierarchy} d1={Vector3.Distance(n.transform.position, p1):0.#} d2={Vector3.Distance(n.transform.position, p2):0.#} renderers={n.GetComponentsInChildren<Renderer>(true).Length}");

            foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                if (l.type != LightType.Directional) continue;
                Plugin.Log.LogInfo($"[Splitscreen][world {tag}] directional light '{PathOf(l.transform)}' enabled={l.enabled} intensity={l.intensity:0.##} mask={l.cullingMask}");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[Splitscreen] DumpWorld failed: {ex.Message}");
        }
    }

    /// <summary>Leaving a building while carrying a package is automatic for P1 (sTeleporter.CheckAutoTeleport).</summary>
    private static void CheckP2AutoTeleport()
    {
        if (_p2Character == null || _p2InCar) return;
        var p2Guy = _p2Character.GetComponent<sCharacterInteraction>();
        if (p2Guy == null || p2Guy.packagePlatform == null || !p2Guy.packagePlatform.activeSelf) return;

        if (Time.unscaledTime >= _nextTeleporterScan)
        {
            _nextTeleporterScan = Time.unscaledTime + 2f;
            _teleporters = FindObjectsByType<sTeleporter>(FindObjectsSortMode.None);
        }

        Vector3 pos = _p2Character.transform.position;
        foreach (var tp in _teleporters)
        {
            if (tp == null || tp.interactText != "leave") continue;
            Vector3 v = pos - tp.transform.position;
            float forward = Vector3.Project(v, tp.transform.forward).magnitude;
            float right = Vector3.Project(v, tp.transform.right).magnitude;
            if (v.magnitude < 1f && right < 1f && forward < 0.5f)
            {
                TeleportP2(tp);
                return;
            }
        }
    }

    internal static void HandleP2Interaction()
    {
        if (_p2Character == null) return;
        var p2Guy = _p2Character.GetComponent<sCharacterInteraction>();
        if (p2Guy == null) return;

        var car = _car ?? FindAnyObjectByType<sCarController>();
        var p1Guy = SplitscreenHelper.FindLocalCharacterController()?.GetComponent<sCharacterInteraction>();

        // 1. If P2 is currently inside the truck -> Exit truck!
        if (_p2InCar)
        {
            ExitTruck(p2Guy, car);
            return;
        }

        Vector3 p2Pos = _p2Character.transform.position;

        // 2. If P2 is holding a package -> Place package!
        if (p2Guy.packagePlatform != null && p2Guy.packagePlatform.activeSelf && p2Guy.packagePoint != null && p2Guy.packagePoint.childCount > 0)
        {
            PlacePackageForP2(p2Guy, car, p1Guy);
            return;
        }

        // 3. Same target the prompt shows (and the game would pick for P1)
        var target = FindP2Target();

        if (target.Ip != null)
        {
            var bestIP = target.Ip;
            string cleanName = bestIP.gameObject.name.ToLowerInvariant();

            // Truck door -> enter truck!
            if (bestIP.callback == "ToggleActive" || cleanName.Contains("get in"))
            {
                if (car != null)
                {
                    EnterTruck(p2Guy, car);
                    return;
                }
            }

            // Tailgate -> toggle tailgate!
            if (bestIP.callback == "ToggleTailgate" || cleanName.Contains("open") || cleanName.Contains("close"))
            {
                if (p1Guy != null)
                    p1Guy.ToggleTailgate();
                else
                    p2Guy.ToggleTailgate();
                return;
            }

            // Pick up from truck bed
            if (bestIP.callback == "PickupFromTruck")
            {
                if (p1Guy != null)
                    PickupFromTruckForP2(p2Guy, p1Guy);
                return;
            }

            // Pick up package from ground
            if (bestIP.callback == "PickupPackage" || cleanName.Contains("pick up"))
            {
                var payload = bestIP.transform.parent != null ? bestIP.transform.parent.gameObject : bestIP.gameObject;
                PickupPayloadForP2(p2Guy, payload);
                return;
            }

            // Building doors ("enter" / "leave"): the game's sTeleporter only knows Player 1
            if (bestIP.callback == "Teleport" && bestIP.listener != null)
            {
                var tp = bestIP.listener.GetComponent<sTeleporter>();
                if (tp != null)
                {
                    TeleportP2(tp);
                    return;
                }
            }

            // Any other InteractionPoint (NPC, Campfire, Bed, Shop, etc.)
            Plugin.Log.LogInfo($"[Splitscreen] P2 interacting with {bestIP.gameObject.name} ({bestIP.callback})");
            bestIP.DoAction();
            return;
        }

        if (target.Payload != null)
        {
            PickupPayloadForP2(p2Guy, target.Payload.gameObject);
            return;
        }

        if (target.Item != null)
        {
            PickupItemForP2(p2Guy, target.Item);
        }
    }

    internal static void HandleP2Back()
    {
        if (_p2Character == null) return;
        var p2Guy = _p2Character.GetComponent<sCharacterInteraction>();
        if (p2Guy == null) return;

        // 1. If holding package -> drop on ground
        if (p2Guy.packagePlatform != null && p2Guy.packagePlatform.activeSelf && p2Guy.packagePoint != null && p2Guy.packagePoint.childCount > 0)
        {
            var package = p2Guy.packagePoint.GetChild(0).gameObject;
            Vector3 dropPos = _p2Character.transform.position - _p2Character.transform.forward * 1.0f + Vector3.up * 0.3f;
            RaycastHit hit;
            if (Physics.Raycast(dropPos + Vector3.up * 2f, Vector3.down, out hit, 10f, ~0, QueryTriggerInteraction.Ignore))
            {
                dropPos = hit.point;
            }

            package.transform.parent = null;
            package.transform.position = dropPos;
            package.name = "Payload";
            package.SetActive(true);

            foreach (var col in package.GetComponentsInChildren<Collider>())
                col.enabled = true;

            p2Guy.packagePlatform.SetActive(false);
            p2Guy.SendMessage("Package", true, SendMessageOptions.DontRequireReceiver);
            Plugin.Log.LogInfo("[Splitscreen] Player 2 dropped package!");
            return;
        }

        // 2. If holding item -> put away
        var p2ItemManager = p2Guy.GetComponentInChildren<sItemManager>();
        if (p2ItemManager != null && p2ItemManager.holdingItem)
        {
            p2ItemManager.BackButtonPressed();
            Plugin.Log.LogInfo("[Splitscreen] Player 2 put away item!");
        }
    }

    private static void EnterTruck(sCharacterInteraction p2Guy, sCarController car)
    {
        _p2InCar = true;

        // Play truck door sound
        car.SendMessage("DoorOpen", SendMessageOptions.DontRequireReceiver);

        // Disable all collisions to prevent truck physics glitching
        SetCharacterCollisionsEnabled(p2Guy.gameObject, false);
        IgnoreCollisionsWithCar(p2Guy.gameObject, car, true);

        // Parent P2 to car so P2 moves smoothly with the truck at any speed
        p2Guy.transform.parent = car.transform;

        float dot = Vector3.Dot(p2Guy.transform.position - car.transform.position, car.transform.right);
        CarSeat seat = dot > 0 ? CarSeat.Driver : CarSeat.Passenger;
        
        // Prevent seat stealing
        if (seat == CarSeat.Driver && P1Seat == CarSeat.Driver) seat = CarSeat.Passenger;
        if (seat == CarSeat.Passenger && P1Seat == CarSeat.Passenger) seat = CarSeat.Driver;
        
        P2Seat = seat;

        p2Guy.transform.localPosition = GetSeatedLocalPosition(seat);
        p2Guy.transform.localRotation = Quaternion.Euler(0, 180, 0);
        p2Guy.transform.localScale = SeatedCharacterScale;

        // P2 camera switches to the truck chase view
        SetP2CameraMode(onFoot: false, car);

        Plugin.Log.LogInfo($"[Splitscreen] Player 2 entered the truck as {seat}!");
    }

    private static void ExitTruck(sCharacterInteraction p2Guy, sCarController? car)
    {
        _p2InCar = false;
        CarSeat seat = P2Seat;
        P2Seat = CarSeat.None;

        // Unparent from car and restore original scale
        p2Guy.transform.parent = null;
        p2Guy.transform.localScale = Vector3.one;

        if (car != null)
        {
            car.SendMessage("DoorOpen", SendMessageOptions.DontRequireReceiver);
            // Position on the side they exited
            float sign = seat == CarSeat.Driver ? 1.5f : -1.5f;
            Vector3 exitPos = car.transform.position + car.transform.right * sign + Vector3.up * 0.3f;
            p2Guy.transform.position = exitPos;
            p2Guy.transform.rotation = car.transform.rotation;

            IgnoreCollisionsWithCar(p2Guy.gameObject, car, false);
        }

        // Re-enable collisions
        SetCharacterCollisionsEnabled(p2Guy.gameObject, true);

        // P2 camera switches back to the on-foot view
        SetP2CameraMode(onFoot: true, car);

        Plugin.Log.LogInfo($"[Splitscreen] Player 2 exited the truck!");
    }

    private static void PlacePackageForP2(sCharacterInteraction p2Guy, sCarController? car, sCharacterInteraction? p1Guy)
    {
        try
        {
            if (p2Guy.packagePoint == null || p2Guy.packagePoint.childCount == 0)
            {
                if (p2Guy.packagePlatform != null)
                    p2Guy.packagePlatform.SetActive(false);
                return;
            }

            var package = p2Guy.packagePoint.GetChild(0).gameObject;

            // Check if looking at or near truck bed
            bool placedInTruck = false;
            if (car != null)
            {
                Vector3 bedPos = car.transform.position - car.transform.forward * 1.5f;
                float distToBed = Vector3.Distance(p2Guy.transform.position, bedPos);
                if (distToBed < 3.2f)
                {
                    // Place in truck bed
                    package.transform.parent = (p1Guy != null && p1Guy.packagePoint != null) ? p1Guy.packagePoint : car.transform;
                    package.transform.localPosition = Vector3.zero;
                    package.name = "Placed In Truck";
                    package.SetActive(false);
                    car.ResetFreeze();

                    if (p1Guy != null)
                    {
                        var bedIpField = typeof(sCharacterInteraction).GetField("truckBedInteraction", AnyField);
                        var bedIp = bedIpField?.GetValue(p1Guy) as InteractionPoint;
                        if (bedIp != null)
                        {
                            bedIp.name = "close";
                            bedIp.useIcon = false;
                            bedIp.callback = "ToggleTailgate";
                        }
                    }

                    placedInTruck = true;
                    Plugin.Log.LogInfo("[Splitscreen] P2 placed package in truck!");
                }
            }

            if (!placedInTruck)
            {
                // Place on ground in front of P2 (facing direction is -forward)
                Vector3 dropPos = p2Guy.transform.position - p2Guy.transform.forward * 1.2f + Vector3.up * 0.3f;
                RaycastHit hit;
                if (Physics.Raycast(dropPos + Vector3.up * 2f, Vector3.down, out hit, 10f, ~0, QueryTriggerInteraction.Ignore))
                {
                    dropPos = hit.point;
                }

                package.transform.parent = null;
                package.transform.position = dropPos;
                package.name = "Payload";
                package.SetActive(true);

                foreach (var col in package.GetComponentsInChildren<Collider>())
                    col.enabled = true;

                Plugin.Log.LogInfo("[Splitscreen] P2 placed package on ground!");
            }

            if (p2Guy.packagePlatform != null)
                p2Guy.packagePlatform.SetActive(false);

            p2Guy.SendMessage("Package", true, SendMessageOptions.DontRequireReceiver);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[Splitscreen] PlacePackageForP2 error: {ex.Message}");
        }
    }

    private static void PickupPayloadForP2(sCharacterInteraction p2Guy, GameObject payload)
    {
        try
        {
            payload.transform.parent = p2Guy.packagePoint;
            payload.transform.localPosition = Vector3.zero;
            payload.transform.localRotation = Quaternion.identity;

            foreach (var col in payload.GetComponentsInChildren<Collider>())
                col.enabled = false;

            if (p2Guy.packagePlatform != null)
                p2Guy.packagePlatform.SetActive(true);

            p2Guy.SendMessage("Package", false, SendMessageOptions.DontRequireReceiver);

            var p2Cc = p2Guy.GetComponent<sCharacterController>();
            if (p2Cc != null && p2Cc.animator != null)
                p2Cc.animator.Play("pickup");

            Plugin.Log.LogInfo("[Splitscreen] Player 2 picked up package!");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[Splitscreen] Pickup error: {ex.Message}");
        }
    }

    private static void PickupFromTruckForP2(sCharacterInteraction p2Guy, sCharacterInteraction p1Guy)
    {
        try
        {
            if (p1Guy.packagePoint == null || p1Guy.packagePoint.childCount == 0) return;

            var payload = p1Guy.packagePoint.GetChild(0).gameObject;
            payload.SetActive(true);
            payload.name = "Payload";

            PickupPayloadForP2(p2Guy, payload);

            var car = _car ?? FindAnyObjectByType<sCarController>();
            if (car != null) car.ResetFreeze();

            var bedIpField = typeof(sCharacterInteraction).GetField("truckBedInteraction", AnyField);
            var bedIp = bedIpField?.GetValue(p1Guy) as InteractionPoint;
            if (bedIp != null)
            {
                bedIp.name = "close";
                bedIp.useIcon = false;
                bedIp.callback = "ToggleTailgate";
            }

            Plugin.Log.LogInfo("[Splitscreen] Player 2 picked up package from truck!");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[Splitscreen] PickupFromTruckForP2 error: {ex.Message}");
        }
    }

    private static void PickupItemForP2(sCharacterInteraction p2Guy, ItemID item)
    {
        try
        {
            var p2ItemManager = p2Guy.GetComponentInChildren<sItemManager>();
            if (p2ItemManager != null)
            {
                p2ItemManager.EquipItem(item.id);
                Destroy(item.gameObject);
                Plugin.Log.LogInfo($"[Splitscreen] Player 2 picked up item ID {item.id}!");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[Splitscreen] PickupItem error: {ex.Message}");
        }
    }

    // ── Movement Execution for P2 (Harmony Hook) ────────────────────────────

    internal static void UpdateP2CharacterController(sCharacterController cc)
    {
        if (_p2InCar) return;

        Vector2 input = GetP2MovementInput();

        // Calculate world movement relative to Player 2's camera facing
        Vector3 fwd = P2CameraTransform != null ? P2CameraTransform.forward : cc.transform.forward;
        Vector3 right = P2CameraTransform != null ? P2CameraTransform.right : cc.transform.right;
        fwd.y = 0f;
        right.y = 0f;
        fwd.Normalize();
        right.Normalize();

        Vector3 moveDir = (fwd * input.y + right * input.x);
        moveDir = Vector3.ClampMagnitude(moveDir, 1f);

        Vector3 accel = Vector3.down * cc.gravity;
        accel += moveDir * cc.acceleration;

        cc.velocity += accel * 0.5f * Time.deltaTime;

        var charCtrl = cc.GetComponent<CharacterController>();
        if (charCtrl != null && charCtrl.enabled)
        {
            charCtrl.Move(cc.velocity * Time.deltaTime);
        }

        cc.velocity += accel * 0.5f * Time.deltaTime;

        float decel = cc.deceleration;
        if (input.magnitude < 0.1f)
        {
            decel *= 4f;
        }
        cc.velocity = Vector3.Lerp(cc.velocity, new Vector3(0f, cc.velocity.y, 0f), Time.deltaTime * decel);

        // Look in movement direction so Player 2 faces forward when walking
        if (moveDir.sqrMagnitude > 0.0025f)
        {
            Quaternion targetRot = Quaternion.LookRotation(-moveDir, Vector3.up);
            cc.transform.rotation = Quaternion.Slerp(cc.transform.rotation, targetRot, Time.deltaTime * 15f);
        }

        if (cc.animator != null)
        {
            cc.animator.SetBool("Move", moveDir.magnitude > 0.01f);
        }

        // Safety respawn if fell through world
        if (cc.transform.position.y < -50f)
        {
            var p1 = SplitscreenHelper.FindLocalCharacterController();
            if (p1 != null)
            {
                if (charCtrl != null) charCtrl.enabled = false;
                cc.transform.position = p1.transform.position + Vector3.up * 1f;
                if (charCtrl != null) charCtrl.enabled = true;
                cc.velocity = Vector3.zero;
            }
        }
    }

    // ── Safe Init for P2 CharacterInteraction (Harmony Hook) ────────────────

    internal static void InitP2CharacterInteraction(sCharacterInteraction p2Guy)
    {
        try
        {
            var cc = p2Guy.GetComponent<sCharacterController>();
            var itemManager = p2Guy.GetComponentInChildren<sItemManager>();
            var car = _car ?? FindAnyObjectByType<sCarController>();

            p2Guy.car = car;
            var ccField = typeof(sCharacterInteraction).GetField("cc", AnyField);
            if (ccField != null && cc != null) ccField.SetValue(p2Guy, cc);

            var itemField = typeof(sCharacterInteraction).GetField("itemManager", AnyField);
            if (itemField != null && itemManager != null) itemField.SetValue(p2Guy, itemManager);

            var activeField = typeof(sCharacterInteraction).GetField("active", AnyField);
            if (activeField != null) activeField.SetValue(p2Guy, true);

            // Clear cam and hud references so P2 does not modify Player 1's camera or HUD
            var camField = typeof(sCharacterInteraction).GetField("cam", AnyField);
            if (camField != null) camField.SetValue(p2Guy, null);

            var hudField = typeof(sCharacterInteraction).GetField("hud", AnyField);
            if (hudField != null) hudField.SetValue(p2Guy, null);

            p2Guy.CancelInvoke();

            Plugin.Log.LogInfo("[Splitscreen] Initialized P2 character interaction cleanly.");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[Splitscreen] InitP2CharacterInteraction error: {ex.Message}");
        }
    }

    // ── Camera & Character Spawning ─────────────────────────────────────────

    private static Camera? FindMainCamera()
    {
        var cam = Camera.main;
        if (cam != null) return cam;

        var all = FindObjectsByType<Camera>(FindObjectsSortMode.None);
        foreach (var c in all)
        {
            if (!c.enabled || !c.gameObject.activeInHierarchy) continue;
            if (c.targetTexture != null) continue;
            return c;
        }
        return null;
    }

    private Camera CreateP2Camera(Camera source)
    {
        // Clone the whole rig (sCameraController + Camera), not only the Camera, otherwise
        // nothing ever moves the clone and it stays frozen where P1's camera was.
        GameObject original = _p1Rig != null ? _p1Rig.gameObject : source.gameObject;

        // sCameraController.Start() does `transform.parent = transform.parent.parent`,
        // so the clone needs two ancestors or Start throws and the rig never initialises.
        var outer = new GameObject("EasyDeliveryCoLanCoop.P2CameraHolderOuter");
        var inner = new GameObject("EasyDeliveryCoLanCoop.P2CameraHolder");
        inner.transform.SetParent(outer.transform, false);
        _p2RigHolder = outer;

        var p2Go = Instantiate(original, original.transform.position, original.transform.rotation, inner.transform);
        p2Go.name = "EasyDeliveryCoLanCoop.P2Camera";
        p2Go.SetActive(true);

        // Remove duplicate AudioListeners so Unity does not report two listeners
        foreach (var al in p2Go.GetComponentsInChildren<AudioListener>(includeInactive: true))
            Destroy(al);


        _p2Rig = p2Go.GetComponent<sCameraController>();
        var p2Cam = _p2Rig != null && _p2Rig.cam != null ? _p2Rig.cam : p2Go.GetComponentInChildren<Camera>(true);

        foreach (var c in p2Go.GetComponentsInChildren<Camera>(includeInactive: true))
            c.depth += 1;

        // Start the rig next to P2 so it does not fly across the map while lerping in
        if (_p2Character != null)
            p2Go.transform.position = _p2Character.transform.position;

        bool vertical = Plugin.SplitscreenOrientation?.Value != "Horizontal";
        _p1Cam!.rect = vertical ? P1RectV : P1RectH;
        ApplyRect(p2Go.transform, vertical ? P2RectV : P2RectH);

        Plugin.Log.LogInfo($"[Splitscreen] P2 camera rig created (controller={_p2Rig != null}, cam={p2Cam.name}).");
        return p2Cam;
    }

    private static Transform? FindChildByName(Transform root, string name)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
            if (t.name == name) return t;
        return null;
    }

    /// <summary>Points P2's camera rig at P2 on foot (fixed view) or at the truck (chase view).</summary>
    private static void SetP2CameraMode(bool onFoot, sCarController? car = null)
    {
        if (_p2Rig == null) return;

        _p2Rig.fixedPerspective = onFoot;
        _p2Rig.camDistance = _camBaseDistance * (onFoot ? 2f : 1f);

        if (onFoot)
        {
            Transform? pivot = _p2Character != null ? FindChildByName(_p2Character.transform, "camPivot") : null;
            _p2Rig.target = pivot != null ? pivot : _p2Character != null ? _p2Character.transform : _p2Rig.target;
        }
        else
        {
            _p2Rig.target = _p2Rig.carTarget != null ? _p2Rig.carTarget : (car != null ? car.transform : _p2Rig.target);
            if (car != null)
                _p2Rig.targetFacing = new Vector3(car.transform.forward.x, 0f, car.transform.forward.z).normalized;
        }
    }

    /// <summary>
    /// The clone's Start() snapshots whatever pose P1's camera had at that moment (e.g. the
    /// 35 degree on-foot pitch). Copy P1's original snapshot values over it after Start ran.
    /// </summary>
    private System.Collections.IEnumerator FinishP2RigInit()
    {
        yield return null;
        yield return null;

        if (_p1Rig == null || _p2Rig == null) yield break;
        string[] fields = { "camEulerAngles", "camDirection", "camStartingDistance", "startingRotationSpeed",
                            "maxFixedCameraOffset", "horizontalSpeedBackup", "fov" };
        foreach (var name in fields)
        {
            try
            {
                var f = typeof(sCameraController).GetField(name, AnyField);
                if (f != null) f.SetValue(_p2Rig, f.GetValue(_p1Rig));
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[Splitscreen] P2 rig field copy '{name}' failed: {ex.Message}");
            }
        }
        Plugin.Log.LogInfo("[Splitscreen] P2 camera rig initialised from P1 rig.");

        _p2HasCameraHack = false;
        foreach (var h in FindObjectsByType<FirstPersonCameraHack>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            bool isP2 = h.realCamera == _p2Rig;
            _p2HasCameraHack |= isP2;
            Plugin.Log.LogInfo($"[Splitscreen] FirstPersonCameraHack '{h.name}' realCamera='{(h.realCamera != null ? h.realCamera.name : "null")}' belongsToP2={isP2} " +
                               $"pivot={(h.realCamera != null && h.realCamera.firstPersonPivot != null ? h.realCamera.firstPersonPivot.name : "null")}");
        }
        Plugin.Log.LogInfo($"[Splitscreen] P2 first-person toggle: {(_p2HasCameraHack ? "game script" : "built-in fallback")}.");
    }

    private static GameObject? SpawnP2Character(GameObject source)
    {
        try
        {
            var clone = Instantiate(source);
            clone.name = "EasyDeliveryCoLanCoop.P2Character";
            clone.hideFlags = HideFlags.DontSave;

            // Spawn beside passenger door of car if available, or beside P1
            Vector3 spawnPos;
            if (_car != null && !IsInsideBuilding(source.transform.position))
            {
                spawnPos = _car.transform.position - _car.transform.right * 1.5f + Vector3.up * 0.2f;
            }
            else
            {
                spawnPos = source.transform.position + source.transform.right * 2f;
            }
            clone.transform.position = spawnPos;
            clone.transform.rotation = source.transform.rotation;

            SplitscreenHelper.DisableGameSingletons(clone);

            // Ensure CharacterController & renderers are active and enabled
            clone.SetActive(true);
            foreach (var r in clone.GetComponentsInChildren<Renderer>(includeInactive: true))
                r.enabled = true;

            var charCtrl = clone.GetComponent<CharacterController>();
            if (charCtrl != null) charCtrl.enabled = true;

            var p2Cc = clone.GetComponent<sCharacterController>();
            if (p2Cc != null) p2Cc.enabled = true;

            var p2Guy = clone.GetComponent<sCharacterInteraction>();
            if (p2Guy != null)
            {
                InitP2CharacterInteraction(p2Guy);
            }

            var apps = clone.GetComponentsInChildren<sCosmeticAppearance>(true);
            foreach (var app in apps)
            {
                PlayerCustomization.ApplySkin(app, 2);
            }

            return clone;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError($"[Splitscreen] Failed to spawn P2 character: {ex.Message}");
            return null;
        }
    }

    private static int CountConnectedJoysticks()
    {
        try
        {
            var names = Input.GetJoystickNames();
            int count = 0;
            foreach (var n in names)
                if (!string.IsNullOrWhiteSpace(n))
                    count++;
            return count;
        }
        catch
        {
            return 0;
        }
    }
}
