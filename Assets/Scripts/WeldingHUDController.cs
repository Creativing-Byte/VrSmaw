using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Floating World-Space HUD — follows the player's camera and shows
/// real-time welding feedback with gamified visual indicators:
///
///   • Arc status + good-arc streak timer
///   • Distance quality bar (shows the "sweet spot" zone)
///   • Electrode angle bar  (deviation from 45°)
///   • Combined quality grade  (ÓPTIMO / BUENO / MEJORAR / FUERA)
///   • Per-bin seam coverage (10 segments per fillet)
///   • Bin-fill popup  "✓ +1 sección"
///   • Arc time · Exercise name
/// </summary>
[DisallowMultipleComponent]
public class WeldingHUDController : MonoBehaviour
{
    // ── Inspector ─────────────────────────────────────────────────────────────

    [Header("References (auto-found if null)")]
    [SerializeField] private MigWelding      migWelding;
    [SerializeField] private WeldingEvaluator evaluator;

    [Header("HUD position — camera-relative (metres)")]
    [SerializeField] private float forwardDist      =  0.55f;
    [SerializeField] private float verticalOffset   = -0.18f;
    [SerializeField] private float horizontalOffset = -0.15f;
    [SerializeField] private float followSpeed      =  4f;

    [Header("Visibility rules")]
    [SerializeField] private bool showWhenArcActive     = true;
    [SerializeField] private bool showWhenSessionActive = true;

    // ── Palette ───────────────────────────────────────────────────────────────

    private static readonly Color CGood    = new Color(0.20f, 0.92f, 0.30f, 1f);
    private static readonly Color CWarn    = new Color(1.00f, 0.78f, 0.05f, 1f);
    private static readonly Color CBad     = new Color(0.92f, 0.18f, 0.08f, 1f);
    private static readonly Color COff     = new Color(0.48f, 0.48f, 0.52f, 1f);
    private static readonly Color CAccentBlue = new Color(0.22f, 0.52f, 1.00f, 1f);

    // ── Runtime UI references ─────────────────────────────────────────────────

    private Canvas      _canvas;
    private Image       _topAccent;          // colored accent bar (quality driven)
    private TMP_Text    _statusText;         // arc state
    private TMP_Text    _streakText;         // 🔥 streak or ⏱ total arc time
    private TMP_Text    _angleBar;           // angle quality bar + value
    private TMP_Text    _qualityText;        // combined grade: ÓPTIMO / BUENO / …
    private TMP_Text    _seamFrenteText;     // per-bin coverage line — Frente
    private TMP_Text    _seamReversoText;    // per-bin coverage line — Reverso
    private TMP_Text    _genProgressText;    // generic progress (non P2-T exercises)
    private TMP_Text    _binPopupText;       // transient "✓ +1 sección" popup
    private CanvasGroup _binPopupGroup;
    private TMP_Text    _exerciseText;
    private RectTransform _progressFill;
    private Image         _progressFillImg;

    // ── State ─────────────────────────────────────────────────────────────────

    private Camera _cam;
    private bool   _sessionJustEnded;
    private float  _finalScore;
    private bool   _forceHidden;

    // Gamification state
    private float  _goodArcTimer;      // seconds both dist + angle are in ideal range
    private int    _prevTotalBins;     // detect when a new bin is filled
    private float  _popupTimer;        // remaining seconds for popup visibility

    private const float PopupDuration  = 1.8f;
    private const float GoodArcMinSec  = 1.0f;  // show streak only after this threshold

    // ══════════════════════════════════════════════════════════════════════════

    private void Awake()
    {
        if (migWelding == null) migWelding = FindAnyObjectByType<MigWelding>();
        if (evaluator  == null) evaluator  = FindAnyObjectByType<WeldingEvaluator>();
        _cam = Camera.main;

        BuildHUD();
        _canvas.gameObject.SetActive(false);

        if (evaluator != null) evaluator.SessionEnded += OnSessionEnded;
    }

    private void OnDestroy()
    {
        if (evaluator != null) evaluator.SessionEnded -= OnSessionEnded;
    }

