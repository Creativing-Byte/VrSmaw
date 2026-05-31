using TMPro;
using UnityEngine;

/// <summary>
/// Permanent corner HUD shown after onboarding.
///
/// Layout (two lines):
///   Line 1 — ● Arduino   ● Controlador    (green = OK, red/yellow = bad)
///   Line 2 — E6013 3.2mm · P2 – Unión en T  (live electrode + exercise)
///
/// Auto-finds all required references; safe to call in any script order.
/// </summary>
[DisallowMultipleComponent]
public class WeldingHudUI : MonoBehaviour
{
    [Header("UI")]
    [SerializeField] private TextMeshProUGUI connectionLine;
    [SerializeField] private TextMeshProUGUI selectionLine;

    [Header("Refresh")]
    [SerializeField] private float refreshIntervalSeconds = 0.5f;

    // ── Runtime refs ──────────────────────────────────────────────────────────
    private ArduinoBridgeReceiver  _bridge;
    private VRControllerWeldSensor _sensor;
    private WeldingEvaluator       _evaluator;
    private WeldingSelectionMenu   _selMenu;
    private float                  _timer;

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    private void Awake()
    {
        _bridge    = ArduinoBridgeReceiver.Instance ?? FindAnyObjectByType<ArduinoBridgeReceiver>();
        _sensor    = FindAnyObjectByType<VRControllerWeldSensor>();
        _evaluator = FindAnyObjectByType<WeldingEvaluator>();
        _selMenu   = FindAnyObjectByType<WeldingSelectionMenu>();
    }

    private void Update()
    {
        _timer += Time.deltaTime;
        if (_timer < refreshIntervalSeconds) return;
        _timer = 0f;
        Refresh();
    }

    // ── Refresh ───────────────────────────────────────────────────────────────

    private void Refresh()
    {
        // Lazy-find in case components spawned after Awake
        if (_bridge    == null) _bridge    = ArduinoBridgeReceiver.Instance ?? FindAnyObjectByType<ArduinoBridgeReceiver>();
        if (_sensor    == null) _sensor    = FindAnyObjectByType<VRControllerWeldSensor>();
        if (_evaluator == null) _evaluator = FindAnyObjectByType<WeldingEvaluator>();
        if (_selMenu   == null) _selMenu   = FindAnyObjectByType<WeldingSelectionMenu>();

        RefreshConnectionLine();
        RefreshSelectionLine();
    }

    private void RefreshConnectionLine()
    {
        if (connectionLine == null) return;

        bool arduinoOk = _bridge != null && _bridge.IsRunning && _bridge.TryGetLatest(out _);
        bool controlOk = _sensor != null && _sensor.IsTracking;

        var aColor = arduinoOk ? "#33DD55" : "#FF5555";
        var cColor = controlOk ? "#33DD55" : "#FFCC00";
        var aDot   = arduinoOk ? "●" : "○";
        var cDot   = controlOk ? "●" : "○";

        connectionLine.text =
            $"<color={aColor}>{aDot}</color> Arduino     " +
            $"<color={cColor}>{cDot}</color> Controlador";
    }

    private void RefreshSelectionLine()
    {
        if (selectionLine == null) return;

        // During an active session prefer the evaluator's live electrode
        string electrodeText = "—";
        string exerciseText  = "—";

        if (_evaluator != null && _evaluator.SessionActive && _evaluator.ActiveElectrode != null)
        {
            electrodeText = $"E6013 {_evaluator.ActiveElectrode.diameterMm:F1} mm";
        }
        else if (_selMenu != null)
        {
            electrodeText = $"E6013 {_selMenu.SelectedElectrodeLabel}";
        }

        if (_selMenu != null)
            exerciseText = _selMenu.SelectedExerciseLabel;

        selectionLine.text = $"<size=85%>{electrodeText}  ·  {exerciseText}</size>";
    }
}
