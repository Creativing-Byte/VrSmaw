using System;
using System.Globalization;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

[DisallowMultipleComponent]
public class ArduinoTelemetryUdpForwarder : MonoBehaviour
{
    [Header("Source")]
    [SerializeField] private ArduinoBridgeReceiver sourceReceiver;

    [Header("Destination")]
    [SerializeField] private bool autoStart = true;
    [SerializeField] private string targetHost = "192.168.0.10";
    [SerializeField] private int targetPort = 9100;

    [Header("Behavior")]
    [SerializeField] private bool appendNewLine = true;
    [SerializeField] private bool verboseLogging;

    [Header("Diagnostics")]
    [SerializeField] private bool isRunning;
    [SerializeField] private int packetsSent;
    [SerializeField] private string lastPayload;
    [SerializeField] private string lastEndpoint;

    private UdpClient _udpClient;

    private void Awake()
    {
        if (sourceReceiver == null)
        {
            sourceReceiver = ArduinoBridgeReceiver.Instance ?? FindAnyObjectByType<ArduinoBridgeReceiver>();
        }
    }

    private void OnEnable()
    {
        if (sourceReceiver != null)
        {
            sourceReceiver.TelemetryUpdated += ForwardTelemetry;
        }

        if (autoStart)
        {
            StartForwarding();
        }
    }

    private void OnDisable()
    {
        if (sourceReceiver != null)
        {
            sourceReceiver.TelemetryUpdated -= ForwardTelemetry;
        }

        StopForwarding();
    }

    public void StartForwarding()
    {
        if (isRunning)
        {
            return;
        }

        _udpClient = new UdpClient();
        isRunning = true;
        lastEndpoint = $"{targetHost}:{targetPort}";

        if (verboseLogging)
        {
            Debug.Log($"ArduinoTelemetryUdpForwarder sending to {lastEndpoint}");
        }
    }

    public void StopForwarding()
    {
        isRunning = false;

        try
        {
            _udpClient?.Close();
        }
        catch
        {
            // Ignore cleanup exceptions.
        }

        _udpClient = null;
    }

    private void ForwardTelemetry(ArduinoBridgeReceiver.WeldSensorTelemetry telemetry)
    {
        if (!isRunning || _udpClient == null || string.IsNullOrWhiteSpace(targetHost))
        {
            return;
        }

        var payload = BuildCsvPayload(telemetry);
        if (appendNewLine)
        {
            payload += "\n";
        }

        var bytes = Encoding.UTF8.GetBytes(payload);
        _udpClient.Send(bytes, bytes.Length, targetHost, targetPort);

        packetsSent++;
        lastPayload = payload.TrimEnd('\n');
        lastEndpoint = $"{targetHost}:{targetPort}";
    }

    private static string BuildCsvPayload(ArduinoBridgeReceiver.WeldSensorTelemetry telemetry)
    {
        return string.Join(",",
            telemetry.timestampMs.ToString(CultureInfo.InvariantCulture),
            telemetry.laserDistanceMm.ToString("F2", CultureInfo.InvariantCulture),
            telemetry.electrodeConsumedMm.ToString("F2", CultureInfo.InvariantCulture),
            telemetry.servoNormalized.ToString("F3", CultureInfo.InvariantCulture),
            telemetry.pitchDeg.ToString("F2", CultureInfo.InvariantCulture),
            telemetry.rollDeg.ToString("F2", CultureInfo.InvariantCulture),
            telemetry.yawDeg.ToString("F2", CultureInfo.InvariantCulture),
            telemetry.triggerPressed ? "1" : "0",
            telemetry.trackingConfidence.ToString("F2", CultureInfo.InvariantCulture),
            telemetry.electrodeButtonIndex.ToString(CultureInfo.InvariantCulture));
    }
}