    private void OnSessionEnded()
    {
        _sessionJustEnded = true;
        _finalScore       = evaluator != null ? evaluator.OverallScore : 0f;
    }

    public void SetForceHidden(bool hidden)
    {
        _forceHidden = hidden;
        if (hidden && _canvas != null) _canvas.gameObject.SetActive(false);
    }

    // ── Unity Update ──────────────────────────────────────────────────────────

    private void Update()
    {
        if (_cam == null) { _cam = Camera.main; return; }
        if (_forceHidden) return;

        bool arcOn     = migWelding != null && migWelding.ArcIsValid;
        bool sessionOn = evaluator  != null && evaluator.SessionActive;
        bool visible   = (showWhenArcActive     && arcOn)
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
        var targetPos = cam.TransformPoint(
            new Vector3(horizontalOffset, verticalOffset, forwardDist));
        var targetRot = Quaternion.LookRotation(targetPos - cam.position, cam.up);
        float t = 1f - Mathf.Exp(-followSpeed * Time.deltaTime);
        transform.position = Vector3.Lerp(transform.position, targetPos, t);
        transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, t);
    }

    // ── Content refresh ───────────────────────────────────────────────────────

    private void RefreshContent(bool arcOn, bool sessionOn)
    {
        var e = evaluator;

        // ── Compute quality metrics ───────────────────────────────────────────

        float angleQuality = 0f;  // 0-1: how well the angle matches 45°
        float tipAngle     = 0f;

        if (migWelding != null && migWelding.weldTip != null)
        {
            var tipPos    = migWelding.weldTip.position;
            var handleRef = migWelding.weldTip.parent != null
                          ? migWelding.weldTip.parent.position
                          : tipPos + Vector3.up;
            var axis = (handleRef - tipPos).normalized;
            tipAngle     = Vector3.Angle(axis, Vector3.up);
            // Ideal 45° for T-joint; perfect = within 5°, degrades to 0 at ±20°
            float dev    = Mathf.Abs(tipAngle - 45f);
            angleQuality = arcOn ? 1f - Mathf.Clamp01(dev / 20f) : 0f;
        }

        // Quality is purely angle-based (distance removed from scoring and UI)
        float combinedQ = arcOn ? angleQuality : 0f;

        // ── Good-arc streak ───────────────────────────────────────────────────

        if (arcOn && combinedQ >= 0.7f)
            _goodArcTimer += Time.deltaTime;
        else if (!arcOn)
            _goodArcTimer = 0f;

        // ── Bin-fill popup ────────────────────────────────────────────────────

        int totalBins = CountFilledBins(e);
        if (totalBins > _prevTotalBins && _prevTotalBins >= 0)
        {
            _popupTimer = PopupDuration;
            if (_binPopupText != null)
                _binPopupText.text = "✓  ¡Sección completada!";
        }
        _prevTotalBins = totalBins;

        if (_popupTimer > 0f)
        {
            _popupTimer -= Time.deltaTime;
            float alpha = Mathf.Clamp01(_popupTimer / 0.4f);   // fade out in last 0.4 s
            if (_binPopupGroup != null) _binPopupGroup.alpha = alpha;
        }
        else if (_binPopupGroup != null && _binPopupGroup.alpha > 0f)
        {
            _binPopupGroup.alpha = 0f;
        }

        // ── Accent bar colour ─────────────────────────────────────────────────

        Color accent = arcOn
            ? Color.Lerp(CBad, CGood, combinedQ)
            : CAccentBlue;
        if (_topAccent != null) _topAccent.color = accent;

        // ── Status row ────────────────────────────────────────────────────────

        if (_statusText != null)
        {
            if (arcOn)
            {
                // Pulse size slightly when in optimal zone
                float szMod = combinedQ >= 0.85f
                    ? 1f + 0.08f * Mathf.Sin(Time.time * 6f)
                    : 1f;
                string sizeTag = combinedQ >= 0.85f
                    ? $"<size={Mathf.RoundToInt(szMod * 100f)}%>"
                    : "";
                string sizeEnd = combinedQ >= 0.85f ? "</size>" : "";
                _statusText.text  = $"{sizeTag}●  ARCO ACTIVO{sizeEnd}";
                _statusText.color = Color.Lerp(CWarn, CGood, combinedQ);
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
        }

        // ── Streak / arc time row ─────────────────────────────────────────────

        if (_streakText != null)
        {
            if (arcOn && _goodArcTimer >= GoodArcMinSec)
            {
                float pulse = 1f + 0.12f * Mathf.Sin(Time.time * 4f);
                _streakText.text  = $"<size={Mathf.RoundToInt(pulse * 100f)}%>🔥</size> {_goodArcTimer:F1}s";
                _streakText.color = CGood;
            }
            else if (e != null)
            {
                _streakText.text  = $"⏱ {e.ArcActiveSeconds:F1}s";
                _streakText.color = COff;
            }
        }

        // ── Angle bar ─────────────────────────────────────────────────────────

        if (_angleBar != null)
        {
            if (migWelding != null && migWelding.weldTip != null)
            {
                string bar   = arcOn ? QualityBar(angleQuality, 8) : NeutralBar(8);
                float  dev   = Mathf.Abs(tipAngle - 45f);
                Color  ac    = arcOn ? Color.Lerp(CBad, CGood, angleQuality) : COff;
                string check = arcOn
                    ? (dev <= 5f  ? " <color=#44EE66>✓</color>"
                     : dev <= 12f ? " <color=#FFCC22>~</color>"
                     :              " <color=#FF5555>✗</color>")
                    : "";
                _angleBar.text  = $"Ángulo {bar}  {tipAngle:F0}°{check}";
                _angleBar.color = Color.white;
            }
            else
            {
                _angleBar.text  = "Ángulo  —°";
                _angleBar.color = COff;
            }
        }

        // ── Combined quality grade ────────────────────────────────────────────

        if (_qualityText != null)
        {
            if (arcOn)
            {
                (string label, Color col) = combinedQ switch
                {
                    >= 0.85f => ("ÓPTIMO",   CGood),
                    >= 0.65f => ("BUENO",    CGood),
                    >= 0.40f => ("MEJORAR",  CWarn),
                    _        => ("FUERA",    CBad),
                };
                _qualityText.text  = $"Calidad   <b>{label}</b>";
                _qualityText.color = col;
            }
            else
            {
                _qualityText.text  = "Calidad   —";
                _qualityText.color = COff;
            }
        }

        // ── Coverage rows (P2-T per-bin) ──────────────────────────────────────

        bool isT = e != null && e.Exercise == WeldingEvaluator.ExerciseType.P2_T;
        float[] perSeam = e?.GetPerSeamCoverages();

        if (_seamFrenteText != null)
            _seamFrenteText.gameObject.SetActive(isT);
        if (_seamReversoText != null)
            _seamReversoText.gameObject.SetActive(isT);
        if (_genProgressText != null)
            _genProgressText.gameObject.SetActive(!isT);

        if (isT && perSeam != null && perSeam.Length >= 2)
        {
            if (_seamFrenteText  != null) _seamFrenteText.text  = SeamBinBar(perSeam[0], "F");
            if (_seamReversoText != null) _seamReversoText.text = SeamBinBar(perSeam[1], "R");
        }
        else if (isT)
        {
            if (_seamFrenteText  != null) { _seamFrenteText.text  = "F  ░░░░░░░░░░  0%";  _seamFrenteText.color  = COff; }
            if (_seamReversoText != null) { _seamReversoText.text = "R  ░░░░░░░░░░  0%";  _seamReversoText.color = COff; }
        }

        if (!isT && e != null && _genProgressText != null)
        {
            float prog = e.GetGuidedCompletion01();
            _progressFill.anchorMax = new Vector2(prog, 1f);
            _progressFillImg.color  = Color.Lerp(CWarn, CGood, prog);
            _genProgressText.text   = $"{e.GetProgressLabel()}  {prog * 100f:F0}%";
            _genProgressText.color  = Color.white;
        }

        // ── Score row ─────────────────────────────────────────────────────────

        if (_sessionJustEnded || !sessionOn)
        {
            // (score shown in results panel; nothing extra needed here)
        }

        // ── Exercise name ─────────────────────────────────────────────────────

        if (_exerciseText != null && e != null)
            _exerciseText.text = ExerciseLabel(e.Exercise);
    }

    // ── Formatting helpers ────────────────────────────────────────────────────

    /// <summary>8-char filled/empty quality bar with colour gradient.</summary>
    private static string QualityBar(float q, int len)
    {
        int filled = Mathf.Clamp(Mathf.RoundToInt(q * len), 0, len);
        string col = q >= 0.75f ? "#44EE66" : q >= 0.45f ? "#FFCC22" : "#FF5555";
        return $"<color={col}>{new string('▮', filled)}</color>" +
               $"<color=#2A2A2A>{new string('▮', len - filled)}</color>";
    }

    private static string NeutralBar(int len) =>
        $"<color=#2A2A2A>{new string('▮', len)}</color>";

    /// <summary>10-char per-bin bar for one fillet seam.</summary>
    private static string SeamBinBar(float coverage, string label)
    {
        const int bins = 10;
        int filled = Mathf.Clamp(Mathf.RoundToInt(coverage * bins), 0, bins);
        string col = coverage >= 0.8f ? "#44EE66" : coverage >= 0.4f ? "#FFCC22" : "#FF5555";
        int pct = Mathf.RoundToInt(coverage * 100f);
        return $"{label}  <color={col}>{new string('█', filled)}</color>" +
               $"<color=#2A2A2A>{new string('█', bins - filled)}</color>  {pct}%";
    }

    private static int CountFilledBins(WeldingEvaluator e)
    {
        if (e == null) return 0;
        var seams = e.GetPerSeamCoverages();
        if (seams == null) return 0;
        // Count how many 10% increments are filled
        int count = 0;
        foreach (var s in seams)
            count += Mathf.RoundToInt(s * 10f);
        return count;
    }

    private static string ExerciseLabel(WeldingEvaluator.ExerciseType ex) => ex switch
    {
        WeldingEvaluator.ExerciseType.P2_T => "P2 · Unión en T — filete doble",
        _                                  => ex.ToString(),
    };

    // ══════════════════════════════════════════════════════════════════════════
    // HUD construction
    // ══════════════════════════════════════════════════════════════════════════

    private void BuildHUD()
    {
        // ── Root canvas ───────────────────────────────────────────────────────

        var root = new GameObject("WeldingHUD_Canvas");
        root.transform.SetParent(transform, false);

        _canvas            = root.AddComponent<Canvas>();
        _canvas.renderMode = RenderMode.WorldSpace;

        var scaler = root.AddComponent<CanvasScaler>();
        scaler.dynamicPixelsPerUnit = 10f;

        root.AddComponent<GraphicRaycaster>();

        var cg = root.AddComponent<CanvasGroup>();
        cg.interactable   = false;
        cg.blocksRaycasts = false;

        // 220 × 260 units at 0.001 scale = 22 cm × 26 cm
        var rootRT           = root.GetComponent<RectTransform>();
        rootRT.sizeDelta     = new Vector2(220f, 260f);
        rootRT.localScale    = Vector3.one * 0.001f;
        rootRT.localPosition = Vector3.zero;
        rootRT.localRotation = Quaternion.identity;

        // ── Dark background ───────────────────────────────────────────────────

        var bg = MakePanel(root.transform, "BG",
            Vector2.zero, new Vector2(220f, 260f),
            new Color(0.04f, 0.04f, 0.07f, 0.92f));

        // ── Top accent bar (quality driven, updated each frame) ───────────────

        var accentGO = MakePanel(bg.transform, "Accent",
            new Vector2(0f, 120f), new Vector2(220f, 6f),
            CAccentBlue);
        _topAccent = accentGO.GetComponent<Image>();

        // ── Layout — top-down ─────────────────────────────────────────────────

        float y = 99f;

        // Row 0: Status (large) + streak (right-aligned)
        _statusText = MakeText(bg.transform, "Status",
            new Vector2(-20f, y), new Vector2(140f, 26f),
            "○  Sin sesión activa", 13f, FontStyles.Bold);
        _statusText.alignment = TextAlignmentOptions.Left;

        _streakText = MakeText(bg.transform, "Streak",
            new Vector2(72f, y), new Vector2(70f, 26f),
            "", 11f, FontStyles.Normal);
        _streakText.alignment = TextAlignmentOptions.Right;
        _streakText.color = COff;

        y -= 34f;
        MakeSep(bg.transform, y + 5f);

        // Row 1: Angle bar
        _angleBar = MakeText(bg.transform, "AngleBar",
            new Vector2(0f, y), new Vector2(210f, 22f),
            "Ángulo  —°", 10.5f, FontStyles.Normal);
        _angleBar.alignment = TextAlignmentOptions.Left;
        y -= 28f;

        MakeSep(bg.transform, y + 5f);

        // Row 3: Combined quality grade
        _qualityText = MakeText(bg.transform, "Quality",
            new Vector2(0f, y), new Vector2(210f, 22f),
            "Calidad   —", 10.5f, FontStyles.Normal);
        _qualityText.color = COff;
        y -= 30f;

        MakeSep(bg.transform, y + 5f);

        // Row 4–5: Per-seam bin coverage (P2-T) OR generic progress bar (others)
        _seamFrenteText = MakeText(bg.transform, "Frente",
            new Vector2(0f, y), new Vector2(210f, 22f),
            "F  ░░░░░░░░░░  0%", 10.5f, FontStyles.Normal);
        _seamFrenteText.alignment = TextAlignmentOptions.Left;
        y -= 26f;

        _seamReversoText = MakeText(bg.transform, "Reverso",
            new Vector2(0f, y), new Vector2(210f, 22f),
            "R  ░░░░░░░░░░  0%", 10.5f, FontStyles.Normal);
        _seamReversoText.alignment = TextAlignmentOptions.Left;

        // Generic progress bar (visible only for non-P2T exercises)
        var barBg = MakePanel(bg.transform, "BarBg",
            new Vector2(0f, y - 10f), new Vector2(196f, 10f),
            new Color(0.10f, 0.10f, 0.14f, 1f));
        barBg.gameObject.SetActive(false); // hidden by default; shares y with seam rows

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

        _genProgressText = MakeText(bg.transform, "GenProg",
            new Vector2(0f, y), new Vector2(210f, 22f),
            "Progreso  0%", 10.5f, FontStyles.Normal);
        _genProgressText.gameObject.SetActive(false);

        y -= 32f;
        MakeSep(bg.transform, y + 5f);

        // Row 6: Bin popup (normally alpha=0)
        var popupGO = new GameObject("BinPopup");
        popupGO.transform.SetParent(bg.transform, false);
        _binPopupGroup = popupGO.AddComponent<CanvasGroup>();
        _binPopupGroup.alpha        = 0f;
        _binPopupGroup.interactable = false;
        _binPopupGroup.blocksRaycasts = false;

        _binPopupText = MakeText(popupGO.transform, "PopupLabel",
            new Vector2(0f, y), new Vector2(210f, 22f),
            "✓  ¡Sección completada!", 10.5f, FontStyles.Bold);
        _binPopupText.color = CGood;
        y -= 28f;

        MakeSep(bg.transform, y + 5f);

        // Row 7: Exercise name (small, dim)
        _exerciseText = MakeText(bg.transform, "Exercise",
            new Vector2(0f, y - 6f), new Vector2(210f, 18f),
            "—", 9f, FontStyles.Italic);
        _exerciseText.color = new Color(0.60f, 0.60f, 0.65f, 1f);

        // Initialise bin detection
        _prevTotalBins = -1;
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
        tmp.textWrappingMode = TMPro.TextWrappingModes.NoWrap;
        tmp.overflowMode       = TextOverflowModes.Overflow;
        return tmp;
    }

    private void MakeSep(Transform parent, float y)
    {
        MakePanel(parent, "Sep",
            new Vector2(0f, y), new Vector2(200f, 1f),
            new Color(1f, 1f, 1f, 0.08f));
    }
}
