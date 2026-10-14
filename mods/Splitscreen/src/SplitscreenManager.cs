using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;
using EasyDeliveryCo.Shared;

namespace EasyDeliveryCoSplitscreen;

/// <summary>
/// Clean splitscreen manager using the "full duplicate" model:
/// - Clone P1 character + camera completely
/// - P1 and P2 are independent instances
/// - Only patch necessary singletons and input routing
/// </summary>
internal sealed class SplitscreenManager : MonoBehaviour
{
    private static SplitscreenManager? _instance;
    private bool _active;

    // P1 and P2 references
    private sCharacterController? _p1Controller;
    private GameObject? _p2Character;
    private sCharacterController? _p2Controller;

    // Cameras
    private Camera? _p1Cam;
    private Camera? _p2Cam;
    private sCameraController? _p2CamController;

    // Input managers
    private sInputManager? _p1InputManager;
    private sInputManager? _p2InputManager;

    // Seat tracking
    private CarSeats.CarSeat _p1Seat = CarSeats.CarSeat.None;
    private CarSeats.CarSeat _p2Seat = CarSeats.CarSeat.None;
    private bool _p2InCar = false;

    // Input config from gamepad count at F8 time
    private int _p2GamepadIndex = 0;
    private bool _p2ZeroGamepadMode = false;

    // Viewport rects
    private static readonly Rect P1RectV = new Rect(0f,   0f, 0.5f, 1f);
    private static readonly Rect P2RectV = new Rect(0.5f, 0f, 0.5f, 1f);
    private static readonly Rect P1RectH = new Rect(0f,  0.5f, 1f, 0.5f);
    private static readonly Rect P2RectH = new Rect(0f,   0f,  1f, 0.5f);
    private static readonly Rect FullRect = new Rect(0f,  0f,  1f, 1f);

    private float _nextToggleCooldown;

    // Public static accessors for patches
    internal static bool IsSplitscreenActive => _instance?._active ?? false;
    internal static GameObject? P2Character => _instance?._p2Character;
    internal static sCharacterController? P2Controller => _instance?._p2Controller;
    internal static sInputManager? P2InputManager => _instance?._p2InputManager;
    internal static sInputManager? P1Input => _instance?._p1InputManager;
    internal static int P2GamepadIndex => _instance?._p2GamepadIndex ?? 0;
    internal static bool P2ZeroGamepadMode => _instance?._p2ZeroGamepadMode ?? false;
    internal static CarSeats.CarSeat P1Seat => _instance?._p1Seat ?? CarSeats.CarSeat.None;
    internal static CarSeats.CarSeat P2Seat => _instance?._p2Seat ?? CarSeats.CarSeat.None;
    internal static bool IsP2InCar => _instance?._p2InCar ?? false;
    internal static Camera? P2CameraInstance => _instance?._p2Cam;
    internal static Transform? P2CameraTransform => _instance?._p2Cam?.transform;
    internal static sCameraController? P2CamController => _instance?._p2CamController;
    internal static GameObject? P1Character => _instance?._p1Controller?.gameObject;

    private void Awake()
    {
        _instance = this;
    }

    private void Update()
    {
        // F8 toggle for manual control
        if (SplitPlugin.ToggleKey != null)
        {
            if (Time.unscaledTime >= _nextToggleCooldown && Input.GetKeyDown(SplitPlugin.ToggleKey.Value))
            {
                _nextToggleCooldown = Time.unscaledTime + 0.5f;

                if (_active)
                    Deactivate();
                else
                    TryActivate();
            }
        }

        if (!_active) return;

        // Update cameras based on seat state
        UpdateCameras();
    }

    private void Start()
    {
        // Auto-activate splitscreen on boot
        StartCoroutine(AutoActivateNextFrame());
    }

    private System.Collections.IEnumerator AutoActivateNextFrame()
    {
        // Wait a couple frames for the scene to fully initialize
        yield return null;
        yield return null;
        
        if (!_active)
        {
            SplitPlugin.Log.LogInfo("[Splitscreen] Auto-activating splitscreen on boot...");
            TryActivate();
        }
    }

