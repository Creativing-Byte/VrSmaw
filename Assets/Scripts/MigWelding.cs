using UnityEngine;

// Compatibility script for the existing welding tool prefab.
// The class name is intentionally kept as MigWelding because the prefab already references it.
[DisallowMultipleComponent]
public class MigWelding : MonoBehaviour
{
    [Header("Existing Prefab References")]
    public Transform weldTip;
    public LineRenderer weldLine;
    public LayerMask weldableLayer;

    [Header("Bridge")]
    [SerializeField]
    private ArduinoBridgeReceiver bridge;

    [SerializeField]
    private bool autoBootstrapBridge = true;

    [SerializeField]
    private int autoBridgePort = 9100;

    [Header("Evaluation Config (optional — syncs arc range with selected electrode)")]
    [SerializeField]
    private WeldingEvaluationConfig evaluationConfig;

    [SerializeField]
    private WeldingEvaluator weldingEvaluator;

    [Header("Electrode Visual")]
    [SerializeField]
    private Transform consumableTipVisual;

    [SerializeField]
    private Vector3 consumptionAxisLocal = Vector3.up;

    [SerializeField]
    private float millimetersToLocalUnits = 0.001f;

    [SerializeField]
    private float maxElectrodeLengthMm = 350f;

    [SerializeField]
    private float minElectrodeLengthMm = 40f;

    [Header("Simulated Consumption (when no real telemetry)")]
    [Tooltip("Simulate electrode consumption based on arc-active time when Arduino is not connected.")]
    [SerializeField] private bool simulateConsumption = true;
    [Tooltip("Electrode consumption rate in mm/s while arc is active. Typical SMAW: 0.5-1.0 mm/s.")]
    [SerializeField] [Range(0.1f, 5f)] private float consumptionRateMmPerSec = 0.7f;

    [Header("Arc / Distance")]
    [SerializeField]
    private float fallbackRayDistanceMeters = 0.15f;

    [SerializeField]
    private float validArcMinMm = 2f;

    [SerializeField]
    private float validArcMaxMm = 5f;

    [SerializeField]
    private bool showArcWhenWithinRange = true;

    [Tooltip("Radius (m) of the proximity sphere used to detect nearby weldable surfaces. " +
             "Keep tight (0.012 m) so surfaces across a T-joint gap are never detected.")]
    [SerializeField] private float proximityDetectionRadiusM = 0.012f;

    [Tooltip("Auto-activate session when arc first detected (no menu required for quick testing).")]
    [SerializeField] private bool autoStartSessionOnArc = true;

    [Header("Arc Visual Feedback")]
    [Tooltip("Light that glows at the electrode tip when arc is active. Auto-created if empty.")]
    [SerializeField] private Light arcLight;
    [Tooltip("Particle system for welding sparks. Auto-created if empty.")]
    [SerializeField] private ParticleSystem arcParticles;
    [SerializeField] private Color guideRayColor  = new Color(0.3f, 0.5f, 1.0f, 0.5f);
    [SerializeField] private Color approachColor  = new Color(1.0f, 0.6f, 0.1f, 0.9f);
    [SerializeField] private Color arcActiveColor = new Color(1.0f, 1.0f, 0.2f, 1.0f);

    [Header("Debug State")]
    [SerializeField]
    private float currentLaserDistanceMm;

    [SerializeField]
    private float currentConsumedMm;

    [SerializeField]
    private float currentElectrodeLengthMm;

    [SerializeField]
    private bool arcIsValid;

    [SerializeField]
    private bool sessionActive;

    [SerializeField]
    private float activeSeconds;

    [SerializeField]
    private float continuitySeconds;

    [SerializeField]
    private float accumulatedDistanceErrorMm;

    [SerializeField]
    private int validArcSamples;

    private Vector3 _initialTipLocalPosition;
    private Vector3 _initialConsumableLocalPosition;
    private bool _manualSessionRequested;
    private bool _receivingRealTelemetry;
    private Vector3 _lastSurfacePoint;
    private bool _hadSurfaceLastFrame;

    // Electrode auto-reset: when fully spent, pause arc and reset after a short delay
    private float _spentCooldownTimer = 0f;
    private bool  _isSpent           = false;
    private const float SpentCooldownSec = 2.5f;   // seconds to "change electrode"

