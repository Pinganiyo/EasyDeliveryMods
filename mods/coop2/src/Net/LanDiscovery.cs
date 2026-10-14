using System.Net;
using System.Net.Sockets;

namespace EasyDeliveryCoLanCoop;

internal sealed class LanDiscovery : IDisposable
{
    private readonly bool _isHost;
    private readonly int _discoveryPort;
    private readonly int _gamePort;
    private readonly int _intervalMs;

    private UdpClient? _udp;
    private DateTime _nextAnnounceUtc;

    public LanDiscovery(bool isHost, int discoveryPort, int gamePort, int intervalMs)
    {
        _isHost = isHost;
        _discoveryPort = discoveryPort;
        _gamePort = gamePort;
        _intervalMs = Math.Max(100, intervalMs);
    }

    public void Start()
    {
        if (_udp != null)
            return;

        if (_isHost)
        {
            _udp = new UdpClient(0);
            _udp.EnableBroadcast = true;
            _udp.Client.Blocking = false;
            _nextAnnounceUtc = DateTime.UtcNow;
            Plugin.Log.LogInfo($"LAN discovery host broadcaster started (port {_discoveryPort})");
        }
        else
        {
            _udp = new UdpClient(_discoveryPort);
            _udp.Client.Blocking = false;
            Plugin.Log.LogInfo($"LAN discovery client listener started (port {_discoveryPort})");
        }
    }

    public void Dispose()
    {
        _udp?.Dispose();
        _udp = null;
    }

    public void Poll(LanCoopManager mgr, UdpTransport transport)
    {
        if (_udp == null)
            return;

        if (_isHost)
        {
            if (DateTime.UtcNow < _nextAnnounceUtc)
                return;

            _nextAnnounceUtc = DateTime.UtcNow.AddMilliseconds(_intervalMs);

            var msg = NetMessages.BuildDiscoveryAnnounce(_gamePort);
            try
            {
                var ep = new IPEndPoint(IPAddress.Broadcast, _discoveryPort);
                _udp.Send(msg, msg.Length, ep);
            }
            catch
            {
                // ignore
            }

            return;
        }

        while (_udp.Available > 0)
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

            try
            {
                NetMessages.TryHandleDiscovery(mgr, transport, remote, data);
            }
            catch
            {
                // ignore
            }
        }
    }
}
