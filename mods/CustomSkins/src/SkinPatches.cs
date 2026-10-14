using System;
using HarmonyLib;

namespace EasyDeliveryCoCustomSkins;

/// <summary>
/// Applies custom PNG skins whenever the game sets a character appearance,
/// so Player 1 (and any P2 clone, if Splitscreen is loaded) gets its texture.
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
            SkinPlugin.Log.LogError($"[CustomSkins] Error applying custom skin: {ex.Message}");
        }
    }
}
