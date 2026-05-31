using TMPro;
using UnityEngine;

/// <summary>
/// Displays real-time connection status for the Arduino bridge and the
/// VR controller sensor. Updates every <see cref="refreshIntervalSeconds"/>.
///
/// Assign <see cref="statusText"/> to any TMP label in the scene.
/// The component auto-locates ArduinoBridgeReceiver and VRControllerWeldSensor.
/// </summary>
[DisallowMultipleComponent]
public class WeldingSensorStatusUI : MonoBehaviour
{
    private static readonly Color ColorOk   = new Color(0.20f, 0.85f, 0.35f);
    private static readonly Color ColorWarn = new Color(1.00f, 0.70f, 0.10f);
    private static readonly Color ColorBad  = new Color(0.90f, 0.25f, 0.25f);

    [Header("UI")]
    [SerializeField] private TextMeshProUGUI statusText;

    [Header("Settings")]
    [SerializeField] private float refreshIntervalSeconds = 0.6f;

    private ArduinoBridgeReceiver _bridge;
    private VRControllerWeldSensor _sensor;
    private float _timer;

    private void Awake()
    {
        _bridge = ArduinoBridgeReceiver.Instance ?? FindAnyObjectByType<ArduinoBridgeReceiver>();
        _sensor = FindAnyObjectByType<VRControllerWeldSensor>();
    }

    private void Update()
    {
        _timer += Time.deltaTime;
        if (_timer < refreshIntervalSeconds) return;
        _timer = 0f;
        Refresh();
    }

    private void Refresh()
    {
        if (_bridge == null)
            _bridge = ArduinoBridgeReceiver.Instance ?? FindAnyObjectByType<ArduinoBridgeReceiver>();
        if (_sensor == null)
            _sensor = FindAnyObjectByType<VRControllerWeldSensor>();

        if (statusText == null) return;

        var arduinoOk  = _bridge != null && _bridge.IsRunning && _bridge.TryGetLatest(out _);
        var controlOk  = _sensor != null && _sensor.IsTracking;

        var aColor = arduinoOk ? "#33DD55" : "#FF5555";
        var cColor = controlOk ? "#33DD55" : "#FFCC00";
        var aDot   = arduinoOk ? "●" : "○";
        var cDot   = controlOk ? "●" : "○";

        statusText.text =
            $"<color={aColor}>{aDot}</color> Arduino   " +
            $"<color={cColor}>{cDot}</color> Controlador";
    }
}
