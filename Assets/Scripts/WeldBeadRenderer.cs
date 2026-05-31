using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Renders the weld bead as a LineRenderer ATTACHED TO THE WELDED PIECE,
/// so the bead moves with the piece when the user grabs and repositions it.
///
/// One LineRenderer child GameObject is created per welded piece (or per
/// clear cycle). Positions are stored in the piece's local space and the
/// LineRenderer uses useWorldSpace = false, so any rigid-body movement of
/// the piece carries the bead along automatically.
/// </summary>
[DisallowMultipleComponent]
public class WeldBeadRenderer : MonoBehaviour
{
    // ── Inspector ─────────────────────────────────────────────────────────────

    [Header("References")]
    [SerializeField] private WeldingEvaluator evaluator;
    [SerializeField] private MigWelding        migWelding;

    [Header("Bead geometry")]
    [Tooltip("Minimum distance (m) between consecutive bead points.")]
    [SerializeField] private float minPointDistanceM = 0.003f;

    [Tooltip("Maximum distance (m) a new bead point may jump from the previous accepted point. " +
             "Outlier detections further than this are silently discarded — they would otherwise " +
             "produce a straight-line artifact in the LineRenderer. Rule of thumb: set to roughly " +
             "the maximum physical movement of the electrode between two frames at normal welding speed.")]
    [SerializeField] private float maxJumpDistanceM = 0.030f;  // 3 cm

    [Tooltip("How far above the surface (mm) the bead sits.")]
    [SerializeField] private float beadRaiseAboveSurfaceMm = 1.0f;

    [Tooltip("Bead width = electrode diameter x this multiplier.")]
    [SerializeField] private float beadWidthMultiplier = 2.5f;

    [Tooltip("Fallback bead width (m) when no electrode profile is available.")]
    [SerializeField] private float fallbackBeadWidthM = 0.007f;

    [Tooltip("Layer mask for the welding surface proximity / raycast.")]
    [SerializeField] private LayerMask weldableLayers = ~0;

    [Tooltip("Maximum distance (m) from tip to surface to accept a bead placement.\n" +
             "Should match the arc detection radius in MigWelding (proximityDetectionRadiusM).\n" +
             "Too small → bead doesn't register. Too large → bead floats away from piece.")]
    [SerializeField] private float maxSurfaceDistM = 0.012f;   // 12 mm — matches arc detection radius

    [Header("Bead colours")]
    [SerializeField] private Color hotColor  = new Color(1.00f, 0.55f, 0.05f, 1f);
    [SerializeField] private Color warmColor = new Color(0.65f, 0.20f, 0.04f, 1f);
    [SerializeField] private Color coolColor = new Color(0.20f, 0.13f, 0.09f, 1f);

    [Header("Diagnostics")]
    [SerializeField] private int   pointCount;
    [SerializeField] private bool  arcActiveThisFrame;
    [SerializeField] private float beadWidthM;

    // ── Per-piece bead state ──────────────────────────────────────────────────

    // Active LineRenderer -- lives as a child of the currently welded piece
    private LineRenderer _line;

    // The piece (Rigidbody root) the current bead segment belongs to
    private Transform    _currentPiece;

    // Points stored in _currentPiece LOCAL space
    private readonly List<Vector3> _points = new();

    // Accumulated world-space points for post-session bead analysis (all segments combined).
    // NOT cleared between segments — only on ClearBead() / OnSessionStarted().
    private readonly List<Vector3> _allWorldPoints = new();
    private float _totalBeadLengthWorldM = 0f;

    // Last added point in WORLD space -- used for the distance guard
    private Vector3 _lastWorldPoint;
    private bool    _hasLastPoint;

    // Surface normal of the last accepted bead point.
    // Used to reject points whose surface is perpendicular to the current one
    // (prevents the bead from jumping between the stem face and the base-plate top).
    private Vector3 _lastNormal;
    private bool    _hasLastNormal;

    [Tooltip("Min dot product between consecutive surface normals (0.5 ≈ 60°). " +
             "Points on a different surface plane are discarded.")]
    [SerializeField] [Range(0.1f, 0.99f)] private float normalContinuityDot = 0.5f;

    // All bead child GameObjects created this session (for cleanup)
    private readonly List<GameObject> _allBeadObjects = new();

    // ── Unity lifecycle ───────────────────────────────────────────────────────

