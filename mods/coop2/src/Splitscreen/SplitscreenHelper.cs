using System.Reflection;
using UnityEngine;

namespace EasyDeliveryCoLanCoop;

/// <summary>
/// Static helpers for splitscreen: finding the player object,
/// disabling singleton game scripts on the P2 clone, and copying URP camera settings.
/// </summary>
internal static class SplitscreenHelper
{

    // Only disable global singletons that should not run multiple times.
    // sCharacterController, sCharacterInteraction, sPlayerHands, and sCharacterRagdoll
    // are kept ENABLED on P2 — our Harmony patches intercept them to provide P2's input.
    private static readonly string[] ScriptsToDisable = new[]
    {
        "sInputManager",
        "PlayerManager",
    };

    /// <summary>Finds the local player character controller GameObject.</summary>
    internal static Component? FindLocalCharacterController()
    {
        try
        {
            var allBehaviours = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
            foreach (var mb in allBehaviours)
            {
                if (mb == null) continue;
                var typeName = mb.GetType().Name;
                if (string.Equals(typeName, "sCharacterController", System.StringComparison.Ordinal))
                    return mb;
            }
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogError($"[Splitscreen] FindLocalCharacterController error: {ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// On the P2 clone, disable singleton scripts so they do not interfere with the real player.
    /// </summary>
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
                    if (string.Equals(name, disabled, System.StringComparison.OrdinalIgnoreCase))
                    {
                        mb.enabled = false;
                        break;
                    }
                }
            }

            // Also remove the duplicate AudioListener to avoid dual listener warnings
            var al = clone.GetComponentInChildren<AudioListener>(includeInactive: true);
            if (al != null)
                UnityEngine.Object.Destroy(al);
        }
        catch (System.Exception ex)
        {
            Plugin.Log.LogWarning($"[Splitscreen] DisableGameSingletons: {ex.Message}");
        }
    }
}
