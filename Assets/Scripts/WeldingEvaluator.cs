using System;
using System.Text;
using UnityEngine;

/// <summary>
/// Evaluates the 11 welding criteria defined in WeldingEvaluationConfig.
/// Reads sensor data from ArduinoBridgeReceiver each frame while a session is active.
///
/// Criteria by exercise:
///   P1-U       → C1 (straightness), C2 (height uniformity)
///   P2-T       → C3 (45° work angle)
///   P3-Cuña    → C4 (13° wedge angle), C5 (uniformity), C6 (continuity pauses)
///   P4-V       → C7 (bead interruptions)
///   P5-Cilindro→ C8 (360° closure), C9 (curve angle), C10 (laser closure), C11 (arc control)
/// </summary>
[DisallowMultipleComponent]
public class WeldingEvaluator : MonoBehaviour
{
    public event Action SessionStarted;
    public event Action SessionEnded;

    // ── Types ─────────────────────────────────────────────────────────────────

    public enum ExerciseType
    {
        P1_U,
        P2_T,
        P3_Cuña,
        P4_V,
        P5_Cilindro
    }

    [Serializable]
    public class CriterionResult
    {
        public int    id;
        public string name;
        public bool   applicable;
        public float  score;      // 0 – 100
        public string details;
    }

    // ── Inspector ─────────────────────────────────────────────────────────────

    [Header("Config")]
    [SerializeField] private WeldingEvaluationConfig config;

    [SerializeField] private int selectedElectrodeIndex;

    [SerializeField] private ExerciseType selectedExercise = ExerciseType.P1_U;

    [Header("References")]
    [SerializeField] private ArduinoBridgeReceiver bridge;

    [Tooltip("Optional VR controller mounted on the clamp. When tracking, its pitch/roll/yaw "
           + "override the Arduino IMU fields for all angle-based criteria (C1-C4, C8, C9).")]
    [SerializeField] private VRControllerWeldSensor controllerSensor;

    [Tooltip("Bead renderer on the scene — used for geometric bead analysis (C12–C14).")]
    [SerializeField] private WeldBeadRenderer beadRenderer;

    [Tooltip("Target line component on the weldable piece — provides seam segments for C12–C14.")]
    [SerializeField] private WeldTargetLine targetLine;

    [Header("Session State (read-only)")]
    [SerializeField] private bool  sessionActive;
    [SerializeField] private float sessionElapsedSeconds;
    [SerializeField] private float overallScore = 100f;
    [SerializeField] private CriterionResult[] criteriaResults;

    // ── Private tracking ─────────────────────────────────────────────────────

    private bool  _arcWasActive;
    private float _arcGapTimerMs;    // time (ms) the arc has been continuously OFF
    
    // Initialisation flags
    private bool  _initialYawSet;
    private float _initialYaw;

    private bool  _prevYawSet;
    private float _prevYaw;
    private float _cumulativeYaw;    // for C8 (360° bead)

    private bool  _initialLaserSet;
    private float _initialLaserMm;
    private float _lastLaserMm;

    // Frame counters
    private int   _totalArcFrames;

    // C1 – straightness
    private int _c1ViolFrames;

    // C2 / C5 – laser uniformity (shared mean tracking)
    private double _laserSum;
    private int    _laserSamples;
    private int    _c2ViolFrames;
    private int    _c5ViolFrames;

    // C3 – 45° angle
    private int _c3ViolFrames;

    // C4 – 13° angle
    private int _c4ViolFrames;

    // C6 / C7 – pauses & interruptions (same detection)
    private int _pauseCount;         // each pause > threshold counted once at arc restart

    // C8 – tracked via _cumulativeYaw
    // C9 – pitch range
    private float _pitchMin;
    private float _pitchMax;

    // C11 – arc stability: frames where distance is within the inner 50 % of the valid range
    private int _c11StableFrames;
    private float _arcActiveSeconds;
    private float _continuousInactiveSeconds;
    private bool _hasArcWork;
    private string _lastSessionEndReason = "Pendiente";

    // ── Public accessors ─────────────────────────────────────────────────────

    public bool  SessionActive   => sessionActive;
    public float OverallScore    => overallScore;
    public int   ElectrodeIndex  => selectedElectrodeIndex;
    public ExerciseType Exercise          => selectedExercise;
    public float        SessionElapsedSeconds => sessionElapsedSeconds;
    public float        ArcActiveSeconds => _arcActiveSeconds;
    public float        CurrentInactiveSeconds => _continuousInactiveSeconds;
    public bool         HasArcWork => _hasArcWork;
    public string       LastSessionEndReason => _lastSessionEndReason;
    public WeldingEvaluationConfig.ElectrodeProfile ActiveElectrode =>
        config?.GetElectrode(selectedElectrodeIndex);

    public CriterionResult GetCriterion(int oneBased) =>
        (oneBased >= 1 && oneBased <= criteriaResults.Length) ? criteriaResults[oneBased - 1] : null;

    /// <summary>Total number of criteria slots (including exercise-specific ones).</summary>
    public int CriteriaCount => criteriaResults?.Length ?? 0;