    /// <summary>True while the electrode is spent and being replaced (arc disabled).</summary>
    public bool ElectrodeIsSpent => _isSpent;

    private void Awake()
    {
        if (weldTip != null)
        {
            _initialTipLocalPosition = weldTip.localPosition;
        }

        if (consumableTipVisual != null)
        {
            _initialConsumableLocalPosition = consumableTipVisual.localPosition;
        }

        if (weldingEvaluator == null)
            weldingEvaluator = FindAnyObjectByType<WeldingEvaluator>();

        if (weldingEvaluator != null)
            weldingEvaluator.SessionStarted += ResetElectrode;

        EnsureBridge();
        EnsureLineRenderer();

        if (weldLine != null)
        {
            weldLine.enabled = false;
            weldLine.positionCount = 2;
        }
    }

    private void OnDestroy()
    {
        if (weldingEvaluator != null)
            weldingEvaluator.SessionStarted -= ResetElectrode;
    }

private void Update()
    {
        EnsureBridge();

        if (bridge != null && bridge.TryGetLatest(out var telemetry))
        {
            ApplyTelemetry(telemetry);
        }
        else
        {
            UpdateArcFromRaycast();

            // Sync sessionActive with the evaluator when no Arduino is connected.
            // Without this, UpdateMetrics() is never reached and simulated
            // consumption / continuity metrics never accumulate.
            // Also preserve the autoStartSessionOnArc flag so arc-triggered
            // sessions aren't immediately killed by the evaluator sync.
            if (weldingEvaluator != null)
                sessionActive = weldingEvaluator.SessionActive
                             || _manualSessionRequested
                             || (autoStartSessionOnArc && arcIsValid);
        }

        UpdateMetrics();
    }

    /// <summary>True when the laser distance is within the valid arc range.</summary>
    public bool ArcIsValid => arcIsValid;

    /// <summary>Last laser distance reading in millimetres.</summary>
    public float CurrentLaserDistanceMm => currentLaserDistanceMm;

    /// <summary>0 = full electrode, 1 = fully consumed. Used by ElectrodeVisualShrink.</summary>
    public float ElectrodeConsumedFraction =>
        Mathf.Clamp01(currentConsumedMm / Mathf.Max(1f, maxElectrodeLengthMm - minElectrodeLengthMm));

    /// <summary>Resets electrode consumption to zero (call on new session / new electrode).</summary>
    public void ResetElectrode()
    {
        currentConsumedMm        = 0f;
        currentElectrodeLengthMm = maxElectrodeLengthMm;
        UpdateConsumableVisual();
    }

    public string GetMetricsSummary()
    {
        var averageDistanceError = validArcSamples > 0
            ? accumulatedDistanceErrorMm / validArcSamples
            : 0f;

        return $"time={activeSeconds:F1}s continuity={continuitySeconds:F1}s distanceErrorAvg={averageDistanceError:F2}mm consumed={currentConsumedMm:F1}mm";
    }

    public void StartWelding()
    {
        _manualSessionRequested = true;
        sessionActive = true;
    }

    public void StopWelding()
    {
        _manualSessionRequested = false;
        sessionActive = false;
    }

    private void EnsureBridge()
    {
        if (bridge != null)
        {
            return;
        }

        bridge = FindAnyObjectByType<ArduinoBridgeReceiver>();
        if (bridge != null || !autoBootstrapBridge)
        {
            return;
        }

        var bridgeObject = new GameObject("Arduino Bridge Receiver");
        bridge = bridgeObject.AddComponent<ArduinoBridgeReceiver>();
        bridge.Configure(autoBridgePort);
    }

    private void EnsureLineRenderer()
    {
        if (weldLine != null)
        {
            return;
        }

        weldLine = GetComponent<LineRenderer>();
        if (weldLine != null)
        {
            return;
        }

        weldLine = gameObject.AddComponent<LineRenderer>();
        weldLine.material = new Material(Shader.Find("Sprites/Default"));
        weldLine.widthMultiplier = 0.003f;
        weldLine.positionCount = 2;
        weldLine.useWorldSpace = true;
        weldLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        weldLine.receiveShadows = false;
        weldLine.startColor = new Color(1f, 0.85f, 0.3f, 0.9f);
        weldLine.endColor = new Color(1f, 0.4f, 0.1f, 0.35f);
    }

