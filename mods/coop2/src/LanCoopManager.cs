using UnityEngine;

namespace EasyDeliveryCoLanCoop;

public sealed class LanCoopManager : MonoBehaviour
{
    private float _nextTick;
    private float _tickInterval;

    private float _nextWorldSnapshotAt;
    private float _forceFullSnapshotUntil;
    private const float MaxWorldSnapshotsPerSecond = 10f;

    private float _nextDebugAt;

    private bool _applyingRemote;

    private UdpTransport? _transport;
    private LanDiscovery? _discovery;

    private readonly Dictionary<string, PlayerPose> _hostKnownPlayers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (UnityEngine.Vector3 Pos, UnityEngine.Quaternion Rot)> _hostKnownCars = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _hostKnownNick = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _hostKnownClientVersion = new(StringComparer.Ordinal);
    private readonly Dictionary<string, float> _hostClientLastSeenAt = new(StringComparer.Ordinal);
    private float _nextHostPruneAt;
    private const float HostPruneIntervalSeconds = 1.0f;

    private sealed class InitialSyncState
    {
        public System.Net.IPEndPoint EndPoint = null!;
        public float NextSendAt;
        public float UntilAt;
    }

    private readonly Dictionary<string, InitialSyncState> _hostInitialSync = new(StringComparer.Ordinal);
    private const float InitialSyncDurationSeconds = 3.0f;
    private const float InitialSyncIntervalSeconds = 0.5f;

    private string? _selfKey;

    private float _clientLastHostPacketAt;
    private System.Net.IPEndPoint? _clientLastKnownServer;
    private int _clientReconnectAttempt;
    private float _clientNextReconnectTryAt;
    private bool _clientManualDisconnect;

    private float _nextHelloAt;
    private const float HelloIntervalSeconds = 1.0f;
    private bool _clientTeleportedFromHost;

    private float _nextClientCargoAt;
    private const float ClientCargoIntervalSeconds = 0.5f;

    private readonly Dictionary<string, float> _nextCarSfxAt = new(StringComparer.Ordinal);

    private int? _lastObservedMoney;
    private float _nextMoneyPollAt;
    private float _suppressMoneyEchoUntil;
    private const float MoneyPollIntervalSeconds = 0.20f;

    private bool _forceClientMode;
    private bool _forceHostMode;

    private bool _clientDiscoveryLocked;

    private bool _showConsole = true;
    private string _consoleInput = string.Empty;
    private Vector2 _consoleScroll;
    private readonly List<string> _consoleLines = new();
    private const int MaxConsoleLines = 20;

    private bool _clientAutoEnterRequested;
    private bool _clientAutoEnterDone;
    private float _clientAutoEnterNextTryAt;
    private float _clientAutoEnterUntil;
    private string _clientLastSaveIdFromHost = string.Empty;

    private float _nextAutoEnterLogAt;
    private string _lastAutoEnterReason = string.Empty;
    private bool _autoEnterDisabledLogged;

    internal bool ClientDiscoveryLocked => _clientDiscoveryLocked;

    private sealed class PendingSaveFull
    {
        public string SaveId = string.Empty;
        public bool Wipe;
        public Dictionary<string, string> Data = new(StringComparer.Ordinal);
        public float NextTryAt;
        public float UntilAt;
        public bool AppliedToPlayerPrefs;
    }

    private PendingSaveFull? _pendingSaveFull;

    private int? _pendingMoneySet;
    private float _pendingMoneyNextTryAt;
    private float _pendingMoneyUntil;

    private (UnityEngine.Vector3 Pos, UnityEngine.Quaternion Rot)? _clientPendingTeleport;
    private float _clientPendingTeleportUntil;

    internal string? SelfKey => _selfKey;
    internal bool HasWelcome => !string.IsNullOrEmpty(_selfKey);
    public static LanCoopManager Instance { get; private set; } = null!;

    private void Awake()
    {
        if (Instance != null)
        {
            Destroy(this);
            return;
        }

        Instance = this;
    }

    private void Start()
    {
        _tickInterval = 1f / Mathf.Max(1, Plugin.TickRate.Value);
        StartNetworking();
    }

    private void Update()
    {
        _transport?.Poll();

        if (_transport != null)
            _discovery?.Poll(this, _transport);

        if (IsClient)
            ClientAutoReconnectTick();

        if (_transport == null)
            return;

        if (Time.unscaledTime < _nextTick)
            return;

        _nextTick = Time.unscaledTime + _tickInterval;

        if (IsClient)
            EnsureClientConnected();

        if (IsClient)
            TryApplyPendingClientTeleport();

        if (IsClient)
        {
            TryApplyPendingSaveFull();
            TryApplyPendingMoneySet();
            TryAutoEnterWorld();
        }

        if (IsClient)
            SendClientMoneyDeltaIfChanged();

        if (IsClient)
            SendLocalClientState();

        if (IsClient || IsHost)
            SendLocalCarSfxIfNeeded();

        if (IsHost)
        {
            PlayerPositionsStore.HostTick(Time.unscaledTime);
            PruneDisconnectedClients();
            BroadcastHostMoneyIfChanged();
            SendPendingInitialSync();
            BroadcastWorldSnapshot();
        }
    }

    private void OnGUI()
    {
        try
        {
            const int baseW = 220;
            const int baseH = 78;
            const int consoleW = 420;
            const int consoleH = 260;
            const int pad = 10;

            var w = _showConsole ? consoleW : baseW;
            var h = _showConsole ? consoleH : baseH;

            var x = pad;
            var y = pad;

            GUI.Box(new Rect(x, y, w, h), "LAN Coop");

            _showConsole = GUI.Toggle(new Rect(x + w - 100, y + 2, 96, 20), _showConsole, "Console");

            var lineY = y + 22;
            var btnRect = new Rect(x + 10, lineY, baseW - 20, 22);

            if (_transport == null)
            {
                if (GUI.Button(btnRect, "Connect to Host"))
                {
                    _forceClientMode = true;
                    _forceHostMode = false;
                    _clientDiscoveryLocked = false;
                    _clientAutoEnterRequested = true;
                    _clientAutoEnterDone = false;
                    _clientAutoEnterNextTryAt = 0f;
                    _clientAutoEnterUntil = Time.unscaledTime + 30.0f;
                    StartNetworking();
                }

                var mode = Plugin.GetEffectiveNetworkMode();
                GUI.Label(new Rect(x + 10, lineY + 26, w - 20, 20), $"Mode={mode}");

                if (_showConsole)
                    DrawConsole(x, y, w, h);
                return;
            }

            if (IsClient)
            {
                var status = HasWelcome ? "Connected" : "Connecting";
                if (GUI.Button(btnRect, $"Disconnect ({status})"))
                {
                    Disconnect();
                    return;
                }

                var server = _transport.ServerEndPoint != null ? _transport.ServerEndPoint.ToString() : "(discovery)";
                GUI.Label(new Rect(x + 10, lineY + 26, w - 20, 20), $"Server: {server}");
                if (_showConsole)
                    DrawConsole(x, y, w, h);
                return;
            }

            // Host or other: show a disconnect button to stop networking.
            if (GUI.Button(btnRect, "Stop Networking"))
            {
                Disconnect();
                return;
            }

            GUI.Label(new Rect(x + 10, lineY + 26, baseW - 20, 20), IsHost ? "Host" : "(custom)" );

            if (_showConsole)
                DrawConsole(x, y, w, h);
        }
        catch (Exception ex)
        {
            try
            {
                if (Plugin.Log != null)
                    Plugin.Log.LogError(ex);
                else
                    Debug.LogException(ex);
            }
            catch
            {
                // ignore
            }
        }
    }

