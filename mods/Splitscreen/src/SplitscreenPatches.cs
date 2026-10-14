using System;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;

namespace EasyDeliveryCoSplitscreen;

/// <summary>
/// Minimal patches for clean splitscreen:
/// - Swap sInputManager.players[0] during P2 updates so P2 reads own input
/// - Route P2's camera look input
/// - Track seat assignments
/// </summary>
public static class SplitscreenPatches
{
    private static bool IsP2Object(GameObject go)
    {
        if (go == null) return false;
        if (go.name.Contains("EasyDeliveryCoSplitscreen.P2")) return true;
        var root = go.transform?.root;
        return root != null && root.name.Contains("EasyDeliveryCoSplitscreen.P2");
    }

    /// <summary>
    /// During P2's sCharacterController.Update(), swap sInputManager.players[0]
    /// to point to P2's input manager so P2 reads its own input.
    /// </summary>
    [HarmonyPatch(typeof(sCharacterController), "Update")]
    public static class CharacterControllerUpdatePatch
    {
        [HarmonyPrefix]
        public static void Prefix(sCharacterController __instance, out sInputManager[]? __state)
        {
            __state = null;
            if (!IsP2Object(__instance.gameObject)) return;

            var p2Mgr = SplitscreenManager.P2InputManager;
            if (p2Mgr == null) return;

            try
            {
                __state = sInputManager.players;
                sInputManager.players = new[] { p2Mgr };
            }
            catch { }
        }

        [HarmonyPostfix]
        public static void Postfix(sInputManager[]? __state)
        {
            if (__state != null)
            {
                try { sInputManager.players = __state; } catch { }
            }
        }
    }

    /// <summary>
    /// Override P2's sInputManager.Update() to poll directly from gamepad 2,
    /// bypassing the InputSystem device binding which isn't reliable.
    /// </summary>
    [HarmonyPatch(typeof(sInputManager), "Update")]
    public static class InputManagerUpdatePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(sInputManager __instance)
        {
            // Only intercept P2's input manager
            if (__instance != SplitscreenManager.P2InputManager)
                return true; // let P1's normal update run

            // P2: manually read from gamepad 2
            try
            {
                int p2Idx = SplitscreenManager.P2GamepadIndex;
                if (p2Idx < Gamepad.all.Count)
                {
                    var pad = Gamepad.all[p2Idx];
                    
                    // Read from gamepad
                    Vector2 movement = pad.leftStick.ReadValue();
                    Vector2 look = pad.rightStick.ReadValue();
                    
                    // Set public fields directly
                    __instance.playerInput = movement;
                    __instance.driveInput = movement;
                    __instance.cameraLook = look;
                    __instance.mouseInput = Vector2.zero;
                    
                    // Read button presses
                    __instance.selectPressed = pad.buttonSouth.wasPressedThisFrame;
                    __instance.selectReleased = pad.buttonSouth.wasReleasedThisFrame;
                    __instance.backPressed = pad.buttonEast.wasPressedThisFrame;
                    __instance.backReleased = pad.buttonEast.wasReleasedThisFrame;
                    __instance.breakPressed = pad.rightTrigger.isPressed || pad.buttonEast.isPressed;
                    __instance.inventoryPressed = pad.buttonWest.wasPressedThisFrame;
                    __instance.inventoryReleased = pad.buttonWest.wasReleasedThisFrame;
                    __instance.inventoryHeld = pad.buttonWest.isPressed;
                    __instance.cameraPressed = pad.buttonNorth.wasPressedThisFrame;
                    __instance.headlightsPressed = pad.rightShoulder.wasPressedThisFrame;
                    __instance.hornPressed = pad.leftShoulder.wasPressedThisFrame;
                    __instance.pausePressed = pad.startButton.wasPressedThisFrame;
                    __instance.mapPressed = pad.selectButton.wasPressedThisFrame;
                    __instance.resetPressed = pad.leftStickButton.wasPressedThisFrame;
                    __instance.resetHeld = pad.leftStickButton.isPressed;
                    
                    return false; // skip original Update
                }
            }
            catch { }
            
