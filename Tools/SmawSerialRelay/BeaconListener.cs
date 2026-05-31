using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SmawSerialRelay;

/// <summary>
/// Listens for UDP beacon packets broadcast by the Quest
/// (QuestBeaconSender MonoBehaviour).
///
/// Beacon format: "SMAW_QUEST:{telemetryPort}"
/// Example:       "SMAW_QUEST:9100"
///
/// The Quest IP is read from the UDP packet's RemoteEndPoint —
/// the Quest does not need to include it in the payload.
/// </summary>
public sealed class BeaconListener : IDisposable
{
    private const string BeaconPrefix = "SMAW_QUEST:";

    private readonly RelayConfig _config;
    private UdpClient? _udp;
    private Thread? _thread;
    private volatile bool _keepRunning;

    private volatile string? _questIp;
    private int _questPort;
    private DateTime _lastBeaconUtc = DateTime.MinValue;

    // ── Public state ──────────────────────────────────────────────────────────

    /// <summary>IP of the Quest, or null when not yet discovered.</summary>
    public string? QuestIp => _questIp;

    /// <summary>Telemetry port advertised by the Quest beacon.</summary>
    public int QuestPort => _questPort;

    /// <summary>True while a recent beacon has been received.</summary>
    public bool IsConnected =>
        _questIp != null &&
        (DateTime.UtcNow - _lastBeaconUtc).TotalSeconds < _config.BeaconTimeoutSeconds;

    /// <summary>Raised on the listener thread when a new Quest IP is discovered.</summary>
    public event Action<string, int>? QuestDiscovered;

    /// <summary>Raised on the listener thread when the Quest beacon times out.</summary>
    public event Action? QuestLost;

    public BeaconListener(RelayConfig config)
    {
        _config = config;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Start()
    {
        if (_thread != null) return;

        _keepRunning = true;
        _udp = new UdpClient(_config.BeaconListenPort);

        _thread = new Thread(ListenLoop)
        {
            IsBackground = true,
            Name = "BeaconListener"
        };
        _thread.Start();
    }

    public void Stop()
    {
        _keepRunning = false;
        try { _udp?.Close(); } catch { /* ignore */ }
        _thread?.Join(500);
        _thread = null;
    }

    public void Dispose() => Stop();

    // ── Background thread ─────────────────────────────────────────────────────

    private void ListenLoop()
    {
        bool wasConnected = false;

        while (_keepRunning)
        {
            // ── Check timeout on already-discovered Quest ──────────────────
            if (wasConnected && !IsConnected)
            {
                _questIp = null;
                wasConnected = false;
                QuestLost?.Invoke();
            }

            // ── Try to receive a beacon packet ─────────────────────────────
            try
            {
                // Non-blocking check using Available so we can poll the timeout
                if (_udp!.Available == 0)
                {
                    Thread.Sleep(100);
                    continue;
                }

                var remote = new IPEndPoint(IPAddress.Any, 0);
                var bytes = _udp.Receive(ref remote);
                var payload = Encoding.UTF8.GetString(bytes).Trim();

                if (!payload.StartsWith(BeaconPrefix, StringComparison.Ordinal))
                    continue;

                // Parse port from payload
                if (!int.TryParse(payload[BeaconPrefix.Length..], out var port))
                    continue;

                _lastBeaconUtc = DateTime.UtcNow;

                var ip = remote.Address.ToString();
                bool newQuest = ip != _questIp || port != _questPort;

                _questIp   = ip;
                _questPort = port;

                if (newQuest || !wasConnected)
                {
                    wasConnected = true;
                    QuestDiscovered?.Invoke(ip, port);
                }
            }
            catch (SocketException)
            {
                if (_keepRunning) Thread.Sleep(100);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
        }
    }
}
