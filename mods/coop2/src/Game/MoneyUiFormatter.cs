using System;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace EasyDeliveryCoLanCoop;

internal sealed class MoneyUiFormatter : MonoBehaviour
{
    private static readonly BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    private object? _hud;
    private object? _moneyText;
    private PropertyInfo? _textProp;

    private float _nextResolveAt;
    private float _nextUpdateAt;

    private void Update()
    {
        if (string.Equals(Plugin.GetEffectiveNetworkMode(), "Off", StringComparison.OrdinalIgnoreCase))
        {
            _hud = null;
            _moneyText = null;
            _textProp = null;
            return;
        }

        var now = Time.unscaledTime;

        if (now >= _nextResolveAt)
        {
            _nextResolveAt = now + 2.0f;
            ResolveTargets();
        }

        if (!IsUnityObjectAlive(_moneyText) || _textProp == null)
        {
            _moneyText = null;
            _textProp = null;
            return;
        }

        if (now < _nextUpdateAt)
            return;
        _nextUpdateAt = now + 0.2f;

        if (!GameAccess.TryReadHudMoney(out var moneyInt))
            return;

        // Display with 2 decimals (e.g. 0,00 for RU locale).
        var formatted = ((float)moneyInt).ToString("F2", CultureInfo.CurrentCulture);

        try
        {
            var current = _textProp.GetValue(_moneyText, null) as string ?? string.Empty;
            var next = ReplaceNumberSegment(current, formatted);
            if (!string.Equals(current, next, StringComparison.Ordinal))
                _textProp.SetValue(_moneyText, next, null);
        }
        catch
        {
            // If UI objects changed (scene reload), force re-resolve.
            _moneyText = null;
            _textProp = null;
        }
    }

    private void ResolveTargets()
    {
        if (!IsUnityObjectAlive(_hud))
            _hud = null;

        if (_hud == null)
            _hud = TryFindObjectOfTypeByName("sHUD");

        if (_hud == null)
        {
            _moneyText = null;
            _textProp = null;
            return;
        }

        if (_moneyText != null && _textProp != null)
            return;

        var hudType = _hud.GetType();

        // 1) Prefer explicit HUD fields/properties with name containing "money" and having a writable string .text
        var best = FindMoneyTextOnObject(_hud, hudType);
        if (best != null)
        {
            _moneyText = best.Value.Target;
            _textProp = best.Value.TextProp;
            return;
        }

        // 2) Fallback: scan child components of HUD GameObject.
        if (_hud is Component comp && comp != null)
        {
            if (!GameAccess.TryReadHudMoney(out var moneyInt))
                moneyInt = 0;

            var moneyToken = moneyInt.ToString(CultureInfo.CurrentCulture);
            Component[] comps;
            try
            {
                comps = comp.GetComponentsInChildren<Component>(includeInactive: true);
            }
            catch
            {
                _hud = null;
                _moneyText = null;
                _textProp = null;
                return;
            }

            for (var i = 0; i < comps.Length; i++)
            {
                var c = comps[i];
                if (c == null)
                    continue;

                if (!TryGetWritableTextProperty(c.GetType(), out var tp))
                    continue;

                try
                {
                    var txt = tp.GetValue(c, null) as string;
                    if (string.IsNullOrWhiteSpace(txt))
                        continue;

                    var go = c.gameObject;
                    if (go == null)
                        continue;

                    var lowName = go.name?.ToLowerInvariant() ?? string.Empty;
                    if (lowName.Contains("money") || lowName.Contains("cash") || lowName.Contains("coins"))
                    {
                        _moneyText = c;
                        _textProp = tp;
                        return;
                    }

                    if (txt.Contains(moneyToken, StringComparison.Ordinal))
                    {
                        _moneyText = c;
                        _textProp = tp;
                        return;
                    }
                }
                catch
                {
                    // ignore
                }
            }
        }
    }

    private static (object Target, PropertyInfo TextProp)? FindMoneyTextOnObject(object instance, Type t)
    {
        // Fields
        var fields = t.GetFields(Any);
        for (var i = 0; i < fields.Length; i++)
        {
            var f = fields[i];
            if (f == null)
                continue;
            if (string.IsNullOrEmpty(f.Name) || !f.Name.ToLowerInvariant().Contains("money"))
                continue;

            object? v;
            try { v = f.GetValue(instance); } catch { continue; }
            if (v == null)
                continue;

            if (TryGetWritableTextProperty(v.GetType(), out var tp))
                return (v, tp);
        }

        // Properties
        var props = t.GetProperties(Any);
        for (var i = 0; i < props.Length; i++)
        {
            var p = props[i];
            if (p == null)
                continue;
            if (string.IsNullOrEmpty(p.Name) || !p.Name.ToLowerInvariant().Contains("money"))
                continue;

            object? v;
            try { v = p.GetValue(instance, null); } catch { continue; }
            if (v == null)
                continue;

            if (TryGetWritableTextProperty(v.GetType(), out var tp))
                return (v, tp);
        }

        return null;
    }

    private static bool TryGetWritableTextProperty(Type t, out PropertyInfo prop)
    {
        prop = null!;
        try
        {
            var p = t.GetProperty("text", Any);
            if (p == null)
                return false;
            if (p.PropertyType != typeof(string))
                return false;
            if (!p.CanWrite)
                return false;

            prop = p;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string ReplaceNumberSegment(string current, string replacement)
    {
        if (string.IsNullOrEmpty(current))
            return replacement;

        var start = -1;
        for (var i = 0; i < current.Length; i++)
        {
            if (char.IsDigit(current[i]))
            {
                start = i;
                break;
            }
        }

        if (start < 0)
            return replacement;

        var end = -1;
        for (var i = current.Length - 1; i >= 0; i--)
        {
            if (char.IsDigit(current[i]))
            {
                end = i;
                break;
            }
        }

        if (end < start)
            return replacement;

        return current.Substring(0, start) + replacement + current.Substring(end + 1);
    }

    private static object? TryFindObjectOfTypeByName(string typeName)
    {
        try
        {
            var type = FindTypeInAssemblyCSharp(typeName);
            if (type == null)
                return null;

            // UnityEngine.Object.FindObjectOfType(Type)
            var find = typeof(UnityEngine.Object).GetMethod("FindObjectOfType", Any, null, new[] { typeof(Type) }, null);
            if (find == null)
                return null;

            return find.Invoke(null, new object?[] { type });
        }
        catch
        {
            return null;
        }
    }

    private static Type? FindTypeInAssemblyCSharp(string typeName)
    {
        try
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (var i = 0; i < assemblies.Length; i++)
            {
                var a = assemblies[i];
                if (a == null)
                    continue;

                var n = a.GetName().Name;
                if (!string.Equals(n, "Assembly-CSharp", StringComparison.Ordinal))
                    continue;

                return a.GetType(typeName, throwOnError: false, ignoreCase: false);
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static bool IsUnityObjectAlive(object? value)
    {
        if (value == null)
            return false;

        if (value is UnityEngine.Object uo)
            return uo != null;

        return true;
    }
}