    private void DrawConsole(float x, float y, float w, float h)
    {
        var startY = y + 48f;
        var areaH = h - 58f;
        if (areaH < 40f)
            return;

        var logRect = new Rect(x + 10f, startY, w - 20f, areaH - 26f);
        var inputRowRect = new Rect(x + 10f, startY + areaH - 24f, w - 20f, 22f);
        var runBtnW = 64f;
        var inputRect = new Rect(inputRowRect.x, inputRowRect.y, Mathf.Max(10f, inputRowRect.width - runBtnW - 6f), inputRowRect.height);
        var runRect = new Rect(inputRect.xMax + 6f, inputRowRect.y, runBtnW, inputRowRect.height);

        GUI.Box(new Rect(x + 8f, startY - 2f, w - 16f, areaH + 2f), string.Empty);

        var contentH = Mathf.Max(1, _consoleLines.Count) * 18f;
        _consoleScroll = GUI.BeginScrollView(logRect, _consoleScroll, new Rect(0f, 0f, logRect.width - 20f, contentH));
        for (var i = 0; i < _consoleLines.Count; i++)
            GUI.Label(new Rect(0f, i * 18f, logRect.width - 24f, 18f), _consoleLines[i]);
        GUI.EndScrollView();

        GUI.SetNextControlName("EDC_LAN_CONSOLE");
        _consoleInput = GUI.TextField(inputRect, _consoleInput ?? string.Empty);

        if (GUI.Button(runRect, "Run"))
        {
            var cmd = _consoleInput;
            _consoleInput = string.Empty;
            ExecuteConsoleCommand(cmd);
            GUI.FocusControl("EDC_LAN_CONSOLE");
        }

        var e = Event.current;
        if (e != null && e.type == EventType.KeyDown && (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter))
        {
            if (GUI.GetNameOfFocusedControl() == "EDC_LAN_CONSOLE")
            {
                var cmd = _consoleInput;
                _consoleInput = string.Empty;
                ExecuteConsoleCommand(cmd);
                e.Use();
            }
        }
    }

    private void ConsoleLog(string msg)
    {
        msg ??= string.Empty;
        _consoleLines.Add(msg);
        while (_consoleLines.Count > MaxConsoleLines)
            _consoleLines.RemoveAt(0);
        _consoleScroll.y = 999999;
    }

    private void ExecuteConsoleCommand(string? input)
    {
        input ??= string.Empty;
        input = input.Trim();
        if (input.Length == 0)
            return;

        ConsoleLog($"> {input}");

        var parts = input.Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
        var cmd = parts[0].Trim().ToLowerInvariant();
        var arg = parts.Length > 1 ? parts[1].Trim() : string.Empty;

        if (cmd == "help" || cmd == "?")
        {
            ConsoleLog("Commands: connect <ip[:port]>, disconnect, reconnect, autoreconnect <on|off|status>, status, help");
            return;
        }

        if (cmd == "status")
        {
            var mode = IsHost ? "Host" : (IsClient ? "Client" : "Off");
            var server = _transport?.ServerEndPoint != null ? _transport.ServerEndPoint.ToString() : "(none)";
            var ar = Plugin.AutoReconnectEnabled != null && Plugin.AutoReconnectEnabled.Value;
            ConsoleLog($"Mode={mode} Welcome={HasWelcome} Server={server} AutoReconnect={ar}");
            return;
        }

        if (cmd == "disconnect" || cmd == "stop")
        {
            Disconnect();
            ConsoleLog("Disconnected.");
            return;
        }

        if (cmd == "reconnect")
        {
            if (!IsClient && !_forceClientMode && !string.Equals(Plugin.GetEffectiveNetworkMode(), "Client", StringComparison.OrdinalIgnoreCase))
            {
                ConsoleLog("Reconnect is for Client mode.");
                return;
            }

            ForceClientReconnect();
            ConsoleLog("Reconnect requested.");
            return;
        }

        if (cmd == "autoreconnect" || cmd == "ar")
        {
            var v = arg.Trim().ToLowerInvariant();
            if (v.Length == 0 || v == "status")
            {
                ConsoleLog($"AutoReconnectEnabled={Plugin.AutoReconnectEnabled.Value}");
                return;
            }
            if (v == "on" || v == "1" || v == "true")
            {
                Plugin.AutoReconnectEnabled.Value = true;
                ConsoleLog("AutoReconnectEnabled=true");
                ForceClientReconnect();
                ConsoleLog("Reconnect requested.");
                return;
            }
            if (v == "off" || v == "0" || v == "false")
            {
                Plugin.AutoReconnectEnabled.Value = false;
                ConsoleLog("AutoReconnectEnabled=false");
                return;
            }

            ConsoleLog("Usage: autoreconnect <on|off|status>");
            return;
        }

        if (cmd == "connect")
        {
            if (!TryParseEndpoint(arg, Plugin.Port.Value, out var ip, out var port, out var err))
            {
                ConsoleLog(err);
                return;
            }
            StartManualConnect(ip, port);
            return;
        }

        // If user typed just an endpoint, treat it as connect.
        if (TryParseEndpoint(input, Plugin.Port.Value, out var ip2, out var port2, out _))
        {
            StartManualConnect(ip2, port2);
            return;
        }

        ConsoleLog("Unknown command. Type 'help'.");
    }

