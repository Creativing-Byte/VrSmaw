namespace SmawSerialRelay;

/// <summary>
/// Configuration for the relay. All values can be overridden via
/// command-line arguments (see Program.cs for argument names).
/// </summary>
public sealed class RelayConfig
{
    /// <summary>Serial port name. Empty = autodetect.</summary>
    public string SerialPort { get; set; } = string.Empty;

    /// <summary>Baud rate for the Arduino serial connection.</summary>
    public int BaudRate { get; set; } = 115200;

    /// <summary>
    /// UDP port on which this relay listens for Quest beacon packets.
    /// Must match QuestBeaconSender.BroadcastPort in Unity.
    /// </summary>
    public int BeaconListenPort { get; set; } = 9101;

    /// <summary>
    /// UDP port on the Quest that receives telemetry.
    /// Must match ArduinoBridgeReceiver.listenPort in Unity.
    /// </summary>
    public int TelemetryPort { get; set; } = 9100;

    /// <summary>
    /// Seconds without a beacon before the relay considers the Quest lost
    /// and goes back to searching.
    /// </summary>
    public double BeaconTimeoutSeconds { get; set; } = 5.0;

    /// <summary>Hints used to autodetect the Arduino serial port.</summary>
    public string[] PortHints { get; set; } =
        ["Arduino", "usbmodem", "wchusbserial", "ttyACM", "ttyUSB", "COM"];
}