    // ── Unity lifecycle ───────────────────────────────────────────────────────

private void Awake()
    {
        EnsureInitialized();
        EnsureBridge();
        EnsureControllerSensor();
    }

private void Update()
    {
        if (!sessionActive) return;

        EnsureBridge();
        if (config == null) return;

        sessionElapsedSeconds += Time.deltaTime;

        var e = config.GetElectrode(selectedElectrodeIndex);
        if (e == null) return;

        // ── Arduino path ─────────────────────────────────────────────────────
        if (bridge != null && bridge.TryGetLatest(out var t))
        {
            // ── Override Arduino IMU with VR controller sensor when available ─
            if (controllerSensor != null && controllerSensor.IsTracking)
            {
                t.pitchDeg = controllerSensor.PitchDeg;
                t.rollDeg  = controllerSensor.RollDeg;
                t.yawDeg   = controllerSensor.YawDeg;
            }

            var arcActive = t.laserDistanceMm >= e.arcMinMm && t.laserDistanceMm <= e.arcMaxMm;

            // ── Haptic feedback on arc state changes ──────────────────────────
            if (controllerSensor != null)
            {
                if (arcActive && !_arcWasActive)
                {
                    controllerSensor.ResetStableArcTimer();
                    controllerSensor.TriggerArcEntryHaptic();
                }
                else if (!arcActive && _arcWasActive)
                {
                    controllerSensor.TriggerArcExitHaptic();
                }

                if (arcActive)
                    controllerSensor.TickStableArcHaptic();
                else
                    controllerSensor.ResetStableArcTimer();
            }

            AccumulateArcTime(arcActive);
            EvaluateFrame(in t, arcActive, e);
            if (arcActive && selectedExercise == ExerciseType.P2_T && targetLine != null)
                TrackSeamVelocityProgress(GetCurrentTipWorldPos());
            _arcWasActive = arcActive;
            UpdateOverallScore(live: true, e);
        }
        else
        {
            // ── Simulated path (no Arduino) ───────────────────────────────────
            // Derive arc state from MigWelding's physics-based detection so that
            // _arcActiveSeconds, GetGuidedCompletion01(), and the auto-end logic
            // all work correctly during simulation / training without hardware.
            if (_migWeldingCache == null)
                _migWeldingCache = FindAnyObjectByType<MigWelding>();

            bool simArc = _migWeldingCache != null && _migWeldingCache.ArcIsValid;

            // Synthesise telemetry from the VR controller sensor so EvaluateFrame
            // can score angle-based criteria (C3) even without Arduino hardware.
            // If no sensor is tracking, use neutral angle values so angle criteria
            // are not penalised — only bead-geometry criteria will count.
            var synth = new ArduinoBridgeReceiver.WeldSensorTelemetry
            {
                pitchDeg = (controllerSensor != null && controllerSensor.IsTracking)
                           ? controllerSensor.PitchDeg
                           : config.workAngle45Deg,   // neutral: won't penalise C3
                rollDeg  = (controllerSensor != null && controllerSensor.IsTracking)
                           ? controllerSensor.RollDeg
                           : 0f,
                yawDeg   = (controllerSensor != null && controllerSensor.IsTracking)
                           ? controllerSensor.YawDeg
                           : (_prevYawSet ? _prevYaw : 0f),
                // laserDistanceMm drives arcActive in EvaluateFrame's pause logic.
                // Set to mid of valid range when arc is on, 0 (= outside range) when off.
                laserDistanceMm = simArc
                                  ? (e.arcMinMm + e.arcMaxMm) * 0.5f
                                  : 0f,
            };

            AccumulateArcTime(simArc);
            EvaluateFrame(in synth, simArc, e);
            if (simArc && selectedExercise == ExerciseType.P2_T && targetLine != null)
                TrackSeamVelocityProgress(GetCurrentTipWorldPos());
            _arcWasActive = simArc;
            UpdateOverallScore(live: true, e);
        }
    }

    // Shared arc-time accumulation used by both Arduino and simulated paths.
    private void AccumulateArcTime(bool arcActive)
    {
        if (arcActive)
        {
            _arcActiveSeconds          += Time.deltaTime;
            _continuousInactiveSeconds  = 0f;
            _hasArcWork                 = true;
        }
        else if (_hasArcWork)
        {
            _continuousInactiveSeconds += Time.deltaTime;
        }
    }

    // Cached MigWelding reference for the simulated path.
    private MigWelding _migWeldingCache;

    // ── Public API ────────────────────────────────────────────────────────────

    public void SelectElectrode(int index)
    {
        EnsureInitialized();
        if (config == null) return;
        selectedElectrodeIndex = Mathf.Clamp(index, 0, config.electrodes.Length - 1);
    }

    public void SelectExercise(ExerciseType exercise)
    {
        EnsureInitialized();
        selectedExercise = exercise;
        SetApplicableCriteria();
    }

public void BeginSession()
    {
        EnsureInitialized();
        EnsureControllerSensor();
        ResetTracking();
        SetApplicableCriteria();
        sessionActive = true;
        _lastSessionEndReason = "En progreso";

        // Clear leftover bead geometry from the previous attempt
        beadRenderer?.ClearBead();

        // Auto-calibrate the controller sensor so the welder's current
        // holding position becomes the zero reference for this session.
        if (controllerSensor != null && controllerSensor.CalibrateOnSessionStart)
            controllerSensor.Calibrate();

        SessionStarted?.Invoke();
    }

    public void EndSession()
    {
        EndSession("Sesión finalizada.");
    }

    public void EndSession(string reason)
    {
        EnsureInitialized();
        sessionActive = false;
        _lastSessionEndReason = string.IsNullOrWhiteSpace(reason)
            ? "Sesión finalizada."
            : reason;

        if (config != null)
        {
            FinalizeScores(config.GetElectrode(selectedElectrodeIndex));
            UpdateOverallScore(live: false, config.GetElectrode(selectedElectrodeIndex));
        }

        SessionEnded?.Invoke();
    }

    /// <summary>Human-readable session report.</summary>
    public string GetReportText()
    {
        var e = config?.GetElectrode(selectedElectrodeIndex);
        var sb = new StringBuilder();
        sb.AppendLine($"ELECTRODO : {e?.code ?? "—"}  |  EJERCICIO : {selectedExercise}");
        sb.AppendLine($"PUNTUACION: {overallScore:F1} %   TIEMPO: {sessionElapsedSeconds:F1} s");
        sb.AppendLine(new string('-', 52));

        foreach (var c in criteriaResults)
        {
            if (!c.applicable) continue;
            var mark = c.score >= 60f ? "✓" : "✗";
            sb.AppendLine($"  [{mark}] #{c.id,2}  {c.name,-30} {c.score,5:F0}%");
            sb.AppendLine($"         {c.details}");
        }

        return sb.ToString();
    }