    private void ApplyTelemetry(ArduinoBridgeReceiver.WeldSensorTelemetry telemetry)
    {
        _receivingRealTelemetry = true;
        currentLaserDistanceMm  = telemetry.laserDistanceMm;
        currentConsumedMm       = Mathf.Max(0f, telemetry.electrodeConsumedMm);

        if (currentConsumedMm <= 0f && telemetry.servoNormalized > 0f)
        {
            currentConsumedMm = Mathf.Lerp(0f, maxElectrodeLengthMm - minElectrodeLengthMm, Mathf.Clamp01(telemetry.servoNormalized));
        }

        currentElectrodeLengthMm = Mathf.Clamp(maxElectrodeLengthMm - currentConsumedMm, minElectrodeLengthMm, maxElectrodeLengthMm);
        sessionActive = telemetry.triggerPressed || _manualSessionRequested;

        UpdateConsumableVisual();
        UpdateArcVisual(telemetry.laserDistanceMm);
    }

private void UpdateConsumableVisual()
    {
        // NOTE: weldTip is intentionally NOT moved here.
        // ElectrodeVisualShrink reads ElectrodeConsumedFraction and
        // handles ALL Cylinder scale + position correction, which is
        // the only visual that needs to change.
        // Moving weldTip via consumptionAxisLocal caused a sliding artefact
        // because _initialTipLocalPosition was never initialised from the
        // actual weldTip.localPosition, so the tip drifted to (0,0,0).

        if (consumableTipVisual != null)
        {
            var localOffset = consumptionAxisLocal.normalized
                            * (currentConsumedMm * millimetersToLocalUnits);
            consumableTipVisual.localPosition = _initialConsumableLocalPosition - localOffset;
        }
    }

    private void UpdateArcVisual(float laserDistanceMm)
    {
        GetArcRange(out var minMm, out var maxMm);
        arcIsValid = laserDistanceMm >= minMm && laserDistanceMm <= maxMm;

        if (weldLine == null || weldTip == null) return;

        weldLine.enabled = true;
        float arcDistanceMeters = Mathf.Max(0.001f, laserDistanceMm) * 0.001f;
        weldLine.SetPosition(0, weldTip.position);
        weldLine.SetPosition(1, weldTip.position + weldTip.forward * arcDistanceMeters);
        ApplyArcLineColor(laserDistanceMm > 0f, arcIsValid);
        UpdateArcFeedback(arcIsValid, weldTip.position + weldTip.forward * arcDistanceMeters);
    }

