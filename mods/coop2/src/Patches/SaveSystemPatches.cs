using System.Reflection;
using HarmonyLib;

namespace EasyDeliveryCoLanCoop.Patches;

// Патчим по имени типа, т.к. у нас нет compile-time ссылок на Assembly-CSharp.
[HarmonyPatch]
internal static class SaveSystemPatches
{
    private static Type? TargetType()
    {
        return AccessTools.TypeByName("sSaveSystem");
    }

    [HarmonyTargetMethods]
    private static IEnumerable<MethodBase> TargetMethods()
    {
        var t = TargetType();
        if (t == null)
            yield break;

        foreach (var name in new[] { "SetString", "DeleteKey" })
        {
            var m = AccessTools.Method(t, name);
            if (m != null)
                yield return m;
        }
    }

    [HarmonyPrefix]
    private static void Prefix(MethodBase __originalMethod, object[] __args)
    {
        // Это минимальная синхронизация: дельты сейва.
        // Хост рассылает всем, клиенты отправляют запрос хосту.

        var mgr = LanCoopManager.Instance;
        if (mgr == null)
            return;

        if (mgr.IsApplyingRemote)
            return;

        if (__originalMethod.Name == "SetString" && __args.Length >= 2)
        {
            var key = __args[0]?.ToString() ?? string.Empty;
            var value = __args[1]?.ToString() ?? string.Empty;
            if (key.Length != 0 && Plugin.IsSaveKeyAllowedForWorldSync(key))
                mgr.SendSaveDelta(key, value);
        }
        else if (__originalMethod.Name == "DeleteKey" && __args.Length >= 1)
        {
            var key = __args[0]?.ToString() ?? string.Empty;
            if (key.Length != 0 && Plugin.IsSaveKeyAllowedForWorldSync(key))
                mgr.SendSaveDelete(key);
        }
    }
}
