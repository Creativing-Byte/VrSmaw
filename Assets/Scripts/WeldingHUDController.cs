using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Floating World-Space HUD that follows the player's camera and shows
/// real-time welding feedback: arc state, distance, electrode angle,
/// arc time, session progress and final score.
///
/// Self-contained: builds all UI at runtime, no prefabs needed.
/// Add this component to any empty GameObject in the scene.
/// </summary>
[DisallowMultipleComponent]
public class WeldingHUDController : MonoBehaviour
{
    // ── Inspector ─────────────────────────────────────────────────────────────

    [Header("References (auto-found if null)")]
    [SerializeField] private MigWelding      migWelding;
    [SerializeField] private WeldingEvaluator evaluator;

    [Header("HUD position — camera-relative (metres)")]
    [Tooltip("How far in front of the camera the HUD floats.")]
    [SerializeField] private float forwardDist     = 0.55f;
    [Tooltip("Vertical offset (negative = below eye level).")]
    [SerializeField] private float verticalOffset  = -0.18f;
    [Tooltip("Horizontal offset (negative = to the left).")]
    [SerializeField] private float horizontalOffset = -0.15f;
    [Tooltip("How fast the HUD follows head rotation (higher = snappier).")]
    [SerializeField] private float followSpeed     = 4f;

    [Header("Visibility rules")]
    [SerializeField] private bool showWhenArcActive    = true;
    [SerializeField] private bool showWhenSessionActive = true;

    // ── Runtime UI references ─────────────────────────────────────────────────

    private Canvas        _canvas;
    private TMP_Text      _statusText;
    private TMP_Text      _distText;
    private TMP_Text      _angleText;
    private TMP_Text      _scoreText;
    private TMP_Text      _arcTimeText;
    private TMP_Text      _progressLabel;
    private TMP_Text      _exerciseText;
    private RectTransform _progressFill;
    private Image         _progressFillImg;

    // ── Palette ───────────────────────────────────────────────────────────────

    private static readonly Color CGood = new Color(0.20f, 0.90f, 0.25f, 1f);
    private static readonly Color CWarn = new Color(1.00f, 0.75f, 0.05f, 1f);
    private static readonly Color CBad  = new Color(0.90f, 0.18f, 0.08f, 1f);
    private static readonly Color COff  = new Color(0.50f, 0.50f, 0.55f, 1f);

    // ── State ─────────────────────────────────────────────────────────────────

    private Camera _cam;
    private bool   _sessionJustEnded;
    private float  _finalScore;

    // ══════════════════════════════════════════════════════════════════════════
    // Unity lifecycle
    // ══════════════════════════════════════════════════════════════════════════

    private void Awake()
    {
        if (migWelding == null) migWelding = FindAnyObjectByType<MigWelding>();
        if (evaluator  == null) evaluator  = FindAnyObjectByType<WeldingEvaluator>();
        _cam = Camera.main;

        BuildHUD();
        _canvas.gameObject.SetActive(false);

        if (evaluator != null)
            evaluator.SessionEnded += OnSessionEnded;
    }

    private void OnDestroy()
    {
        if (evaluator != null)
            evaluator.SessionEnded -= OnSessionEnded;
    }

    private void OnSessionEnded()
    {
        _sessionJustEnded = true;
        _finalScore       = evaluator != null ? evaluator.OverallScore : 0f;
    }

    private void Update()
    {
        if (_cam == null) { _cam = Camera.main; return; }

        bool arcOn     = migWelding != null && migWelding.ArcIsValid;
        bool sessionOn = evaluator  != null && evaluator.SessionActive;
        bool visible   = (showWhenArcActive    && arcOn)
                       || (showWhenSessionActive && sessionOn)
                       || _sessionJustEnded;

        if (_canvas.gameObject.activeSelf != visible)
            _canvas.gameObject.SetActive(visible);

        if (!visible) return;

        SmoothFollow();
        RefreshContent(arcOn, sessionOn);
    }

    // ── Camera follow ─────────────────────────────────────────────────────────

private void SmoothFollow()
    {
        var cam = _cam.transform;

        // Target position: in front of camera, offset down and to the left
        var targetPos = cam.TransformPoint(
            new Vector3(horizontalOffset, verticalOffset, forwardDist));

        // Canvas face toward camera:
        // Unity WorldSpace Canvas is visible from the side its local +Z points AWAY from.
        // So +Z must point from canvas toward the scene (away from camera).
        var targetRot = Quaternion.LookRotation(targetPos - cam.position, cam.up);

        // Frame-rate independent exponential smoothing
        float t = 1f - Mathf.Exp(-followSpeed * Time.deltaTime);
        transform.position = Vector3.Lerp(transform.position, targetPos, t);
        transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, t);
    }

    // ── Content refresh ───────────────────────────────────────────────────────

