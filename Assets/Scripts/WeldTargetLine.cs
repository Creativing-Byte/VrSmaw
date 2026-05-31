using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Renders dashed guide lines on the weldable piece showing the correct weld seam.
///
/// RENDERING APPROACH — MeshRenderer + Quad (NOT LineRenderer):
///   URP overrides the per-material depth state (ZTest) for LineRenderers in its
///   Transparent pass, so ZTest=Always set on the material is silently ignored.
///   MeshRenderers rendered through DrawRenderers *do* respect the shader ZTest,
///   so each seam becomes a thin Quad child with Custom/WeldSeamLine shader whose
///   Pass block hard-codes "ZTest Always" — the quads are never occluded.
///
/// Each SeamSegment defines start / end in THIS GameObject's LOCAL space.
/// The Quad children are parented with worldPositionStays=false, so they travel
/// with the piece when the player grabs and repositions it.
///
/// Setup:
///   Attach to the weldable piece (e.g. "Figura T").
///   Adjust Seams in the Inspector if the T-joint mesh coordinates change.
/// </summary>
[DisallowMultipleComponent]
public class WeldTargetLine : MonoBehaviour
{
    // ── Seam definition ───────────────────────────────────────────────────────

    [System.Serializable]
    public struct SeamSegment
    {
        [Tooltip("Start point in this GameObject's local space.")]
        public Vector3 start;
        [Tooltip("End point in this GameObject's local space.")]
        public Vector3 end;
        [Tooltip("Override colour for this segment. Alpha=0 uses the global seamColor.")]
        public Color   colorOverride;
    }

    [Header("Seam Segments (local space)")]
    [SerializeField] private List<SeamSegment> seams = new List<SeamSegment>
    {
        // Front fillet: where the front face of the stem (local Y≈0.023) meets the base plate top
        new SeamSegment {
            start         = new Vector3(-0.01f, 0.023f, 0f),
            end           = new Vector3( 0.01f, 0.023f, 0f),
            colorOverride = Color.clear
        },
        // Back fillet: opposite face of the same plate (local Y≈0.026, same Z=0 height as front)
        new SeamSegment {
            start         = new Vector3(-0.01f, 0.026f, 0f),
            end           = new Vector3( 0.01f, 0.026f, 0f),
            colorOverride = Color.clear
        },
    };

    // ── Shader reference ──────────────────────────────────────────────────────

    [Header("Shader")]
    [Tooltip("Assign Custom/WeldSeamLine here so Unity includes it in builds.\n" +
             "Shader.Find() alone is NOT enough — the build pipeline skips shaders\n" +
             "that are only referenced at runtime and not via a serialized field.")]
    [SerializeField] private Shader seamShader;

    // ── Appearance ────────────────────────────────────────────────────────────

    [Header("Line Appearance")]
    [Tooltip("Global line colour. Overridden per-segment if colorOverride.a > 0.")]
    [SerializeField] private Color seamColor = new Color(0.1f, 0.85f, 1.0f, 0.95f);

    [Tooltip("Thickness of the guide strip in WORLD metres.")]
    [SerializeField] private float lineWidthM = 0.010f;

    [Tooltip("Number of dash+gap repetitions along the full seam.")]
    [SerializeField] [Range(3, 40)] private int dashCount = 10;

    [Tooltip("Fraction of each period that is solid. 0.6 = 60% solid.")]
    [SerializeField] [Range(0.2f, 0.9f)] private float dashFill = 0.6f;

    [Tooltip("How far in front of its face (in LOCAL units) the quad floats.\n" +
             "Offsets in local Y (= world depth/front-back) so the guide is just in front of\n" +
             "the respective fillet face, not inside the mesh.\n" +
             "With lossyScale=15: 0.0002 local ≈ 3 mm world offset from face.\n" +
             "ZTest Always means it's always visible regardless, but this prevents z-fighting.")]
    [SerializeField] private float surfaceOffsetLocal = 0.0002f;

    // ── Pulse ─────────────────────────────────────────────────────────────────

    [Header("Pulse")]
    [SerializeField] private bool  pulseEnabled  = true;
    [SerializeField] private float pulseSpeed    = 1.4f;
    [SerializeField] [Range(0.15f, 0.9f)] private float pulseMinAlpha = 0.30f;

    // ── Private ───────────────────────────────────────────────────────────────

