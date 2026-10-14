using System.IO;
using System.Text;

namespace EasyDeliveryCoLanCoop;

internal static class NetMessages
{
    private const string DiscoveryMagic = "EDC_LAN_COOP_DISCOVERY";
    internal static byte[] BuildHello()
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write((byte)MessageType.Hello);
        bw.Write("EDC_LAN_COOP");
        bw.Write(Plugin.PluginVersion);
        return ms.ToArray();
    }

    internal static byte[] BuildWelcome(string selfKey)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write((byte)MessageType.Welcome);
        bw.Write(selfKey);
        bw.Write(Plugin.PluginVersion);
        return ms.ToArray();
    }

    internal static byte[] BuildDiscoveryAnnounce(int gamePort)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write((byte)MessageType.DiscoveryAnnounce);
        bw.Write(DiscoveryMagic);
        bw.Write(gamePort);
        bw.Write(Plugin.PluginVersion);
        return ms.ToArray();
    }

    internal static void TryHandleDiscovery(LanCoopManager mgr, UdpTransport transport, System.Net.IPEndPoint remote, byte[] data)
    {
        if (!mgr.IsClient)
            return;
        if (mgr.HasWelcome)
            return;
        if (mgr.ClientDiscoveryLocked)
            return;

        using var ms = new MemoryStream(data);
        using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        if (ms.Length < 2)
            return;

        var type = (MessageType)br.ReadByte();
        if (type != MessageType.DiscoveryAnnounce)
            return;

        var magic = br.ReadString();
        if (!string.Equals(magic, DiscoveryMagic, StringComparison.Ordinal))
            return;

        var gamePort = br.ReadInt32();
        _ = br.ReadString(); // version (currently unused)

        var server = new System.Net.IPEndPoint(remote.Address, gamePort);

        // Until we have a Welcome, allow discovery to set/override the server endpoint.
        var shouldUpdate = !transport.HasServer || transport.ServerEndPoint == null || !transport.ServerEndPoint.Equals(server);
        if (shouldUpdate)
        {
            transport.SetServer(server);
            Plugin.Log.LogInfo($"Discovered host: {server}");
        }

        transport.SendToServer(BuildHello());
    }

    internal static byte[] BuildSaveSet(string key, string value)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write((byte)MessageType.SaveSet);
        bw.Write(key);
        bw.Write(value);
        return ms.ToArray();
    }

    internal static byte[] BuildSaveDelete(string key)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write((byte)MessageType.SaveDelete);
        bw.Write(key);
        return ms.ToArray();
    }

    internal static byte[] BuildSaveFull(string saveId, bool wipeClientFirst, Dictionary<string, string?> save)
    {
        using var ms = new MemoryStream(16 * 1024);
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write((byte)MessageType.SaveFull);
        bw.Write(saveId ?? string.Empty);
        bw.Write(wipeClientFirst);
        bw.Write(save?.Count ?? 0);
        if (save != null)
        {
            foreach (var kv in save)
            {
                bw.Write(kv.Key ?? string.Empty);
                bw.Write(kv.Value ?? string.Empty);
            }
        }
        return ms.ToArray();
    }

    internal static byte[] BuildPlayerLocations(string saveId, Dictionary<string, (UnityEngine.Vector3 Pos, UnityEngine.Quaternion Rot)> positions)
    {
        using var ms = new MemoryStream(8 * 1024);
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write((byte)MessageType.PlayerLocations);
        bw.Write(saveId ?? string.Empty);
        bw.Write(positions?.Count ?? 0);
        if (positions != null)
        {
            foreach (var kv in positions)
            {
                bw.Write(kv.Key ?? string.Empty);
                bw.Write(kv.Value.Pos.x);
                bw.Write(kv.Value.Pos.y);
                bw.Write(kv.Value.Pos.z);
                bw.Write(kv.Value.Rot.x);
                bw.Write(kv.Value.Rot.y);
                bw.Write(kv.Value.Rot.z);
                bw.Write(kv.Value.Rot.w);
            }
        }
        return ms.ToArray();
    }

    internal static byte[] BuildMoneyDelta(int delta)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write((byte)MessageType.MoneyDelta);
        bw.Write(delta);
        return ms.ToArray();
    }

    internal static byte[] BuildMoneySet(int money)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write((byte)MessageType.MoneySet);
        bw.Write(money);
        return ms.ToArray();
    }

    internal static byte[] BuildClientState(string nickname, float px, float py, float pz, float qx, float qy, float qz, float qw, bool inCar, string heldPayloadName)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write((byte)MessageType.ClientState);
        bw.Write(Plugin.SanitizeNickname(nickname));
        bw.Write(px);
        bw.Write(py);
        bw.Write(pz);
        bw.Write(qx);
        bw.Write(qy);
        bw.Write(qz);
        bw.Write(qw);
        bw.Write(inCar);
        bw.Write(heldPayloadName ?? string.Empty);

        // Optional trailing controller pose for position persistence/teleporting.
        if (GameAccess.TryReadLocalPlayerControllerPose(out var cp, out var cr))
        {
            bw.Write(true);
            bw.Write(cp.x); bw.Write(cp.y); bw.Write(cp.z);
            bw.Write(cr.x); bw.Write(cr.y); bw.Write(cr.z); bw.Write(cr.w);
        }
        else
        {
            bw.Write(false);
        }

        // Optional trailing field: client's currently observed money.
        // Helps recover from occasional UDP loss of MoneyDelta packets.
        if (Plugin.SharedMoneyEnabled.Value && GameAccess.TryReadHudMoney(out var observedMoney))
        {
            bw.Write(true);
            bw.Write(observedMoney);
        }
        else
        {
            bw.Write(false);
        }
        return ms.ToArray();
    }

    internal static byte[] BuildClientCarState(float px, float py, float pz, float qx, float qy, float qz, float qw)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write((byte)MessageType.ClientCarState);
        bw.Write(px);
        bw.Write(py);
        bw.Write(pz);
        bw.Write(qx);
        bw.Write(qy);
        bw.Write(qz);
        bw.Write(qw);
        return ms.ToArray();
    }

    internal static byte[] BuildClientCarCargo(IReadOnlyList<(string Name, UnityEngine.Vector3 LocalPos, UnityEngine.Quaternion LocalRot)> cargo)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write((byte)MessageType.ClientCarCargo);

        bw.Write(cargo.Count);
        for (var i = 0; i < cargo.Count; i++)
        {
            bw.Write(cargo[i].Name ?? string.Empty);
            bw.Write(cargo[i].LocalPos.x);
            bw.Write(cargo[i].LocalPos.y);
            bw.Write(cargo[i].LocalPos.z);
            bw.Write(cargo[i].LocalRot.x);
            bw.Write(cargo[i].LocalRot.y);
            bw.Write(cargo[i].LocalRot.z);
            bw.Write(cargo[i].LocalRot.w);
        }

        return ms.ToArray();
    }

    internal static byte[] BuildClientCarSfx(byte sfxId, string? clipName = null, string? sourceName = null)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write((byte)MessageType.ClientCarSfx);
        bw.Write(sfxId);
        var clip = clipName ?? string.Empty;
        var source = sourceName ?? string.Empty;
        bw.Write(clip.Length > 0);
        if (clip.Length > 0)
            bw.Write(clip);
        bw.Write(source.Length > 0);
        if (source.Length > 0)
            bw.Write(source);
        return ms.ToArray();
    }

    internal static byte[] BuildCarSfx(string carKey, byte sfxId, string? clipName = null, string? sourceName = null)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write((byte)MessageType.CarSfx);
        bw.Write(carKey ?? string.Empty);
        bw.Write(sfxId);
        var clip = clipName ?? string.Empty;
        var source = sourceName ?? string.Empty;
        bw.Write(clip.Length > 0);
        if (clip.Length > 0)
            bw.Write(clip);
        bw.Write(source.Length > 0);
        if (source.Length > 0)
            bw.Write(source);
        return ms.ToArray();
    }

    internal static byte[] BuildWorldSnapshot(
        Dictionary<string, string?>? save,
        float? dayTime,
        int? money,
        IReadOnlyList<JobData>? jobs,
        IReadOnlyList<PlayerPose>? players,
        CarState? car,
        IReadOnlyList<CargoItem>? cargo)
    {
        using var ms = new MemoryStream(16 * 1024);
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);

        bw.Write((byte)MessageType.WorldSnapshot);

        if (save == null)
        {
            bw.Write(0);
        }
        else
        {
            bw.Write(save.Count);
            foreach (var kv in save)
            {
                bw.Write(kv.Key);
                bw.Write(kv.Value ?? string.Empty);
            }
        }

        bw.Write(dayTime.HasValue);
        if (dayTime.HasValue)
            bw.Write(dayTime.Value);

        bw.Write(money.HasValue);
        if (money.HasValue)
            bw.Write(money.Value);

        if (jobs == null)
        {
            bw.Write(0);
        }
        else
        {
            bw.Write(jobs.Count);
            foreach (var j in jobs)
            {
                bw.Write(j.Name ?? string.Empty);
                bw.Write(j.From ?? string.Empty);
                bw.Write(j.To ?? string.Empty);
                bw.Write(j.DestinationIndex);
                bw.Write(j.PayloadIndex);
                bw.Write(j.Price);
                bw.Write(j.Mass);
                bw.Write(j.TimeStart);
                bw.Write(j.IsChallenge);
                bw.Write(j.IsIntercity);
                bw.Write(j.Duration);
                bw.Write(j.Distance);
                bw.Write(j.StartingCityName ?? string.Empty);
                bw.Write(j.DestCityName ?? string.Empty);
            }
        }

        // Players
        if (players == null)
        {
            bw.Write(0);
        }
        else
        {
            bw.Write(players.Count);
            foreach (var p in players)
            {
                bw.Write(p.Key ?? string.Empty);
                bw.Write(p.Nick ?? string.Empty);
                bw.Write(p.Px);
                bw.Write(p.Py);
                bw.Write(p.Pz);
                bw.Write(p.Qx);
                bw.Write(p.Qy);
                bw.Write(p.Qz);
                bw.Write(p.Qw);
                bw.Write(p.InCar);
                bw.Write(p.HeldPayload ?? string.Empty);
            }
        }

        // Car
        bw.Write(car.HasValue);
        if (car.HasValue)
        {
            var c = car.Value;
            bw.Write(c.Px); bw.Write(c.Py); bw.Write(c.Pz);
            bw.Write(c.Qx); bw.Write(c.Qy); bw.Write(c.Qz); bw.Write(c.Qw);
            bw.Write(c.Vx); bw.Write(c.Vy); bw.Write(c.Vz);
            bw.Write(c.Ax); bw.Write(c.Ay); bw.Write(c.Az);
        }

        // Cargo
        if (cargo == null)
        {
            bw.Write(0);
        }
        else
        {
            bw.Write(cargo.Count);
            foreach (var it in cargo)
            {
                bw.Write(it.Name ?? string.Empty);
                bw.Write(it.Lpx); bw.Write(it.Lpy); bw.Write(it.Lpz);
                bw.Write(it.Lqx); bw.Write(it.Lqy); bw.Write(it.Lqz); bw.Write(it.Lqw);
            }
        }

        return ms.ToArray();
    }

    internal static void ParseAndApplyIncoming(LanCoopManager mgr, UdpTransport transport, System.Net.IPEndPoint remote, byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var br = new BinaryReader(ms, Encoding.UTF8, leaveOpen: true);

        var type = (MessageType)br.ReadByte();

        switch (type)
        {
            case MessageType.Hello:
            {
                var key = br.ReadString();
                string? clientVersion = null;
                if (ms.Position < ms.Length)
                {
                    try { clientVersion = br.ReadString(); } catch { clientVersion = null; }
                }
                if (!mgr.IsHost)
                    return;
                if (!string.Equals(key, "EDC_LAN_COOP", StringComparison.Ordinal))
                    return;

                transport.RegisterClient(remote);
                Plugin.Log.LogInfo($"Client registered: {remote}");

                mgr.OnClientHello(remote, clientVersion);

                mgr.NotifyClientRegistered();

                // Tell client its stable key so it can ignore its own avatar updates.
                transport.SendTo(remote, BuildWelcome(remote.ToString()));

                // Immediately sync shared money on join (also repeated via initial-sync for UDP reliability).
                if (Plugin.SharedMoneyEnabled.Value && GameAccess.TryReadHudMoney(out var money))
                    transport.SendTo(remote, BuildMoneySet(money));

                // Push host authoritative save/positions for a short time (UDP).
                mgr.QueueInitialSync(remote);
                break;
            }
            case MessageType.Welcome:
            {
                if (!mgr.IsClient)
                    return;
                var selfKey = br.ReadString();
                string? hostVersion = null;
                if (ms.Position < ms.Length)
                {
                    try { hostVersion = br.ReadString(); } catch { hostVersion = null; }
                }
                mgr.SetSelfKey(selfKey, hostVersion);
                break;
            }
            case MessageType.SaveSet:
            {
                var k = br.ReadString();
                var v = br.ReadString();

                if (!Plugin.IsSaveKeyAllowedForWorldSync(k))
                    return;

                if (mgr.IsHost)
                {
                    mgr.WithRemoteApply(() => GameAccess.TryApplySaveKey(k, v));
                    // echo to all clients
                    transport.SendToAll(BuildSaveSet(k, v));
                }
                else
                {
                    mgr.WithRemoteApply(() => GameAccess.TryApplySaveKey(k, v));
                }

                break;
            }
            case MessageType.SaveDelete:
            {
                var k = br.ReadString();

                if (!Plugin.IsSaveKeyAllowedForWorldSync(k))
                    return;

                if (mgr.IsHost)
                {
                    mgr.WithRemoteApply(() => GameAccess.TryDeleteSaveKey(k));
                    transport.SendToAll(BuildSaveDelete(k));
                }
                else
                {
                    mgr.WithRemoteApply(() => GameAccess.TryDeleteSaveKey(k));
                }

                break;
            }
            case MessageType.WorldSnapshot:
            {
                if (!mgr.IsClient)
                    return;

                var count = br.ReadInt32();
                mgr.WithRemoteApply(() =>
                {
                    try
                    {
                        for (var i = 0; i < count; i++)
                        {
                            var k = br.ReadString();
                            var v = br.ReadString();
                            if (Plugin.IsSaveKeyAllowedForWorldSync(k))
                                GameAccess.TryApplySaveKey(k, v);
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogWarning($"Save snapshot apply failed: {ex.GetType().Name}");
                        // Continue with remaining fields to keep visuals/network alive.
                    }

                    var hasDay = br.ReadBoolean();
                    if (hasDay)
                    {
                        var t = br.ReadSingle();
                        GameAccess.TryApplyDayNightTime(t);
                    }

                    var hasMoney = br.ReadBoolean();
                    if (hasMoney)
                    {
                        var m = br.ReadInt32();
                        if (Plugin.SharedMoneyEnabled.Value)
                        {
                            mgr.OnMoneySetReceived(m);
                        }
                    }

                    try
                    {
                        var jobCount = br.ReadInt32();
                        if (jobCount > 0)
                        {
                            var jobs = new List<JobData>(jobCount);
                            for (var i = 0; i < jobCount; i++)
                            {
                                var jd = new JobData
                                {
                                    Name = br.ReadString(),
                                    From = br.ReadString(),
                                    To = br.ReadString(),
                                    DestinationIndex = br.ReadInt32(),
                                    PayloadIndex = br.ReadInt32(),
                                    Price = br.ReadSingle(),
                                    Mass = br.ReadSingle(),
                                    TimeStart = br.ReadSingle(),
                                    IsChallenge = br.ReadBoolean(),
                                    IsIntercity = br.ReadBoolean(),
                                    Duration = br.ReadSingle(),
                                    Distance = br.ReadSingle(),
                                    StartingCityName = br.ReadString(),
                                    DestCityName = br.ReadString(),
                                };
                                jobs.Add(jd);
                            }

                            GameAccess.TryApplyJobBoardJobs(jobs);
                        }
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log.LogWarning($"Jobs snapshot apply failed: {ex.GetType().Name}");
                    }

                    // Players
                    var alive = new HashSet<string>(StringComparer.Ordinal);
                    var hostNick = "";
                    var playerCount = br.ReadInt32();
                    for (var i = 0; i < playerCount; i++)
                    {
                        var key = br.ReadString();
                        var nick = br.ReadString();
                        var px = br.ReadSingle();
                        var py = br.ReadSingle();
                        var pz = br.ReadSingle();
                        var qx = br.ReadSingle();
                        var qy = br.ReadSingle();
                        var qz = br.ReadSingle();
                        var qw = br.ReadSingle();
                        var inCar = br.ReadBoolean();
                        var heldPayload = br.ReadString();

                        if (string.Equals(key, "host", StringComparison.Ordinal))
                            hostNick = Plugin.SanitizeNickname(nick);

                        if (!string.IsNullOrEmpty(key) && key != mgr.SelfKey)
                        {
                            alive.Add(key);
                            RemoteAvatars.ApplyPlayerPose(key, Plugin.SanitizeNickname(nick), px, py, pz, qx, qy, qz, qw, inCar, heldPayload);
                        }
                    }
                    RemoteAvatars.CleanupMissing(alive);

                    // Car
                    var hasCar = br.ReadBoolean();
                    if (hasCar)
                    {
                        var cpos = new UnityEngine.Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                        var crot = new UnityEngine.Quaternion(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                        var cvel = new UnityEngine.Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                        var cang = new UnityEngine.Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());

                        // IMPORTANT: do NOT move the client's real car/controller.
                        // That makes the client "ride along" with the host and feels like spectating.
                        RemoteCars.ApplyCarState("host", cpos, crot, hostNick);
                    }

                    // Cargo
                    var cargoCount = br.ReadInt32();
                    if (cargoCount > 0)
                    {
                        var cargo = new List<(string Name, UnityEngine.Vector3 LocalPos, UnityEngine.Quaternion LocalRot)>(cargoCount);
                        for (var i = 0; i < cargoCount; i++)
                        {
                            var name = br.ReadString();
                            var lp = new UnityEngine.Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                            var lr = new UnityEngine.Quaternion(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                            cargo.Add((name, lp, lr));
                        }

                        RemoteCars.ApplyCargo("host", cargo);
                    }

                    mgr.TraceSnapshotReceive(playerCount, hasCar, cargoCount);
                });

                break;
            }
            case MessageType.MoneyDelta:
            {
                if (!mgr.IsHost)
                    return;
                if (!Plugin.SharedMoneyEnabled.Value)
                    return;

                var delta = br.ReadInt32();
                if (delta == 0)
                    return;

                // Apply to host money and broadcast authoritative value.
                if (GameAccess.TryReadHudMoney(out var cur))
                {
                    var next = cur + delta;
                    if (next < 0)
                        next = 0;

                    mgr.WithRemoteApply(() => GameAccess.TryApplyHudMoney(next));
                    mgr.NotifyRemoteMoneyApplied(next);
                    transport.SendToAll(BuildMoneySet(next));

                    if (Plugin.DebugLogs.Value)
                        Plugin.Log.LogInfo($"MoneyDelta from {remote}: delta={delta} total={next}");
                }

                break;
            }
            case MessageType.MoneySet:
            {
                if (!mgr.IsClient)
                    return;
                if (!Plugin.SharedMoneyEnabled.Value)
                    return;

                var money = br.ReadInt32();
                mgr.OnMoneySetReceived(money);
                break;
            }
            case MessageType.ClientState:
            {
                if (!mgr.IsHost)
                    return;

                var nick = br.ReadString();

                var px = br.ReadSingle();
                var py = br.ReadSingle();
                var pz = br.ReadSingle();
                var qx = br.ReadSingle();
                var qy = br.ReadSingle();
                var qz = br.ReadSingle();
                var qw = br.ReadSingle();
                var inCar = br.ReadBoolean();

                // Optional trailing field (newer versions): held payload name.
                var heldPayload = string.Empty;
                if (ms.Position < ms.Length)
                {
                    try { heldPayload = br.ReadString(); } catch { heldPayload = string.Empty; }
                }

                UnityEngine.Vector3? controllerPos = null;
                UnityEngine.Quaternion? controllerRot = null;
                if (ms.Position < ms.Length)
                {
                    try
                    {
                        var hasController = br.ReadBoolean();
                        if (hasController)
                        {
                            controllerPos = new UnityEngine.Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                            controllerRot = new UnityEngine.Quaternion(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                        }
                    }
                    catch
                    {
                        controllerPos = null;
                        controllerRot = null;
                    }
                }

                int? reportedMoney = null;
                if (ms.Position < ms.Length)
                {
                    try
                    {
                        var hasMoney = br.ReadBoolean();
                        if (hasMoney && ms.Position < ms.Length)
                            reportedMoney = br.ReadInt32();
                    }
                    catch
                    {
                        reportedMoney = null;
                    }
                }

                mgr.OnClientState(remote, Plugin.SanitizeNickname(nick), px, py, pz, qx, qy, qz, qw, inCar, heldPayload, controllerPos, controllerRot);
                if (reportedMoney.HasValue)
                    mgr.OnClientReportedMoney(remote, reportedMoney.Value);
                break;
            }
            case MessageType.SaveFull:
            {
                if (!mgr.IsClient)
                    return;

                var saveId = br.ReadString();
                var wipe = br.ReadBoolean();
                var count = br.ReadInt32();

                var map = new Dictionary<string, string>(Math.Max(0, count), StringComparer.Ordinal);
                for (var i = 0; i < count; i++)
                {
                    var k = br.ReadString();
                    var v = br.ReadString();
                    map[k] = v;
                }

                mgr.OnSaveFullReceived(saveId, wipe, map);

                if (Plugin.DebugLogs.Value)
                    Plugin.Log.LogInfo($"Received host SaveFull: keys={count} saveId='{saveId}'");

                break;
            }
            case MessageType.PlayerLocations:
            {
                if (!mgr.IsClient)
                    return;

                var saveId = br.ReadString();
                var count = br.ReadInt32();
                var locs = new Dictionary<string, (UnityEngine.Vector3 Pos, UnityEngine.Quaternion Rot)>(Math.Max(0, count), StringComparer.Ordinal);
                for (var i = 0; i < count; i++)
                {
                    var nick = br.ReadString();
                    var pos = new UnityEngine.Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                    var rot = new UnityEngine.Quaternion(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                    locs[Plugin.SanitizeNickname(nick)] = (pos, rot);
                }

                mgr.OnPlayerLocations(saveId, locs);
                break;
            }
            case MessageType.ClientCarState:
            {
                if (!mgr.IsHost)
                    return;

                var cpx = br.ReadSingle();
                var cpy = br.ReadSingle();
                var cpz = br.ReadSingle();
                var cqx = br.ReadSingle();
                var cqy = br.ReadSingle();
                var cqz = br.ReadSingle();
                var cqw = br.ReadSingle();

                mgr.OnClientCarState(remote, cpx, cpy, cpz, cqx, cqy, cqz, cqw);
                break;
            }
            case MessageType.ClientCarCargo:
            {
                if (!mgr.IsHost)
                    return;

                var cargoCount = br.ReadInt32();
                var cargo = new List<(string Name, UnityEngine.Vector3 LocalPos, UnityEngine.Quaternion LocalRot)>(Math.Max(0, cargoCount));
                for (var i = 0; i < cargoCount; i++)
                {
                    var name = br.ReadString();
                    var lp = new UnityEngine.Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                    var lr = new UnityEngine.Quaternion(br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                    cargo.Add((name, lp, lr));
                }

                mgr.OnClientCarCargo(remote, cargo);
                break;
            }
            case MessageType.ClientCarSfx:
            {
                if (!mgr.IsHost)
                    return;

                var sfxId = br.ReadByte();
                string? clipName = null;
                string? sourceName = null;

                if (ms.Position < ms.Length)
                {
                    var hasClip = br.ReadBoolean();
                    if (hasClip && ms.Position < ms.Length)
                        clipName = br.ReadString();
                }
                if (ms.Position < ms.Length)
                {
                    var hasSource = br.ReadBoolean();
                    if (hasSource && ms.Position < ms.Length)
                        sourceName = br.ReadString();
                }

                mgr.OnClientCarSfx(remote, sfxId, clipName, sourceName);
                break;
            }
            case MessageType.CarSfx:
            {
                if (!mgr.IsClient)
                    return;

                var carKey = br.ReadString();
                var sfxId = br.ReadByte();
                string? clipName = null;
                string? sourceName = null;

                if (ms.Position < ms.Length)
                {
                    var hasClip = br.ReadBoolean();
                    if (hasClip && ms.Position < ms.Length)
                        clipName = br.ReadString();
                }
                if (ms.Position < ms.Length)
                {
                    var hasSource = br.ReadBoolean();
                    if (hasSource && ms.Position < ms.Length)
                        sourceName = br.ReadString();
                }

                mgr.OnCarSfx(carKey, sfxId, clipName, sourceName);
                break;
            }
            case MessageType.PlayerCustomization:
            {
                // A peer is announcing their nickname + avatar color.
                // Key is the sender's stable peer key (sent in payload).
                var peerKey = string.Empty;
                var nick    = string.Empty;
                float cr = 1f, cg = 1f, cb = 1f;
                try
                {
                    peerKey = br.ReadString();
                    nick    = br.ReadString();
                    cr      = br.ReadSingle();
                    cg      = br.ReadSingle();
                    cb      = br.ReadSingle();
                }
                catch { break; }

                if (string.IsNullOrWhiteSpace(peerKey))
                    break;

                var tint = PlayerCustomization.ClampColor(cr, cg, cb);
                PlayerCustomization.SetRemoteTint(peerKey, tint);
                RemoteAvatars.ApplyTint(peerKey, tint);

                if (Plugin.DebugLogs.Value)
                    Plugin.Log.LogInfo($"[Customization] Received from '{peerKey}': nick='{nick}' color=({cr:F2},{cg:F2},{cb:F2})");

                // Host relays to all other clients.
                if (mgr.IsHost)
                    transport.SendToAllExcept(remote, BuildPlayerCustomization(peerKey, nick, cr, cg, cb));

                break;
            }
        }
    }

    // ── PlayerCustomization builders ──────────────────────────────────────

    /// <summary>Build a PlayerCustomization packet for the local player.</summary>
    internal static byte[] BuildPlayerCustomization(string peerKey, string nickname, float r, float g, float b)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true);
        bw.Write((byte)MessageType.PlayerCustomization);
        bw.Write(peerKey    ?? string.Empty);
        bw.Write(nickname   ?? string.Empty);
        bw.Write(r);
        bw.Write(g);
        bw.Write(b);
        return ms.ToArray();
    }
}

internal enum MessageType : byte
{
    Hello = 1,
    WorldSnapshot = 2,
    SaveSet = 3,
    SaveDelete = 4,
    DiscoveryAnnounce = 5,
    ClientState = 6,
    Welcome = 7,
    ClientCarState = 8,
    ClientCarCargo = 9,
    SaveFull = 10,
    PlayerLocations = 11,
    MoneyDelta = 12,
    MoneySet = 13,
    ClientCarSfx = 14,
    CarSfx = 15,
    PlayerCustomization = 16,
}

internal sealed class JobData
{
    public string? Name;
    public string? From;
    public string? To;
    public int DestinationIndex;
    public int PayloadIndex;
    public float Price;
    public float Mass;
    public float TimeStart;
    public bool IsChallenge;
    public bool IsIntercity;
    public float Duration;
    public float Distance;
    public string? StartingCityName;
    public string? DestCityName;
}

internal readonly struct PlayerPose
{
    public readonly string? Key;
    public readonly string? Nick;
    public readonly float Px, Py, Pz;
    public readonly float Qx, Qy, Qz, Qw;
    public readonly bool InCar;
    public readonly string? HeldPayload;

    public PlayerPose(string key, string nick, float px, float py, float pz, float qx, float qy, float qz, float qw, bool inCar, string heldPayload)
    {
        Key = key;
        Nick = nick;
        Px = px; Py = py; Pz = pz;
        Qx = qx; Qy = qy; Qz = qz; Qw = qw;
        InCar = inCar;
        HeldPayload = heldPayload;
    }
}

internal readonly struct CarState
{
    public readonly float Px, Py, Pz;
    public readonly float Qx, Qy, Qz, Qw;
    public readonly float Vx, Vy, Vz;
    public readonly float Ax, Ay, Az;

    public CarState(float px, float py, float pz, float qx, float qy, float qz, float qw, float vx, float vy, float vz, float ax, float ay, float az)
    {
        Px = px; Py = py; Pz = pz;
        Qx = qx; Qy = qy; Qz = qz; Qw = qw;
        Vx = vx; Vy = vy; Vz = vz;
        Ax = ax; Ay = ay; Az = az;
    }
}

internal readonly struct CargoItem
{
    public readonly string? Name;
    public readonly float Lpx, Lpy, Lpz;
    public readonly float Lqx, Lqy, Lqz, Lqw;

    public CargoItem(string name, float lpx, float lpy, float lpz, float lqx, float lqy, float lqz, float lqw)
    {
        Name = name;
        Lpx = lpx; Lpy = lpy; Lpz = lpz;
        Lqx = lqx; Lqy = lqy; Lqz = lqz; Lqw = lqw;
    }
}