    private void Awake()
    {
        if (evaluator  == null) evaluator  = FindAnyObjectByType<WeldingEvaluator>();
        if (migWelding == null) migWelding = FindAnyObjectByType<MigWelding>();

        if (evaluator != null)
            evaluator.SessionStarted += OnSessionStarted;
    }

    private void OnDestroy()
    {
        if (evaluator != null)
            evaluator.SessionStarted -= OnSessionStarted;
    }

    private void Update()
    {
        if (migWelding == null || migWelding.weldTip == null) return;

        bool sessionOk = evaluator == null || evaluator.SessionActive;
        arcActiveThisFrame = migWelding.ArcIsValid && (sessionOk || true);

        if (!arcActiveThisFrame)
        {
            // Reset ALL continuity state AND the current piece reference.
            // Setting _currentPiece = null ensures the next arc start opens a FRESH
            // LineRenderer segment, so no straight line is ever drawn from the last
            // bead point to wherever the arc re-establishes.
            _hasLastPoint  = false;
            _hasLastNormal = false;
            _currentPiece  = null;
            return;
        }

        // Find surface point AND which piece is being welded
        if (!TryGetSurfacePoint(out var surfacePoint, out var surfaceNormal, out var hitPiece)) return;
        if (hitPiece == null) return;

        // Minimum distance guard: skip micro-jitter without touching any state.
        if (_hasLastPoint && Vector3.Distance(surfacePoint, _lastWorldPoint) < minPointDistanceM)
            return;

        // Maximum jump guard: skip physically impossible jumps (detection outliers).
        // Do NOT update state — next frame will re-evaluate from the last good point.
        if (_hasLastPoint && Vector3.Distance(surfacePoint, _lastWorldPoint) > maxJumpDistanceM)
            return;

        // Normal continuity guard: discard points on the wrong face silently.
        // IMPORTANT: also reset _hasLastNormal so the very next frame can re-establish
        // the correct face without being "locked" to the old (wrong) normal.
        // This prevents the bead from stopping permanently when the detection briefly
        // hits an adjacent face on the same continuous arc stroke.
        if (_hasLastNormal && Vector3.Dot(surfaceNormal, _lastNormal) < normalContinuityDot)
        {
            _hasLastNormal = false;   // allow next frame to re-lock on any face
            return;
        }

        // Start a new segment when the piece changes or after an arc break
        // (_currentPiece is nulled in the arc-off block above).
        if (hitPiece != _currentPiece || _line == null)
            StartNewBeadOnPiece(hitPiece);

        // Accumulate world-space position for post-session bead analysis.
        // Distance is measured BEFORE updating _lastWorldPoint so we use the previous point.
        if (_hasLastPoint)
            _totalBeadLengthWorldM += Vector3.Distance(surfacePoint, _lastWorldPoint);
        _allWorldPoints.Add(surfacePoint);

        // Store position in piece-local space
        var localPoint = _currentPiece.InverseTransformPoint(surfacePoint);
        _points.Add(localPoint);
        _lastWorldPoint = surfacePoint;
        _hasLastPoint   = true;
        _lastNormal     = surfaceNormal;
        _hasLastNormal  = true;
        pointCount      = _points.Count;

        RefreshBeadWidth();

        // Push local positions to LineRenderer
        _line.positionCount = _points.Count;
        _line.SetPositions(_points.ToArray());
    }

    // ── Public API ────────────────────────────────────────────────────────────

    // ── Public analysis accessors ─────────────────────────────────────────────

    /// <summary>All world-space bead points accumulated since the last ClearBead/session start.</summary>
    public System.Collections.Generic.IReadOnlyList<Vector3> AllWorldPoints => _allWorldPoints;

    /// <summary>Total arc-path length in world metres (ignores arc-break gaps between segments).</summary>
    public float TotalBeadLengthWorldM => _totalBeadLengthWorldM;

    // ─────────────────────────────────────────────────────────────────────────

