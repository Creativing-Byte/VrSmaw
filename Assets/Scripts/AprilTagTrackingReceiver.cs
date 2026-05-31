using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

[DisallowMultipleComponent]
public class AprilTagTrackingReceiver : MonoBehaviour
{
    public enum MarkerPoseFrame
    {
        World,
        CameraLocal,
        XROriginLocal
    }

    [Serializable]
    public struct MarkerPoseSample
    {
        public long timestampMs;
        public int markerId;
        public Vector3 position;
        public Quaternion rotation;
        public float quality;
        public float markerSizeMeters;
        public MarkerPoseFrame frame;

        public bool IsValid()
        {
            return markerId >= 0 && rotation != default;
        }
    }

    [Header("Network")]
    [SerializeField]
    private bool autoStart = true;

    [SerializeField]
    private int listenPort = 9200;

    [SerializeField]
    private bool verboseLogging;

    [Header("Diagnostics")]
    [SerializeField]
    private string latestRawPayload;

    public static AprilTagTrackingReceiver Instance { get; private set; }

    private readonly object _lock = new object();
    private readonly Dictionary<int, MarkerPoseSample> _latestByMarker = new Dictionary<int, MarkerPoseSample>();
    private Thread _workerThread;
    private UdpClient _udpClient;
    private bool _keepRunning;
    private bool _hasPendingPayload;
    private string _pendingPayload;

    public event Action<MarkerPoseSample> MarkerPoseUpdated;

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

    private void Update()
    {
        string payload = null;

        lock (_lock)
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
        ProcessPayload(payload);
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

    public void Configure(int port, bool restartIfRunning = true)
    {
        if (listenPort == port)
        {
            return;
        }

        listenPort = port;
        if (restartIfRunning && _workerThread != null)
        {
            StopReceiver();
            StartReceiver();
        }
    }

    public void StartReceiver()
    {
        if (_workerThread != null)
        {
            return;
        }

        _keepRunning = true;
        _udpClient = new UdpClient(listenPort);
        _workerThread = new Thread(ReceiveLoop)
        {
            IsBackground = true,
            Name = "AprilTagTrackingReceiver"
        };
        _workerThread.Start();

        if (verboseLogging)
        {
            Debug.Log($"AprilTagTrackingReceiver listening on UDP {listenPort}");
        }
    }

    public void StopReceiver()
    {
        _keepRunning = false;

        try
        {
            _udpClient?.Close();
        }
        catch
        {
            // Ignore cleanup exceptions on shutdown.
        }

        _udpClient = null;

        if (_workerThread != null && _workerThread.IsAlive)
        {
            _workerThread.Join(200);
        }

        _workerThread = null;
    }

    public bool TryGetMarkerPose(int markerId, out MarkerPoseSample sample)
    {
        return _latestByMarker.TryGetValue(markerId, out sample);
    }

    public void SubmitMarkerPose(MarkerPoseSample sample)
    {
        if (sample.markerId < 0)
        {
            return;
        }

        _latestByMarker[sample.markerId] = sample;
        MarkerPoseUpdated?.Invoke(sample);
    }

    private void ReceiveLoop()
    {
        while (_keepRunning)
        {
            try
            {
                var remoteEndPoint = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
                var bytes = _udpClient.Receive(ref remoteEndPoint);
                var payload = Encoding.UTF8.GetString(bytes);

                lock (_lock)
                {
                    _pendingPayload = payload;
                    _hasPendingPayload = true;
                }
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
                    Debug.LogWarning($"AprilTagTrackingReceiver receive loop error: {ex.Message}");
                }

                Thread.Sleep(100);
            }
        }
    }

    private void ProcessPayload(string payload)
    {
        var lines = payload.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (!TryParsePayload(line, out var sample))
            {
                if (verboseLogging)
                {
                    Debug.LogWarning($"AprilTagTrackingReceiver could not parse payload: {line}");
                }

                continue;
            }

            _latestByMarker[sample.markerId] = sample;
            MarkerPoseUpdated?.Invoke(sample);
        }
    }

    private static bool TryParsePayload(string payload, out MarkerPoseSample sample)
    {
        payload = payload.Trim();

        if (payload.StartsWith("{", StringComparison.Ordinal))
        {
            return TryParseJsonLikePayload(payload, out sample);
        }

        return TryParseCsvPayload(payload, out sample);
    }

    private static bool TryParseCsvPayload(string payload, out MarkerPoseSample sample)
    {
        sample = default;

        var parts = payload.Split(',');
        if (parts.Length < 11)
        {
            return false;
        }

        if (!TryParseLong(parts[0], out sample.timestampMs))
        {
            sample.timestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        if (!TryParseInt(parts[1], out sample.markerId)) return false;
        if (!TryParseFloat(parts[2], out sample.position.x)) return false;
        if (!TryParseFloat(parts[3], out sample.position.y)) return false;
        if (!TryParseFloat(parts[4], out sample.position.z)) return false;
        if (!TryParseFloat(parts[5], out sample.rotation.x)) return false;
        if (!TryParseFloat(parts[6], out sample.rotation.y)) return false;
        if (!TryParseFloat(parts[7], out sample.rotation.z)) return false;
        if (!TryParseFloat(parts[8], out sample.rotation.w)) return false;
        if (!TryParseFloat(parts[9], out sample.quality)) return false;
        if (!TryParseFloat(parts[10], out sample.markerSizeMeters)) return false;

        sample.frame = MarkerPoseFrame.CameraLocal;
        if (parts.Length > 11)
        {
            sample.frame = ParseFrame(parts[11]);
        }

        return true;
    }

    private static bool TryParseJsonLikePayload(string payload, out MarkerPoseSample sample)
    {
        sample = default;
        sample.timestampMs = ReadLong(payload, "timestampMs", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        sample.markerId = ReadInt(payload, "id", ReadInt(payload, "markerId", -1));
        sample.position = new Vector3(
            ReadFloat(payload, "px", ReadFloat(payload, "x", 0f)),
            ReadFloat(payload, "py", ReadFloat(payload, "y", 0f)),
            ReadFloat(payload, "pz", ReadFloat(payload, "z", 0f)));
        sample.rotation = new Quaternion(
            ReadFloat(payload, "qx", 0f),
            ReadFloat(payload, "qy", 0f),
            ReadFloat(payload, "qz", 0f),
            ReadFloat(payload, "qw", 1f));
        sample.quality = ReadFloat(payload, "quality", 0f);
        sample.markerSizeMeters = ReadFloat(payload, "sizeM", ReadFloat(payload, "markerSizeMeters", 0f));
        sample.frame = ParseFrame(ReadString(payload, "frame", "CameraLocal"));

        return sample.markerId >= 0;
    }

    private static MarkerPoseFrame ParseFrame(string raw)
    {
        if (Enum.TryParse(raw.Trim().Trim('"'), true, out MarkerPoseFrame frame))
        {
            return frame;
        }

        return MarkerPoseFrame.CameraLocal;
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

    private static string ReadString(string payload, string key, string defaultValue)
    {
        return TryExtractValue(payload, key, out var raw) ? raw : defaultValue;
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
}