    /// <summary>
    /// Directional arc detection using SphereCast in the electrode's forward direction.
    ///
    /// WHY SphereCast instead of OverlapSphere+ClosestPoint:
    ///   OverlapSphere finds any surface within a radius in all directions, including
    ///   surfaces ACROSS a joint gap. SphereCast only sweeps FORWARD from the tip, so
    ///   the electrode must actually be POINTED AT the surface — preventing arc-in-air
    ///   when the tip is floating inside a T-joint groove.
    ///
    /// A tight sphere radius (≈ electrode tip diameter) and small angle tolerance (±12°)
    /// make it forgiving enough for real use without allowing sideways/gap detection.
    /// </summary>
private void UpdateArcFromRaycast()
    {
        _receivingRealTelemetry = false;
        if (weldTip == null) return;

        if (_isSpent) { arcIsValid = false; currentLaserDistanceMm = 0f; return; }

        EnsureArcParticles();
        EnsureArcLight();

        GetArcRange(out var minMm, out var maxMm);

        float scanDistM = (maxMm + 8f) * 0.001f;

        bool    surfaceFound  = false;
        float   closestDistMm = float.MaxValue;
        Vector3 closestPoint  = weldTip.position + weldTip.forward * scanDistM;

        // OverlapSphere with tight radius: electrode tip just needs to be NEAR the surface.
        // The radius is intentionally small (proximityDetectionRadiusM, default 0.012 m)
        // so surfaces across a T-joint gap are NOT picked up (gap is wider than 12 mm).
        // This allows free welding along the seam without requiring the tip to be aimed
        // at a specific angle, unlike SphereCast which was too directional.
        var nearby = Physics.OverlapSphere(weldTip.position, proximityDetectionRadiusM, weldableLayer);
        foreach (var col in nearby)
        {
            // Exclude weld-bead child objects — they inherit the parent's "Weldable" tag
            // but must not register as weld surfaces (would keep the arc active above old beads).
            if (col.gameObject.name.StartsWith("WeldBead")) continue;
            bool isWeldable = col.CompareTag("Weldable")
                           || (col.transform.parent != null
                               && col.transform.parent.CompareTag("Weldable"));
            if (!isWeldable) continue;

            var   pt     = col.ClosestPoint(weldTip.position);
            float distMm = Vector3.Distance(weldTip.position, pt) * 1000f;
            if (distMm < closestDistMm)
            {
                closestDistMm = distMm;
                closestPoint  = pt;
                surfaceFound  = true;
            }
        }

        if (surfaceFound)
        {
            currentLaserDistanceMm = closestDistMm;
            arcIsValid             = closestDistMm <= maxMm;
            _lastSurfacePoint      = closestPoint;
            _hadSurfaceLastFrame   = true;

            if (arcIsValid && autoStartSessionOnArc && !sessionActive)
                sessionActive = true;
        }
        else
        {
            currentLaserDistanceMm = 0f;
            arcIsValid             = false;
            _hadSurfaceLastFrame   = false;
        }

        if (weldLine != null)
        {
            weldLine.enabled = true;
            weldLine.SetPosition(0, weldTip.position);
            weldLine.SetPosition(1, surfaceFound ? closestPoint
                                                 : weldTip.position + weldTip.forward * scanDistM);
            ApplyArcLineColor(surfaceFound, arcIsValid);
        }

        UpdateArcFeedback(arcIsValid, closestPoint);
    }

    private void ApplyArcLineColor(bool surfaceHit, bool arcActive)
    {
        if (weldLine == null) return;
        if (arcActive)
        {
            weldLine.startColor      = arcActiveColor;
            weldLine.endColor        = new Color(1f, 0.5f, 0.05f, 0.9f);
            weldLine.widthMultiplier = 0.006f;
        }
        else if (surfaceHit)
        {
            weldLine.startColor      = approachColor;
            weldLine.endColor        = new Color(1f, 0.3f, 0.05f, 0.4f);
            weldLine.widthMultiplier = 0.004f;
        }
        else
        {
            weldLine.startColor      = guideRayColor;
            weldLine.endColor        = new Color(0.1f, 0.1f, 0.4f, 0.15f);
            weldLine.widthMultiplier = 0.002f;
        }
    }

    /// <summary>Drives the arc glow light and spark particles.</summary>
    private void UpdateArcFeedback(bool active, Vector3 surfacePoint)
    {
        EnsureArcLight();
        EnsureArcParticles();

        if (arcLight != null)
        {
            arcLight.enabled   = active;
            arcLight.intensity = active ? 2.0f : 0f;
        }

        if (arcParticles != null)
        {
            if (active && !arcParticles.isPlaying) arcParticles.Play();
            else if (!active && arcParticles.isPlaying) arcParticles.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        }
    }

    private void EnsureArcLight()
    {
        if (arcLight != null || weldTip == null) return;
        var go      = new GameObject("Arc Light");
        go.transform.SetParent(weldTip, false);
        arcLight           = go.AddComponent<Light>();
        arcLight.type      = LightType.Point;
        arcLight.color     = new Color(1f, 0.85f, 0.3f);
        arcLight.intensity = 0f;
        arcLight.range     = 0.4f;
        arcLight.shadows   = LightShadows.None;
    }