    public void ClearBead()
    {
        foreach (var go in _allBeadObjects)
            if (go != null) Destroy(go);
        _allBeadObjects.Clear();

        _line          = null;
        _currentPiece  = null;
        _points.Clear();
        _allWorldPoints.Clear();
        _totalBeadLengthWorldM = 0f;
        _hasLastPoint  = false;
        _hasLastNormal = false;
        pointCount     = 0;
        beadWidthM     = 0f;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private void StartNewBeadOnPiece(Transform piece)
    {
        _points.Clear();
        _hasLastPoint  = false;
        _hasLastNormal = false;
        _currentPiece  = piece;

        var go = new GameObject("WeldBead");
        // Parent with no local offset so local space == piece local space
        go.transform.SetParent(piece, worldPositionStays: false);
        _allBeadObjects.Add(go);

        _line = go.AddComponent<LineRenderer>();
        ApplyLineRendererSettings(_line);
        // KEY: positions are in piece-local space -> bead moves with the piece
        _line.useWorldSpace = false;
        _line.positionCount = 0;

        // NOTE: BoxCollider intentionally removed.
        // Adding a collider here caused the SphereCast in TryGetSurfacePoint (and the
        // OverlapSphere in MigWelding arc detection) to snap onto previously placed beads
        // in mid-air, producing the "welding in the air at 45°" artifact. The multi-pass
        // bead detection feature that required this collider is not active in the MVP.
    }

    /// <summary>
    /// Finds the nearest weldable surface point for bead placement.
    ///
    /// PRIMARY — SphereCasts from the electrode tip in 7 directions (forward hemisphere):
    ///   SphereCast (thick ray, 3 mm radius) instead of Raycast so that narrow fillet
    ///   strips at T-joint corners are reliably detected even when a pencil-thin ray
    ///   just misses the geometry.
    ///   Front-face guard: Dot(normal, castDir) >= 0 → reject back-faces.
    ///   This prevents the bead from ever landing inside the mesh, even if the tip is
    ///   very close to a surface. Among all valid hits the closest one is chosen.
    ///
    /// FALLBACK — OverlapSphere + ClosestPoint + inward probe:
    ///   Used only when ALL SphereCasts miss (unusual electrode angle).
    ///   The probe shoots from OUTSIDE the surface inward, guaranteeing a front-face
    ///   normal without the ambiguity of ClosestPoint at concave corners.
    /// </summary>
    private bool TryGetSurfacePoint(out Vector3 surfacePoint, out Vector3 surfaceNormal, out Transform hitPiece)
    {
        surfacePoint  = Vector3.zero;
        surfaceNormal = Vector3.up;
        hitPiece      = null;
        var   tip    = migWelding.weldTip;
        float raiseM = beadRaiseAboveSurfaceMm * 0.001f;
        Vector3 f = tip.forward, r = tip.right, u = tip.up;

        // ── Primary: SphereCasts — forward hemisphere, front-face validated ──────
        // 3 mm sphere radius catches fillet geometry that a thin ray misses.
        // All directions are in the electrode's forward hemisphere so we only
        // snap to surfaces the user is actually pointing the electrode at.
        const float castR = 0.003f;   // sphere radius (m)

        Vector3[] castDirs =
        {
            f,
            (f * 0.87f + r  * 0.50f).normalized,   // 30° right
            (f * 0.87f - r  * 0.50f).normalized,   // 30° left
            (f * 0.87f - u  * 0.50f).normalized,   // 30° down
            (f * 0.70f + r  * 0.50f - u * 0.50f).normalized,  // diagonal down-right
            (f * 0.70f - r  * 0.50f - u * 0.50f).normalized,  // diagonal down-left
            // NOTE: the steep-downward direction (f*0.5 - u*0.87) was intentionally removed.
            // It caused the SphereCast to detect the base-plate TOP face when welding the
            // T-joint fillet, producing a spurious bead segment "in the air" every time the
            // electrode was lifted even slightly. The remaining directions cover all realistic
            // electrode angles, including flat-bead welding (P1) where tip.forward is
            // already nearly vertical and the forward/diagonal casts reach the surface.
        };

        RaycastHit bestHit  = default;
        bool       hasHit   = false;
        float      bestDist = float.MaxValue;

        foreach (var dir in castDirs)
        {
            if (!Physics.SphereCast(tip.position, castR, dir, out var hit,
                                    maxSurfaceDistM, weldableLayers)) continue;
            if (!IsWeldableSurface(hit.collider)) continue;
            // Front-face guard: if the normal points the SAME direction as the cast,
            // the ray entered through a back-face (tip inside mesh) → reject.
            if (Vector3.Dot(hit.normal, dir) >= 0f) continue;
            if (hit.distance < bestDist)
            {
                bestDist = hit.distance;
                bestHit  = hit;
                hasHit   = true;
            }
        }

        if (hasHit)
        {
            surfacePoint  = bestHit.point + bestHit.normal * raiseM;
            surfaceNormal = bestHit.normal;
            hitPiece      = GetPieceRoot(bestHit.collider);
            return true;
        }

        // ── Fallback: OverlapSphere ClosestPoint + inward probe ───────────────────
        // Reached only when all SphereCasts miss (e.g. electrode nearly perpendicular
        // to the surface or at a very obtuse angle).
        var nearby = Physics.OverlapSphere(tip.position, maxSurfaceDistM * 2f, weldableLayers);
        float    minD   = float.MaxValue;
        Collider best   = null;
        Vector3  bestPt = Vector3.zero;

        foreach (var col in nearby)
        {
            if (!IsWeldableSurface(col)) continue;
            var pt = col.ClosestPoint(tip.position);
            float d = Vector3.Distance(tip.position, pt);
            if (d < minD) { minD = d; bestPt = pt; best = col; }
        }

        if (best != null && minD <= maxSurfaceDistM)
        {
            // outDir: from the surface toward the electrode tip (the "outside" side).
            Vector3 outDir = minD > 0.0001f
                ? (tip.position - bestPt).normalized
                : tip.forward;

            // Inward probe from 6 mm outside → guaranteed front-face normal.
            if (Physics.Raycast(bestPt + outDir * 0.006f, -outDir, out var probe, 0.018f, weldableLayers)
                && IsWeldableSurface(probe.collider))
            {
                surfacePoint  = probe.point + probe.normal * raiseM;
                surfaceNormal = probe.normal;
                hitPiece      = GetPieceRoot(probe.collider);
                return true;
            }

            // Ultimate fallback: ClosestPoint + approximate normal.
            surfacePoint  = bestPt + outDir * raiseM;
            surfaceNormal = outDir;
            hitPiece      = GetPieceRoot(best);
            return true;
        }

        return false;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static bool IsWeldableSurface(Collider col)
    {
        // Exclude weld-bead child GameObjects: they inherit the parent piece's "Weldable"
        // tag via the parent-check below, but must never be treated as weld target surfaces.
        // Without this guard the SphereCast snaps onto previously placed beads in mid-air,
        // producing the "welding in the air at 45°" artifact.
        if (col.gameObject.name.StartsWith("WeldBead")) return false;

        return col.CompareTag("Weldable")
            || (col.transform.parent != null && col.transform.parent.CompareTag("Weldable"));
    }

    private static Transform GetPieceRoot(Collider col)
    {
        return col.attachedRigidbody != null
             ? col.attachedRigidbody.transform
             : col.transform;
    }

    private void RefreshBeadWidth()
    {
        if (_line == null) return;
        float widthM = fallbackBeadWidthM;
        if (evaluator?.ActiveElectrode != null)
            widthM = evaluator.ActiveElectrode.diameterMm * beadWidthMultiplier * 0.001f;
        if (Mathf.Approximately(widthM, beadWidthM)) return;
        beadWidthM            = widthM;
        _line.widthMultiplier = widthM;
    }

    private void OnSessionStarted() => ClearBead();

    // ── LineRenderer factory ──────────────────────────────────────────────────

    private void ApplyLineRendererSettings(LineRenderer lr)
    {
        lr.widthMultiplier   = fallbackBeadWidthM;
        lr.positionCount     = 0;
        lr.numCapVertices    = 6;
        lr.numCornerVertices = 6;
        lr.alignment         = LineAlignment.View;
        lr.textureMode       = LineTextureMode.Tile;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        lr.receiveShadows    = false;

        var gradient = new Gradient();
        gradient.SetKeys(
            new GradientColorKey[]
            {
                new GradientColorKey(coolColor, 0.00f),
                new GradientColorKey(warmColor, 0.60f),
                new GradientColorKey(hotColor,  1.00f),
            },
            new GradientAlphaKey[]
            {
                new GradientAlphaKey(1f, 0f),
                new GradientAlphaKey(1f, 1f),
            }
        );
        lr.colorGradient = gradient;

        var shader = Shader.Find("Universal Render Pipeline/Unlit")
                  ?? Shader.Find("Unlit/Color")
                  ?? Shader.Find("Sprites/Default");
        if (shader != null)
        {
            var mat = new Material(shader);
            mat.color = hotColor;
            lr.material = mat;
        }
    }
}
