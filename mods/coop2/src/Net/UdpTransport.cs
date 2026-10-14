using System.Net;
using System.Net.Sockets;

namespace EasyDeliveryCoLanCoop;

internal sealed class UdpTransport : IDisposable
{
    private readonly Action<IPEndPoint, byte[]> _onDatagram;

    private UdpClient? _udp;
    private IPEndPoint? _server;

    private readonly Dictionary<string, IPEndPoint> _clients = new(StringComparer.Ordinal);

    public bool IsHost { get; private set; }

    public bool HasServer => _server != null;

    public IPEndPoint? ServerEndPoint => _server;

    public UdpTransport(Action<IPEndPoint, byte[]> onDatagram)
    {
        _onDatagram = onDatagram;
    }

    public void StartHost(int port)
    {
        IsHost = true;
        _udp = new UdpClient(port);
        _udp.Client.Blocking = false;
        Plugin.Log.LogInfo($"UDP host listening on {port}");
    }

    public void StartClient(string host, int port)
    {
        IsHost = false;
        _udp = new UdpClient(0);
        _udp.Client.Blocking = false;
        if (!string.IsNullOrWhiteSpace(host))
        {
            _server = new IPEndPoint(IPAddress.Parse(host), port);
            Plugin.Log.LogInfo($"UDP client started. Server={_server}");
            SendToServer(NetMessages.BuildHello());
        }
        else
        {
            Plugin.Log.LogInfo("UDP client started. Server is not set yet (waiting for discovery)");
        }
    }

    public void SetServer(IPEndPoint server)
    {
        _server = server;
        Plugin.Log.LogInfo($"Server set: {_server}");
    }

    public void Dispose()
    {
        _udp?.Dispose();
        _udp = null;
        _clients.Clear();
    }

    public void Poll()
    {
        if (_udp == null)
            return;

        // Avoid freezing the Unity main thread when the host is sending a lot of packets.
        const int MaxDatagramsPerPoll = 128;
        var processed = 0;

        byte[]? latestSnapshot = null;
        IPEndPoint? latestSnapshotRemote = null;

        while (_udp.Available > 0 && processed < MaxDatagramsPerPoll)
        {
            IPEndPoint remote = new(IPAddress.Any, 0);
            byte[] data;
            try
            {
                data = _udp.Receive(ref remote);
            }
            catch
            {
                break;
            }

            processed++;

            // Coalesce snapshots: only keep the most recent one for this frame.
            if (data.Length > 0 && data[0] == (byte)MessageType.WorldSnapshot)
            {
                latestSnapshot = data;
                latestSnapshotRemote = remote;
                continue;
            }

            _onDatagram(remote, data);
        }

        if (latestSnapshot != null && latestSnapshotRemote != null)
            _onDatagram(latestSnapshotRemote, latestSnapshot);
    }

    public void RegisterClient(IPEndPoint endpoint)
    {
        var key = endpoint.ToString();
        _clients[key] = endpoint;
    }

    public void UnregisterClient(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;
        _clients.Remove(key);
    }

    public void SendToAll(byte[] payload)
    {
        if (_udp == null)
            return;

        foreach (var kv in _clients)
        {
            try { _udp.Send(payload, payload.Length, kv.Value); } catch { /* ignore */ }
        }
    }

    public void SendToAllExcept(System.Net.IPEndPoint except, byte[] payload)
    {
        if (_udp == null)
            return;

        var exceptKey = except?.ToString() ?? string.Empty;
        foreach (var kv in _clients)
        {
            if (string.Equals(kv.Key, exceptKey, StringComparison.Ordinal))
                continue;
            try { _udp.Send(payload, payload.Length, kv.Value); } catch { /* ignore */ }
        }
    }

    public void SendToServer(byte[] payload)
    {
        if (_udp == null || _server == null)
            return;

        try { _udp.Send(payload, payload.Length, _server); } catch { /* ignore */ }
    }

    public void SendTo(IPEndPoint endpoint, byte[] payload)
    {
        if (_udp == null)
            return;

        try { _udp.Send(payload, payload.Length, endpoint); } catch { /* ignore */ }
    }
}
