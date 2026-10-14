using System;

namespace EasyDeliveryCoLanCoop;

internal static class ExternalMods
{
    internal static bool IsCustomTruckShopLoaded()
    {
        // CustomTruckMecanic V0.1 + LOGS.dll has assembly title/product "CustomTruckShop".
        // In runtime it will typically be loaded as an assembly named "CustomTruckShop".
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var n = asm.GetName().Name;
                if (string.Equals(n, "CustomTruckShop", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            catch
            {
                // ignore
            }
        }

        return false;
    }
}