    // One MeshRenderer per seam segment (the Quad that shows the dashed line)
    private readonly List<MeshRenderer> _quads = new();

    /// <summary>Read-only access to seam definitions for external bead analysis.</summary>
    public System.Collections.Generic.IReadOnlyList<SeamSegment> Seams => seams;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private void Start()  => Build();

    private void Update()
    {
        if (!pulseEnabled || _quads.Count == 0) return;

        float t = (Mathf.Sin(Time.time * pulseSpeed * Mathf.PI * 2f) + 1f) * 0.5f;

        for (int i = 0; i < _quads.Count; i++)
        {
            if (_quads[i] == null) continue;
            Color baseCol  = EffectiveColor(i);
            float alpha    = Mathf.Lerp(pulseMinAlpha, baseCol.a, t);
            Color pulsed   = new Color(baseCol.r, baseCol.g, baseCol.b, alpha);

            // Update both _Color (used by Custom/WeldSeamLine) and material.color
            var mat = _quads[i].material;
            if (mat.HasProperty("_Color")) mat.SetColor("_Color", pulsed);
            mat.color = pulsed;
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Rebuild quads (call after changing seams at runtime).</summary>
    public void Rebuild()
    {
        foreach (var mr in _quads)
            if (mr != null) Destroy(mr.gameObject);
        _quads.Clear();
        Build();
    }

    // ── Build ─────────────────────────────────────────────────────────────────

    private void Build()
    {
        // World-space scale of the parent — needed to convert desired world widths to local.
        Vector3 ws = transform.lossyScale;
        float   invScaleX = ws.x > 0.001f ? 1f / ws.x : 1f;
        float   invScaleY = ws.y > 0.001f ? 1f / ws.y : 1f;

        for (int i = 0; i < seams.Count; i++)
        {
            var seg = seams[i];

            // ── Geometry ─────────────────────────────────────────────────────
            // Coordinate system of Figura T (lossyScale=15, eulerAngles=(270,0,0)):
            //   local +X = world +X (width along seam)
            //   local +Y = world -Z (depth: toward/away from player)
            //   local +Z = world +Y (up)
            //
            // The seam runs in local X. With Quaternion.Euler(-90,0,0) the quad is
            // rotated into the local XZ plane, and its normal points in local -Y
            // (= world +Z = toward player). This makes the strip face-on from the
            // welding angle instead of edge-on (Quaternion.identity was edge-on).
            //
            // Offset direction: push each guide AWAY from its face along local Y.
            //   Front face (local Y is smaller, closer to viewer): offset in local -Y
            //   Back face (local Z < 0 = lower world height): offset in local +Y

            float seamLengthLocal = Mathf.Abs(seg.end.x - seg.start.x);
            if (seamLengthLocal < 0.0001f)
                seamLengthLocal = (seg.end - seg.start).magnitude; // fallback for non-X seams

            float localWidth = lineWidthM * invScaleY;  // strip width in local Z (= world Y, up)

            Vector3 midLocal = (seg.start + seg.end) * 0.5f;

            // Offset in local Y (= world depth/front-back) toward the face's outward normal.
            //
            // Discrimination strategy:
            //   • If seams are at DIFFERENT local Z (heights): the one at lower Z is the
            //     "back" seam and its face normal points in local +Y.
            //   • If seams are at the SAME local Z (same height, T-joint both-side fillets):
            //     discriminate by Y. The seam at SMALLER Y is the front face (normal -Y,
            //     toward player); the seam at LARGER Y is the back face (normal +Y).
            //     We use the centroid Y of all seams as the split point.
            bool isBackFace;
            if (Mathf.Abs(midLocal.z) > 0.0005f)
            {
                // Height-based: seam lower in world = back face
                isBackFace = midLocal.z < -0.0001f;
            }
            else
            {
                // Same-height seams: determine front/back from Y centroid
                float sumY = 0f;
                foreach (var s in seams) sumY += (s.start.y + s.end.y) * 0.5f;
                float centroidY = sumY / Mathf.Max(1, seams.Count);
                isBackFace = midLocal.y > centroidY;
            }
            float offsetSignY = isBackFace ? 1f : -1f;
            midLocal.y += offsetSignY * surfaceOffsetLocal;

            // ── Create Quad ───────────────────────────────────────────────────
            var go = GameObject.CreatePrimitive(PrimitiveType.Quad);
            go.name = $"SeamGuide_{i}";
            go.transform.SetParent(transform, worldPositionStays: false);
            go.transform.localPosition = midLocal;
            // Euler(-90,0,0): rotates quad from XY plane into XZ plane.
            // Normal (originally local +Z) becomes local -Y = world +Z = faces player from front.
            // Cull Off in shader means both sides render → also visible from the back.
            go.transform.localRotation = Quaternion.Euler(-90f, 0f, 0f);
            go.transform.localScale    = new Vector3(seamLengthLocal, localWidth, 1f);

            // Remove the auto-added MeshCollider so it doesn't interfere with physics
            var col = go.GetComponent<Collider>();
            if (col != null) Destroy(col);

            // ── Material & shadow ─────────────────────────────────────────────
            var mr   = go.GetComponent<MeshRenderer>();
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows    = false;

            Color segCol = EffectiveColor(i);
            var mat      = CreateDashMaterial(segCol);

            // Tile the dash texture along the seam (X direction)
            mat.mainTextureScale = new Vector2(dashCount, 1f);
            mr.material = mat;

            _quads.Add(mr);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private Color EffectiveColor(int i) =>
        (i >= 0 && i < seams.Count && seams[i].colorOverride.a > 0.01f)
            ? seams[i].colorOverride
            : seamColor;

    private Material CreateDashMaterial(Color col)
    {
        // Programmatic 64-pixel dash texture: dashFill% solid, rest transparent
        const int W = 64;
        var tex = new Texture2D(W, 2, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Bilinear,
            wrapMode   = TextureWrapMode.Repeat
        };
        int solidW = Mathf.RoundToInt(W * dashFill);
        for (int x = 0; x < W; x++)
        {
            Color pix = x < solidW
                ? new Color(col.r, col.g, col.b, 1f)
                : new Color(0f, 0f, 0f, 0f);
            tex.SetPixel(x, 0, pix);
            tex.SetPixel(x, 1, pix);
        }
        tex.Apply();

        // Use the serialized shader reference so Unity includes it in builds.
        // Shader.Find() alone is not enough — the build pipeline only bundles
        // shaders that are explicitly referenced (Inspector, Resources folder,
        // or "Always Included Shaders" in Graphics Settings).
        var shader = seamShader                               // assigned in Inspector ← preferred
                  ?? Shader.Find("Custom/WeldSeamLine")       // editor fallback
                  ?? Shader.Find("Sprites/Default")
                  ?? Shader.Find("Universal Render Pipeline/Unlit");

        var mat = new Material(shader) { mainTexture = tex };

        if (mat.HasProperty("_Color"))  mat.SetColor("_Color", col);
        if (mat.HasProperty("_Color4")) mat.SetColor("_Color4", col);

        // If falling back to URP/Unlit, force transparency manually
        if (mat.HasProperty("_Surface"))
        {
            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend",   0f);
            mat.SetFloat("_ZWrite",  0f);
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        }

        mat.renderQueue = 4100;
        return mat;
    }

    // ── Editor gizmos ─────────────────────────────────────────────────────────

    private void OnDrawGizmosSelected()
    {
        for (int i = 0; i < seams.Count; i++)
        {
            var seg = seams[i];
            Gizmos.color = EffectiveColor(i);

            // Mirror the same offset logic used in Build().
            Vector3 mid = (seg.start + seg.end) * 0.5f;
            bool isBackFace;
            if (Mathf.Abs(mid.z) > 0.0005f)
            {
                isBackFace = mid.z < -0.0001f;
            }
            else
            {
                float sumY = 0f;
                foreach (var s in seams) sumY += (s.start.y + s.end.y) * 0.5f;
                isBackFace = mid.y > sumY / Mathf.Max(1, seams.Count);
            }
            float offsetSignY = isBackFace ? 1f : -1f;

            Vector3 offsetStart = seg.start;
            Vector3 offsetEnd   = seg.end;
            offsetStart.y += offsetSignY * surfaceOffsetLocal;
            offsetEnd.y   += offsetSignY * surfaceOffsetLocal;

            var ws = transform.TransformPoint(offsetStart);
            var we = transform.TransformPoint(offsetEnd);
            Gizmos.DrawLine(ws, we);
            Gizmos.DrawSphere(ws, 0.003f);
            Gizmos.DrawSphere(we, 0.003f);
        }
    }
}