    public float GetGuidedCompletion01()
    {
        var electrode = ActiveElectrode;
        if (electrode == null) return 0f;

        // P2-T: progress = velocity-gated bin coverage of target seams.
        // A bin is only marked when the electrode moves at deliberate welding speed
        // (1.5 – 25 mm/s) along the seam.  Hovering (0 mm/s) and VR tremor
        // (>>25 mm/s) are both rejected, so only real forward progress counts.
        if (selectedExercise == ExerciseType.P2_T && targetLine != null)
        {
            var seams = targetLine.Seams;
            if (seams == null || seams.Count == 0) return 0f;
            if (_seamBinWelded == null)             return 0f;
            float total = 0f;
            for (int i = 0; i < seams.Count; i++)
                total += GetSeamWeldCoverage(i);
            return total / seams.Count;
        }

        var targetArcSeconds = GetGuidedTargetArcSeconds(electrode);
        var arcProgress = targetArcSeconds > 0.01f
            ? Mathf.Clamp01(_arcActiveSeconds / targetArcSeconds)
            : 0f;

        if (selectedExercise != ExerciseType.P5_Cilindro)
            return arcProgress;

        var yawProgress = Mathf.Clamp01(Mathf.Abs(_cumulativeYaw) / 360f);
        var closureProgress = 0f;
        if (_initialLaserSet)
        {
            var threshold = Mathf.Max(0.01f, electrode.continuityClosureThresholdMm * 1.5f);
            var diff = Mathf.Abs(_lastLaserMm - _initialLaserMm);
            closureProgress = 1f - Mathf.Clamp01(diff / threshold);
        }

        return Mathf.Clamp01(arcProgress * 0.40f + yawProgress * 0.50f + closureProgress * 0.10f);
    }

    /// <summary>Human-readable label for what GetGuidedCompletion01 represents.</summary>
    public string GetProgressLabel()
    {
        return selectedExercise == ExerciseType.P2_T ? "Cobertura" : "Progreso";
    }

    public bool TryGetGuidedAutoEnd(out string reason)
    {
        reason = null;

        if (!sessionActive || config == null)
            return false;

        var electrode = ActiveElectrode;
        if (electrode == null)
            return false;

        if (sessionElapsedSeconds >= GetGuidedMaxSessionSeconds(electrode))
        {
            reason = "Tiempo máximo alcanzado para este ejercicio.";
            return true;
        }

        if (!_hasArcWork)
            return false;

        // 90 % threshold: 18 of 20 seam bins covered on each fillet (average).
        // Asking for 99.5 % with discrete bins is impractical — the student would
        // have to graze the last millimetre of every section.
        if (GetGuidedCompletion01() < 0.90f)
            return false;

        if (_continuousInactiveSeconds < GetGuidedCompletionIdleSeconds())
            return false;

        reason = selectedExercise == ExerciseType.P5_Cilindro
            ? "Recorrido completo detectado en el cilindro."
            : "Cordón completado. Avanzando al siguiente ejercicio.";
        return true;
    }

    // ── Per-frame evaluation ──────────────────────────────────────────────────