    private void EnsureArcParticles()
    {
        if (arcParticles != null || weldTip == null) return;

        var go = new GameObject("Arc Sparks");
        go.transform.SetParent(weldTip, false);
        arcParticles = go.AddComponent<ParticleSystem>();

        // ── Main ──────────────────────────────────────────────────────────────
        var main         = arcParticles.main;
        main.loop        = true;
        main.startLifetime   = new ParticleSystem.MinMaxCurve(0.05f, 0.15f);
        main.startSpeed      = new ParticleSystem.MinMaxCurve(0.3f, 1.2f);
        main.startSize       = new ParticleSystem.MinMaxCurve(0.001f, 0.004f);
        main.startColor      = new ParticleSystem.MinMaxGradient(
                                   new Color(1f, 0.9f, 0.3f), new Color(1f, 0.5f, 0.1f));
        main.gravityModifier = 0.4f;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles    = 80;

        // ── Emission ──────────────────────────────────────────────────────────
        var emission      = arcParticles.emission;
        emission.enabled  = true;
        emission.rateOverTime = 60f;

        // ── Shape: tiny cone downward from tip ────────────────────────────────
        var shape       = arcParticles.shape;
        shape.enabled   = true;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle     = 35f;
        shape.radius    = 0.001f;

        // ── Colour over lifetime ──────────────────────────────────────────────
        var col         = arcParticles.colorOverLifetime;
        col.enabled     = true;
        var grad        = new Gradient();
        grad.SetKeys(
            new[] { new GradientColorKey(new Color(1f,1f,0.6f), 0f),
                    new GradientColorKey(new Color(1f,0.3f,0f), 1f) },
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(0f, 1f) });
        col.color = new ParticleSystem.MinMaxGradient(grad);

        // ── Renderer: URP-compatible unlit material ───────────────────────────
        var rend       = arcParticles.GetComponent<ParticleSystemRenderer>();
        var shader     = Shader.Find("Universal Render Pipeline/Particles/Unlit")
                      ?? Shader.Find("Particles/Standard Unlit")
                      ?? Shader.Find("Universal Render Pipeline/Unlit");
        if (shader != null)
        {
            var mat    = new Material(shader);
            mat.color  = Color.white;
            rend.material = mat;
        }
        rend.renderMode = ParticleSystemRenderMode.Billboard;

        arcParticles.Stop(); // start paused; UpdateArcFeedback will play when arc valid
    }

    /// <summary>Returns arc valid range: uses active electrode from WeldingEvaluator/config when
    /// available, otherwise falls back to the Inspector-set values.</summary>
    private void GetArcRange(out float minMm, out float maxMm)
    {
        if (weldingEvaluator != null && weldingEvaluator.ActiveElectrode != null)
        {
            minMm = weldingEvaluator.ActiveElectrode.arcMinMm;
            maxMm = weldingEvaluator.ActiveElectrode.arcMaxMm;
            return;
        }

        if (evaluationConfig != null)
        {
            var e = evaluationConfig.GetElectrode(0);
            if (e != null) { minMm = e.arcMinMm; maxMm = e.arcMaxMm; return; }
        }

        minMm = validArcMinMm;
        maxMm = validArcMaxMm;
    }

    private void UpdateMetrics()
    {
        if (!sessionActive) return;

        // ── Electrode spent cooldown ───────────────────────────────────────────
        if (_isSpent)
        {
            _spentCooldownTimer -= Time.deltaTime;
            if (_spentCooldownTimer <= 0f)
            {
                _isSpent = false;
                ResetElectrode();   // new electrode ready
            }
            return;   // no welding while changing electrode
        }

        activeSeconds += Time.deltaTime;

        if (arcIsValid)
        {
            continuitySeconds += Time.deltaTime;
            validArcSamples++;

            GetArcRange(out var minMm, out var maxMm);
            var targetDistanceMm = Mathf.Clamp(currentLaserDistanceMm, minMm, maxMm);
            accumulatedDistanceErrorMm += Mathf.Abs(currentLaserDistanceMm - targetDistanceMm);

            // Simulate electrode consumption when Arduino is not providing real data
            if (simulateConsumption && !_receivingRealTelemetry)
            {
                float maxConsumable = maxElectrodeLengthMm - minElectrodeLengthMm;
                currentConsumedMm   = Mathf.Min(
                    currentConsumedMm + consumptionRateMmPerSec * Time.deltaTime,
                    maxConsumable);
                currentElectrodeLengthMm = Mathf.Clamp(
                    maxElectrodeLengthMm - currentConsumedMm,
                    minElectrodeLengthMm, maxElectrodeLengthMm);
                UpdateConsumableVisual();

                // Check if fully spent → trigger electrode-change pause
                if (currentConsumedMm >= maxConsumable && !_isSpent)
                {
                    _isSpent            = true;
                    _spentCooldownTimer = SpentCooldownSec;
                    arcIsValid          = false;   // kill arc immediately
                }
            }
        }
    }
}
