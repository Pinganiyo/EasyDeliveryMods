using System;
using System.Globalization;
using System.Text;
using BepInEx;
using UnityEngine;

namespace EasyDeliveryCoLanCoop;

internal static class PlayerPositionsStore
{
    private sealed class Entry
    {
        public Vector3 Pos;
        public Quaternion Rot;
        public float LastUpdateAt;
    }

    private static readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private static string _currentSaveId = "default";
    private static float _nextSaveIdRefreshAt;
    private static float _nextFlushAt;
    private static bool _dirty;
    private static bool _loggedSaveId;

    internal static string CurrentSaveId => _currentSaveId;

    internal static void HostRefreshSaveIdNow()
    {
        if (!Plugin.PlayerPositionsEnabled.Value)
            return;

        var id = TryGetCurrentSaveId(out _);
        if (string.Equals(id, _currentSaveId, StringComparison.Ordinal))
            return;

        _currentSaveId = id;
        _entries.Clear();
        _dirty = false;
        TryLoadFromDisk(_currentSaveId);
    }

    internal static void HostTick(float now)
    {
        if (!Plugin.PlayerPositionsEnabled.Value)
            return;

        if (now >= _nextSaveIdRefreshAt)
        {
            _nextSaveIdRefreshAt = now + 2.0f;

            var id = TryGetCurrentSaveId(out var source);
            if (Plugin.DebugLogs.Value && (!_loggedSaveId || !string.Equals(id, _currentSaveId, StringComparison.Ordinal)))
            {
                _loggedSaveId = true;
                var path = GetFilePath(id);
                Plugin.Log.LogInfo($"Positions: saveId='{id}' source={source} file='{path}'");
            }

            if (!string.Equals(id, _currentSaveId, StringComparison.Ordinal))
            {
                _currentSaveId = id;
                _entries.Clear();
                _dirty = false;
                TryLoadFromDisk(_currentSaveId);
            }
        }

        if (now >= _nextFlushAt)
        {
            _nextFlushAt = now + 2.0f;
            if (_dirty)
                TryFlushToDisk(_currentSaveId);
        }
    }

    internal static void HostUpdatePlayer(string nickname, Vector3 pos, Quaternion rot, float now)
    {
        if (!Plugin.PlayerPositionsEnabled.Value)
            return;

        nickname = Plugin.SanitizeNickname(nickname);
        if (!_entries.TryGetValue(nickname, out var e))
        {
            e = new Entry();
            _entries[nickname] = e;
        }

        e.Pos = pos;
        e.Rot = rot;
        e.LastUpdateAt = now;
        _dirty = true;
    }

    internal static bool TryGetSnapshot(out Dictionary<string, (Vector3 Pos, Quaternion Rot)> snapshot)
    {
        snapshot = new Dictionary<string, (Vector3, Quaternion)>(StringComparer.Ordinal);
        if (!Plugin.PlayerPositionsEnabled.Value)
            return false;

        foreach (var kv in _entries)
            snapshot[kv.Key] = (kv.Value.Pos, kv.Value.Rot);

        return snapshot.Count > 0;
    }

    internal static bool TryGetForNickname(string nickname, out Vector3 pos, out Quaternion rot)
    {
        nickname = Plugin.SanitizeNickname(nickname);
        if (_entries.TryGetValue(nickname, out var e))
        {
            pos = e.Pos;
            rot = e.Rot;
            return true;
        }

        pos = default;
        rot = default;
        return false;
    }

    private static string TryGetCurrentSaveId(out string source)
    {
        source = "fallback";

        var mapSuffix = string.Empty;
        if (GameAccess.TryReadCurrentMapBuildIndex(out var mapBuildIndex) && mapBuildIndex >= 0)
            mapSuffix = $"__map{mapBuildIndex}";

        var overrideId = Plugin.PlayerPositionsSaveIdOverride.Value;
        if (!string.IsNullOrWhiteSpace(overrideId))
        {
            source = "override";
            return Plugin.SanitizeFileName(overrideId + mapSuffix);
        }

        if (GameAccess.TryReadSaveId(out var id) && !string.IsNullOrWhiteSpace(id))
        {
            source = "auto";
            return Plugin.SanitizeFileName(id + mapSuffix);
        }

        return string.IsNullOrEmpty(mapSuffix) ? "default" : Plugin.SanitizeFileName("default" + mapSuffix);
    }

    private static string GetFilePath(string saveId)
    {
        saveId = Plugin.SanitizeFileName(saveId);
        var dir = System.IO.Path.Combine(Paths.ConfigPath, "EasyDeliveryCoLanCoop.Positions");
        return System.IO.Path.Combine(dir, $"{saveId}.txt");
    }

    private static void TryLoadFromDisk(string saveId)
    {
        try
        {
            var path = GetFilePath(saveId);
            if (!System.IO.File.Exists(path))
                return;

            var lines = System.IO.File.ReadAllLines(path, Encoding.UTF8);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                if (line.StartsWith("#", StringComparison.Ordinal))
                    continue;

                var parts = line.Split('\t');
                if (parts.Length < 9)
                    continue;

                var nick = Plugin.SanitizeNickname(parts[0]);
                if (!TryParseFloat(parts[1], out var px)) continue;
                if (!TryParseFloat(parts[2], out var py)) continue;
                if (!TryParseFloat(parts[3], out var pz)) continue;
                if (!TryParseFloat(parts[4], out var qx)) continue;
                if (!TryParseFloat(parts[5], out var qy)) continue;
                if (!TryParseFloat(parts[6], out var qz)) continue;
                if (!TryParseFloat(parts[7], out var qw)) continue;

                var pos = new Vector3(px, py, pz);
                var rot = new Quaternion(qx, qy, qz, qw);

                _entries[nick] = new Entry { Pos = pos, Rot = rot, LastUpdateAt = 0f };
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"PlayerPositionsStore load failed: {ex.GetType().Name}");
        }
    }

    private static void TryFlushToDisk(string saveId)
    {
        try
        {
            var path = GetFilePath(saveId);
            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir) && !System.IO.Directory.Exists(dir))
                System.IO.Directory.CreateDirectory(dir);

            var sb = new StringBuilder(1024);
            sb.AppendLine("# EasyDeliveryCoLanCoop player positions");
            sb.AppendLine("# nickname\tpx\tpy\tpz\tqx\tqy\tqz\tqw");

            foreach (var kv in _entries)
            {
                var nick = kv.Key;
                var e = kv.Value;
                sb.Append(nick).Append('\t')
                    .Append(e.Pos.x.ToString("R", CultureInfo.InvariantCulture)).Append('\t')
                    .Append(e.Pos.y.ToString("R", CultureInfo.InvariantCulture)).Append('\t')
                    .Append(e.Pos.z.ToString("R", CultureInfo.InvariantCulture)).Append('\t')
                    .Append(e.Rot.x.ToString("R", CultureInfo.InvariantCulture)).Append('\t')
                    .Append(e.Rot.y.ToString("R", CultureInfo.InvariantCulture)).Append('\t')
                    .Append(e.Rot.z.ToString("R", CultureInfo.InvariantCulture)).Append('\t')
                    .Append(e.Rot.w.ToString("R", CultureInfo.InvariantCulture))
                    .AppendLine();
            }

            System.IO.File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            _dirty = false;
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"PlayerPositionsStore flush failed: {ex.GetType().Name}");
        }
    }

    private static bool TryParseFloat(string s, out float v)
        => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
}
