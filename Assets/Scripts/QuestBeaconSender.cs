using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

/// <summary>
/// Broadcasts a UDP beacon every <see cref="intervalSeconds"/> seconds so the
/// SmawSerialRelay running on the PC can discover the Quest's IP automatically.
///
/// Beacon format: "SMAW_QUEST:{telemetryPort}"
/// Example:       "SMAW_QUEST:9100"
///
/// The relay reads the Quest IP from the UDP packet's RemoteEndPoint,
/// so the payload only needs to carry the port number.
///
/// Place this component on any persistent GameObject (e.g. Arduino Bridge).
/// </summary>
[DisallowMultipleComponent]
public sealed class QuestBeaconSender : MonoBehaviour
{
    [Header("Beacon settings")]
    [Tooltip("UDP port the relay listens on for beacon packets.")]
    [SerializeField] private int broadcastPort = 9101;

    [Tooltip("UDP port this device listens on for telemetry (must match ArduinoBridgeReceiver.listenPort).")]
    [SerializeField] private int telemetryPort = 9100;

    [Tooltip("Seconds between beacon broadcasts.")]
    [SerializeField] private float intervalSeconds = 2f;

    [Header("Diagnostics")]
    [SerializeField] private bool verboseLogging;
    [SerializeField] private int packetsSent;
    [SerializeField] private string lastPayload;

    private Thread _thread;
    private volatile bool _keepRunning;

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    private void OnEnable()
    {
        _keepRunning = true;
        _thread = new Thread(BroadcastLoop)
        {
            IsBackground = true,
            Name = "QuestBeaconSender"
        };
        _thread.Start();
    }

    private void OnDisable()
    {
        _keepRunning = false;
        _thread?.Join(500);
        _thread = null;
    }

    // ── Broadcast loop ────────────────────────────────────────────────────────

    private void BroadcastLoop()
    {
        var payload = Encoding.UTF8.GetBytes($"SMAW_QUEST:{telemetryPort}");
        lastPayload = $"SMAW_QUEST:{telemetryPort}";

        using var udp = new UdpClient();

        // Allow sending to broadcast address
        udp.EnableBroadcast = true;

        var endpoint = new IPEndPoint(IPAddress.Broadcast, broadcastPort);
        var sleepMs  = (int)(intervalSeconds * 1000f);

        while (_keepRunning)
        {
            try
            {
                udp.Send(payload, payload.Length, endpoint);
                packetsSent++;

                if (verboseLogging)
                    Debug.Log($"[QuestBeacon] Broadcast → 255.255.255.255:{broadcastPort}  payload={lastPayload}");
            }
            catch (Exception ex)
            {
                if (verboseLogging)
                    Debug.LogWarning($"[QuestBeacon] Error al enviar beacon: {ex.Message}");
            }

            Thread.Sleep(sleepMs);
        }
    }
}
