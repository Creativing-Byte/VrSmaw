using System.IO.Ports;

namespace SmawSerialRelay;

/// <summary>
/// Reads CSV telemetry lines from the Arduino over a serial USB connection.
/// Mirrors the logic in ArduinoBridgeReceiver.ReceiveSerialLoop / ResolveSerialPortName.
///
/// Raises <see cref="LineReceived"/> for each non-empty line read.
/// Raises <see cref="PortChanged"/> when the active port changes (useful for status display).
/// </summary>
public sealed class SerialReader : IDisposable
{
    private readonly RelayConfig _config;
    private Thread? _thread;
    private volatile bool _keepRunning;

    // ── Public state ──────────────────────────────────────────────────────────

    public string? ActivePort { get; private set; }
    public bool IsOpen       { get; private set; }
    public long LinesRead    { get; private set; }

    public event Action<string>? LineReceived;
    public event Action<string>? PortChanged;

    public SerialReader(RelayConfig config)
    {
        _config = config;
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public void Start()
    {
        if (_thread != null) return;

        _keepRunning = true;
        _thread = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = "SerialReader"
        };
        _thread.Start();
    }

    public void Stop()
    {
        _keepRunning = false;
        _thread?.Join(1000);
        _thread = null;
    }

    public void Dispose() => Stop();

    // ── Background thread ─────────────────────────────────────────────────────

    private void ReadLoop()
    {
        while (_keepRunning)
        {
            SerialPort? port = null;
            try
            {
                var portName = ResolvePortName();
                if (portName == null)
                {
                    Console.WriteLine("[Serial] No se encontró puerto Arduino. Reintentando en 2s...");
                    Thread.Sleep(2000);
                    continue;
                }

                port = new SerialPort(portName, _config.BaudRate)
                {
                    NewLine      = "\n",
                    ReadTimeout  = 500,
                    DtrEnable    = false,
                    RtsEnable    = false
                };

                port.Open();
                ActivePort = portName;
                IsOpen     = true;

                if (ActivePort != portName)
                    PortChanged?.Invoke(portName);

                Console.WriteLine($"[Serial] Abierto {portName} @ {_config.BaudRate} baud");

                while (_keepRunning && port.IsOpen)
                {
                    try
                    {
                        var line = port.ReadLine();
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            LinesRead++;
                            LineReceived?.Invoke(line.Trim());
                        }
                    }
                    catch (TimeoutException)
                    {
                        // Normal — Arduino hasn't sent data yet, keep waiting
                    }
                }
            }
            catch (Exception ex)
            {
                IsOpen     = false;
                ActivePort = null;
                Console.WriteLine($"[Serial] Error: {ex.Message}. Reintentando en 2s...");
                Thread.Sleep(2000);
            }
            finally
            {
                IsOpen = false;
                try { port?.Close(); } catch { /* ignore */ }
            }
        }
    }

    // ── Port autodetection ────────────────────────────────────────────────────

    private string? ResolvePortName()
    {
        // Use explicitly configured port if provided
        if (!string.IsNullOrWhiteSpace(_config.SerialPort))
            return _config.SerialPort;

        string[] available;
        try
        {
            available = SerialPort.GetPortNames();
        }
        catch
        {
            return null;
        }

        if (available.Length == 0) return null;

        // Match against known Arduino port name patterns
        foreach (var hint in _config.PortHints)
        {
            foreach (var name in available)
            {
                if (name.Contains(hint, StringComparison.OrdinalIgnoreCase))
                    return name;
            }
        }

        // Fallback: return the first available port
        return available[0];
    }
}
