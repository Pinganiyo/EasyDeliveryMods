namespace EasyDeliveryCoCustomSkins;

/// <summary>
/// Public entry point for other mods (e.g. Splitscreen) to request a P2 skin
/// apply without taking a compile-time dependency. Called via reflection.
/// </summary>
public static class SkinApplier
{
    public static void ApplyP2Skin(object appearance)
    {
        if (appearance is sCosmeticAppearance app)
            PlayerCustomization.ApplySkin(app, 2);
    }

    public static void ApplyP1Skin(object appearance)
    {
        if (appearance is sCosmeticAppearance app)
            PlayerCustomization.ApplySkin(app, 1);
    }
}
