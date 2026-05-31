using System.Net.Sockets;
using System.Text;

namespace SmawSerialRelay;

/// <summary>
/// Forwards raw CSV telemetry lines to the Quest via UDP.
/// Target IP is set dynamically by the BeaconListener when the Quest is found.
///
/// Thread-safe: SetTarget and Forward can be called from different threads.
/// </summary>
public sealed class UdpForwarder : IDisposable
{
    private readonly UdpClient _udp = new();
    private readonly object _lock = new();

    private string? _targetIp;
    private int _targetPort;

    // ── Public state ──────────────────────────────────────────────────────────

    public bool HasTarget => _targetIp != null;
    public string? TargetIp   => _targetIp;
    public int     TargetPort => _targetPort;
    public long    PacketsSent { get; private set; }

    // ── API ───────────────────────────────────────────────────────────────────

    /// <summary>Set or update the Quest destination.</summary>
    public void SetTarget(string ip, int port)
    {
        lock (_lock)
        {
            _targetIp   = ip;
            _targetPort = port;
        }
    }

    /// <summary>Clear the destination (Quest lost).</summary>
    public void ClearTarget()
    {
        lock (_lock)
        {
            _targetIp = null;
        }
    }

    /// <summary>
    /// Send a raw telemetry line to the Quest.
    /// Does nothing if no target is set.
    /// </summary>
    public void Forward(string line)
    {
        string? ip;
        int port;

        lock (_lock)
        {
            ip   = _targetIp;
            port = _targetPort;
        }

        if (ip == null) return;

        try
        {
            var payload = Encoding.UTF8.GetBytes(line + "\n");
            _udp.Send(payload, payload.Length, ip, port);
            PacketsSent++;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[UDP] Error enviando paquete: {ex.Message}");
        }
    }

    public void Dispose()
    {
        try { _udp.Close(); } catch { /* ignore */ }
    }
}