    private void ForceClientReconnect()
    {
        // Clear the manual-disconnect latch so auto-reconnect can operate.
        _clientManualDisconnect = false;

        // Ensure networking exists.
        if (_transport == null)
        {
            _forceClientMode = true;
            _forceHostMode = false;
            StartNetworking();
        }

        // Restart handshake.
        _selfKey = null;
        _clientTeleportedFromHost = false;
        _clientPendingTeleport = null;
        _pendingSaveFull = null;
        _pendingMoneySet = null;

        _clientReconnectAttempt = 0;
        _clientNextReconnectTryAt = 0f;
        _nextHelloAt = 0f;
        _clientLastHostPacketAt = Time.unscaledTime;

        // Restore server endpoint if possible.
        if (_transport != null)
        {
            if (!_transport.HasServer)
            {
                if (_clientLastKnownServer != null)
                {
                    try { _transport.SetServer(_clientLastKnownServer); } catch { /* ignore */ }
                }
                else
                {
                    try
                    {
                        if (System.Net.IPAddress.TryParse(Plugin.HostAddress.Value, out var ip))
                            _transport.SetServer(new System.Net.IPEndPoint(ip, Plugin.Port.Value));
                    }
                    catch
                    {
                        // ignore
                    }
                }
            }

            // Send Hello ASAP.
            if (_transport.HasServer)
                _transport.SendToServer(NetMessages.BuildHello());
        }
    }

    private void StartManualConnect(string ip, int port)
    {
        _clientDiscoveryLocked = true;
        Plugin.HostAddress.Value = ip;
        Plugin.Port.Value = port;

        _forceClientMode = true;
        _forceHostMode = false;
        _clientAutoEnterRequested = true;
        _clientAutoEnterDone = false;
        _clientAutoEnterNextTryAt = 0f;
        _clientAutoEnterUntil = Time.unscaledTime + 30.0f;

        StartNetworking();
        ConsoleLog($"Connecting to {ip}:{port} ...");
    }

    private static bool TryParseEndpoint(string s, int defaultPort, out string ip, out int port, out string error)
    {
        ip = string.Empty;
        port = defaultPort;
        error = string.Empty;

        s ??= string.Empty;
        s = s.Trim();
        if (s.Length == 0)
        {
            error = "Usage: connect <ip[:port]>";
            return false;
        }

        var host = s;
        var p = defaultPort;
        var idx = s.LastIndexOf(':');
        if (idx > 0 && idx < s.Length - 1)
        {
            host = s.Substring(0, idx);
            var portStr = s.Substring(idx + 1);
            if (!int.TryParse(portStr, out p) || p <= 0 || p > 65535)
            {
                error = "Invalid port";
                return false;
            }
        }

        // Transport currently requires a numeric IP.
        if (!System.Net.IPAddress.TryParse(host, out _))
        {
            error = "Host must be an IP address (e.g. 192.168.0.10:27777)";
            return false;
        }

        ip = host;
        port = p;
        return true;
    }

    private void SendClientMoneyDeltaIfChanged()
    {
        if (!Plugin.SharedMoneyEnabled.Value)
            return;
        if (_transport == null || !_transport.HasServer)
            return;
        if (!HasWelcome)
            return;

        var now = Time.unscaledTime;
        if (now < _nextMoneyPollAt)
            return;
        _nextMoneyPollAt = now + MoneyPollIntervalSeconds;

        // If money was just applied from host, do not echo it back.
        if (now < _suppressMoneyEchoUntil)
            return;
        if (IsApplyingRemote)
            return;

        if (!GameAccess.TryReadHudMoney(out var m))
            return;

        if (_lastObservedMoney == null)
        {
            _lastObservedMoney = m;
            return;
        }

        var prev = _lastObservedMoney.Value;
        if (m == prev)
            return;

        var delta = m - prev;
        _lastObservedMoney = m;
        if (delta != 0)
            _transport.SendToServer(NetMessages.BuildMoneyDelta(delta));
    }

    private void BroadcastHostMoneyIfChanged()
    {
        if (!Plugin.SharedMoneyEnabled.Value)
            return;
        if (_transport == null)
            return;

        var now = Time.unscaledTime;
        if (now < _nextMoneyPollAt)
            return;
        _nextMoneyPollAt = now + MoneyPollIntervalSeconds;

        if (!GameAccess.TryReadHudMoney(out var m))
            return;

        if (_lastObservedMoney == null || _lastObservedMoney.Value != m)
        {
            _lastObservedMoney = m;
            _transport.SendToAll(NetMessages.BuildMoneySet(m));
        }
    }

    private void TryApplyPendingClientTeleport()
    {
        if (_clientPendingTeleport == null)
            return;

        var now = Time.unscaledTime;
        if (now > _clientPendingTeleportUntil)
        {
            _clientPendingTeleport = null;
            return;
        }

        // Avoid fighting the game while inside the car.
        if (GameAccess.TryReadInCar(out var inCar) && inCar)
            return;

        var tp = _clientPendingTeleport.Value;
        if (GameAccess.TryApplyLocalPlayerControllerPose(tp.Pos, tp.Rot))
        {
            _clientPendingTeleport = null;
            _clientTeleportedFromHost = true;
            Plugin.Log.LogInfo("Applied saved position from host.");
        }
    }

    private void SendPendingInitialSync()
    {
        if (!IsHost)
            return;
        if (_transport == null)
            return;
        if (!Plugin.ClientReceivesHostSaveOnJoin.Value && !Plugin.PlayerPositionsEnabled.Value)
            return;

        var now = Time.unscaledTime;
        if (_hostInitialSync.Count == 0)
            return;

        // Copy keys to avoid dictionary modification while iterating.
        var keys = new List<string>(_hostInitialSync.Keys);
        for (var i = 0; i < keys.Count; i++)
        {
            var k = keys[i];
            if (!_hostInitialSync.TryGetValue(k, out var st))
                continue;

            if (now > st.UntilAt)
            {
                _hostInitialSync.Remove(k);
                continue;
            }

            if (now < st.NextSendAt)
                continue;
            st.NextSendAt = now + InitialSyncIntervalSeconds;

            PlayerPositionsStore.HostRefreshSaveIdNow();
            var saveId = PlayerPositionsStore.CurrentSaveId;

            if (Plugin.SharedMoneyEnabled.Value)
            {
                if (GameAccess.TryReadHudMoney(out var m))
                    _transport.SendTo(st.EndPoint, NetMessages.BuildMoneySet(m));
            }

            if (Plugin.ClientReceivesHostSaveOnJoin.Value)
            {
                var snap = GameAccess.TryReadSaveSystemSnapshot();
                if (snap != null)
                {
                    // Only send keys that are allowed by current config.
                    var filtered = new Dictionary<string, string?>(snap.Count, StringComparer.Ordinal);
                    foreach (var kv in snap)
                    {
                        if (Plugin.IsSaveKeyAllowedForWorldSync(kv.Key))
                            filtered[kv.Key] = kv.Value;
                    }

                    var msg = NetMessages.BuildSaveFull(saveId, Plugin.ClientWipeLocalSaveOnJoin.Value, filtered);
                    _transport.SendTo(st.EndPoint, msg);
                }
            }

            if (Plugin.PlayerPositionsEnabled.Value)
            {
                if (PlayerPositionsStore.TryGetSnapshot(out var positions))
                {
                    var msg = NetMessages.BuildPlayerLocations(saveId, positions);
                    _transport.SendTo(st.EndPoint, msg);
                }
            }
        }
    }

