using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace EasyDeliveryCoLanCoop;

/// <summary>
/// Harmony patches that route input and interactions for Player 2.
/// </summary>
public static class SplitscreenPatches
{

    /// <summary>
    /// Intercepts sCharacterController.Update() on Player 2's clone so that it runs
    /// using Player 2's controller input instead of reading sInputManager.players[0].
    /// </summary>
    [HarmonyPatch(typeof(sCharacterController), "Update")]
    public static class CharacterControllerUpdatePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(sCharacterController __instance)
        {
            if (__instance.gameObject.name.Contains("P2"))
            {
                SplitscreenManager.UpdateP2CharacterController(__instance);
                return false; // Skip original method to avoid reading player[0]
            }
            return true;
        }
    }

    /// <summary>
    /// Intercepts sCharacterInteraction.Start() on Player 2's clone to prevent P2 from
    /// calling SetActive(false) / HideGuy() or overwriting car.GuyActive and P1 camera settings.
    /// </summary>
    [HarmonyPatch(typeof(sCharacterInteraction), "Start")]
    public static class CharacterInteractionStartPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(sCharacterInteraction __instance)
        {
            if (__instance.gameObject.name.Contains("P2"))
            {
                SplitscreenManager.InitP2CharacterInteraction(__instance);
                return false; // Skip original Start to prevent hiding P2
            }
            return true;
        }
    }

    /// <summary>
    /// Intercepts sCharacterInteraction.Update() on Player 2's clone.
    /// Skips original Update so P2 does not read Player 1's input (selectPressed)
    /// or overwrite Player 1's camera target or car handbrake.
    /// P2 interactions are handled safely by SplitscreenManager.Update().
    /// </summary>
    [HarmonyPatch(typeof(sCharacterInteraction), "Update")]
    public static class CharacterInteractionUpdatePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(sCharacterInteraction __instance)
        {
            if (__instance.gameObject.name.Contains("P2"))
            {
                return false; // Skip original method on P2
            }
            return true;
        }
    }

    /// <summary>
    /// Fixes sItemManager.LookingAt() on Player 2 so it checks Player 2's own position
    /// and forward orientation.
    /// </summary>
    [HarmonyPatch(typeof(sItemManager), "LookingAt")]
    public static class ItemManagerLookingAtPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(sItemManager __instance, Vector3 point, ref bool __result)
        {
            if (__instance.gameObject.name.Contains("P2") || __instance.transform.root.name.Contains("P2"))
            {
                Vector3 vector = point - __instance.transform.position;
                vector.y = 0f;
                // True if within close range (< 2.8m) or generally facing towards the point
                __result = vector.magnitude < 2.8f || Vector3.Dot(__instance.transform.forward, vector.normalized) > -0.2f;
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Fixes sCharacterInteraction.LookingAt() on Player 2.
    /// </summary>
    [HarmonyPatch(typeof(sCharacterInteraction), "LookingAt")]
    public static class CharacterInteractionLookingAtPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(sCharacterInteraction __instance, Vector3 point, ref bool __result)
        {
            if (__instance.gameObject.name.Contains("P2") || __instance.transform.root.name.Contains("P2"))
            {
                Vector3 vector = point - __instance.transform.position;
                vector.y = 0f;
                __result = vector.magnitude < 2.8f || Vector3.Dot(__instance.transform.forward, vector.normalized) > -0.2f;
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Null-checks InteractionPoint.listener before invoking SendMessage to prevent
    /// NullReferenceExceptions if an interaction point has no listener assigned.
    /// Also hooks into P1 getting into the truck to assign Driver/Passenger seats.
    /// </summary>
    [HarmonyPatch(typeof(InteractionPoint), "DoAction", new Type[0])]
    public static class InteractionPointDoActionPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(InteractionPoint __instance)
        {
            if (__instance.listener == null)
                return false;

            if (__instance.callback == "ToggleActive")
            {
                var car = UnityEngine.Object.FindFirstObjectByType<sCarController>();
                if (car != null)
                {
                    float dot = Vector3.Dot(__instance.transform.position - car.transform.position, car.transform.right);
                    var seat = dot > 0 ? SplitscreenManager.CarSeat.Driver : SplitscreenManager.CarSeat.Passenger;
                    
                    if (seat == SplitscreenManager.CarSeat.Driver && SplitscreenManager.P2Seat == SplitscreenManager.CarSeat.Driver) 
                        seat = SplitscreenManager.CarSeat.Passenger;
                    if (seat == SplitscreenManager.CarSeat.Passenger && SplitscreenManager.P2Seat == SplitscreenManager.CarSeat.Passenger) 
                        seat = SplitscreenManager.CarSeat.Driver;
                        
                    SplitscreenManager.P1Seat = seat;
                }
            }
            return true;
        }
    }

    /// <summary>
    /// Overload for InteractionPoint.DoAction(int itemInteraction).
    /// </summary>
    [HarmonyPatch(typeof(InteractionPoint), "DoAction", new Type[] { typeof(int) })]
    public static class InteractionPointDoActionIntPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(InteractionPoint __instance)
        {
            if (__instance.listener == null)
                return false;
            return true;
        }
    }

    /// <summary>
    /// Intercepts HideGuy to prevent Player 1 from being completely hidden in the truck.
    /// Instead, places them in their assigned driver or passenger seat.
    /// </summary>
    [HarmonyPatch(typeof(sCharacterInteraction), "HideGuy")]
    public static class HideGuyPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(sCharacterInteraction __instance)
        {
            if (__instance.gameObject.name.Contains("P2")) return false;

            if (SplitscreenManager.P1Seat != SplitscreenManager.CarSeat.None)
            {
                __instance.transform.parent = __instance.car.transform;
                __instance.transform.localPosition = SplitscreenManager.GetSeatedLocalPosition(SplitscreenManager.P1Seat);
                __instance.transform.localRotation = Quaternion.Euler(0, 180, 0);
                __instance.transform.localScale = SplitscreenManager.SeatedCharacterScale;
                
                SplitscreenManager.SetCharacterCollisionsEnabled(__instance.gameObject, false);
                if (__instance.car != null)
                    SplitscreenManager.IgnoreCollisionsWithCar(__instance.gameObject, __instance.car, true);

                foreach (var r in __instance.GetComponentsInChildren<Renderer>())
                    r.enabled = true;
                    
                return false;
            }
            return true;
        }
    }

    /// <summary>
    /// Checks when Player 1 exits the truck to clear their seat assignment.
    /// </summary>
    [HarmonyPatch(typeof(sCharacterInteraction), "ToggleActive")]
    public static class ToggleActivePostfixPatch
    {
        [HarmonyPostfix]
        public static void Postfix(sCharacterInteraction __instance)
        {
            if (__instance.gameObject.name.Contains("P2")) return;
            
            var activeField = typeof(sCharacterInteraction).GetField("active", BindingFlags.NonPublic | BindingFlags.Instance);
            if (activeField != null)
            {
                bool active = (bool)activeField.GetValue(__instance);
                if (!active && SplitscreenManager.P1Seat != SplitscreenManager.CarSeat.None)
                {
                    // Entering truck
                    SplitscreenManager.SetCharacterCollisionsEnabled(__instance.gameObject, false);
                    if (__instance.car != null)
                        SplitscreenManager.IgnoreCollisionsWithCar(__instance.gameObject, __instance.car, true);
                }
                else if (active && SplitscreenManager.P1Seat != SplitscreenManager.CarSeat.None)
                {
                    // Exiting truck
                    SplitscreenManager.P1Seat = SplitscreenManager.CarSeat.None;
                    __instance.transform.parent = null;
                    __instance.transform.localScale = Vector3.one;
                    SplitscreenManager.SetCharacterCollisionsEnabled(__instance.gameObject, true);
                    if (__instance.car != null)
                        SplitscreenManager.IgnoreCollisionsWithCar(__instance.gameObject, __instance.car, false);
                }
            }
        }
    }

    /// <summary>
    /// Intercepts car controls. If Player 2 is the driver, inject their inputs into Player 1's input buffer.
    /// If Player 1 is just a passenger, nullify their inputs so they don't accidentally drive.
    /// </summary>
    [HarmonyPatch(typeof(sCarController), "Update")]
    public static class CarControllerUpdatePatch
    {
        private static Vector2 _p1DriveInputCache;
        private static bool _p1BreakCache;

        [HarmonyPrefix]
        public static void Prefix(sCarController __instance)
        {
            if (sInputManager.players == null || sInputManager.players.Length == 0) return;
            var p1Input = sInputManager.players[0];
            _p1DriveInputCache = p1Input.driveInput;
            _p1BreakCache = p1Input.breakPressed;

            if (SplitscreenManager.IsP2Driver)
            {
                p1Input.driveInput = SplitscreenManager.GetP2MovementInput(forDriving: true);
                p1Input.breakPressed = SplitscreenManager.GetP2BrakeHeld();
            }
            else if (SplitscreenManager.IsP1Passenger)
            {
                p1Input.driveInput = Vector2.zero;
                p1Input.breakPressed = false;
            }
        }

        [HarmonyPostfix]
        public static void Postfix(sCarController __instance)
        {
            if (sInputManager.players == null || sInputManager.players.Length == 0) return;
            var p1Input = sInputManager.players[0];
            p1Input.driveInput = _p1DriveInputCache;
            p1Input.breakPressed = _p1BreakCache;
        }
    }

    /// <summary>
    /// The HUD overlay is a quad that is always 1.25x as wide as the view is tall. A split
    /// half is narrower than that, so the quad overflows and the HUD gets cropped towards the
    /// centre. Squeeze the quad horizontally to the half's width so the HUD keeps hugging the
    /// edges and corners of each player's view.
    /// </summary>
    [HarmonyPatch(typeof(pixelPerfectView), "AdjustViewPlane")]
    public static class PixelPerfectViewPatch
    {
        [HarmonyPostfix]
        public static void Postfix(pixelPerfectView __instance)
        {
            if (!SplitscreenManager.SplitActive) return;

            var cam = __instance.GetComponentInParent<Camera>();
            if (cam == null || (cam != SplitscreenManager.P1CameraInstance && cam != SplitscreenManager.P2CameraInstance))
                return;

            var rt = cam.targetTexture;
            float rtAspect = rt != null ? (float)rt.width / rt.height : (float)Screen.width / Screen.height;
            float viewAspect = cam.rect.width / cam.rect.height * rtAspect;
            float quadAspect = __instance.menuWidth / __instance.menuHeight;
            if (viewAspect >= quadAspect) return;

            var scale = __instance.transform.localScale;
            scale.x *= viewAspect / quadAspect;
            __instance.transform.localScale = scale;
        }
    }

    /// <summary>
    /// Inside (first-person) view: the game's first-person pivot is the driver's head, on the right
    /// of the truck. For a passenger the pivot is mirrored to the left seat, only for the duration of
    /// that rig's LateUpdate so the other player's view of the same pivot is not affected.
    /// The third-person chase camera is left alone.
    /// </summary>
    [HarmonyPatch(typeof(sCameraController), "LateUpdate")]
    public static class PassengerFirstPersonSeatPatch
    {
        private static Transform? _pivot;
        private static Vector3 _savedPos;
        private static bool _logged;

        [HarmonyPrefix]
        public static void Prefix(sCameraController __instance)
        {
            _pivot = null;
            if (!SplitscreenManager.SplitActive || __instance.car == null) return;
            if (!__instance.firstPerson || __instance.fixedPerspective) return;
            if (__instance.firstPersonPivot == null) return;

            bool passenger =
                (__instance == SplitscreenManager.P1Rig && SplitscreenManager.P1Seat == SplitscreenManager.CarSeat.Passenger) ||
                (__instance == SplitscreenManager.P2Rig && SplitscreenManager.P2Seat == SplitscreenManager.CarSeat.Passenger);
            if (!passenger) return;

            var car = __instance.car.transform;
            var pivot = __instance.firstPersonPivot;
            Vector3 local = car.InverseTransformPoint(pivot.position);

            // Mirror across the truck's centre line; if the pivot is already centred use the config offset
            float mirroredX = Mathf.Abs(local.x) > 0.05f ? -local.x : -(Plugin.PassengerCameraOffset?.Value ?? 0.5f);

            if (!_logged)
            {
                _logged = true;
                Plugin.Log.LogInfo($"[Splitscreen] Passenger first-person view moved to the left seat (pivot local x {local.x:0.##} -> {mirroredX:0.##}).");
            }

            _pivot = pivot;
            _savedPos = pivot.position;
            pivot.position = car.TransformPoint(new Vector3(mirroredX, local.y, local.z));
        }

        [HarmonyPostfix]
        public static void Postfix()
        {
            if (_pivot != null)
            {
                _pivot.position = _savedPos;
                _pivot = null;
            }
        }
    }

    /// <summary>
    /// ProximityToggle switches areas on/off depending on how far Player 1 is. With two players:
    ///  - the area's content (toEnable, e.g. a shop interior) is on while EITHER player is near;
    ///  - the outside world (toDisable: sun, snow, engine sounds) goes off only when P1 is near and
    ///    P2 too. Otherwise it stays on for whoever is still outside, and ProximityToggleVisibility
    ///    hides it per camera for the player who is indoors.
    /// </summary>
    [HarmonyPatch(typeof(ProximityToggle), "Update")]
    public static class ProximityToggleUpdatePatch
    {
        private static readonly FieldInfo? HasToggled =
            typeof(ProximityToggle).GetField("hasToggled", BindingFlags.Instance | BindingFlags.NonPublic);

        [HarmonyPrefix]
        public static bool Prefix(ProximityToggle __instance)
        {
            if (!SplitscreenManager.SplitActive || HasToggled == null) return true;
            if (!ProximityToggleVisibility.FollowsP1(__instance)) return true; // not a P1 toggle: untouched

            var p2 = SplitscreenManager.P2CharacterObject;
            if (p2 == null) return true;

            Vector3 here = __instance.transform.position;
            float threshold = __instance.distanceThreashold;
            bool near1 = Vector3.Distance(here, __instance.target.position) < threshold;
            bool near2 = Vector3.Distance(here, p2.transform.position) < threshold;
            bool toggled = (bool)HasToggled.GetValue(__instance);

            if (near1 || near2)
            {
                if (!toggled) HasToggled.SetValue(__instance, true);
                foreach (var go in __instance.toEnable)
                    if (go != null && !go.activeSelf) go.SetActive(true);

                bool outsideStillOn = !(near1 && near2);
                foreach (var go in __instance.toDisable)
                    if (go != null && go.activeSelf != outsideStillOn) go.SetActive(outsideStillOn);
            }
            else if (toggled)
            {
                HasToggled.SetValue(__instance, false);
                foreach (var go in __instance.toDisable) if (go != null) go.SetActive(true);
                foreach (var go in __instance.toEnable) if (go != null) go.SetActive(false);
            }
            return false;
        }
    }

    /// <summary>
    /// DeathManager reloads the whole world when Player 1's warmth reaches 0. With two players, if only
    /// one is down the other must keep playing in the same world: P1's death then just collapses P1 and,
    /// after the death time, gets P1 back up next to the truck. When both are down at the same time the
    /// game's own death (world reload) is left to run.
    /// </summary>
    [HarmonyPatch(typeof(DeathManager), "Update")]
    public static class DeathManagerUpdatePatch
    {
        private static readonly FieldInfo? GuyField =
            typeof(DeathManager).GetField("guy", BindingFlags.Instance | BindingFlags.NonPublic);

        [HarmonyPrefix]
        public static bool Prefix(DeathManager __instance)
        {
            if (!SplitscreenManager.SplitActive || GuyField == null) return true;
            var guy = GuyField.GetValue(__instance) as sCharacterInteraction;
            if (guy == null || guy.hud == null) return true;

            bool p2Dead = SplitscreenManager.P2Dead;

            if (__instance.dying)
            {
                if (p2Dead) return true; // both down: the game finishes the death (world reload)

                __instance.deathTime -= Time.unscaledDeltaTime;
                if (__instance.deathTime <= 0f)
                    RespawnP1(__instance, guy);
                return false;
            }

            if (guy.hud.temperature == 0f)
            {
                if (p2Dead)
                {
                    __instance.deathTime = SplitscreenManager.DeathSeconds;
                    return true; // P2 is already down: the game handles it
                }

                if (!guy.hud.navigation.car.GuyActive)
                    guy.SetActive(a: true);
                __instance.dying = true;
                __instance.deathTime = SplitscreenManager.DeathSeconds;
                guy.GetComponent<Animator>().SetBool("Move", value: false);
                guy.GetComponent<Animator>().SetBool("Dead", value: true);
                guy.GetComponent<sCharacterController>().enabled = false;
                Plugin.Log.LogInfo("[Splitscreen] Player 1 froze to death (Player 2 keeps playing).");
                return false;
            }

            return true;
        }

        private static void RespawnP1(DeathManager dm, sCharacterInteraction guy)
        {
            dm.dying = false;
            dm.deathTime = SplitscreenManager.DeathSeconds;

            var cc = guy.GetComponent<sCharacterController>();
            SplitscreenManager.StandUp(guy.GetComponent<Animator>());
            if (cc != null)
            {
                cc.enabled = true;
                cc.MoveTo(SplitscreenManager.SpawnPointNearCar(driverSide: true));
                cc.velocity = Vector3.zero;
            }
            guy.hud.temperature = guy.hud.temperatureLimit;
            Plugin.Log.LogInfo("[Splitscreen] Player 1 is back up.");

            // The game's respawn starts the player in the truck; the same call it uses at level start
            guy.SetActive(a: false);
        }
    }

    /// <summary>
    /// NPC heads (sNPCAppearence) look at the character they found at level load (Player 1) or at the
    /// truck when P1 is in it. With two players an NPC looks at the nearest player who is on foot, and
    /// only at the truck when nobody is.
    /// </summary>
    [HarmonyPatch(typeof(sNPCAppearence), "Update")]
    public static class NpcHeadLookPatch
    {
        private const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic;
        private static readonly FieldInfo? HeadField = typeof(sNPCAppearence).GetField("head", F);
        private static readonly FieldInfo? CarField = typeof(sNPCAppearence).GetField("car", F);
        private static readonly FieldInfo? InfoField = typeof(sNPCAppearence).GetField("info", F);
        private static readonly FieldInfo? AnimField = typeof(sNPCAppearence).GetField("anim", F);
        private const float HeadTurnSpeed = 6f; // higher = snappier (about 0.4 s to settle)
        private static readonly System.Collections.Generic.Dictionary<int, Quaternion> _smoothed = new();

        [HarmonyPrefix]
        public static bool Prefix(sNPCAppearence __instance)
        {
            if (!SplitscreenManager.SplitActive) return true;
            if (HeadField == null || CarField == null || InfoField == null || AnimField == null) return true;

            var head = HeadField.GetValue(__instance) as Transform;
            var car = CarField.GetValue(__instance) as sCarController;
            var info = InfoField.GetValue(__instance) as sNPCInformation;
            var anim = AnimField.GetValue(__instance) as Animator;
            var p1 = SplitscreenManager.P1CharacterObject;
            var p2 = SplitscreenManager.P2CharacterObject;
            if (head == null || car == null || info == null || p1 == null || p2 == null) return true;

            // Same first branch as the game
            if (info.IsDead(__instance.npcIndex) || EndingManager.instance.currentEnding == EndingManager.Ending.shutdown)
            {
                if (anim != null) anim.enabled = false;
                return false;
            }

            Vector3 here = __instance.transform.position;
            Transform? target = null;
            float best = float.MaxValue;

            if (car.GuyActive) // P1 on foot
            {
                best = Vector3.Distance(here, p1.transform.position);
                target = p1.transform;
            }
            if (!SplitscreenManager.IsP2InCar) // P2 on foot
            {
                float d = Vector3.Distance(here, p2.transform.position);
                if (d < best) { best = d; target = p2.transform; }
            }

            Vector3 lookAt = target != null ? target.position + Vector3.up : car.transform.position;
            Vector3 dir = lookAt - head.position;
            Quaternion desired = dir.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(dir, Vector3.up) : head.rotation;

            // Turn smoothly (also when the nearest player changes) instead of snapping like head.LookAt
            int id = __instance.GetInstanceID();
            if (!_smoothed.TryGetValue(id, out var current)) current = head.rotation;
            current = Quaternion.Slerp(current, desired, 1f - Mathf.Exp(-HeadTurnSpeed * Time.deltaTime));
            _smoothed[id] = current;
            head.rotation = current;
            return false;
        }
    }

    /// <summary>
    /// FirstPersonCameraHack.Toggle switches shared truck objects on/off for everyone. While splitscreen
    /// runs, ViewModeVisibility decides per camera instead, so the game's own switch is skipped.
    /// </summary>
    [HarmonyPatch(typeof(FirstPersonCameraHack), "Toggle")]
    public static class FirstPersonCameraHackTogglePatch
    {
        [HarmonyPrefix]
        public static bool Prefix(FirstPersonCameraHack __instance)
        {
            return !ViewModeVisibility.Owns(__instance);
        }
    }

    /// <summary>
    /// FirstPersonCameraHack reads players[0].cameraPressed (the C key). The hack cloned with
    /// P2's camera rig would otherwise follow P1's key, so feed it P2's own camera input.
    /// </summary>
    [HarmonyPatch(typeof(FirstPersonCameraHack), "Update")]
    public static class FirstPersonCameraHackPatch
    {
        [HarmonyPrefix]
        public static void Prefix(FirstPersonCameraHack __instance, out bool __state)
        {
            __state = false;
            var p2Rig = SplitscreenManager.P2Rig;
            if (p2Rig == null || __instance.realCamera != p2Rig) return;
            if (sInputManager.players == null || sInputManager.players.Length == 0) return;

            __state = true;
            _saved = sInputManager.players[0].cameraPressed;
            sInputManager.players[0].cameraPressed = SplitscreenManager.GetP2CameraPressed();
        }

        [HarmonyPostfix]
        public static void Postfix(bool __state)
        {
            if (__state && sInputManager.players != null && sInputManager.players.Length > 0)
                sInputManager.players[0].cameraPressed = _saved;
        }

        private static bool _saved;
    }

    /// <summary>
    /// F6 testing aid: when the keyboard controls Player 2, capture what the game just read
    /// from the normal bindings into SplitscreenManager.Raw and neutralise Player 1's input
    /// (pause stays available), so only one player reacts to the keys.
    /// </summary>
    [HarmonyPatch(typeof(sInputManager), "Update")]
    public static class InputManagerUpdatePatch
    {
        [HarmonyPostfix]
        public static void Postfix(sInputManager __instance)
        {
            if (sInputManager.players == null || sInputManager.players.Length == 0) return;
            if (__instance != sInputManager.players[0]) return;
            if (!SplitscreenManager.ControlP2) return;

            SplitscreenManager.Raw = new SplitscreenManager.RawInput
            {
                PlayerInput = __instance.playerInput,
                DriveInput = __instance.driveInput,
                CameraLook = __instance.cameraLook,
                SelectPressed = __instance.selectPressed,
                BackPressed = __instance.backPressed,
                BreakHeld = __instance.breakPressed,
                CameraPressed = __instance.cameraPressed || SplitscreenManager.Raw.CameraPressed,
            };

            __instance.driveInput = Vector2.zero;
            __instance.playerInput = Vector2.zero;
            __instance.radioInput = Vector2.zero;
            __instance.cameraLook = Vector2.zero;
            __instance.radioPressed = false;
            __instance.selectPressed = false;
            __instance.selectReleased = false;
            __instance.backPressed = false;
            __instance.backReleased = false;
            __instance.breakPressed = false;
            __instance.inventoryPressed = false;
            __instance.inventoryReleased = false;
            __instance.inventoryHeld = false;
            __instance.headlightsPressed = false;
            __instance.cameraPressed = false;
            __instance.resetPressed = false;
            __instance.resetHeld = false;
            __instance.mapPressed = false;
            __instance.hornPressed = false;
        }
    }

    /// <summary>
    /// Applies custom player skins to Player 1 and Player 2.
    /// Patches sCosmeticAppearance.SetAppearance to apply custom textures to head/head and cylinder.
    /// </summary>
    [HarmonyPatch(typeof(sCosmeticAppearance), "SetAppearance")]
    public static class CosmeticAppearanceSetAppearancePatch
    {
        [HarmonyPostfix]
        public static void Postfix(sCosmeticAppearance __instance)
        {
            try
            {
                PlayerCustomization.ApplySkin(__instance);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[Splitscreen] Error applying custom skin: {ex.Message}");
            }
        }
    }
}
