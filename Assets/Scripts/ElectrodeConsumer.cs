using UnityEngine;

/// <summary>
/// Standalone electrode visual consumption — completely independent of sessions,
/// Arduino state, and _receivingRealTelemetry.
///
/// Logic: whenever MigWelding.ArcIsValid is true, consume the electrode at a
/// fixed mm/s rate.  Scales the Cylinder directly and keeps the base-end fixed
/// while the tip recedes.  Auto-resets after a short "electrode-change" pause.
///
/// Setup:
///   1. Attach to ARco (or any always-active GameObject).
///   2. Assign migWelding and electrodeCylinder in the Inspector.
///      (both are auto-found at Start if left empty)
/// </summary>
[DisallowMultipleComponent]
public class ElectrodeConsumer : MonoBehaviour
{
    // ── Inspector ─────────────────────────────────────────────────────────────

    [Header("References")]
    [SerializeField] private MigWelding migWelding;
    [Tooltip("The 'Cylinder' child of 'electrodo_a'. Assign explicitly for reliability.")]
    [SerializeField] private Transform  electrodeCylinder;
    [Tooltip("weld_t — the arc-detection origin. Will be repositioned to match the real tip as the electrode recedes.")]
    [SerializeField] private Transform  weldTip;

    [Header("Consumption")]
    [Tooltip("mm consumed per second of active arc. Real SMAW ≈ 5 mm/s.")]
    [SerializeField] [Range(0.1f, 15f)] private float consumptionRateMmPerSec = 5f;
    [Tooltip("Total consumable length in mm (electrode usable portion).")]
    [SerializeField] private float totalElectrodeMm = 200f;
    [Tooltip("Seconds to wait before auto-resetting (simulates inserting a new electrode).")]
    [SerializeField] private float changeElectrodeDelaySec = 2.5f;

    [Header("Visual colours")]
    [SerializeField] private Color normalColor  = new Color(0.75f, 0.60f, 0.40f);
    [SerializeField] private Color warningColor = new Color(0.90f, 0.20f, 0.05f);
    [Tooltip("Fraction remaining at which the warning colour kicks in.")]
    [SerializeField] [Range(0.05f, 0.5f)] private float warningFraction = 0.20f;

    [Header("Debug (read-only)")]
    [SerializeField] private float consumedMm;
    [SerializeField] private float fractionRemaining = 1f;
    [SerializeField] private bool  arcActive;
    [SerializeField] private bool  isSpent;

    // ── Private ───────────────────────────────────────────────────────────────

    private Vector3  _initScale;
    private Vector3  _initPos;
    private float    _halfLenLocal;   // half-length of Cylinder in parent-local units
    private float    _spentTimer;
    private Material _mat;
    private bool     _ready;

    // weld_t tracking — all in ARco-local space to be immune to XR rig movement
    private Vector3  _initWeldTipLocalPos;   // weld_t.localPosition at start (ARco-local)
    private Vector3  _initTipInArcoLocal;    // cylinder tip position in ARco-local space at start

    // ── Public API ─────────────────────────────────────────────────────────────
    /// <summary>1 = full electrode, 0 = fully spent.</summary>
    public float FractionRemaining => fractionRemaining;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Start()
    {
        // Auto-find references if not assigned
        if (migWelding == null)
            migWelding = FindAnyObjectByType<MigWelding>();

        if (electrodeCylinder == null)
        {
            var all = Resources.FindObjectsOfTypeAll<Transform>();
            foreach (var t in all)
                if (t.name == "Cylinder" && t.parent != null && t.parent.name == "electrodo_a")
                { electrodeCylinder = t; break; }
        }

        if (electrodeCylinder == null)
        {
            Debug.LogError("[ElectrodeConsumer] Could not find electrode Cylinder. Assign it in the Inspector.");
            return;
        }

        _initScale = electrodeCylinder.localScale;
        _initPos   = electrodeCylinder.localPosition;

        // Compute half-length using BOTH localBounds and world bounds — take the better one
        _halfLenLocal = ComputeHalfLength();

        // Grab material instance for colour tinting
        var rend = electrodeCylinder.GetComponent<Renderer>();
        if (rend != null)
        {
            _mat = rend.material;   // creates an instance automatically
            _mat.color = normalColor;
        }

        // Cache initial weld_t state in ARco-local space.
        // Using ARco-local (not world) means the values stay correct even after
        // the XR rig moves ARco to the gameplay position.
        if (weldTip != null && weldTip.parent != null && electrodeCylinder.parent != null)
        {
            _initWeldTipLocalPos = weldTip.localPosition;

            // Tip of Cylinder in electrodo_a local space at full electrode
            Vector3 initTipElectrodoA = _initPos - Vector3.forward * _halfLenLocal;

            // Convert: electrodo_a-local → world → ARco-local
            // InverseTransformPoint on ARco removes the world-position component,
            // giving a value that is STABLE across XR rig movements.
            _initTipInArcoLocal = weldTip.parent.InverseTransformPoint(
                electrodeCylinder.parent.TransformPoint(initTipElectrodoA));
        }

        _ready = true;
        Debug.Log($"[ElectrodeConsumer] Ready. halfLenLocal={_halfLenLocal:F4}  initScale={_initScale}  initPos={_initPos}");
    }