    private void EvaluateFrame(
        in ArduinoBridgeReceiver.WeldSensorTelemetry t,
        bool arcActive,
        WeldingEvaluationConfig.ElectrodeProfile e)
    {
        // ── Gap / pause tracking ─────────────────────────────────────────────
        // Arc just turned OFF
        if (!arcActive && _arcWasActive)
        {
            _arcGapTimerMs = 0f;
        }

        // Arc is OFF, accumulate gap
        if (!arcActive)
        {
            _arcGapTimerMs += Time.deltaTime * 1000f;
            return; // nothing more to do when not welding
        }

        // Arc just turned ON (restart after a gap)
        // For P2_T: ignore pauses longer than 8 s — that duration means the student
        // was rotating the piece between front and reverso passes, which is correct
        // technique and must not be counted as a continuity failure.
        //
        // Guard: _totalArcFrames == 0 means this is the VERY FIRST arc activation
        // of the session.  The gap timer has been accumulating since session start
        // (student navigating to the piece, positioning, etc.) — that pre-welding
        // time must NOT be penalised as a continuity pause.
        if (arcActive && !_arcWasActive && _arcGapTimerMs >= config.continuityPauseThresholdMs
            && _totalArcFrames > 0)
        {
            bool isRotationPause = selectedExercise == ExerciseType.P2_T
                                && _arcGapTimerMs > 8000f;
            if (!isRotationPause)
                _pauseCount++;
        }

        // ── Arc-active processing ────────────────────────────────────────────
        _totalArcFrames++;

        // Initialise reference values on first arc frame
        if (!_initialYawSet)   { _initialYaw   = t.yawDeg;            _initialYawSet   = true; }
        if (!_initialLaserSet) { _initialLaserMm = t.laserDistanceMm; _initialLaserSet = true; }

        _lastLaserMm = t.laserDistanceMm;

        // Cumulative yaw (C8)
        if (_prevYawSet)
            _cumulativeYaw += Mathf.DeltaAngle(_prevYaw, t.yawDeg);
        _prevYaw    = t.yawDeg;
        _prevYawSet = true;

        // ── C1: Rectitud del cordón ──────────────────────────────────────────
        if (criteriaResults[0].applicable)
        {
            if (Mathf.Abs(Mathf.DeltaAngle(_initialYaw, t.yawDeg)) > config.straightnessMaxDeviationDeg)
                _c1ViolFrames++;
        }

        // ── C2 / C5: Laser uniformity (running mean) ─────────────────────────
        _laserSum += t.laserDistanceMm;
        _laserSamples++;
        if (_laserSamples > 1)
        {
            var mean    = (float)(_laserSum / _laserSamples);
            var devMm   = Mathf.Abs(t.laserDistanceMm - mean);
            if (devMm > e.uniformityToleranceMm)
            {
                if (criteriaResults[1].applicable) _c2ViolFrames++;
                if (criteriaResults[4].applicable) _c5ViolFrames++;
            }
        }

        // ── C3: Ángulo trabajo 45° ───────────────────────────────────────────
        // VRControllerWeldSensor reports pitch RELATIVE to the calibrated zero pose
        // (= 0° when holding the exact position at calibration time).
        // The student calibrates at the correct 45° work angle, so "good" = stay near 0°.
        //
        // Arduino IMU reports ABSOLUTE angle, so we compare against config.workAngle45Deg.
        if (criteriaResults[2].applicable)
        {
            float c3Target = (controllerSensor != null && controllerSensor.IsTracking)
                             ? 0f                       // VR: deviation from calibrated pose
                             : config.workAngle45Deg;   // Arduino IMU: absolute 45°
            if (Mathf.Abs(t.pitchDeg - c3Target) > config.workAngle45ToleranceDeg)
                _c3ViolFrames++;
        }

        // ── C4: Adaptación ángulo 13° ────────────────────────────────────────
        if (criteriaResults[3].applicable)
        {
            if (Mathf.Abs(t.pitchDeg - config.wedgeAngleDeg) > config.wedgeAngleToleranceDeg)
                _c4ViolFrames++;
        }

        // ── C9: Pitch range on curve ─────────────────────────────────────────
        if (criteriaResults[8].applicable)
        {
            if (t.pitchDeg < _pitchMin) _pitchMin = t.pitchDeg;
            if (t.pitchDeg > _pitchMax) _pitchMax = t.pitchDeg;
        }

        // ── C11: Arc stability (distance within inner 80 % of valid range) ─────
        // Uses ±40 % of the half-band so the student has a generous "good zone"
        // while still penalising holding far outside the arc window.
        if (criteriaResults[10].applicable)
        {
            var midMm    = (e.arcMinMm + e.arcMaxMm) * 0.5f;
            var halfBand = (e.arcMaxMm - e.arcMinMm) * 0.40f; // ±40 % of total band
            if (Mathf.Abs(t.laserDistanceMm - midMm) <= halfBand)
                _c11StableFrames++;
        }
    }

    // ── Final score computation ───────────────────────────────────────────────

