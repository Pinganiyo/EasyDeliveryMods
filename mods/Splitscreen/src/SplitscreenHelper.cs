using System;
using System.Reflection;
using UnityEngine;

namespace EasyDeliveryCoSplitscreen;

/// <summary>
/// Static helpers for splitscreen setup and maintenance.
/// </summary>
internal static class SplitscreenHelper
{
    private static readonly string[] ScriptsToDisable = new[]
    {
        "PlayerManager",
    };

    /// <summary>Finds the local player character controller.</summary>
    internal static sCharacterController? FindLocalCharacterController()
    {
        try
        {
            var all = UnityEngine.Object.FindObjectsByType<sCharacterController>(FindObjectsSortMode.None);
            foreach (var cc in all)
            {
                if (cc != null && !cc.gameObject.name.Contains("P2"))
                    return cc;
            }
        }
        catch (Exception ex)
        {
            SplitPlugin.Log.LogError($"[Splitscreen] FindLocalCharacterController error: {ex.Message}");
        }
        return null;
    }

    /// <summary>Disable singleton scripts on P2 clone so they don't interfere.</summary>
    internal static void DisableGameSingletons(GameObject clone)
    {
        try
        {
            var behaviours = clone.GetComponentsInChildren<MonoBehaviour>(includeInactive: true);
            foreach (var mb in behaviours)
            {
                if (mb == null) continue;
                var name = mb.GetType().Name;
                foreach (var disabled in ScriptsToDisable)
                {
                    if (string.Equals(name, disabled, StringComparison.OrdinalIgnoreCase))
                    {
                        mb.enabled = false;
                        break;
                    }
                }
            }

            // Remove duplicate audio listener
            var al = clone.GetComponentInChildren<AudioListener>(includeInactive: true);
            if (al != null)
                UnityEngine.Object.Destroy(al);
        }
        catch (Exception ex)
        {
            SplitPlugin.Log.LogWarning($"[Splitscreen] DisableGameSingletons: {ex.Message}");
        }
    }

    /// <summary>Count connected legacy joysticks (fallback for gamepad detection).</summary>
    internal static int CountConnectedJoysticks()
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