    private void Update()
    {
        if (!_ready || migWelding == null) return;

        arcActive = migWelding.ArcIsValid;

        // ── Spent: wait for electrode-change delay ───────────────────────────
        if (isSpent)
        {
            _spentTimer -= Time.deltaTime;
            if (_spentTimer <= 0f)
            {
                isSpent    = false;
                consumedMm = 0f;
                Debug.Log("[ElectrodeConsumer] New electrode inserted!");
            }
            ApplyVisual();
            return;
        }

        // ── Consume while arc is active ──────────────────────────────────────
        if (arcActive)
        {
            consumedMm = Mathf.Min(consumedMm + consumptionRateMmPerSec * Time.deltaTime,
                                   totalElectrodeMm);

            if (consumedMm >= totalElectrodeMm)
            {
                isSpent     = true;
                _spentTimer = changeElectrodeDelaySec;
                Debug.Log("[ElectrodeConsumer] Electrode fully spent!");
            }
        }

        ApplyVisual();
    }

    // ── Visual update ─────────────────────────────────────────────────────────

    private void ApplyVisual()
    {
        fractionRemaining = 1f - Mathf.Clamp01(consumedMm / Mathf.Max(1f, totalElectrodeMm));

        if (electrodeCylinder == null) return;

        float remaining = Mathf.Max(0.01f, fractionRemaining);

        // Scale down along local Z (the electrode's length axis)
        var newScale  = _initScale;
        newScale.z    = _initScale.z * remaining;
        electrodeCylinder.localScale = newScale;

        // Shift the cylinder centre so the BASE end (+Z) stays fixed,
        // only the TIP end (-Z) recedes.
        //   halfDelta = how much the centre must move toward base
        float halfDelta = _halfLenLocal * (1f - remaining);
        electrodeCylinder.localPosition = _initPos + Vector3.forward * halfDelta;

        // Colour tint: normal → warning as electrode nears spent
        if (_mat != null)
        {
            float t = Mathf.InverseLerp(warningFraction, 0f, fractionRemaining);
            _mat.color = Color.Lerp(normalColor, warningColor, t);
        }

        // ── Move weld_t so arc detection follows the real physical tip ────────
        // All math in ARco-local space → immune to XR rig teleporting ARco.
        if (weldTip != null && weldTip.parent != null && electrodeCylinder.parent != null)
        {
            // Current cylinder tip in electrodo_a local space
            Vector3 currentTipElectrodoA = _initPos + Vector3.forward * _halfLenLocal * (1f - 2f * remaining);

            // Convert to ARco-local: electrodo_a-local → world → ARco-local
            Vector3 currentTipArcoLocal = weldTip.parent.InverseTransformPoint(
                electrodeCylinder.parent.TransformPoint(currentTipElectrodoA));

            // Delta in ARco-local space (0 when electrode is full, grows as tip recedes)
            Vector3 deltaArcoLocal = currentTipArcoLocal - _initTipInArcoLocal;

            weldTip.localPosition = _initWeldTipLocalPos + deltaArcoLocal;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Compute half the physical length of the Cylinder in parent-local space.
    /// Uses several fallback strategies to handle meshes with unusual bounds.
    /// </summary>
    private float ComputeHalfLength()
    {
        var rend = electrodeCylinder.GetComponent<Renderer>();

        float bestLen = 0f;

        if (rend != null)
        {
            // Strategy A: localBounds.size.z (pre-scale mesh extent) × localScale.z
            float lenA = rend.localBounds.size.z * _initScale.z;

            // Strategy B: world-bounds projected onto the cylinder's forward axis
            // then divided by the parent's world-scale on that axis
            var worldFwd = electrodeCylinder.TransformDirection(Vector3.forward);
            var wbs = rend.bounds.size;
            float worldLen = Mathf.Abs(Vector3.Dot(wbs,
                new Vector3(Mathf.Abs(worldFwd.x), Mathf.Abs(worldFwd.y), Mathf.Abs(worldFwd.z))));

            // Convert world length → parent-local length
            float parentLossyZ = electrodeCylinder.parent != null
                ? Mathf.Max(0.001f, Mathf.Abs(electrodeCylinder.parent.lossyScale.z))
                : 1f;
            float lenB = worldLen / parentLossyZ;

            // Pick the larger (more likely correct) result
            bestLen = Mathf.Max(lenA, lenB);

            Debug.Log($"[ElectrodeConsumer] lenA(localBounds)={lenA:F4}  lenB(worldBounds)={lenB:F4}  chosen={bestLen:F4}");
        }

        // Final fallback: plain scale.z (correct for unit-bounds meshes)
        if (bestLen < 0.001f)
            bestLen = _initScale.z;

        // Absolute safety net
        if (bestLen < 0.001f)
            bestLen = 0.25f;

        return bestLen * 0.5f;
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Force-reset to a full electrode (called on new session start).</summary>
    public void ResetElectrode()
    {
        consumedMm  = 0f;
        isSpent     = false;
        _spentTimer = 0f;
        if (_ready) ApplyVisual();
    }
}
