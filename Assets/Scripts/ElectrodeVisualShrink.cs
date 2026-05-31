using UnityEngine;

/// <summary>
/// Shrinks the electrode Cylinder visual as the electrode is consumed,
/// keeping the base end (inside the holder) fixed while the tip shortens.
///
/// Setup:
///   1. Attach to the ARco GameObject (or any persistent GO in the scene).
///   2. Assign migWelding reference (auto-found if left empty).
///   3. Assign cylinderTransform → the "Cylinder" child of "electrodo_a" inside ARco.
///      Leave empty and it will be found automatically by name.
///
/// How it works:
///   - Reads MigWelding.ElectrodeConsumedFraction (0=full, 1=spent)
///   - Scales the Cylinder along its local Z axis (the electrode length axis)
///   - Corrects the local position so the BASE end (in the holder) stays fixed
///     while the TIP end shrinks inward
///   - Also shows a "stub glow" colour tint when electrode is nearly spent
/// </summary>
[DisallowMultipleComponent]
public class ElectrodeVisualShrink : MonoBehaviour
{
    // ── Inspector ─────────────────────────────────────────────────────────────

    [Header("References")]
    [SerializeField] private MigWelding  migWelding;
    [Tooltip("The 'Cylinder' child of 'electrodo_a' inside ARco. Auto-found if left empty.")]
    [SerializeField] private Transform   cylinderTransform;

    [Header("Electrode axis")]
    [Tooltip("Local axis of the Cylinder that runs along the electrode length.")]
    [SerializeField] private Vector3 lengthAxisLocal = Vector3.forward;   // local +Z

    [Tooltip("True = consumable tip is at the +axis end (correct for this Cylinder).")]
    [SerializeField] private bool tipAtPositiveEnd = true;

    [Header("Visual limits")]
    [Tooltip("Fraction below which the cylinder is hidden (electrode fully spent).")]
    [SerializeField] [Range(0f, 0.2f)] private float hideThreshold = 0.02f;

    [Tooltip("Fraction at which the electrode starts turning red (nearly spent).")]
    [SerializeField] [Range(0.05f, 0.5f)] private float warningThreshold = 0.15f;

    [Header("Colours")]
    [SerializeField] private Color normalColor  = new Color(0.75f, 0.60f, 0.40f); // warm metal
    [SerializeField] private Color warningColor = new Color(0.90f, 0.20f, 0.05f); // red-hot stub

    // ── Private state ─────────────────────────────────────────────────────────

    private Vector3  _initLocalScale;
    private Vector3  _initLocalPos;
    private float    _origMeshLen;   // actual length of the mesh in local space (bounds.size * scale)
    private Renderer _rend;
    private Material _mat;          // instance material (so we don't modify the shared asset)
    private bool     _initialized;

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    private void Awake()
    {
        if (migWelding == null)
            migWelding = FindAnyObjectByType<MigWelding>();

        TryAutoFindCylinder();
        Init();
    }

    private void LateUpdate()
    {
        if (!_initialized) { Init(); if (!_initialized) return; }
        if (migWelding == null) return;

        float fraction = 1f - migWelding.ElectrodeConsumedFraction;   // 1=full, 0=spent
        fraction = Mathf.Clamp01(fraction);

        // ── Hide when fully spent ──────────────────────────────────────────
        bool visible = fraction > hideThreshold;
        if (cylinderTransform.gameObject.activeSelf != visible)
            cylinderTransform.gameObject.SetActive(visible);
        if (!visible) return;

        // ── Scale along length axis ────────────────────────────────────────
        float remaining = Mathf.Max(hideThreshold, fraction);
        var axis    = lengthAxisLocal.normalized;
        var axisAbs = new Vector3(Mathf.Abs(axis.x), Mathf.Abs(axis.y), Mathf.Abs(axis.z));

        float origLen = _origMeshLen;   // actual mesh length cached at Init (bounds.size × scale)
        var newScale = new Vector3(
            axisAbs.x > 0.5f ? _initLocalScale.x * remaining : _initLocalScale.x,
            axisAbs.y > 0.5f ? _initLocalScale.y * remaining : _initLocalScale.y,
            axisAbs.z > 0.5f ? _initLocalScale.z * remaining : _initLocalScale.z
        );
        cylinderTransform.localScale = newScale;

        // ── Reposition so BASE end stays fixed ────────────────────────────
        // Pivot is at cylinder centre → when scale shrinks, centre moves toward base.
        float halfDelta = origLen * (1f - remaining) * 0.5f;
        var   baseDir   = tipAtPositiveEnd ? -axis : axis;   // direction toward base
        cylinderTransform.localPosition = _initLocalPos + baseDir * halfDelta;

        // ── Colour tint when nearly spent ─────────────────────────────────
        if (_mat != null)
        {
            float t = Mathf.InverseLerp(warningThreshold, hideThreshold, fraction);
            _mat.color = Color.Lerp(normalColor, warningColor, t);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void Init()
    {
        if (cylinderTransform == null) { TryAutoFindCylinder(); }
        if (cylinderTransform == null) return;

        _initLocalScale = cylinderTransform.localScale;
        _initLocalPos   = cylinderTransform.localPosition;

        // Cache the ACTUAL local-space length along the electrode axis.
        // Strategy: try localBounds first; if it yields near-zero (some custom meshes)
        // fall back to world-space bounds projected onto the world axis; last resort = scale.
        _rend = cylinderTransform.GetComponent<Renderer>();

        var axis    = lengthAxisLocal.normalized;
        var axisAbs = new Vector3(Mathf.Abs(axis.x), Mathf.Abs(axis.y), Mathf.Abs(axis.z));

        if (_rend != null)
        {
            // Primary: localBounds.size (pre-scale mesh extents) × localScale
            var b = _rend.localBounds.size;
            _origMeshLen = Vector3.Dot(
                new Vector3(b.x * _initLocalScale.x,
                            b.y * _initLocalScale.y,
                            b.z * _initLocalScale.z),
                axisAbs);

            // Fallback A: if localBounds gives ~0 (import quirk), use world bounds
            if (_origMeshLen < 0.001f)
            {
                var worldAxis = cylinderTransform.TransformDirection(axis);
                var wb = _rend.bounds.size;
                _origMeshLen = Mathf.Abs(Vector3.Dot(wb,
                    new Vector3(Mathf.Abs(worldAxis.x),
                                Mathf.Abs(worldAxis.y),
                                Mathf.Abs(worldAxis.z))));
            }

            _mat = _rend.material;   // auto-creates instance
            _mat.color = normalColor;
        }

        // Fallback B: plain scale dot (original behaviour for unit-bounds primitives)
        if (_origMeshLen < 0.001f)
            _origMeshLen = Vector3.Dot(_initLocalScale, axisAbs);

        // Safety: if still zero (degenerate cylinder), default to 1 m so nothing explodes
        if (_origMeshLen < 0.001f)
            _origMeshLen = 1f;

        _initialized = true;
    }

    private void TryAutoFindCylinder()
    {
        if (cylinderTransform != null) return;

        // Search for Cylinder whose parent is named electrodo_a
        var all = UnityEngine.Resources.FindObjectsOfTypeAll<Transform>();
        foreach (var t in all)
            if (t.name == "Cylinder" && t.parent != null && t.parent.name == "electrodo_a")
            { cylinderTransform = t; return; }
    }
}
