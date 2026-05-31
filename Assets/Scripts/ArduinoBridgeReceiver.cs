using System;
using System.Globalization;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEngine;

[DisallowMultipleComponent]
public class ArduinoBridgeReceiver : MonoBehaviour
{
    public enum TransportMode
    {
        SerialUsb,
        Udp
    }

    [Serializable]
    public struct WeldSensorTelemetry
    {
        public long timestampMs;
        public float laserDistanceMm;
        public float electrodeConsumedMm;
        public float servoNormalized;
        public float pitchDeg;
        public float rollDeg;
        public float yawDeg;
        public float trackingConfidence;
        public bool triggerPressed;
        /// <summary>
        /// Physical electrode selection button: 0 = none, 1 = E6013 3/32", 2 = E6013 1/8", 3 = E6013 5/32".
        /// Sent as a momentary pulse by the Arduino when the user presses a button.
        /// </summary>
        public int electrodeButtonIndex;

        public bool HasAnyData()
        {
            return timestampMs != 0
                || laserDistanceMm > 0f
                || electrodeConsumedMm > 0f
                || servoNormalized > 0f
                || Mathf.Abs(pitchDeg) > 0f
                || Mathf.Abs(rollDeg) > 0f
                || Mathf.Abs(yawDeg) > 0f
                || triggerPressed;
        }
    }

    [Header("Transport")]
    [SerializeField] private bool autoStart = true;
    [SerializeField] private TransportMode transportMode = TransportMode.Udp;

    [Header("Serial")]
    [SerializeField] private string serialPortName = "COM3";
    [SerializeField] private int serialBaudRate = 115200;
    [SerializeField] private bool autoDetectSerialPort = true;
    [SerializeField] private string serialPortHint = "Arduino,usbmodem,wchusbserial,ttyACM,ttyUSB,COM";

    [Header("UDP")]
    [SerializeField] private int listenPort = 9100;

    [Header("Command Output (Unity → Arduino)")]
    [Tooltip("Enable sending servo-target commands back to the Arduino.")]
    [SerializeField] private bool enableCommandOutput = true;
    [Tooltip("For UDP mode: IP of the PC/bridge that relays UDP commands to the Arduino via Serial. " +
             "Leave empty to disable UDP command output.")]
    [SerializeField] private string commandTargetIp = "";
    [Tooltip("UDP port on the bridge PC that listens for commands from Unity.")]
    [SerializeField] private int commandTargetPort = 9101;

    [Header("Diagnostics")]
    [SerializeField] private bool verboseLogging;
    [SerializeField] private string latestRawPayload;
    [SerializeField] private WeldSensorTelemetry latestTelemetry;
    [SerializeField] private string activeTransportEndpoint;

    public static ArduinoBridgeReceiver Instance { get; private set; }

    public WeldSensorTelemetry LatestTelemetry => latestTelemetry;
    public string LatestRawPayload => latestRawPayload;
    public string ActiveTransportEndpoint => activeTransportEndpoint;
    public bool IsRunning => _workerThread != null;

    public event Action<WeldSensorTelemetry> TelemetryUpdated;

    private readonly object _payloadLock = new object();
    private readonly object _sendLock    = new object();
    private readonly System.Collections.Generic.Queue<string> _sendQueue
        = new System.Collections.Generic.Queue<string>(16);

    private Thread _workerThread;
    private UdpClient _udpClient;
    private UdpClient _udpSender;
    private bool _keepRunning;
    private bool _hasPendingPayload;
    private string _pendingPayload;

    // Accessible from main thread for serial writes (set/cleared by ReceiveSerialLoop)
    private volatile SerialPortProxy _activeSerialPort;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    private void OnEnable()
    {
        if (autoStart)
        {
            StartReceiver();
        }
    }

    // ── Public command API ────────────────────────────────────────────────────

    /// <summary>
    /// Tell the Arduino where to position the electrode rack (0 = full, 1 = spent).
    /// Maps directly to the physical servo position so the hardware mirrors the VR
    /// electrode visual.  Safe to call every frame — internally throttled via queue.
    /// </summary>
    public void SendServoTarget(float normalized)
    {
        if (!enableCommandOutput) return;
        normalized = Mathf.Clamp01(normalized);
        EnqueueCommand($"S:{normalized:F3}");
    }