private void RefreshContent(bool arcOn, bool sessionOn)
    {
        // ── Arc status row ────────────────────────────────────────────────────
        if (arcOn)
        {
            _statusText.text  = "●  ARCO ACTIVO";
            _statusText.color = CGood;
        }
        else if (sessionOn)
        {
            _statusText.text  = "○  Acercar electrodo";
            _statusText.color = CWarn;
        }
        else if (_sessionJustEnded)
        {
            _statusText.text  = "✓  Sesión finalizada";
            _statusText.color = CGood;
        }
        else
        {
            _statusText.text  = "○  Sin sesión activa";
            _statusText.color = COff;
        }

        // ── Distance ──────────────────────────────────────────────────────────
        float distMm = migWelding != null ? migWelding.CurrentLaserDistanceMm : 0f;
        if (distMm > 0f)
        {
            _distText.text  = $"Distancia   {distMm:F1} mm";
            _distText.color = arcOn ? CGood : CWarn;
        }
        else
        {
            _distText.text  = "Distancia   — mm";
            _distText.color = COff;
        }

        // ── Electrode axis angle from vertical ────────────────────────────────
        // In SMAW the electrode should be held nearly vertical:
        // handle high, tip angled ~10-25° from straight down toward the piece.
        // We compute the physical axis as the vector from the TIP toward the
        // handle (weld_t parent = ARco = roughly the grip position).
        // Angle of that axis from Vector3.up:
        //   0° = handle perfectly above tip (vertical) — good
        //  10-25° = slight drag angle — ideal SMAW range
        //  >45° = electrode too flat — bad
        if (migWelding != null && migWelding.weldTip != null)
        {
            var tipPos    = migWelding.weldTip.position;
            // ARco is the direct parent of weld_t
            var handleRef = migWelding.weldTip.parent != null
                          ? migWelding.weldTip.parent.position
                          : tipPos + Vector3.up;

            var  electrodeAxis = (handleRef - tipPos).normalized;
            float tilt         = Vector3.Angle(electrodeAxis, Vector3.up);

            Color ac;
            string hint;
            if (tilt <= 30f)
            {
                ac   = CGood;
                hint = "✓";
            }
            else if (tilt <= 50f)
            {
                ac   = CWarn;
                hint = "↓ inclinar más";
            }
            else
            {
                ac   = CBad;
                hint = "✗ demasiado horizontal";
            }

            _angleText.text  = $"Ángulo elec.  {tilt:F0}°  {hint}";
            _angleText.color = ac;
        }
        else
        {
            _angleText.text  = "Ángulo elec.  —°";
            _angleText.color = COff;
        }

        // ── Evaluator data ────────────────────────────────────────────────────
        if (evaluator != null)
        {
            // Score: show final value after session ends; live = "en curso"
            if (_sessionJustEnded || !sessionOn)
            {
                float s = _sessionJustEnded ? _finalScore : evaluator.OverallScore;
                _scoreText.text  = $"Puntuación  {s:F0}%";
                _scoreText.color = s >= 70f ? CGood : s >= 40f ? CWarn : CBad;
            }
            else
            {
                _scoreText.text  = "Puntuación  (al terminar)";
                _scoreText.color = COff;
            }

            // Arc time
            float arcSec = evaluator.ArcActiveSeconds;
            _arcTimeText.text = $"⏱ {arcSec:F1} s";

            // Progress bar + label
            float prog = evaluator.GetGuidedCompletion01();
            _progressFill.anchorMax    = new Vector2(prog, 1f);
            _progressFillImg.color     = Color.Lerp(CWarn, CGood, prog);
            _progressLabel.text        = $"{evaluator.GetProgressLabel()}  {prog * 100f:F0}%";

            // Exercise name
            _exerciseText.text = ExerciseLabel(evaluator.Exercise);
        }
        else
        {
            _scoreText.text    = "Puntuación  —";
            _arcTimeText.text  = "⏱ —";
            _progressLabel.text = "Progreso  —%";
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string ExerciseLabel(WeldingEvaluator.ExerciseType ex)
    {
        switch (ex)
        {
            case WeldingEvaluator.ExerciseType.P1_U:        return "P1 · Cordón en U";
            case WeldingEvaluator.ExerciseType.P2_T:        return "P2 · Ángulo de trabajo T";
            case WeldingEvaluator.ExerciseType.P3_Cuña:     return "P3 · Cuña";
            case WeldingEvaluator.ExerciseType.P4_V:        return "P4 · Cordón en V";
            case WeldingEvaluator.ExerciseType.P5_Cilindro: return "P5 · Cilindro 360°";
            default:                                         return "—";
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // HUD construction — all UI built at runtime, no prefabs required
    // ══════════════════════════════════════════════════════════════════════════

    private void BuildHUD()
    {
        // ── Root canvas ───────────────────────────────────────────────────────
        var root   = new GameObject("WeldingHUD_Canvas");
        root.transform.SetParent(transform, false);

        _canvas            = root.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.WorldSpace;

        var scaler = root.AddComponent<CanvasScaler>();
        scaler.dynamicPixelsPerUnit = 10f;

        root.AddComponent<GraphicRaycaster>();

        var cg             = root.AddComponent<CanvasGroup>();
        cg.interactable    = false;
        cg.blocksRaycasts  = false;

        // 220 × 180 units at 0.001 scale = 22 cm × 18 cm in world space
        var rootRT             = root.GetComponent<RectTransform>();
        rootRT.sizeDelta       = new Vector2(220f, 180f);
        rootRT.localScale      = Vector3.one * 0.001f;
        rootRT.localPosition   = Vector3.zero;
        rootRT.localRotation   = Quaternion.identity;

        // ── Dark background ───────────────────────────────────────────────────
        var bg = MakePanel(root.transform, "BG",
            Vector2.zero, new Vector2(220f, 180f),
            new Color(0.04f, 0.04f, 0.07f, 0.90f));

        // Blue accent bar at the top
        MakePanel(bg.transform, "Accent",
            new Vector2(0f, 80f), new Vector2(220f, 6f),
            new Color(0.22f, 0.52f, 1.00f, 0.95f));

        // ── Layout: top → bottom, starting just below accent bar ─────────────
        float y = 58f;

        // STATUS (large, bold)
        _statusText = MakeText(bg.transform, "Status",
            new Vector2(0f, y), new Vector2(210f, 28f),
            "○  Sin sesión activa", 14f, FontStyles.Bold);
        y -= 34f;

        MakeSep(bg.transform, y + 4f);

        // DISTANCE
        _distText = MakeText(bg.transform, "Dist",
            new Vector2(0f, y), new Vector2(210f, 20f),
            "Distancia   — mm", 11f, FontStyles.Normal);
        y -= 22f;

        // ANGLE
        _angleText = MakeText(bg.transform, "Angle",
            new Vector2(0f, y), new Vector2(210f, 20f),
            "Ángulo elec.  —°", 11f, FontStyles.Normal);
        y -= 22f;

        // SCORE
        _scoreText = MakeText(bg.transform, "Score",
            new Vector2(0f, y), new Vector2(210f, 20f),
            "Puntuación  —", 11f, FontStyles.Normal);
        y -= 28f;

        MakeSep(bg.transform, y + 4f);

        // PROGRESS BAR
        var barBg = MakePanel(bg.transform, "BarBg",
            new Vector2(0f, y - 8f), new Vector2(196f, 12f),
            new Color(0.10f, 0.10f, 0.14f, 1f));

        var fillGO           = new GameObject("BarFill");
        fillGO.transform.SetParent(barBg.transform, false);
        _progressFill        = fillGO.AddComponent<RectTransform>();
        _progressFillImg     = fillGO.AddComponent<Image>();
        _progressFillImg.color = CWarn;
        _progressFill.anchorMin  = new Vector2(0f, 0f);
        _progressFill.anchorMax  = new Vector2(0f, 1f);
        _progressFill.offsetMin  = Vector2.zero;
        _progressFill.offsetMax  = Vector2.zero;
        _progressFill.pivot      = new Vector2(0f, 0.5f);

        y -= 28f;

        // ARC TIME  |  PROGRESS LABEL
        _arcTimeText = MakeText(bg.transform, "ArcTime",
            new Vector2(-55f, y), new Vector2(90f, 18f),
            "⏱ 0.0 s", 10f, FontStyles.Normal);
        _arcTimeText.alignment = TextAlignmentOptions.Left;

        _progressLabel = MakeText(bg.transform, "ProgLabel",
            new Vector2(55f, y), new Vector2(90f, 18f),
            "Progreso  0%", 10f, FontStyles.Normal);
        _progressLabel.alignment = TextAlignmentOptions.Right;
        y -= 24f;

        MakeSep(bg.transform, y + 4f);

        // EXERCISE NAME
        _exerciseText = MakeText(bg.transform, "Exercise",
            new Vector2(0f, y - 8f), new Vector2(210f, 18f),
            "—", 9f, FontStyles.Italic);
        _exerciseText.color = new Color(0.65f, 0.65f, 0.70f, 1f);
    }

    // ── UI factory helpers ────────────────────────────────────────────────────

    private static GameObject MakePanel(Transform parent, string name,
        Vector2 pos, Vector2 size, Color color)
    {
        var go  = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt  = go.AddComponent<RectTransform>();
        rt.anchoredPosition = pos;
        rt.sizeDelta        = size;
        var img = go.AddComponent<Image>();
        img.color = color;
        return go;
    }

    private static TMP_Text MakeText(Transform parent, string name,
        Vector2 pos, Vector2 size, string text, float fontSize, FontStyles style)
    {
        var go  = new GameObject(name);
        go.transform.SetParent(parent, false);
        var rt  = go.AddComponent<RectTransform>();
        rt.anchoredPosition = pos;
        rt.sizeDelta        = size;
        var tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text               = text;
        tmp.fontSize           = fontSize;
        tmp.fontStyle          = style;
        tmp.color              = Color.white;
        tmp.alignment          = TextAlignmentOptions.Center;
        tmp.enableWordWrapping = false;
        tmp.overflowMode       = TextOverflowModes.Overflow;
        return tmp;
    }

    private void MakeSep(Transform parent, float y)
    {
        MakePanel(parent, "Sep",
            new Vector2(0f, y), new Vector2(200f, 1f),
            new Color(1f, 1f, 1f, 0.10f));
    }
}