    internal void NotifyClientRegistered()
    {
        // Send full world data (save + jobs) for a short time to ensure
        // a newly joined client receives an initial state even on UDP.
        _forceFullSnapshotUntil = Time.unscaledTime + 2.0f;
    }

    internal void TraceSnapshotReceive(int players, bool hasCar, int cargo)
    {
        if (!Plugin.DebugLogs.Value)
            return;

        if (Time.unscaledTime < _nextDebugAt)
            return;

        _nextDebugAt = Time.unscaledTime + Mathf.Max(0.5f, Plugin.DebugLogIntervalSeconds.Value);
        Plugin.Log.LogInfo($"Snapshot recv: players={players} hasCar={hasCar} cargo={cargo}");
    }

    private void TraceSnapshotSend(int players, bool hasCar, int cargo)
    {
        if (!Plugin.DebugLogs.Value)
            return;

        if (Time.unscaledTime < _nextDebugAt)
            return;

        _nextDebugAt = Time.unscaledTime + Mathf.Max(0.5f, Plugin.DebugLogIntervalSeconds.Value);
        Plugin.Log.LogInfo($"Snapshot send: players={players} hasCar={hasCar} cargo={cargo} clients={_hostKnownPlayers.Count}");
    }

    private void OnDestroy()
    {
        try { _transport?.Dispose(); } catch { /* ignore */ }
        try { _discovery?.Dispose(); } catch { /* ignore */ }
        if (ReferenceEquals(Instance, this))
            Instance = null!;
    }

    public bool IsHost => _forceHostMode || string.Equals(Plugin.GetEffectiveNetworkMode(), "Host", StringComparison.OrdinalIgnoreCase);
    public bool IsClient => _forceClientMode || string.Equals(Plugin.GetEffectiveNetworkMode(), "Client", StringComparison.OrdinalIgnoreCase);

    private void StartNetworking()
    {
        var mode = Plugin.GetEffectiveNetworkMode().Trim();
        if (!_forceClientMode && !_forceHostMode && string.Equals(mode, "Off", StringComparison.OrdinalIgnoreCase))
            return;

        StopNetworkingInternal();

        _clientManualDisconnect = false;
        _clientLastHostPacketAt = Time.unscaledTime;
        _clientReconnectAttempt = 0;
        _clientNextReconnectTryAt = 0f;

        _transport = new UdpTransport(OnDatagram);

        if (IsHost)
            _transport.StartHost(Plugin.Port.Value);
        else if (IsClient)
            _transport.StartClient(Plugin.HostAddress.Value, Plugin.Port.Value);

        if (Plugin.AutoDiscovery.Value)
        {
            _discovery = new LanDiscovery(
                isHost: IsHost,
                discoveryPort: Plugin.DiscoveryPort.Value,
                gamePort: Plugin.Port.Value,
                intervalMs: Plugin.DiscoveryIntervalMs.Value);
            _discovery.Start();
        }
    }

    private void StopNetworkingInternal()
    {
        try { _transport?.Dispose(); } catch { /* ignore */ }
        try { _discovery?.Dispose(); } catch { /* ignore */ }
        _transport = null;
        _discovery = null;

        _hostKnownPlayers.Clear();
        _hostKnownCars.Clear();
        _hostKnownNick.Clear();
        _hostKnownClientVersion.Clear();
        _hostClientLastSeenAt.Clear();
        _hostInitialSync.Clear();

        _selfKey = null;
        _pendingSaveFull = null;
        _pendingMoneySet = null;
    }

    internal void Disconnect()
    {
        _clientManualDisconnect = true;
        StopNetworkingInternal();
        _forceClientMode = false;
        _forceHostMode = false;
        _clientDiscoveryLocked = false;

        _clientAutoEnterRequested = false;
        _clientAutoEnterDone = false;
        _clientAutoEnterUntil = 0f;
        _clientLastSaveIdFromHost = string.Empty;
    }

    private void TryApplyPendingSaveFull()
    {
        if (_pendingSaveFull == null)
            return;

        var now = Time.unscaledTime;
        if (now > _pendingSaveFull.UntilAt)
        {
            _pendingSaveFull = null;
            return;
        }

        if (now < _pendingSaveFull.NextTryAt)
            return;
        _pendingSaveFull.NextTryAt = now + 0.5f;

        // If save system doesn't exist yet (main menu), pre-apply into PlayerPrefs
        // so the game's own menu flow can see a valid save.
        var local = GameAccess.TryReadSaveSystemSnapshot();
        if (local == null)
        {
            if (!_pendingSaveFull.AppliedToPlayerPrefs)
            {
                var applied = 0;
                try
                {
                    foreach (var kv in _pendingSaveFull.Data)
                    {
                        if (!Plugin.IsSaveKeyAllowedForWorldSync(kv.Key))
                            continue;
                        if (GameAccess.TryApplyPlayerPrefString(kv.Key, kv.Value))
                            applied++;
                    }

                    GameAccess.TrySavePlayerPrefs();
                    _pendingSaveFull.AppliedToPlayerPrefs = true;

                    if (Plugin.DebugLogs.Value)
                        Plugin.Log.LogInfo($"Pre-applied SaveFull to PlayerPrefs (menu): keys={applied} saveId='{_pendingSaveFull.SaveId}'");
                }
                catch (Exception ex)
                {
                    if (Plugin.DebugLogs.Value)
                        Plugin.Log.LogWarning($"Pre-apply SaveFull to PlayerPrefs failed: {ex.GetType().Name}");
                    _pendingSaveFull.AppliedToPlayerPrefs = true;
                }
            }

            // Even from menu, keep trying to enter gameplay.
            TryAutoEnterWorld();
            return;
        }

        WithRemoteApply(() =>
        {
            if (_pendingSaveFull == null)
                return;

            if (_pendingSaveFull.Wipe)
            {
                foreach (var kv in local)
                {
                    if (Plugin.IsSaveKeyAllowedForWorldSync(kv.Key))
                        GameAccess.TryDeleteSaveKey(kv.Key);
                }
            }

            foreach (var kv in _pendingSaveFull.Data)
            {
                if (Plugin.IsSaveKeyAllowedForWorldSync(kv.Key))
                    GameAccess.TryApplySaveKey(kv.Key, kv.Value);
            }

            if (Plugin.DebugLogs.Value)
                Plugin.Log.LogInfo($"Applied pending SaveFull: keys={_pendingSaveFull.Data.Count} saveId='{_pendingSaveFull.SaveId}'");

            _pendingSaveFull = null;
        });

        // After applying host save, attempt to enter the world.
        TryAutoEnterWorld();
    }

