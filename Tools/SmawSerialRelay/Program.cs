using SmawSerialRelay;

// ── Parse arguments ───────────────────────────────────────────────────────────
// Usage: dotnet run -- [--port COM3] [--baud 115200] [--beacon-port 9101] [--telemetry-port 9100]

var config = new RelayConfig();

for (int i = 0; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "--port":           config.SerialPort          = args[++i]; break;
        case "--baud":           config.BaudRate            = int.Parse(args[++i]); break;
        case "--beacon-port":    config.BeaconListenPort    = int.Parse(args[++i]); break;
        case "--telemetry-port": config.TelemetryPort       = int.Parse(args[++i]); break;
        case "--timeout":        config.BeaconTimeoutSeconds= double.Parse(args[++i]); break;
    }
}

// ── Banner ────────────────────────────────────────────────────────────────────

Console.WriteLine("╔══════════════════════════════════════════════╗");
Console.WriteLine("║       SMAW Serial Relay  —  DigitalSmaw      ║");
Console.WriteLine("╚══════════════════════════════════════════════╝");
Console.WriteLine($"  Serial  : {(string.IsNullOrEmpty(config.SerialPort) ? "autodetectar" : config.SerialPort)} @ {config.BaudRate} baud");
Console.WriteLine($"  Beacon  : escuchando en UDP :{config.BeaconListenPort}");
Console.WriteLine($"  Telemetría: reenviar a Quest UDP :{config.TelemetryPort}");
Console.WriteLine("  Ctrl+C para salir");
Console.WriteLine();

// ── Components ────────────────────────────────────────────────────────────────

using var beacon   = new BeaconListener(config);
using var serial   = new SerialReader(config);
using var forwarder = new UdpForwarder();

// Wire beacon → forwarder
beacon.QuestDiscovered += (ip, port) =>
{
    forwarder.SetTarget(ip, port);
    Console.WriteLine($"\n[Beacon] Quest encontrada en {ip}:{port}  → iniciando reenvío\n");
};

beacon.QuestLost += () =>
{
    forwarder.ClearTarget();
    Console.WriteLine("\n[Beacon] Quest perdida — buscando...\n");
};

// Wire serial → forwarder
serial.LineReceived += line => forwarder.Forward(line);

// ── Start ─────────────────────────────────────────────────────────────────────

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

beacon.Start();
serial.Start();

// ── Status loop ───────────────────────────────────────────────────────────────

Console.WriteLine("[Relay] Iniciado. Buscando Quest en la red...");

try
{
    while (!cts.Token.IsCancellationRequested)
    {
        await Task.Delay(1000, cts.Token);
        PrintStatus(serial, beacon, forwarder);
    }
}
catch (OperationCanceledException)
{
    // Normal Ctrl+C
}

// ── Shutdown ──────────────────────────────────────────────────────────────────

Console.WriteLine("\n[Relay] Cerrando...");
serial.Stop();
beacon.Stop();
Console.WriteLine("[Relay] Listo. Hasta luego.");

// ── Helpers ───────────────────────────────────────────────────────────────────

static void PrintStatus(SerialReader serial, BeaconListener beacon, UdpForwarder forwarder)
{
    var serialStatus = serial.IsOpen
        ? $"✓ {serial.ActivePort}"
        : "✗ buscando...";

    var questStatus = beacon.IsConnected
        ? $"✓ {beacon.QuestIp}:{beacon.QuestPort}"
        : "✗ buscando beacon...";

    Console.Write($"\r  Serial: {serialStatus,-22}  Quest: {questStatus,-28}  Paquetes: {forwarder.PacketsSent,6}   ");
}