    private void UpdateCameras()
    {
        if (_p1Cam == null || _p2Cam == null) return;

        bool p1InCar = _p1Seat != CarSeats.CarSeat.None;
        bool p2InCar = _p2Seat != CarSeats.CarSeat.None;

        if (p1InCar && p2InCar)
        {
            // Both in car: only show P1
            _p1Cam.rect = FullRect;
            _p2Cam.enabled = false;
        }
        else
        {
            _p2Cam.enabled = true;
            bool vertical = SplitPlugin.Orientation?.Value != "Horizontal";
            if (vertical)
            {
                _p1Cam.rect = P1RectV;
                _p2Cam.rect = P2RectV;
            }
            else
            {
                _p1Cam.rect = P1RectH;
                _p2Cam.rect = P2RectH;
            }
        }
    }

    private void TryActivate()
    {
        var mainCam = FindMainCamera();
        if (mainCam == null)
        {
            SplitPlugin.Log.LogWarning("[Splitscreen] Could not find main camera.");
            return;
        }

        _p1Controller = SplitscreenHelper.FindLocalCharacterController();
        if (_p1Controller == null)
        {
            SplitPlugin.Log.LogWarning("[Splitscreen] Could not find player character.");
            return;
        }

        _p1Cam = mainCam;

        // Get P1's input manager (search everywhere)
        _p1InputManager = _p1Controller.GetComponentInChildren<sInputManager>(includeInactive: true);
        if (_p1InputManager == null)
        {
            // Try the controller's parent/root
            _p1InputManager = _p1Controller.GetComponent<sInputManager>();
        }
        if (_p1InputManager == null)
        {
            // Try finding it globally via static players array
            if (sInputManager.players != null && sInputManager.players.Length > 0)
            {
                _p1InputManager = sInputManager.players[0];
            }
        }
        if (_p1InputManager == null)
        {
            // Last resort: search all instances
            var all = FindObjectsByType<sInputManager>(FindObjectsSortMode.None);
            SplitPlugin.Log.LogInfo($"[Splitscreen] Found {all.Length} sInputManager(s) in scene");
            foreach (var m in all)
            {
                if (m != null)
                {
                    SplitPlugin.Log.LogInfo($"  - {m.gameObject.name} (parent: {m.transform.parent?.name ?? "null"})");
                    if (!m.gameObject.name.Contains("P2"))
                    {
                        _p1InputManager = m;
                        break;
                    }
                }
            }
        }
        
        if (_p1InputManager == null)
        {
            SplitPlugin.Log.LogWarning("[Splitscreen] Could not find P1's sInputManager anywhere.");
            return;
        }
        
        SplitPlugin.Log.LogInfo($"[Splitscreen] Found P1 sInputManager on {_p1InputManager.gameObject.name}");

        // Count gamepads for input routing
        int gamepadCount = Gamepad.all.Count;
        if (gamepadCount == 0)
            gamepadCount = SplitscreenHelper.CountConnectedJoysticks();

        if (gamepadCount == 1)
        {
            _p2GamepadIndex = 0;
            _p2ZeroGamepadMode = false;
            SplitPlugin.Log.LogInfo("[Splitscreen] 1 controller detected -> P1: Keyboard, P2: Gamepad 1");
        }
        else if (gamepadCount >= 2)
        {
            _p2GamepadIndex = 1;
            _p2ZeroGamepadMode = false;
            SplitPlugin.Log.LogInfo("[Splitscreen] 2+ controllers detected -> P1: Gamepad 1, P2: Gamepad 2");
        }
        else
        {
            _p2GamepadIndex = 0;
            _p2ZeroGamepadMode = true;
            SplitPlugin.Log.LogInfo("[Splitscreen] 0 controllers detected -> P1: WASD, P2: Arrow keys");
        }

        // Clone P1 into P2
        _p2Character = CloneP2Character(_p1Controller.gameObject);
        if (_p2Character == null)
        {
            SplitPlugin.Log.LogWarning("[Splitscreen] Failed to clone P2 character.");
            return;
        }

        _p2Controller = _p2Character.GetComponent<sCharacterController>();
        if (_p2Controller == null)
        {
            SplitPlugin.Log.LogWarning("[Splitscreen] P2 clone missing sCharacterController.");
            Destroy(_p2Character);
            return;
        }

        // CRITICAL: sItemManager.Awake() does
        // `guy = FindFirstObjectByType<sCharacterInteraction>();` — a scene-wide search.
        // Now that there are TWO sCharacterInteraction instances (P1's original + P2's
        // clone), this call (which already ran synchronously during Instantiate above)
        // very likely bound P2's own sItemManager to P1's sCharacterInteraction instead
        // of its own. That breaks P2's interaction prompts entirely: proximity checks use
        // P1's position/facing, and the button-press event fires into P1's handler.
        // Rebind P2's sItemManager(s) to P2's own sCharacterInteraction.
        FixP2ItemManagerBindings(_p2Character);

        // Get or create P2's input manager
        _p2InputManager = _p2Character.GetComponentInChildren<sInputManager>(includeInactive: true);
        if (_p2InputManager == null)
        {
            SplitPlugin.Log.LogInfo("[Splitscreen] P2 clone has no sInputManager, adding one...");
            // Find the same GameObject path where P1's sInputManager is, and add one to P2
            // If P1's is on the root, add to P2 root
            var p2InputGo = _p2Character; // by default, add to root
            
            if (_p1InputManager != null && _p1InputManager.gameObject != _p1Controller.gameObject)
            {
                // P1's sInputManager is on a different GameObject, find same relative path
                var relPath = GetRelativePath(_p1InputManager.gameObject, _p1Controller.gameObject);
                if (!string.IsNullOrEmpty(relPath))
                {
                    p2InputGo = FindOrCreatePath(_p2Character, relPath);
                }
            }
            
            // Ensure P2's PlayerInput exists and is configured for P2's gamepad
            var p2PlayerInput = p2InputGo.GetComponent<PlayerInput>();
            if (p2PlayerInput == null)
            {
                // Try to copy from P1
                var p1Pi = _p1InputManager.GetComponent<PlayerInput>();
                if (p1Pi != null)
                {
                    p2PlayerInput = p2InputGo.AddComponent<PlayerInput>();
                    p2PlayerInput.actions = p1Pi.actions;
                    SplitPlugin.Log.LogInfo("[Splitscreen] Added PlayerInput to P2 input GameObject");
                    
                    // Try to configure for P2's gamepad, but don't fail if scheme doesn't exist
                    try
                    {
                        if (_p2GamepadIndex < Gamepad.all.Count)
                        {
                            var scheme = p2PlayerInput.actions.controlSchemes.FirstOrDefault(cs => cs.name == "Gamepad");
                            if (scheme.name == "Gamepad")
                            {
                                p2PlayerInput.SwitchCurrentControlScheme("Gamepad", new[] { Gamepad.all[_p2GamepadIndex] });
                                SplitPlugin.Log.LogInfo($"[Splitscreen] P2 PlayerInput configured for Gamepad {_p2GamepadIndex}");
                            }
                            else
                            {
                                SplitPlugin.Log.LogInfo("[Splitscreen] No 'Gamepad' control scheme found, skipping explicit gamepad assignment");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        SplitPlugin.Log.LogWarning($"[Splitscreen] Could not configure P2 gamepad: {ex.Message}");
                    }
                }
            }
            
            _p2InputManager = p2InputGo.AddComponent<sInputManager>();
            SplitPlugin.Log.LogInfo($"[Splitscreen] Added new sInputManager to P2 on {p2InputGo.name}");
            
            // Force re-initialization of P2's input manager by calling methods directly
            try
            {
                // Access the private input field via reflection and set it
                var inputField = typeof(sInputManager).GetField("input", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (inputField != null && p2PlayerInput != null)
                {
                    inputField.SetValue(_p2InputManager, p2PlayerInput);
                    _p2InputManager.AssignActions();
                    SplitPlugin.Log.LogInfo("[Splitscreen] P2 sInputManager re-initialized with PlayerInput");
                }
            }
            catch (Exception ex)
            {
                SplitPlugin.Log.LogWarning($"[Splitscreen] Failed to re-initialize P2 input: {ex.Message}");
            }
        }
        
        if (_p2InputManager == null)
        {
            SplitPlugin.Log.LogWarning("[Splitscreen] Failed to ensure P2 has sInputManager.");
            Destroy(_p2Character);
            return;
        }

        // Clone P1's camera into P2
        _p2Cam = CloneP2Camera(_p1Cam);
        if (_p2Cam == null)
        {
            SplitPlugin.Log.LogWarning("[Splitscreen] Failed to clone P2 camera.");
            Destroy(_p2Character);
            return;
        }

        // Find sCameraController - it might be on the camera or on a parent
        if (_p2CamController == null)
        {
            _p2CamController = _p2Cam.GetComponent<sCameraController>();
            if (_p2CamController == null)
                _p2CamController = _p2Cam.GetComponentInParent<sCameraController>();
            if (_p2CamController == null)
                _p2CamController = _p2Cam.GetComponentInChildren<sCameraController>(includeInactive: true);
        }

        // Fix P2 camera to follow P2 character using the game's camera controller
        if (_p2CamController != null)
        {
            _p2CamController.target = _p2Character.transform;
            _p2CamController.firstPerson = false;
            _p2CamController.firstPersonMode = false;
            try { _p2CamController.ResetFacing(); } catch { }

            // P2's rig was cloned at P1's rig position (good - same viewpoint), but
            // ResetFacing() above snaps facing/targetFacing to the shared car's forward,
            // which can be ~180° off from where P1's camera is actually looking.
            // Worse, DoCameraTracking() converges `facing` with Slerp(..., dt * rotationSpeed)
            // and rotationSpeed decays toward 0 while the car is stationary (on-foot),
            // so a flipped initial facing NEVER converges and P2's controls feel inverted
            // (up->down, right->left) relative to their own screen. Copy P1's live facing
            // so both cameras start looking the same world direction.
            try
            {
                var p1CamCtrl = _p1Cam != null ? _p1Cam.GetComponentInParent<sCameraController>() : null;
                if (p1CamCtrl != null && p1CamCtrl != _p2CamController)
                {
                    _p2CamController.targetFacing = p1CamCtrl.targetFacing;
                    _p2CamController.transform.rotation = p1CamCtrl.transform.rotation;
                    var facingField = typeof(sCameraController).GetField("facing", BindingFlags.NonPublic | BindingFlags.Instance);
                    var p1Facing = facingField?.GetValue(p1CamCtrl);
                    if (p1Facing != null) facingField?.SetValue(_p2CamController, p1Facing);
                }
            }
            catch { }
            SplitPlugin.Log.LogInfo($"[Splitscreen] P2 camera controller set to follow {_p2Character.name}");

            // CRITICAL: sCharacterInteraction.cam is a serialized reference to the
            // ORIGINAL P1 sCameraController (set in the source prefab/scene). Since that
            // camera rig detaches itself from the player hierarchy in its own Start()
            // (transform.parent = transform.parent.parent) BEFORE we clone the character,
            // it's an EXTERNAL reference relative to the cloned character hierarchy, so
            // Unity's Instantiate does NOT remap it — P2's cloned sCharacterInteraction
            // still points at P1's real controller. Every frame it does
            // `cam.target = camTarget;`, which hijacks P1's camera to follow P2!
            // Rebind every sCharacterInteraction on the P2 clone to P2's own controller.
            var p2Interactions = _p2Character.GetComponentsInChildren<sCharacterInteraction>(includeInactive: true);
            foreach (var interaction in p2Interactions)
            {
                if (interaction != null)
                {
                    interaction.cam = _p2CamController;
                }
            }
            if (p2Interactions.Length > 0)
                SplitPlugin.Log.LogInfo($"[Splitscreen] Rebound {p2Interactions.Length} sCharacterInteraction.cam reference(s) to P2's camera controller");
        }
        else
        {
            // Fallback to simple follower if no controller found
            var follower = _p2Cam.gameObject.AddComponent<SimpleCameraFollower>();
            follower.target = _p2Character.transform;
            SplitPlugin.Log.LogInfo($"[Splitscreen] P2 using simple follower (no sCameraController found)");
        }

        _active = true;
        _p1Seat = CarSeats.CarSeat.None;
        _p2Seat = CarSeats.CarSeat.None;
        _p2InCar = false;

        SplitPlugin.Log.LogInfo("[Splitscreen] Activated — clean co-op model running.");
    }

    internal void Deactivate()
    {
        if (!_active) return;
        _active = false;

        // Restore P1 camera
        if (_p1Cam != null)
        {
            _p1Cam.rect = FullRect;
            _p1Cam = null;
        }

        // Destroy P2 camera
        if (_p2Cam != null)
        {
            // Walk up to destroy the whole rig (camera -> controller -> dummy parent -> dummy grandparent)
            var root = _p2Cam.transform.root;
            Destroy(root != null ? root.gameObject : _p2Cam.gameObject);
            _p2Cam = null;
            _p2CamController = null;
        }

        // Destroy P2 character
        if (_p2Character != null)
        {
            Destroy(_p2Character);
            _p2Character = null;
            _p2Controller = null;
            _p2InputManager = null;
        }

        _p1Seat = CarSeats.CarSeat.None;
        _p2Seat = CarSeats.CarSeat.None;

        SplitPlugin.Log.LogInfo("[Splitscreen] Deactivated.");
    }

    private void OnDestroy() => Deactivate();

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Rebind P2's sItemManager(s) to P2's own sCharacterInteraction. Awake() on the
    /// clone already ran during Instantiate and used a scene-wide Find call that likely
    /// grabbed P1's instance, so we need to correct both the field and the event
    /// subscription (OnButtonPressed) that Awake() already wired up incorrectly.
    /// </summary>
    private static void FixP2ItemManagerBindings(GameObject p2Character)
    {
        var p2Guy = p2Character.GetComponentInChildren<sCharacterInteraction>(includeInactive: true);
        if (p2Guy == null)
        {
            SplitPlugin.Log.LogWarning("[Splitscreen] P2 clone has no sCharacterInteraction; skipping item manager rebind.");
            return;
        }

        var itemManagers = p2Character.GetComponentsInChildren<sItemManager>(includeInactive: true);
        if (itemManagers.Length == 0) return;

        var buttonPressedMethod = typeof(sItemManager).GetMethod("ButtonPressed", BindingFlags.NonPublic | BindingFlags.Instance);
        int fixedCount = 0;

        foreach (var im in itemManagers)
        {
            if (im == null) continue;

            var oldGuy = im.guy;
            if (oldGuy == p2Guy) continue; // already correct, nothing to do

            try
            {
                if (buttonPressedMethod != null)
                {
                    var del = (sCharacterInteraction.ButtonPressed)Delegate.CreateDelegate(
                        typeof(sCharacterInteraction.ButtonPressed), im, buttonPressedMethod);
                    if (oldGuy != null) oldGuy.OnButtonPressed -= del;
                    p2Guy.OnButtonPressed += del;
                }
            }
            catch (Exception ex)
            {
                SplitPlugin.Log.LogWarning($"[Splitscreen] Failed to rebind P2 item manager button event: {ex.Message}");
            }

            im.guy = p2Guy;
            fixedCount++;
        }

        if (fixedCount > 0)
            SplitPlugin.Log.LogInfo($"[Splitscreen] Rebound {fixedCount} sItemManager.guy reference(s) to P2's own sCharacterInteraction");
    }

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

    private static GameObject? CloneP2Character(GameObject source)
    {
        try
        {
            var clone = Instantiate(source);
            clone.name = "EasyDeliveryCoSplitscreen.P2Character";
            clone.hideFlags = HideFlags.DontSave;

            // Spawn beside P1
            clone.transform.position = source.transform.position + source.transform.right * 2f;
            clone.transform.rotation = source.transform.rotation;

            // Disable singletons
            SplitscreenHelper.DisableGameSingletons(clone);

            // Ensure active
            clone.SetActive(true);
            foreach (var r in clone.GetComponentsInChildren<Renderer>(includeInactive: true))
                r.enabled = true;

            var charCtrl = clone.GetComponent<CharacterController>();
            if (charCtrl != null) charCtrl.enabled = true;

            SplitPlugin.Log.LogInfo("[Splitscreen] P2 character cloned.");
            return clone;
        }
        catch (Exception ex)
        {
            SplitPlugin.Log.LogError($"[Splitscreen] Failed to clone P2 character: {ex.Message}");
            return null;
        }
    }

    private Camera? CloneP2Camera(Camera source)
    {
        try
        {
            // sCameraController.Awake() does: cam = GetComponentInChildren<Camera>();
            // So the controller lives on a PARENT of the camera, not the camera itself.
            var sourceController = source.GetComponentInParent<sCameraController>();
            if (sourceController == null)
            {
                SplitPlugin.Log.LogWarning("[Splitscreen] P1 camera has no sCameraController in its parent chain; using simple follower.");
                return CloneCameraOnlySimple(source);
            }

            var controllerGo = sourceController.gameObject;
            SplitPlugin.Log.LogInfo($"[Splitscreen] Cloning full camera rig from {controllerGo.name}...");

            var clone = Instantiate(controllerGo);
            clone.name = "EasyDeliveryCoSplitscreen.P2CameraRig";
            clone.hideFlags = HideFlags.DontSave;

            var clonedController = clone.GetComponent<sCameraController>();
            if (clonedController == null)
            {
                SplitPlugin.Log.LogError("[Splitscreen] Cloned camera rig lost its sCameraController!");
                Destroy(clone);
                return null;
            }

            // Disable IMMEDIATELY (before Unity's next Update phase) so Start() doesn't
            // run yet — we need to set up a safe parent chain first, since Start() does
            // `transform.parent = transform.parent.parent`, which NREs if parent is null.
            clonedController.enabled = false;

            // Build a 2-level dummy parent chain with identity transforms so Start()'s
            // reparenting line succeeds without crashing. After Start() runs, the clone
            // will end up parented to _p2CamDummyRoot (the grandparent), which is fine.
            var dummyGrandparent = new GameObject("EasyDeliveryCoSplitscreen.P2CamDummyRoot");
            dummyGrandparent.hideFlags = HideFlags.DontSave;
            DontDestroyOnLoad(dummyGrandparent);

            var dummyParent = new GameObject("EasyDeliveryCoSplitscreen.P2CamDummyParent");
            dummyParent.hideFlags = HideFlags.DontSave;
            dummyParent.transform.SetParent(dummyGrandparent.transform, false);

            clone.transform.SetParent(dummyParent.transform, true); // keep world position

            // Remove audio listener from clone
            var al = clone.GetComponentInChildren<AudioListener>(includeInactive: true);
            if (al != null) Destroy(al);

            // Disable PlayerInput on clone (if any lives under the camera rig)
            foreach (var pi in clone.GetComponentsInChildren<PlayerInput>(includeInactive: true))
                if (pi != null) pi.enabled = false;

            // Disable CameraPause if present
            foreach (var mb in clone.GetComponentsInChildren<MonoBehaviour>(includeInactive: true))
            {
                if (mb != null && mb.GetType().Name == "CameraPause")
                {
                    mb.enabled = false;
                    break;
                }
            }

            var cam = clone.GetComponentInChildren<Camera>(includeInactive: true);
            if (cam == null)
            {
                SplitPlugin.Log.LogError("[Splitscreen] Cloned camera rig has no Camera component!");
                Destroy(clone);
                Destroy(dummyGrandparent);
                return null;
            }

            cam.tag = "Untagged";
            cam.depth = source.depth + 1;
            cam.enabled = true;

            // Now it's safe to enable the controller — Start() will run before the next
            // Update/LateUpdate and will succeed since transform.parent.parent is valid.
            clonedController.target = null; // set by caller after this returns
            clonedController.firstPerson = false;
            clonedController.firstPersonMode = false;
            clonedController.enabled = true;

            _p2CamController = clonedController;
            SplitPlugin.Log.LogInfo("[Splitscreen] P2 camera rig cloned successfully (full sCameraController).");
            return cam;
        }
        catch (Exception ex)
        {
            SplitPlugin.Log.LogError($"[Splitscreen] Failed to clone P2 camera: {ex.Message}\n{ex.StackTrace}");
            return null;
        }
    }

    private static Camera? CloneCameraOnlySimple(Camera source)
    {
        try
        {
            var camGo = source.gameObject;
            var clone = Instantiate(camGo);
            clone.name = "EasyDeliveryCoSplitscreen.P2Camera";
            clone.transform.SetParent(null, true);
            DontDestroyOnLoad(clone);
            clone.hideFlags = HideFlags.DontSave;

            var al = clone.GetComponentInChildren<AudioListener>(includeInactive: true);
            if (al != null) Destroy(al);

            var cam = clone.GetComponent<Camera>();
            if (cam == null)
            {
                Destroy(clone);
                return null;
            }

            cam.tag = "Untagged";
            cam.depth = source.depth + 1;
            cam.enabled = true;

            foreach (var pi in clone.GetComponentsInChildren<PlayerInput>(includeInactive: true))
                if (pi != null) pi.enabled = false;

            return cam;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Get the relative path from a child to its ancestor (walking up parents).
    /// Returns null if not actually a child.
    /// </summary>
    private static string? GetRelativePath(GameObject child, GameObject ancestor)
    {
        var path = new System.Collections.Generic.List<string>();
        var current = child;
        
        while (current != null)
        {
            if (current == ancestor)
                return string.Join("/", path); // path from ancestor down to child
            
            path.Insert(0, current.name);
            current = current.transform.parent?.gameObject;
        }
        
        return null; // not a child of ancestor
    }

    /// <summary>
    /// Find or create a path of GameObjects under root matching the given path string.
    /// </summary>
    private static GameObject FindOrCreatePath(GameObject root, string path)
    {
        if (string.IsNullOrEmpty(path)) return root;
        
        var parts = path.Split('/');
        var current = root;
        
        foreach (var part in parts)
        {
            var child = current.transform.Find(part)?.gameObject;
            if (child == null)
            {
                child = new GameObject(part);
                child.transform.SetParent(current.transform);
            }
            current = child;
        }
        
        return current;
    }
}

/// <summary>
/// Simple camera follower for P2 - just smoothly follows the target without all the complexity
/// of the game's sCameraController.
/// </summary>
internal sealed class SimpleCameraFollower : MonoBehaviour
{
    internal Transform? target;
    private Camera? _camera;
    private float _distance = 5f;
    private float _height = 1.5f;
    private float _smoothTime = 0.1f;
    private Vector3 _velocity = Vector3.zero;

    private void Start()
    {
        _camera = GetComponent<Camera>();
    }

    private void LateUpdate()
    {
        if (target == null) return;

        // Simple follow: stay behind and above the target
        Vector3 targetPos = target.position + Vector3.up * _height;
        Vector3 cameraPos = targetPos - target.forward * _distance;

        transform.position = Vector3.SmoothDamp(transform.position, cameraPos, ref _velocity, _smoothTime);
        transform.LookAt(targetPos);

        // Match P1's fog far plane
        if (_camera != null)
        {
            try { _camera.farClipPlane = Mathf.Sqrt(Mathf.Log(526.3158f)) / RenderSettings.fogDensity; } catch { }
        }
    }
}