    private void TryApplyPendingMoneySet()
    {
        if (_pendingMoneySet == null)
            return;

        var now = Time.unscaledTime;
        if (now > _pendingMoneyUntil)
        {
            _pendingMoneySet = null;
            return;
        }

        if (now < _pendingMoneyNextTryAt)
            return;
        _pendingMoneyNextTryAt = now + 0.3f;

        var money = _pendingMoneySet.Value;
        if (GameAccess.TryApplyHudMoney(money))
        {
            NotifyRemoteMoneyApplied(money);
            _pendingMoneySet = null;
        }
    }

    internal void OnSaveFullReceived(string saveId, bool wipe, Dictionary<string, string> data)
    {
        if (!IsClient)
            return;

        _clientLastSaveIdFromHost = saveId ?? string.Empty;

        // Store and try-apply soon; keep for a while in case we are still in main menu.
        _pendingSaveFull = new PendingSaveFull
        {
            SaveId = saveId ?? string.Empty,
            Wipe = wipe,
            Data = data ?? new Dictionary<string, string>(StringComparer.Ordinal),
            NextTryAt = 0f,
            UntilAt = Time.unscaledTime + 25.0f,
        };

        TryApplyPendingSaveFull();

        // Some builds only create save system after entering game; try to enter now too.
        TryAutoEnterWorld();
    }

    internal void OnMoneySetReceived(int money)
    {
        if (!IsClient)
            return;

        _pendingMoneySet = money;
        _pendingMoneyNextTryAt = 0f;
        _pendingMoneyUntil = Time.unscaledTime + 25.0f;

        TryApplyPendingMoneySet();

        // HUD/save systems might appear only after entering the world.
        TryAutoEnterWorld();
    }

    private void OnDatagram(System.Net.IPEndPoint remote, byte[] data)
    {
        if (_transport == null)
            return;

        // Update host reachability timestamp for auto-reconnect.
        try
        {
            if (IsClient)
            {
                var server = _transport.ServerEndPoint;
                if (server != null && remote.Address.Equals(server.Address) && remote.Port == server.Port)
                {
                    _clientLastHostPacketAt = Time.unscaledTime;
                    _clientLastKnownServer = server;
                }
            }
        }
        catch
        {
            // ignore
        }

        try
        {
            NetMessages.ParseAndApplyIncoming(this, _transport, remote, data);
        }
        catch (Exception ex)
        {
            Plugin.Log.LogError(ex);
        }
    }

    internal void SetSelfKey(string key, string? hostVersion = null)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        _selfKey = key;
        _clientLastHostPacketAt = Time.unscaledTime;
        _clientReconnectAttempt = 0;
        _clientNextReconnectTryAt = 0f;
        _clientPendingTeleport = null;
        _clientTeleportedFromHost = false;

        if (!string.IsNullOrWhiteSpace(hostVersion) && !string.Equals(hostVersion, Plugin.PluginVersion, StringComparison.Ordinal))
            Plugin.Log.LogWarning($"Version mismatch: host={hostVersion}, client={Plugin.PluginVersion}. Please use same mod version on all peers.");

        if (IsClient && Plugin.AutoEnterWorldOnConnect.Value)
        {
            // Even if we connected automatically via config Mode=Client,
            // request auto-enter once we have a Welcome.
            _clientAutoEnterRequested = true;
            _clientAutoEnterDone = false;
            _clientAutoEnterNextTryAt = 0f;
            _clientAutoEnterUntil = Time.unscaledTime + 90.0f;
        }
        Plugin.Log.LogInfo($"SelfKey set: {_selfKey}");

