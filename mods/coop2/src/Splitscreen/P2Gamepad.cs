using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;

namespace EasyDeliveryCoLanCoop;

/// <summary>
/// Player 2's controller, mapped exactly like Player 1's. The game reads input from named actions
/// ("Movement", "AltMovement", "Click", "Break", "Camera", "ChangeCamera"...) bound in its own input asset.
/// Instead of guessing which button does what, P2 gets a private copy of that asset restricted to P2's
/// pad, so every action means the same thing it means for P1 (and any rebinding the game ships follows).
/// </summary>
internal static class P2Gamepad
{
    private static InputActionAsset? _asset;
    private static Gamepad? _pad;
    private static float _nextRefresh;

    // The game has more than one action set with the same action names (Player / PlayerABSwap swaps the
    // A and B buttons). P1 uses one of them; P2 must read each action from the set P1's own input reads.
    private static readonly Dictionary<string, string> Qualified = new();
    private static readonly (string action, string field)[] Used =
    {
        ("Movement", "_movement"), ("AltMovement", "_altMovement"), ("Click", "_select"), ("Break", "_back"),
        ("Camera", "_cameraLook"), ("ChangeCamera", "_camera"), ("Headlights", "_headlights"), ("Horn", "_horn"), ("Inventory", "_inventory"),
    };

    internal static bool Active => _asset != null && _pad != null && _pad.added;
    internal static string PadName => _pad != null ? _pad.displayName : "none";

    internal static void Setup(InputActionAsset source, Gamepad pad, PlayerInput? player1)
    {
        if (_asset != null && _pad == pad) { ResolveSets(player1); return; }
        Teardown();

        try
        {
            var copy = InputActionAsset.FromJson(source.ToJson());
            copy.devices = new InputDevice[] { pad }; // only this pad can drive these actions
            copy.Enable();
            _asset = copy;
            _pad = pad;
            ResolveSets(player1);
            Plugin.Log.LogInfo($"[Devices] Player 2's actions bound to '{pad.displayName}' (same mapping as Player 1).");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[Devices] Could not copy the game's actions for Player 2 ({ex.Message}); using a basic controller mapping.");
            Teardown();
        }
    }

    internal static void Teardown()
    {
        if (_asset != null)
        {
            try { _asset.Disable(); } catch { }
            UnityEngine.Object.Destroy(_asset);
        }
        _asset = null;
        _pad = null;
        Qualified.Clear();
    }

    private static InputAction? Action(string name) =>
        _asset?.FindAction(Qualified.TryGetValue(name, out var qualified) ? qualified : name, throwIfNotFound: false);

    /// <summary>Looks up which action set each of Player 1's actions comes from.</summary>
    internal static void ResolveSets(PlayerInput? player1)
    {
        var input = player1 != null ? player1.GetComponent<sInputManager>() : null;
        if (input == null && sInputManager.players != null && sInputManager.players.Length > 0)
            input = sInputManager.players[0];
        if (input == null) return;

        foreach (var (action, field) in Used)
        {
            var f = typeof(sInputManager).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic);
            if (f?.GetValue(input) is InputAction a && a.actionMap != null)
            {
                string qualified = a.actionMap.name + "/" + a.name;
                if (!Qualified.TryGetValue(action, out var old) || old != qualified)
                {
                    Qualified[action] = qualified;
                    Plugin.Log.LogInfo($"[Devices] Player 2's '{action}' reads {qualified} (what Player 1 uses).");
                }
            }
        }
    }

    /// <summary>The game can switch sets while running (its button-swap option): follow it.</summary>
    internal static void Refresh(PlayerInput? player1)
    {
        if (!Active || Time.unscaledTime < _nextRefresh) return;
        _nextRefresh = Time.unscaledTime + 1f;
        ResolveSets(player1);
    }

    private static Vector2 Vec(string name)
    {
        var a = Action(name);
        return a != null ? a.ReadValue<Vector2>() : Vector2.zero;
    }

    private static bool Pressed(string name) => Action(name)?.WasPressedThisFrame() ?? false;
    private static bool Held(string name) => Action(name)?.IsPressed() ?? false;

    /// <summary>On foot the game uses "AltMovement"; driving uses "Movement" (see sInputManager).</summary>
    internal static Vector2 Movement(bool forDriving) => Vec(forDriving ? "Movement" : "AltMovement");
    internal static Vector2 Look => Vec("Camera");
    internal static bool SelectPressed => Pressed("Click");
    internal static bool BackPressed => Pressed("Break");
    internal static bool BrakeHeld => Held("Break");
    internal static bool InventoryPressed => Pressed("Inventory");
    internal static bool InventoryReleased => Action("Inventory")?.WasReleasedThisFrame() ?? false;
    internal static bool CameraPressed => Pressed("ChangeCamera");
    internal static bool HeadlightsPressed => Pressed("Headlights");
    internal static bool HeadlightsHeld => Held("Headlights");
    internal static bool HornPressed => Action("Horn")?.WasPerformedThisFrame() ?? false; // sInputManager uses WasPerformedThisFrame
}