            return true; // fallback: run normal update
        }
    }

    public sealed class InteractionPatchState
    {
        public sInputManager[]? input;
        public Camera? cam;
    }

    /// <summary>
    /// During P2's sCharacterInteraction.Update(), swap input so interactions work,
    /// and swap the shared car's carCamera so DrawMarkers()/DrawPlaceMarkers() (which
    /// call hud.DrawWorldPointLabel/Marker, projected through navigation.car.carCamera)
    /// use P2's own camera instead of P1's.
    /// </summary>
    [HarmonyPatch(typeof(sCharacterInteraction), "Update")]
    public static class CharacterInteractionUpdatePatch
    {
        [HarmonyPrefix]
        public static void Prefix(sCharacterInteraction __instance, out InteractionPatchState __state)
        {
            __state = new InteractionPatchState();
            if (!IsP2Object(__instance.gameObject)) return;

            var p2Mgr = SplitscreenManager.P2InputManager;
            if (p2Mgr != null)
            {
                try
                {
                    __state.input = sInputManager.players;
                    sInputManager.players = new[] { p2Mgr };
                }
                catch { }
            }

            var p2Cam = SplitscreenManager.P2CameraInstance;
            if (p2Cam != null && __instance.car != null)
            {
                __state.cam = __instance.car.carCamera;
                __instance.car.carCamera = p2Cam;
            }
        }

        [HarmonyPostfix]
        public static void Postfix(sCharacterInteraction __instance, InteractionPatchState __state)
        {
            if (__state?.input != null)
            {
                try { sInputManager.players = __state.input; } catch { }
            }

            if (__state?.cam != null && __instance.car != null)
            {
                __instance.car.carCamera = __state.cam;
            }
        }
    }

    /// <summary>
    /// During P2's sItemManager.Update(), swap input for item interactions, AND swap
    /// the shared car's carCamera so hud.DrawWorldPointLabel/Sprite (which project
    /// through navigation.car.carCamera inside sHUD.WorldToHUDPoint) use P2's own
    /// camera instead of P1's. Without this, P2's interaction prompts compute their
    /// screen position from P1's camera and often end up off-screen (-100,-100) since
    /// the point isn't in front of P1's camera.
    /// </summary>
    [HarmonyPatch(typeof(sItemManager), "Update")]
    public static class ItemManagerUpdatePatch
    {
        [HarmonyPrefix]
        public static void Prefix(sItemManager __instance, out InteractionPatchState __state)
        {
            __state = new InteractionPatchState();
            if (!IsP2Object(__instance.gameObject)) return;

            var p2Mgr = SplitscreenManager.P2InputManager;
            if (p2Mgr != null)
            {
                try
                {
                    __state.input = sInputManager.players;
                    sInputManager.players = new[] { p2Mgr };
                }
                catch { }
            }

            var p2Cam = SplitscreenManager.P2CameraInstance;
            if (p2Cam != null && __instance.car != null)
            {
                __state.cam = __instance.car.carCamera;
                __instance.car.carCamera = p2Cam;
            }
        }

        [HarmonyPostfix]
        public static void Postfix(sItemManager __instance, InteractionPatchState __state)
        {
            if (__state?.input != null)
            {
                try { sInputManager.players = __state.input; } catch { }
            }

            if (__state?.cam != null && __instance.car != null)
            {
                __instance.car.carCamera = __state.cam;
            }
        }
    }

    /// <summary>
    /// Both P1 and P2 run sCharacterInteraction.Start(), each creating its own duplicate
    /// "get in" / "open" / "flip" InteractionPoints parented to the SAME shared car, with
    /// listener set to their own character object. The game's FindClosestInteraction()
    /// searches ALL points scene-wide with no ownership check, so P2 can lock onto P1's
    /// duplicate point (whose listener routes the action to P1) and vice versa — making
    /// it look like P2 "sometimes can't interact" (the press actually drove P1).
    /// Fix: when splitscreen is active, each player's search skips points owned by the
    /// other player's character, while still accepting shared world points (shops, etc.)
    /// whose listener belongs to neither player.
    /// </summary>
    [HarmonyPatch(typeof(sItemManager), "FindClosestInteraction")]
    public static class FindClosestInteractionPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(sItemManager __instance, ref InteractionPoint? __result)
        {
            if (!SplitscreenManager.IsSplitscreenActive) return true;

            GameObject? selfRoot;
            GameObject? otherRoot;
            if (IsP2Object(__instance.gameObject))
            {
                selfRoot = SplitscreenManager.P2Character;
                otherRoot = SplitscreenManager.P1Character;
            }
            else
            {
                // P1 caller: still filter out P2's duplicates while splitscreen is on.
                selfRoot = SplitscreenManager.P1Character;
                otherRoot = SplitscreenManager.P2Character;
                if (selfRoot == null || otherRoot == null) return true;
                if (__instance.gameObject != selfRoot && __instance.transform?.root?.gameObject != selfRoot)
                    return true; // not a player item manager (shouldn't happen) — run original
            }

            try
            {
                var guy = __instance.guy;
                if (guy == null || __instance.hands == null) { __result = null; return false; }

                InteractionPoint? best = null;
                float bestDist = float.MaxValue;
                var all = UnityEngine.Object.FindObjectsByType<InteractionPoint>(FindObjectsSortMode.None);
                foreach (var pt in all)
                {
                    if (pt == null || !pt.enabled) continue;

                    // Skip points owned by the OTHER player (their listener routes to them).
                    var listener = pt.listener;
                    if (listener != null && otherRoot != null)
                    {
                        if (listener == otherRoot) continue;
                        var listenerRoot = listener.transform?.root?.gameObject;
                        if (listenerRoot != null && listenerRoot == otherRoot) continue;
                    }

                    // Mirror of the game's LookingAt(): dot(-guyForward, dirToPoint) > 0
                    Vector3 to = pt.transform.position - guy.transform.position;
                    to.y = 0f;
                    if (to.sqrMagnitude < 1e-8f) continue;
                    if (Vector3.Dot(-guy.transform.forward, to.normalized) <= 0f) continue;

                    // Mirror of the game's holding-item filters
                    if (__instance.holdingItem)
                    {
                        if (!(pt.ignoreItems || (pt.validItemInteractions != null && pt.validItemInteractions.Contains(__instance.heldItemIndex))))
                            continue;
                    }
                    else
                    {
                        if (!(pt.validItemInteractions == null || pt.validItemInteractions.Count <= 0))
                            continue;
                    }

                    float d = Vector3.Distance(__instance.hands.position, pt.transform.position);
                    if (best == null || d < bestDist) { best = pt; bestDist = d; }
                }

                __result = best;
                return false;
            }
            catch
            {
                return true; // on any error, fall back to original logic
            }
        }
    }

    /// <summary>
    /// During P2's sCameraController.Update(), swap sInputManager.players[0]
    /// so the camera reads P2's input instead of P1's.
    /// </summary>
    [HarmonyPatch(typeof(sCameraController), "Update")]
    public static class CameraControllerUpdatePatch
    {
        [HarmonyPrefix]
        public static void Prefix(sCameraController __instance, out sInputManager[]? __state)
        {
            __state = null;
            if (__instance == null || __instance != SplitscreenManager.P2CamController)
                return;

            var p2Mgr = SplitscreenManager.P2InputManager;
            if (p2Mgr == null) return;

            try
            {
                __state = sInputManager.players;
                sInputManager.players = new[] { p2Mgr };
            }
            catch { }
        }

        [HarmonyPostfix]
        public static void Postfix(sInputManager[]? __state)
        {
            if (__state != null)
            {
                try { sInputManager.players = __state; } catch { }
            }
        }
    }

    /// <summary>
    /// Track when P1 enters the truck (to assign seat).
    /// </summary>
    [HarmonyPatch(typeof(sCharacterInteraction), "HideGuy")]
    public static class HideGuyPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(sCharacterInteraction __instance)
        {
            if (IsP2Object(__instance.gameObject)) return true; // let P2 hide normally
            
            // P1 is entering truck; assign seat based on door proximity
            if (__instance.car != null)
            {
                float dot = Vector3.Dot(__instance.transform.position - __instance.car.transform.position, __instance.car.transform.right);
                var seat = dot > 0 ? EasyDeliveryCo.Shared.CarSeats.CarSeat.Driver : EasyDeliveryCo.Shared.CarSeats.CarSeat.Passenger;
                
                // Anti-steal: if P2 is already in this seat, take the other one
                if (seat == SplitscreenManager.P2Seat)
                    seat = seat == EasyDeliveryCo.Shared.CarSeats.CarSeat.Driver 
                        ? EasyDeliveryCo.Shared.CarSeats.CarSeat.Passenger 
                        : EasyDeliveryCo.Shared.CarSeats.CarSeat.Driver;

                // Store P1's seat (will be used by HideGuy to position them)
                // We need to patch the hiding logic itself to use seat positioning
                // For now, just let normal HideGuy run
            }
            return true;
        }
    }

    /// <summary>
    /// Track when P1 exits the truck.
    /// </summary>
    [HarmonyPatch(typeof(sCharacterInteraction), "ToggleActive")]
    public static class ToggleActivePostfixPatch
    {
        [HarmonyPostfix]
        public static void Postfix(sCharacterInteraction __instance)
        {
            if (IsP2Object(__instance.gameObject)) return; // ignore P2

            var activeField = typeof(sCharacterInteraction).GetField("active", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (activeField != null)
            {
                bool active = (bool)activeField.GetValue(__instance);
                if (active) // exiting truck
                {
                    // Clear P1 seat
                }
            }
        }
    }
}