        // Announce our appearance to the host immediately upon connection.
        SendLocalCustomization();
    }

    /// <summary>
    /// Sends the local player's current customization (nickname + RGB color) to all connected peers.
    /// Call this after the player saves changes in the customization menu.
    /// </summary>
    internal void SendLocalCustomization()
    {
        if (_transport == null)
            return;

        var nick = PlayerCustomization.LocalNickname;
        var col  = PlayerCustomization.LocalColor;

        var packet = NetMessages.BuildPlayerCustomization(
            peerKey:  IsHost ? "host" : (_selfKey ?? "host"),
            nickname: nick,
            r: col.r, g: col.g, b: col.b);

        if (IsHost)
            _transport.SendToAll(packet);
        else if (HasWelcome)
            _transport.SendToServer(packet);
    }


    private void ClientAutoReconnectTick()
    {
        if (!IsClient)
            return;
        if (!Plugin.AutoReconnectEnabled.Value)
            return;
        if (_clientManualDisconnect)
            return;

        var now = Time.unscaledTime;

        // If networking isn't running (e.g. host restart or reload), bring it back.
        if (_transport == null)
        {
            if (now < _clientNextReconnectTryAt)
                return;
            StartNetworking();
            return;
        }

        if (_transport.ServerEndPoint != null)
            _clientLastKnownServer = _transport.ServerEndPoint;

        var lostAfter = Plugin.AutoReconnectLostSeconds.Value;
        if (lostAfter < 1.0f)
            lostAfter = 1.0f;

        // If we believe we are connected but the host is silent, restart handshake.
        if (HasWelcome)
        {
            var silentFor = now - _clientLastHostPacketAt;
            if (silentFor >= lostAfter)
            {
                _selfKey = null;
                _clientTeleportedFromHost = false;
                _clientPendingTeleport = null;

                // Allow host to re-send authoritative state after reconnect.
                _pendingSaveFull = null;
                _pendingMoneySet = null;
                _clientLastSaveIdFromHost = string.Empty;

                // Let hello happen soon, but apply a small backoff to avoid tight loops if host is down.
                ScheduleReconnect(now, $"lost host ({silentFor:0.0}s no packets)");
            }

            return;
        }

        // Handshake phase: if discovery is disabled and server became unset, try to restore.
        if (!_transport.HasServer && _clientLastKnownServer != null)
        {
            try { _transport.SetServer(_clientLastKnownServer); } catch { /* ignore */ }
        }

        // EnsureClientConnected will send Hello when allowed.
    }

    private void ScheduleReconnect(float now, string why)
    {
        // Exponential backoff with a cap.
        _clientReconnectAttempt = Math.Max(0, _clientReconnectAttempt);
        var attempt = Math.Max(1, _clientReconnectAttempt);

        var delay = 1.0f * Mathf.Pow(2f, Mathf.Min(5, attempt - 1));
        var max = Plugin.AutoReconnectBackoffMaxSeconds.Value;
        if (max < 1.0f)
            max = 1.0f;
        delay = Mathf.Min(delay, max);
        delay += UnityEngine.Random.Range(0f, 0.35f);

        _clientNextReconnectTryAt = now + delay;
        _nextHelloAt = _clientNextReconnectTryAt;

        var server = _transport?.ServerEndPoint != null ? _transport.ServerEndPoint.ToString() : (_clientLastKnownServer != null ? _clientLastKnownServer.ToString() : "(unknown)");
        Plugin.Log.LogWarning($"AutoReconnect: {why}. Next Hello in {delay:0.0}s. Server={server}");
    }

    private void TryAutoEnterWorld()
    {
        if (!IsClient)
            return;
        if (!_clientAutoEnterRequested)
            return;
        if (!Plugin.AutoEnterWorldOnConnect.Value)
        {
            if (!_autoEnterDisabledLogged)
            {
                _autoEnterDisabledLogged = true;
                Plugin.Log.LogInfo("AutoEnterWorld: disabled by config (AutoEnterWorldOnConnect=false)");
            }
            return;
        }
        if (!HasWelcome)
            return;

        // Only consider auto-enter completed once we are actually in-game.
        if (GameAccess.IsGameplayWorldLoaded())
        {
            _clientAutoEnterDone = true;
            return;
        }

        if (_clientAutoEnterDone)
            return;

        var now = Time.unscaledTime;
        if (now > _clientAutoEnterUntil)
            return;
        if (now < _clientAutoEnterNextTryAt)
            return;
        _clientAutoEnterNextTryAt = now + 0.75f;

        var invoked = GameAccess.TryAutoEnterWorldFromMenu(_clientLastSaveIdFromHost, out var reason);

        // Log at info with throttling so users can share logs without enabling DebugLogs.
        if (now >= _nextAutoEnterLogAt || !string.Equals(_lastAutoEnterReason, reason, StringComparison.Ordinal))
        {
            _nextAutoEnterLogAt = now + 3.0f;
            _lastAutoEnterReason = reason ?? string.Empty;
            Plugin.Log.LogInfo($"AutoEnterWorld: {reason}");
        }

        // If we invoked something, give the game a moment to transition.
        if (invoked)
            _clientAutoEnterNextTryAt = now + 2.5f;
    }

    private void EnsureClientConnected()
    {
        if (_transport == null)
            return;
        if (!_transport.HasServer)
            return;
        if (HasWelcome)
            return;

        if (Plugin.AutoReconnectEnabled.Value && !_clientManualDisconnect)
        {
            if (Time.unscaledTime < _clientNextReconnectTryAt)
                return;
        }

        if (Time.unscaledTime < _nextHelloAt)
            return;

        _nextHelloAt = Time.unscaledTime + HelloIntervalSeconds;
        _transport.SendToServer(NetMessages.BuildHello());

        if (Plugin.AutoReconnectEnabled.Value && !_clientManualDisconnect)
        {
            _clientReconnectAttempt = Math.Min(_clientReconnectAttempt + 1, 50);

            var delay = 1.0f * Mathf.Pow(2f, Mathf.Min(5, _clientReconnectAttempt - 1));
            var max = Plugin.AutoReconnectBackoffMaxSeconds.Value;
            if (max < 1.0f)
                max = 1.0f;
            delay = Mathf.Min(delay, max);
            delay += UnityEngine.Random.Range(0f, 0.35f);

            _clientNextReconnectTryAt = Time.unscaledTime + delay;
        }
    }

    private void BroadcastWorldSnapshot()
    {
        if (_transport == null)
            return;

        // Hard cap snapshot rate to avoid saturating the client main thread.
        var minInterval = 1f / MaxWorldSnapshotsPerSecond;
        if (Time.unscaledTime < _nextWorldSnapshotAt)
            return;
        _nextWorldSnapshotAt = Time.unscaledTime + minInterval;

        var sendFull = Time.unscaledTime < _forceFullSnapshotUntil;

        // Build players list: host + all client states received.
        var players = new List<PlayerPose>(_hostKnownPlayers.Count + 1);

        if (GameAccess.TryReadLocalPlayerPose(out var p, out var r))
        {
            GameAccess.TryReadInCar(out var inCar);
            var heldPayload = GameAccess.TryReadHeldPayloadName(out var hp) ? hp : string.Empty;
            players.Add(new PlayerPose(
                key: "host",
                nick: Plugin.SanitizeNickname(Plugin.Nickname.Value),
                px: p.x, py: p.y, pz: p.z,
                qx: r.x, qy: r.y, qz: r.z, qw: r.w,
                inCar: inCar,
                heldPayload: heldPayload));

            // Persist host position by nickname (controller pose is preferred).
            if (Plugin.PlayerPositionsEnabled.Value && GameAccess.TryReadLocalPlayerControllerPose(out var cp, out var cr))
                PlayerPositionsStore.HostUpdatePlayer(Plugin.SanitizeNickname(Plugin.Nickname.Value), cp, cr, Time.unscaledTime);
        }

        foreach (var kv in _hostKnownPlayers)
            players.Add(kv.Value);

        CarState? carState = null;
        if (GameAccess.TryReadCarState(out var cpos, out var crot, out var cvel, out var cang, out var hasCar) && hasCar)
        {
            carState = new CarState(
                cpos.x, cpos.y, cpos.z,
                crot.x, crot.y, crot.z, crot.w,
                cvel.x, cvel.y, cvel.z,
                cang.x, cang.y, cang.z);
        }

        List<CargoItem>? cargo = null;
        var cargoRaw = GameAccess.TryReadCarCargo();
        if (cargoRaw != null)
        {
            cargo = new List<CargoItem>(cargoRaw.Count);
            foreach (var it in cargoRaw)
            {
                cargo.Add(new CargoItem(
                    it.Name,
                    it.LocalPos.x, it.LocalPos.y, it.LocalPos.z,
                    it.LocalRot.x, it.LocalRot.y, it.LocalRot.z, it.LocalRot.w));
            }
        }

        var payload = NetMessages.BuildWorldSnapshot(
            save: null,
            dayTime: GameAccess.TryReadDayNightTime(out var dayTime) ? dayTime : (float?)null,
            money: Plugin.SharedMoneyEnabled.Value && GameAccess.TryReadHudMoney(out var money) ? money : (int?)null,
            jobs: sendFull ? GameAccess.TryReadJobBoardJobs() : null,
            players: players,
            car: carState,
            cargo: cargo);

        _transport.SendToAll(payload);

        TraceSnapshotSend(players.Count, carState.HasValue, cargo?.Count ?? 0);
    }

    private void SendLocalClientState()
    {
        if (_transport == null || !_transport.HasServer)
            return;

        if (!GameAccess.TryReadLocalPlayerPose(out var p, out var r))
            return;

        GameAccess.TryReadInCar(out var inCar);
        var msg = NetMessages.BuildClientState(
            Plugin.Nickname.Value,
            p.x, p.y, p.z,
            r.x, r.y, r.z, r.w,
            inCar,
            GameAccess.TryReadHeldPayloadName(out var held) ? held : string.Empty);

        _transport.SendToServer(msg);

        // Also send our car pose so host can render a second car for this client.
        if (GameAccess.TryReadCarState(out var cpos, out var crot, out _, out _, out var hasCar) && hasCar)
        {
            var carMsg = NetMessages.BuildClientCarState(
                cpos.x, cpos.y, cpos.z,
                crot.x, crot.y, crot.z, crot.w);
            _transport.SendToServer(carMsg);
        }

        // Send cargo less frequently (can be larger).
        if (Time.unscaledTime >= _nextClientCargoAt)
        {
            _nextClientCargoAt = Time.unscaledTime + ClientCargoIntervalSeconds;
            var cargo = GameAccess.TryReadCarCargo();
            if (cargo != null)
                _transport.SendToServer(NetMessages.BuildClientCarCargo(cargo));
            else
                _transport.SendToServer(NetMessages.BuildClientCarCargo(Array.Empty<(string, UnityEngine.Vector3, UnityEngine.Quaternion)>()));
        }
    }

    private void SendLocalCarSfxIfNeeded()
    {
        if (!Plugin.CarSoundSyncEnabled.Value)
            return;

        // Do not require "in car" flag: in some builds this flag is unreliable,
        // while car AudioSources still play correctly and should be synced.
        if (!GameAccess.TryReadCarState(out _, out _, out _, out _, out var hasCar) || !hasCar)
            return;

        var minInterval = Plugin.CarSoundSyncMinIntervalSeconds.Value;
        if (minInterval < 0.02f)
            minInterval = 0.02f;

        if (!GameAccess.TryGetLocalCarSfxPulses(out var sfxEvents))
            return;

        var now = Time.unscaledTime;
        for (var i = 0; i < sfxEvents.Count; i++)
        {
            var evt = sfxEvents[i];
            var sfxId = evt.SfxId;
            if (Plugin.IsHornOnlyCarSoundMode() && sfxId != GameAccess.CarSfxHorn)
                continue;

            var clipName = evt.ClipName ?? string.Empty;
            var sourceName = evt.SourceName ?? string.Empty;

            var eventKey = $"{sfxId}|{clipName}|{sourceName}";
            if (_nextCarSfxAt.TryGetValue(eventKey, out var nextAt) && now < nextAt)
                continue;

            _nextCarSfxAt[eventKey] = now + minInterval;

            if (IsHost)
            {
                _transport?.SendToAll(NetMessages.BuildCarSfx("host", sfxId, clipName, sourceName));
                if (Plugin.DebugLogs.Value)
                    Plugin.Log.LogInfo($"CarSfx send host: car=host sfx={CarSfxName(sfxId)}({sfxId}) clip='{clipName}' src='{sourceName}'");
                continue;
            }

            if (IsClient && _transport != null && _transport.HasServer && HasWelcome)
            {
                _transport.SendToServer(NetMessages.BuildClientCarSfx(sfxId, clipName, sourceName));
                if (Plugin.DebugLogs.Value)
                    Plugin.Log.LogInfo($"CarSfx send client: sfx={CarSfxName(sfxId)}({sfxId}) clip='{clipName}' src='{sourceName}'");
            }
        }
    }

    internal void OnClientState(System.Net.IPEndPoint remote, string nickname, float px, float py, float pz, float qx, float qy, float qz, float qw, bool inCar, string heldPayload, UnityEngine.Vector3? controllerPos, UnityEngine.Quaternion? controllerRot)
    {
        var key = remote.ToString();
        MarkClientSeen(key);
        var nick = Plugin.SanitizeNickname(nickname);
        _hostKnownNick[key] = nick;
        _hostKnownPlayers[key] = new PlayerPose(key, nick, px, py, pz, qx, qy, qz, qw, inCar, heldPayload ?? string.Empty);

        if (Plugin.PlayerPositionsEnabled.Value)
        {
            var pos = controllerPos ?? new UnityEngine.Vector3(px, py, pz);
            var rot = controllerRot ?? new UnityEngine.Quaternion(qx, qy, qz, qw);
            PlayerPositionsStore.HostUpdatePlayer(nick, pos, rot, Time.unscaledTime);
        }

        // Host also needs to see remote players.
        RemoteAvatars.ApplyPlayerPose(key, nick, px, py, pz, qx, qy, qz, qw, inCar, heldPayload);
    }

    internal void OnClientCarState(System.Net.IPEndPoint remote, float px, float py, float pz, float qx, float qy, float qz, float qw)
    {
        var key = remote.ToString();
        MarkClientSeen(key);
        var pos = new UnityEngine.Vector3(px, py, pz);
        var rot = new UnityEngine.Quaternion(qx, qy, qz, qw);
        _hostKnownCars[key] = (pos, rot);

        // Render client's car as a second car in host world.
        var nick = _hostKnownNick.TryGetValue(key, out var n) ? n : "";
        RemoteCars.ApplyCarState($"client.{key}", pos, rot, nick);
    }

    internal void OnClientCarCargo(System.Net.IPEndPoint remote, IReadOnlyList<(string Name, UnityEngine.Vector3 LocalPos, UnityEngine.Quaternion LocalRot)> cargo)
    {
        var key = remote.ToString();
        MarkClientSeen(key);
        RemoteCars.ApplyCargo($"client.{key}", cargo);
    }

    internal void OnClientCarSfx(System.Net.IPEndPoint remote, byte sfxId, string? clipName, string? sourceName)
    {
        if (!IsHost)
            return;
        if (Plugin.IsHornOnlyCarSoundMode() && sfxId != GameAccess.CarSfxHorn)
            return;

        var key = remote.ToString();
        MarkClientSeen(key);

        var carKey = $"client.{key}";
        RemoteCars.PlayCarSfx(carKey, sfxId, clipName, sourceName);

        clipName ??= string.Empty;
        sourceName ??= string.Empty;

        if (Plugin.DebugLogs.Value)
            Plugin.Log.LogInfo($"CarSfx recv host: from={remote} car={carKey} sfx={CarSfxName(sfxId)}({sfxId}) clip='{clipName}' src='{sourceName}'");

        if (_transport != null)
        {
            _transport.SendToAll(NetMessages.BuildCarSfx(carKey, sfxId, clipName, sourceName));
            if (Plugin.DebugLogs.Value)
                Plugin.Log.LogInfo($"CarSfx relay host: car={carKey} sfx={CarSfxName(sfxId)}({sfxId}) clip='{clipName}' src='{sourceName}'");
        }
    }

    internal void OnCarSfx(string carKey, byte sfxId, string? clipName, string? sourceName)
    {
        if (!IsClient)
            return;

        if (!string.IsNullOrWhiteSpace(_selfKey) && string.Equals(carKey, $"client.{_selfKey}", StringComparison.Ordinal))
            return;

        if (Plugin.IsHornOnlyCarSoundMode() && sfxId != GameAccess.CarSfxHorn)
            return;

        RemoteCars.PlayCarSfx(carKey, sfxId, clipName, sourceName);

        clipName ??= string.Empty;
        sourceName ??= string.Empty;

        if (Plugin.DebugLogs.Value)
            Plugin.Log.LogInfo($"CarSfx recv client: car={carKey} sfx={CarSfxName(sfxId)}({sfxId}) clip='{clipName}' src='{sourceName}'");
    }

    private static string CarSfxName(byte sfxId)
    {
        return sfxId switch
        {
            GameAccess.CarSfxHorn => "horn",
            GameAccess.CarSfxSkid => "skid",
            GameAccess.CarSfxCrash => "crash",
            _ => "unknown",
        };
    }

    internal void OnClientReportedMoney(System.Net.IPEndPoint remote, int reportedMoney)
    {
        if (!IsHost)
            return;
        if (!Plugin.SharedMoneyEnabled.Value)
            return;

        if (reportedMoney < 0)
            reportedMoney = 0;

        var hasCur = GameAccess.TryReadHudMoney(out var cur);
        if (!hasCur)
        {
            if (_lastObservedMoney == null)
                return;
            cur = _lastObservedMoney.Value;
        }

        // Money from client (e.g. job completion) should not be lost if MoneyDelta packet was dropped.
        // Accept only upward correction here; spending is still handled by MoneyDelta.
        if (reportedMoney <= cur)
            return;

        WithRemoteApply(() => GameAccess.TryApplyHudMoney(reportedMoney));
        NotifyRemoteMoneyApplied(reportedMoney);

        if (_transport != null)
            _transport.SendToAll(NetMessages.BuildMoneySet(reportedMoney));

        if (Plugin.DebugLogs.Value)
            Plugin.Log.LogInfo($"MoneyReport from {remote}: report={reportedMoney} total={reportedMoney}");
    }

    internal void OnClientHello(System.Net.IPEndPoint remote, string? clientVersion)
    {
        var key = remote.ToString();
        MarkClientSeen(key);

        if (!string.IsNullOrWhiteSpace(clientVersion))
        {
            _hostKnownClientVersion[key] = clientVersion;
            if (!string.Equals(clientVersion, Plugin.PluginVersion, StringComparison.Ordinal))
                Plugin.Log.LogWarning($"Version mismatch: client {remote} has {clientVersion}, host has {Plugin.PluginVersion}. Sound sync/features may not work.");
        }
    }

    internal void QueueInitialSync(System.Net.IPEndPoint remote)
    {
        if (!IsHost)
            return;

        var key = remote.ToString();
        _hostInitialSync[key] = new InitialSyncState
        {
            EndPoint = remote,
            NextSendAt = 0f,
            UntilAt = Time.unscaledTime + InitialSyncDurationSeconds,
        };

        // Send host's own appearance directly to the newly connected client.
        if (_transport != null)
        {
            var nick = PlayerCustomization.LocalNickname;
            var col  = PlayerCustomization.LocalColor;
            _transport.SendTo(remote, NetMessages.BuildPlayerCustomization("host", nick, col.r, col.g, col.b));
        }
    }


    internal void NotifyRemoteMoneyApplied(int money)
    {
        _lastObservedMoney = money;
        _suppressMoneyEchoUntil = UnityEngine.Time.unscaledTime + 0.35f;
    }

    internal void OnPlayerLocations(string saveId, Dictionary<string, (UnityEngine.Vector3 Pos, UnityEngine.Quaternion Rot)> locations)
    {
        if (!IsClient)
            return;

        if (!Plugin.PlayerPositionsEnabled.Value)
            return;

        if (!Plugin.PlayerPositionsClientTeleportOnJoin.Value)
            return;

        if (_clientTeleportedFromHost)
            return;

        // Ignore stale locations from another save slot/city while reconnecting or loading.
        if (!string.IsNullOrWhiteSpace(_clientLastSaveIdFromHost) &&
            !string.IsNullOrWhiteSpace(saveId) &&
            !string.Equals(GameAccess.NormalizeSaveIdForSyncCompare(_clientLastSaveIdFromHost), GameAccess.NormalizeSaveIdForSyncCompare(saveId), StringComparison.Ordinal))
            return;

        var selfNick = Plugin.SanitizeNickname(Plugin.Nickname.Value);
        if (locations.TryGetValue(selfNick, out var tp))
        {
            _clientPendingTeleport = tp;
            _clientPendingTeleportUntil = Time.unscaledTime + 5.0f;
        }
    }

    private void MarkClientSeen(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;
        _hostClientLastSeenAt[key] = Time.unscaledTime;
    }

    private void PruneDisconnectedClients()
    {
        if (!IsHost)
            return;
        if (_transport == null)
            return;

        var now = Time.unscaledTime;
        if (now < _nextHostPruneAt)
            return;
        _nextHostPruneAt = now + HostPruneIntervalSeconds;

        var timeout = Plugin.ClientTimeoutSeconds != null ? Plugin.ClientTimeoutSeconds.Value : 6.0f;
        if (timeout < 1.0f)
            timeout = 1.0f;

        var toRemove = new List<string>();
        foreach (var kv in _hostClientLastSeenAt)
        {
            if (now - kv.Value > timeout)
                toRemove.Add(kv.Key);
        }

        for (var i = 0; i < toRemove.Count; i++)
        {
            var key = toRemove[i];

            _hostClientLastSeenAt.Remove(key);
            _hostKnownPlayers.Remove(key);
            _hostKnownCars.Remove(key);
            _hostKnownNick.Remove(key);
            _hostKnownClientVersion.Remove(key);
            _hostInitialSync.Remove(key);

            // Remove ghost visuals on host.
            RemoteAvatars.Remove(key);
            RemoteCars.Remove($"client.{key}");

            // Stop sending snapshots to this endpoint.
            _transport.UnregisterClient(key);

            Plugin.Log.LogInfo($"Client disconnected (timeout): {key}");
        }
    }

    internal void SendSaveDelta(string key, string value)
    {
        if (_transport == null)
            return;

        var msg = NetMessages.BuildSaveSet(key, value);
        if (IsHost)
            _transport.SendToAll(msg);
        else
            _transport.SendToServer(msg);
    }

    internal void SendSaveDelete(string key)
    {
        if (_transport == null)
            return;

        var msg = NetMessages.BuildSaveDelete(key);
        if (IsHost)
            _transport.SendToAll(msg);
        else
            _transport.SendToServer(msg);
    }

    internal void WithRemoteApply(Action action)
    {
        if (_applyingRemote)
            action();
        else
        {
            _applyingRemote = true;
            try { action(); }
            finally { _applyingRemote = false; }
        }
    }

    internal bool IsApplyingRemote => _applyingRemote;
}