    private void FinalizeScores(WeldingEvaluationConfig.ElectrodeProfile e)
    {
        if (_totalArcFrames == 0 || e == null) return;

        float violRate;

        // C1 – Straightness
        var c1 = criteriaResults[0];
        if (c1.applicable)
        {
            violRate   = (float)_c1ViolFrames / _totalArcFrames;
            c1.score   = 100f * (1f - violRate);
            c1.details = $"Muestras fuera de ±{config.straightnessMaxDeviationDeg}°: {_c1ViolFrames}/{_totalArcFrames}";
        }

        // C2 – Uniformidad altura
        var c2 = criteriaResults[1];
        if (c2.applicable)
        {
            violRate   = (float)_c2ViolFrames / _totalArcFrames;
            c2.score   = 100f * (1f - violRate);
            c2.details = $"Muestras fuera de ±{e.uniformityToleranceMm} mm: {_c2ViolFrames}/{_totalArcFrames}";
        }

        // C3 – Ángulo trabajo 45°
        var c3 = criteriaResults[2];
        if (c3.applicable)
        {
            violRate = (float)_c3ViolFrames / _totalArcFrames;
            c3.score = 100f * (1f - violRate);
            bool usingController = controllerSensor != null && controllerSensor.IsTracking;
            c3.details = usingController
                ? $"Muestras fuera de posición calibrada ±{config.workAngle45ToleranceDeg}°: {_c3ViolFrames}/{_totalArcFrames}"
                : $"Muestras fuera de {config.workAngle45Deg}° ±{config.workAngle45ToleranceDeg}°: {_c3ViolFrames}/{_totalArcFrames}";
        }

        // C4 – Adaptación 13°
        var c4 = criteriaResults[3];
        if (c4.applicable)
        {
            violRate   = (float)_c4ViolFrames / _totalArcFrames;
            c4.score   = 100f * (1f - violRate);
            c4.details = $"Muestras fuera de {config.wedgeAngleDeg}° ±{config.wedgeAngleToleranceDeg}°: {_c4ViolFrames}/{_totalArcFrames}";
        }

        // C5 – Uniformidad cuña
        var c5 = criteriaResults[4];
        if (c5.applicable)
        {
            violRate   = (float)_c5ViolFrames / _totalArcFrames;
            c5.score   = 100f * (1f - violRate);
            c5.details = $"Muestras fuera de ±{e.uniformityToleranceMm} mm: {_c5ViolFrames}/{_totalArcFrames}";
        }

        // C6 – Continuidad (pauses)
        var c6 = criteriaResults[5];
        if (c6.applicable)
        {
            c6.score   = _pauseCount == 0 ? 100f : Mathf.Max(0f, 100f - _pauseCount * config.c6PenaltyPerPause);
            c6.details = $"Pausas > {config.continuityPauseThresholdMs:F0} ms: {_pauseCount}";
        }

        // C7 – Interruptions
        var c7 = criteriaResults[6];
        if (c7.applicable)
        {
            var excess = Mathf.Max(0, _pauseCount - config.maxInterruptionsBeforeFail);
            c7.score   = excess == 0 ? 100f : Mathf.Max(0f, 100f - excess * config.c7PenaltyPerInterruption);
            c7.details = $"Interrupciones: {_pauseCount} (límite: {config.maxInterruptionsBeforeFail})";
        }

        // C8 – Cordón circunferencial 360°
        var c8 = criteriaResults[7];
        if (c8.applicable)
        {
            var closureErr = Mathf.Abs(Mathf.Abs(_cumulativeYaw) - 360f);
            c8.score   = closureErr <= config.circumferentialClosureToleranceDeg
                ? 100f
                : Mathf.Max(0f, 100f - (closureErr - config.circumferentialClosureToleranceDeg) * 5f);
            c8.details = $"Rotación acumulada: {_cumulativeYaw:F1}° | Error cierre: {closureErr:F1}° (lím ±{config.circumferentialClosureToleranceDeg}°)";
        }

        // C9 – Ángulo curva
        var c9 = criteriaResults[8];
        if (c9.applicable && _pitchMin < float.MaxValue)
        {
            var variation   = _pitchMax - _pitchMin;
            var limit       = config.curveAngleVariationMaxDeg * 2f;
            c9.score   = variation <= limit
                ? 100f
                : Mathf.Max(0f, 100f - (variation - limit) * 5f);
            c9.details = $"Variación pitch: {variation:F1}° (lím ±{config.curveAngleVariationMaxDeg}°)";
        }

        // C10 – Continuidad y cierre (laser start vs end)
        var c10 = criteriaResults[9];
        if (c10.applicable && _initialLaserSet)
        {
            var diff   = Mathf.Abs(_lastLaserMm - _initialLaserMm);
            c10.score  = diff <= e.continuityClosureThresholdMm
                ? 100f
                : Mathf.Max(0f, 100f - (diff - e.continuityClosureThresholdMm) * 20f);
            c10.details = $"Diferencia inicio/fin: {diff:F1} mm (lím {e.continuityClosureThresholdMm} mm)";
        }

        // C11 – Control del arco
        var c11 = criteriaResults[10];
        if (c11.applicable)
        {
            c11.score   = 100f * (float)_c11StableFrames / _totalArcFrames;
            c11.details = $"Arco estable (centro ±40% rango): {_c11StableFrames}/{_totalArcFrames} ({c11.score:F0}%)";
        }

        // ── P2-T fillet-specific bead geometry criteria ───────────────────────
        if (selectedExercise == ExerciseType.P2_T
            && beadRenderer != null && targetLine != null && config != null)
        {
            var pts  = beadRenderer.AllWorldPoints;
            var seams = targetLine.Seams;

            // C1 override – geometric bead straightness replaces the yaw-deviation method
            var c1g = criteriaResults[0];
            if (c1g.applicable)
            {
                if (pts.Count >= 2)
                {
                    float devM = ComputeStraightnessDeviation(pts);
                    c1g.score   = ScoreLinear(devM, config.beadStraightnessPerfectM, config.beadStraightnessFailM);
                    c1g.details = $"Desviación máx de línea recta: {devM * 1000f:F1} mm";
                }
                else
                {
                    c1g.score   = 0f;
                    c1g.details = "Sin datos de cordón";
                }
            }

            // C12 – Posición del cordón (average proximity to target seam)
            var c12 = criteriaResults[11];
            if (c12.applicable)
            {
                if (pts.Count >= 1)
                {
                    float avgDev = ComputeAvgSeamDeviation(pts, seams);
                    c12.score   = ScoreLinear(avgDev, config.beadPositionPerfectM, config.beadPositionFailM);
                    c12.details = $"Desv. media del cordón al seam: {avgDev * 1000f:F1} mm";
                }
                else
                {
                    c12.score   = 0f;
                    c12.details = "Sin datos de cordón";
                }
            }

            // C13 – Cobertura del cordón (velocity-gated bin coverage)
            var c13 = criteriaResults[12];
            if (c13.applicable)
            {
                float cov = 0f;
                if (seams != null && _seamBinWelded != null)
                {
                    for (int si = 0; si < seams.Count; si++) cov += GetSeamWeldCoverage(si);
                    cov /= Mathf.Max(1, seams.Count);
                }
                c13.score   = ScoreLinear(1f - cov,
                                          1f - config.beadCoverageFullFraction,
                                          1f - config.beadCoverageMinFraction);
                c13.details = $"Cobertura: {cov * 100f:F0}% del seam";
            }

            // C14 – Velocidad de avance (travel speed, estimated from coverage)
            var c14 = criteriaResults[13];
            if (c14.applicable)
            {
                if (_arcActiveSeconds > 0.1f && _seamBinWelded != null && seams != null)
                {
                    float cov         = 0f;
                    for (int si = 0; si < seams.Count; si++) cov += GetSeamWeldCoverage(si);
                    cov /= Mathf.Max(1, seams.Count);
                    float seamLenM    = GetBestSeamLengthM(seams);
                    float speedMmPerS = (cov * seamLenM * 1000f) / _arcActiveSeconds;
                    float error       = Mathf.Max(0f,
                                           Mathf.Abs(speedMmPerS - config.travelSpeedIdealMmPerSec)
                                           - config.travelSpeedToleranceMmPerSec);
                    c14.score   = ScoreLinear(error, 0f,
                                              config.travelSpeedFailMmPerSec
                                              - config.travelSpeedToleranceMmPerSec);
                    c14.details = $"Vel. avance: {speedMmPerS:F1} mm/s " +
                                  $"(ideal {config.travelSpeedIdealMmPerSec:F1}±{config.travelSpeedToleranceMmPerSec:F1})";
                }
                else
                {
                    c14.score   = 0f;
                    c14.details = "Sin datos suficientes";
                }
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void UpdateOverallScore(bool live, WeldingEvaluationConfig.ElectrodeProfile e)
    {
        if (!live) { ComputeAverage(); return; }

        // During the session, derive a lightweight live score from C2/C5/C11
        // (the criteria that can be meaningfully approximated in real time).
        // Full accuracy is only guaranteed after EndSession().
        if (_totalArcFrames == 0) { overallScore = 100f; return; }

        ComputeAverage();
    }

    private void ComputeAverage()
    {
        float sum  = 0f;
        int   count = 0;
        foreach (var c in criteriaResults)
        {
            if (!c.applicable) continue;
            sum += c.score;
            count++;
        }
        overallScore = count > 0 ? sum / count : 100f;
    }

    private void SetApplicableCriteria()
    {
        foreach (var c in criteriaResults) c.applicable = false;

        switch (selectedExercise)
        {
            case ExerciseType.P1_U:
                criteriaResults[0].applicable = true;
                criteriaResults[1].applicable = true;
                break;
            case ExerciseType.P2_T:
                // Only three criteria matter for the T-joint double-fillet:
                //   C3  – Ángulo de trabajo 45°   (technique)
                //   C6  – Continuidad              (arc pauses within each pass)
                //   C13 – Cobertura del cordón     (amount of seam covered)
                //
                // Distance (C11/C12), travel speed (C14), and geometric straightness (C1)
                // are not scored because VR controller precision makes them unreliable.
                // C6 is filtered below to ignore the long pause while rotating the piece.
                criteriaResults[2].applicable  = true;  // C3  Ángulo de trabajo 45°
                criteriaResults[5].applicable  = true;  // C6  Continuidad (pausas cortas)
                criteriaResults[12].applicable = true;  // C13 Cobertura del cordón
                break;
            case ExerciseType.P3_Cuña:
                criteriaResults[3].applicable = true;
                criteriaResults[4].applicable = true;
                criteriaResults[5].applicable = true;
                break;
            case ExerciseType.P4_V:
                criteriaResults[6].applicable = true;
                break;
            case ExerciseType.P5_Cilindro:
                criteriaResults[7].applicable  = true;
                criteriaResults[8].applicable  = true;
                criteriaResults[9].applicable  = true;
                criteriaResults[10].applicable = true;
                break;
        }
    }

    private void ResetTracking()
    {
        sessionElapsedSeconds = 0f;
        overallScore          = 100f;
        _lastSessionEndReason = "En progreso";

        _arcWasActive    = false;
        _arcGapTimerMs   = 0f;
        _initialYawSet   = false;
        _initialYaw      = 0f;
        _prevYawSet      = false;
        _prevYaw         = 0f;
        _cumulativeYaw   = 0f;
        _initialLaserSet = false;
        _initialLaserMm  = 0f;
        _lastLaserMm     = 0f;
        _totalArcFrames  = 0;

        _c1ViolFrames   = 0;
        _laserSum       = 0.0;
        _laserSamples   = 0;
        _c2ViolFrames   = 0;
        _c3ViolFrames   = 0;
        _c4ViolFrames   = 0;
        _c5ViolFrames   = 0;
        _pauseCount     = 0;
        _pitchMin       = float.MaxValue;
        _pitchMax       = float.MinValue;
        _c11StableFrames = 0;
        _arcActiveSeconds = 0f;
        _continuousInactiveSeconds = 0f;
        _hasArcWork = false;

        // Reset P2-T velocity-gated coverage tracking
        _seamBinWelded      = null;
        _seamWeldSeamCount  = 0;
        _velTrackLastPosSet = false;
        _smoothedVelMmS     = 0f;

        foreach (var c in criteriaResults)
        {
            c.score   = 100f;
            c.details = "Pendiente";
        }
    }

    private void InitCriteriaResults()
    {
        var names = new[]
        {
            "Rectitud del cordón",        // 1
            "Uniformidad (altura)",        // 2
            "Ángulo de trabajo 45°",       // 3
            "Adaptación ángulo 13°",       // 4
            "Uniformidad cuña",            // 5
            "Continuidad cuña",            // 6
            "Continuidad del cordón",      // 7
            "Cordón circunferencial 360°", // 8
            "Ángulo de trabajo en curva",  // 9
            "Continuidad y cierre",        // 10
            "Control del arco",            // 11
            "Posición del cordón",         // 12
            "Cobertura del cordón",        // 13
            "Velocidad de avance",         // 14
        };

        criteriaResults = new CriterionResult[14];
        for (int i = 0; i < 14; i++)
        {
            criteriaResults[i] = new CriterionResult
            {
                id      = i + 1,
                name    = names[i],
                score   = 100f,
                details = "Pendiente"
            };
        }
    }

private void EnsureBridge()
    {
        if (bridge != null) return;
        bridge = ArduinoBridgeReceiver.Instance ?? FindAnyObjectByType<ArduinoBridgeReceiver>();
    }

    private void EnsureControllerSensor()
    {
        if (controllerSensor != null) return;
        controllerSensor = FindAnyObjectByType<VRControllerWeldSensor>();
    }

    private void EnsureInitialized()
    {
        if (criteriaResults == null || criteriaResults.Length != 14)
            InitCriteriaResults();
    }

    private float GetGuidedTargetArcSeconds(WeldingEvaluationConfig.ElectrodeProfile electrode)
    {
        if (electrode == null)
            return 0f;

        float ratio;
        float minSeconds;
        float maxSeconds;

        switch (selectedExercise)
        {
            case ExerciseType.P1_U:
                ratio = 0.18f;
                minSeconds = 7f;
                maxSeconds = 18f;
                break;
            case ExerciseType.P2_T:
                ratio = 0.20f;
                minSeconds = 8f;
                maxSeconds = 20f;
                break;
            case ExerciseType.P3_Cuña:
                ratio = 0.24f;
                minSeconds = 10f;
                maxSeconds = 24f;
                break;
            case ExerciseType.P4_V:
                ratio = 0.27f;
                minSeconds = 12f;
                maxSeconds = 28f;
                break;
            case ExerciseType.P5_Cilindro:
                ratio = 0.33f;
                minSeconds = 15f;
                maxSeconds = 34f;
                break;
            default:
                ratio = 0.20f;
                minSeconds = 8f;
                maxSeconds = 20f;
                break;
        }

        return Mathf.Clamp(electrode.baseConsumptionSeconds * ratio, minSeconds, maxSeconds);
    }

    private float GetGuidedMaxSessionSeconds(WeldingEvaluationConfig.ElectrodeProfile electrode)
    {
        // P2-T is coverage-based, not time-based.  Give the student 5 minutes to
        // weld both seams before the safety time-out kicks in.
        if (selectedExercise == ExerciseType.P2_T)
            return 300f;

        var target = GetGuidedTargetArcSeconds(electrode);
        return Mathf.Max(12f, target * 2.2f + 8f);
    }

    private float GetGuidedCompletionIdleSeconds()
    {
        return selectedExercise == ExerciseType.P5_Cilindro ? 1.5f : 1.0f;
    }

    // ── Bead geometry helpers ─────────────────────────────────────────────────

    /// <summary>
    /// Maps a raw value to a 0–100 score.
    /// Returns 100 when value ≤ perfectAt, 0 when value ≥ failAt, linear in between.
    /// </summary>
    private static float ScoreLinear(float value, float perfectAt, float failAt)
    {
        if (perfectAt >= failAt) return value <= perfectAt ? 100f : 0f;
        return Mathf.Clamp01(1f - (value - perfectAt) / (failAt - perfectAt)) * 100f;
    }

    /// <summary>
    /// Returns the maximum perpendicular distance from any bead point to the
    /// straight line connecting the first and last bead point (world metres).
    /// </summary>
    private static float ComputeStraightnessDeviation(
        System.Collections.Generic.IReadOnlyList<Vector3> pts)
    {
        if (pts.Count < 2) return 0f;
        Vector3 lineDir = (pts[pts.Count - 1] - pts[0]).normalized;
        if (lineDir.sqrMagnitude < 0.001f) return 0f;

        float maxPerp = 0f;
        Vector3 origin = pts[0];
        foreach (var pt in pts)
        {
            float proj    = Vector3.Dot(pt - origin, lineDir);
            Vector3 onLine = origin + lineDir * proj;
            float perp    = Vector3.Distance(pt, onLine);
            if (perp > maxPerp) maxPerp = perp;
        }
        return maxPerp;
    }

    /// <summary>
    /// Returns the average world-space distance from each bead point to the
    /// nearest point on any seam segment (metres).
    /// </summary>
    private float ComputeAvgSeamDeviation(
        System.Collections.Generic.IReadOnlyList<Vector3> pts,
        System.Collections.Generic.IReadOnlyList<WeldTargetLine.SeamSegment> seams)
    {
        if (pts.Count == 0 || seams == null || seams.Count == 0) return float.MaxValue;

        float sum = 0f;
        foreach (var pt in pts)
        {
            float minDist = float.MaxValue;
            foreach (var seg in seams)
            {
                Vector3 ws  = targetLine.transform.TransformPoint(seg.start);
                Vector3 we  = targetLine.transform.TransformPoint(seg.end);
                Vector3 dir = we - ws;
                float   len = dir.magnitude;
                if (len < 0.0001f) continue;
                float   t   = Mathf.Clamp01(Vector3.Dot(pt - ws, dir) / (len * len));
                float   d   = Vector3.Distance(pt, ws + dir * t);
                if (d < minDist) minDist = d;
            }
            if (minDist < float.MaxValue) sum += minDist;
        }
        return sum / pts.Count;
    }

    /// <summary>
    /// Returns the fraction of the best-matching seam that the bead covers (0–1).
    /// A bead point is considered "on" the seam if it is within proximityThreshold metres.
    /// </summary>
    // ComputeSeamCoverage (bead-based) removed — replaced by TrackSeamDwellTime /
    // GetSeamDwellCoverage which are robust against VR arm tremor.

    // ── Seam velocity-gated weld coverage (P2-T) ─────────────────────────────────
    //
    // WHY velocity-gating instead of dwell-time or plain spatial bins:
    //
    //   Natural VR arm tremor oscillates the electrode tip ±8-15 cm at 0.5-2 Hz.
    //   Peak velocity ≈ 2π × 0.10 m × 1 Hz ≈ 630 mm/s.
    //   Deliberate fillet welding travel speed: 3-8 mm/s.
    //
    //   A velocity window [MinWeldVelMmS, MaxWeldVelMmS] lets through ONLY real
    //   welding motion:
    //     • Stationary hovering  (0 mm/s)   < MinWeldVelMmS → rejected
    //     • Deliberate welding   (3-8 mm/s) → inside window  → accepted ✓
    //     • Tremor / fast moves  (>>25 mm/s) > MaxWeldVelMmS → rejected
    //
    //   This makes it physically impossible to fill bins without actual forward
    //   movement along the seam at a realistic welding pace.

    private const int   SeamWeldBins      = 10;    // 10 × 3 cm sections = 30 cm seam
    private const float SeamProximityM    = 0.080f; // 80 mm to seam axis  (generous for 45° approach)
    private const float MinWeldVelMmS     = 1.5f;  // below = hovering / stationary
    private const float MaxWeldVelMmS     = 25f;   // above = tremor / fast repositioning
    private const float VelSmoothingAlpha = 0.25f; // EMA smoothing for per-frame velocity

    private bool[,] _seamBinWelded;
    private int     _seamWeldSeamCount;
    private Vector3 _velTrackLastPos;
    private bool    _velTrackLastPosSet;
    private float   _smoothedVelMmS;

    /// <summary>
    /// Called every frame the arc is active (P2-T only).
    /// Computes smoothed velocity along the nearest seam axis and marks the
    /// corresponding bin as welded only when speed is in the deliberate-welding range.
    /// </summary>
    private void TrackSeamVelocityProgress(Vector3 tipWorld)
    {
        if (tipWorld == Vector3.zero) { _velTrackLastPosSet = false; return; }
        if (targetLine == null) return;
        var seams = targetLine.Seams;
        if (seams == null || seams.Count == 0) return;

        // (Re-)initialise bin array if needed
        if (_seamBinWelded == null || _seamWeldSeamCount != seams.Count)
        {
            _seamWeldSeamCount = seams.Count;
            _seamBinWelded     = new bool[seams.Count, SeamWeldBins];
        }

        // ── Find nearest seam ─────────────────────────────────────────────────
        int   nearestSeam = -1;
        float nearestDist = float.MaxValue;
        float nearestT    = 0f;

        for (int si = 0; si < seams.Count; si++)
        {
            Vector3 ws   = targetLine.transform.TransformPoint(seams[si].start);
            Vector3 we   = targetLine.transform.TransformPoint(seams[si].end);
            Vector3 dir  = we - ws;
            float   len2 = dir.sqrMagnitude;
            if (len2 < 0.0001f) continue;

            float   t      = Vector3.Dot(tipWorld - ws, dir) / len2;
            if (t < -0.05f || t > 1.05f) continue;

            Vector3 onSeam = ws + dir * Mathf.Clamp01(t);
            float   dist   = Vector3.Distance(tipWorld, onSeam);
            if (dist < nearestDist) { nearestDist = dist; nearestSeam = si; nearestT = t; }
        }

        // Lost proximity to any seam — reset velocity tracking
        if (nearestSeam < 0 || nearestDist > SeamProximityM)
        {
            _velTrackLastPosSet = false;
            return;
        }

        // ── Velocity gate ─────────────────────────────────────────────────────
        if (_velTrackLastPosSet)
        {
            Vector3 ws     = targetLine.transform.TransformPoint(seams[nearestSeam].start);
            Vector3 we     = targetLine.transform.TransformPoint(seams[nearestSeam].end);
            Vector3 dir    = (we - ws).normalized;

            // Absolute displacement along the seam axis this frame (mm/s)
            float dispM    = Mathf.Abs(Vector3.Dot(tipWorld - _velTrackLastPos, dir));
            float rawMmS   = Time.deltaTime > 0.0001f ? (dispM * 1000f) / Time.deltaTime : 0f;

            // Exponential moving average — low-pass filter to smooth per-frame jitter
            _smoothedVelMmS = Mathf.Lerp(_smoothedVelMmS, rawMmS, VelSmoothingAlpha);

            // Accept only deliberate welding speed
            if (_smoothedVelMmS >= MinWeldVelMmS && _smoothedVelMmS <= MaxWeldVelMmS)
            {
                int bin = Mathf.Clamp(
                    Mathf.FloorToInt(Mathf.Clamp01(nearestT) * SeamWeldBins),
                    0, SeamWeldBins - 1);
                _seamBinWelded[nearestSeam, bin] = true;
            }
        }

        _velTrackLastPos    = tipWorld;
        _velTrackLastPosSet = true;
    }

    /// <summary>Returns the velocity-gated weld coverage fraction (0-1) for one seam.</summary>
    private float GetSeamWeldCoverage(int si)
    {
        if (_seamBinWelded == null || si >= _seamWeldSeamCount) return 0f;
        int filled = 0;
        for (int b = 0; b < SeamWeldBins; b++)
            if (_seamBinWelded[si, b]) filled++;
        return (float)filled / SeamWeldBins;
    }

    /// <summary>
    /// Returns the raw weld-tip world position.  Uses the weldTip transform directly
    /// so velocity is accurate every frame (needed for the velocity gate).
    /// Falls back to the last bead point if weldTip is unavailable.
    /// </summary>
    private Vector3 GetCurrentTipWorldPos()
    {
        if (_migWeldingCache == null)
            _migWeldingCache = FindAnyObjectByType<MigWelding>();
        if (_migWeldingCache != null && _migWeldingCache.weldTip != null)
            return _migWeldingCache.weldTip.position;

        // Fallback: last bead surface point
        if (beadRenderer != null && beadRenderer.AllWorldPoints.Count > 0)
        {
            var pts = beadRenderer.AllWorldPoints;
            return pts[pts.Count - 1];
        }

        return Vector3.zero;
    }

    /// <summary>
    /// Returns per-seam coverage array for P2_T HUD display.
    /// Index 0 = first seam (front fillet), 1 = second seam (back fillet), etc.
    /// Returns null when data is unavailable.
    /// </summary>
    public float[] GetPerSeamCoverages()
    {
        if (selectedExercise != ExerciseType.P2_T || targetLine == null) return null;
        var seams = targetLine.Seams;
        if (seams == null || seams.Count == 0 || _seamBinWelded == null) return null;
        var result = new float[seams.Count];
        for (int i = 0; i < seams.Count; i++)
            result[i] = GetSeamWeldCoverage(i);
        return result;
    }

    /// <summary>
    /// Returns the world-space length (metres) of the longest seam segment.
    /// </summary>
    private float GetBestSeamLengthM(
        System.Collections.Generic.IReadOnlyList<WeldTargetLine.SeamSegment> seams)
    {
        float maxLen = 0f;
        foreach (var seg in seams)
        {
            Vector3 ws  = targetLine.transform.TransformPoint(seg.start);
            Vector3 we  = targetLine.transform.TransformPoint(seg.end);
            float   len = (we - ws).magnitude;
            if (len > maxLen) maxLen = len;
        }
        return maxLen;
    }
}