    /// <summary>Tell the Arduino to home the rack (new electrode inserted).</summary>
    public void SendElectrodeReset()
    {
        if (!enableCommandOutput) return;
        EnqueueCommand("R");
    }

    // ── Internal command queue ────────────────────────────────────────────────

    private void EnqueueCommand(string cmd)
    {
        lock (_sendLock)
        {
            // Keep only the latest command of the same type to avoid queue build-up
            _sendQueue.Enqueue(cmd);
            while (_sendQueue.Count > 8)
                _sendQueue.Dequeue();
        }
    }

    private void FlushSendQueue()
    {
        string cmd;
        lock (_sendLock)
        {
            if (_sendQueue.Count == 0) return;
            cmd = _sendQueue.Dequeue();
        }

        // ── Serial write (Editor / PC build) ──────────────────────────────────
        // SerialPortProxy.WriteLine appends the NewLine character ("\n") itself,
        // so pass only the raw command string.
        var serialPort = _activeSerialPort;
        if (serialPort != null)
        {
            try { serialPort.WriteLine(cmd); }
            catch (Exception ex)
            {
                if (verboseLogging)
                    Debug.LogWarning($"[ArduinoBridgeReceiver] Serial write failed: {ex.Message}");
            }
        }

        // ── UDP send (Quest / any platform with a PC bridge relay) ────────────
        // Include the newline so the Arduino's readline parser terminates correctly.
        if (!string.IsNullOrEmpty(commandTargetIp))
        {
            try
            {
                if (_udpSender == null) _udpSender = new UdpClient();
                var bytes = Encoding.UTF8.GetBytes(cmd + "\n");
                _udpSender.Send(bytes, bytes.Length, commandTargetIp, commandTargetPort);
            }
            catch (Exception ex)
            {
                if (verboseLogging)
                    Debug.LogWarning($"[ArduinoBridgeReceiver] UDP send failed: {ex.Message}");
            }
        }
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Update()
    {
        // ── Flush outgoing command queue first ────────────────────────────────
        FlushSendQueue();

        // ── Process incoming telemetry ────────────────────────────────────────
        string payload = null;

        lock (_payloadLock)
        {
            if (_hasPendingPayload)
            {
                payload = _pendingPayload;
                _pendingPayload = null;
                _hasPendingPayload = false;
            }
        }

        if (string.IsNullOrWhiteSpace(payload))
        {
            return;
        }

        latestRawPayload = payload;

        if (TryParsePayload(payload, out var telemetry))
        {
            latestTelemetry = telemetry;
            TelemetryUpdated?.Invoke(latestTelemetry);
        }
        else if (verboseLogging)
        {
            Debug.LogWarning($"ArduinoBridgeReceiver could not parse payload: {payload}");
        }
    }

    private void OnDisable()
    {
        StopReceiver();
    }

    private void OnDestroy()
    {
        StopReceiver();

        if (Instance == this)
        {
            Instance = null;
        }
    }

    public void StartReceiver()
    {
        if (_workerThread != null)
        {
            return;
        }

        _keepRunning = true;

        if (transportMode == TransportMode.Udp)
        {
            _udpClient = new UdpClient(listenPort);
            activeTransportEndpoint = $"udp://0.0.0.0:{listenPort}";
            _workerThread = new Thread(ReceiveUdpLoop)
            {
                IsBackground = true,
                Name = "ArduinoBridgeReceiverUdp"
            };

            if (verboseLogging)
            {
                Debug.Log($"ArduinoBridgeReceiver listening on {activeTransportEndpoint}");
            }
        }
        else
        {
            activeTransportEndpoint = "serial://resolving";
            _workerThread = new Thread(ReceiveSerialLoop)
            {
                IsBackground = true,
                Name = "ArduinoBridgeReceiverSerial"
            };

            if (verboseLogging)
            {
                Debug.Log("ArduinoBridgeReceiver opening Serial USB.");
            }
        }

        _workerThread.Start();
    }

    public void Configure(int port, bool restartIfRunning = true)
    {
        if (listenPort == port)
        {
            return;
        }

        listenPort = port;

        if (restartIfRunning && IsRunning)
        {
            StopReceiver();
            StartReceiver();
        }
    }

    public void ConfigureSerial(string portName, int baudRate, bool restartIfRunning = true)
    {
        serialPortName = portName;
        serialBaudRate = baudRate;

        if (restartIfRunning && IsRunning)
        {
            StopReceiver();
            StartReceiver();
        }
    }

    public bool TryGetLatest(out WeldSensorTelemetry telemetry)
    {
        telemetry = latestTelemetry;
        return telemetry.HasAnyData();
    }

    public void StopReceiver()
    {
        _keepRunning = false;
        activeTransportEndpoint = null;

        try { _udpClient?.Close(); } catch { }
        _udpClient = null;

        try { _udpSender?.Close(); } catch { }
        _udpSender = null;

        if (_workerThread != null && _workerThread.IsAlive)
        {
            _workerThread.Join(250);
        }

        _workerThread = null;
    }

    private void ReceiveUdpLoop()
    {
        while (_keepRunning)
        {
            try
            {
                var remoteEndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
                var bytes = _udpClient.Receive(ref remoteEndPoint);
                var payload = Encoding.UTF8.GetString(bytes);
                PushPendingPayload(payload);
            }
            catch (SocketException)
            {
                if (_keepRunning)
                {
                    Thread.Sleep(50);
                }
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (verboseLogging)
                {
                    Debug.LogWarning($"ArduinoBridgeReceiver UDP loop error: {ex.Message}");
                }

                Thread.Sleep(100);
            }
        }
    }

#if !UNITY_ANDROID || UNITY_EDITOR
    private void ReceiveSerialLoop()
    {
        while (_keepRunning)
        {
            SerialPortProxy serialPort = null;

            try
            {
                var resolvedPortName = ResolveSerialPortName();
                if (string.IsNullOrWhiteSpace(resolvedPortName))
                {
                    if (verboseLogging)
                    {
                        Debug.LogWarning("ArduinoBridgeReceiver could not resolve a serial port for the Arduino.");
                    }

                    Thread.Sleep(1000);
                    continue;
                }

                serialPort = SerialPortProxy.Open(resolvedPortName, serialBaudRate);
                _activeSerialPort = serialPort;   // expose for main-thread writes
                activeTransportEndpoint = $"serial://{resolvedPortName}@{serialBaudRate}";

                if (verboseLogging)
                {
                    Debug.Log($"ArduinoBridgeReceiver reading {activeTransportEndpoint}");
                }

                while (_keepRunning && serialPort.IsOpen)
                {
                    try
                    {
                        var payload = serialPort.ReadLine();
                        if (string.IsNullOrWhiteSpace(payload))
                        {
                            continue;
                        }

                        PushPendingPayload(payload);
                    }
                    catch (TimeoutException)
                    {
                        // Keep waiting for data.
                    }
                }
            }
            catch (Exception ex)
            {
                activeTransportEndpoint = null;

                if (verboseLogging)
                {
                    Debug.LogWarning($"ArduinoBridgeReceiver serial loop error: {ex.Message}");
                }

                Thread.Sleep(1000);
            }
            finally
            {
                _activeSerialPort = null;   // no longer writable
                try
                {
                    if (serialPort != null && serialPort.IsOpen)
                    {
                        serialPort.Close();
                    }
                }
                catch
                {
                    // Ignore cleanup exceptions on shutdown.
                }
            }
        }
    }

    private string ResolveSerialPortName()
    {
        if (!autoDetectSerialPort)
        {
            return serialPortName;
        }

        string[] portNames;
        try
        {
            portNames = SerialPortProxy.GetPortNames();
        }
        catch
        {
            return serialPortName;
        }

        if (portNames == null || portNames.Length == 0)
        {
            return serialPortName;
        }

        var hints = string.IsNullOrWhiteSpace(serialPortHint)
            ? Array.Empty<string>()
            : serialPortHint.Split(',');

        foreach (var hint in hints)
        {
            var trimmedHint = hint.Trim();
            if (string.IsNullOrWhiteSpace(trimmedHint))
            {
                continue;
            }

            foreach (var portName in portNames)
            {
                if (portName.IndexOf(trimmedHint, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return portName;
                }
            }
        }

        foreach (var portName in portNames)
        {
            if (portName.IndexOf("usb", StringComparison.OrdinalIgnoreCase) >= 0
                || portName.IndexOf("COM", StringComparison.OrdinalIgnoreCase) >= 0
                || portName.IndexOf("tty", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return portName;
            }
        }

        return portNames[0];
    }
#else
    private void ReceiveSerialLoop()
    {
        if (verboseLogging)
        {
            Debug.LogWarning("ArduinoBridgeReceiver serial mode is not supported on this build target.");
        }

        while (_keepRunning)
        {
            Thread.Sleep(1000);
        }
    }

    private string ResolveSerialPortName()
    {
        return serialPortName;
    }
#endif

    private void PushPendingPayload(string payload)
    {
        lock (_payloadLock)
        {
            _pendingPayload = payload;
            _hasPendingPayload = true;
        }
    }

    private static bool TryParsePayload(string payload, out WeldSensorTelemetry telemetry)
    {
        payload = payload.Trim();

        if (payload.StartsWith("{", StringComparison.Ordinal))
        {
            return TryParseJsonLikePayload(payload, out telemetry);
        }

        return TryParseCsvPayload(payload, out telemetry);
    }

    private static bool TryParseCsvPayload(string payload, out WeldSensorTelemetry telemetry)
    {
        telemetry = default;

        var parts = payload.Split(',');
        if (parts.Length < 8)
        {
            return false;
        }

        if (!TryParseLong(parts[0], out telemetry.timestampMs))
        {
            telemetry.timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        if (!TryParseFloat(parts[1], out telemetry.laserDistanceMm)) return false;
        if (!TryParseFloat(parts[2], out telemetry.electrodeConsumedMm)) return false;
        if (!TryParseFloat(parts[3], out telemetry.servoNormalized)) return false;
        if (!TryParseFloat(parts[4], out telemetry.pitchDeg)) return false;
        if (!TryParseFloat(parts[5], out telemetry.rollDeg)) return false;
        if (!TryParseFloat(parts[6], out telemetry.yawDeg)) return false;

        telemetry.triggerPressed = TryParseBool(parts[7]);

        if (parts.Length > 8)
        {
            TryParseFloat(parts[8], out telemetry.trackingConfidence);
        }

        if (parts.Length > 9)
        {
            TryParseInt(parts[9], out telemetry.electrodeButtonIndex);
        }

        return true;
    }

    private static bool TryParseJsonLikePayload(string payload, out WeldSensorTelemetry telemetry)
    {
        telemetry = default;
        telemetry.timestampMs = ReadLong(payload, "timestampMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        telemetry.laserDistanceMm = ReadFloat(payload, "laserMm", ReadFloat(payload, "laserDistanceMm", 0f));
        telemetry.electrodeConsumedMm = ReadFloat(payload, "consumedMm", ReadFloat(payload, "electrodeConsumedMm", 0f));
        telemetry.servoNormalized = ReadFloat(payload, "servo01", ReadFloat(payload, "servoNormalized", 0f));
        telemetry.pitchDeg = ReadFloat(payload, "pitch", ReadFloat(payload, "pitchDeg", 0f));
        telemetry.rollDeg = ReadFloat(payload, "roll", ReadFloat(payload, "rollDeg", 0f));
        telemetry.yawDeg = ReadFloat(payload, "yaw", ReadFloat(payload, "yawDeg", 0f));
        telemetry.trackingConfidence = ReadFloat(payload, "trackingConfidence", 0f);
        telemetry.triggerPressed = ReadBool(payload, "active", ReadBool(payload, "triggerPressed", false));
        telemetry.electrodeButtonIndex = ReadInt(payload, "electrodeBtn", ReadInt(payload, "electrodeButtonIndex", 0));

        return telemetry.HasAnyData();
    }

    private static float ReadFloat(string payload, string key, float defaultValue)
    {
        return TryExtractValue(payload, key, out var raw) && TryParseFloat(raw, out var value)
            ? value
            : defaultValue;
    }

    private static int ReadInt(string payload, string key, int defaultValue)
    {
        return TryExtractValue(payload, key, out var raw) && TryParseInt(raw, out var value)
            ? value
            : defaultValue;
    }

    private static long ReadLong(string payload, string key, long defaultValue)
    {
        return TryExtractValue(payload, key, out var raw) && TryParseLong(raw, out var value)
            ? value
            : defaultValue;
    }

    private static bool ReadBool(string payload, string key, bool defaultValue)
    {
        return TryExtractValue(payload, key, out var raw)
            ? TryParseBool(raw)
            : defaultValue;
    }

    private static bool TryExtractValue(string payload, string key, out string value)
    {
        value = null;
        var keyToken = $"\"{key}\"";
        var keyIndex = payload.IndexOf(keyToken, StringComparison.OrdinalIgnoreCase);

        if (keyIndex < 0)
        {
            return false;
        }

        var colonIndex = payload.IndexOf(':', keyIndex + keyToken.Length);
        if (colonIndex < 0)
        {
            return false;
        }

        var start = colonIndex + 1;
        while (start < payload.Length && char.IsWhiteSpace(payload[start]))
        {
            start++;
        }

        var end = start;
        var isQuoted = start < payload.Length && payload[start] == '"';
        if (isQuoted)
        {
            start++;
            end = payload.IndexOf('"', start);
            if (end < 0)
            {
                return false;
            }
        }
        else
        {
            while (end < payload.Length && payload[end] != ',' && payload[end] != '}')
            {
                end++;
            }
        }

        value = payload.Substring(start, end - start).Trim();
        return !string.IsNullOrEmpty(value);
    }

    private static bool TryParseFloat(string raw, out float value)
    {
        return float.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseInt(string raw, out int value)
    {
        return int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseLong(string raw, out long value)
    {
        return long.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static bool TryParseBool(string raw)
    {
        raw = raw.Trim().Trim('"');

        if (bool.TryParse(raw, out var boolValue))
        {
            return boolValue;
        }

        return raw == "1";
    }

#if !UNITY_ANDROID || UNITY_EDITOR
    private sealed class SerialPortProxy
    {
        private static readonly Type SerialPortType = ResolveSerialPortType();

        private readonly object _instance;
        private readonly MethodInfo _openMethod;
        private readonly MethodInfo _closeMethod;
        private readonly MethodInfo _readLineMethod;
        private readonly MethodInfo _writeLineMethod;
        private readonly PropertyInfo _isOpenProperty;

        private SerialPortProxy(object instance)
        {
            _instance = instance;
            var type = instance.GetType();
            _openMethod     = type.GetMethod("Open",     BindingFlags.Instance | BindingFlags.Public);
            _closeMethod    = type.GetMethod("Close",    BindingFlags.Instance | BindingFlags.Public);
            _readLineMethod = type.GetMethod("ReadLine", BindingFlags.Instance | BindingFlags.Public);
            _writeLineMethod = type.GetMethod("WriteLine",
                BindingFlags.Instance | BindingFlags.Public, null,
                new[] { typeof(string) }, null);
            _isOpenProperty = type.GetProperty("IsOpen", BindingFlags.Instance | BindingFlags.Public);
        }

        public bool IsOpen => _isOpenProperty != null && (bool)_isOpenProperty.GetValue(_instance);

        public static string[] GetPortNames()
        {
            if (SerialPortType == null)
            {
                return Array.Empty<string>();
            }

            var method = SerialPortType.GetMethod("GetPortNames", BindingFlags.Static | BindingFlags.Public);
            return method?.Invoke(null, null) as string[] ?? Array.Empty<string>();
        }

        public static SerialPortProxy Open(string portName, int baudRate)
        {
            if (SerialPortType == null)
            {
                throw new InvalidOperationException("System.IO.Ports.SerialPort is not available in this Unity runtime.");
            }

            var instance = Activator.CreateInstance(SerialPortType, portName, baudRate);
            var proxy = new SerialPortProxy(instance);

            proxy.SetProperty("NewLine", "\n");
            proxy.SetProperty("ReadTimeout", 500);
            proxy.SetProperty("DtrEnable", false);
            proxy.SetProperty("RtsEnable", false);
            proxy._openMethod?.Invoke(proxy._instance, null);

            return proxy;
        }

        public string ReadLine()
        {
            return _readLineMethod?.Invoke(_instance, null) as string;
        }

        public void WriteLine(string line)
        {
            _writeLineMethod?.Invoke(_instance, new object[] { line });
        }

        public void Close()
        {
            _closeMethod?.Invoke(_instance, null);
        }

        private void SetProperty(string propertyName, object value)
        {
            var property = _instance.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            property?.SetValue(_instance, value);
        }

        private static Type ResolveSerialPortType()
        {
            return Type.GetType("System.IO.Ports.SerialPort, System.IO.Ports")
                ?? Type.GetType("System.IO.Ports.SerialPort, System");
        }
    }
#endif
}
